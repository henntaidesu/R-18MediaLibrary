using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using R18MediaLibrary.Core;

namespace R18MediaLibrary.Services;

/// <summary>
/// fanbox.cc 官方站客户端（登录后可用）。与 pawchive 归档并列，作品搜索里显示为「FANBOX（账号名）」。
///
/// 只走站点自家前端那套 JSON 接口（api.fanbox.cc，返回 <c>{"body": …}</c>），鉴权只认一个 cookie
/// <c>FANBOXSESSID</c>，另外每个请求都必须带 <c>Origin: https://www.fanbox.cc</c>，缺了一律 400/403：
///   /creator.get?creatorId=     → 作家信息（user.userId / name / iconUrl）
///   /post.paginateCreator?creatorId= → 作家全部作品的分页地址表（每页一条 post.listCreator 地址）
///   /post.info?postId=          → 单篇详情（正文 + 图片/附件直链；未赞助的档位 body 为 null）
///   /plan.listSupporting        → 我赞助的作家（顺带用作登录态探针：未登录不给数据）
///   /creator.listFollowing      → 我关注的作家
///   /creator.search?q=          → 作家搜索
///
/// 与 pawchive 的数据对齐成同一套模型（<see cref="PawchiveArtist"/> / <see cref="PawchivePost"/>），
/// 下载编排、入库、媒体库全部复用 FanboxService；两边的作品号同是站上的投稿号（FB + 投稿号），
/// 作家归属（works.maker_id）同是 pixiv 用户号，所以同一篇从哪边下都是同一个作品。
///
/// 官方站的作家以 creatorId（字符串）寻址，<see cref="PawchiveArtist.Id"/> 存它，用户号在 UserId。
/// 图片/附件在 downloads.fanbox.cc，取图同样要带 cookie 与 Referer（见 <see cref="ApplyHeaders"/>）。
/// </summary>
public static class FanboxOfficialApi
{
    public const string ApiBase = "https://api.fanbox.cc";
    public const string Origin = "https://www.fanbox.cc";
    public const string Referer = Origin + "/";

    /// <summary>每页作品数取自 paginateCreator 给出的地址（limit 参数）；取不到时按站点现行的 10。</summary>
    private const int DefaultPageLimit = 10;

    /// <summary>作家主页一次给前端的页数：站点每页只有 10 篇，一次拼 5 页，下拉翻页的节奏才与 pawchive 的 50 篇相当。</summary>
    private const int PagesPerBatch = 5;

    public static bool HasCookie => AppConfig.FanboxSessionId.Length > 0;

    public static string CookieHeader => HasCookie ? $"FANBOXSESSID={AppConfig.FanboxSessionId}" : "";

    /// <summary>作家主页地址（供外链）。</summary>
    public static string CreatorPageUrl(string creatorId) => $"https://{creatorId}.fanbox.cc/";

    public static string PostPageUrl(string creatorId, string postId) =>
        $"https://{creatorId}.fanbox.cc/posts/{postId}";

    /// <summary>是否 fanbox.cc 官方站的地址（下载引擎、图片代理据此决定要不要带登录 cookie）。</summary>
    public static bool IsOfficialUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
        (uri.Host.Equals("fanbox.cc", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.EndsWith(".fanbox.cc", StringComparison.OrdinalIgnoreCase));

    /// <summary>作家头像所在的 pixiv 图床（官方站的头像地址指向这里，防盗链认 fanbox 的 Referer）。</summary>
    public static bool IsPximgUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
        uri.Host.EndsWith(".pximg.net", StringComparison.OrdinalIgnoreCase);

    // ---------- HTTP ----------

    // 同 PixivApi：一篇几十张图、详情页要逐张取预览，客户端必须复用；cookie / 代理变了才重建
    private static readonly object ClientLock = new();
    private static HttpClient? _client;
    private static string _clientKey = "";
    private static HttpClient? _downloadClient;
    private static string _downloadKey = "";

    private static HttpClient Client()
    {
        var key = $"{CookieHeader}|{ProxyKey()}";
        lock (ClientLock)
        {
            if (_client != null && _clientKey == key)
                return _client;
            _client?.Dispose();
            _client = Build(TimeSpan.FromSeconds(30));
            _clientKey = key;
            return _client;
        }
    }

    /// <summary>
    /// 下载引擎取官方站附件用的客户端（不限时，断点续传的大文件也能下完）。
    /// 下载线程共用的那个客户端带 cookie 罐，不能拿来下官方站，原因见 <see cref="Build"/>。
    /// 代理变了才换新的；旧的不 Dispose——可能还有文件正用它下着。
    /// </summary>
    internal static HttpClient DownloadClient()
    {
        var key = ProxyKey();
        lock (ClientLock)
        {
            if (_downloadClient == null || _downloadKey != key)
            {
                _downloadClient = Build(System.Threading.Timeout.InfiniteTimeSpan);
                _downloadKey = key;
            }
            return _downloadClient;
        }
    }

    private static string ProxyKey()
    {
        var (proxyOn, proxy) = AppConfig.ReadProxy();
        return proxyOn ? proxy.Address?.ToString() ?? "" : "";
    }

    /// <summary>
    /// 官方站专用客户端：<b>不带 cookie 罐</b>（UseCookies=false），登录 cookie 一律逐请求手写。
    ///
    /// 实测两个坑都出在 cookie 罐上：(1) 站点每次响应都会下发一个匿名的 FANBOXSESSID 与 Cloudflare 的 __cf_bm，
    /// 罐子把它们存下来、下一个请求又带回去之后，Cloudflare 对同一客户端的第二个请求起一律 403；
    /// (2) 登录后罐里那个匿名 FANBOXSESSID 会和手写的登录 cookie 同时发出去，站点认哪个全凭运气。
    /// 所以这里不能用 <see cref="Http.CreateClient"/>（它的 handler 开着 cookie 罐）。
    /// </summary>
    private static HttpClient Build(TimeSpan timeout)
    {
        var (enabled, proxy) = AppConfig.ReadProxy();
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = false,
        };
        if (enabled)
        {
            handler.Proxy = proxy;
            handler.UseProxy = true;
        }
        var client = new HttpClient(handler) { Timeout = timeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(Http.UserAgent);
        return client;
    }

    /// <summary>cookie / 代理变更后调用，下次请求用新配置重建客户端，并丢掉按旧账号缓存的数据。</summary>
    public static void Invalidate()
    {
        lock (ClientLock)
        {
            _client?.Dispose();
            _client = null;
            _clientKey = "";
        }
        lock (PagesCache)
            PagesCache.Clear();
        lock (CreatorCache)
            CreatorCache.Clear();
    }

    /// <summary>
    /// 站点按 Origin 判定是不是自家前端发来的请求（缺了就拒），按 cookie 认人；
    /// downloads.fanbox.cc 上的图片/附件同样要 cookie 与 Referer。
    /// </summary>
    internal static void ApplyHeaders(HttpRequestMessage request)
    {
        // 登录 cookie 只发给 fanbox.cc 自家的域；pximg 上的头像只认 Referer，不必把凭据也交出去
        if (CookieHeader.Length > 0 && IsOfficialUrl(request.RequestUri?.ToString() ?? ""))
            request.Headers.TryAddWithoutValidation("Cookie", CookieHeader);
        request.Headers.TryAddWithoutValidation("Origin", Origin);
        request.Headers.Referrer = new Uri(Referer);
    }

    /// <summary>
    /// 取一个接口的 body。返回 (body, 错误文案, HTTP 状态码)；成功时错误为 null。
    /// 站点未登录时多回 401/403，被 Cloudflare 拦下时回一张 HTML 挑战页，两者要分开说。
    /// </summary>
    private static async Task<(JsonElement? Body, string? Error, int Status)> GetBodyAsync(string url)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            ApplyHeaders(request);
            request.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
            using var resp = await Client().SendAsync(request);
            var text = await resp.Content.ReadAsStringAsync();
            var status = (int)resp.StatusCode;
            if (text.TrimStart().StartsWith('<'))
            {
                Logger.Error($"fanbox 返回的不是 JSON（HTTP {status}，多半被 Cloudflare 拦下）: {url}");
                return (null, "被 fanbox 的防护网关拦下了（Cloudflare）：稍后再试，或检查代理", status);
            }
            if (!resp.IsSuccessStatusCode)
            {
                Logger.Error($"fanbox 请求失败 HTTP {status}: {url}");
                // 站点对「没登录 / 会话失效 / 作家或作品不存在」一律回 400 + {"error":"general_error"}，分不出是哪种
                return (null, status switch
                {
                    400 => "站点拒绝了请求：FANBOXSESSID 无效或已过期，或者作家 / 作品不存在",
                    401 or 403 => "FANBOX 未登录或 FANBOXSESSID 已失效（请到「账号访问」重新填写）",
                    404 => "站点上没有这个作家或作品",
                    _ => $"站点返回 HTTP {status}",
                }, status);
            }
            using var doc = JsonDocument.Parse(text);
            if (!doc.RootElement.TryGetProperty("body", out var body))
                return (null, "站点返回的数据无法解析", status);
            // JsonDocument 随 using 释放，元素必须克隆出来才能带出本方法
            return (body.Clone(), null, status);
        }
        catch (JsonException e)
        {
            Logger.Error($"fanbox 返回的不是 JSON: {e.Message} ({url})");
            return (null, "站点返回的数据无法解析", 0);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            Logger.Error($"fanbox 网络请求失败: {e.Message} ({url})");
            return (null, "连不上 fanbox.cc：检查网络与代理设置", 0);
        }
    }

    // ---------- 登录态 ----------

    /// <summary>
    /// 检测登录：拿「我赞助的作家」当探针——这个接口只对已登录会话给数据，匿名一律 400 general_error。
    /// 成功时顺带按 FANBOXSESSID 里的用户号去 pixiv 查显示名（FANBOX 用的就是 pixiv 账号）。
    /// </summary>
    public static async Task<(bool Ok, string Name, string Message)> CheckLoginAsync()
    {
        if (!HasCookie)
            return (false, "", "未填写 FANBOXSESSID");
        var (body, error, status) = await GetBodyAsync($"{ApiBase}/plan.listSupporting");
        if (body is null)
            return (false, "", status is 400 or 401 or 403
                ? "FANBOXSESSID 无效或已过期（请重新从浏览器复制）"
                : error ?? "检测失败");
        var userId = PixivApi.UserIdFromSession(AppConfig.FanboxSessionId);
        var name = await PixivApi.GetUserNameAsync(userId) ?? userId;
        var supporting = body.Value.ValueKind == JsonValueKind.Array ? body.Value.GetArrayLength() : 0;
        return (true, name, $"登录有效（赞助中的作家 {supporting} 位）");
    }

    // ---------- 作家 ----------

    // creatorId → 作家信息。作家主页每翻一页、每次入队都要用到用户号，没必要每次都问站点
    private static readonly Dictionary<string, PawchiveArtist> CreatorCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 把用户输入认成 creatorId：作家主页链接（<c>xxx.fanbox.cc</c> / <c>www.fanbox.cc/@xxx</c>）、
    /// 作品链接，或直接就是 creatorId。认不出返回 null（当作搜索关键字）。
    /// </summary>
    public static string? ParseCreatorInput(string raw)
    {
        raw = (raw ?? "").Trim();
        var m = Regex.Match(raw, @"(?:https?://)?(?:www\.)?fanbox\.cc/@([A-Za-z0-9_-]+)", RegexOptions.IgnoreCase);
        if (m.Success)
            return m.Groups[1].Value;
        m = Regex.Match(raw, @"(?:https?://)?([A-Za-z0-9_-]+)\.fanbox\.cc", RegexOptions.IgnoreCase);
        if (m.Success && !m.Groups[1].Value.Equals("www", StringComparison.OrdinalIgnoreCase) &&
            !m.Groups[1].Value.Equals("api", StringComparison.OrdinalIgnoreCase))
            return m.Groups[1].Value;
        return null;
    }

    /// <summary>creatorId 只含字母数字、下划线与连字符；用作 URL 参数前先校验。</summary>
    public static bool IsCreatorId(string id) =>
        id.Length is > 0 and <= 64 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    /// <summary>取作家信息；失败返回 null。</summary>
    public static async Task<PawchiveArtist?> GetCreatorAsync(string creatorId)
    {
        if (!IsCreatorId(creatorId))
            return null;
        lock (CreatorCache)
            if (CreatorCache.TryGetValue(creatorId, out var hit))
                return hit;
        var (body, _, _) = await GetBodyAsync($"{ApiBase}/creator.get?creatorId={Uri.EscapeDataString(creatorId)}");
        if (body is not { } b || ParseCreator(b) is not { } artist)
            return null;
        lock (CreatorCache)
            CreatorCache[creatorId] = artist;
        return artist;
    }

    /// <summary>
    /// 搜作家。关键字为空时列出「我赞助的 + 我关注的」作家（登录后最常用的入口）；
    /// 否则先按 creatorId 精确找一次，再走站点的作家搜索，两边合并去重。
    /// </summary>
    public static async Task<(List<PawchiveArtist> Artists, string? Error)> SearchCreatorsAsync(string keyword)
    {
        keyword = (keyword ?? "").Trim();
        var list = new List<PawchiveArtist>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(PawchiveArtist? a)
        {
            if (a != null && a.Id.Length > 0 && seen.Add(a.Id))
                list.Add(a);
        }

        if (keyword.Length == 0)
        {
            var (supporting, error, _) = await GetBodyAsync($"{ApiBase}/plan.listSupporting");
            if (supporting is null)
                return (list, error);
            foreach (var plan in Items(supporting.Value))
                Add(ParseCreator(plan));
            var (following, _, _) = await GetBodyAsync($"{ApiBase}/creator.listFollowing");
            if (following is { } f)
                foreach (var c in Items(f))
                    Add(ParseCreator(c));
            return (list, null);
        }

        var creatorId = ParseCreatorInput(keyword) ?? (IsCreatorId(keyword) ? keyword : null);
        if (creatorId != null)
            Add(await GetCreatorAsync(creatorId));
        // 粘的是作家链接时不必再搜
        if (ParseCreatorInput(keyword) != null)
            return (list, list.Count == 0 ? "站点上没有这个作家" : null);

        var (found, searchError, _) = await GetBodyAsync(
            $"{ApiBase}/creator.search?q={Uri.EscapeDataString(keyword)}&page=0");
        if (found is { } body)
            foreach (var c in Items(body))
                Add(ParseCreator(c));
        return (list, list.Count == 0 ? searchError : null);
    }

    /// <summary>
    /// body 可能直接是数组，也可能把数组包一层（实测：作家搜索 { creators }、作品列表 { posts }、
    /// 分页表 { pageUrls }，旧版接口用 { items }），几种都认。
    /// </summary>
    private static IEnumerable<JsonElement> Items(JsonElement body)
    {
        if (body.ValueKind == JsonValueKind.Array)
            return body.EnumerateArray().ToList();
        foreach (var key in new[] { "items", "creators", "posts", "pageUrls" })
            if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty(key, out var arr) &&
                arr.ValueKind == JsonValueKind.Array)
                return arr.EnumerateArray().ToList();
        return [];
    }

    /// <summary>作家条目（creator.get / 赞助列表的档位 / 关注列表 / 搜索结果都是这一形状：creatorId + user）。</summary>
    private static PawchiveArtist? ParseCreator(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object)
            return null;
        var creatorId = DlsiteApi.JStr(e, "creatorId");
        if (creatorId.Length == 0)
            return null;
        var user = e.TryGetProperty("user", out var u) && u.ValueKind == JsonValueKind.Object ? u : default;
        return new PawchiveArtist
        {
            Id = creatorId,
            Service = PawchiveApi.FanboxService,
            Name = user.ValueKind == JsonValueKind.Object ? DlsiteApi.JStr(user, "name") : "",
            PublicId = creatorId,
            UserId = user.ValueKind == JsonValueKind.Object ? DlsiteApi.JStr(user, "userId") : "",
            Icon = user.ValueKind == JsonValueKind.Object ? DlsiteApi.JStr(user, "iconUrl") : "",
        };
    }

    // ---------- 作品 ----------

    // creatorId → (分页地址表, 取到的时间)。地址里带着「早于某时刻」的游标，翻页期间要保持同一份
    private static readonly Dictionary<string, (List<string> Urls, DateTime At)> PagesCache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan PagesTtl = TimeSpan.FromMinutes(10);

    private static async Task<(List<string>? Urls, string? Error)> GetPageUrlsAsync(string creatorId, bool refresh)
    {
        lock (PagesCache)
            if (!refresh && PagesCache.TryGetValue(creatorId, out var hit) && DateTime.Now - hit.At < PagesTtl)
                return (hit.Urls, null);
        var (body, error, _) = await GetBodyAsync(
            $"{ApiBase}/post.paginateCreator?creatorId={Uri.EscapeDataString(creatorId)}");
        if (body is not { } b)
            return (null, error ?? "获取作品列表失败");
        var urls = Items(b)
            .Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : "")
            .Where(x => x.StartsWith(ApiBase + "/", StringComparison.OrdinalIgnoreCase))
            .ToList();
        lock (PagesCache)
            PagesCache[creatorId] = (urls, DateTime.Now);
        return (urls, null);
    }

    /// <summary>站点每页的作品数（取自分页地址的 limit 参数）。</summary>
    private static int PageLimit(List<string> urls)
    {
        if (urls.Count == 0)
            return DefaultPageLimit;
        var m = Regex.Match(urls[0], @"[?&]limit=(\d+)");
        return m.Success && int.TryParse(m.Groups[1].Value, out var n) && n > 0 ? n : DefaultPageLimit;
    }

    /// <summary>
    /// 作家主页的一批作品（offset 为已加载的篇数）。站点一页只有 10 篇，这里一次连拼几页；
    /// 列表项只是摘要（没有正文与附件），<see cref="PawchivePost.Partial"/> 为 true，入队前要逐篇取详情。
    /// offset 为 0 时刷新分页地址表，免得看到的是十分钟前的旧列表。
    /// </summary>
    public static async Task<(List<PawchivePost>? Posts, bool HasMore, string? Error)> GetPostsAsync(
        string creatorId, int offset)
    {
        var (urls, error) = await GetPageUrlsAsync(creatorId, refresh: offset == 0);
        if (urls is null)
            return (null, false, error);
        var limit = PageLimit(urls);
        var start = offset / limit;
        var posts = new List<PawchivePost>();
        var end = Math.Min(urls.Count, start + PagesPerBatch);
        for (var i = start; i < end; i++)
        {
            var (body, pageError, _) = await GetBodyAsync(urls[i]);
            if (body is not { } b)
            {
                if (posts.Count == 0)
                    return (null, false, pageError);
                return (posts, true, null);   // 中途某页失败：先把拿到的给出去，下次从这里续
            }
            foreach (var item in Items(b))
                posts.Add(ParsePost(item, creatorId, summary: true));
        }
        return (posts, end < urls.Count, null);
    }

    /// <summary>取单篇详情；失败返回 (null, 错误)。未赞助到的档位能取到摘要，但 Locked=true、没有文件。</summary>
    public static async Task<(PawchivePost? Post, string? Error)> GetPostAsync(string postId)
    {
        if (postId.Length == 0 || !postId.All(char.IsAsciiDigit))
            return (null, "作品号无效");
        var (body, error, _) = await GetBodyAsync($"{ApiBase}/post.info?postId={postId}");
        if (body is not { } b || b.ValueKind != JsonValueKind.Object)
            return (null, error ?? "获取作品详情失败");
        // 现行接口把投稿包在 { post: {...} } 里，旧版直接就是投稿本身
        if (b.TryGetProperty("post", out var post) && post.ValueKind == JsonValueKind.Object)
            b = post;
        return (ParsePost(b, DlsiteApi.JStr(b, "creatorId"), summary: false), null);
    }

    /// <summary>
    /// 投稿条目 → 统一模型。summary=true 是列表摘要（不解析正文与附件）；
    /// 详情按投稿类型取文件：image（图集）/ file（附件）/ article（图文混排的块）/ text / video / entry。
    /// 图片落盘名用站上的图片号（FanboxService 会再统一改成三位序号），附件保留原名。
    /// </summary>
    private static PawchivePost ParsePost(JsonElement item, string creatorId, bool summary)
    {
        var id = DlsiteApi.JStr(item, "id");
        var restricted = DlsiteApi.JStr(item, "isRestricted") == "true";
        var fee = int.TryParse(DlsiteApi.JStr(item, "feeRequired"), out var f) ? f : 0;
        var tags = item.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Array
            ? t.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList()
            : [];
        // 封面：详情给 coverImageUrl，列表给 cover: { type, url }（未赞助的档位常为 null）
        var cover = DlsiteApi.JStr(item, "coverImageUrl");
        if (cover.Length == 0 && item.TryGetProperty("cover", out var coverObj) &&
            coverObj.ValueKind == JsonValueKind.Object)
            cover = DlsiteApi.JStr(coverObj, "url");

        var files = new List<PawchiveFile>();
        var text = new StringBuilder();
        var links = new List<string>();
        var hasBody = item.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.Object;
        if (!summary && hasBody)
            ParseBody(DlsiteApi.JStr(item, "type"), body, files, text, links);
        if (summary && text.Length == 0)
            text.Append(DlsiteApi.JStr(item, "excerpt"));

        // 没有封面图的投稿（图集帖常见）拿第一张图的预览顶上
        if (cover.Length == 0)
            cover = files.FirstOrDefault(x => PawchiveApi.IsImageName(x.Name))?.PreviewUrl ?? "";

        return new PawchivePost
        {
            Id = id,
            Service = PawchiveApi.FanboxService,
            ArtistId = creatorId.Length > 0 ? creatorId : DlsiteApi.JStr(item, "creatorId"),
            Title = DlsiteApi.JStr(item, "title"),
            Content = text.ToString().Trim(),
            Tags = tags,
            Published = DlsiteApi.JStr(item, "publishedDatetime"),
            CoverImage = cover,
            Files = files,
            Links = links,
            Partial = summary,
            // 详情接口对看不到的档位把 body 置 null；列表摘要则直接给 isRestricted
            Locked = restricted || (!summary && !hasBody),
            FeeRequired = fee,
        };
    }

    private static readonly Regex UrlPattern = new(@"https?://[^\s""'<>]+", RegexOptions.Compiled);

    private static void ParseBody(string type, JsonElement body, List<PawchiveFile> files,
        StringBuilder text, List<string> links)
    {
        var seenLinks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void AddText(string s)
        {
            if (s.Length == 0)
                return;
            text.AppendLine(s);
            foreach (Match m in UrlPattern.Matches(s))
                AddLink(m.Value);
        }
        void AddLink(string url)
        {
            url = url.TrimEnd('.', ',', '，', '。', '、', ')', '）', ']', '】', ';', '；', ':', '：');
            if (url.Length > 0 && seenLinks.Add(url))
                links.Add(url);
        }
        void AddImage(JsonElement img)
        {
            var url = DlsiteApi.JStr(img, "originalUrl");
            if (url.Length == 0)
                return;
            var ext = DlsiteApi.JStr(img, "extension");
            files.Add(new PawchiveFile
            {
                Name = DlsiteApi.JStr(img, "id") + "." + (ext.Length > 0 ? ext : "jpg"),
                Url = url,
                PreviewUrl = DlsiteApi.JStr(img, "thumbnailUrl"),
            });
        }
        void AddFile(JsonElement file)
        {
            var url = DlsiteApi.JStr(file, "url");
            if (url.Length == 0)
                return;
            var name = DlsiteApi.JStr(file, "name");
            var ext = DlsiteApi.JStr(file, "extension");
            files.Add(new PawchiveFile
            {
                Name = (name.Length > 0 ? name : DlsiteApi.JStr(file, "id")) + (ext.Length > 0 ? "." + ext : ""),
                Url = url,
            });
        }

        AddText(DlsiteApi.JStr(body, "text"));
        if (body.TryGetProperty("images", out var images) && images.ValueKind == JsonValueKind.Array)
            foreach (var img in images.EnumerateArray())
                AddImage(img);
        if (body.TryGetProperty("files", out var fileArr) && fileArr.ValueKind == JsonValueKind.Array)
            foreach (var file in fileArr.EnumerateArray())
                AddFile(file);
        if (body.TryGetProperty("video", out var video) && video.ValueKind == JsonValueKind.Object)
        {
            var provider = DlsiteApi.JStr(video, "serviceProvider");
            var vid = DlsiteApi.JStr(video, "videoId");
            if (provider == "youtube" && vid.Length > 0)
                AddLink($"https://www.youtube.com/watch?v={vid}");
            else if (provider == "vimeo" && vid.Length > 0)
                AddLink($"https://vimeo.com/{vid}");
        }
        if (type == "entry" && DlsiteApi.JStr(body, "html") is { Length: > 0 } html)
            foreach (Match m in UrlPattern.Matches(html))
                AddLink(m.Value);

        // article：正文是一串块，图片/附件按块里的 id 去 imageMap / fileMap 查；保持块的顺序（即图文顺序）
        if (body.TryGetProperty("blocks", out var blocks) && blocks.ValueKind == JsonValueKind.Array)
        {
            var imageMap = body.TryGetProperty("imageMap", out var im) ? im : default;
            var fileMap = body.TryGetProperty("fileMap", out var fm) ? fm : default;
            var urlEmbedMap = body.TryGetProperty("urlEmbedMap", out var um) ? um : default;
            var embedMap = body.TryGetProperty("embedMap", out var em) ? em : default;
            foreach (var block in blocks.EnumerateArray())
            {
                switch (DlsiteApi.JStr(block, "type"))
                {
                    case "p" or "header":
                        AddText(DlsiteApi.JStr(block, "text"));
                        if (block.TryGetProperty("links", out var bl) && bl.ValueKind == JsonValueKind.Array)
                            foreach (var l in bl.EnumerateArray())
                                AddLink(DlsiteApi.JStr(l, "url"));
                        break;
                    case "image" when Lookup(imageMap, DlsiteApi.JStr(block, "imageId")) is { } img:
                        AddImage(img);
                        break;
                    case "file" when Lookup(fileMap, DlsiteApi.JStr(block, "fileId")) is { } file:
                        AddFile(file);
                        break;
                    case "url_embed" when Lookup(urlEmbedMap, DlsiteApi.JStr(block, "urlEmbedId")) is { } ue:
                        AddLink(DlsiteApi.JStr(ue, "url"));
                        foreach (Match m in UrlPattern.Matches(DlsiteApi.JStr(ue, "html")))
                            AddLink(m.Value);
                        break;
                    case "embed" when Lookup(embedMap, DlsiteApi.JStr(block, "embedId")) is { } eb:
                        var provider = DlsiteApi.JStr(eb, "serviceProvider");
                        var cid = DlsiteApi.JStr(eb, "contentId");
                        if (provider == "youtube" && cid.Length > 0)
                            AddLink($"https://www.youtube.com/watch?v={cid}");
                        else if (provider == "google_forms" && cid.Length > 0)
                            AddLink($"https://docs.google.com/forms/d/e/{cid}/viewform");
                        break;
                }
            }
        }
    }

    private static JsonElement? Lookup(JsonElement map, string key) =>
        map.ValueKind == JsonValueKind.Object && key.Length > 0 && map.TryGetProperty(key, out var v) &&
        v.ValueKind == JsonValueKind.Object
            ? v
            : null;

    // ---------- 取图 ----------

    /// <summary>取官方站 / pximg 上的图片字节（预览、封面、头像）：带 cookie 与 fanbox 的 Referer。</summary>
    public static async Task<(byte[] Bytes, string ContentType)> GetImageAsync(string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyHeaders(request);
        using var resp = await Client().SendAsync(request);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadAsByteArrayAsync(), resp.Content.Headers.ContentType?.MediaType ?? "");
    }
}
