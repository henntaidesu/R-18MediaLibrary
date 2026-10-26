using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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

/// <summary>画廊的最小标识：站点用 (gid, token) 二元组定位一个画廊，两者缺一不可。</summary>
public readonly record struct EhGalleryRef(long Gid, string Token)
{
    public override string ToString() => $"{Gid}/{Token}";
}

/// <summary>画廊元数据（来自官方 API 的 gdata 接口）。</summary>
public class EhGallery
{
    public long Gid { get; init; }
    public string Token { get; init; } = "";
    /// <summary>英文标题（站上的主标题）。</summary>
    public string Title { get; init; } = "";
    /// <summary>日文标题，可能为空。</summary>
    public string TitleJpn { get; init; } = "";
    /// <summary>分类：Doujinshi / Manga / Artist CG / Game CG / Non-H / Image Set / Cosplay / Misc …</summary>
    public string Category { get; init; } = "";
    /// <summary>站上封面缩略图地址（ehgt.org，无需登录）。</summary>
    public string Thumb { get; init; } = "";
    public string Uploader { get; init; } = "";
    /// <summary>投稿时间（unix 秒）。</summary>
    public long PostedUnix { get; init; }
    /// <summary>图片张数。</summary>
    public int FileCount { get; init; }
    /// <summary>画廊总字节数。</summary>
    public long FileSize { get; init; }
    public string Rating { get; init; } = "";
    /// <summary>已被删除/下架（站上仍可访问，但内容可能不全）。</summary>
    public bool Expunged { get; init; }
    /// <summary>带命名空间的原始标签，形如 artist:xxx、female:sole female、language:chinese。</summary>
    public List<string> Tags { get; init; } = [];

    public EhGalleryRef Ref => new(Gid, Token);

    /// <summary>展示用标题：优先日文原名，没有才退回英文标题。</summary>
    public string DisplayTitle => TitleJpn.Length > 0 ? TitleJpn : Title;

    /// <summary>画廊页地址。</summary>
    public string PageUrl => EhentaiApi.GalleryUrl(Gid, Token);

    /// <summary>发布日期（本地时区）。</summary>
    public DateTime Posted => DateTimeOffset.FromUnixTimeSeconds(PostedUnix).LocalDateTime;

    /// <summary>某命名空间下的全部标签值（不含命名空间前缀）。</summary>
    public List<string> TagsOf(string ns)
    {
        var prefix = ns + ":";
        return Tags.Where(t => t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                   .Select(t => t[prefix.Length..])
                   .Where(t => t.Length > 0)
                   .ToList();
    }
}

/// <summary>画廊内的一张图：站上的图片页地址（真正的图片直链要进这一页才拿得到）。</summary>
public class EhImagePage
{
    /// <summary>页码，从 1 起。</summary>
    public int Index { get; init; }
    /// <summary>图片页地址（/s/&lt;key&gt;/&lt;gid&gt;-&lt;index&gt;）。</summary>
    public string Url { get; init; } = "";
    /// <summary>站上登记的原始文件名（缩略图 title 属性里带着），可能为空。</summary>
    public string FileName { get; init; } = "";
    /// <summary>
    /// 站上的逐张缩略图地址（仅「大缩略图」版式有；匿名访问时为空，改用下面的雪碧图字段）。
    /// </summary>
    public string Thumb { get; init; } = "";

    /// <summary>
    /// 雪碧图地址。匿名访问时站点把 20 张缩略图拼成一张图投递，
    /// 单张缩略图 = 从 (SpriteX, 0) 起、TileW×TileH 大小的那一格。
    /// </summary>
    public string SpriteUrl { get; init; } = "";

    /// <summary>本页缩略图在雪碧图中的横向起点（站点给的是负的 background-position，这里存正值）。</summary>
    public int SpriteX { get; init; }

    /// <summary>单格宽高（同一本画廊内固定，站点按画廊内容给，实测 200×128 一类）。</summary>
    public int TileW { get; init; }
    public int TileH { get; init; }

    /// <summary>有没有可用的缩略图（逐张的或雪碧图里的一格）。</summary>
    public bool HasThumb => Thumb.Length > 0 || SpriteUrl.Length > 0;
}

/// <summary>一页搜索结果：画廊列表 + 下一页游标（站点已改为游标翻页，没有页号）。</summary>
public class EhSearchResult
{
    public List<EhGallery> Items { get; init; } = [];
    /// <summary>下一页游标（传回 <see cref="EhentaiApi.SearchAsync"/> 的 next 参数）；没有下一页则为空。</summary>
    public string Next { get; init; } = "";
    public bool HasMore => Next.Length > 0;
    public string? Error { get; init; }
}

/// <summary>解析图片页的结果：拿到直链，或者说明为什么没拿到。</summary>
public readonly record struct EhImageLink(string Url, string FileName, string? Error)
{
    public bool Ok => Error is null && Url.Length > 0;
    /// <summary>失败原因为「本 IP 的看图额度已用尽」——重试没有意义，要等额度恢复。</summary>
    public bool Throttled => Error == EhentaiApi.LimitError;
}

/// <summary>
/// e-hentai / exhentai 客户端。
///
/// 站点没有搜索 API，只有元数据 API，所以取数据分两条腿走：
///   1. 搜索/画廊/图片页 → 抓 HTML（用正则挑关键链接，不依赖具体版式，改版也不易碎）；
///   2. 画廊元数据      → 官方 API <c>https://api.e-hentai.org/api.php</c> 的 gdata 方法
///      （一次最多 25 个画廊，返回标题/分类/标签/张数/大小等，比解析列表页可靠得多）。
/// 搜索因此是「列表页只取 (gid, token)，元数据统一走 API」——列表页版式随显示模式（紧凑/缩略图）
/// 变化很大，但画廊链接的形状是固定的。
///
/// 图片直链拿不到批量接口：每张图都要进它自己的图片页（/s/…）才有 &lt;img id="img"&gt;，
/// 且直链与访问者 IP 绑定、会过期。故入队时只存图片页地址，由下载线程临下载前现解析
/// （与论坛源经 debrid-link 现解析同构，见 <see cref="DownloadEngine"/> 的 ehentai 分支）。
///
/// exhentai 必须带登录 cookie（<c>ipb_member_id</c> / <c>ipb_pass_hash</c> / <c>igneous</c>），
/// 否则整站返回空白页；e-hentai 可匿名浏览，但带上 cookie 能解锁被过滤的内容与更高的看图额度。
/// </summary>
public static class EhentaiApi
{
    /// <summary>works.source / download_list.source 的取值。</summary>
    public const string SourceName = "ehentai";

    /// <summary>元数据 API 地址；e-hentai 与 exhentai 的画廊都查这一个。</summary>
    private const string ApiUrl = "https://api.e-hentai.org/api.php";

    /// <summary>gdata 单次请求的画廊数上限（站点规定）。</summary>
    public const int MetadataBatch = 25;

    /// <summary>画廊列表页固定每页张数（站点默认 20，雪碧图也按 20 张一拼）。</summary>
    public const int ListingPageSize = 20;

    /// <summary>看图额度用尽的失败标记（见 <see cref="EhImageLink.Throttled"/>）。</summary>
    public const string LimitError = "limit";

    /// <summary>当前站点域名：e-hentai.org 或 exhentai.org。</summary>
    public static string Host => AppConfig.EhentaiHost;

    public static string Origin => $"https://{Host}";

    public static string GalleryUrl(long gid, string token) => $"{Origin}/g/{gid}/{token}/";

    /// <summary>是否为里站（exhentai）——它没有匿名访问，未配置 cookie 时要提前拦下并提示。</summary>
    public static bool IsExHentai => Host.Contains("exhentai", StringComparison.OrdinalIgnoreCase);

    /// <summary>已配置登录 cookie（里站必需；表站可选）。</summary>
    public static bool HasCookie =>
        AppConfig.EhentaiMemberId.Length > 0 && AppConfig.EhentaiPassHash.Length > 0;

    /// <summary>
    /// 请求用的 Cookie 头。
    /// <c>nw=1</c> 跳过站点对部分画廊的「Content Warning」拦页（不跳过会被重定向到确认页，抓不到内容）；
    /// <c>sl=dm_2</c> 请求缩略图版式的画廊页。
    ///
    /// 注意：<c>sl</c> 对匿名访问**不生效**（实测 e-hentai 匿名一律返回雪碧图版式，
    /// <c>?inline_set=ts_l/ts_m</c> 同样无效，字节数完全一致）。所以解析不能依赖逐张 &lt;img&gt;：
    /// 见 <see cref="ThumbTagPattern"/>——页码与原始文件名在雪碧图版式下挂在 &lt;div&gt; 上，
    /// 只有登录且把画廊版式设成大缩略图时才会是带 src 的 &lt;img&gt;。
    /// </summary>
    public static string CookieHeader
    {
        get
        {
            var parts = new List<string> { "nw=1", "sl=dm_2" };
            if (AppConfig.EhentaiMemberId is { Length: > 0 } id)
                parts.Add($"ipb_member_id={id}");
            if (AppConfig.EhentaiPassHash is { Length: > 0 } hash)
                parts.Add($"ipb_pass_hash={hash}");
            if (AppConfig.EhentaiIgneous is { Length: > 0 } igneous)
                parts.Add($"igneous={igneous}");
            return string.Join("; ", parts);
        }
    }

    // ---------- 账号登录 ----------

    /// <summary>
    /// 账号密码的登录入口只有论坛这一个：e-hentai / exhentai 主站本身没有登录表单，
    /// 页面上的「Login」也是跳到这里。
    /// </summary>
    private const string LoginUrl = "https://forums.e-hentai.org/index.php?act=Login&CODE=01";

    /// <summary>里站领取 igneous 的落点；随便哪个需要登录的页面都行，取收藏页是各实现的惯例。</summary>
    private const string IgneousProbeUrl = "https://exhentai.org/favorites.php";

    /// <summary>
    /// 登录结果。<paramref name="Captcha"/> 要单独区分：触发验证码时程序无路可走，
    /// 只能提示用户改用浏览器复制 cookie，而不是让他反复重试账号密码。
    /// </summary>
    public readonly record struct EhLoginResult(bool Ok, string Message, bool Captcha = false);

    /// <summary>
    /// 用账号密码登录并取回 cookie，成功后写入配置。
    ///
    /// 站点的鉴权认三个 cookie，而它们**不是一次拿全的**：
    ///   1. POST 论坛登录 → 下发 <c>ipb_member_id</c> / <c>ipb_pass_hash</c>；
    ///   2. 带着这两个再访问一次里站（<see cref="IgneousProbeUrl"/>）→ 里站才下发 <c>igneous</c>。
    /// 第二步不能省：只有前两个 cookie 时里站照样回空白页，这正是「cookie 都填了却进不去里站」
    /// 最常见的原因。
    ///
    /// 密码只在本次调用里用一次、不落库：cookie 本身就是长期凭据，存下口令没有额外用处，
    /// 却多一处明文。
    /// </summary>
    public static async Task<EhLoginResult> LoginAsync(string username, string password)
    {
        username = username.Trim();
        if (username.Length == 0 || password.Length == 0)
            return new EhLoginResult(false, "请先填写账号与密码");

        // 三步请求要共用同一个 cookie 罐，否则第 2 步带不上第 1 步下发的登录 cookie
        var jar = new CookieContainer();
        // nw=1 要进罐里而不是手写 Cookie 头：同时用手写头和 cookie 罐会互相覆盖
        SeedCookie(jar, "e-hentai.org", "nw", "1");
        SeedCookie(jar, "exhentai.org", "nw", "1");
        using var client = Http.CreateClient(TimeSpan.FromSeconds(30), cookies: jar);

        string body;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, LoginUrl)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    // CookieDate=1 = 勾上「记住我」；不给的话只发会话级 cookie，关掉进程就没了
                    ["CookieDate"] = "1",
                    ["b"] = "d",
                    ["bt"] = "1-1",
                    ["UserName"] = username,
                    ["PassWord"] = password,
                    ["ipb_login_submit"] = "Login!",
                }),
            };
            // 论坛会校验来路，缺了这个 Referer 会被当成站外提交打回
            request.Headers.Referrer = new Uri("https://e-hentai.org/bounce_login.php?b=d&bt=1-1");
            using var response = await client.SendAsync(request);
            body = await response.Content.ReadAsStringAsync();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            Logger.Error($"E-Hentai 登录失败（连不上论坛）: {e.Message}");
            return new EhLoginResult(false, $"连不上登录服务器，检查网络与代理：{e.Message}");
        }

        // 论坛登录成败都回 HTTP 200，只能看正文里的提示文案
        if (body.Contains("The captcha was not entered correctly", StringComparison.Ordinal))
        {
            Logger.Error("E-Hentai 登录被要求输入验证码，无法自动登录");
            return new EhLoginResult(false,
                "站点要求输入验证码，程序无法自动登录。请在浏览器里登录一次，再把三个 cookie 手工填到下面。",
                Captcha: true);
        }
        if (!body.Contains("You are now logged in as", StringComparison.Ordinal))
        {
            Logger.Error("E-Hentai 登录失败：账号或密码错误");
            return new EhLoginResult(false, "登录失败：账号或密码不正确");
        }

        var memberId = ReadCookie(jar, "ipb_member_id");
        var passHash = ReadCookie(jar, "ipb_pass_hash");
        if (memberId.Length == 0 || passHash.Length == 0)
        {
            Logger.Error("E-Hentai 登录成功但没取到 ipb_member_id / ipb_pass_hash");
            return new EhLoginResult(false, "登录成功但没取到 cookie，站点可能改版了");
        }

        // 论坛把 cookie 种在 .e-hentai.org 上，而 exhentai.org 是另一个顶级域，cookie 罐
        // 不会把它们发过去——不显式复制一份到里站域，下一步就是匿名请求，永远领不到 igneous。
        // （EhViewer 等客户端同样是两个域各写一份，原因就在这里。）
        SeedCookie(jar, "exhentai.org", "ipb_member_id", memberId);
        SeedCookie(jar, "exhentai.org", "ipb_pass_hash", passHash);

        var (igneous, reached) = await FetchIgneousAsync(client, jar);

        AppConfig.Write("ehentai", "member_id", memberId);
        AppConfig.Write("ehentai", "pass_hash", passHash);

        if (igneous.Length > 0)
        {
            AppConfig.Write("ehentai", "igneous", igneous);
            Invalidate();
            Logger.Info($"E-Hentai 登录成功（member_id={memberId}），已取得里站 cookie");
            return new EhLoginResult(true, "登录成功，已取得里站(exhentai)访问权限");
        }

        // 连不上里站 ≠ 账号没有里站权限。前者多半是代理没通，此时绝不能动已有的 igneous——
        // 那可能是用户手工填进来、本来能用的值，冲掉了就要重新去浏览器扒一遍。
        if (!reached)
        {
            Invalidate();
            Logger.Info($"E-Hentai 登录成功（member_id={memberId}），但连不上 exhentai，未更新 igneous");
            return new EhLoginResult(true,
                "登录成功，但连不上 exhentai(里站)，igneous 保持原值。若要用里站请检查代理后重试登录。");
        }

        // 连上了却不下发 igneous：这个账号确实没有里站权限（新号通常要满一段时间才开放）
        AppConfig.Write("ehentai", "igneous", "");
        Invalidate();
        Logger.Info($"E-Hentai 登录成功（member_id={memberId}），但该账号没有里站权限");
        return new EhLoginResult(true,
            "登录成功，但站点未下发 igneous——该账号暂无 exhentai(里站) 权限，可先用 e-hentai(表站)。");
    }

    /// <summary>
    /// 领取 igneous：只有带着登录 cookie 访问里站，里站才会下发它。
    /// 首次访问常先吃一次重定向（种 cookie 发生在重定向那一跳上），所以没拿到时再试一次。
    ///
    /// 返回值里的 Reached 用来区分两种"没拿到"：连不上里站（不能动已有配置）
    /// 与连上了但没下发（账号确实没权限）。
    /// </summary>
    private static async Task<(string Igneous, bool Reached)> FetchIgneousAsync(
        HttpClient client, CookieContainer jar)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, IgneousProbeUrl);
                request.Headers.Referrer = new Uri("https://e-hentai.org/");
                using var response = await client.SendAsync(request);
                await response.Content.ReadAsStringAsync();   // 读完正文才算走完一次请求
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
                Logger.Error($"E-Hentai 领取 igneous 失败（连不上 exhentai）: {e.Message}");
                return ("", false);
            }

            var value = ReadCookie(jar, "igneous");
            // "mystery" 是站点在说「我不认这个会话」，不是真令牌；存下来只会让人以为配好了
            if (value.Equals("mystery", StringComparison.OrdinalIgnoreCase))
            {
                Logger.Error("E-Hentai 里站下发的 igneous 为 mystery（会话未被认可），按未取得处理");
                return ("", true);
            }
            if (value.Length > 0)
                return (value, true);
        }
        return ("", true);
    }

    /// <summary>往 cookie 罐里按指定域塞一个 cookie（域不带前导点即按主机精确匹配）。</summary>
    private static void SeedCookie(CookieContainer jar, string domain, string name, string value) =>
        jar.Add(new Cookie(name, value, "/", domain));

    /// <summary>从 cookie 罐里取一个值：同名 cookie 可能种在论坛域或两个主站域上，挨个找。</summary>
    private static string ReadCookie(CookieContainer jar, string name)
    {
        foreach (var origin in new[]
                 { "https://exhentai.org", "https://e-hentai.org", "https://forums.e-hentai.org" })
        {
            if (jar.GetCookies(new Uri(origin))[name] is { Value.Length: > 0 } cookie)
                return cookie.Value;
        }
        return "";
    }

    // ---------- HTTP ----------

    // 一个画廊动辄几百张图，每张都要现解析图片页：客户端必须复用，
    // 否则每次新建 HttpClient 会把本机端口耗尽。配置（代理/站点/cookie）变了才重建。
    private static readonly object ClientLock = new();
    private static HttpClient? _client;
    private static string _clientKey = "";

    private static HttpClient Client()
    {
        var (proxyOn, proxy) = AppConfig.ReadProxy();
        var key = $"{Host}|{CookieHeader}|{(proxyOn ? proxy.Address?.ToString() : "")}";
        lock (ClientLock)
        {
            if (_client != null && _clientKey == key)
                return _client;
            _client?.Dispose();
            _client = Http.CreateClient(TimeSpan.FromSeconds(30));
            _clientKey = key;
            return _client;
        }
    }

    /// <summary>配置（站点/cookie/代理）变更后调用，下次请求会用新配置重建客户端。</summary>
    public static void Invalidate()
    {
        lock (ClientLock)
        {
            _client?.Dispose();
            _client = null;
            _clientKey = "";
        }
    }

    /// <summary>带 cookie 与 Referer 的 GET，返回响应正文；失败返回 null（已记日志）。</summary>
    private static async Task<string?> GetAsync(string url)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            ApplyHeaders(request);
            using var resp = await Client().SendAsync(request);
            if (!resp.IsSuccessStatusCode)
            {
                Logger.Error($"E-Hentai 请求失败 HTTP {(int)resp.StatusCode}: {url}");
                return null;
            }
            var text = await resp.Content.ReadAsStringAsync();
            // 里站未带有效 cookie 时不会报错，而是回一个空白页（几百字节），要单独认出来
            if (IsExHentai && text.Length < 512 && !text.Contains("/g/", StringComparison.Ordinal))
            {
                Logger.Error("exhentai 返回空白页：登录 cookie 缺失或已失效（见系统设置 → E-Hentai）");
                return null;
            }
            return text;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            Logger.Error($"E-Hentai 网络请求失败: {e.Message} ({url})");
            return null;
        }
    }

    /// <summary>站点会按 Referer 与 cookie 判定访问者，两者都要逐请求带上。</summary>
    internal static void ApplyHeaders(HttpRequestMessage request)
    {
        request.Headers.TryAddWithoutValidation("Cookie", CookieHeader);
        request.Headers.Referrer = new Uri(Origin + "/");
    }

    /// <summary>
    /// 连接测试（设置页的按钮）：取一次站点首页，看能不能拿到画廊列表。
    /// 返回 (是否成功, 说明)。里站没配 cookie 会返回空白页而不是错误码，这里单独说清楚。
    /// </summary>
    public static async Task<(bool Ok, string Message)> TestAsync()
    {
        if (IsExHentai && !HasCookie)
            return (false, "exhentai 需要登录 cookie（ipb_member_id / ipb_pass_hash / igneous）");
        var html = await GetAsync(Origin + "/");
        if (html is null)
            return (false, IsExHentai
                ? "连不上或返回空白页：检查代理与登录 cookie 是否有效"
                : "连不上站点：检查网络与代理设置");
        if (!GalleryLinkPattern.IsMatch(html))
            return (false, "已连上站点，但没解析到画廊列表（可能被拦页或站点改版）");
        // 登录后页面右上角会出现设置入口，据此粗略区分匿名/已登录
        var signedIn = HasCookie && !html.Contains("Login", StringComparison.OrdinalIgnoreCase);
        return (true, signedIn ? $"连接正常（{Host}，已登录）" : $"连接正常（{Host}，匿名浏览）");
    }

    // ---------- 输入解析 ----------

    private static readonly Regex GalleryLinkPattern =
        new(@"/g/(\d+)/([0-9a-f]{10})", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ImagePagePattern =
        new(@"/s/([0-9a-f]{10})/(\d+)-(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// 把用户输入认成「画廊链接」还是「关键字」：贴画廊地址直接进那一本，否则当搜索词。
    /// 形如 https://e-hentai.org/g/123456/abcdef0123/ 的链接（表/里站皆可）会被认出来。
    /// </summary>
    public static EhGalleryRef? ParseGalleryInput(string raw)
    {
        raw = (raw ?? "").Trim();
        if (raw.Length == 0)
            return null;
        var m = GalleryLinkPattern.Match(raw);
        if (!m.Success || !long.TryParse(m.Groups[1].Value, out var gid))
            return null;
        return new EhGalleryRef(gid, m.Groups[2].Value.ToLowerInvariant());
    }

    // ---------- 搜索 ----------

    /// <summary>
    /// 搜索画廊：抓列表页取出本页所有 (gid, token)，再统一走 API 补元数据。
    /// next 为上一页返回的游标（站点已改为游标翻页，传空即第一页）。
    /// </summary>
    public static async Task<EhSearchResult> SearchAsync(string keyword, string next = "")
    {
        keyword = (keyword ?? "").Trim();
        if (IsExHentai && !HasCookie)
            return new EhSearchResult { Error = "exhentai 需要登录 cookie，请在系统设置 → E-Hentai 中填写" };

        var url = $"{Origin}/?f_search={Uri.EscapeDataString(keyword)}";
        if (next.Length > 0)
            url += $"&next={Uri.EscapeDataString(next)}";
        var html = await GetAsync(url);
        if (html is null)
            return new EhSearchResult { Error = "搜索请求失败（站点无响应或 cookie 失效）" };

        var refs = ParseGalleryRefs(html);
        if (refs.Count == 0)
            return new EhSearchResult { Next = "" };   // 没有结果，不是错误

        var items = await GetMetadataAsync(refs);
        // 元数据按 API 返回顺序回来，这里恢复成列表页上的原始排序（站点默认按投稿时间新→旧）
        var byGid = items.ToDictionary(g => g.Gid);
        var ordered = refs.Where(r => byGid.ContainsKey(r.Gid)).Select(r => byGid[r.Gid]).ToList();
        return new EhSearchResult { Items = ordered, Next = ParseNextCursor(html) };
    }

    /// <summary>从列表页 HTML 里挑出全部画廊链接并去重（保持出现顺序）。</summary>
    private static List<EhGalleryRef> ParseGalleryRefs(string html)
    {
        var list = new List<EhGalleryRef>();
        var seen = new HashSet<long>();
        foreach (Match m in GalleryLinkPattern.Matches(html))
        {
            if (!long.TryParse(m.Groups[1].Value, out var gid) || !seen.Add(gid))
                continue;
            list.Add(new EhGalleryRef(gid, m.Groups[2].Value.ToLowerInvariant()));
        }
        return list;
    }

    private static readonly Regex NextLinkPattern =
        new(@"id=""[ud]next""[^>]*href=""([^""]+)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>从列表页底部的「下一页」链接里取出游标；没有下一页返回空串。</summary>
    private static string ParseNextCursor(string html)
    {
        var m = NextLinkPattern.Match(html);
        if (!m.Success)
            return "";
        var href = System.Net.WebUtility.HtmlDecode(m.Groups[1].Value);
        var q = href.IndexOf("next=", StringComparison.OrdinalIgnoreCase);
        if (q < 0)
            return "";
        var value = href[(q + 5)..];
        var end = value.IndexOf('&');
        return end >= 0 ? value[..end] : value;
    }

    // ---------- 元数据（官方 API）----------

    /// <summary>取一批画廊的元数据（自动按 25 个一组分批请求）；取不到的画廊会被略过。</summary>
    public static async Task<List<EhGallery>> GetMetadataAsync(IReadOnlyList<EhGalleryRef> refs)
    {
        var all = new List<EhGallery>();
        for (var i = 0; i < refs.Count; i += MetadataBatch)
        {
            var chunk = refs.Skip(i).Take(MetadataBatch).ToList();
            var batch = await FetchMetadataBatchAsync(chunk);
            if (batch != null)
                all.AddRange(batch);
        }
        return all;
    }

    /// <summary>取单个画廊的元数据；失败返回 null。</summary>
    public static async Task<EhGallery?> GetGalleryAsync(EhGalleryRef gref) =>
        (await GetMetadataAsync([gref])).FirstOrDefault();

    private static async Task<List<EhGallery>?> FetchMetadataBatchAsync(IReadOnlyList<EhGalleryRef> refs)
    {
        var sb = new StringBuilder("{\"method\":\"gdata\",\"gidlist\":[");
        for (var i = 0; i < refs.Count; i++)
        {
            if (i > 0)
                sb.Append(',');
            sb.Append('[').Append(refs[i].Gid).Append(",\"").Append(refs[i].Token).Append("\"]");
        }
        sb.Append("],\"namespace\":1}");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl)
            {
                Content = new StringContent(sb.ToString(), Encoding.UTF8, "application/json"),
            };
            ApplyHeaders(request);
            using var resp = await Client().SendAsync(request);
            if (!resp.IsSuccessStatusCode)
            {
                Logger.Error($"E-Hentai 元数据接口失败 HTTP {(int)resp.StatusCode}");
                return null;
            }
            var text = await resp.Content.ReadAsStringAsync();
            return ParseMetadata(text);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            Logger.Error($"E-Hentai 元数据请求失败: {e.Message}");
            return null;
        }
    }

    private static List<EhGallery> ParseMetadata(string json)
    {
        var list = new List<EhGallery>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("gmetadata", out var arr) ||
            arr.ValueKind != JsonValueKind.Array)
        {
            // 站点把错误也塞在 gmetadata 之外（如 {"error":"..."}）
            if (doc.RootElement.TryGetProperty("error", out var err))
                Logger.Error($"E-Hentai 元数据接口返回错误: {err}");
            return list;
        }
        foreach (var item in arr.EnumerateArray())
        {
            // 单个画廊查不到时这一项会带 error（如已被彻底删除），跳过即可
            if (item.TryGetProperty("error", out _))
                continue;
            var tags = new List<string>();
            if (item.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Array)
                foreach (var one in t.EnumerateArray())
                    if (one.GetString() is { Length: > 0 } s)
                        tags.Add(s);
            list.Add(new EhGallery
            {
                Gid = JNum(item, "gid"),
                Token = DlsiteApi.JStr(item, "token"),
                Title = DlsiteApi.JStr(item, "title"),
                TitleJpn = DlsiteApi.JStr(item, "title_jpn"),
                Category = DlsiteApi.JStr(item, "category"),
                Thumb = DlsiteApi.JStr(item, "thumb"),
                Uploader = DlsiteApi.JStr(item, "uploader"),
                PostedUnix = JNum(item, "posted"),
                FileCount = (int)JNum(item, "filecount"),
                FileSize = JNum(item, "filesize"),
                Rating = DlsiteApi.JStr(item, "rating"),
                Expunged = item.TryGetProperty("expunged", out var ex) && ex.ValueKind == JsonValueKind.True,
                Tags = tags,
            });
        }
        return list;
    }

    /// <summary>API 的数字字段时而是数字、时而是字符串（filecount/posted 就是字符串），两种都认。</summary>
    private static long JNum(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v))
            return 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n))
            return n;
        return v.ValueKind == JsonValueKind.String &&
               long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s)
            ? s : 0;
    }

    // ---------- 画廊内的图片页清单 ----------

    // 画廊页里每张图的缩略图标签：**不能限定 <img>**——站点有两套版式，
    //   雪碧图版式（匿名访问一律如此）：<div title="Page 1: xxx.jpg" style="background:...url(雪碧图)...">
    //   大缩略图版式（登录并设置后）  ：<img title="Page 1: xxx.jpg" src="逐张缩略图">
    // 两套都带 title，故按「任意单个标签」取出整块，再从中挑 title / src（src 只有后者有）。
    // 属性顺序不固定，所以是先取标签再逐项挑，而不是一条正则串起来。
    private static readonly Regex ThumbTagPattern =
        new(@"<[^>]*title=""Page \d+: [^""]*""[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ThumbTitlePattern =
        new(@"title=""Page (\d+): ([^""]*)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ThumbSrcPattern =
        new(@"src=""([^""]+)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // 雪碧图版式的 style，形如
    //   width:200px;height:128px;background:transparent url(<雪碧图>) -400px 0 no-repeat
    // 偏移按单格宽度递增，20 张拼一图。三项都取到才算数（缺一就没法定位那一格）。
    private static readonly Regex TileSizePattern =
        new(@"width:\s*(\d+)px;\s*height:\s*(\d+)px", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SpriteUrlPattern =
        new(@"background:[^""]*?url\(([^)]+)\)\s*(-?\d+)px", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// 取画廊的**一个列表页**（站点每页固定 20 张），供详情页按需懒加载。
    /// 返回该页的缩略图条目；请求失败返回 null，该页没有条目则返回空列表。
    /// </summary>
    public static async Task<List<EhImagePage>?> GetListingPageAsync(EhGalleryRef gref, int p)
    {
        var url = GalleryUrl(gref.Gid, gref.Token) + (p > 0 ? $"?p={p}" : "");
        var html = await GetAsync(url);
        return html is null ? null : ParseListing(html, gref);
    }

    /// <summary>
    /// 解析一个画廊列表页里的全部缩略图条目。
    ///
    /// 站点有两套版式，这里一并吃下：匿名访问给雪碧图（title/style 挂在 &lt;div&gt; 上），
    /// 登录并把画廊版式设成大缩略图则是逐张 &lt;img src&gt;。两者都带 title="Page N: 文件名"，
    /// 故先按标签整块取出，再从块里挑 title / src / style。
    /// </summary>
    private static List<EhImagePage> ParseListing(string html, EhGalleryRef gref)
    {
        // 页码 -> 图片页 key（两套版式的链接形状一致）
        var keys = new Dictionary<int, string>();
        foreach (Match m in ImagePagePattern.Matches(html))
        {
            if (!long.TryParse(m.Groups[2].Value, out var gid) || gid != gref.Gid)
                continue;   // 页面上还有「相关画廊」的链接，只认本画廊的
            if (int.TryParse(m.Groups[3].Value, out var index))
                keys.TryAdd(index, m.Groups[1].Value);
        }

        var list = new List<EhImagePage>();
        foreach (Match tag in ThumbTagPattern.Matches(html))
        {
            var title = ThumbTitlePattern.Match(tag.Value);
            if (!title.Success || !int.TryParse(title.Groups[1].Value, out var index))
                continue;
            if (!keys.TryGetValue(index, out var key))
                continue;

            var size = TileSizePattern.Match(tag.Value);
            var sprite = SpriteUrlPattern.Match(tag.Value);
            // 逐张缩略图的 <img src>；雪碧图版式下没有 src，此处为空
            var src = tag.Value.Contains("<img", StringComparison.OrdinalIgnoreCase) &&
                      ThumbSrcPattern.Match(tag.Value) is { Success: true } sm
                ? System.Net.WebUtility.HtmlDecode(sm.Groups[1].Value)
                : "";

            list.Add(new EhImagePage
            {
                Index = index,
                Url = $"{Origin}/s/{key}/{gref.Gid}-{index}",
                FileName = System.Net.WebUtility.HtmlDecode(title.Groups[2].Value),
                Thumb = src,
                SpriteUrl = sprite.Success && src.Length == 0
                    ? System.Net.WebUtility.HtmlDecode(sprite.Groups[1].Value) : "",
                SpriteX = sprite.Success && int.TryParse(sprite.Groups[2].Value, out var x) ? Math.Abs(x) : 0,
                TileW = size.Success && int.TryParse(size.Groups[1].Value, out var w) ? w : 0,
                TileH = size.Success && int.TryParse(size.Groups[2].Value, out var h) ? h : 0,
            });
        }
        return list.OrderBy(x => x.Index).ToList();
    }

    /// <summary>
    /// 列出画廊内每张图的图片页地址（按页码升序）。
    ///
    /// 画廊页一页只列固定张数（站点默认 20），故要按 ?p=0,1,2… 逐页翻，翻到不再出现新页码为止。
    /// 每页同时能抓到缩略图 title 里的「Page N: 原始文件名」，文件名用来定扩展名。
    /// expected 传元数据里的张数，用来提前结束与校验；传 0 则一直翻到没有新内容。
    /// </summary>
    public static async Task<List<EhImagePage>> GetImagePagesAsync(
        EhGalleryRef gref, int expected = 0, Action<int>? onProgress = null, int maxPages = 200)
    {
        var pages = new Dictionary<int, EhImagePage>();
        for (var p = 0; p < maxPages; p++)
        {
            var batch = await GetListingPageAsync(gref, p);
            if (batch is null)
                break;

            var before = pages.Count;
            foreach (var one in batch)
                pages.TryAdd(one.Index, one);
            onProgress?.Invoke(pages.Count);

            if (pages.Count == before)
                break;                       // 这一页没有新图，说明已经翻到尾
            if (expected > 0 && pages.Count >= expected)
                break;                       // 已凑齐元数据里登记的张数
        }
        return pages.Values.OrderBy(x => x.Index).ToList();
    }

    // ---------- 图片直链 ----------

    private static readonly Regex ImgSrcPattern =
        new(@"<img[^>]+id=""img""[^>]+src=""([^""]+)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex FullImgPattern =
        new(@"href=""([^""]*fullimg[^""]*)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex NlTokenPattern =
        new(@"onclick=""return nl\('([^']+)'\)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex OriginalNamePattern =
        new(@"<div>([^<>]+\.\w{2,4})\s*::", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// 解析一张图的直链。
    ///
    /// 站点给的图片有两档：页面上显示的那张（按浏览设置缩放过，默认 1280px 宽）和原图
    /// （页面上的「Download original」链接，即 fullimg.php）。要原图时优先取后者，
    /// 没有这个链接说明显示的就是原图本身。见 <see cref="AppConfig.EhentaiOriginal"/>。
    ///
    /// **fullimg 必须登录**：未登录时它不报错，而是回 HTTP 200 + 登录页 HTML（约 1.3KB）。
    /// 照着存就会把登录页当图片写进作品目录并标记完成——整本画廊全是 1.3KB 的 HTML，
    /// 却一路"成功"入库。故没有 cookie 时一律退回显示图（它匿名可取，实测 image/webp 正常）。
    /// 下载端另有一道 Content-Type 兜底，防的是 cookie 中途失效，见 DownloadEngine 的 notimage 分支。
    ///
    /// 直链所在的图片服务器有时会临时掉线（页面上会给一个 nl 令牌用来换一台机器），
    /// 也可能因为本 IP 的看图额度用尽而只给出 509 占位图——后者重试无益，直接报 limit。
    /// </summary>
    public static async Task<EhImageLink> ResolveImageAsync(string pageUrl, bool original)
    {
        string? nl = null;
        // 换服务器最多试三轮：一轮一台，再不行就是这张图本身有问题
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var url = nl is null ? pageUrl : pageUrl + (pageUrl.Contains('?') ? "&" : "?") + "nl=" + nl;
            var html = await GetAsync(url);
            if (html is null)
                return new EhImageLink("", "", "fetch");

            if (IsLimitPage(html))
                return new EhImageLink("", "", LimitError);

            var m = ImgSrcPattern.Match(html);
            if (!m.Success)
                return new EhImageLink("", "", "parse");
            var src = System.Net.WebUtility.HtmlDecode(m.Groups[1].Value);

            // 509.gif 是「本图暂时超额」的占位图，换一台服务器再试
            if (src.Contains("/509.gif", StringComparison.OrdinalIgnoreCase) ||
                src.Contains("509s.gif", StringComparison.OrdinalIgnoreCase))
            {
                nl = NlTokenPattern.Match(html) is { Success: true } nm ? nm.Groups[1].Value : null;
                if (nl is null)
                    return new EhImageLink("", "", LimitError);
                continue;
            }

            var name = OriginalNamePattern.Match(html) is { Success: true } om
                ? System.Net.WebUtility.HtmlDecode(om.Groups[1].Value).Trim()
                : "";

            if (original && HasCookie && FullImgPattern.Match(html) is { Success: true } fm)
            {
                var full = System.Net.WebUtility.HtmlDecode(fm.Groups[1].Value);
                if (full.StartsWith('/'))
                    full = Origin + full;
                return new EhImageLink(full, name, null);
            }
            return new EhImageLink(src, name, null);
        }
        return new EhImageLink("", "", LimitError);
    }

    /// <summary>页面是否在说「本 IP 的看图额度已用尽」（站点用整页文案提示，不是 HTTP 错误码）。</summary>
    private static bool IsLimitPage(string html) =>
        html.Contains("exceeded your image viewing limits", StringComparison.OrdinalIgnoreCase) ||
        html.Contains("temporarily banned", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 取站上的一张图（封面/缩略图）。cookie 与 Referer 照常带上——里站连缩略图都要认登录态。
    /// 客户端由调用方持有并复用（一页几十张卡片，逐张新建会把端口耗尽）。
    /// </summary>
    public static async Task<byte[]> GetImageBytesAsync(HttpClient client, string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyHeaders(request);
        using var resp = await client.SendAsync(request);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsByteArrayAsync();
    }

    /// <summary>供下载线程调用的同步版（下载引擎全程同步 IO，见其设计注释）。</summary>
    internal static EhImageLink ResolveImage(string pageUrl, bool original) =>
        ResolveImageAsync(pageUrl, original).GetAwaiter().GetResult();

    // ---------- 杂项 ----------

    /// <summary>按图片看待的扩展名（命名与封面定位共用）。</summary>
    internal static readonly string[] ImageExts =
        [".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".avif"];

    /// <summary>
    /// 按 magic bytes 判断已下载图片的真实格式，返回 ".jpg"/".png"/".webp"/".gif"；认不出返回空串。
    ///
    /// 站点的显示图是重新编码过的，实测 .png 原图常被投递成 WebP、部分 .jpg 也是，
    /// 而页面上登记的原始文件名仍写着原格式（且请求带什么 Accept 都不改变投递格式）。
    /// 所以落盘扩展名只能以内容为准，否则会出现「001.jpg 里装着 WebP」。
    /// 认不出时返回空串而不是瞎猜一个，调用方据此保持原名不动。
    /// </summary>
    internal static string SniffImageExtension(string filePath)
    {
        byte[] head;
        try
        {
            using var fs = File.OpenRead(filePath);
            head = new byte[12];
            if (fs.Read(head, 0, head.Length) < head.Length)
                return "";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "";
        }
        return head switch
        {
            [0xFF, 0xD8, ..] => ".jpg",
            [0x89, 0x50, 0x4E, 0x47, ..] => ".png",
            [0x52, 0x49, 0x46, 0x46, _, _, _, _, 0x57, 0x45, 0x42, 0x50, ..] => ".webp",
            [0x47, 0x49, 0x46, ..] => ".gif",
            _ => "",
        };
    }

    /// <summary>从文件名/URL 猜扩展名，猜不出按 .jpg 算（站上绝大多数是 jpg）。</summary>
    internal static string ExtensionOf(string nameOrUrl)
    {
        var ext = Path.GetExtension(nameOrUrl.Split('?')[0]).ToLowerInvariant();
        return ImageExts.Contains(ext) ? ext : ".jpg";
    }
}
