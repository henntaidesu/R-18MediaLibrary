using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using R18MediaLibrary.Core;

namespace R18MediaLibrary.Services;

/// <summary>
/// fantia.jp 客户端。作品搜索里与 FANBOX 并列：「Fantia」（匿名）与每个已登录账号各一项「Fantia（账号名）」，
/// 面板、卡片、详情、下载流程全部复用 FANBOX 那一套（经 <see cref="FanboxSource"/> 分流），
/// 数据对齐成同一套模型（<see cref="PawchiveArtist"/> = 粉丝团，<see cref="PawchivePost"/> = 投稿）。
/// pawchive 不收 Fantia（作家表里只有 fanbox / patreon），所以没有匿名归档这条路，只走官方站。
///
/// 实测的站点接口：
///   /api/v1/fanclubs/{id}       → 粉丝团信息（匿名可取）
///   /api/v1/posts/{id}          → 单篇详情（匿名可取公开内容；<b>必须带 X-CSRF-Token</b>，缺了回 422）
///   /api/v1/me                  → 当前登录用户（匿名 401，登录探针）
///   /api/v1/me/fanclubs         → 我关注的粉丝团号
///   /fanclubs/{id}/posts?page=N → 投稿列表（只有 HTML，每页 20 篇；匿名看不到标题，故列表项逐篇再取详情）
///   /fanclubs?keyword=          → 粉丝团搜索（只有 HTML，且匿名会被 302 到登录页）
/// CSRF 令牌取自任一页面的 &lt;meta name="csrf-token"&gt;，与会话 cookie（_session_id）绑定，
/// 故每个账号（含匿名）一个带 cookie 罐的客户端，令牌跟着客户端走。
///
/// <b>图片直链是 CloudFront 签名地址、几小时就过期</b>（Policy 里的 DateLessThan），附件下载地址
/// （/posts/{id}/download/{content}）则要登录并 302 到签名地址。所以与 E-Hentai 同构：
/// download_list.url 存不过期的站内地址（图片 = /posts/{投稿}/post_content_photo/{图片号}），
/// 下载线程临下载前经 <see cref="ResolveDownload"/> 现换签名直链。
/// </summary>
public static class FantiaApi
{
    /// <summary>works.source / download_list.source / 账号表 site 的取值。</summary>
    public const string SourceName = "fantia";

    public const string Root = "https://fantia.jp";

    /// <summary>下载地址解析失败的错误码（download_list.error）：没登录 / 档位不够看不到 / 连不上站点。</summary>
    public const string LoginError = "fantiaLogin";
    public const string LockedError = "fantiaLocked";
    public const string FetchError = "fantiaFetch";

    /// <summary>站点投稿列表每页的篇数。</summary>
    public const int PageSize = 20;

    /// <summary>当前上下文的 Fantia 账号（<see cref="SiteAccounts.Active"/>）。</summary>
    public static bool HasCookie => SiteAccounts.Active(AccountService.Fantia) != null;

    public static string CookieHeader => SiteAccounts.Active(AccountService.Fantia)?.Cookie ?? "";

    public static string FanclubPageUrl(string fanclubId) => $"{Root}/fanclubs/{fanclubId}";

    public static string PostPageUrl(string postId) => $"{Root}/posts/{postId}";

    /// <summary>是否 fantia.jp 及其子域（c. / cc. 图床）的地址。</summary>
    public static bool IsFantiaUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
        (uri.Host.Equals("fantia.jp", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.EndsWith(".fantia.jp", StringComparison.OrdinalIgnoreCase));

    /// <summary>粉丝团号 / 投稿号都是纯数字；用作 URL 参数前先校验。</summary>
    public static bool IsId(string id) => id.Length is > 0 and <= 20 && id.All(char.IsAsciiDigit);

    // ---------- HTTP ----------

    /// <summary>一个账号（或匿名）的会话：带 cookie 罐的客户端 + 与该会话绑定的 CSRF 令牌。</summary>
    private sealed class Session
    {
        public required HttpClient Client { get; init; }
        public string Csrf = "";
        public DateTime CsrfAt;
    }

    private static readonly object SessionLock = new();
    private static readonly Dictionary<string, Session> Sessions = [];
    private static readonly TimeSpan CsrfTtl = TimeSpan.FromMinutes(30);

    private static string ProxyKey()
    {
        var (proxyOn, proxy) = AppConfig.ReadProxy();
        return proxyOn ? proxy.Address?.ToString() ?? "" : "";
    }

    /// <summary>
    /// 当前上下文账号的会话。同一个 cookie 共用一个客户端（详情页逐张取预览、列表逐篇取详情都要复用连接）；
    /// 不同账号必须分开——罐子里会存下站点轮换的会话 cookie，共用会把 A 的会话带到 B 的请求上。
    /// </summary>
    private static Session Current()
    {
        var cookie = CookieHeader;
        var key = ProxyKey() + "|" + cookie;
        lock (SessionLock)
        {
            if (Sessions.TryGetValue(key, out var hit))
                return hit;
            var session = new Session { Client = Http.CreateClient(TimeSpan.FromSeconds(30), cookies: Jar(cookie)) };
            Sessions[key] = session;
            return session;
        }
    }

    /// <summary>把 Cookie 请求头装进一个只对 fantia.jp 生效的 cookie 罐（值里带逗号等非法字符的个别 cookie 跳过）。</summary>
    private static CookieContainer Jar(string cookieHeader)
    {
        var jar = new CookieContainer();
        foreach (var part in cookieHeader.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0)
                continue;
            try
            {
                jar.Add(new Cookie(part[..eq], part[(eq + 1)..], "/", "fantia.jp"));
            }
            catch (CookieException)
            {
                // 统计类 cookie 的值偶尔带逗号，与登录无关，丢掉即可
            }
        }
        return jar;
    }

    /// <summary>cookie / 代理变更、账号增删后调用：丢掉全部会话与按账号缓存的数据。</summary>
    public static void Invalidate()
    {
        lock (SessionLock)
        {
            foreach (var s in Sessions.Values)
                s.Client.Dispose();
            Sessions.Clear();
        }
        lock (PostCache)
            PostCache.Clear();
        lock (FanclubCache)
            FanclubCache.Clear();
    }

    private static readonly Regex CsrfMeta = new(@"<meta\s+name=""csrf-token""\s+content=""([^""]+)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>取（必要时刷新）会话的 CSRF 令牌；拿不到返回空串。</summary>
    private static async Task<string> CsrfAsync(Session s, bool refresh = false)
    {
        if (!refresh && s.Csrf.Length > 0 && DateTime.Now - s.CsrfAt < CsrfTtl)
            return s.Csrf;
        var (html, _, _) = await GetTextAsync(s, Root + "/", json: false);
        var m = html is null ? Match.Empty : CsrfMeta.Match(html);
        s.Csrf = m.Success ? WebUtility.HtmlDecode(m.Groups[1].Value) : "";
        s.CsrfAt = DateTime.Now;
        return s.Csrf;
    }

    /// <summary>GET 一个地址，返回 (正文, 错误文案, HTTP 状态码)；成功时错误为 null。</summary>
    private static async Task<(string? Text, string? Error, int Status)> GetTextAsync(
        Session s, string url, bool json, string csrf = "")
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Referrer = new Uri(Root + "/");
            if (json)
            {
                request.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
                request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
                if (csrf.Length > 0)
                    request.Headers.TryAddWithoutValidation("X-CSRF-Token", csrf);
            }
            using var resp = await s.Client.SendAsync(request);
            var status = (int)resp.StatusCode;
            var text = await resp.Content.ReadAsStringAsync();
            if (resp.IsSuccessStatusCode)
                return (text, null, status);
            Logger.Error($"fantia 请求失败 HTTP {status}: {url}");
            return (null, status switch
            {
                401 => "Fantia 未登录或登录已失效（请到「账号访问」登录 Fantia 账号）",
                403 => "站点拒绝了请求（HTTP 403）：多半是该内容需要登录或年龄确认",
                404 => "站点上没有这个粉丝团或投稿",
                422 => "站点拒绝了请求（CSRF 校验失败）",
                429 => "请求过于频繁，被站点限流了，稍后再试",
                _ => $"站点返回 HTTP {status}",
            }, status);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            Logger.Error($"fantia 网络请求失败: {e.Message} ({url})");
            return (null, "连不上 fantia.jp：检查网络与代理设置", 0);
        }
    }

    /// <summary>调一个 JSON 接口；CSRF 令牌失效（422）时刷新一次重试。</summary>
    private static async Task<(JsonElement? Root, string? Error, int Status)> GetJsonAsync(string path)
    {
        var s = Current();
        var url = path.StartsWith("http", StringComparison.Ordinal) ? path : Root + path;
        var (text, error, status) = await GetTextAsync(s, url, json: true, await CsrfAsync(s));
        if (status == 422)
            (text, error, status) = await GetTextAsync(s, url, json: true, await CsrfAsync(s, refresh: true));
        if (text is null)
            return (null, error, status);
        try
        {
            using var doc = JsonDocument.Parse(text);
            return (doc.RootElement.Clone(), null, status);
        }
        catch (JsonException)
        {
            Logger.Error($"fantia 返回的不是 JSON: {url}");
            return (null, "站点返回的数据无法解析", status);
        }
    }

    // ---------- 登录态 ----------

    /// <summary>
    /// 检测当前账号的登录：/api/v1/me 匿名一律 401，登录后返回用户信息。
    /// 返回 (是否有效, 用户号, 显示名, 提示)。
    /// </summary>
    public static async Task<(bool Ok, string UserId, string Name, string Message)> CheckLoginAsync()
    {
        if (!HasCookie)
            return (false, "", "", "未登录");
        var (root, error, status) = await GetJsonAsync("/api/v1/me");
        if (root is not { } r)
            return (false, "", "", status == 401 ? "登录已失效（请在「账号访问」重新登录）" : error ?? "检测失败");
        var (uid, name) = ParseMe(r);
        return uid.Length == 0
            ? (false, "", "", "登录已失效（请在「账号访问」重新登录）")
            : (true, uid, name, "登录有效");
    }

    /// <summary>
    /// 登录浏览器用：拿浏览器手上的 cookie 问一次 /api/v1/me。
    /// 返回 (站点是否给了明确答复, 用户号, 显示名)：登上了用户号非空；站点明确说「匿名」时 Definite=true、用户号为空；
    /// 网络不通等没得到答复时 Definite=false（调用方下一秒再问）。
    /// 用一次性的客户端，不进会话缓存（登录过程中会话值会变好几次）。
    /// </summary>
    public static async Task<(bool Definite, string UserId, string Name)> ProbeSessionAsync(string cookieHeader)
    {
        using var client = Http.CreateClient(TimeSpan.FromSeconds(20), cookies: Jar(cookieHeader));
        var s = new Session { Client = client };
        var (text, _, status) = await GetTextAsync(s, Root + "/api/v1/me", json: true, await CsrfAsync(s));
        if (text is null)
            return (status is 401 or 403, "", "");
        try
        {
            using var doc = JsonDocument.Parse(text);
            var (uid, name) = ParseMe(doc.RootElement);
            return (true, uid, name);
        }
        catch (JsonException)
        {
            return (false, "", "");
        }
    }

    /// <summary>/api/v1/me 的用户号与名字：可能直接在根上，也可能包一层 user / me。</summary>
    private static (string UserId, string Name) ParseMe(JsonElement root)
    {
        foreach (var e in new[] { root, Child(root, "user"), Child(root, "me"), Child(Child(root, "me"), "user") })
        {
            var id = DlsiteApi.JStr(e, "id");
            if (id.Length > 0 && id.All(char.IsAsciiDigit))
                return (id, DlsiteApi.JStr(e, "name"));
        }
        return ("", "");
    }

    private static JsonElement Child(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : default;

    // ---------- 粉丝团 ----------

    private static readonly Dictionary<string, PawchiveArtist> FanclubCache = [];

    /// <summary>
    /// 把用户输入认成粉丝团号：粉丝团链接（fantia.jp/fanclubs/123）或直接就是数字。
    /// 认不出返回 null（当作搜索关键字）。投稿链接由 <see cref="SearchFanclubsAsync"/> 另行处理。
    /// </summary>
    public static string? ParseFanclubInput(string raw)
    {
        raw = (raw ?? "").Trim();
        var m = Regex.Match(raw, @"fantia\.jp/fanclubs/(\d+)", RegexOptions.IgnoreCase);
        if (m.Success)
            return m.Groups[1].Value;
        return IsId(raw) ? raw : null;
    }

    private static string? ParsePostInput(string raw)
    {
        var m = Regex.Match(raw ?? "", @"fantia\.jp/posts/(\d+)", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>取粉丝团信息；失败返回 null。</summary>
    public static async Task<PawchiveArtist?> GetFanclubAsync(string fanclubId)
    {
        if (!IsId(fanclubId))
            return null;
        lock (FanclubCache)
            if (FanclubCache.TryGetValue(fanclubId, out var hit))
                return hit;
        var (root, _, _) = await GetJsonAsync($"/api/v1/fanclubs/{fanclubId}");
        if (root is not { } r || ParseFanclub(Child(r, "fanclub")) is not { } artist)
            return null;
        lock (FanclubCache)
            FanclubCache[fanclubId] = artist;
        return artist;
    }

    /// <summary>粉丝团条目（/api/v1/fanclubs/{id} 的 fanclub，投稿详情里的 fanclub 同形）。</summary>
    private static PawchiveArtist? ParseFanclub(JsonElement fc)
    {
        var id = DlsiteApi.JStr(fc, "id");
        if (id.Length == 0)
            return null;
        var name = DlsiteApi.JStr(fc, "creator_name");
        if (name.Length == 0)
            name = DlsiteApi.JStr(fc, "fanclub_name_or_creator_name");
        if (name.Length == 0)
            name = DlsiteApi.JStr(Child(fc, "user"), "name");
        var icon = Child(fc, "icon");
        return new PawchiveArtist
        {
            Id = id,
            Service = SourceName,
            Name = name,
            Favorited = int.TryParse(DlsiteApi.JStr(fc, "fan_count"), out var fans) ? fans : 0,
            Icon = FirstOf(icon, "main", "thumb", "original"),
        };
    }

    /// <summary>
    /// 搜粉丝团。粘的是粉丝团 / 投稿链接或粉丝团号时直接定位（匿名也行）；
    /// 关键字为空时列出「我关注的」粉丝团；其余走站点的粉丝团搜索——这两样站点都只对登录用户开放。
    /// </summary>
    public static async Task<(List<PawchiveArtist> Artists, string? Error)> SearchFanclubsAsync(string keyword)
    {
        keyword = (keyword ?? "").Trim();
        var list = new List<PawchiveArtist>();

        if (ParseFanclubInput(keyword) is { } direct)
        {
            if (await GetFanclubAsync(direct) is { } a)
                list.Add(a);
            return (list, list.Count == 0 ? "站点上没有这个粉丝团" : null);
        }
        if (ParsePostInput(keyword) is { } postId)
        {
            var (post, error) = await GetPostAsync(postId);
            if (post != null && await GetFanclubAsync(post.ArtistId) is { } a)
                list.Add(a);
            return (list, list.Count == 0 ? error ?? "站点上没有这篇投稿" : null);
        }

        if (!HasCookie)
            return (list, keyword.Length == 0
                ? "匿名访问时请输入作者主页链接 / 粉丝团号；登录 Fantia 账号后留空查询即列出关注的作者"
                : "Fantia 的作者搜索只对登录用户开放：请在「账号访问」登录 Fantia 账号，或直接粘贴作者主页链接 / 粉丝团号");

        List<string> ids;
        if (keyword.Length == 0)
        {
            var (root, error, _) = await GetJsonAsync("/api/v1/me/fanclubs");
            if (root is not { } r)
                return (list, error);
            ids = Child(r, "fanclub_ids") is { ValueKind: JsonValueKind.Array } arr
                ? arr.EnumerateArray().Select(x => x.GetRawText().Trim('"')).Where(IsId).ToList()
                : [];
        }
        else
        {
            var (html, error, _) = await GetTextAsync(Current(),
                $"{Root}/fanclubs?keyword={Uri.EscapeDataString(keyword)}", json: false);
            if (html is null)
                return (list, error);
            ids = Regex.Matches(html, @"href=""/fanclubs/(\d+)""").Select(m => m.Groups[1].Value)
                .Distinct().ToList();
        }
        foreach (var a in await MapLimitedAsync(ids.Take(60).ToList(), GetFanclubAsync))
            if (a != null)
                list.Add(a);
        return (list, list.Count == 0 && keyword.Length == 0 ? "还没有关注任何粉丝团" : null);
    }

    /// <summary>并发取一批（最多 4 路，结果保持原顺序）：站点对突发请求会限流。</summary>
    private static async Task<List<T?>> MapLimitedAsync<T>(List<string> ids, Func<string, Task<T?>> fetch)
        where T : class
    {
        using var gate = new SemaphoreSlim(4);
        var tasks = ids.Select(async id =>
        {
            await gate.WaitAsync();
            try { return await fetch(id); }
            finally { gate.Release(); }
        }).ToList();
        return (await Task.WhenAll(tasks)).ToList();
    }

    // ---------- 投稿 ----------

    private static readonly Regex PostLink = new(@"class=""link-block""\s+href=""/posts/(\d+)""", RegexOptions.Compiled);

    /// <summary>
    /// 粉丝团的一页投稿（offset 为已加载篇数，按站点每页 20 篇换算页号）。
    /// 列表页只有 HTML、匿名还看不到标题，所以只从中取投稿号，再逐篇取详情（并发 4 路）——
    /// 拿到的都是完整投稿（文件清单、能否查看都齐了），卡片能直接标出锁定与文件数，入队也不必再取。
    /// </summary>
    public static async Task<(List<PawchivePost>? Posts, bool HasMore, string? Error)> GetPostsAsync(
        string fanclubId, int offset)
    {
        if (!IsId(fanclubId))
            return (null, false, "粉丝团号无效");
        var page = offset / PageSize + 1;
        var (html, error, _) = await GetTextAsync(Current(),
            $"{Root}/fanclubs/{fanclubId}/posts?page={page}", json: false);
        if (html is null)
            return (null, false, error ?? "获取投稿列表失败");
        var ids = PostLink.Matches(html).Select(m => m.Groups[1].Value).Distinct().ToList();
        var hasMore = ids.Count > 0 && html.Contains("rel=\"next\"", StringComparison.Ordinal);
        var posts = (await MapLimitedAsync(ids, async id => (await GetPostAsync(id)).Post))
            .Select((p, i) => p ?? new PawchivePost { Id = ids[i], Service = SourceName, ArtistId = fanclubId, Partial = true })
            .ToList();
        return (posts, hasMore, null);
    }

    // 投稿号 → (原始 JSON, 取到的时间)。列表、详情、入队、下载线程现解析签名直链都要它，十分钟内复用
    private static readonly Dictionary<string, (JsonElement Post, DateTime At)> PostCache = [];
    private static readonly TimeSpan PostTtl = TimeSpan.FromMinutes(10);

    private static async Task<(JsonElement? Post, string? Error)> GetPostJsonAsync(string postId, bool refresh = false)
    {
        var key = CookieHeader + "|" + postId;
        lock (PostCache)
            if (!refresh && PostCache.TryGetValue(key, out var hit) && DateTime.Now - hit.At < PostTtl)
                return (hit.Post, null);
        var (root, error, _) = await GetJsonAsync($"/api/v1/posts/{postId}");
        if (root is not { } r || Child(r, "post") is not { ValueKind: JsonValueKind.Object } post)
            return (null, error ?? "获取投稿详情失败");
        lock (PostCache)
        {
            if (PostCache.Count > 2000)
                PostCache.Clear();
            PostCache[key] = (post, DateTime.Now);
        }
        return (post, null);
    }

    /// <summary>取单篇详情；失败返回 (null, 错误)。当前账号看不到的内容（未加入对应档位）不计入文件。</summary>
    public static async Task<(PawchivePost? Post, string? Error)> GetPostAsync(string postId)
    {
        if (!IsId(postId))
            return (null, "投稿号无效");
        var (post, error) = await GetPostJsonAsync(postId);
        return post is { } p ? (ParsePost(p), null) : (null, error);
    }

    /// <summary>
    /// 投稿 → 统一模型。一篇投稿由若干「内容」（post_contents）组成，每块各有可见档位：
    ///   photo_gallery → 图集（每张图一个文件）；file → 附件；blog → 图文混排（Quill delta，图片在 fantiaImage 里）；
    ///   text → 纯文字；embed → 外链；product → 商品（不下）。
    /// visible_status 不是 visible 的块当前账号看不到（没有文件），只记下解锁所需的最低金额。
    /// </summary>
    private static PawchivePost ParsePost(JsonElement p)
    {
        var postId = DlsiteApi.JStr(p, "id");
        var fanclub = Child(p, "fanclub");
        var files = new List<PawchiveFile>();
        var seenUrls = new HashSet<string>(StringComparer.Ordinal);
        var text = new StringBuilder();
        var links = new List<string>();
        var seenLinks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddLinksIn(string s)
        {
            foreach (Match m in UrlPattern.Matches(s))
            {
                var url = m.Value.TrimEnd('.', ',', '，', '。', '、', ')', '）', ']', '】', ';', '；', ':', '：');
                if (url.Length > 0 && seenLinks.Add(url))
                    links.Add(url);
            }
        }
        void AddText(string s)
        {
            s = (s ?? "").Replace("\r\n", "\n").Trim();
            if (s.Length == 0)
                return;
            text.AppendLine(s);
            AddLinksIn(s);
        }
        void AddFile(string name, string url, string preview = "")
        {
            if (url.Length > 0 && seenUrls.Add(url))
                files.Add(new PawchiveFile { Name = name, Url = url, PreviewUrl = preview });
        }

        AddText(DlsiteApi.JStr(p, "comment"));
        var hidden = 0;
        var fee = 0;
        var contents = Child(p, "post_contents");
        foreach (var c in contents.ValueKind == JsonValueKind.Array ? contents.EnumerateArray().ToList() : [])
        {
            if (DlsiteApi.JStr(c, "visible_status") != "visible")
            {
                hidden++;
                var price = int.TryParse(DlsiteApi.JStr(Child(c, "plan"), "price"), out var pr) ? pr : 0;
                if (price > 0 && (fee == 0 || price < fee))
                    fee = price;
                continue;
            }
            var title = DlsiteApi.JStr(c, "title");
            if (title.Length > 0)
                AddText($"【{title}】");
            var category = DlsiteApi.JStr(c, "category");
            switch (category)
            {
                case "photo_gallery":
                    AddText(DlsiteApi.JStr(c, "comment"));
                    if (Child(c, "post_content_photos") is { ValueKind: JsonValueKind.Array } photos)
                        foreach (var ph in photos.EnumerateArray())
                        {
                            var urls = Child(ph, "url");
                            var original = DlsiteApi.JStr(urls, "original");
                            var photoId = DlsiteApi.JStr(ph, "id");
                            if (original.Length == 0 || photoId.Length == 0)
                                continue;
                            var page = DlsiteApi.JStr(ph, "show_original_uri");
                            if (page.Length == 0)
                                page = $"/posts/{postId}/post_content_photo/{photoId}";
                            AddFile(photoId + ExtensionOf(original), Absolute(page),
                                FirstOf(urls, "main", "large", "medium", "thumb"));
                        }
                    break;
                case "file":
                    AddText(DlsiteApi.JStr(c, "comment"));
                    var uri = DlsiteApi.JStr(c, "download_uri");
                    var filename = DlsiteApi.JStr(c, "filename");
                    if (uri.Length > 0)
                        AddFile(filename.Length > 0 ? filename : DlsiteApi.JStr(c, "id"), Absolute(uri));
                    break;
                case "blog":
                    ParseBlog(DlsiteApi.JStr(c, "comment"), AddText, AddFile);
                    break;
                case "embed":
                    AddText(DlsiteApi.JStr(c, "comment"));
                    AddLinksIn(DlsiteApi.JStr(c, "embed_url"));
                    break;
                default:
                    AddText(DlsiteApi.JStr(c, "comment"));
                    break;
            }
        }

        // 投稿封面（thumb）在公开图床 c.fantia.jp 上、不签名。同 pawchive 的 file 字段：它是作者另设的封面图，
        // 内容里已有图片时不下（免得 001 是一张裁出来的横幅），一张图都没有（纯附件帖）时才补进清单当封面
        var thumb = Child(p, "thumb");
        var cover = FirstOf(thumb, "main", "medium", "large", "original");
        var thumbOriginal = FirstOf(thumb, "original", "main");
        if (thumbOriginal.Length > 0 && files.Count > 0 && !files.Any(f => PawchiveApi.IsImageName(f.Name)))
            AddFile("thumb" + ExtensionOf(thumbOriginal), thumbOriginal);
        if (cover.Length == 0)
            cover = files.FirstOrDefault(f => PawchiveApi.IsImageName(f.Name))?.PreviewUrl ?? "";

        var tags = Child(p, "tags") is { ValueKind: JsonValueKind.Array } t
            ? t.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : DlsiteApi.JStr(x, "name"))
                .Where(x => x.Length > 0).ToList()
            : [];

        return new PawchivePost
        {
            Id = postId,
            Service = SourceName,
            ArtistId = DlsiteApi.JStr(fanclub, "id"),
            Title = DlsiteApi.JStr(p, "title"),
            Content = text.ToString().Trim(),
            Tags = tags,
            Published = IsoDate(DlsiteApi.JStr(p, "posted_at")),
            CoverImage = cover,
            Files = files,
            Links = links,
            // 有内容块却一块都看不到 → 当前账号下不了这篇（卡片置灰、标出解锁金额）
            Locked = hidden > 0 && files.Count == 0,
            FeeRequired = fee,
        };
    }

    /// <summary>
    /// blog 块的正文是 Quill delta 的 JSON 串：{"ops":[{"insert":"文字"}, {"insert":{"fantiaImage":{…}}}]}。
    /// 图片的 original_url 是站内地址（登录后 302 到签名直链），与附件下载地址同样现解析。
    /// </summary>
    private static void ParseBlog(string json, Action<string> addText, Action<string, string, string> addFile)
    {
        if (json.Length == 0)
            return;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var sb = new StringBuilder();
            if (Child(doc.RootElement, "ops") is not { ValueKind: JsonValueKind.Array } ops)
                return;
            foreach (var op in ops.EnumerateArray())
            {
                var insert = Child(op, "insert");
                if (insert.ValueKind == JsonValueKind.String)
                {
                    sb.Append(insert.GetString());
                    continue;
                }
                var img = Child(insert, "fantiaImage");
                var original = DlsiteApi.JStr(img, "original_url");
                if (original.Length == 0)
                    continue;
                var preview = DlsiteApi.JStr(img, "url");
                var id = DlsiteApi.JStr(img, "id");
                addFile((id.Length > 0 ? id : "blog") + ExtensionOf(preview.Length > 0 ? preview : original),
                    Absolute(original), preview.Length > 0 ? Absolute(preview) : "");
            }
            addText(sb.ToString());
        }
        catch (JsonException)
        {
            addText(json);   // 不是 delta：当纯文本收下
        }
    }

    private static readonly Regex UrlPattern = new(@"https?://[^\s""'<>]+", RegexOptions.Compiled);

    private static string Absolute(string url) => url.StartsWith('/') ? Root + url : url;

    private static string FirstOf(JsonElement e, params string[] keys)
    {
        foreach (var k in keys)
            if (DlsiteApi.JStr(e, k) is { Length: > 0 } v)
                return v;
        return "";
    }

    /// <summary>地址路径上的扩展名（含点，小写）；认不出按 .jpg。</summary>
    private static string ExtensionOf(string url)
    {
        var path = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.AbsolutePath : url.Split('?')[0];
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        return ext.Length is > 1 and <= 6 ? ext : ".jpg";
    }

    /// <summary>站点的 posted_at（RFC 1123 风格：Fri, 20 Dec 2019 14:55:48 +0900）→ ISO（2019-12-20T14:55:48）。</summary>
    private static string IsoDate(string raw)
    {
        var s = Regex.Replace(raw ?? "", @"([+-]\d{2})(\d{2})$", "$1:$2");
        return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)
            : raw ?? "";
    }

    // ---------- 下载：现解析签名直链 ----------

    private static readonly Regex PhotoPage = new(@"^https://fantia\.jp/posts/(\d+)/post_content_photo/(\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// 下载线程用（同步）：把队列里的站内地址换成可直接下载的签名直链。
    /// 返回 (直链, 错误码)；公开图床（c.fantia.jp 上的投稿封面）原样返回。
    /// </summary>
    public static (string Url, string? Error) ResolveDownload(string url) =>
        ResolveDownloadAsync(url).GetAwaiter().GetResult();

    private static async Task<(string Url, string? Error)> ResolveDownloadAsync(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            !uri.Host.Equals("fantia.jp", StringComparison.OrdinalIgnoreCase))
            return (url, null);

        // 图集里的图：签名直链在投稿详情里，按图片号找（详情十分钟内复用，一篇几十张只取一次）
        // 签名的有效期按刻钟取整（实测 Policy 里的 EpochTime 落在整 15 分钟上），缓存里的可能已过期，过期就重取一次
        if (PhotoPage.Match(url) is { Success: true } m)
        {
            var found = false;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var (post, _) = await GetPostJsonAsync(m.Groups[1].Value, refresh: attempt > 0);
                if (post is not { } p)
                    return ("", FetchError);
                var direct = FindPhotoOriginal(p, m.Groups[2].Value);
                found |= !string.IsNullOrEmpty(direct);
                if (!string.IsNullOrEmpty(direct) && !IsExpired(direct))
                    return (direct, null);
            }
            // 详情里没有这张图：多半是账号档位不够（或作者撤了这张）；有图但签名过期，下一轮再取
            return ("", found ? FetchError : LockedError);
        }

        // 附件 / blog 里的图：站内地址，登录后 302 到签名直链。不自动跟随，自己读 Location
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Referrer = new Uri(Root + "/");
            using var resp = await SendNoRedirectAsync(request);
            var status = (int)resp.StatusCode;
            if (status is >= 300 and < 400 && resp.Headers.Location is { } loc)
            {
                var target = loc.IsAbsoluteUri ? loc.ToString() : new Uri(uri, loc).ToString();
                // 跳去登录页 = 没登录或会话失效
                if (target.Contains("/sessions/signin", StringComparison.OrdinalIgnoreCase) ||
                    target.Contains("/recaptcha", StringComparison.OrdinalIgnoreCase))
                    return ("", LoginError);
                return (target, null);
            }
            if (status == 200)
                return (url, null);   // 直接给了文件本体：照原地址下（下载线程会再请求一次）
            Logger.Error($"fantia 解析下载地址失败 HTTP {status}: {url}");
            return ("", status is 403 or 404 ? LockedError : FetchError);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            Logger.Error($"fantia 解析下载地址失败: {e.Message} ({url})");
            return ("", FetchError);
        }
    }

    /// <summary>
    /// 不跟随跳转地发一个请求（带当前账号的 cookie）。会话客户端开着自动跳转，这里单建一个不跳的 handler，
    /// cookie 手写（只发给 fantia.jp 自己）。
    /// </summary>
    private static async Task<HttpResponseMessage> SendNoRedirectAsync(HttpRequestMessage request)
    {
        var (enabled, proxy) = AppConfig.ReadProxy();
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All,
        };
        if (enabled)
        {
            handler.Proxy = proxy;
            handler.UseProxy = true;
        }
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(Http.UserAgent);
        if (CookieHeader.Length > 0)
            request.Headers.TryAddWithoutValidation("Cookie", CookieHeader);
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
    }

    private static string? FindPhotoOriginal(JsonElement post, string photoId)
    {
        if (Child(post, "post_contents") is not { ValueKind: JsonValueKind.Array } contents)
            return null;
        foreach (var c in contents.EnumerateArray())
            if (Child(c, "post_content_photos") is { ValueKind: JsonValueKind.Array } photos)
                foreach (var ph in photos.EnumerateArray())
                    if (DlsiteApi.JStr(ph, "id") == photoId)
                        return DlsiteApi.JStr(Child(ph, "url"), "original");
        return null;
    }

    /// <summary>签名直链是否已过期（或一分钟内过期）：Policy 是 base64 的 JSON，里面有 AWS:EpochTime。</summary>
    private static bool IsExpired(string signedUrl)
    {
        var m = Regex.Match(signedUrl, @"[?&]Policy=([^&]+)");
        if (!m.Success)
            return false;
        try
        {
            var b64 = m.Groups[1].Value.Replace('-', '+').Replace('_', '=').Replace('~', '/');
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
            var epoch = Regex.Match(json, @"""AWS:EpochTime""\s*:\s*(\d+)");
            return epoch.Success &&
                   DateTimeOffset.FromUnixTimeSeconds(long.Parse(epoch.Groups[1].Value)) < DateTimeOffset.UtcNow.AddMinutes(1);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    // ---------- 取图 ----------

    /// <summary>取站上图片（粉丝团头像、投稿封面、详情预览）。图床地址已签名或公开，不带 cookie。</summary>
    public static async Task<(byte[] Bytes, string ContentType)> GetImageAsync(string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Referrer = new Uri(Root + "/");
        using var resp = await Current().Client.SendAsync(request);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadAsByteArrayAsync(), resp.Content.Headers.ContentType?.MediaType ?? "");
    }
}
