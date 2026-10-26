using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using R18MediaLibrary.Core;

namespace R18MediaLibrary.Services;

/// <summary>一件 pixiv 作品（插画 / 漫画 / 动图）的元数据。搜索结果与作品详情共用此类型。</summary>
public class PixivArtwork
{
    /// <summary>作品号（站点的 illust id，纯数字串）。</summary>
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    /// <summary>封面图：搜索结果给 250x250 缩略图，详情给 1200px 的 regular。</summary>
    public string Thumb { get; init; } = "";
    /// <summary>作者号（pixiv 用户 id），入库后即 works.maker_id。</summary>
    public string UserId { get; init; } = "";
    public string UserName { get; init; } = "";
    /// <summary>张数（漫画/多图投稿 > 1；动图恒为 1，动画本体另在 ugoira 的 zip 里）。</summary>
    public int PageCount { get; init; }
    /// <summary>作品形式：0=插画 1=漫画 2=动图(ugoira)。</summary>
    public int IllustType { get; init; }
    /// <summary>分级：0=全年龄 1=R-18 2=R-18G。未登录时站点只返回 0。</summary>
    public int XRestrict { get; init; }
    /// <summary>AI 生成标记（站点的 aiType，2 = AI 生成）。</summary>
    public int AiType { get; init; }
    /// <summary>投稿时间（ISO 8601 带时区，如 2022-07-25T17:35:00+00:00）。</summary>
    public string CreateDate { get; init; } = "";
    public List<string> Tags { get; init; } = [];
    public int Width { get; init; }
    public int Height { get; init; }
    /// <summary>作者写的说明（详情才有；站点存的是 HTML，这里已转成纯文本）。</summary>
    public string Description { get; init; } = "";
    public int BookmarkCount { get; init; }
    public int LikeCount { get; init; }
    public int ViewCount { get; init; }

    /// <summary>动图（ugoira）：本体是一个装着逐帧图的 zip，要另走 ugoira_meta 取。</summary>
    public bool IsUgoira => IllustType == 2;

    public string TypeName => IllustType switch { 1 => "漫画", 2 => "动图", _ => "插画" };

    /// <summary>分级标签；全年龄返回空串（卡片上不画角标）。</summary>
    public string RestrictName => XRestrict switch { 1 => "R-18", 2 => "R-18G", _ => "" };

    public bool IsAi => AiType == 2;

    public string PageUrl => PixivApi.ArtworkUrl(Id);

    /// <summary>投稿时间（本地时区）；解析不出返回 null。</summary>
    public DateTime? Created =>
        DateTimeOffset.TryParse(CreateDate, out var t) ? t.LocalDateTime : null;
}

/// <summary>一页搜索结果。站点按页号翻页（每页 60 件），与 E-Hentai 的游标翻页不同。</summary>
public class PixivSearchResult
{
    public List<PixivArtwork> Items { get; init; } = [];
    /// <summary>命中总数（站点给的估计值，用于显示）。</summary>
    public int Total { get; init; }
    /// <summary>本次返回的是第几页（从 1 起）。</summary>
    public int Page { get; init; }
    /// <summary>站点给出的末页页号。</summary>
    public int LastPage { get; init; }
    public bool HasMore => Items.Count > 0 && Page < LastPage;
    public string? Error { get; init; }
}

/// <summary>作品内的一张图。站点同时给出原图与几档缩放图，按用途各取所需。</summary>
public class PixivPageImage
{
    /// <summary>页码，从 1 起。</summary>
    public int Index { get; init; }
    /// <summary>原图地址（img-original，保留原始格式与尺寸）。</summary>
    public string Original { get; init; } = "";
    /// <summary>1200px 长边的缩放图（img-master），详情页看图用这一档。</summary>
    public string Regular { get; init; } = "";
    public int Width { get; init; }
    public int Height { get; init; }

    /// <summary>按画质设置取下载地址；原图缺失时退回缩放图。</summary>
    public string Best(bool original) =>
        original && Original.Length > 0 ? Original : (Regular.Length > 0 ? Regular : Original);
}

/// <summary>
/// 动图本体：一个逐帧 zip + 每帧的停留毫秒数（帧延迟不在 zip 里，丢了就还原不出动画）。
///
/// 非动图作品与取不到时一律是 <c>default</c>，那时两个字段都是 null——
/// 故这里的属性都要按 null 兜底，不能直接点出去（插画占绝大多数，走的正是这条路）。
/// </summary>
public readonly record struct PixivUgoira(string ZipUrl, IReadOnlyList<int> Delays)
{
    public bool Ok => !string.IsNullOrEmpty(ZipUrl);

    /// <summary>帧延迟（毫秒）；没有就是空表。</summary>
    public IReadOnlyList<int> Frames => Delays ?? [];
}

/// <summary>
/// pixiv 客户端。
///
/// 站点自家的前端走的是一套 JSON 接口（<c>/ajax/…</c>），返回 <c>{error, message, body}</c>，
/// 本类只用这一套，不解析页面 HTML——比 E-Hentai 那种「列表页抓 HTML + 元数据走 API」稳得多。
/// 用到的四个：
///   <c>/ajax/search/artworks/{词}</c>  搜索（每页 60 件，按页号翻页）
///   <c>/ajax/illust/{id}</c>           作品详情（标题/说明/标签/作者/张数…）
///   <c>/ajax/illust/{id}/pages</c>     逐页图片地址（原图 + 各档缩放图）
///   <c>/ajax/illust/{id}/ugoira_meta</c> 动图的 zip 与帧延迟
///
/// 两处与别的来源不同、改动时要留意：
///   1. <b>图片直链带防盗链</b>：i.pximg.net 对没有 pixiv Referer 的请求一律 403，
///      故取图的每一处（下载线程、Web 端图片代理、桌面端详情预览）都必须带
///      <see cref="ImageReferer"/>。直链本身不会过期，因此它仍算直链源。
///   2. <b>R-18 要登录</b>：未带 PHPSESSID 时站点不报错，只是把 R-18 作品从结果里滤掉
///      （<c>xRestrict</c> 一律是 0），单独打开 R-18 作品则返回 error。
///      所以「搜不到东西」既可能是真没有，也可能是没登录，提示文案要说清。
/// </summary>
public static class PixivApi
{
    /// <summary>works.source / download_list.source 的取值。</summary>
    public const string SourceName = "pixiv";

    public const string Origin = "https://www.pixiv.net";

    /// <summary>
    /// 取图必带的 Referer。i.pximg.net 的防盗链只认这一个头，缺了就是 403
    /// （与 cookie 无关，匿名也能取图，只要带上它）。
    /// </summary>
    public const string ImageReferer = Origin + "/";

    /// <summary>搜索接口固定每页 60 件（站点规定，不可调）。</summary>
    public const int SearchPageSize = 60;

    public static string ArtworkUrl(string id) => $"{Origin}/artworks/{id}";

    public static string UserUrl(string userId) => $"{Origin}/users/{userId}";

    /// <summary>
    /// 当前上下文用的是登录账号（<see cref="SiteAccounts.Active"/>：来源下拉选中的那个 pixiv 账号）。
    /// 匿名也能搜索，但结果里不会有 R-18。
    /// </summary>
    public static bool HasCookie => SiteAccounts.Active(AccountService.Pixiv) != null;

    /// <summary>请求用的 Cookie 头；匿名时为空串。</summary>
    public static string CookieHeader => SiteAccounts.Active(AccountService.Pixiv)?.Cookie ?? "";

    // ---------- HTTP ----------

    // 一件作品动辄几十张图，且详情页要逐张取预览：客户端必须复用，否则新建 HttpClient
    // 会把本机端口耗尽。多个账号并行使用，每个账号（cookie）各留一个——共用一个的话
    // 客户端的 cookie 罐会把站点给 A 账号种的 cookie 带到 B 账号的请求上。代理变了全部重建。
    private static readonly object ClientLock = new();
    private static readonly Dictionary<string, HttpClient> Clients = [];
    private static string _proxyKey = "";

    private static HttpClient Client()
    {
        var (proxyOn, proxy) = AppConfig.ReadProxy();
        var proxyKey = proxyOn ? proxy.Address?.ToString() ?? "" : "";
        lock (ClientLock)
        {
            if (proxyKey != _proxyKey)
            {
                foreach (var c in Clients.Values)
                    c.Dispose();
                Clients.Clear();
                _proxyKey = proxyKey;
            }
            if (!Clients.TryGetValue(CookieHeader, out var client))
                Clients[CookieHeader] = client = Http.CreateClient(TimeSpan.FromSeconds(30));
            return client;
        }
    }

    /// <summary>账号增删后调用，丢掉按旧 cookie 建的客户端。</summary>
    public static void Invalidate()
    {
        lock (ClientLock)
        {
            foreach (var c in Clients.Values)
                c.Dispose();
            Clients.Clear();
        }
    }

    /// <summary>站点按 Referer 与 cookie 判定访问者，两者都要逐请求带上。</summary>
    internal static void ApplyHeaders(HttpRequestMessage request)
    {
        if (CookieHeader.Length > 0)
            request.Headers.TryAddWithoutValidation("Cookie", CookieHeader);
        request.Headers.Referrer = new Uri(ImageReferer);
    }

    /// <summary>
    /// 取一个 ajax 接口的 body。
    ///
    /// 站点把业务错误也放在 HTTP 200 里（<c>{"error":true,"message":"…"}</c>），
    /// 所以状态码与 error 字段都要看；message 是站点原文（按 lang 参数给中文），直接透给用户最省事。
    /// </summary>
    private static async Task<(JsonElement? Body, string? Error)> GetBodyAsync(string url)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            ApplyHeaders(request);
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            using var resp = await Client().SendAsync(request);
            if (!resp.IsSuccessStatusCode)
            {
                Logger.Error($"pixiv 请求失败 HTTP {(int)resp.StatusCode}: {url}");
                return (null, resp.StatusCode == HttpStatusCode.NotFound
                    ? "站点上没有这件作品（可能已删除或设为不公开）"
                    : $"站点返回 HTTP {(int)resp.StatusCode}");
            }
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.True)
            {
                var message = root.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
                Logger.Error($"pixiv 接口返回错误: {message} ({url})");
                return (null, message.Length > 0 ? message : "站点拒绝了该请求");
            }
            if (!root.TryGetProperty("body", out var body))
                return (null, "站点返回的数据无法解析");
            // JsonDocument 随 using 释放，元素必须克隆出来才能带出本方法
            return (body.Clone(), null);
        }
        catch (JsonException e)
        {
            Logger.Error($"pixiv 返回的不是 JSON: {e.Message} ({url})");
            return (null, "站点返回的数据无法解析（可能被拦页或需要登录）");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            Logger.Error($"pixiv 网络请求失败: {e.Message} ({url})");
            return (null, "连不上 pixiv：检查网络与代理设置");
        }
    }

    /// <summary>
    /// 连接测试（设置页的按钮）：先搜一次确认连得上，再按是否配了 cookie 查登录态。
    /// </summary>
    public static async Task<(bool Ok, string Message)> TestAsync()
    {
        var probe = await SearchAsync("pixiv", 1);
        if (probe.Error is { } error)
            return (false, error);
        if (!HasCookie)
            return (true, "连接正常（未登录：搜索结果中不含 R-18 作品）");
        // 该接口只对已登录会话返回数据，匿名一律 error —— 拿它当登录态探针
        var (body, _) = await GetBodyAsync($"{Origin}/ajax/user/extra?lang=zh");
        return body is null
            ? (false, "已连上 pixiv，但登录已失效（请在「账号访问」重新登录）")
            : (true, "连接正常（已登录）");
    }

    /// <summary>
    /// 从 PHPSESSID / FANBOXSESSID 里取出 pixiv 用户号：两者都形如 <c>12345678_随机串</c>，
    /// 下划线前那段就是账号的用户号（FANBOX 用的就是 pixiv 账号）。取不出返回空串。
    /// </summary>
    public static string UserIdFromSession(string session)
    {
        var cut = (session ?? "").IndexOf('_');
        if (cut <= 0)
            return "";
        var id = session![..cut];
        return id.All(char.IsAsciiDigit) ? id : "";
    }

    /// <summary>
    /// 按用户号查 pixiv 的显示名（匿名即可查，不依赖登录）；查不到返回 null。
    /// 「账号访问」页据此把「已登录（显示名）」写出来，FANBOX 的登录也借它取名字——两者是同一个 pixiv 账号。
    /// </summary>
    public static async Task<string?> GetUserNameAsync(string userId)
    {
        if (userId.Length == 0 || !userId.All(char.IsAsciiDigit))
            return null;
        var (body, _) = await GetBodyAsync($"{Origin}/ajax/user/{userId}?lang=zh");
        if (body is not { } b)
            return null;
        var name = JStr(b, "name");
        return name.Length > 0 ? name : null;
    }

    // ---------- 输入解析 ----------

    private static readonly Regex ArtworkLinkPattern =
        new(@"pixiv\.net/(?:[a-z]{2}/)?artworks/(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex LegacyLinkPattern =
        new(@"illust_id=(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// 把用户输入认成「作品链接 / 作品号」还是「搜索关键字」：
    /// 贴作品地址（新版 /artworks/123 或旧版 member_illust.php?illust_id=123）或直接写作品号，
    /// 都直达那一件；其余当关键字搜。纯数字才当作品号，否则「2024」这种搜索词会被误判。
    /// </summary>
    public static string? ParseArtworkInput(string raw)
    {
        raw = (raw ?? "").Trim();
        if (raw.Length == 0)
            return null;
        if (ArtworkLinkPattern.Match(raw) is { Success: true } m)
            return m.Groups[1].Value;
        if (LegacyLinkPattern.Match(raw) is { Success: true } legacy)
            return legacy.Groups[1].Value;
        return raw.All(char.IsAsciiDigit) && raw.Length >= 5 ? raw : null;
    }

    // ---------- 搜索 ----------

    /// <summary>
    /// 按关键字搜作品（插画 + 漫画 + 动图）。page 从 1 起，每页 60 件。
    ///
    /// 关键字要同时出现在路径与 word 参数里（站点如此要求，少一处就报错）。
    /// 匹配方式与分级由设置决定（<see cref="AppConfig.PixivTagMatch"/> / <see cref="AppConfig.PixivSearchMode"/>）。
    /// </summary>
    public static async Task<PixivSearchResult> SearchAsync(string keyword, int page = 1)
    {
        keyword = (keyword ?? "").Trim();
        if (keyword.Length == 0)
            return new PixivSearchResult { Error = "请输入搜索关键字" };
        page = Math.Max(1, page);

        var word = Uri.EscapeDataString(keyword);
        var url = $"{Origin}/ajax/search/artworks/{word}?word={word}&order=date_d" +
                  $"&mode={AppConfig.PixivSearchMode}&p={page}&s_mode={AppConfig.PixivTagMatch}" +
                  "&type=all&lang=zh";
        var (body, error) = await GetBodyAsync(url);
        if (body is not { } b)
            return new PixivSearchResult { Error = error ?? "搜索请求失败" };
        if (!b.TryGetProperty("illustManga", out var section))
            return new PixivSearchResult { Error = "站点返回的搜索结果无法解析" };

        var items = new List<PixivArtwork>();
        if (section.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            foreach (var item in data.EnumerateArray())
            {
                // 结果数组里混着广告位等没有 id 的条目，挑掉
                if (ParseListItem(item) is { } artwork)
                    items.Add(artwork);
            }

        return new PixivSearchResult
        {
            Items = items,
            Total = JInt(section, "total"),
            Page = page,
            LastPage = Math.Max(page, JInt(section, "lastPage")),
        };
    }

    // ---------- 收藏 ----------

    /// <summary>收藏列表每页件数（与站点自家收藏页一致）。</summary>
    public const int BookmarkPageSize = 48;

    /// <summary>
    /// 当前账号（<see cref="SiteAccounts.Active"/>）的收藏列表，作品搜索里的「pixiv 收藏（账号名）」来源用它。
    /// <paramref name="tag"/> 为收藏标签筛选，空 = 全部。
    ///
    /// 站点把收藏分成公开（rest=show）与非公开（rest=hide）两份，各自分页；这里把两份接成一条：
    /// 先排完公开的，再接非公开的，按位置算偏移，所以翻页不会漏也不会重。
    /// 接口是 <c>/ajax/user/{用户号}/illusts/bookmarks</c>，非公开那份只对本人可见，故必须用账号自己的 cookie。
    /// </summary>
    public static async Task<PixivSearchResult> GetBookmarksAsync(string tag, int page = 1)
    {
        var account = SiteAccounts.Active(AccountService.Pixiv);
        if (account == null)
            return new PixivSearchResult { Error = "查看收藏要先在「账号访问」登录 pixiv 账号" };
        page = Math.Max(1, page);
        var offset = (page - 1) * BookmarkPageSize;

        var shown = await FetchBookmarksAsync(account.UserId, tag, "show", offset, BookmarkPageSize);
        if (shown.Error != null)
            return new PixivSearchResult { Error = shown.Error };
        var items = shown.Items;
        // 公开的不够一页（或已翻过公开那份）就接非公开的；够一页也取一条，只为拿到非公开的总数
        var need = BookmarkPageSize - shown.Raw;
        var hidden = await FetchBookmarksAsync(account.UserId, tag, "hide",
            Math.Max(0, offset - shown.Total), Math.Max(1, need));
        if (need > 0 && hidden.Error == null)
            items.AddRange(hidden.Items);

        var total = shown.Total + (hidden.Error == null ? hidden.Total : 0);
        return new PixivSearchResult
        {
            Items = items,
            Total = total,
            Page = page,
            LastPage = Math.Max(1, (total + BookmarkPageSize - 1) / BookmarkPageSize),
        };
    }

    /// <summary>取一段收藏。Raw = 站点返回的条数（含已删除/不可见的占位条目，偏移要按它算）。</summary>
    private static async Task<(List<PixivArtwork> Items, int Raw, int Total, string? Error)> FetchBookmarksAsync(
        string userId, string tag, string rest, int offset, int limit)
    {
        var url = $"{Origin}/ajax/user/{userId}/illusts/bookmarks?tag={Uri.EscapeDataString(tag ?? "")}" +
                  $"&offset={offset}&limit={limit}&rest={rest}&lang=zh";
        var (body, error) = await GetBodyAsync(url);
        if (body is not { } b)
            return ([], 0, 0, error ?? "获取收藏失败");
        var items = new List<PixivArtwork>();
        var raw = 0;
        if (b.TryGetProperty("works", out var works) && works.ValueKind == JsonValueKind.Array)
            foreach (var w in works.EnumerateArray())
            {
                raw++;
                // 作者删除或设为不公开的作品还占着收藏位，但标记 isMasked、内容全空，下不了，挑掉
                if (w.TryGetProperty("isMasked", out var masked) && masked.ValueKind == JsonValueKind.True)
                    continue;
                if (ParseListItem(w) is { } artwork)
                    items.Add(artwork);
            }
        return (items, raw, JInt(b, "total"), null);
    }

    /// <summary>搜索结果 / 收藏列表里的一条。没有 id（广告位/占位条目）返回 null。</summary>
    private static PixivArtwork? ParseListItem(JsonElement item)
    {
        var id = JStr(item, "id");
        if (id.Length == 0 && item.TryGetProperty("id", out var numId) && numId.ValueKind == JsonValueKind.Number)
            id = numId.GetRawText();   // 收藏接口的 id 有时是数字
        if (id.Length == 0)
            return null;
        return new PixivArtwork
        {
            Id = id,
            Title = JStr(item, "title"),
            Thumb = JStr(item, "url"),
            UserId = JStr(item, "userId"),
            UserName = JStr(item, "userName"),
            PageCount = Math.Max(1, JInt(item, "pageCount")),
            IllustType = JInt(item, "illustType"),
            XRestrict = JInt(item, "xRestrict"),
            AiType = JInt(item, "aiType"),
            CreateDate = JStr(item, "createDate"),
            Width = JInt(item, "width"),
            Height = JInt(item, "height"),
            Tags = JStrArray(item, "tags"),
        };
    }

    // ---------- 作品详情 ----------

    /// <summary>取一件作品的详情；取不到返回 (null, 原因)。</summary>
    public static async Task<(PixivArtwork? Artwork, string? Error)> GetArtworkAsync(string id)
    {
        var (body, error) = await GetBodyAsync($"{Origin}/ajax/illust/{id}?lang=zh");
        if (body is not { } b)
            return (null, error ?? "获取作品信息失败");

        // 标签在 tags.tags[].tag 里；站点另给 translation，但入库统一存原文
        // （与 E-Hentai 保留站点原始标签同理：翻译随语言变，原文才是稳定的分组键）
        var tags = new List<string>();
        if (b.TryGetProperty("tags", out var tagBox) && tagBox.TryGetProperty("tags", out var list) &&
            list.ValueKind == JsonValueKind.Array)
            foreach (var t in list.EnumerateArray())
            {
                var tag = JStr(t, "tag");
                if (tag.Length > 0)
                    tags.Add(tag);
            }

        var urls = b.TryGetProperty("urls", out var u) ? u : default;
        return (new PixivArtwork
        {
            Id = JStr(b, "illustId") is { Length: > 0 } iid ? iid : id,
            Title = JStr(b, "illustTitle") is { Length: > 0 } title ? title : JStr(b, "title"),
            Thumb = urls.ValueKind == JsonValueKind.Object ? JStr(urls, "regular") : "",
            UserId = JStr(b, "userId"),
            UserName = JStr(b, "userName"),
            PageCount = Math.Max(1, JInt(b, "pageCount")),
            IllustType = JInt(b, "illustType"),
            XRestrict = JInt(b, "xRestrict"),
            AiType = JInt(b, "aiType"),
            CreateDate = JStr(b, "createDate"),
            Width = JInt(b, "width"),
            Height = JInt(b, "height"),
            Description = StripHtml(JStr(b, "description")),
            BookmarkCount = JInt(b, "bookmarkCount"),
            LikeCount = JInt(b, "likeCount"),
            ViewCount = JInt(b, "viewCount"),
            Tags = tags,
        }, null);
    }

    /// <summary>取一件作品的逐页图片地址（单图投稿也回一条）。</summary>
    public static async Task<List<PixivPageImage>> GetPagesAsync(string id)
    {
        var (body, _) = await GetBodyAsync($"{Origin}/ajax/illust/{id}/pages?lang=zh");
        var pages = new List<PixivPageImage>();
        if (body is not { } b || b.ValueKind != JsonValueKind.Array)
            return pages;
        var index = 0;
        foreach (var page in b.EnumerateArray())
        {
            index++;
            if (!page.TryGetProperty("urls", out var urls))
                continue;
            pages.Add(new PixivPageImage
            {
                Index = index,
                Original = JStr(urls, "original"),
                Regular = JStr(urls, "regular"),
                Width = JInt(page, "width"),
                Height = JInt(page, "height"),
            });
        }
        return pages;
    }

    /// <summary>
    /// 取动图本体：一个逐帧 zip + 每帧的停留毫秒数。
    ///
    /// 帧延迟只在这个接口里，zip 内没有——单存 zip 就还原不出原本的播放速度，
    /// 所以入库时会把延迟一并写进作品目录下的说明文件。
    /// </summary>
    public static async Task<PixivUgoira> GetUgoiraAsync(string id)
    {
        var (body, _) = await GetBodyAsync($"{Origin}/ajax/illust/{id}/ugoira_meta?lang=zh");
        if (body is not { } b)
            return default;
        // originalSrc 是原尺寸 zip，src 是 600x600 预览版；下载一律取前者
        var zip = JStr(b, "originalSrc");
        if (zip.Length == 0)
            zip = JStr(b, "src");
        var delays = new List<int>();
        if (b.TryGetProperty("frames", out var frames) && frames.ValueKind == JsonValueKind.Array)
            foreach (var frame in frames.EnumerateArray())
                delays.Add(JInt(frame, "delay"));
        return new PixivUgoira(zip, delays);
    }

    // ---------- 小工具 ----------

    private static readonly Regex BreakPattern =
        new(@"<br\s*/?>|</p>|</div>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TagPattern = new("<[^>]+>", RegexOptions.Compiled);

    /// <summary>
    /// 作者说明里存的是 HTML（含 &lt;br&gt; 与站内链接），入库前转成纯文本：
    /// 换行类标签换成换行，其余标签去掉，最后解实体。
    /// </summary>
    internal static string StripHtml(string html)
    {
        if (string.IsNullOrEmpty(html))
            return "";
        var text = BreakPattern.Replace(html, "\n");
        text = TagPattern.Replace(text, "");
        return WebUtility.HtmlDecode(text).Trim();
    }

    /// <summary>
    /// 图片地址里的扩展名。pixiv 的原图地址形如 <c>…/12345_p0.png</c>，扩展名即真实格式；
    /// 取缩放图时一律是 .jpg。认不出的按 .jpg 算（站点没有第三种投递格式）。
    /// </summary>
    internal static string ExtensionOf(string url)
    {
        var path = url.Split('?')[0];
        var dot = path.LastIndexOf('.');
        if (dot < 0 || dot < path.LastIndexOf('/'))
            return ".jpg";
        var ext = path[dot..].ToLowerInvariant();
        return ext.Length is > 1 and <= 5 ? ext : ".jpg";
    }

    /// <summary>该地址是否指向 pixiv 自家域（图片代理只放行这些，免得被当成任意地址的抓取器）。</summary>
    public static bool IsPixivUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return false;
        var host = uri.Host.ToLowerInvariant();
        string[] roots = ["pixiv.net", "pximg.net"];
        return roots.Any(r => host == r || host.EndsWith("." + r, StringComparison.Ordinal));
    }

    private static string JStr(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) &&
        v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : "";

    private static int JInt(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v))
            return 0;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetInt32(out var n) ? n : 0,
            // 作品号一类的数字站点有时给字符串，两种都认
            JsonValueKind.String => int.TryParse(v.GetString(), out var s) ? s : 0,
            _ => 0,
        };
    }

    private static List<string> JStrArray(JsonElement e, string name)
    {
        var list = new List<string>();
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v) ||
            v.ValueKind != JsonValueKind.Array)
            return list;
        foreach (var item in v.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } s)
                list.Add(s);
        return list;
    }
}
