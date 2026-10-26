using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using R18MediaLibrary.Core;

namespace R18MediaLibrary.Services;

/// <summary>一条 FANBOX 作家监控记录（fanbox_watch 表的一行）。</summary>
public class FanboxWatch
{
    public string ArtistId { get; init; } = "";
    public string Service { get; init; } = PawchiveApi.FanboxService;
    public string ArtistName { get; init; } = "";
    /// <summary>轮询间隔（分钟）。</summary>
    public int IntervalMin { get; init; } = 360;
    /// <summary>该条监控是否参与自动轮询（暂停的监控仍可手动「立即检查」）。</summary>
    public bool Enabled { get; init; } = true;
    /// <summary>上次检查时间（yyyy-MM-dd HH:mm:ss），空表示还没查过。</summary>
    public string LastCheck { get; init; } = "";
    /// <summary>
    /// 水位线：已处理到的最新投稿的发布时间（站上的 ISO 串）。
    /// 比它新的才算"新作品"；空串表示连现有作品也要下（第一轮翻到底）。
    /// </summary>
    public string LastPublished { get; init; } = "";
    public string TargetLib { get; init; } = "";
    public string TargetFolder { get; init; } = "";
    /// <summary>上次检查的结果文案（界面直接显示）。</summary>
    public string LastResult { get; init; } = "";
    /// <summary>累计自动下载的作品篇数。</summary>
    public int Downloaded { get; init; }
    public string AddTime { get; init; } = "";

    /// <summary>界面上称呼这位作家用的名字（没抓到名字时退回作家号）。</summary>
    public string Display => ArtistName.Length > 0 ? ArtistName : ArtistId;
}

/// <summary>
/// FANBOX 作家监控：盯住几位作家，按各自的间隔轮询 pawchive，
/// 发现比水位线更新的投稿就自动走 <see cref="FanboxService"/> 入队下载。
///
/// 判"新"的依据是投稿的发布时间（站上的 ISO 串，字典序即时间序），而不是"本地有没有这篇"——
/// 后者会把用户主动删掉的作品一次次下回来。水位线只在成功入队后才推进：
/// 站点没响应、或入队因配置问题失败时都保持原位，下一轮重新发现同一批新作品。
///
/// 编排方式对齐 <see cref="ImageHostService"/> / <see cref="MediaLibScanner"/>：进程级静态、
/// 单实例串行（同一时刻只查一位作家，免得几位作家同时翻页把站点惹毛）、状态以字符串暴露给两端轮询。
/// </summary>
public static class FanboxWatchService
{
    /// <summary>轮询间隔的上下限（分钟）：太短只是白骚扰站点，太长就失去了"监控"的意义。</summary>
    public const int MinIntervalMin = 10;
    public const int MaxIntervalMin = 7 * 24 * 60;

    /// <summary>界面上可选的轮询间隔档位（分钟），两端共用同一组。</summary>
    public static readonly int[] IntervalChoices = [30, 60, 120, 360, 720, 1440, 4320];

    /// <summary>翻页上限：只有"连现有作品也要下"的首轮才会真的翻这么多页。</summary>
    private const int MaxPages = 200;

    /// <summary>轮询线程的醒来间隔：每分钟看一眼有没有到点的监控。</summary>
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(60);

    /// <summary>启动后先静默一会再开始第一轮：刚启动时网络/代理往往还没就位。</summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(20);

    private static readonly object Sync = new();
    private static CancellationTokenSource? _cts;
    private static bool _busy;
    private static string _status = "";

    private const string Columns =
        "\"artist_id\", \"service\", \"artist_name\", \"interval_min\", \"enabled\", " +
        "\"last_check\", \"last_published\", \"target_lib\", \"target_folder\", " +
        "\"last_result\", \"downloaded\", \"add_time\"";

    // ---------- 查询 ----------

    /// <summary>全部监控记录，按作家名排序（界面列表用）。</summary>
    public static List<FanboxWatch> All()
    {
        var rows = Db.Select(
            $"SELECT {Columns} FROM \"fanbox_watch\" ORDER BY \"artist_name\" COLLATE NOCASE ASC");
        return (rows ?? []).Select(Map).ToList();
    }

    public static FanboxWatch? Get(string artistId)
    {
        var rows = Db.Select(
            $"SELECT {Columns} FROM \"fanbox_watch\" WHERE \"artist_id\" = @a", ("@a", artistId));
        return rows is { Count: > 0 } ? Map(rows[0]) : null;
    }

    /// <summary>该作家是否已在监控中（作家主页的按钮据此切换文案）。</summary>
    public static bool IsWatched(string artistId) =>
        Convert.ToInt64(Db.Scalar(
            "SELECT COUNT(*) FROM \"fanbox_watch\" WHERE \"artist_id\" = @a", ("@a", artistId)) ?? 0L) > 0;

    /// <summary>当前是否正在检查，以及进行中的状态文案（供两端每秒/每 1.5 秒轮询显示）。</summary>
    public static (bool Busy, string Status) State()
    {
        lock (Sync)
            return (_busy, _status);
    }

    /// <summary>不在检查时的常驻提示；两端共用这一句，免得文案各写一份。</summary>
    public static string IdleSummary()
    {
        var all = All();
        if (all.Count == 0)
            return "";
        var live = all.Count(w => w.Enabled);
        var text = $"监控 {all.Count} 位作家" + (live < all.Count ? $"（{all.Count - live} 位已暂停）" : "");
        if (!AppConfig.FanboxWatchEnabled)
            return text + "　自动轮询已关闭";
        var next = all.Where(w => w.Enabled).Select(DueAt).DefaultIfEmpty().Min();
        return next == default ? text : text + "　下次检查 " + NextText(next);
    }

    /// <summary>某条监控的下次检查时间文案（已到点/已暂停另有说法）。</summary>
    public static string NextCheckText(FanboxWatch w)
    {
        if (!w.Enabled)
            return "";
        if (!AppConfig.FanboxWatchEnabled)
            return "自动轮询已关闭";
        return NextText(DueAt(w));
    }

    private static string NextText(DateTime at) =>
        at <= DateTime.Now ? "即将检查" : at.ToString("MM-dd HH:mm");

    // ---------- 增删改 ----------

    /// <summary>
    /// 添加（或覆盖）一条作家监控。
    ///
    /// includeExisting=false（默认）时把水位线直接放到站上现有的最新一篇，
    /// 于是只有"今后新发的"才会被自动下载——现有作品仍可在作家主页手动挑。
    /// 这一步要先问一次站点，问不到就不建：宁可让用户重试，也好过悄悄建成"下全站历史"。
    /// </summary>
    public static async Task<(bool Ok, string Message)> AddAsync(
        PawchiveArtist artist, int intervalMin, bool includeExisting, string? lib, string? folder)
    {
        if (artist.Id.Length == 0)
            return (false, "作家号无效");

        var baseline = "";
        if (!includeExisting)
        {
            var batch = await PawchiveApi.GetPostsAsync(
                artist.Service.Length > 0 ? artist.Service : PawchiveApi.FanboxService, artist.Id, 0);
            if (batch is null)
                return (false, "获取该作家的作品列表失败，请稍后重试");
            baseline = NewestPublished(batch, "");
            // 一篇作品都还没有的作家：水位线无从取起，用当前时刻兜底
            if (baseline.Length == 0)
                baseline = NowIso();
        }

        var interval = NormalizeInterval(intervalMin);
        var existing = Get(artist.Id);
        Db.Execute(
            $"INSERT INTO \"fanbox_watch\" ({Columns}) VALUES " +
            "(@a, @s, @n, @iv, '1', '', @lp, @lib, @folder, '', @dl, @time) " +
            "ON CONFLICT(\"artist_id\") DO UPDATE SET " +
            "\"service\" = excluded.\"service\", \"artist_name\" = excluded.\"artist_name\", " +
            "\"interval_min\" = excluded.\"interval_min\", \"enabled\" = '1', " +
            "\"last_published\" = excluded.\"last_published\", " +
            "\"target_lib\" = excluded.\"target_lib\", \"target_folder\" = excluded.\"target_folder\"",
            ("@a", artist.Id),
            ("@s", artist.Service.Length > 0 ? artist.Service : PawchiveApi.FanboxService),
            ("@n", artist.Name), ("@iv", interval), ("@lp", baseline),
            ("@lib", lib ?? ""), ("@folder", folder ?? ""),
            ("@dl", existing?.Downloaded ?? 0), ("@time", Now()));

        Logger.Info($"fanbox 监控已添加：{(artist.Name.Length > 0 ? artist.Name : artist.Id)}" +
            $"（每 {interval} 分钟检查一次{(includeExisting ? "，含现有作品" : "")}）");
        Start();
        Kick();   // 新建的监控 last_check 为空，立刻就到点，不必等下一个整分
        return (true, includeExisting
            ? "已添加监控，正在把该作家现有的作品加入下载队列"
            : "已添加监控，今后发布的新作品会自动下载");
    }

    public static void Remove(string artistId)
    {
        Db.Execute("DELETE FROM \"fanbox_watch\" WHERE \"artist_id\" = @a", ("@a", artistId));
        Logger.Info($"fanbox 监控已移除：{artistId}");
    }

    public static void SetEnabled(string artistId, bool enabled)
    {
        Db.Execute("UPDATE \"fanbox_watch\" SET \"enabled\" = @e WHERE \"artist_id\" = @a",
            ("@e", enabled ? "1" : "0"), ("@a", artistId));
        if (enabled)
            Kick();
    }

    public static void SetInterval(string artistId, int intervalMin)
    {
        Db.Execute("UPDATE \"fanbox_watch\" SET \"interval_min\" = @iv WHERE \"artist_id\" = @a",
            ("@iv", NormalizeInterval(intervalMin)), ("@a", artistId));
        Kick();
    }

    /// <summary>把监控的下载目标改到另一个媒体库（此后自动下载的作品都落到那里）。</summary>
    public static void SetTarget(string artistId, string? lib, string? folder)
    {
        Db.Execute(
            "UPDATE \"fanbox_watch\" SET \"target_lib\" = @lib, \"target_folder\" = @folder " +
            "WHERE \"artist_id\" = @a",
            ("@lib", lib ?? ""), ("@folder", folder ?? ""), ("@a", artistId));
    }

    // ---------- 后台轮询 ----------

    /// <summary>启动后台轮询线程（重复调用安全）；程序启动时调用一次即可。</summary>
    public static void Start()
    {
        CancellationToken token;
        lock (Sync)
        {
            if (_cts != null)
                return;
            _cts = new CancellationTokenSource();
            token = _cts.Token;
        }
        _ = LoopAsync(token);
    }

    /// <summary>停止后台轮询（退出程序时）。</summary>
    public static void Stop()
    {
        lock (Sync)
        {
            _cts?.Cancel();
            _cts = null;
        }
    }

    /// <summary>立刻扫一遍到点的监控（新建/启用监控、改间隔后调用）；正在检查时是空操作。</summary>
    public static void Kick()
    {
        if (!AppConfig.FanboxWatchEnabled)
            return;
        _ = RunExclusiveAsync(SweepDueAsync, CancellationToken.None);
    }

    /// <summary>手动「立即检查」某位作家（不受总开关与到点与否影响）。</summary>
    public static Task CheckNowAsync(string artistId) =>
        RunExclusiveAsync(async ct =>
        {
            if (Get(artistId) is { } w)
                await CheckOneAsync(w, ct);
        }, CancellationToken.None);

    /// <summary>手动「立即检查全部」（含已暂停的条目——用户点了就是要现在查）。</summary>
    public static Task CheckAllAsync() =>
        RunExclusiveAsync(async ct =>
        {
            foreach (var w in All())
            {
                ct.ThrowIfCancellationRequested();
                await CheckOneAsync(w, ct);
            }
        }, CancellationToken.None);

    private static async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(StartupDelay, ct);
            while (!ct.IsCancellationRequested)
            {
                if (AppConfig.FanboxWatchEnabled)
                    await RunExclusiveAsync(SweepDueAsync, ct);
                await Task.Delay(Tick, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // 退出程序 / 关闭轮询
        }
        catch (Exception e)
        {
            Logger.Error(e, "fanbox 监控轮询线程");
        }
    }

    /// <summary>到点的监控逐个检查（串行：同一时刻只向站点要一位作家的列表）。</summary>
    private static async Task SweepDueAsync(CancellationToken ct)
    {
        foreach (var w in All().Where(w => w.Enabled && DueAt(w) <= DateTime.Now))
        {
            ct.ThrowIfCancellationRequested();
            await CheckOneAsync(w, ct);
        }
    }

    /// <summary>单实例串行闸：同一时刻只跑一轮检查，重复触发直接返回。</summary>
    private static async Task RunExclusiveAsync(Func<CancellationToken, Task> body, CancellationToken ct)
    {
        lock (Sync)
        {
            if (_busy)
                return;
            _busy = true;
            _status = "正在准备…";
        }
        try
        {
            await body(ct);
        }
        catch (OperationCanceledException)
        {
            // 正常取消
        }
        catch (Exception e)
        {
            Logger.Error(e, "fanbox 监控检查");
        }
        finally
        {
            lock (Sync)
            {
                _busy = false;
                _status = "";
            }
        }
    }

    /// <summary>检查一位作家：取新投稿 → 入队 → 推进水位线。</summary>
    private static async Task CheckOneAsync(FanboxWatch w, CancellationToken ct)
    {
        SetStatus($"正在检查 {w.Display}…");
        var now = Now();

        var found = await CollectNewPostsAsync(w, ct);
        if (found is null)
        {
            // 站点没响应：水位线不动，下一轮会重新发现这批新作品
            Save(w.ArtistId, lastCheck: now, lastResult: "检查失败（站点无响应）");
            return;
        }
        if (found.Count == 0)
        {
            Save(w.ArtistId, lastCheck: now, lastResult: "无新作品");
            return;
        }

        SetStatus($"{w.Display}：发现 {found.Count} 篇新作品，正在加入下载…");
        var artist = new PawchiveArtist { Id = w.ArtistId, Service = w.Service, Name = w.ArtistName };
        var result = await FanboxService.EnqueuePostsAsync(
            artist, found,
            w.TargetFolder.Length > 0 ? w.TargetFolder : null,
            w.TargetLib.Length > 0 ? w.TargetLib : null);

        // 一篇都没进队、也没有一篇是"跳过"的 → 入队本身失败了（多半是没设下载缓存目录）。
        // 这时绝不能推进水位线，否则这批新作品就永远错过了。
        if (result.PostCount == 0 && result.Skipped == 0)
        {
            Save(w.ArtistId, lastCheck: now, lastResult: "入队失败：" + (result.Error ?? "未知错误"));
            Logger.Warning($"fanbox 监控 {w.Display} 入队失败：{result.Error}");
            return;
        }

        var baseline = NewestPublished(found, w.LastPublished);
        // 这批投稿一个发布时间都没有（异常数据）时水位线推不动，改用当前时刻兜底，
        // 否则每一轮都会把同一批作品重新翻一遍
        if (baseline == w.LastPublished)
            baseline = NowIso();

        var summary = result.PostCount > 0
            ? $"新增 {result.PostCount} 篇 / {result.FileCount} 个文件"
            : "新作品都已在库或队列中";
        if (result.Skipped > 0 && result.PostCount > 0)
            summary += $"，跳过 {result.Skipped} 篇";
        Save(w.ArtistId, lastCheck: now, lastPublished: baseline,
            lastResult: summary, addDownloaded: result.PostCount);
        if (result.PostCount > 0)
            Logger.Info($"fanbox 监控 {w.Display}：自动下载 {result.PostCount} 篇新作品 / {result.FileCount} 个文件");
    }

    /// <summary>
    /// 取比水位线更新的投稿。站点按发布时间倒序返回，故整页都不比水位线新时即可收手；
    /// 水位线为空（要连现有作品一起下）时会一直翻到最后一页。
    /// 任何一页取失败都整体作废（返回 null）——只翻了一半就推进水位线，会把没看到的旧投稿永久跳过。
    /// </summary>
    private static async Task<List<PawchivePost>?> CollectNewPostsAsync(FanboxWatch w, CancellationToken ct)
    {
        var found = new List<PawchivePost>();
        for (var page = 0; page < MaxPages; page++)
        {
            ct.ThrowIfCancellationRequested();
            var batch = await PawchiveApi.GetPostsAsync(
                w.Service, w.ArtistId, page * PawchiveApi.PageSize);
            if (batch is null)
                return null;
            if (batch.Count == 0)
                break;
            var fresh = batch.Where(p => IsNewer(p.Published, w.LastPublished)).ToList();
            found.AddRange(fresh);
            if (fresh.Count == 0)
                break;          // 整页都是旧的：再往下只会更旧
            if (batch.Count < PawchiveApi.PageSize)
                break;          // 不足一页即最后一页
        }
        // 站上是"新→旧"，入队时倒过来按"旧→新"，下载顺序才与发布顺序一致
        found.Reverse();
        return found;
    }

    // ---------- 小工具 ----------

    /// <summary>投稿的发布时间是 ISO 串（2026-08-29T23:53:10），字典序比较即时间序。</summary>
    private static bool IsNewer(string published, string baseline) =>
        published.Length > 0 && string.CompareOrdinal(published, baseline) > 0;

    private static string NewestPublished(IEnumerable<PawchivePost> posts, string current)
    {
        foreach (var p in posts)
            if (IsNewer(p.Published, current))
                current = p.Published;
        return current;
    }

    /// <summary>该条监控下次该检查的时刻（没查过 / 时间解析不了的一律视为"现在就该查"）。</summary>
    private static DateTime DueAt(FanboxWatch w)
    {
        if (w.LastCheck.Length == 0 ||
            !DateTime.TryParse(w.LastCheck, CultureInfo.InvariantCulture, DateTimeStyles.None, out var last))
            return DateTime.MinValue;
        return last.AddMinutes(w.IntervalMin);
    }

    private static int NormalizeInterval(int minutes) =>
        minutes <= 0
            ? AppConfig.FanboxWatchInterval
            : Math.Clamp(minutes, MinIntervalMin, MaxIntervalMin);

    /// <summary>只更新传入的那几列（其余保持原样）。</summary>
    private static void Save(string artistId, string? lastCheck = null, string? lastPublished = null,
        string? lastResult = null, int addDownloaded = 0)
    {
        var sets = new List<string>();
        var args = new List<(string, object?)>();
        if (lastCheck != null)
        {
            sets.Add("\"last_check\" = @lc");
            args.Add(("@lc", lastCheck));
        }
        if (lastPublished != null)
        {
            sets.Add("\"last_published\" = @lp");
            args.Add(("@lp", lastPublished));
        }
        if (lastResult != null)
        {
            sets.Add("\"last_result\" = @lr");
            args.Add(("@lr", lastResult));
        }
        if (addDownloaded > 0)
        {
            sets.Add("\"downloaded\" = COALESCE(\"downloaded\", 0) + @add");
            args.Add(("@add", addDownloaded));
        }
        if (sets.Count == 0)
            return;
        args.Add(("@a", artistId));
        Db.Execute(
            $"UPDATE \"fanbox_watch\" SET {string.Join(", ", sets)} WHERE \"artist_id\" = @a",
            args.ToArray());
    }

    private static void SetStatus(string text)
    {
        lock (Sync)
            _status = text;
    }

    private static string Now() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

    /// <summary>与站点发布时间同格式的当前时刻（水位线兜底用）。</summary>
    private static string NowIso() => DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");

    private static FanboxWatch Map(object?[] r) => new()
    {
        ArtistId = r[0] as string ?? "",
        Service = r[1] as string is { Length: > 0 } s ? s : PawchiveApi.FanboxService,
        ArtistName = r[2] as string ?? "",
        IntervalMin = r[3] is null ? 360 : Math.Clamp(Convert.ToInt32(r[3]), MinIntervalMin, MaxIntervalMin),
        Enabled = (r[4] as string ?? "1") != "0",
        LastCheck = r[5] as string ?? "",
        LastPublished = r[6] as string ?? "",
        TargetLib = r[7] as string ?? "",
        TargetFolder = r[8] as string ?? "",
        LastResult = r[9] as string ?? "",
        Downloaded = r[10] is null ? 0 : Convert.ToInt32(r[10]),
        AddTime = r[11] as string ?? "",
    };
}
