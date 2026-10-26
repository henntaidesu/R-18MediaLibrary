using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using R18MediaLibrary.Core;

namespace R18MediaLibrary.Services;

/// <summary>pawchive 作家（creator）条目。</summary>
public class PawchiveArtist
{
    public string Id { get; init; } = "";
    public string Service { get; init; } = PawchiveApi.FanboxService;
    public string Name { get; init; } = "";
    public string PublicId { get; init; } = "";
    public int Favorited { get; init; }
    /// <summary>站上最后更新时间（unix 秒，0 表示未知）。</summary>
    public long UpdatedUnix { get; init; }
}

/// <summary>作品内的单个文件：站上哈希路径 + 原始文件名。</summary>
public class PawchiveFile
{
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
}

/// <summary>pawchive 上的一篇作品（fanbox post）。</summary>
public class PawchivePost
{
    public string Id { get; init; } = "";
    public string Service { get; init; } = PawchiveApi.FanboxService;
    public string ArtistId { get; init; } = "";
    public string Title { get; init; } = "";
    /// <summary>正文（已去掉 HTML 标签）。</summary>
    public string Content { get; init; } = "";
    public List<string> Tags { get; init; } = [];
    public string Published { get; init; } = "";
    /// <summary>封面的站上哈希路径（形如 /e1/1d/xxxx.jpeg），可空。</summary>
    public string CoverPath { get; init; } = "";
    public List<PawchiveFile> Files { get; init; } = [];
    /// <summary>
    /// 正文里出现的外部链接（去重、保持原顺序）。
    /// 作者常把作品本体放在网盘上、正文只留一条分享链接，站上归档的附件就只剩一张封面图；
    /// 其中的谷歌网盘链接由 <see cref="GoogleDriveClient"/> 认出来并一并下载。
    /// </summary>
    public List<string> Links { get; init; } = [];
}

/// <summary>
/// pawchive.pw 客户端（Kemono 系公开 API，无需登录）：
///   /api/v1/creators                     → 全站作家表（约 15MB；站点自身也是前端本地过滤，没有服务端搜索接口）
///   /api/v1/{service}/user/{id}/profile  → 作家信息
///   /api/v1/{service}/user/{id}?o={n}    → 作品列表，每页 50 条
/// 附件直链在 file.&lt;host&gt;（主站 /data 会 404），缩略图在 img.&lt;host&gt;，头像/横幅在主站。
/// 作家表下载后落盘缓存，24 小时内复用；搜索在本地内存表上做（只保留 fanbox 服务的条目）。
/// </summary>
public static class PawchiveApi
{
    public const string FanboxService = "fanbox";

    /// <summary>站点固定的作品列表分页大小。</summary>
    public const int PageSize = 50;

    /// <summary>
    /// 访问 pawchive 专用的中性 User-Agent。
    /// 站点前置 DDoS-Guard，会对"带浏览器 UA 却没有浏览器握手（JS 挑战 cookie / sec-ch-ua 等）"的请求
    /// 直接回 403——附件域 file.&lt;host&gt; 尤其严格；换成非浏览器 UA 反而按普通客户端放行。
    /// 故本站的所有请求（含下载引擎取附件时）都必须用这个 UA，不要用 Http.UserAgent 的 Chrome UA。
    /// </summary>
    public const string UserAgent = "R-18MediaLibrary/1.0";

    private static string Host => AppConfig.PawchiveHost;
    private static string ApiBase => $"https://{Host}/api/v1";

    /// <summary>附件下载直链（支持 Range 断点续传）。</summary>
    public static string FileUrl(string path, string name) =>
        $"https://file.{Host}/data{path}" +
        (string.IsNullOrEmpty(name) ? "" : $"?f={Uri.EscapeDataString(name)}");

    /// <summary>站上缩略图（未下载的作品在列表里用它当封面）。</summary>
    public static string ThumbUrl(string path) => $"https://img.{Host}/thumbnail/data{path}";

    /// <summary>
    /// 由附件直链推出对应的预览图地址；不是 /data/ 开头的链接返回空串。
    ///
    /// 站点对"只导入了元数据、没归档文件本体"的投稿（has_full=false）原图一律 404，
    /// 但 img.&lt;host&gt; 上的 800px 预览图是有的，可在原图缺失时兜底（见 DownloadEngine 的 missing 分支）。
    /// 预览图只按路径寻址，直链上的 ?f=&amp;pcid= 查询串要去掉。
    /// </summary>
    public static string ThumbUrlFromFileUrl(string fileUrl)
    {
        try
        {
            var path = new Uri(fileUrl).AbsolutePath;   // /data/ab/cd/<hash>.png
            return path.StartsWith("/data/", StringComparison.Ordinal)
                ? $"https://img.{Host}/thumbnail{path}"
                : "";
        }
        catch (UriFormatException)
        {
            return "";
        }
    }

    public static string IconUrl(string service, string id) => $"https://{Host}/icons/{service}/{id}";

    public static string BannerUrl(string service, string id) => $"https://{Host}/banners/{service}/{id}";

    /// <summary>作家主页地址（供 UI 展示/外链）。</summary>
    public static string ArtistPageUrl(string service, string id) => $"https://{Host}/{service}/user/{id}";

    // ---------- 作家表缓存 ----------

    private static string CacheDir => Path.Combine(Environment.CurrentDirectory, "cache");
    private static string CreatorsFile => Path.Combine(CacheDir, "pawchive-creators.json");
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);

    // 解析后的 fanbox 作家表：只留四个字段，整表常驻内存，反复搜索无需重新读盘
    private static List<PawchiveArtist>? _creators;
    private static readonly SemaphoreSlim CreatorsLock = new(1, 1);

    /// <summary>作家表是否已就绪（UI 据此提示"正在下载作家索引"）。</summary>
    public static bool CreatorsReady => _creators is { Count: > 0 };

    /// <summary>作家表缓存文件的落盘时间；无缓存返回 null。</summary>
    public static DateTime? CreatorsCachedAt =>
        File.Exists(CreatorsFile) ? File.GetLastWriteTime(CreatorsFile) : null;

    /// <summary>
    /// 确保本地有可用的作家表：内存已加载直接返回；缓存文件在有效期内则读盘；
    /// 否则下载整表落盘后再读。force=true 时强制重新下载。
    /// </summary>
    public static async Task<bool> EnsureCreatorsAsync(bool force = false)
    {
        if (!force && CreatorsReady)
            return true;
        await CreatorsLock.WaitAsync();
        try
        {
            if (!force && CreatorsReady)
                return true;
            var stale = !File.Exists(CreatorsFile) ||
                        DateTime.Now - File.GetLastWriteTime(CreatorsFile) > CacheTtl;
            if (force || stale)
                await DownloadCreatorsAsync();
            if (!File.Exists(CreatorsFile))
                return false;
            _creators = LoadCreators();
            return CreatorsReady;
        }
        catch (Exception e)
        {
            Logger.Error(e, "pawchive 作家表");
            return false;
        }
        finally
        {
            CreatorsLock.Release();
        }
    }

    /// <summary>下载整份作家表到缓存文件（先写临时文件再改名，避免中断留下半截文件）。</summary>
    private static async Task DownloadCreatorsAsync()
    {
        Logger.Info("正在下载 pawchive 作家索引…");
        using var client = Http.CreateClient(TimeSpan.FromMinutes(5), UserAgent);
        using var resp = await client.GetAsync($"{ApiBase}/creators", HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();
        Directory.CreateDirectory(CacheDir);
        var tmp = CreatorsFile + ".tmp";
        await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            await resp.Content.CopyToAsync(fs);
        File.Move(tmp, CreatorsFile, overwrite: true);
        Logger.Info($"pawchive 作家索引已更新：{new FileInfo(CreatorsFile).Length / 1024 / 1024} MB");
    }

    /// <summary>读缓存文件，只保留 fanbox 服务的作家条目。</summary>
    private static List<PawchiveArtist> LoadCreators()
    {
        var list = new List<PawchiveArtist>();
        using var stream = File.OpenRead(CreatorsFile);
        using var doc = JsonDocument.Parse(stream);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return list;
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (DlsiteApi.JStr(item, "service") != FanboxService)
                continue;
            list.Add(new PawchiveArtist
            {
                Id = DlsiteApi.JStr(item, "id"),
                Service = FanboxService,
                Name = DlsiteApi.JStr(item, "name"),
                PublicId = DlsiteApi.JStr(item, "public_id"),
                Favorited = JInt(item, "favorited"),
                UpdatedUnix = JLong(item, "updated"),
            });
        }
        Logger.Info($"pawchive 作家索引已加载：{list.Count} 位 fanbox 作家");
        return list;
    }

    /// <summary>
    /// 在本地作家表里搜索：作家名或 public_id 包含关键字（忽略大小写）。
    /// 完全相同者排最前，其余按被收藏数降序，最多返回 limit 条。
    /// </summary>
    public static async Task<List<PawchiveArtist>> SearchArtistsAsync(string keyword, int limit = 60)
    {
        keyword = (keyword ?? "").Trim();
        if (keyword.Length == 0 || !await EnsureCreatorsAsync())
            return [];
        return _creators!
            .Where(a => a.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                        a.PublicId.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(a => string.Equals(a.PublicId, keyword, StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(a.Name, keyword, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(a => a.Favorited)
            .Take(limit)
            .ToList();
    }

    // ---------- 作家 / 作品 ----------

    /// <summary>取作家信息（/profile）；失败返回 null。</summary>
    public static async Task<PawchiveArtist?> GetArtistAsync(string service, string artistId)
    {
        var text = await GetAsync($"{ApiBase}/{service}/user/{artistId}/profile");
        if (text is null)
            return null;
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            return new PawchiveArtist
            {
                Id = DlsiteApi.JStr(root, "id"),
                Service = DlsiteApi.JStr(root, "service"),
                Name = DlsiteApi.JStr(root, "name"),
                PublicId = DlsiteApi.JStr(root, "public_id"),
            };
        }
        catch (JsonException e)
        {
            Logger.Error(e, "pawchive 解析作家信息");
            return null;
        }
    }

    /// <summary>取作家的一页作品（offset 为条目偏移，步长 PageSize）；失败返回 null。</summary>
    public static async Task<List<PawchivePost>?> GetPostsAsync(string service, string artistId, int offset)
    {
        var url = $"{ApiBase}/{service}/user/{artistId}" + (offset > 0 ? $"?o={offset}" : "");
        var text = await GetAsync(url);
        if (text is null)
            return null;
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return null;
            var posts = new List<PawchivePost>();
            foreach (var item in doc.RootElement.EnumerateArray())
                posts.Add(ParsePost(item, service));
            return posts;
        }
        catch (JsonException e)
        {
            Logger.Error(e, "pawchive 解析作品列表");
            return null;
        }
    }

    /// <summary>
    /// 取单篇作品的详情；失败返回 null。
    ///
    /// 列表接口每页 50 篇、正文与附件清单都带着，但「查看内容」只需要其中一篇，
    /// 故走站点的单篇接口，避免为看一篇而把整页重新拉一遍。返回结构与列表项一致，
    /// 因此复用 ParsePost——展示的文件清单也就与下载时的清单完全同源。
    /// </summary>
    public static async Task<PawchivePost?> GetPostAsync(string service, string artistId, string postId)
    {
        var text = await GetAsync($"{ApiBase}/{service}/user/{artistId}/post/{postId}");
        if (text is null)
            return null;
        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                ? ParsePost(doc.RootElement, service)
                : null;
        }
        catch (JsonException e)
        {
            Logger.Error(e, "pawchive 解析作品详情");
            return null;
        }
    }

    /// <summary>取作家全部作品（逐页拉到尾），每页回调一次已获取总数。</summary>
    public static async Task<List<PawchivePost>> GetAllPostsAsync(
        string service, string artistId, Action<int>? onPage = null, int maxPages = 200)
    {
        var all = new List<PawchivePost>();
        for (var page = 0; page < maxPages; page++)
        {
            var batch = await GetPostsAsync(service, artistId, page * PageSize);
            if (batch is null || batch.Count == 0)
                break;
            all.AddRange(batch);
            onPage?.Invoke(all.Count);
            if (batch.Count < PageSize)
                break;   // 不足一页即最后一页
        }
        return all;
    }

    private static PawchivePost ParsePost(JsonElement item, string service)
    {
        // 站点的 file 字段是作者设定的帖子封面，多是从作品里裁出来的横幅（如 800x420），
        // 既不代表作品内容、也是 attachments 之外的另一份文件；attachments 才是作品本体。
        // 因此：下载清单只要 attachments，封面取其中第一张图片。
        // 但附件里一张图都没有时（纯压缩包/视频帖），file 是这篇唯一能当封面的图，
        // 不下下来作品入库后卡片就是一片空白——这时把它补进清单。
        PawchiveFile? siteCover = null;
        if (item.TryGetProperty("file", out var file) && file.ValueKind == JsonValueKind.Object)
        {
            var path = DlsiteApi.JStr(file, "path");
            if (path.Length > 0)
                siteCover = new PawchiveFile { Name = DlsiteApi.JStr(file, "name"), Path = path };
        }

        var files = new List<PawchiveFile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (item.TryGetProperty("attachments", out var atts) && atts.ValueKind == JsonValueKind.Array)
            foreach (var att in atts.EnumerateArray())
            {
                var path = DlsiteApi.JStr(att, "path");
                if (path.Length > 0 && seen.Add(path))
                    files.Add(new PawchiveFile { Name = DlsiteApi.JStr(att, "name"), Path = path });
            }
        if (siteCover != null && !files.Any(f => IsImageName(f.Name)))
            files.Add(siteCover);

        // 封面 = 清单里的第一张图片；清单里没有图片（纯压缩包/视频帖）时退回站点封面
        var coverPath = files.FirstOrDefault(f => IsImageName(f.Name))?.Path
                        ?? siteCover?.Path
                        ?? (files.Count > 0 ? files[0].Path : "");

        var contentHtml = DlsiteApi.JStr(item, "content");
        return new PawchivePost
        {
            Id = DlsiteApi.JStr(item, "id"),
            Service = service,
            ArtistId = DlsiteApi.JStr(item, "user"),
            Title = DlsiteApi.JStr(item, "title"),
            Content = StripHtml(contentHtml),
            Tags = ParseTags(item),
            Published = DlsiteApi.JStr(item, "published"),
            CoverPath = coverPath,
            Files = files,
            Links = ExtractLinks(contentHtml, item),
        };
    }

    /// <summary>正文/嵌入块里的外部链接，按 http(s) 整段匹配。</summary>
    private static readonly System.Text.RegularExpressions.Regex UrlPattern =
        new(@"https?://[^\s""'<>]+", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// 抽出正文里的外部链接：<c>&lt;a href&gt;</c> 与光秃秃写在文字里的地址都要认
    /// （同一作者两种写法都见得到），外加 fanbox 的 embed 块里的地址。
    /// 先反转义 HTML 实体，免得 <c>&amp;amp;</c> 把查询串切坏；按出现顺序去重。
    /// </summary>
    private static List<string> ExtractLinks(string contentHtml, JsonElement item)
    {
        var links = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string url)
        {
            // 紧跟在地址后面的中英文标点不属于地址（正文里常写成「…/view?usp=sharing。」）
            url = url.TrimEnd('.', ',', '，', '。', '、', ')', '）', ']', '】', ';', '；', ':', '：');
            if (url.Length > 0 && seen.Add(url))
                links.Add(url);
        }

        if (contentHtml.Length > 0)
            foreach (System.Text.RegularExpressions.Match m in
                     UrlPattern.Matches(HtmlAgilityPack.HtmlEntity.DeEntitize(contentHtml) ?? contentHtml))
                Add(m.Value);
        if (item.TryGetProperty("embed", out var embed) && embed.ValueKind == JsonValueKind.Object)
            foreach (var key in new[] { "url", "description" })
                foreach (System.Text.RegularExpressions.Match m in UrlPattern.Matches(DlsiteApi.JStr(embed, key)))
                    Add(m.Value);
        return links;
    }

    /// <summary>按图片看待的扩展名（决定卡片封面取哪一张）。</summary>
    private static readonly string[] ImageExts =
        [".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".avif", ".jfif"];

    internal static bool IsImageName(string name) =>
        ImageExts.Contains(Path.GetExtension(name).ToLowerInvariant());

    /// <summary>tags 字段可能是 Postgres 数组字面量字符串（形如 {a,b}）或 JSON 数组，两种都解析。</summary>
    private static List<string> ParseTags(JsonElement item)
    {
        var tags = new List<string>();
        if (!item.TryGetProperty("tags", out var t))
            return tags;
        if (t.ValueKind == JsonValueKind.Array)
        {
            foreach (var one in t.EnumerateArray())
                if (one.ValueKind == JsonValueKind.String && one.GetString() is { Length: > 0 } s)
                    tags.Add(s);
            return tags;
        }
        if (t.ValueKind != JsonValueKind.String)
            return tags;
        var raw = (t.GetString() ?? "").Trim();
        if (raw.StartsWith('{') && raw.EndsWith('}'))
            raw = raw[1..^1];
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var one = part.Trim().Trim('"').Trim();
            if (one.Length > 0)
                tags.Add(one);
        }
        return tags;
    }

    /// <summary>正文是 HTML 片段，转成纯文本保存/展示（br 与块级标签换行）。</summary>
    private static string StripHtml(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return "";
        try
        {
            var doc = new HtmlAgilityPack.HtmlDocument();
            doc.LoadHtml(html
                .Replace("<br>", "\n").Replace("<br/>", "\n").Replace("<br />", "\n")
                .Replace("</p>", "\n").Replace("</div>", "\n"));
            var text = HtmlAgilityPack.HtmlEntity.DeEntitize(doc.DocumentNode.InnerText) ?? "";
            var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0);
            return string.Join('\n', lines);
        }
        catch (Exception e)
        {
            Logger.Error(e, "pawchive 正文转纯文本");
            return "";
        }
    }

    // ---------- HTTP ----------

    /// <summary>GET 并返回响应文本；网络错误或非 2xx 返回 null（已记日志）。</summary>
    private static async Task<string?> GetAsync(string url)
    {
        try
        {
            using var client = Http.CreateClient(TimeSpan.FromSeconds(30), UserAgent);
            using var resp = await client.GetAsync(url);
            if (!resp.IsSuccessStatusCode)
            {
                Logger.Error($"pawchive 请求失败 HTTP {(int)resp.StatusCode}: {url}");
                return null;
            }
            return await resp.Content.ReadAsStringAsync();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            Logger.Error($"pawchive 网络请求失败: {e.Message} ({url})");
            return null;
        }
    }

    private static int JInt(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number &&
        v.TryGetInt32(out var i) ? i : 0;

    private static long JLong(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number &&
        v.TryGetInt64(out var i) ? i : 0;
}
