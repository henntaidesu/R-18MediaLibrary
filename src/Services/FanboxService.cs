using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using R18MediaLibrary.Core;

namespace R18MediaLibrary.Services;

/// <summary>一次 fanbox 批量入队的结果。</summary>
public class FanboxEnqueueResult
{
    /// <summary>实际入队的作品数。</summary>
    public int PostCount { get; init; }
    /// <summary>实际入队的文件数。</summary>
    public int FileCount { get; init; }
    /// <summary>已在库/已在下载队列而跳过的作品数。</summary>
    public int Skipped { get; init; }
    public string? Error { get; init; }
    public bool Ok => Error is null && PostCount > 0;
}

/// <summary>
/// fanbox（pawchive）下载编排。
///
/// fanbox 作品和 DLsite 作品一样存在 works 表里，用 <c>works.source = 'fanbox'</c> 区分来源
/// （与 asmr 的做法一致）。这样媒体库的所有层级、扫描导入、移动媒体库、已读/收藏都不需要任何特例，
/// 删掉下载记录后也能像 DLsite 作品那样靠「扫描媒体库」重新导回来。
///
/// 作品号用 <see cref="WorkIdPrefix"/> + 站上的作品号（如 FB12506673），作品文件夹即以此命名——
/// 不用标题命名，避免改标题/重名导致目录对不上，也让顶层扫描能认出它们。
/// 下载队列复用 download_list（source='fanbox'，url 即直链），沿用同一套断点续传/限速/暂停设施。
/// </summary>
public static class FanboxService
{
    /// <summary>作品号前缀：FB + 站上作品号。RJ/BJ/VJ 不会以此开头，故可安全区分。</summary>
    public const string WorkIdPrefix = "FB";

    /// <summary>works.source 的取值，标记该作品来自 fanbox。</summary>
    public const string SourceName = "fanbox";

    /// <summary>作品形式（works.work_type），媒体库「作品形式」分区据此归类。</summary>
    public const string WorkTypeName = "FANBOX";

    /// <summary>作品目录内保存正文的文本文件名。</summary>
    public const string PostTextFile = "post.txt";

    public static string WorkIdOf(string postId) => WorkIdPrefix + postId;

    public static string PostIdOf(string workId) =>
        workId.StartsWith(WorkIdPrefix, StringComparison.OrdinalIgnoreCase)
            ? workId[WorkIdPrefix.Length..] : workId;

    public static bool IsFanboxWorkId(string workId) =>
        workId.StartsWith(WorkIdPrefix, StringComparison.OrdinalIgnoreCase) &&
        workId.Length > WorkIdPrefix.Length &&
        workId[WorkIdPrefix.Length..].All(char.IsAsciiDigit);

    // ---------- 入队 ----------

    /// <summary>
    /// 把选中的作品加入下载队列：逐篇 UPSERT works 行，再按文件逐条写 download_list，最后启动下载引擎。
    /// 已入库（已品悦）或已在队列中的作品会跳过。
    /// </summary>
    public static Task<FanboxEnqueueResult> EnqueuePostsAsync(
        PawchiveArtist artist, IReadOnlyList<PawchivePost> posts,
        string? targetFolder = null, string? targetLib = null) =>
        Task.Run(() => EnqueuePosts(artist, posts, targetFolder, targetLib));

    private static FanboxEnqueueResult EnqueuePosts(
        PawchiveArtist artist, IReadOnlyList<PawchivePost> posts,
        string? targetFolder, string? targetLib)
    {
        if (posts.Count == 0)
            return new FanboxEnqueueResult { Error = "未选择任何作品" };
        if (string.IsNullOrEmpty(AppConfig.DownloadPath))
            return new FanboxEnqueueResult { Error = "尚未设置下载缓存目录（请在系统设置中填写）" };

        var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.ffffff");
        int postCount = 0, fileCount = 0, skipped = 0;

        foreach (var post in posts)
        {
            var workId = WorkIdOf(post.Id);
            if (IsBusy(workId))
            {
                skipped++;
                continue;   // 已入库或队列里还有未完成的文件
            }
            // 正文里的谷歌网盘链接也算作品文件：作者常把本体（压缩包）放网盘、站上只归档一张封面图。
            // 共享文件夹要现拉一次目录展开成逐个文件，故放在 IsBusy 之后，免得为已入队的作品白跑一趟。
            var driveFiles = ResolveDriveFiles(post);
            if (post.Files.Count == 0 && driveFiles.Count == 0)
            {
                skipped++;
                continue;   // 纯文字作品，既无附件也无可下载的外链
            }

            // meta_scanned='1'：元数据已从 pawchive 取到，别让 DL API 的两级元数据补全去动它
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
                ("@w", workId), ("@n", post.Title), ("@mi", artist.Id), ("@mn", artist.Name),
                ("@t", WorkTypeName), ("@s", post.Content), ("@g", string.Join(", ", post.Tags)),
                ("@d", FormatPublished(post.Published)), ("@src", SourceName), ("@time", now));

            SyncGenres(workId, post.Tags);

            // 入队目标媒体库目录（重启后仍可恢复）
            if (!string.IsNullOrEmpty(targetFolder))
                DownloadEngine.SetWorkTargetPath(workId, targetFolder, targetLib);

            // 重新入队前清掉旧队列记录，避免残留文件行导致永远"未完成"
            Db.Execute("DELETE FROM \"download_list\" WHERE \"work_id\" = @w", ("@w", workId));

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var index = 0;
            void Enqueue(string url, string subPath)
            {
                Db.Execute(
                    "INSERT OR REPLACE INTO \"download_list\" " +
                    "(\"UUID\", \"work_id\", \"url\", \"status\", \"long\", \"delete\", \"source\", \"sub_path\") " +
                    "VALUES (@uuid, @w, @url, '0', '0', '1', @src, @sub)",
                    ("@uuid", Guid.NewGuid().ToString()), ("@w", workId),
                    ("@url", url), ("@src", SourceName), ("@sub", subPath));
                fileCount++;
            }

            foreach (var file in post.Files)
            {
                index++;
                Enqueue(DownloadUrl(file, post.Id), FileLeafName(index, file.Name, file.Path, names));
            }
            foreach (var (fileId, name) in driveFiles)
            {
                index++;
                // 单条文件链接在入队时还不知道文件名（要现请求一次网盘才有），先挂个占位名；
                // 下载线程解析出真名后会就地改掉 sub_path（见 DownloadEngine.FixDriveFileName）。
                // 从共享文件夹展开出来的文件在列目录时已经有名字，直接用。
                var leaf = FileLeafName(
                    index, name.Length > 0 ? name : DrivePlaceholderName + fileId, "", names, keepName: true);
                Enqueue(DriveDownloadUrl(fileId, post.Id), leaf);
            }
            postCount++;
        }

        if (postCount == 0)
            return new FanboxEnqueueResult
            {
                Skipped = skipped,
                Error = skipped > 0 ? "选中的作品都已在库或已在下载队列中" : "选中的作品没有可下载的文件",
            };

        Logger.Info($"fanbox {artist.Name} 已加入下载队列：{postCount} 篇作品 / {fileCount} 个文件");
        DownloadEngine.Start();
        return new FanboxEnqueueResult { PostCount = postCount, FileCount = fileCount, Skipped = skipped };
    }

    /// <summary>发布时间 ISO（2026-08-29T23:53:10）转成 DLsite 的「年月日」写法，两种来源才能混排。</summary>
    private static string FormatPublished(string published)
    {
        if (published.Length < 10)
            return published;
        return $"{published[..4]}年{published.Substring(5, 2)}月{published.Substring(8, 2)}日";
    }

    /// <summary>
    /// 文件下载直链。站点按内容哈希存文件，同一张图可能同时出现在多篇作品里（路径相同 → URL 相同），
    /// 而 download_list 以 url 为主键，直接复用会让后入队的作品顶掉前一篇的记录；
    /// 故附加一个作品号参数保证每篇作品的每个文件都是独立记录（服务端忽略未知参数）。
    /// </summary>
    private static string DownloadUrl(PawchiveFile file, string postId)
    {
        var url = PawchiveApi.FileUrl(file.Path, file.Name);
        return url + (url.Contains('?') ? "&" : "?") + "pcid=" + postId;
    }

    // ---------- 正文里的谷歌网盘链接 ----------

    /// <summary>还不知道真名的网盘文件的占位名前缀（下载线程解析出真名后就地替换）。</summary>
    public const string DrivePlaceholderName = "谷歌网盘_";

    /// <summary>
    /// 网盘文件在队列里的地址。存的是分享页地址而非直链——直链要现请求网盘才能拿到、
    /// 还带一次性参数，故与 E-Hentai 的图片页同理，由下载线程临下载前现解析。
    /// 同一个网盘文件可能被多篇投稿引用，仍要带作品号（download_list 以 url 为主键）。
    /// </summary>
    private static string DriveDownloadUrl(string fileId, string postId) =>
        GoogleDriveClient.FileUrl(fileId) + "?pcid=" + postId;

    /// <summary>
    /// 把投稿正文里的谷歌网盘链接摊成待下载文件表（网盘文件号 + 已知文件名，文件名未知时为空）。
    /// 共享文件夹会现拉一次目录展开成逐个文件；拉不到（未公开分享/已删除）就记一笔日志跳过。
    /// </summary>
    private static List<(string FileId, string Name)> ResolveDriveFiles(PawchivePost post)
    {
        var files = new List<(string, string)>();
        foreach (var link in GoogleDriveClient.LinksIn(post.Links))
        {
            if (link.Kind == DriveLinkKind.File)
            {
                files.Add((link.Id, ""));
                continue;
            }
            var entries = GoogleDriveClient.ListFolderFilesAsync(link.Id).GetAwaiter().GetResult();
            if (entries.Count == 0)
            {
                Logger.Warning($"fanbox {WorkIdOf(post.Id)} 的谷歌网盘文件夹取不到内容: {link.Url}");
                continue;
            }
            Logger.Info($"fanbox {WorkIdOf(post.Id)} 的谷歌网盘文件夹展开出 {entries.Count} 个文件");
            files.AddRange(entries.Select(e => (e.Id, e.Name)));
        }
        return files;
    }

    /// <summary>
    /// 社团分组查询里取该社团的 fanbox 作家号的 SQL 片段。
    ///
    /// 同一个社团名下可能混着多来源的作品，只挑 source='fanbox' 那一行的 maker_id
    /// （即 pawchive 作家号）；社团里没有 fanbox 作品时为 NULL。prefix 传表别名（如 "w."）。
    /// </summary>
    public static string MakerIdExpr(string prefix) =>
        $"MAX(CASE WHEN {prefix}\"source\" = '{SourceName}' THEN {prefix}\"maker_id\" END)";

    /// <summary>
    /// fanbox 社团（= pawchive 作家）的头像地址；不是 fanbox 社团则返回空串。
    /// 取图须带 <see cref="PawchiveApi.UserAgent"/>，Web 端则要经 /api/fanbox/image 代理。
    /// </summary>
    public static string MakerIconUrl(string? fanboxMakerId) =>
        string.IsNullOrEmpty(fanboxMakerId)
            ? ""
            : PawchiveApi.IconUrl(PawchiveApi.FanboxService, fanboxMakerId);

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

    /// <summary>
    /// 作品内的文件名。
    ///
    /// 图片一律命名为「三位序号 + 扩展名」（001.jpg）：站点的图片附件名是随机串（形如
    /// cl6TGSqZd0AopE9lk6EbWM8n.png），留着没有任何意义，序号才是浏览时需要的顺序信息。
    /// 其余附件（压缩包 / 视频 / PSD 等）的文件名通常有含义，保留原名并加同样的序号前缀。
    /// 序号在整篇作品内连续递增、图片与非图片共用一个计数器，以保持站上的原始顺序。
    ///
    /// keepName=true 时即使是图片也保留原名（网盘里的文件名是作者自己起的，有意义，
    /// 不像站上图片附件那样是随机串）。
    /// </summary>
    private static string FileLeafName(
        int index, string name, string path, HashSet<string> seen, bool keepName = false)
    {
        // 附件名偶尔不带扩展名，退回用站上哈希路径的扩展名
        var ext = NormalizeExtension(Path.GetExtension(name));
        if (ext.Length == 0)
            ext = NormalizeExtension(Path.GetExtension(path));

        string leaf;
        if (!keepName && ImageExts.Contains(ext))
        {
            leaf = $"{index:D3}{ext}";
        }
        else
        {
            var clean = SanitizeSegment(name);
            if (clean.Length == 0 || clean == "_")
                clean = SanitizeSegment(Path.GetFileName(path));
            leaf = $"{index:D3}_{clean}";
            if (leaf.Length > 120)
                leaf = leaf[..(120 - ext.Length)] + ext;
        }

        if (seen.Add(leaf))
            return leaf;
        // 同一作品内序号唯一，正常不会撞名；异常数据兜底追加序号
        var e = Path.GetExtension(leaf);
        var stem = leaf[..^e.Length];
        for (var i = 2; ; i++)
        {
            var candidate = $"{stem}_{i}{e}";
            if (seen.Add(candidate))
                return candidate;
        }
    }

    /// <summary>扩展名规范化为小写的 ".xxx"；不像扩展名（为空/过长/含非字母数字）时返回空串。</summary>
    private static string NormalizeExtension(string? ext)
    {
        ext = (ext ?? "").ToLowerInvariant();
        return ext.Length is > 1 and <= 6 && ext[0] == '.' && ext[1..].All(char.IsAsciiLetterOrDigit)
            ? ext : "";
    }

    /// <summary>按图片处理的扩展名（命名规则与封面定位共用）。</summary>
    private static readonly string[] ImageExts =
        [".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".avif", ".jfif"];

    /// <summary>净化成合法的 Windows 目录/文件名片段。</summary>
    public static string SanitizeSegment(string name)
    {
        name ??= "";
        foreach (var ch in "\\/:*?\"<>|")
            name = name.Replace(ch, ' ');
        name = new string(name.Where(c => !char.IsControl(c)).ToArray());
        name = string.Join(' ', name.Split(' ', StringSplitOptions.RemoveEmptyEntries)).TrimEnd('.', ' ');
        return name.Length == 0 ? "_" : name;
    }

    /// <summary>
    /// 标签写进 work_genres（与 DLsite 作品同一张表），「作品标签」分区因此能一起统计。
    /// 标签原文同时留在 works.genre 里供详情页显示。
    /// </summary>
    public static void SyncGenres(string workId, IEnumerable<string> tags)
    {
        Db.Execute("DELETE FROM \"work_genres\" WHERE \"work_id\" = @w", ("@w", workId));
        foreach (var tag in tags)
        {
            var one = (tag ?? "").Trim();
            if (one.Length > 0)
                Db.Execute(
                    "INSERT OR IGNORE INTO \"work_genres\" (\"work_id\", \"genre\") VALUES (@w, @g)",
                    ("@w", workId), ("@g", one));
        }
    }

    // ---------- 下载完成后的收尾 ----------

    /// <summary>
    /// 下载完成后的收尾：复用 DLsite 那套入库流程（移动到媒体库 + 标记已品悦 + 关联媒体库），
    /// 再补上 fanbox 特有的两件事——把正文写成文本文件、把封面指向目录内第一张图。
    /// 元数据在入队时已从 pawchive 取到并置了 meta_scanned='1'，故不会触发 DL API 补全。
    /// </summary>
    internal static void FinalizeIntoLibrary(string workId)
    {
        var folderBefore = DownloadEngine.WorkFolderPath(workId);
        UnzipService.FinalizeIntoLibrary(workId, folderBefore);

        var folder = Db.Scalar(
            "SELECT \"folder\" FROM \"works\" WHERE \"work_id\" = @w", ("@w", workId)) as string ?? folderBefore;
        WritePostText(workId, folder);
        if (FindCover(folder) is { } cover)
            Db.Execute("UPDATE \"works\" SET \"cover\" = @c WHERE \"work_id\" = @w",
                ("@c", cover), ("@w", workId));
    }

    /// <summary>把标题/作家/发布时间/标签/正文写成作品目录下的文本文件，便于离线查看。</summary>
    private static void WritePostText(string workId, string folder)
    {
        if (!Directory.Exists(folder))
            return;
        var rows = Db.Select(
            "SELECT \"work_name\", \"sell_date\", \"genre\", \"intro_s\", \"maker_name\" " +
            "FROM \"works\" WHERE \"work_id\" = @w", ("@w", workId));
        if (rows is not { Count: > 0 })
            return;
        var r = rows[0];
        var lines = new List<string>
        {
            r[0] as string ?? "",
            $"作家：{r[4] as string ?? ""}",
            $"发布：{r[1] as string ?? ""}",
        };
        if (r[2] as string is { Length: > 0 } tags)
            lines.Add($"标签：{tags}");
        // 源站没归档原图、改存了预览图的，记一笔：日后翻到这篇才知道画质为何偏低
        var thumbs = Db.Scalar(
            "SELECT COUNT(*) FROM \"download_list\" WHERE \"work_id\" = @w " +
            "AND \"status\" = '1' AND \"error\" = @e",
            ("@w", workId), ("@e", DownloadEngine.ThumbError));
        if (thumbs != null && Convert.ToInt64(thumbs) > 0)
            lines.Add($"注：其中 {Convert.ToInt64(thumbs)} 张为 800px 预览图（源站未归档原图）");
        // 作品本体来自网盘时记下出处：正文里的链接可能是 <a> 标签（纯文本正文里看不到地址），
        // 日后想回源找原件，这一行就是唯一的线索
        foreach (var url in DriveLinksOf(workId))
            lines.Add($"网盘：{url}");
        if (r[3] as string is { Length: > 0 } content)
        {
            lines.Add("");
            lines.Add(content);
        }
        try
        {
            File.WriteAllText(Path.Combine(folder, PostTextFile), string.Join(Environment.NewLine, lines));
        }
        catch (IOException e)
        {
            Logger.Error($"fanbox 写入作品正文失败 {workId}: {e.Message}");
        }
    }

    /// <summary>该作品下载队列里的谷歌网盘分享页地址（去重，保持入队顺序）。</summary>
    private static List<string> DriveLinksOf(string workId)
    {
        var urls = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var rows = Db.Select(
            "SELECT \"url\" FROM \"download_list\" WHERE \"work_id\" = @w", ("@w", workId));
        foreach (var r in rows ?? [])
        {
            if (r[0] as string is not { Length: > 0 } url || GoogleDriveClient.Parse(url) is not { } link)
                continue;
            if (seen.Add(link.Id))
                urls.Add(link.Url);
        }
        return urls;
    }

    /// <summary>
    /// 作品封面：目录内按名称排序的第一张图片（文件名的三位序号保证与站上顺序一致）。
    /// E-Hentai 来源的命名规则与此一致（三位页码 + 扩展名），故共用这一个实现。
    ///
    /// 根目录一张图都没有时，再往下找一层：作品本体在网盘上（正文只给一条分享链接）的投稿，
    /// 图全在解压出来的子目录里，不往下找就只能留一张空白卡片。
    /// </summary>
    internal static string? FindCover(string folder)
    {
        if (!Directory.Exists(folder))
            return null;
        try
        {
            return FirstImage(folder) ?? Directory
                .EnumerateDirectories(folder)
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .Select(FirstImage)
                .FirstOrDefault(f => f != null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>该目录内按名称排序的第一张图片（不递归）。</summary>
    private static string? FirstImage(string folder) =>
        Directory.EnumerateFiles(folder)
            .Where(f => ImageExts.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

    // ---------- 供搜索页用的查询 ----------

    /// <summary>该作家下已入库或已在队列中的作品号集合（作家主页据此标记"已下载/下载中"）。</summary>
    public static Dictionary<string, string> PostStates(string artistId)
    {
        var map = new Dictionary<string, string>();
        var rows = Db.Select(
            "SELECT \"work_id\", \"state\" FROM \"works\" WHERE \"source\" = @src AND \"maker_id\" = @a",
            ("@src", SourceName), ("@a", artistId));
        foreach (var r in rows ?? [])
            map[PostIdOf(r[0] as string ?? "")] = r[1] as string ?? "";
        return map;
    }
}
