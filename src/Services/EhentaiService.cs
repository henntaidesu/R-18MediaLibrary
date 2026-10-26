using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using R18MediaLibrary.Core;

namespace R18MediaLibrary.Services;

/// <summary>一次 E-Hentai 批量入队的结果。</summary>
public class EhentaiEnqueueResult
{
    /// <summary>实际入队的画廊数。</summary>
    public int GalleryCount { get; init; }
    /// <summary>实际入队的图片数。</summary>
    public int FileCount { get; init; }
    /// <summary>已在库/已在下载队列而跳过的画廊数。</summary>
    public int Skipped { get; init; }
    public string? Error { get; init; }
    public bool Ok => Error is null && GalleryCount > 0;
}

/// <summary>
/// E-Hentai 下载编排。
///
/// 与 fanbox 同构：画廊照样存进 works 表，用 <c>works.source = 'ehentai'</c> 区分来源，
/// 作品号是 <see cref="WorkIdPrefix"/> + 画廊 gid（如 EH3456789），作品文件夹也以此命名——
/// 于是媒体库的层级、扫描导入、移动媒体库、已读/收藏全都不必加特例，删掉下载记录后
/// 还能靠「扫描媒体库」把它重新导回来。
///
/// 与 fanbox 不同的是：图片直链不能提前拿。站点每张图各有一个图片页，直链在页面里、
/// 且与访问者 IP 绑定又会过期，所以 <c>download_list.url</c> 存的是图片页地址，
/// 由下载线程临下载前现解析（与论坛源经 debrid-link 现解析同构）。
/// </summary>
public static class EhentaiService
{
    /// <summary>作品号前缀：EH + 画廊 gid。RJ/BJ/VJ/FB 都不会以此开头，故可安全区分。</summary>
    public const string WorkIdPrefix = "EH";

    /// <summary>works.source 的取值。</summary>
    public const string SourceName = EhentaiApi.SourceName;

    /// <summary>作品形式（works.work_type），媒体库「作品形式」分区据此归类。</summary>
    public const string WorkTypeName = "E-HENTAI";

    /// <summary>作品目录内保存画廊信息的文本文件名。</summary>
    public const string InfoTextFile = "gallery.txt";

    public static string WorkIdOf(long gid) => WorkIdPrefix + gid;

    public static string GidOf(string workId) =>
        workId.StartsWith(WorkIdPrefix, StringComparison.OrdinalIgnoreCase)
            ? workId[WorkIdPrefix.Length..] : workId;

    /// <summary>该作品号是否为 E-Hentai 画廊（前缀 EH + 全数字）。</summary>
    public static bool IsEhentaiWorkId(string workId) =>
        workId.StartsWith(WorkIdPrefix, StringComparison.OrdinalIgnoreCase) &&
        workId.Length > WorkIdPrefix.Length &&
        workId[WorkIdPrefix.Length..].All(char.IsAsciiDigit);

    // ---------- 入队 ----------

    /// <summary>
    /// 把选中的画廊加入下载队列：逐本抓齐图片页清单，UPSERT works 行，
    /// 再按图片逐条写 download_list，最后启动下载引擎。
    /// 已入库（已品悦）或已在队列中的画廊会跳过。
    /// onProgress 回调 (当前第几本, 总本数, 画廊标题)，供 UI 显示抓取进度——
    /// 一本几百张图要翻十几页画廊页，这一步比 fanbox 慢得多，不给反馈会像卡死。
    /// </summary>
    public static Task<EhentaiEnqueueResult> EnqueueGalleriesAsync(
        IReadOnlyList<EhGallery> galleries, string? targetFolder = null, string? targetLib = null,
        Action<int, int, string>? onProgress = null) =>
        Task.Run(() => EnqueueGalleries(galleries, targetFolder, targetLib, onProgress));

    private static async Task<EhentaiEnqueueResult> EnqueueGalleries(
        IReadOnlyList<EhGallery> galleries, string? targetFolder, string? targetLib,
        Action<int, int, string>? onProgress)
    {
        if (galleries.Count == 0)
            return new EhentaiEnqueueResult { Error = "未选择任何画廊" };
        if (string.IsNullOrEmpty(AppConfig.DownloadPath))
            return new EhentaiEnqueueResult { Error = "尚未设置下载缓存目录（请在系统设置中填写）" };
        if (EhentaiApi.IsExHentai && !EhentaiApi.HasCookie)
            return new EhentaiEnqueueResult { Error = "exhentai 需要登录 cookie，请在系统设置 → E-Hentai 中填写" };

        var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.ffffff");
        int galleryCount = 0, fileCount = 0, skipped = 0;
        string? lastError = null;

        for (var i = 0; i < galleries.Count; i++)
        {
            var gallery = galleries[i];
            var workId = WorkIdOf(gallery.Gid);
            onProgress?.Invoke(i + 1, galleries.Count, gallery.DisplayTitle);

            if (IsBusy(workId))
            {
                skipped++;
                continue;   // 已入库或队列里还有未完成的图片
            }

            // 图片页清单要现抓：一页画廊页只列 20 张，几百张的本子得翻十几页
            var pages = await EhentaiApi.GetImagePagesAsync(gallery.Ref, gallery.FileCount);
            if (pages.Count == 0)
            {
                lastError = $"《{gallery.DisplayTitle}》没有取到任何图片页（站点可能要求登录或已下架）";
                Logger.Error($"E-Hentai {workId} 未取到图片页清单");
                continue;
            }
            if (gallery.FileCount > 0 && pages.Count < gallery.FileCount)
                Logger.Warning(
                    $"E-Hentai {workId} 只取到 {pages.Count}/{gallery.FileCount} 张图片页，仍按已取到的入队");

            var tags = BuildTags(gallery);
            // meta_scanned='1'：元数据已从站点 API 取到，别让 DL API 的两级元数据补全去动它
            Db.Execute(
                "INSERT INTO \"works\" (\"work_id\", \"work_name\", \"maker_id\", \"maker_name\", " +
                "\"work_type\", \"intro_s\", \"genre\", \"sell_date\", \"state\", \"source\", " +
                "\"eh_token\", \"file_size\", \"meta_scanned\", \"down_time\") VALUES " +
                "(@w, @n, @mi, @mn, @t, @s, @g, @d, '下载中', @src, @tok, @size, '1', @time) " +
                "ON CONFLICT(\"work_id\") DO UPDATE SET " +
                "\"work_name\" = excluded.\"work_name\", \"maker_id\" = excluded.\"maker_id\", " +
                "\"maker_name\" = excluded.\"maker_name\", \"work_type\" = excluded.\"work_type\", " +
                "\"intro_s\" = excluded.\"intro_s\", \"genre\" = excluded.\"genre\", " +
                "\"sell_date\" = excluded.\"sell_date\", \"state\" = excluded.\"state\", " +
                "\"source\" = excluded.\"source\", \"eh_token\" = excluded.\"eh_token\", " +
                "\"file_size\" = excluded.\"file_size\", \"meta_scanned\" = '1', " +
                "\"down_time\" = excluded.\"down_time\", " +
                "\"folder\" = NULL, \"target\" = NULL, \"target_lib\" = NULL, \"cover\" = NULL",
                ("@w", workId), ("@n", gallery.DisplayTitle), ("@mi", MakerIdOf(gallery)),
                ("@mn", MakerNameOf(gallery)), ("@t", WorkTypeName), ("@s", BuildIntro(gallery)),
                ("@g", string.Join(", ", tags)), ("@d", FormatPosted(gallery.PostedUnix)),
                ("@src", SourceName), ("@tok", gallery.Token), ("@size", FormatSize(gallery.FileSize)),
                ("@time", now));

            FanboxService.SyncGenres(workId, tags);

            // 入队目标媒体库目录（重启后仍可恢复）
            if (!string.IsNullOrEmpty(targetFolder))
                DownloadEngine.SetWorkTargetPath(workId, targetFolder, targetLib);

            // 重新入队前清掉旧队列记录，避免残留文件行导致永远"未完成"
            Db.Execute("DELETE FROM \"download_list\" WHERE \"work_id\" = @w", ("@w", workId));

            foreach (var page in pages)
            {
                Db.Execute(
                    "INSERT OR REPLACE INTO \"download_list\" " +
                    "(\"UUID\", \"work_id\", \"url\", \"status\", \"long\", \"delete\", \"source\", \"sub_path\") " +
                    "VALUES (@uuid, @w, @url, '0', '0', '1', @src, @sub)",
                    ("@uuid", Guid.NewGuid().ToString()), ("@w", workId), ("@url", page.Url),
                    ("@src", SourceName), ("@sub", LeafName(page)));
                fileCount++;
            }
            galleryCount++;
        }

        if (galleryCount == 0)
            return new EhentaiEnqueueResult
            {
                Skipped = skipped,
                Error = lastError ??
                        (skipped > 0 ? "选中的画廊都已在库或已在下载队列中" : "选中的画廊没有可下载的图片"),
            };

        Logger.Info($"E-Hentai 已加入下载队列：{galleryCount} 本画廊 / {fileCount} 张图片");
        DownloadEngine.Start();
        return new EhentaiEnqueueResult
        {
            GalleryCount = galleryCount, FileCount = fileCount, Skipped = skipped,
        };
    }

    /// <summary>
    /// 作品内的文件名：三位页码 + 扩展名（001.jpg）。
    ///
    /// 站上的原始文件名多是扫描器流水号，留着无益，页码才是浏览时需要的顺序信息——
    /// 与 fanbox 的命名规则一致。扩展名取自画廊页缩略图 title 里的原始文件名；
    /// 那里没写时按 .jpg 算，真实扩展名在解析出直链后由下载线程纠正（见 DownloadEngine）。
    /// </summary>
    private static string LeafName(EhImagePage page) =>
        $"{page.Index:D3}{EhentaiApi.ExtensionOf(page.FileName)}";

    /// <summary>
    /// 标签：站点的带命名空间标签原样保留（artist:xxx / female:xxx / language:chinese …），
    /// 再把分类也塞进去当一个标签，这样「作品标签」分区里按分类筛选也走得通。
    /// </summary>
    private static List<string> BuildTags(EhGallery gallery)
    {
        var tags = new List<string>();
        if (gallery.Category.Length > 0)
            tags.Add(gallery.Category);
        tags.AddRange(gallery.Tags);
        return tags;
    }

    /// <summary>
    /// 社团名：优先 group（社团/团体），其次 artist（作者），都没有才退回投稿者。
    /// 同人志的圈子概念对应 group，与 DLsite 的「社团」最接近。
    /// </summary>
    public static string MakerNameOf(EhGallery gallery)
    {
        var group = gallery.TagsOf("group").FirstOrDefault();
        if (group is { Length: > 0 })
            return group;
        var artist = gallery.TagsOf("artist").FirstOrDefault();
        if (artist is { Length: > 0 })
            return artist;
        return gallery.Uploader.Length > 0 ? gallery.Uploader : "(未知社团)";
    }

    /// <summary>
    /// 社团号：站点没有社团 id，用「命名空间:标签」当稳定标识（如 group:xxx）。
    /// 它只用来分组与回查，不参与头像（站点没有社团头像，见 DlsiteMakerIcon 对本来源的排除）。
    /// </summary>
    public static string MakerIdOf(EhGallery gallery)
    {
        var group = gallery.TagsOf("group").FirstOrDefault();
        if (group is { Length: > 0 })
            return "group:" + group;
        var artist = gallery.TagsOf("artist").FirstOrDefault();
        if (artist is { Length: > 0 })
            return "artist:" + artist;
        return gallery.Uploader.Length > 0 ? "uploader:" + gallery.Uploader : "";
    }

    /// <summary>简介：把英文标题、分类、投稿者、评分、张数拼成一段，详情页直接显示。</summary>
    private static string BuildIntro(EhGallery gallery)
    {
        var lines = new List<string>();
        // 主标题用了日文名时，英文名这里留一份（站上两个名字往往信息量不同）
        if (gallery.Title.Length > 0 && gallery.Title != gallery.DisplayTitle)
            lines.Add(gallery.Title);
        lines.Add($"分类：{gallery.Category}");
        lines.Add($"投稿者：{gallery.Uploader}");
        if (gallery.Rating.Length > 0)
            lines.Add($"评分：{gallery.Rating}");
        if (gallery.FileCount > 0)
            lines.Add($"张数：{gallery.FileCount}");
        if (gallery.Expunged)
            lines.Add("注：该画廊已在站点被删除，内容可能不全");
        return string.Join('\n', lines);
    }

    /// <summary>投稿时间转成 DLsite 的「年月日」写法，多来源作品才能按日期混排。</summary>
    private static string FormatPosted(long unix)
    {
        if (unix <= 0)
            return "";
        var t = DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime;
        return $"{t.Year:D4}年{t.Month:D2}月{t.Day:D2}日";
    }

    /// <summary>字节数转成 works.file_size 惯用的可读写法。</summary>
    private static string FormatSize(long bytes)
    {
        if (bytes <= 0)
            return "";
        string[] units = ["B", "KB", "MB", "GB"];
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return $"{size:0.##} {units[unit]}";
    }

    /// <summary>该画廊是否已入库或仍有未完成的下载任务（据此跳过重复入队）。</summary>
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

    /// <summary>已入库或已在队列中的画廊 {gid: 状态}（搜索结果卡据此标记"已下载/下载中"）。</summary>
    public static Dictionary<string, string> GalleryStates(IEnumerable<long> gids)
    {
        var map = new Dictionary<string, string>();
        foreach (var gid in gids)
        {
            var workId = WorkIdOf(gid);
            if (Db.Scalar("SELECT \"state\" FROM \"works\" WHERE \"work_id\" = @w",
                    ("@w", workId)) is string state && state.Length > 0)
                map[gid.ToString()] = state;
        }
        return map;
    }

    // ---------- 下载完成后的收尾 ----------

    /// <summary>
    /// 下载完成后的收尾：复用 DLsite 那套入库流程（移动到媒体库 + 标记已品悦 + 关联媒体库），
    /// 再补上本来源特有的两件事——把画廊信息写成文本文件、把封面指向目录内第一张图。
    /// 元数据在入队时已从站点 API 取到并置了 meta_scanned='1'，故不会触发 DL API 补全。
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

    /// <summary>把标题/社团/投稿时间/标签/简介写成作品目录下的文本文件，便于离线查看。</summary>
    private static void WriteInfoText(string workId, string folder)
    {
        if (!Directory.Exists(folder))
            return;
        var rows = Db.Select(
            "SELECT \"work_name\", \"sell_date\", \"genre\", \"intro_s\", \"maker_name\", \"eh_token\" " +
            "FROM \"works\" WHERE \"work_id\" = @w", ("@w", workId));
        if (rows is not { Count: > 0 })
            return;
        var r = rows[0];
        var lines = new List<string>
        {
            r[0] as string ?? "",
            $"社团/作者：{r[4] as string ?? ""}",
            $"投稿：{r[1] as string ?? ""}",
        };
        if (long.TryParse(GidOf(workId), out var gid) && r[5] as string is { Length: > 0 } token)
            lines.Add($"原址：{EhentaiApi.GalleryUrl(gid, token)}");
        if (r[2] as string is { Length: > 0 } tags)
            lines.Add($"标签：{tags}");
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
            Logger.Error($"E-Hentai 写入画廊信息失败 {workId}: {e.Message}");
        }
    }
}
