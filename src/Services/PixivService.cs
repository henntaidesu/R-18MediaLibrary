using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using R18MediaLibrary.Core;

namespace R18MediaLibrary.Services;

/// <summary>一次 pixiv 批量入队的结果。</summary>
public class PixivEnqueueResult
{
    /// <summary>实际入队的作品数。</summary>
    public int ArtworkCount { get; init; }
    /// <summary>实际入队的文件数（图片 + 动图 zip）。</summary>
    public int FileCount { get; init; }
    /// <summary>已在库/已在下载队列而跳过的作品数。</summary>
    public int Skipped { get; init; }
    public string? Error { get; init; }
    public bool Ok => Error is null && ArtworkCount > 0;
}

/// <summary>
/// pixiv 下载编排。
///
/// 与 fanbox / E-Hentai 同构：作品照样存进 works 表，用 <c>works.source = 'pixiv'</c> 区分来源，
/// 作品号是 <see cref="WorkIdPrefix"/> + 站点的 illust id（如 PX100000000），作品文件夹也以此命名——
/// 于是媒体库的层级、扫描导入、移动媒体库、已读/收藏全都不必加特例，删掉下载记录后
/// 还能靠「扫描媒体库」把它重新导回来。一件作品（一篇插画/漫画）就是一个作品行，
/// 社团即作者（maker_id 存 pixiv 用户号）。
///
/// 与 E-Hentai 不同的是：pixiv 的图片地址是真直链（不绑 IP、不过期），入队时就能全部拿到，
/// 故 <c>download_list.url</c> 直接存图片地址，下载线程无需现解析——只是取图必须带
/// <see cref="PixivApi.ImageReferer"/>（防盗链），见 DownloadEngine 的 pixiv 分支。
/// </summary>
public static class PixivService
{
    /// <summary>作品号前缀：PX + 站点作品号。RJ/BJ/VJ/FB/EH 都不会以此开头，故可安全区分。</summary>
    public const string WorkIdPrefix = "PX";

    /// <summary>works.source 的取值。</summary>
    public const string SourceName = PixivApi.SourceName;

    /// <summary>作品形式（works.work_type），媒体库「作品形式」分区据此归类。</summary>
    public const string WorkTypeName = "PIXIV";

    /// <summary>作品目录内保存作品信息的文本文件名。</summary>
    public const string InfoTextFile = "artwork.txt";

    /// <summary>动图本体（逐帧 zip）在作品目录内的文件名。</summary>
    public const string UgoiraFile = "ugoira.zip";

    public static string WorkIdOf(string illustId) => WorkIdPrefix + illustId;

    public static string IllustIdOf(string workId) =>
        workId.StartsWith(WorkIdPrefix, StringComparison.OrdinalIgnoreCase)
            ? workId[WorkIdPrefix.Length..] : workId;

    /// <summary>该作品号是否为 pixiv 作品（前缀 PX + 全数字）。</summary>
    public static bool IsPixivWorkId(string workId) =>
        workId.StartsWith(WorkIdPrefix, StringComparison.OrdinalIgnoreCase) &&
        workId.Length > WorkIdPrefix.Length &&
        workId[WorkIdPrefix.Length..].All(char.IsAsciiDigit);

    // ---------- 入队 ----------

    /// <summary>
    /// 把选中的作品加入下载队列：逐件取齐元数据与图片地址，UPSERT works 行，
    /// 再按图片逐条写 download_list，最后启动下载引擎。
    /// 已入库（已品悦）或已在队列中的作品会跳过。
    /// onProgress 回调 (当前第几件, 总件数, 作品标题)，供 UI 显示抓取进度。
    ///
    /// 入口只收作品号、元数据一律在这里现取：搜索结果里的条目没有说明文与收藏数
    /// （站点的列表接口就不给），直接拿它入库会比从详情页入队的同一件作品少一截信息。
    /// 两端都走这一个入口，入库结果才一致。
    /// </summary>
    public static Task<PixivEnqueueResult> EnqueueByIdsAsync(
        IReadOnlyList<string> illustIds, string? targetFolder = null, string? targetLib = null,
        Action<int, int, string>? onProgress = null) =>
        Task.Run(() => Enqueue(illustIds, targetFolder, targetLib, onProgress));

    private static async Task<PixivEnqueueResult> Enqueue(
        IReadOnlyList<string> illustIds, string? targetFolder, string? targetLib,
        Action<int, int, string>? onProgress)
    {
        if (illustIds.Count == 0)
            return new PixivEnqueueResult { Error = "未选择任何作品" };
        if (string.IsNullOrEmpty(AppConfig.DownloadPath))
            return new PixivEnqueueResult { Error = "尚未设置下载缓存目录（请在系统设置中填写）" };

        var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.ffffff");
        var original = AppConfig.PixivOriginal;
        int artworkCount = 0, fileCount = 0, skipped = 0;
        string? lastError = null;

        for (var i = 0; i < illustIds.Count; i++)
        {
            var illustId = illustIds[i];
            var workId = WorkIdOf(illustId);
            onProgress?.Invoke(i + 1, illustIds.Count, illustId);

            if (IsBusy(workId))
            {
                skipped++;
                continue;   // 已入库或队列里还有未完成的文件；元数据也就不必白取一次
            }

            var (art, error) = await PixivApi.GetArtworkAsync(illustId);
            if (art is null)
            {
                lastError = $"{workId} 取不到作品信息：{error ?? "站点无响应"}";
                Logger.Error($"pixiv {workId} 取不到作品信息：{error}");
                continue;
            }
            onProgress?.Invoke(i + 1, illustIds.Count, Display(art));

            // 图片地址现取：详情接口只给封面一张，逐页地址要另走 pages 接口
            var pages = await PixivApi.GetPagesAsync(illustId);
            if (pages.Count == 0)
            {
                lastError = $"《{Display(art)}》没有取到任何图片（作品可能已删除，或 R-18 作品需要登录）";
                Logger.Error($"pixiv {workId} 未取到图片清单");
                continue;
            }

            // 动图：pages 只给一张静止帧，动画本体是另一个逐帧 zip，两者都要下
            // （只存静止帧等于把动图存成了插画；只存 zip 则媒体库里连封面都没有）
            var ugoira = art.IsUgoira ? await PixivApi.GetUgoiraAsync(illustId) : default;
            var tags = BuildTags(art);

            // meta_scanned='1'：元数据已从站点接口取到，别让 DL API 的两级元数据补全去动它
            Db.Execute(
                "INSERT INTO \"works\" (\"work_id\", \"work_name\", \"maker_id\", \"maker_name\", " +
                "\"work_type\", \"intro_s\", \"genre\", \"sell_date\", \"state\", \"source\", " +
                "\"meta_scanned\", \"down_time\") VALUES " +
                "(@w, @n, @mi, @mn, @t, @s, @g, @d, '下载中', @src, '1', @time) " +
                "ON CONFLICT(\"work_id\") DO UPDATE SET " +
                "\"work_name\" = excluded.\"work_name\", \"maker_id\" = excluded.\"maker_id\", " +
                "\"maker_name\" = excluded.\"maker_name\", \"work_type\" = excluded.\"work_type\", " +
                "\"intro_s\" = excluded.\"intro_s\", \"genre\" = excluded.\"genre\", " +
                "\"sell_date\" = excluded.\"sell_date\", \"state\" = excluded.\"state\", " +
                "\"source\" = excluded.\"source\", \"meta_scanned\" = '1', " +
                "\"down_time\" = excluded.\"down_time\", " +
                "\"folder\" = NULL, \"target\" = NULL, \"target_lib\" = NULL, \"cover\" = NULL",
                ("@w", workId), ("@n", Display(art)), ("@mi", art.UserId),
                ("@mn", MakerNameOf(art)), ("@t", WorkTypeName), ("@s", BuildIntro(art)),
                ("@g", string.Join(", ", tags)), ("@d", FormatCreated(art)),
                ("@src", SourceName), ("@time", now));

            FanboxService.SyncGenres(workId, tags);

            // 入队目标媒体库目录（重启后仍可恢复）
            if (!string.IsNullOrEmpty(targetFolder))
                DownloadEngine.SetWorkTargetPath(workId, targetFolder, targetLib);

            // 重新入队前清掉旧队列记录，避免残留文件行导致永远"未完成"
            Db.Execute("DELETE FROM \"download_list\" WHERE \"work_id\" = @w", ("@w", workId));

            var added = 0;
            foreach (var page in pages)
            {
                var url = page.Best(original);
                if (url.Length == 0)
                    continue;
                AddRow(workId, url, $"{page.Index:D3}{PixivApi.ExtensionOf(url)}");
                added++;
            }
            if (ugoira.Ok)
            {
                AddRow(workId, ugoira.ZipUrl, UgoiraFile);
                added++;
            }
            // 一条都没写进队列的话，works 行会永远停在「下载中」而没有任何任务去推进它
            if (added == 0)
            {
                lastError = $"《{Display(art)}》的图片地址为空，未加入队列";
                Logger.Error($"pixiv {workId} 图片地址为空，未写入下载队列");
                Db.Execute("DELETE FROM \"works\" WHERE \"work_id\" = @w AND \"state\" = '下载中'",
                    ("@w", workId));
                continue;
            }
            fileCount += added;
            artworkCount++;
        }

        if (artworkCount == 0)
            return new PixivEnqueueResult
            {
                Skipped = skipped,
                Error = lastError ??
                        (skipped > 0 ? "选中的作品都已在库或已在下载队列中" : "选中的作品没有可下载的图片"),
            };

        Logger.Info($"pixiv 已加入下载队列：{artworkCount} 件作品 / {fileCount} 个文件");
        DownloadEngine.Start();
        return new PixivEnqueueResult
        {
            ArtworkCount = artworkCount, FileCount = fileCount, Skipped = skipped,
        };
    }

    /// <summary>写一条下载任务。sub_path 即该文件在作品目录内的名字（图片为三位页码，动图为 ugoira.zip）。</summary>
    private static void AddRow(string workId, string url, string subPath) =>
        Db.Execute(
            "INSERT OR REPLACE INTO \"download_list\" " +
            "(\"UUID\", \"work_id\", \"url\", \"status\", \"long\", \"delete\", \"source\", \"sub_path\") " +
            "VALUES (@uuid, @w, @url, '0', '0', '1', @src, @sub)",
            ("@uuid", Guid.NewGuid().ToString()), ("@w", workId), ("@url", url),
            ("@src", SourceName), ("@sub", subPath));

    /// <summary>展示用标题；站上允许空标题，那就退回作品号，免得媒体库里出现无名卡片。</summary>
    private static string Display(PixivArtwork art) =>
        art.Title.Length > 0 ? art.Title : WorkIdOf(art.Id);

    /// <summary>
    /// 标签：站点原始标签原样保留（与 E-Hentai 同理，翻译随语言变、原文才是稳定的分组键），
    /// 再把作品形式与分级也塞进去当标签，这样「作品标签」分区里按「漫画」「R-18」筛选也走得通。
    /// </summary>
    private static List<string> BuildTags(PixivArtwork art)
    {
        var tags = new List<string> { art.TypeName };
        if (art.RestrictName.Length > 0)
            tags.Add(art.RestrictName);
        if (art.IsAi)
            tags.Add("AI生成");
        tags.AddRange(art.Tags);
        return tags;
    }

    /// <summary>社团名即作者名；站上允许改名但不允许为空，真空了就退回用户号。</summary>
    public static string MakerNameOf(PixivArtwork art) =>
        art.UserName.Length > 0 ? art.UserName : (art.UserId.Length > 0 ? art.UserId : "(未知作者)");

    /// <summary>简介：说明正文 + 尺寸/张数/收藏数，详情页直接显示。</summary>
    private static string BuildIntro(PixivArtwork art)
    {
        var lines = new List<string>();
        if (art.Description.Length > 0)
        {
            lines.Add(art.Description);
            lines.Add("");
        }
        lines.Add($"形式：{art.TypeName}" + (art.RestrictName.Length > 0 ? $"（{art.RestrictName}）" : ""));
        if (art.PageCount > 0)
            lines.Add($"张数：{art.PageCount}");
        if (art.Width > 0 && art.Height > 0)
            lines.Add($"尺寸：{art.Width}×{art.Height}");
        if (art.BookmarkCount > 0)
            lines.Add($"收藏：{art.BookmarkCount}");
        if (art.IsAi)
            lines.Add("注：站点标记为 AI 生成作品");
        return string.Join('\n', lines);
    }

    /// <summary>投稿时间转成 DLsite 的「年月日」写法，多来源作品才能按日期混排。</summary>
    private static string FormatCreated(PixivArtwork art) =>
        art.Created is { } t ? $"{t.Year:D4}年{t.Month:D2}月{t.Day:D2}日" : "";

    /// <summary>该作品是否已入库或仍有未完成的下载任务（据此跳过重复入队）。</summary>
    private static bool IsBusy(string workId)
    {
        var state = Db.Scalar(
            "SELECT \"state\" FROM \"works\" WHERE \"work_id\" = @w", ("@w", workId)) as string;
        if (state == "已品悦")
            return true;
        var pending = Db.Scalar(
            "SELECT COUNT(*) FROM \"download_list\" WHERE \"work_id\" = @w AND \"status\" != '1'",
            ("@w", workId));
        return pending != null && Convert.ToInt64(pending) > 0;
    }

    /// <summary>已入库或已在队列中的作品 {作品号: 状态}（搜索结果卡据此标记"已下载/下载中"）。</summary>
    public static Dictionary<string, string> ArtworkStates(IEnumerable<string> illustIds)
    {
        var map = new Dictionary<string, string>();
        foreach (var id in illustIds)
        {
            if (Db.Scalar("SELECT \"state\" FROM \"works\" WHERE \"work_id\" = @w",
                    ("@w", WorkIdOf(id))) is string state && state.Length > 0)
                map[id] = state;
        }
        return map;
    }

    // ---------- 下载完成后的收尾 ----------

    /// <summary>
    /// 下载完成后的收尾：复用 DLsite 那套入库流程（移动到媒体库 + 标记已品悦 + 关联媒体库），
    /// 再补上本来源特有的两件事——把作品信息写成文本文件、把封面指向目录内第一张图。
    /// 元数据在入队时已从站点接口取到并置了 meta_scanned='1'，故不会触发 DL API 补全。
    ///
    /// **不解压**：动图的逐帧 zip 解开就散成上百张无序帧图、还会丢掉播放速度，
    /// 它必须原样留着（帧延迟写在下面的说明文件里）。
    /// </summary>
    internal static void FinalizeIntoLibrary(string workId)
    {
        var folderBefore = DownloadEngine.WorkFolderPath(workId);
        UnzipService.FinalizeIntoLibrary(workId, folderBefore);

        var folder = Db.Scalar(
            "SELECT \"folder\" FROM \"works\" WHERE \"work_id\" = @w", ("@w", workId)) as string ?? folderBefore;
        WriteInfoText(workId, folder);
        if (FanboxService.FindCover(folder) is { } cover)
            Db.Execute("UPDATE \"works\" SET \"cover\" = @c WHERE \"work_id\" = @w",
                ("@c", cover), ("@w", workId));
    }

    /// <summary>
    /// 把标题/作者/投稿时间/标签/说明写成作品目录下的文本文件，便于离线查看。
    /// 动图还要多写一行帧延迟——那是还原播放速度的唯一依据，只存 zip 就丢了。
    /// </summary>
    private static void WriteInfoText(string workId, string folder)
    {
        if (!Directory.Exists(folder))
            return;
        var rows = Db.Select(
            "SELECT \"work_name\", \"sell_date\", \"genre\", \"intro_s\", \"maker_name\", \"maker_id\" " +
            "FROM \"works\" WHERE \"work_id\" = @w", ("@w", workId));
        if (rows is not { Count: > 0 })
            return;
        var r = rows[0];
        var illustId = IllustIdOf(workId);
        var lines = new List<string>
        {
            r[0] as string ?? "",
            $"作者：{r[4] as string ?? ""}",
            $"投稿：{r[1] as string ?? ""}",
            $"原址：{PixivApi.ArtworkUrl(illustId)}",
        };
        if (r[5] as string is { Length: > 0 } userId)
            lines.Add($"作者主页：{PixivApi.UserUrl(userId)}");
        if (r[2] as string is { Length: > 0 } tags)
            lines.Add($"标签：{tags}");
        AppendUgoiraDelays(illustId, folder, lines);
        if (r[3] as string is { Length: > 0 } intro)
        {
            lines.Add("");
            lines.Add(intro);
        }
        try
        {
            File.WriteAllText(Path.Combine(folder, InfoTextFile), string.Join(Environment.NewLine, lines));
        }
        catch (IOException e)
        {
            Logger.Error($"pixiv 写入作品信息失败 {workId}: {e.Message}");
        }
    }

    /// <summary>
    /// 动图作品补一行帧延迟。
    ///
    /// 延迟只在站点的 ugoira_meta 接口里、zip 内没有，所以在这里重新取一次
    /// （每件动图一次请求，可忽略）。取不到就不写这一行，不因此中断入库——
    /// zip 本身已经下好了，缺一行说明不值得让整件作品卡住。
    /// </summary>
    private static void AppendUgoiraDelays(string illustId, string folder, List<string> lines)
    {
        if (!File.Exists(Path.Combine(folder, UgoiraFile)))
            return;
        try
        {
            var ugoira = PixivApi.GetUgoiraAsync(illustId).GetAwaiter().GetResult();
            if (ugoira.Frames.Count > 0)
                lines.Add($"动图帧延迟(毫秒)：{string.Join(',', ugoira.Frames)}");
        }
        catch (Exception e)
        {
            Logger.Warning($"pixiv 取动图帧延迟失败 {illustId}: {e.Message}");
        }
    }
}
