using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using R18MediaLibrary.Core;

namespace R18MediaLibrary.Services;

/// <summary>
/// 图床接管作品卡封面：把本地封面迁移到图床，并把"某作品的封面在图床上的地址"提供给
/// 桌面端作品卡与 Web 端 /api/works。
///
/// 动机是媒体库放在 HDD 上：翻一页卡片要逐张唤醒磁盘随机读，卡顿明显。封面迁到图床后，
/// 桌面端与浏览器都直接向图床要 <c>?w=400</c> 的缩略图，HDD 只在真正播放作品时才转。
/// 本地原图保留不动（它同时是详情页轮播的第一张图），图床只是副本。
///
/// <b>同一个作品的封面绝不会被迁移两次</b>，由三道闸拦住：
/// <list type="number">
/// <item>external_key 固定为 <c>work/&lt;RJ&gt;/cover</c>，是 <c>image_host</c> 表的主键，
///       也是图床侧的幂等键——一个作品至多一行、图床上至多一张。</item>
/// <item>本地文件指纹（路径|大小|mtime）一致就直接跳过，连盘都不读。</item>
/// <item>指纹变了（移库改了路径、重扫改了 mtime）再比内容指纹 sha256：内容没变就只刷新指纹，
///       不重传。本地映射表整个丢了也不怕——先用图床的 lookup 把已有的映射捡回来。</item>
/// </list>
///
/// 编排方式对齐 <see cref="MediaLibScanner"/>：进程级静态、单实例串行、状态以字符串暴露给两端轮询。
/// </summary>
public static class ImageHostService
{
    private const string CoverKind = "cover";
    private const string IconKind = "maker_icon";

    /// <summary>社团头像取图宽度：卡片显示 82px，二倍图 164 向上取到图床的 200 这一档。</summary>
    private const int IconWidth = 200;

    private static readonly object Sync = new();
    private static Dictionary<string, string>? _covers;   // work_id -> /images/<slug>/<name>
    private static Dictionary<string, string>? _icons;    // external_key -> /images/<slug>/<name>
    private static bool _running;
    private static string _status = "";
    private static CancellationTokenSource? _cts;

    /// <summary>图床封面的幂等键：同一作品重复迁移只会在图床上存一份。</summary>
    private static string CoverKey(string workId) => $"work/{workId}/cover";

    /// <summary>社团来源：DLsite 社团头像来自 ci-en，与 fanbox 作家头像分属两个命名空间。</summary>
    public const string DlsiteSource = "dlsite";

    /// <summary>图床头像的幂等键：同一社团重复迁移只会在图床上存一份。</summary>
    private static string IconKey(string source, string makerId) => $"maker/{source}/{makerId}/icon";

    /// <summary>一条已迁移记录：图床上的位置 + 迁移时的本地文件指纹与内容指纹。</summary>
    private sealed record Record(string StoredName, string Path, string Fingerprint, string Sha256);

    // ---------- 对外：取图地址 ----------

    /// <summary>图床是否已可用于取图（开关已开且地址/项目/Token 齐全）。</summary>
    public static bool Active => AppConfig.ImageHostEnabled && AppConfig.ImageHostConfigured;

    /// <summary>
    /// 作品封面在图床上的地址；未启用或该作品尚未迁移时返回 null，由调用方回退本地原图。
    /// 映射整表驻留内存：作品卡是成批渲染的，逐张查库等于把省下的 IO 又还回去。
    /// </summary>
    public static string? CoverUrl(string workId, int width = ImageHostClient.CardWidth)
    {
        if (!Active || workId.Length == 0)
            return null;
        var path = CoverMap().GetValueOrDefault(workId);
        return path == null ? null : ImageHostClient.BuildUrl(AppConfig.ImageHostBaseUrl, path, width);
    }

    /// <summary>
    /// fanbox 社团头像在图床上的地址；未启用或该作家尚未迁移时返回 null，由调用方回退站点直链。
    ///
    /// 与封面的动机不同：头像的源在 pawchive（网络）而不是本地 HDD，迁到图床是为了不必反复回源
    /// ——桌面端只有进程内缓存、重启即失效，Web 端每个冷缓存的浏览器都要让服务端再取一次。
    /// </summary>
    public static string? MakerIconUrl(string source, string makerId, int width = IconWidth)
    {
        if (!Active || makerId.Length == 0)
            return null;
        var path = IconMap().GetValueOrDefault(IconKey(source, makerId));
        return path == null ? null : ImageHostClient.BuildUrl(AppConfig.ImageHostBaseUrl, path, width);
    }

    /// <summary>已迁移到图床的封面张数（设置页显示）。</summary>
    public static int MappedCount => CoverMap().Count;

    /// <summary>还没迁移的作品数（估算：有封面的作品数 - 已迁移数，仅用于设置页提示）。</summary>
    public static int PendingCount
    {
        get
        {
            var total = Convert.ToInt32(Db.Scalar(
                "SELECT COUNT(*) FROM \"works\" WHERE \"cover\" IS NOT NULL AND \"cover\" <> ''") ?? 0);
            return Math.Max(0, total - MappedCount);
        }
    }

    /// <summary>
    /// 没有正在迁移时的常驻提示。桌面端与 Web 端共用这一句，免得两边文案各写一份。
    /// </summary>
    public static string IdleSummary()
    {
        if (!Active)
            return "";
        var mapped = MappedCount;
        var pending = PendingCount;
        if (mapped == 0 && pending == 0)
            return "";
        return pending > 0
            ? $"已迁移 {mapped} 张，待迁移 {pending} 张"
            : $"已全部迁移（{mapped} 张封面）";
    }

    /// <summary>
    /// 作品被删除时清掉它在图床上的封面与本地映射行。
    /// 图床不可达（或没开）时只删本地行：external_key 是幂等键，作品日后重新入库时
    /// 由迁移流程的 lookup 认领回那一张，不会重复占位。
    /// </summary>
    public static async Task DeleteWorkCoverAsync(string workId)
    {
        if (workId.Length == 0)
            return;
        var key = CoverKey(workId);
        var stored = Db.Scalar(
            "SELECT \"stored_name\" FROM \"image_host\" WHERE \"external_key\" = @k", ("@k", key)) as string;
        if (Active && !string.IsNullOrEmpty(stored))
            await ImageHostClient.DeleteAsync(
                AppConfig.ImageHostBaseUrl, AppConfig.ImageHostProject, AppConfig.ImageHostToken, stored);
        Db.Execute("DELETE FROM \"image_host\" WHERE \"external_key\" = @k", ("@k", key));
        Invalidate();
    }

    /// <summary>配置变更或迁移完成后丢弃内存映射，下次取图重新载入。</summary>
    public static void Invalidate()
    {
        lock (Sync)
        {
            _covers = null;
            _icons = null;
        }
    }

    private static Dictionary<string, string> CoverMap()
    {
        lock (Sync)
            return _covers ??= LoadPathMap(CoverKind);
    }

    /// <summary>
    /// 社团头像映射：external_key -> 图床路径。
    /// 按 external_key 而不是社团号索引——DLsite 与 fanbox 的社团号各自独立，
    /// 只有带上来源的键才保证不撞。
    /// </summary>
    private static Dictionary<string, string> IconMap()
    {
        lock (Sync)
        {
            if (_icons != null)
                return _icons;
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            var rows = Db.Select(
                "SELECT \"external_key\", \"path\" FROM \"image_host\" WHERE \"kind\" = @k",
                ("@k", IconKind));
            foreach (var row in rows ?? [])
                if (row[0] is string key && row[1] is string path && key.Length > 0 && path.Length > 0)
                    map[key] = path;
            return _icons = map;
        }
    }

    /// <summary>按 kind 读出「业务 id -> 图床路径」映射（work_id 列对封面存作品号、对头像存作家号）。</summary>
    private static Dictionary<string, string> LoadPathMap(string kind)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var rows = Db.Select(
            "SELECT \"work_id\", \"path\" FROM \"image_host\" WHERE \"kind\" = @k", ("@k", kind));
        foreach (var row in rows ?? [])
            if (row[0] is string id && row[1] is string path && id.Length > 0 && path.Length > 0)
                map[id] = path;
        return map;
    }

    // ---------- 对外：迁移 ----------

    /// <summary>当前迁移状态（是否在迁移、状态文本），供设置页轮询。</summary>
    public static (bool Running, string Status) State()
    {
        lock (Sync)
            return (_running, _status);
    }

    /// <summary>
    /// 触发一次封面迁移（补齐图床上缺的封面）；已在迁移中则忽略，未启用图床时为空操作。
    /// 设置页的"迁移封面"按钮、程序启动、元数据补全完成后都走这里——重复触发是安全的。
    /// </summary>
    public static void Kick()
    {
        if (!Active)
            return;
        lock (Sync)
        {
            if (_running)
                return;
            _running = true;
            _status = "正在准备…";
            _cts = new CancellationTokenSource();
        }
        _ = RunAsync(_cts!.Token);
    }

    /// <summary>中止正在进行的迁移（关闭图床开关 / 退出程序时）。</summary>
    public static void Stop()
    {
        lock (Sync)
            _cts?.Cancel();
    }

    /// <summary>
    /// 后台迁移：逐个作品判断"要不要传"，只传真正缺的那些。
    ///
    /// 有意串行：源文件在 HDD 上，并发读只会让磁头来回寻道，比顺序读更慢；
    /// 而且迁移是一次性开销，慢一点也不占用户操作。
    /// </summary>
    private static async Task RunAsync(CancellationToken ct)
    {
        var baseUrl = AppConfig.ImageHostBaseUrl;
        var project = AppConfig.ImageHostProject;
        var token = AppConfig.ImageHostToken;
        var done = 0;
        var migrated = 0;   // 本轮真正上传的
        var adopted = 0;    // 图床上已有同一张，直接认领映射（没有重传）
        var skipped = 0;    // 本地已有记录且内容未变
        var missing = 0;    // 封面文件已不在磁盘上
        var failed = 0;
        try
        {
            // 先探连通性：地址/Token 不对时逐张试会刷满日志，也让用户等半天才看到错误
            var (ok, _, pingError) = await ImageHostClient.PingAsync(baseUrl, project, token, ct);
            if (!ok)
            {
                SetStatus(pingError ?? "连接图床失败");
                Logger.Error($"图床迁移中止：{pingError}");
                return;
            }

            var works = Db.Select(
                "SELECT \"work_id\", \"cover\" FROM \"works\" " +
                "WHERE \"cover\" IS NOT NULL AND \"cover\" <> ''") ?? [];
            var known = LoadRecords(CoverKind);
            var total = works.Count;

            // 本地没有映射的，先批量问一遍图床："这些键你有吗？"
            // 本地映射表丢失（重装 / 还原备份 / 换机器）时，这一步把映射整批捡回来，
            // 而不是把已经在图床上的封面再传一遍。
            var hosted = await LookupHostedAsync(
                baseUrl, project, token,
                works.Select(r => r[0] as string ?? "").Where(id => id.Length > 0).Select(CoverKey),
                known, ct);

            SetStatus($"0/{total}");
            foreach (var row in works)
            {
                if (ct.IsCancellationRequested || !Active)
                {
                    SetStatus($"已停止（{done}/{total}）");
                    return;
                }
                done++;
                if (row[0] is not string workId || row[1] is not string cover || workId.Length == 0)
                    continue;

                FileInfo info;
                try
                {
                    info = new FileInfo(cover);
                    if (!info.Exists)
                    {
                        missing++;   // 封面文件已不在（移库/删档），留给媒体库扫描去修 works.cover
                        continue;
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    missing++;
                    continue;
                }

                var key = CoverKey(workId);
                var fingerprint = $"{cover}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
                known.TryGetValue(key, out var record);

                // 闸 2：指纹一致 —— 已迁移过且文件没动过，连盘都不读
                if (record != null && record.Fingerprint == fingerprint)
                {
                    skipped++;
                    continue;
                }

                // 走到这里才读盘算内容指纹（下面无论哪条分支都要用到它，只读这一次）
                byte[] bytes;
                try
                {
                    bytes = await File.ReadAllBytesAsync(cover, ct);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    missing++;
                    continue;
                }
                var sha256 = ImageHostClient.Sha256Of(bytes);

                // 闸 3a：内容没变，只是路径/时间变了（移动过媒体库、强制重扫）—— 刷新指纹，不重传
                if (record != null && record.Sha256 == sha256)
                {
                    Save(key, workId, record.StoredName, record.Path, cover, info, sha256);
                    skipped++;
                    continue;
                }

                // 闸 3b：图床上已有同键同内容的一张 —— 直接认领，不重传
                if (hosted.TryGetValue(key, out var hit) && hit.Sha256 == sha256 && hit.Path.Length > 0)
                {
                    Save(key, workId, hit.StoredName, hit.Path, cover, info, sha256);
                    adopted++;
                    continue;
                }

                // 确实要传：先清掉占着这个幂等键的旧图，否则上传会被幂等键顶回旧记录
                var stale = record?.StoredName ?? (hosted.TryGetValue(key, out var old) ? old.StoredName : null);
                if (!string.IsNullOrEmpty(stale))
                    await ImageHostClient.DeleteAsync(baseUrl, project, token, stale, ct);

                var extension = Path.GetExtension(cover).ToLowerInvariant();
                var result = await ImageHostClient.UploadAsync(
                    baseUrl, project, token, bytes, sha256, extension, key, ct);
                if (result.Stale)
                {
                    // 图床上仍有同键旧图（上面没查到、或删除没生效）：查出存储名删掉再传一次。
                    // 少了这一步，这些封面会因 sha256 校验不一致而永远传不上去。
                    var hitAgain = await ImageHostClient.LookupAsync(baseUrl, project, token, [key], ct);
                    if (hitAgain.TryGetValue(key, out var staleHit))
                    {
                        await ImageHostClient.DeleteAsync(baseUrl, project, token, staleHit.StoredName, ct);
                        result = await ImageHostClient.UploadAsync(
                            baseUrl, project, token, bytes, sha256, extension, key, ct);
                    }
                }

                if (!result.Ok || result.Path == null || result.StoredName == null)
                {
                    failed++;
                    Logger.Error($"图床迁移失败 {workId}：{result.Error}");
                }
                else
                {
                    Save(key, workId, result.StoredName, result.Path, cover, info, sha256);
                    migrated++;
                }
                if (done % 10 == 0)
                    SetStatus($"{done}/{total}" + (failed > 0 ? $"，失败 {failed}" : ""));
            }

            var icons = await MigrateMakerIconsAsync(baseUrl, project, token, ct);
            SetStatus(Summary(migrated, adopted, skipped, missing, failed)
                      + (icons > 0 ? $"；社团头像 {icons} 张" : ""));
            if (migrated > 0 || adopted > 0 || failed > 0)
                Logger.Info($"图床封面迁移完成：新迁移 {migrated} 张，认领 {adopted} 张，" +
                            $"跳过 {skipped} 张，缺文件 {missing} 个，失败 {failed} 张");
        }
        catch (OperationCanceledException)
        {
            SetStatus($"已停止（{done}）");
        }
        catch (Exception e)
        {
            SetStatus($"迁移出错：{e.Message}");
            Logger.Error($"图床封面迁移出错：{e}");
        }
        finally
        {
            Invalidate();   // 新映射生效：下次取图即走图床
            lock (Sync)
            {
                _running = false;
                _cts?.Dispose();
                _cts = null;
            }
        }
    }

    private static string Summary(int migrated, int adopted, int skipped, int missing, int failed)
    {
        var parts = new List<string>();
        if (migrated > 0)
            parts.Add($"新迁移 {migrated} 张");
        if (adopted > 0)
            parts.Add($"图床已有 {adopted} 张");
        if (skipped > 0)
            parts.Add($"已迁移过 {skipped} 张");
        if (missing > 0)
            parts.Add($"缺封面文件 {missing} 个");
        if (failed > 0)
            parts.Add($"失败 {failed} 张");
        if (parts.Count == 0)
            return "没有需要迁移的封面";
        // 本轮一张都没传、也没认领：早就迁完了，别用"迁移完成"暗示又搬了一遍
        var allDone = migrated == 0 && adopted == 0 && failed == 0;
        return (allDone ? "已全部迁移：" : "迁移完成：") + string.Join("，", parts);
    }

    /// <summary>
    /// 把社团头像迁到图床（fanbox 作家头像来自 pawchive，DLsite 社团头像来自 ci-en），
    /// 返回本轮新上传 + 认领的张数。
    ///
    /// <b>一个作家在图床上永远只有一张头像</b>，靠四处拦住重复：
    /// <list type="number">
    /// <item>枚举用 <c>SELECT DISTINCT maker_id</c>：同一社团有几十篇作品也只出现一次。</item>
    /// <item>external_key 固定为 <c>maker/&lt;来源&gt;/&lt;社团号&gt;/icon</c>，既是 image_host 表的主键，
    ///       也是图床侧的幂等键——重复上传同一个键不会产生第二份文件。
    ///       来源进键里，是因为 DLsite 的 RG 号与 fanbox 的作家号各自独立编号。</item>
    /// <item>本地已有该键的记录就直接跳过，连源站都不取。</item>
    /// <item>本地没记录时先批量 lookup 认领图床上已有的那张，而不是再传一遍。</item>
    /// </list>
    ///
    /// 与封面的差别在于源在网络上：
    /// <list type="bullet">
    /// <item>没有"本地文件指纹"那道闸——本地已有映射就直接跳过，不重新下载比对。
    ///       头像极少变，为了检测变化而每轮都回源，正好把要省的请求又花回去了。</item>
    /// <item>取图必须带 <see cref="PawchiveApi.UserAgent"/>，且要走代理感知的客户端
    ///       （图床自己的客户端刻意关了代理，对外网站点不合适）。</item>
    /// <item>头像 URL 不带扩展名、JPEG 与 WebP 混用，扩展名只能按 magic bytes 嗅探——
    ///       图床按扩展名判定允许类型，给错会被 400 顶回来。</item>
    /// </list>
    /// </summary>
    private static async Task<int> MigrateMakerIconsAsync(
        string baseUrl, string project, string token, CancellationToken ct)
    {
        // fanbox 作家：头像地址由作家号直接拼出来
        var targets = new List<(string Source, string MakerId, string Url)>();
        foreach (var r in Db.Select(
                     "SELECT DISTINCT \"maker_id\" FROM \"works\" WHERE \"source\" = @src " +
                     "AND \"state\" = '已品悦' AND \"maker_id\" IS NOT NULL AND \"maker_id\" <> ''",
                     ("@src", FanboxService.SourceName)) ?? [])
            if (r[0] as string is { Length: > 0 } fanboxId)
                targets.Add((PawchiveApi.FanboxService, fanboxId,
                    PawchiveApi.IconUrl(PawchiveApi.FanboxService, fanboxId)));

        // DLsite 社团：地址来自 ci-en 查表缓存，只有确实查到头像的才有得传
        foreach (var r in Db.Select(
                     "SELECT \"maker_id\", \"icon_url\" FROM \"maker_icon\" " +
                     "WHERE \"icon_url\" IS NOT NULL AND \"icon_url\" <> ''") ?? [])
            if (r[0] as string is { Length: > 0 } dlsiteId && r[1] as string is { Length: > 0 } iconUrl)
                targets.Add((DlsiteSource, dlsiteId, iconUrl));

        if (targets.Count == 0)
            return 0;

        var known = LoadRecords(IconKind);
        var pending = targets.Where(t => !known.ContainsKey(IconKey(t.Source, t.MakerId))).ToList();
        if (pending.Count == 0)
            return 0;

        // 本地映射丢了也先向图床认领，别把已经在图床上的头像再传一遍
        var hosted = await LookupHostedAsync(
            baseUrl, project, token, pending.Select(t => IconKey(t.Source, t.MakerId)), known, ct);

        // pawchive 的防护网关拦浏览器 UA，取它的图要用中性 UA；ci-en 用默认客户端即可
        using var pawchiveClient = Http.CreateClient(TimeSpan.FromSeconds(20), PawchiveApi.UserAgent);
        using var plainClient = Http.CreateClient(TimeSpan.FromSeconds(20));
        var handled = 0;
        var failed = 0;
        foreach (var (makerSource, makerId, source) in pending)
        {
            if (ct.IsCancellationRequested || !Active)
                break;
            var key = IconKey(makerSource, makerId);
            var client = makerSource == PawchiveApi.FanboxService ? pawchiveClient : plainClient;

            // 图床上已有同键的一张：直接认领（头像不比内容，同键即同一作家的头像）
            if (hosted.TryGetValue(key, out var hit) && hit.Path.Length > 0)
            {
                SaveRecord(key, makerId, IconKind, hit.StoredName, hit.Path, source, "", "", hit.Sha256);
                handled++;
                continue;
            }

            byte[] bytes;
            try
            {
                bytes = await client.GetByteArrayAsync(source, ct);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
                failed++;
                Logger.Error($"图床头像取源失败 {makerId}：{e.Message}");
                continue;
            }
            if (bytes.Length == 0)
            {
                failed++;
                continue;
            }

            var sha256 = ImageHostClient.Sha256Of(bytes);
            var extension = SniffImageExtension(bytes);
            var result = await ImageHostClient.UploadAsync(
                baseUrl, project, token, bytes, sha256, extension, key, ct);
            if (result.Stale)
            {
                // 同封面那条恢复路径：图床上仍占着这个幂等键，查出来删掉再传
                var again = await ImageHostClient.LookupAsync(baseUrl, project, token, [key], ct);
                if (again.TryGetValue(key, out var staleHit))
                {
                    await ImageHostClient.DeleteAsync(baseUrl, project, token, staleHit.StoredName, ct);
                    result = await ImageHostClient.UploadAsync(
                        baseUrl, project, token, bytes, sha256, extension, key, ct);
                }
            }
            if (!result.Ok || result.Path == null || result.StoredName == null)
            {
                failed++;
                Logger.Error($"图床头像迁移失败 {makerId}：{result.Error}");
                continue;
            }
            SaveRecord(key, makerId, IconKind, result.StoredName, result.Path, source, "", "", sha256);
            handled++;
        }
        if (handled > 0 || failed > 0)
            Logger.Info($"图床社团头像迁移：完成 {handled} 张" + (failed > 0 ? $"，失败 {failed} 张" : ""));
        return handled;
    }

    /// <summary>
    /// 按 magic bytes 判断图片扩展名。pawchive 的头像地址不带扩展名、Content-Type 又常是
    /// application/octet-stream，只能看内容——实测同一站点 JPEG 与 WebP 都有。
    /// </summary>
    private static string SniffImageExtension(byte[] b) => b switch
    {
        [0xFF, 0xD8, ..] => ".jpg",
        [0x89, 0x50, 0x4E, 0x47, ..] => ".png",
        [0x52, 0x49, 0x46, 0x46, _, _, _, _, 0x57, 0x45, 0x42, 0x50, ..] => ".webp",
        [0x47, 0x49, 0x46, ..] => ".gif",
        _ => ".jpg",
    };

    /// <summary>把本地还没有映射的 key 分批问图床要（每批不超过图床的 lookup 上限）。</summary>
    private static async Task<Dictionary<string, ImageHostClient.HostedImage>> LookupHostedAsync(
        string baseUrl, string project, string token,
        IEnumerable<string> keys, Dictionary<string, Record> known, CancellationToken ct)
    {
        var hosted = new Dictionary<string, ImageHostClient.HostedImage>(StringComparer.Ordinal);
        var wanted = keys
            .Where(key => key.Length > 0 && !known.ContainsKey(key))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        for (var i = 0; i < wanted.Count; i += ImageHostClient.LookupBatch)
        {
            if (ct.IsCancellationRequested)
                break;
            var batch = wanted.GetRange(i, Math.Min(ImageHostClient.LookupBatch, wanted.Count - i));
            foreach (var (key, image) in await ImageHostClient.LookupAsync(baseUrl, project, token, batch, ct))
                hosted[key] = image;
        }
        return hosted;
    }

    /// <summary>写入/更新一条封面迁移记录（external_key 是主键，故同一作品永远只有一行）。</summary>
    private static void Save(string key, string workId, string storedName, string path,
        string cover, FileInfo info, string sha256) =>
        SaveRecord(key, workId, CoverKind, storedName, path,
            cover, info.Length.ToString(), info.LastWriteTimeUtc.Ticks.ToString(), sha256);

    /// <summary>
    /// 写入/更新一条迁移记录。头像没有本地文件，src_* 三列改存来源 URL（便于排查），
    /// 大小/时间留空——它们只服务于封面那道"本地文件指纹"闸。
    /// </summary>
    private static void SaveRecord(string key, string id, string kind, string storedName, string path,
        string srcPath, string srcSize, string srcMtime, string sha256) =>
        Db.Execute(
            "INSERT OR REPLACE INTO \"image_host\" (\"external_key\", \"work_id\", \"kind\", " +
            "\"stored_name\", \"path\", \"src_path\", \"src_size\", \"src_mtime\", \"sha256\", \"up_time\") " +
            "VALUES (@k, @w, @kind, @s, @p, @sp, @ss, @sm, @sha, @t)",
            ("@k", key), ("@w", id), ("@kind", kind),
            ("@s", storedName), ("@p", path),
            ("@sp", srcPath), ("@ss", srcSize), ("@sm", srcMtime),
            ("@sha", sha256),
            ("@t", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.ffffff")));

    /// <summary>已迁移记录：external_key -> (图床存储名/路径, 本地文件指纹, 内容指纹)。</summary>
    private static Dictionary<string, Record> LoadRecords(string kind)
    {
        var map = new Dictionary<string, Record>(StringComparer.Ordinal);
        var rows = Db.Select(
            "SELECT \"external_key\", \"stored_name\", \"path\", \"src_path\", \"src_size\", " +
            "\"src_mtime\", \"sha256\" FROM \"image_host\" WHERE \"kind\" = @k", ("@k", kind));
        foreach (var row in rows ?? [])
            if (row[0] is string key && row[1] is string stored)
                map[key] = new Record(
                    stored, row[2] as string ?? "",
                    $"{row[3] as string}|{row[4] as string}|{row[5] as string}",
                    row[6] as string ?? "");
        return map;
    }

    private static void SetStatus(string text)
    {
        lock (Sync)
            _status = text;
    }
}
