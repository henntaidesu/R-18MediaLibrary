using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using R18MediaLibrary.Core;

namespace R18MediaLibrary.Services;

/// <summary>
/// 内嵌的外部访问 HTTP 服务：把媒体库（已品悦作品）以响应式网页的形式暴露给手机/电脑浏览器。
///
/// 不用 <see cref="HttpListener"/>：Windows 下监听非 localhost 地址需要管理员级 URL-ACL 预留，
/// 普通用户无法直接对外开放。这里用裸 <see cref="TcpListener"/> 自行实现极简 HTTP/1.1，
/// 可无特权绑定 0.0.0.0，零额外依赖。所有 DB 读写沿用 <see cref="Db"/>（WAL，并发读安全）。
///
/// 路由全部镜像 <c>Views/MediaLibPage</c> 的查询：媒体库→社团→作品→详情，外加标签/作品形式/收藏/搜索/排序/筛选。
/// </summary>
public static class WebServer
{
    private static TcpListener? _listener;
    private static Thread? _acceptThread;
    private static volatile bool _running;
    private static string _expectedToken = "";   // SHA256(password) 的十六进制；密码为空时不鉴权
    private static bool _authRequired;
    private static readonly object Sync = new();
    private static readonly object LibSync = new();   // 媒体库配置读改写串行化（并发请求安全）

    public static bool IsRunning => _running;
    public static int RunningPort { get; private set; }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly Dictionary<string, string> AgeMap = new()
    {
        ["1"] = "全年龄", ["2"] = "R-15", ["3"] = "R-18",
    };

    // 详情页可点击筛选字段：label 对应的 works 列（白名单，防 SQL 注入）。
    private static readonly HashSet<string> FilterCols =
        ["maker_name", "series", "scenario", "illust", "voice_actor", "age_category"];

    private static readonly string[] ImageExts = [".jpg", ".jpeg", ".png", ".gif", ".webp"];

    // 搜索页：番号 → DL API 作品数据缓存，供加入下载时写 works 表（避免重复请求）。
    private static readonly ConcurrentDictionary<string, DlWork?> SearchCache = new();
    // 已尝试补齐元数据（作品名等）的番号，避免下载页每秒轮询重复拉取 DL API
    private static readonly ConcurrentDictionary<string, byte> MetaBackfilled = new();
    // 自动下载"搜索可用下载连接"串行闸：同一时间只解析一个作品，其余排队等待（占位行已入库，故排队中的作品仍显示在下载列表）
    private static readonly SemaphoreSlim ResolveGate = new(1, 1);

    private static readonly Regex WorkIdRe = new(@"^(?:RJ|BJ|VJ)\d+$", RegexOptions.Compiled);
    private static readonly Regex MakerIdRe = new(@"^RG\d+$", RegexOptions.Compiled);

    // download_list.status → (显示文本, 颜色)
    private static readonly Dictionary<string, (string Text, string Color)> StatusMap = new()
    {
        ["0"] = ("等待下载", "#facc15"),
        ["3"] = ("下载中", "#60a5fa"),
        ["1"] = ("已完成", "#4ade80"),
        ["2"] = ("解析失败", "#f87171"),
        ["4"] = ("已暂停", "#9aa4b2"),
        ["5"] = ("搜索可用下载连接", "#facc15"),
        ["6"] = ("无可用下载连接", "#f87171"),
    };

    // 设置页可写入的 section/key 白名单（web_server 段不允许从网页改，避免自我锁死）。
    private static readonly HashSet<string> SettingsWhitelist =
    [
        "downpath/downpath",
        "down_list/auto_download", "down_list/auto_unzip", "down_list/download_processes",
        "down_list/min_speed", "down_list/speed_limit",
        "free_host/enabled", "free_host/interval",
        "proxy/openproxy", "proxy/host", "proxy/port", "proxy/type",
        "debrid/api_key", "language/lang", "loglevel/level", "encoding/encoding",
        "unzip/passwords",
        "image_host/enabled", "image_host/base_url", "image_host/project", "image_host/token",
    ];

    // ---------- 生命周期 ----------

    /// <summary>按指定端口/密码启动服务；已在运行则先停止。返回是否成功及错误信息。</summary>
    public static (bool Ok, string? Error) Start(int port, string password)
    {
        lock (Sync)
        {
            Stop();
            _authRequired = password.Length > 0;
            _expectedToken = _authRequired ? Sha256Hex(password) : "";
            try
            {
                _listener = new TcpListener(IPAddress.Any, port);
                _listener.Start();
            }
            catch (Exception e) when (e is SocketException or IOException)
            {
                _listener = null;
                Logger.Error($"外部访问启动失败（端口 {port}）：{e.Message}");
                return (false, e.Message);
            }
            _running = true;
            RunningPort = port;
            _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "WebServerAccept" };
            _acceptThread.Start();
            Logger.Info($"外部访问已开启：端口 {port}，鉴权 {(_authRequired ? "开" : "关")}");
            return (true, null);
        }
    }

    /// <summary>按当前配置启动（设置页/启动时调用）。</summary>
    public static (bool Ok, string? Error) StartFromConfig() =>
        Start(AppConfig.WebPort, AppConfig.WebPassword);

    public static void Stop()
    {
        lock (Sync)
        {
            if (!_running && _listener == null)
                return;
            _running = false;
            try
            {
                _listener?.Stop();
            }
            catch (Exception)
            {
                // 关闭监听器时的异常忽略
            }
            _listener = null;
            RunningPort = 0;
        }
    }

    private static void AcceptLoop()
    {
        var listener = _listener;
        while (_running && listener != null)
        {
            TcpClient client;
            try
            {
                client = listener.AcceptTcpClient();
            }
            catch (Exception)
            {
                break;  // 监听器已关闭
            }
            // 交给线程池起步（同步读请求头不占用 accept 线程），进入外部网络调用后 await 又会让出线程
            _ = Task.Run(() => HandleClientAsync(client));
        }
    }

    // ---------- 请求处理 ----------

    private static async Task HandleClientAsync(TcpClient client)
    {
        try
        {
            using (client)
            {
                client.ReceiveTimeout = 15000;
                client.SendTimeout = 30000;
                using var stream = client.GetStream();
                var request = ReadRequest(stream);
                if (request == null)
                    return;
                // 路由异步到底：处理链路中的外部网络调用（debrid/dlsite/asmr）await 让出线程池线程，
                // 多客户端并发访问时线程不被阻塞、响应不排队。
                await RouteAsync(stream, request);
            }
        }
        catch (Exception e)
        {
            if (e is not (IOException or SocketException or ObjectDisposedException))
                Logger.Error($"外部访问请求异常：{e.Message}");
        }
    }

    private sealed class Request
    {
        public string Method = "GET";
        public string Path = "/";
        public Dictionary<string, string> Query = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Headers = new(StringComparer.OrdinalIgnoreCase);
        public byte[] Body = [];
    }

    private static Request? ReadRequest(NetworkStream stream)
    {
        // 读到 \r\n\r\n 为止即为请求头
        var header = new List<byte>(1024);
        int b;
        while ((b = stream.ReadByte()) != -1)
        {
            header.Add((byte)b);
            var n = header.Count;
            if (n >= 4 && header[n - 4] == 13 && header[n - 3] == 10 &&
                header[n - 2] == 13 && header[n - 1] == 10)
                break;
            if (n > 65536)
                return null;  // 请求头过大
        }
        if (header.Count == 0)
            return null;

        var text = Encoding.ASCII.GetString(header.ToArray());
        var lines = text.Split("\r\n");
        var requestLine = lines[0].Split(' ');
        if (requestLine.Length < 2)
            return null;

        var req = new Request { Method = requestLine[0].ToUpperInvariant() };
        var rawUrl = requestLine[1];
        var qIndex = rawUrl.IndexOf('?');
        if (qIndex >= 0)
        {
            req.Path = UrlDecode(rawUrl[..qIndex]);
            ParseQuery(rawUrl[(qIndex + 1)..], req.Query);
        }
        else
        {
            req.Path = UrlDecode(rawUrl);
        }

        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length == 0)
                continue;
            var colon = line.IndexOf(':');
            if (colon <= 0)
                continue;
            req.Headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        if (req.Headers.TryGetValue("Content-Length", out var clv) &&
            int.TryParse(clv, out var len) && len is > 0 and <= 1_048_576)
        {
            var body = new byte[len];
            var read = 0;
            while (read < len)
            {
                var got = stream.Read(body, read, len - read);
                if (got <= 0)
                    break;
                read += got;
            }
            req.Body = body;
        }
        return req;
    }

    private static void ParseQuery(string query, Dictionary<string, string> into)
    {
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq < 0)
                into[UrlDecode(pair)] = "";
            else
                into[UrlDecode(pair[..eq])] = UrlDecode(pair[(eq + 1)..]);
        }
    }

    private static string UrlDecode(string s) => Uri.UnescapeDataString(s.Replace('+', ' '));

    // ---------- 路由 ----------

    private static async Task RouteAsync(NetworkStream stream, Request req)
    {
        // 防 DNS 重绑定：仅允许通过 IP 直连或 localhost 访问（本程序对外只用 IP 地址宣告）。
        // 用域名访问一律拒绝——恶意网站借 DNS 重绑定把自身域名指到本机 LAN IP 时会被挡在此处。
        if (!HostAllowed(req))
        {
            WriteJson(stream, 403, new { error = "forbidden host" });
            return;
        }

        var path = req.Path;
        if (path == "/")
            path = "/index.html";
        // 静态资源（外壳 / 样式 / 各页面 JS 模块）在鉴权前放行：登录页本身要靠这些 CSS/JS 才能渲染
        if (WebAssets.TryGet(path, out var assetBytes, out var assetType))
        {
            // SPA 的 html/css/js 是嵌到 exe 里的，随程序版本一起变；文件名又不带版本号，
            // 浏览器一缓存就会在升级后继续跑旧页面（看不到新加的设置项）。禁掉缓存，局域网重取不值钱。
            WriteBytes(stream, 200, "OK", assetType, assetBytes, ("Cache-Control", "no-store"));
            return;
        }
        if (path == "/api/state")
        {
            var user = SessionUser(req);
            WriteJson(stream, 200, new
            {
                needLogin = AuthRequired && !Authed(req),
                password = _authRequired,                 // 登录页据此决定画不画密码框
                sso = PortalSso.Active,                   // 程序这边开了单点登录才画「门户登录」按钮
                user = user == null ? null : new { username = user.Username, display = user.DisplayName },
            });
            return;
        }
        if (path == "/api/login")
        {
            HandleLogin(stream, req);
            return;
        }
        if (path == "/api/logout")
        {
            WriteJson(stream, 200, new { ok = true },
                ("Set-Cookie", "dasd_auth=; Path=/; Max-Age=0; HttpOnly; SameSite=Lax"),
                ("Set-Cookie", $"{PortalSso.SessionCookie}=; Path=/; Max-Age=0; HttpOnly; SameSite=Lax"));
            return;
        }
        if (path is "/api/sso/login" or "/api/sso/callback")
        {
            // 程序这边没开单点登录（或没配齐）时这两条路就当不存在
            if (!PortalSso.Active)
            {
                WriteJson(stream, 404, new { error = "sso disabled" });
                return;
            }
            if (path == "/api/sso/login")
                SsoLogin(stream, req);
            else
                await SsoCallbackAsync(stream, req);
            return;
        }

        // 其余 /api 与资源端点需鉴权
        if (!Authed(req))
        {
            WriteJson(stream, 401, new { error = "unauthorized" });
            return;
        }

        try
        {
            switch (path)
            {
                case "/api/libs": ApiLibs(stream); break;
                case "/api/makers": ApiMakers(stream, req); break;
                case "/api/makeralias": ApiMakerAlias(stream, req); break;
                case "/api/makersettings": ApiMakerSettings(stream, req); break;
                case "/api/works": ApiWorks(stream, req); break;
                case "/api/genreworks": ApiGenreWorks(stream, req); break;
                case "/api/libworks": ApiLibWorks(stream, req); break;
                case "/api/genres": ApiGenres(stream); break;
                case "/api/types": ApiTypes(stream); break;
                case "/api/favorites": ApiFavorites(stream, req); break;
                case "/api/filter": ApiFilter(stream, req); break;
                case "/api/detail": ApiDetail(stream, req); break;
                case "/api/toggle": ApiToggle(stream, req); break;
                case "/api/searchworks": ApiSearchWorks(stream, req); break;
                case "/api/movework": await ApiMoveWorkAsync(stream, req); break;
                // 媒体库里彻底删除作品（连文件一起）；/api/deletework 是下载列表那个，别混
                case "/api/delwork": await ApiDeleteLibWorkAsync(stream, req); break;
                case "/api/cover": ApiCover(stream, req); break;
                case "/api/asset": ApiAsset(stream, req); break;
                case "/api/files": ApiFiles(stream, req); break;
                case "/api/file": ApiFile(stream, req); break;
                case "/api/runexe": ApiRunExe(stream, req); break;
                case "/api/stream": await ApiStreamAsync(stream, req); break;
                // 搜索
                case "/api/search": await ApiSearchAsync(stream, req); break;
                case "/api/maker": await ApiMakerAsync(stream, req); break;
                case "/api/catalog": await ApiCatalogAsync(stream, req); break;
                case "/api/asscan": await ApiAsScanAsync(stream, req); break;
                case "/api/thumb": await ApiThumbAsync(stream, req); break;
                case "/api/posturls": await ApiPostUrlsAsync(stream, req); break;
                case "/api/checkhost": await ApiCheckHostAsync(stream, req); break;
                case "/api/downtargets": ApiDownTargets(stream); break;
                case "/api/enqueue": ApiEnqueue(stream, req); break;
                case "/api/autodownload": ApiAutoDownload(stream, req); break;
                case "/api/combine": await ApiCombineAsync(stream, req); break;
                // 下载 / 已下载
                case "/api/downloads": ApiDownloads(stream); break;
                case "/api/usage": await ApiUsageAsync(stream); break;
                case "/api/engine": ApiEngine(stream, req); break;
                case "/api/reparse": ApiReparse(stream, req); break;
                case "/api/reparseall": ApiReparseAll(stream); break;
                case "/api/pausework": ApiPauseWork(stream, req); break;
                case "/api/resumework": ApiResumeWork(stream, req); break;
                case "/api/deletework": ApiDeleteWork(stream, req); break;
                case "/api/research": ApiResearch(stream, req); break;
                case "/api/clearall": ApiClearAll(stream); break;
                case "/api/clearnolink": ApiClearNoLink(stream); break;
                case "/api/downloaded": ApiDownloaded(stream); break;
                case "/api/mark": ApiMark(stream, req); break;
                case "/api/dislike": ApiDislike(stream, req); break;
                // FANBOX（pawchive）
                case "/api/fanbox/artists": await ApiFanboxArtistsAsync(stream, req); break;
                case "/api/fanbox/posts": await ApiFanboxPostsAsync(stream, req); break;
                case "/api/fanbox/post": await ApiFanboxPostAsync(stream, req); break;
                case "/api/fanbox/enqueue": await ApiFanboxEnqueueAsync(stream, req); break;
                case "/api/fanbox/image": await ApiFanboxImageAsync(stream, req); break;
                // 使用帮助（各页面的 ? 按钮）：按章节拆好的使用手册 + 其中的截图
                case "/api/help": ApiHelp(stream, req); break;
                case "/api/help/img": ApiHelpImage(stream, req); break;
                // 账号访问（FANBOX / pixiv / E-Hentai 的登录 cookie）
                case "/api/accounts": ApiAccounts(stream); break;
                case "/api/account/check": await ApiAccountCheckAsync(stream, req); break;
                case "/api/account/logout": ApiAccountLogout(stream, req); break;
                // FANBOX 作家监控（有新投稿就自动下载）
                case "/api/fanbox/watches": ApiFanboxWatches(stream); break;
                case "/api/fanbox/watch/add": await ApiFanboxWatchAddAsync(stream, req); break;
                case "/api/fanbox/watch/update": ApiFanboxWatchUpdate(stream, req); break;
                case "/api/fanbox/watch/check": ApiFanboxWatchCheck(stream, req); break;
                case "/api/fanbox/watch/config": ApiFanboxWatchConfig(stream, req); break;
                // Pixiv 收藏监控（账号有新收藏就自动下载）
                case "/api/pixiv/watches": ApiPixivWatches(stream); break;
                case "/api/pixiv/watch/add": await ApiPixivWatchAddAsync(stream, req); break;
                case "/api/pixiv/watch/update": ApiPixivWatchUpdate(stream, req); break;
                case "/api/pixiv/watch/check": ApiPixivWatchCheck(stream, req); break;
                case "/api/pixiv/watch/config": ApiPixivWatchConfig(stream, req); break;
                case "/api/dlsite/watches": ApiDlsiteWatches(stream); break;
                case "/api/dlsite/watch/add": await ApiDlsiteWatchAddAsync(stream, req); break;
                case "/api/dlsite/watch/update": ApiDlsiteWatchUpdate(stream, req); break;
                case "/api/dlsite/watch/check": ApiDlsiteWatchCheck(stream, req); break;
                case "/api/dlsite/watch/config": ApiDlsiteWatchConfig(stream, req); break;

                case "/api/eh/search": await ApiEhSearchAsync(stream, req); break;
                case "/api/eh/gallery": await ApiEhGalleryAsync(stream, req); break;
                case "/api/eh/page": await ApiEhPageImageAsync(stream, req); break;
                case "/api/eh/enqueue": await ApiEhEnqueueAsync(stream, req); break;
                case "/api/eh/image": await ApiEhImageAsync(stream, req); break;

                case "/api/px/search": await ApiPixivSearchAsync(stream, req); break;
                case "/api/px/artwork": await ApiPixivArtworkAsync(stream, req); break;
                case "/api/px/enqueue": await ApiPixivEnqueueAsync(stream, req); break;
                case "/api/px/image": await ApiPixivImageAsync(stream, req); break;
                // 设置
                case "/api/settings": if (req.Method == "POST") ApiSettingsWrite(stream, req); else ApiSettings(stream); break;
                case "/api/debridtest": await ApiDebridTestAsync(stream, req); break;
                case "/api/debridkey": if (req.Method == "POST") ApiDebridKeyReveal(stream, req); else WriteJson(stream, 400, new { ok = false }); break;
                case "/api/imagehost/test": await ApiImageHostTestAsync(stream, req); break;
                case "/api/imagehost/migrate": ApiImageHostMigrate(stream); break;
                case "/api/imagehost/status": ApiImageHostStatus(stream); break;
                // 媒体库管理
                case "/api/medialibs": ApiMediaLibs(stream); break;
                case "/api/lib/create": ApiLibCreate(stream, req); break;
                case "/api/lib/delete": ApiLibDelete(stream, req); break;
                case "/api/lib/update": ApiLibUpdate(stream, req); break;
                case "/api/lib/addfolder": ApiLibAddFolder(stream, req); break;
                case "/api/lib/removefolder": ApiLibRemoveFolder(stream, req); break;
                case "/api/lib/editfolder": ApiLibEditFolder(stream, req); break;
                case "/api/lib/scan": ApiLibScan(stream, req); break;
                case "/api/lib/scanall": ApiLibScanAll(stream); break;
                case "/api/lib/scanstatus": ApiLibScanStatus(stream); break;
                default: WriteJson(stream, 404, new { error = "not found" }); break;
            }
        }
        catch (Exception e)
        {
            Logger.Error($"外部访问处理 {path} 出错：{e.Message}");
            WriteJson(stream, 500, new { error = "server error" });
        }
    }

    /// <summary>要不要登录：设了访问密码，或开了门户单点登录（开着单点登录却放任匿名访问就没意义了）。</summary>
    private static bool AuthRequired => _authRequired || PortalSso.Active;

    private static bool Authed(Request req)
    {
        if (!AuthRequired)
            return true;
        if (_authRequired && GetCookie(req, "dasd_auth") is { } token && TokensEqual(token, _expectedToken))
            return true;
        return SessionUser(req) != null;
    }

    /// <summary>单点登录进来的用户；没开单点登录时一律不认，关掉开关即让这类会话全部失效。</summary>
    private static PortalSso.WebUser? SessionUser(Request req) =>
        PortalSso.Active && GetCookie(req, PortalSso.SessionCookie) is { Length: > 0 } s ? PortalSso.UserFromSession(s) : null;

    private static string? GetCookie(Request req, string name)
    {
        if (!req.Headers.TryGetValue("Cookie", out var cookie))
            return null;
        foreach (var part in cookie.Split(';'))
        {
            var kv = part.Trim();
            if (kv.Length > name.Length && kv[name.Length] == '=' && kv.StartsWith(name, StringComparison.Ordinal))
                return kv[(name.Length + 1)..];
        }
        return null;
    }

    // ---------- 门户单点登录（逻辑在 PortalSso） ----------

    /// <summary>
    /// 浏览器整页跳过来；base 是页面所在目录（经门户代理时是代理底下那个地址）。
    /// 回 303 去门户授权端点，同时种下 state/nonce/PKCE 的临时 Cookie。
    /// </summary>
    private static void SsoLogin(NetworkStream stream, Request req)
    {
        var baseUrl = PortalSso.NormalizeBase(req.Query.GetValueOrDefault("base"))
                      ?? $"http://{req.Headers.GetValueOrDefault("Host")}/";
        var (url, state) = PortalSso.Begin(baseUrl);
        WriteRedirect(stream, url,
            ("Set-Cookie", $"{PortalSso.StateCookie}={state}; Path=/; Max-Age=600; HttpOnly; SameSite=Lax"));
    }

    /// <summary>
    /// 门户跳回来。成功种会话 Cookie，失败把原因带在 sso_error 里交给登录页显示。
    /// 跳回首页用相对地址 <c>../../</c>：直连时落到 /，经门户代理时落到 /api/proxy/&lt;卡片&gt;/，两种都对。
    /// </summary>
    private static async Task SsoCallbackAsync(NetworkStream stream, Request req)
    {
        const string home = "../../";
        var clearState = ("Set-Cookie", $"{PortalSso.StateCookie}=; Path=/; Max-Age=0; HttpOnly; SameSite=Lax");
        var error = req.Query.GetValueOrDefault("error");
        if (!string.IsNullOrEmpty(error))
        {
            var desc = req.Query.GetValueOrDefault("error_description") ?? "";
            WriteRedirect(stream, home + "?sso_error=" + Uri.EscapeDataString($"{error} {desc}".Trim()), clearState);
            return;
        }
        try
        {
            var user = await PortalSso.CompleteAsync(req.Query.GetValueOrDefault("code") ?? "",
                req.Query.GetValueOrDefault("state") ?? "", GetCookie(req, PortalSso.StateCookie) ?? "");
            Logger.Info($"门户单点登录：{user.Username} 登录外部访问");
            WriteRedirect(stream, home, clearState,
                ("Set-Cookie", $"{PortalSso.SessionCookie}={PortalSso.MakeSession(user)}; Path=/; " +
                               $"Max-Age={PortalSso.SessionMaxAge}; HttpOnly; SameSite=Lax"));
        }
        catch (PortalSso.SsoException e)
        {
            Logger.Warning($"门户单点登录失败：{e.Message}");
            WriteRedirect(stream, home + "?sso_error=" + Uri.EscapeDataString(e.Message), clearState);
        }
    }

    private static void WriteRedirect(NetworkStream stream, string location, params (string Key, string Value)[] extra) =>
        WriteBytes(stream, 303, "See Other", "text/plain; charset=utf-8", [],
            [("Location", location), ("Cache-Control", "no-store"), .. extra]);

    // 登录暴力破解限流：全局共享一个密码，故失败次数按整体计。达到阈值即锁定一个窗口。
    private const int MaxLoginFails = 10;
    private static readonly TimeSpan LockoutWindow = TimeSpan.FromMinutes(1);
    private static int _loginFails;
    private static DateTime _lockoutUntil = DateTime.MinValue;

    private static void HandleLogin(NetworkStream stream, Request req)
    {
        lock (Sync)
        {
            if (DateTime.UtcNow < _lockoutUntil)
            {
                WriteJson(stream, 429, new { ok = false, error = "尝试过于频繁，请稍后再试" });
                return;
            }
        }
        var password = "";
        try
        {
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(req.Body));
            if (doc.RootElement.TryGetProperty("password", out var p))
                password = p.GetString() ?? "";
        }
        catch (Exception)
        {
            password = "";
        }
        if (!_authRequired && PortalSso.Active)
        {
            // 没设访问密码、只开了单点登录：密码这条路是关着的
            WriteJson(stream, 401, new { ok = false });
            return;
        }
        if (!_authRequired || TokensEqual(Sha256Hex(password), _expectedToken))
        {
            lock (Sync) { _loginFails = 0; }
            // 令牌写入 Cookie（HttpOnly，长期有效：登录后不再过期）
            WriteJson(stream, 200, new { ok = true },
                ("Set-Cookie", $"dasd_auth={_expectedToken}; Path=/; Max-Age=315360000; HttpOnly; SameSite=Lax"));
        }
        else
        {
            lock (Sync)
            {
                if (++_loginFails >= MaxLoginFails)
                {
                    _lockoutUntil = DateTime.UtcNow + LockoutWindow;
                    _loginFails = 0;
                }
            }
            WriteJson(stream, 401, new { ok = false });
        }
    }

    /// <summary>恒定时间比较两个令牌/哈希字符串，避免通过响应时间侧信道逐字符猜测。</summary>
    private static bool TokensEqual(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));

    // ---------- 安全：Host 校验与 SSRF 目标过滤 ----------

    /// <summary>仅允许 IP 直连或 localhost 访问，用域名访问一律拒绝（防 DNS 重绑定）。</summary>
    private static bool HostAllowed(Request req)
    {
        if (!req.Headers.TryGetValue("Host", out var host) || host.Length == 0)
            return false;
        var hostname = host;
        if (hostname.StartsWith('['))   // IPv6 字面量形如 [::1]:8080
        {
            var end = hostname.IndexOf(']');
            if (end > 0) hostname = hostname[1..end];
        }
        else
        {
            var colon = hostname.LastIndexOf(':');
            if (colon >= 0) hostname = hostname[..colon];
        }
        if (hostname.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return true;
        return IPAddress.TryParse(hostname, out _);
    }

    /// <summary>SSRF 防护：目标必须是 http(s) 且解析后的 IP 不落在环回/私有/链路本地网段，
    /// 否则拒绝——避免把本服务当作探测内网或读取云元数据端点的跳板。</summary>
    private static bool IsSafeFetchUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return false;
        try
        {
            IPAddress[] addrs = IPAddress.TryParse(uri.Host, out var direct)
                ? [direct]
                : Dns.GetHostAddresses(uri.DnsSafeHost);
            if (addrs.Length == 0)
                return false;
            foreach (var ip in addrs)
                if (IsPrivateOrLocal(ip))
                    return false;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsPrivateOrLocal(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any))
            return true;
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] switch
            {
                0 or 10 or 127 => true,                     // 0/8、10/8、127/8
                169 => b[1] == 254,                         // 169.254/16 链路本地
                172 => b[1] is >= 16 and <= 31,             // 172.16/12
                192 => b[1] == 168,                         // 192.168/16
                100 => b[1] is >= 64 and <= 127,            // 100.64/10 运营商级 NAT
                _ => false,
            };
        }
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv4MappedToIPv6)
                return IsPrivateOrLocal(ip.MapToIPv4());
            return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal;
        }
        return true;   // 未知地址族一律视为不安全
    }

    // ---------- API：卡片层级 ----------

    private static void ApiLibs(NetworkStream stream)
    {
        var counts = new Dictionary<string, long>();
        var rows = Db.Select(
            "SELECT \"library\", COUNT(*) FROM \"works\" WHERE \"state\" = '已品悦' GROUP BY \"library\"");
        if (rows != null)
            foreach (var row in rows)
                counts[row[0] as string ?? ""] = Convert.ToInt64(row[1]);
        var libs = AppConfig.ReadMediaLibs().Select(lib => new
        {
            name = lib.Name,
            works = counts.GetValueOrDefault(lib.Name),
            folders = lib.Folders.Count,
            view = lib.View,
        });
        WriteJson(stream, 200, new { libs });
    }

    private static void ApiMakers(NetworkStream stream, Request req)
    {
        DlsiteMakerIcon.Kick();   // 后台补齐还没查过的社团头像（已查过的不会重复请求）
        var lib = req.Query.GetValueOrDefault("lib");
        var genre = req.Query.GetValueOrDefault("genre");
        var type = req.Query.GetValueOrDefault("type");
        var order = MakerOrderClause(GetInt(req, "sort"));
        List<object?[]>? rows;
        if (!string.IsNullOrEmpty(genre))
            rows = Db.Select(
                "SELECT w.\"maker_name\", COUNT(*), " + FanboxService.MakerIdExpr("w.") + ", " + DlsiteMakerIcon.MakerIdExpr("w.") + " FROM \"works\" w " +
                "JOIN \"work_genres\" g ON g.\"work_id\" = w.\"work_id\" " +
                "WHERE w.\"state\" = '已品悦' AND g.\"genre\" = @g " +
                "GROUP BY w.\"maker_name\" " + order.Replace("{p}", "w."),
                ("@g", genre));
        else if (!string.IsNullOrEmpty(type))
            rows = Db.Select(
                "SELECT \"maker_name\", COUNT(*), " + FanboxService.MakerIdExpr("") + ", " + DlsiteMakerIcon.MakerIdExpr("") + " FROM \"works\" " +
                "WHERE \"state\" = '已品悦' AND \"work_type\" = @t " +
                "GROUP BY \"maker_name\" " + order.Replace("{p}", ""),
                ("@t", type));
        else if (!string.IsNullOrEmpty(lib))
            rows = Db.Select(
                "SELECT \"maker_name\", COUNT(*), " + FanboxService.MakerIdExpr("") + ", " + DlsiteMakerIcon.MakerIdExpr("") + " FROM \"works\" " +
                "WHERE \"state\" = '已品悦' AND \"library\" = @lib " +
                "GROUP BY \"maker_name\" " + order.Replace("{p}", ""),
                ("@lib", lib));
        else
            // 顶级"作品社团"分区：不限媒体库，聚合全部社团
            rows = Db.Select(
                "SELECT \"maker_name\", COUNT(*), " + FanboxService.MakerIdExpr("") + ", " + DlsiteMakerIcon.MakerIdExpr("") + " FROM \"works\" " +
                "WHERE \"state\" = '已品悦' " +
                "GROUP BY \"maker_name\" " + order.Replace("{p}", ""));
        var makers = (rows ?? []).Select(r =>
        {
            // fanbox 作家头像来自 pawchive，DLsite 社团头像来自 ci-en；都没有就留空
            var fanboxId = r.Length > 2 ? r[2] as string ?? "" : "";
            var dlsiteId = r.Length > 3 ? r[3] as string ?? "" : "";
            var (source, hosted) = MakerIcons.Resolve(fanboxId, dlsiteId);
            // pawchive 的图必须经本服务代理（浏览器直连会被防护网关按浏览器 UA 拦下）；
            // ci-en 是公开 CDN，直接给原地址即可
            var direct = source.Length == 0 ? ""
                : fanboxId.Length > 0 ? "/api/fanbox/image?url=" + Uri.EscapeDataString(source)
                : source;
            var maker = r[0] as string ?? "";
            var home = MakerHomeService.Get(maker);
            return new
            {
                maker,
                // 社团主页（FANBOX 官方站 / pawchive），卡片上显示成外链；没有为空串
                fanboxUrl = home.Fanbox,
                pawchiveUrl = home.Pawchive,
                // 显示名：设过别名就用别名。分组/筛选一律仍按真名 maker，别名只进界面
                display = MakerAliasService.Display(maker),
                count = Convert.ToInt64(r[1]),
                icon = direct,
                // 已迁到图床的直接给图床地址（既不经本服务、也不回源）；没迁的为空串，前端退回 icon
                iconUrl = hosted,
                // 社团卡一律留出头像位：没有头像的社团也保持同样的版式，只是那一格什么都不画
                avatar = true,
            };
        });
        WriteJson(stream, 200, new { makers });
    }

    /// <summary>
    /// 设置/清除社团显示名映射。只改显示，works.maker_name 不动；alias 留空即取消映射。
    /// </summary>
    private static void ApiMakerAlias(NetworkStream stream, Request req)
    {
        if (req.Method != "POST")
        {
            WriteJson(stream, 405, new { error = "method not allowed" });
            return;
        }
        var maker = ReadStringField(req.Body, "maker");
        if (maker.Length == 0)
        {
            WriteJson(stream, 400, new { error = "bad request" });
            return;
        }
        MakerAliasService.Set(maker, ReadStringField(req.Body, "alias"));
        WriteJson(stream, 200, new { ok = true, display = MakerAliasService.Display(maker) });
    }

    /// <summary>
    /// 社团设置（社团卡右上角 🖊）：显示名 + FANBOX 主页 + pawchive 主页一次存。
    /// 先校验主页地址，不合法则什么都不写（免得显示名存了、主页没存）。
    /// </summary>
    private static void ApiMakerSettings(NetworkStream stream, Request req)
    {
        if (req.Method != "POST")
        {
            WriteJson(stream, 405, new { error = "method not allowed" });
            return;
        }
        var maker = ReadStringField(req.Body, "maker");
        if (maker.Length == 0)
        {
            WriteJson(stream, 400, new { error = "bad request" });
            return;
        }
        if (MakerHomeService.Set(maker, ReadStringField(req.Body, "fanbox"),
                ReadStringField(req.Body, "pawchive")) is { } err)
        {
            WriteJson(stream, 400, new { error = err });
            return;
        }
        MakerAliasService.Set(maker, ReadStringField(req.Body, "alias"));
        WriteJson(stream, 200, new { ok = true });
    }

    private static void ApiWorks(NetworkStream stream, Request req)
    {
        var lib = req.Query.GetValueOrDefault("lib");
        var maker = req.Query.GetValueOrDefault("maker") ?? "";
        var genre = req.Query.GetValueOrDefault("genre");
        var type = req.Query.GetValueOrDefault("type");
        var order = WorkOrderClause(GetInt(req, "sort"));
        // maker 为空串表示"未知社团"
        var makerCond = maker.Length == 0
            ? "({p}\"maker_name\" IS NULL OR {p}\"maker_name\" = '')"
            : "{p}\"maker_name\" = @maker";
        List<object?[]>? rows;
        if (!string.IsNullOrEmpty(genre))
            rows = Db.Select(
                "SELECT w.\"work_id\", w.\"work_name\", w.\"maker_name\", w.\"work_type\", " +
                "w.\"age_category\", w.\"cover\" FROM \"works\" w " +
                "JOIN \"work_genres\" g ON g.\"work_id\" = w.\"work_id\" " +
                "WHERE w.\"state\" = '已品悦' AND g.\"genre\" = @g AND " + makerCond.Replace("{p}", "w.") + " " +
                order.Replace("{p}", "w."),
                ("@g", genre), ("@maker", maker));
        else if (!string.IsNullOrEmpty(type))
            rows = Db.Select(
                "SELECT \"work_id\", \"work_name\", \"maker_name\", \"work_type\", \"age_category\", \"cover\" " +
                "FROM \"works\" WHERE \"state\" = '已品悦' AND \"work_type\" = @t AND " + makerCond.Replace("{p}", "") + " " +
                order.Replace("{p}", ""),
                ("@t", type), ("@maker", maker));
        else if (!string.IsNullOrEmpty(lib))
            rows = Db.Select(
                "SELECT \"work_id\", \"work_name\", \"maker_name\", \"work_type\", \"age_category\", \"cover\" " +
                "FROM \"works\" WHERE \"state\" = '已品悦' AND \"library\" = @lib AND " + makerCond.Replace("{p}", "") + " " +
                order.Replace("{p}", ""),
                ("@lib", lib), ("@maker", maker));
        else
            // 顶级"作品社团"分区：不限媒体库，仅按社团过滤
            rows = Db.Select(
                "SELECT \"work_id\", \"work_name\", \"maker_name\", \"work_type\", \"age_category\", \"cover\" " +
                "FROM \"works\" WHERE \"state\" = '已品悦' AND " + makerCond.Replace("{p}", "") + " " +
                order.Replace("{p}", ""),
                ("@maker", maker));
        WriteJson(stream, 200, new { works = WorksJson(rows) });
    }

    /// <summary>某标签下全部已品悦作品（不分社团）——作品详情页点击类型标签直接列出作品。</summary>
    private static void ApiGenreWorks(NetworkStream stream, Request req)
    {
        var genre = req.Query.GetValueOrDefault("genre") ?? "";
        var rows = Db.Select(
            "SELECT w.\"work_id\", w.\"work_name\", w.\"maker_name\", w.\"work_type\", " +
            "w.\"age_category\", w.\"cover\" FROM \"works\" w " +
            "JOIN \"work_genres\" g ON g.\"work_id\" = w.\"work_id\" " +
            "WHERE w.\"state\" = '已品悦' AND g.\"genre\" = @g " +
            WorkOrderClause(GetInt(req, "sort")).Replace("{p}", "w."),
            ("@g", genre));
        WriteJson(stream, 200, new { works = WorksJson(rows) });
    }

    /// <summary>某媒体库内全部已品悦作品（媒体库默认"显示作品"视图）。</summary>
    private static void ApiLibWorks(NetworkStream stream, Request req)
    {
        var lib = req.Query.GetValueOrDefault("lib") ?? "";
        var rows = Db.Select(
            "SELECT \"work_id\", \"work_name\", \"maker_name\", \"work_type\", \"age_category\", \"cover\" " +
            "FROM \"works\" WHERE \"state\" = '已品悦' AND \"library\" = @lib " +
            WorkOrderClause(GetInt(req, "sort")).Replace("{p}", ""),
            ("@lib", lib));
        WriteJson(stream, 200, new { works = WorksJson(rows) });
    }

    private static void ApiGenres(NetworkStream stream)
    {
        var rows = Db.Select(
            "SELECT g.\"genre\", COUNT(*) FROM \"work_genres\" g " +
            "JOIN \"works\" w ON w.\"work_id\" = g.\"work_id\" " +
            "WHERE w.\"state\" = '已品悦' GROUP BY g.\"genre\" ORDER BY COUNT(*) DESC");
        var genres = (rows ?? []).Select(r => new
        {
            genre = r[0] as string ?? "",
            count = Convert.ToInt64(r[1]),
        });
        WriteJson(stream, 200, new { genres });
    }

    private static void ApiTypes(NetworkStream stream)
    {
        var rows = Db.Select(
            "SELECT \"work_type\", COUNT(*) FROM \"works\" " +
            "WHERE \"state\" = '已品悦' AND \"work_type\" IS NOT NULL AND \"work_type\" <> '' " +
            "GROUP BY \"work_type\" ORDER BY COUNT(*) DESC");
        var types = (rows ?? []).Select(r => new
        {
            type = r[0] as string ?? "",
            count = Convert.ToInt64(r[1]),
        });
        WriteJson(stream, 200, new { types });
    }

    private static void ApiFavorites(NetworkStream stream, Request req)
    {
        var rows = Db.Select(
            "SELECT \"work_id\", \"work_name\", \"maker_name\", \"work_type\", \"age_category\", \"cover\" " +
            "FROM \"works\" WHERE \"favorite\" = '1' " + WorkOrderClause(GetInt(req, "sort")).Replace("{p}", ""));
        WriteJson(stream, 200, new { works = WorksJson(rows) });
    }

    private static void ApiFilter(NetworkStream stream, Request req)
    {
        var col = req.Query.GetValueOrDefault("col") ?? "";
        var val = req.Query.GetValueOrDefault("val") ?? "";
        if (!FilterCols.Contains(col))
        {
            WriteJson(stream, 400, new { error = "bad column" });
            return;
        }
        var cond = col == "voice_actor" ? "\"voice_actor\" LIKE @val" : $"\"{col}\" = @val";
        var value = col == "voice_actor" ? $"%{val}%" : val;
        var rows = Db.Select(
            "SELECT \"work_id\", \"work_name\", \"maker_name\", \"work_type\", \"age_category\", \"cover\" " +
            "FROM \"works\" WHERE \"state\" = '已品悦' AND " + cond + " " +
            WorkOrderClause(GetInt(req, "sort")).Replace("{p}", ""),
            ("@val", value));
        WriteJson(stream, 200, new { works = WorksJson(rows) });
    }

    private static IEnumerable<object> WorksJson(List<object?[]>? rows) =>
        (rows ?? []).Select(r =>
        {
            var id = r[0] as string ?? "";
            // 图床接管该封面时直接给出图床直链：浏览器绕开本服务与本机 HDD 取图，
            // 也省掉下面那次 File.Exists（媒体库在 HDD 上时，逐行 stat 本身就是一次唤醒）
            var hosted = ImageHostService.CoverUrl(id);
            return new
            {
                id,
                name = r[1] as string ?? "",
                maker = r[2] as string ?? "",
                type = r[3] as string ?? "",
                age = r[4]?.ToString() ?? "",
                cover = hosted != null || (r[5] != null && File.Exists(r[5] as string)),
                coverUrl = hosted,
            };
        });

    // ---------- API：详情 ----------

    private static void ApiDetail(NetworkStream stream, Request req)
    {
        var id = req.Query.GetValueOrDefault("id") ?? "";
        var rows = Db.Select(
            "SELECT \"work_id\", \"work_name\", \"maker_name\", \"sell_date\", \"series\", " +
            "\"scenario\", \"illust\", \"voice_actor\", \"age_category\", \"work_type\", " +
            "\"genre\", \"file_size\", \"intro_s\", \"folder\", \"read_flag\", \"favorite\", \"cover\" " +
            "FROM \"works\" WHERE \"work_id\" = @w", ("@w", id));
        if (rows is not { Count: > 0 })
        {
            WriteJson(stream, 404, new { error = "not found" });
            return;
        }
        var r = rows[0];
        // fanbox 投稿 / E-Hentai 画廊的目录里摊着整篇的图（动辄上百张），且没有 description.txt，
        // 进详情一律不翻目录：左侧只挂封面，要看图走「查看作品」
        var isFlat = MediaLibraryService.IsFlatImageWork(id);
        var folder = ResolveAssetFolder(id, r[13] as string);

        // 正文：按 [img:文件名] 占位标记拆成 文本/图片 块
        var body = new List<object>();
        var bodyFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var txtPath = Path.Combine(folder, DlsitePage.DescriptionTxt);
        if (!isFlat && File.Exists(txtPath))
        {
            string raw;
            try { raw = File.ReadAllText(txtPath).Trim(); }
            catch (IOException) { raw = ""; }
            var buf = new List<string>();
            foreach (var line in raw.Split('\n'))
            {
                var match = DlsitePage.BodyImageRe.Match(line.Trim());
                if (match.Success)
                {
                    if (buf.Count > 0) { body.Add(new { kind = "text", value = string.Join('\n', buf) }); buf.Clear(); }
                    var fn = match.Groups[1].Value;
                    body.Add(new { kind = "image", value = fn });
                    bodyFiles.Add(fn);
                }
                else
                {
                    buf.Add(line);
                }
            }
            if (buf.Count > 0)
                body.Add(new { kind = "text", value = string.Join('\n', buf) });
        }

        // 轮播图：数据源中除正文图片外的图片，主图排最前。
        // fanbox / E-Hentai 不给列表（前端据此回退到 /api/cover，只显示封面一张）
        var slider = new List<string>();
        if (!isFlat && Directory.Exists(folder))
        {
            slider = Directory.GetFiles(folder)
                .Select(Path.GetFileName)
                .Where(f => f != null &&
                            ImageExts.Contains(Path.GetExtension(f).ToLowerInvariant()) &&
                            !bodyFiles.Contains(f))
                .Select(f => f!)
                .OrderBy(f => f.Contains("img_main", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var tags = (Db.Select("SELECT \"genre\" FROM \"work_genres\" WHERE \"work_id\" = @w", ("@w", id)) ?? [])
            .Select(t => t[0] as string ?? "").Where(t => t.Length > 0).ToList();

        var ageRaw = r[8]?.ToString() ?? "";
        WriteJson(stream, 200, new
        {
            id = r[0] as string ?? "",
            name = r[1] as string ?? "",
            maker = r[2] as string ?? "",
            sellDate = r[3] as string ?? "",
            series = r[4] as string ?? "",
            scenario = r[5] as string ?? "",
            illust = r[6] as string ?? "",
            voiceActor = r[7] as string ?? "",
            age = ageRaw,
            ageText = AgeMap.GetValueOrDefault(ageRaw, ageRaw),
            workType = r[9] as string ?? "",
            fileSize = r[11] as string ?? "",
            intro = r[12] as string ?? "",
            read = r[14] as string == "1",
            favorite = r[15] as string == "1",
            hasCover = r[16] != null && File.Exists(r[16] as string),
            hasFiles = r[13] is string wf && Directory.Exists(wf),
            tags,
            slider,
            body,
        });
    }

    private static void ApiToggle(NetworkStream stream, Request req)
    {
        string id = "", field = "";
        var value = false;
        try
        {
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(req.Body));
            var root = doc.RootElement;
            id = root.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "";
            field = root.TryGetProperty("field", out var f) ? f.GetString() ?? "" : "";
            value = root.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.True;
        }
        catch (Exception)
        {
            // 解析失败按非法请求处理
        }
        // 字段白名单
        var col = field switch { "read" => "read_flag", "favorite" => "favorite", _ => null };
        if (col == null || id.Length == 0)
        {
            WriteJson(stream, 400, new { error = "bad request" });
            return;
        }
        Db.Execute($"UPDATE \"works\" SET \"{col}\" = @v WHERE \"work_id\" = @w",
            ("@v", value ? "1" : null), ("@w", id));
        WriteJson(stream, 200, new { ok = true, value });
    }

    /// <summary>设置/取消作品"不喜欢"：POST { id, value }。命中后前端跳过 AS 扫描并置灰。</summary>
    private static void ApiDislike(NetworkStream stream, Request req)
    {
        string id = "";
        var value = false;
        try
        {
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(req.Body));
            var root = doc.RootElement;
            id = (root.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "").ToUpperInvariant();
            value = root.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.True;
        }
        catch (Exception)
        {
            // 解析失败按非法请求处理
        }
        if (id.Length == 0)
        {
            WriteJson(stream, 400, new { error = "bad request" });
            return;
        }
        Dislikes.Set(id, value);
        WriteJson(stream, 200, new { ok = true, value });
    }

    // ---------- API：图片 ----------

    private static void ApiCover(NetworkStream stream, Request req)
    {
        var id = req.Query.GetValueOrDefault("id") ?? "";
        var cover = Db.Scalar("SELECT \"cover\" FROM \"works\" WHERE \"work_id\" = @w", ("@w", id)) as string;
        ServeImageFile(stream, cover, ThumbParam(req));
    }

    /// <summary>请求是否要缩略图（thumb=1）：压缩到 128KB 以内再发，避免传原图。</summary>
    private static bool ThumbParam(Request req) => req.Query.GetValueOrDefault("thumb") == "1";

    private static void ApiAsset(NetworkStream stream, Request req)
    {
        var id = req.Query.GetValueOrDefault("id") ?? "";
        var name = req.Query.GetValueOrDefault("name") ?? "";
        // 文件名净化：禁止路径分隔符与上跳，仅允许图片扩展名
        if (name.Length == 0 || name.Contains('/') || name.Contains('\\') || name.Contains("..") ||
            !ImageExts.Contains(Path.GetExtension(name).ToLowerInvariant()))
        {
            WriteBytes(stream, 400, "Bad Request", "text/plain", Encoding.ASCII.GetBytes("bad name"));
            return;
        }
        var folder = ResolveAssetFolder(id, Db.Scalar(
            "SELECT \"folder\" FROM \"works\" WHERE \"work_id\" = @w", ("@w", id)) as string);
        ServeImageFile(stream, Path.Combine(folder, name), ThumbParam(req));
    }

    /// <summary>详情资源文件夹：作品文件夹/DataSource 优先，否则回退 images/&lt;RJ&gt;。</summary>
    private static string ResolveAssetFolder(string id, string? workFolder)
    {
        // fanbox / E-Hentai 作品的图片直接摊在作品目录里，没有 DLsite 那套 DataSource 子目录
        if (MediaLibraryService.IsFlatImageWork(id))
            return workFolder ?? "";
        if (workFolder != null)
        {
            var ds = Path.Combine(workFolder, DlsitePage.DataSourceDir);
            if (Directory.Exists(ds))
                return ds;
        }
        return Path.Combine(DlsitePage.ImagesDir, id);
    }

    private static void ServeImageFile(NetworkStream stream, string? path, bool thumb = false)
    {
        if (path == null || !File.Exists(path) ||
            !ImageExts.Contains(Path.GetExtension(path).ToLowerInvariant()))
        {
            WriteBytes(stream, 404, "Not Found", "text/plain", Encoding.ASCII.GetBytes("not found"));
            return;
        }
        // 缩略图：压到 128KB 以内的 JPEG 再发（压缩失败则回退原图）
        if (thumb && ImageThumb.Compress(path) is { } jpg)
        {
            WriteBytes(stream, 200, "OK", "image/jpeg", jpg, ("Cache-Control", "max-age=86400"));
            return;
        }
        byte[] data;
        try { data = File.ReadAllBytes(path); }
        catch (IOException) { WriteBytes(stream, 404, "Not Found", "text/plain", []); return; }
        WriteBytes(stream, 200, "OK", ContentType(path), data,
            ("Cache-Control", "max-age=86400"));
    }

    private static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        _ => "image/jpeg",
    };

    // ---------- API：作品文件树（“查看作品”） ----------

    /// <summary>列出作品文件夹的文件树（递归，根目录排除 DataSource，镜像桌面端 ShowFileTree）。</summary>
    private static void ApiFiles(NetworkStream stream, Request req)
    {
        var id = req.Query.GetValueOrDefault("id") ?? "";
        var folder = Db.Scalar("SELECT \"folder\" FROM \"works\" WHERE \"work_id\" = @w", ("@w", id)) as string;
        if (folder == null || !Directory.Exists(folder))
        {
            WriteJson(stream, 404, new { error = "作品文件夹不存在" });
            return;
        }
        var root = Path.GetFullPath(folder);
        WriteJson(stream, 200, new { id, nodes = BuildFileTree(root, root, isRoot: true) });
    }

    private static List<object> BuildFileTree(string dir, string root, bool isRoot)
    {
        var nodes = new List<object>();
        string[] subDirs = [], files = [];
        try
        {
            subDirs = Directory.GetDirectories(dir);
            files = Directory.GetFiles(dir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return nodes;
        }
        foreach (var sub in subDirs
                     .Where(d => !(isRoot && string.Equals(Path.GetFileName(d), DlsitePage.DataSourceDir,
                         StringComparison.OrdinalIgnoreCase)))
                     .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            nodes.Add(new
            {
                name = Path.GetFileName(sub), dir = true, rel = RelPath(root, sub),
                children = BuildFileTree(sub, root, false),
            });
        foreach (var f in files.OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            long size = 0;
            try { size = new FileInfo(f).Length; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            nodes.Add(new
            {
                name = Path.GetFileName(f), dir = false, rel = RelPath(root, f),
                ext = Path.GetExtension(f).ToLowerInvariant(), size,
            });
        }
        return nodes;
    }

    private static string RelPath(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');

    /// <summary>流式发送作品文件夹内的单个文件，支持 HTTP Range（视频/音频拖动、断点续传）。</summary>
    private static void ApiFile(NetworkStream stream, Request req)
    {
        var id = req.Query.GetValueOrDefault("id") ?? "";
        var rel = req.Query.GetValueOrDefault("path") ?? "";
        var folder = Db.Scalar("SELECT \"folder\" FROM \"works\" WHERE \"work_id\" = @w", ("@w", id)) as string;
        if (folder == null || !Directory.Exists(folder))
        {
            WriteBytes(stream, 404, "Not Found", "text/plain", []);
            return;
        }
        var root = Path.GetFullPath(folder);
        string full;
        try { full = Path.GetFullPath(Path.Combine(root, rel)); }
        catch (Exception) { WriteBytes(stream, 400, "Bad Request", "text/plain", []); return; }
        // 路径穿越防护：必须落在作品文件夹内
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(full))
        {
            WriteBytes(stream, 404, "Not Found", "text/plain", []);
            return;
        }
        // 缩略图网格：图片按 thumb=1 请求，压到 128KB 以内再发，不传原图（非图片/非缩略图仍走 Range 流式）
        if (ThumbParam(req) && ImageExts.Contains(Path.GetExtension(full).ToLowerInvariant()))
        {
            ServeImageFile(stream, full, thumb: true);
            return;
        }
        WriteFileRange(stream, req, full);
    }

    /// <summary>
    /// 在运行本程序的这台电脑上启动作品里的 .exe（游戏/软件类作品）。浏览器跑不了 Windows 程序，
    /// 所以"网页上运行"= 网页发指令、本机开窗口，画面出现在这台电脑的屏幕上而不是手机里。
    /// 只认 POST（防止 &lt;img src&gt; 之类的 GET 被人借道触发）、只认作品文件夹内的 .exe。
    /// </summary>
    private static void ApiRunExe(NetworkStream stream, Request req)
    {
        if (req.Method != "POST")
        {
            WriteJson(stream, 405, new { ok = false, message = "method not allowed" });
            return;
        }
        var id = ReadStringField(req.Body, "id");
        var rel = ReadStringField(req.Body, "path");
        if (ResolveWorkExe(id, rel) is not { Full: var full })
        {
            WriteJson(stream, 404, new { ok = false, message = "找不到该程序" });
            return;
        }
        try
        {
            // 工作目录必须设成 exe 所在目录：游戏多按相对路径读自己的资源包（如 Godot 的 .pck）
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(full)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(full)!,
            });
            Logger.Info($"Web 端启动程序：{id} / {rel}");
            WriteJson(stream, 200, new { ok = true });
        }
        catch (Exception e)
        {
            Logger.Warning($"Web 端启动程序失败：{id} / {rel}：{e.Message}");
            WriteJson(stream, 200, new { ok = false, message = e.Message });
        }
    }

    /// <summary>
    /// 作品文件夹里的某个 .exe → (完整路径, 作品根目录)。路径穿越防护同 ApiFile；另外只认 .exe，
    /// 不让"运行程序"变成"执行作品目录里任意文件"。
    /// </summary>
    private static (string Full, string Root)? ResolveWorkExe(string id, string rel)
    {
        var folder = Db.Scalar("SELECT \"folder\" FROM \"works\" WHERE \"work_id\" = @w", ("@w", id)) as string;
        if (folder == null || !Directory.Exists(folder))
            return null;
        var root = Path.GetFullPath(folder);
        string full;
        try { full = Path.GetFullPath(Path.Combine(root, rel)); }
        catch (Exception) { return null; }
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(full) ||
            !Path.GetExtension(full).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            return null;
        return (full, root);
    }

    /// <summary>
    /// 远程串流（WebSocket）：握手后整条连接交给 <see cref="GameStream"/>，直到浏览器关掉为止。
    /// 必须校验 Origin 与 Host 同源——WebSocket 不受同源策略约束、浏览器又会自动带上 Cookie，
    /// 不校验的话任意网页都能借已登录的浏览器连进来远程操作这台电脑。
    /// </summary>
    private static async Task ApiStreamAsync(NetworkStream stream, Request req)
    {
        if (!req.Headers.TryGetValue("Sec-WebSocket-Key", out var key) || key.Length == 0 ||
            !req.Headers.TryGetValue("Upgrade", out var upgrade) ||
            !upgrade.Equals("websocket", StringComparison.OrdinalIgnoreCase))
        {
            WriteJson(stream, 400, new { error = "websocket required" });
            return;
        }
        if (!req.Headers.TryGetValue("Origin", out var origin) ||
            !Uri.TryCreate(origin, UriKind.Absolute, out var ou) ||
            !req.Headers.TryGetValue("Host", out var host) ||
            !string.Equals(ou.Authority, host, StringComparison.OrdinalIgnoreCase))
        {
            WriteJson(stream, 403, new { error = "forbidden origin" });
            return;
        }
        // ?login=<站点>：账号访问的「在服务器上登录」，在本机开登录浏览器并串流过去（LoginBrowser）
        var login = req.Query.GetValueOrDefault("login") ?? "";
        if (login.Length > 0)
        {
            await StreamLoginBrowserAsync(stream, key, login);
            return;
        }
        var id = req.Query.GetValueOrDefault("id") ?? "";
        var rel = req.Query.GetValueOrDefault("path") ?? "";
        if (ResolveWorkExe(id, rel) is not { Full: var full, Root: var root })
        {
            WriteJson(stream, 404, new { error = "找不到该程序" });
            return;
        }
        var head = "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
                   $"Sec-WebSocket-Accept: {GameStream.WebSocket.AcceptKey(key)}\r\n\r\n";
        stream.Write(Encoding.ASCII.GetBytes(head));
        Logger.Info($"Web 端远程串流：{id} / {rel}");
        // 串流要跑很久，单独起一条线程，不占线程池
        await Task.Factory.StartNew(() => GameStream.Run(stream, full, root), TaskCreationOptions.LongRunning);
    }

    /// <summary>
    /// 网页端登录：在运行本程序的电脑上打开登录浏览器（窗口要在 UI 线程上建），再交给 <see cref="LoginStream"/>
    /// 把画面推过去、把操作送回来。登录成功后窗口自己关掉，串流随之结束，结束提示取 <see cref="LoginBrowser.ResultOf"/>。
    ///
    /// <b>先完成 WebSocket 握手再开窗口</b>：开不了（没装 WebView2 运行时、以服务方式运行…）时把原因当消息发过去，
    /// 而不是回一个 HTTP 错误——握手阶段的错误码浏览器拿不到（经门户代理更是只剩一个「连接已断开」），用户无从知道为什么。
    /// </summary>
    private static async Task StreamLoginBrowserAsync(NetworkStream stream, string key, string site)
    {
        var head = "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
                   $"Sec-WebSocket-Accept: {GameStream.WebSocket.AcceptKey(key)}\r\n\r\n";
        stream.Write(Encoding.ASCII.GetBytes(head));
        void Fail(string message)
        {
            Logger.Warning($"Web 端登录浏览器打不开（{site}）：{message}");
            var json = JsonSerializer.Serialize(new { t = "exit", text = message });
            GameStream.WebSocket.WriteFrame(stream, GameStream.WebSocket.OpText, Encoding.UTF8.GetBytes(json));
            GameStream.WebSocket.WriteFrame(stream, GameStream.WebSocket.OpClose, []);
        }
        var app = System.Windows.Application.Current;
        if (!LoginBrowser.Supports(site) || app == null)
        {
            Fail("不支持的站点");
            return;
        }
        try
        {
            await app.Dispatcher.InvokeAsync(() => LoginBrowser.Open(site));
        }
        catch (Exception e)
        {
            Fail(e.Message);
            return;
        }
        Logger.Info($"Web 端串流登录浏览器：{site}");
        await Task.Factory.StartNew(() => LoginStream.Run(stream, site), TaskCreationOptions.LongRunning);
    }

    /// <summary>按 Range 头流式写出文件（206 Partial Content）或整文件（200），分块拷贝不占内存。</summary>
    private static void WriteFileRange(NetworkStream stream, Request req, string path)
    {
        FileStream fs;
        try { fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); }
        catch (Exception) { WriteBytes(stream, 404, "Not Found", "text/plain", []); return; }
        using (fs)
        {
            var length = fs.Length;
            long start = 0, end = length - 1;
            var partial = false;
            if (req.Headers.TryGetValue("Range", out var range) &&
                range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase) && length > 0)
            {
                var spec = range["bytes=".Length..].Split('-');
                if (spec.Length == 2)
                {
                    if (spec[0].Length == 0)
                    {
                        // 后缀形式 bytes=-N：取最后 N 字节
                        if (long.TryParse(spec[1], out var suffix))
                            start = Math.Max(0, length - suffix);
                    }
                    else
                    {
                        long.TryParse(spec[0], out start);
                        if (spec[1].Length > 0 && long.TryParse(spec[1], out var e))
                            end = e;
                    }
                }
                if (start < 0) start = 0;
                if (end >= length) end = length - 1;
                if (start > end) { start = 0; end = length - 1; }
                partial = true;
            }
            var count = end - start + 1;
            var sb = new StringBuilder();
            sb.Append("HTTP/1.1 ").Append(partial ? "206 Partial Content" : "200 OK").Append("\r\n");
            sb.Append("Content-Type: ").Append(MediaContentType(path)).Append("\r\n");
            sb.Append("Accept-Ranges: bytes\r\n");
            sb.Append("Content-Length: ").Append(count).Append("\r\n");
            if (partial)
                sb.Append("Content-Range: bytes ").Append(start).Append('-').Append(end).Append('/').Append(length).Append("\r\n");
            sb.Append("Connection: close\r\n\r\n");
            var head = Encoding.ASCII.GetBytes(sb.ToString());
            stream.Write(head, 0, head.Length);

            fs.Seek(start, SeekOrigin.Begin);
            var buffer = new byte[81920];
            var remaining = count;
            int read;
            while (remaining > 0 &&
                   (read = fs.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining))) > 0)
            {
                stream.Write(buffer, 0, read);
                remaining -= read;
            }
            stream.Flush();
        }
    }

    /// <summary>按扩展名给出媒体 MIME 类型（供浏览器原生播放/预览）。</summary>
    private static string MediaContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mp4" or ".m4v" => "video/mp4",
        ".webm" => "video/webm",
        ".ogv" => "video/ogg",
        ".mov" => "video/quicktime",
        ".mkv" => "video/x-matroska",
        ".avi" => "video/x-msvideo",
        ".flv" => "video/x-flv",
        ".wmv" => "video/x-ms-wmv",
        ".ts" => "video/mp2t",
        ".mpg" or ".mpeg" => "video/mpeg",
        ".mp3" => "audio/mpeg",
        ".wav" => "audio/wav",
        ".flac" => "audio/flac",
        ".m4a" => "audio/mp4",
        ".aac" => "audio/aac",
        ".ogg" or ".opus" => "audio/ogg",
        ".wma" => "audio/x-ms-wma",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".txt" => "text/plain; charset=utf-8",
        ".pdf" => "application/pdf",
        _ => "application/octet-stream",
    };

    // ---------- API：搜索 ----------

    private static async Task ApiSearchAsync(NetworkStream stream, Request req)
    {
        var id = (req.Query.GetValueOrDefault("id") ?? "").Trim().ToUpperInvariant();
        if (!WorkIdRe.IsMatch(id))
        {
            WriteJson(stream, 400, new { error = "番号格式错误（RJ/BJ/VJ + 数字）" });
            return;
        }
        // 已下载过的作品：返回提示信息供前端确认
        var existed = Db.Select(
            "SELECT \"work_name\", \"down_time\" FROM \"works\" WHERE \"work_id\" = @w", ("@w", id));
        string? exName = null, exTime = null;
        if (existed is { Count: > 0 })
        {
            exName = existed[0][0] as string ?? "";
            var t = existed[0][1]?.ToString() ?? "";
            exTime = t.Length > 19 ? t[..19] : t;
        }
        var workTask = DlsiteApi.GetWorkDataAsync(id);
        var searchTask = AnimeSharing.SearchWorkAsync(id);
        try
        {
            await Task.WhenAll(workTask, searchTask);
        }
        catch (Exception e)
        {
            WriteJson(stream, 200, new { id, error = "查询失败：" + e.Message, results = Array.Empty<object>() });
            return;
        }
        var work = workTask.Result;
        SearchCache[id] = work;
        var results = searchTask.Result.Select(r => new
        {
            title = r.Title, minor = r.Minor, snippet = TrimSnippet(r.Snippet), url = r.Url, thumb = r.Thumb,
        });
        WriteJson(stream, 200, new
        {
            id,
            work = new { name = work?.WorkName ?? "", maker = work?.MakerName ?? "", type = work?.WorkType ?? "" },
            existed = exName != null, existedName = exName, existedTime = exTime,
            results,
        });
    }

    /// <summary>按社团号（RG）返回该社团作品列表的某一页。</summary>
    private static async Task ApiMakerAsync(NetworkStream stream, Request req)
    {
        var id = (req.Query.GetValueOrDefault("id") ?? "").Trim().ToUpperInvariant();
        if (!Regex.IsMatch(id, @"^RG\d+$"))
        {
            WriteJson(stream, 400, new { error = "社团号格式错误（RG + 数字）" });
            return;
        }
        var page = GetInt(req, "page");
        if (page < 1)
            page = 1;
        var (works, hasMore) = await DlsiteApi.GetMakerWorksAsync(id, page);
        // 校验后台：本页作品号哪些已在库（works 表已有记录）、哪些被标记不喜欢，供前端置灰/跳过 AS 扫描
        var states = LookupWorkStates(works.Select(w => w.WorkId));
        var disliked = Dislikes.Lookup(works.Select(w => w.WorkId));
        WriteJson(stream, 200, new
        {
            works = works.Select(w => new
            {
                id = w.WorkId, title = w.Title, thumb = w.Thumb,
                inLib = states.ContainsKey(w.WorkId),
                state = states.GetValueOrDefault(w.WorkId, ""),
                disliked = disliked.Contains(w.WorkId),
            }),
            hasMore,
            watched = DlsiteWatchService.IsWatched(id),   // 社团网格工具栏「+ 监控社团 / ✓ 已监控」据此定文案
        });
    }

    /// <summary>按 DLsite 搜索/筛选/分类列表页（fsr 等 dlsite.com 链接）返回作品列表的某一页。</summary>
    private static async Task ApiCatalogAsync(NetworkStream stream, Request req)
    {
        var url = (req.Query.GetValueOrDefault("url") ?? "").Trim();
        // 仅允许真实的 dlsite.com 主机，避免被当作任意 URL 抓取
        if (!(Uri.TryCreate(url, UriKind.Absolute, out var u)
              && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps)
              && (u.Host.Equals("dlsite.com", StringComparison.OrdinalIgnoreCase)
                  || u.Host.EndsWith(".dlsite.com", StringComparison.OrdinalIgnoreCase))))
        {
            WriteJson(stream, 400, new { error = "链接格式错误（需为 DLsite 链接）" });
            return;
        }
        var page = GetInt(req, "page");
        if (page < 1)
            page = 1;
        var (works, hasMore) = await DlsiteApi.GetCatalogWorksAsync(url, page);
        var states = LookupWorkStates(works.Select(w => w.WorkId));
        var disliked = Dislikes.Lookup(works.Select(w => w.WorkId));
        WriteJson(stream, 200, new
        {
            works = works.Select(w => new
            {
                id = w.WorkId, title = w.Title, thumb = w.Thumb,
                inLib = states.ContainsKey(w.WorkId),
                state = states.GetValueOrDefault(w.WorkId, ""),
                disliked = disliked.Contains(w.WorkId),
            }),
            hasMore,
        });
    }

    /// <summary>批量查询作品号在 works 表中的状态（不存在则不在结果里），用于搜索时标记"已在库"。</summary>
    private static Dictionary<string, string> LookupWorkStates(IEnumerable<string> ids)
    {
        var list = ids.Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (list.Count == 0)
            return result;
        var names = new List<string>();
        var args = new List<(string, object?)>();
        for (var i = 0; i < list.Count; i++)
        {
            names.Add($"@w{i}");
            args.Add(($"@w{i}", list[i]));
        }
        var rows = Db.Select(
            $"SELECT \"work_id\", \"state\" FROM \"works\" WHERE \"work_id\" IN ({string.Join(",", names)})",
            args.ToArray());
        foreach (var row in rows ?? [])
            result[row[0] as string ?? ""] = row[1] as string ?? "";
        return result;
    }

    /// <summary>对单个作品号做一次 AS 论坛扫描，返回匹配帖子数（-1 表示扫描出错）。
    /// 供 RG 搜索时前端按 3 秒间隔逐个探测未在库作品的可下载性。</summary>
    private static async Task ApiAsScanAsync(NetworkStream stream, Request req)
    {
        var id = (req.Query.GetValueOrDefault("id") ?? "").Trim().ToUpperInvariant();
        if (!WorkIdRe.IsMatch(id))
        {
            WriteJson(stream, 400, new { error = "番号格式错误（RJ/BJ/VJ + 数字）" });
            return;
        }
        // 7 天内已扫出"无结果"的作品直接返回 0，不再重复请求 AS（cached 标记供前端跳过限速间隔，remainSec 供倒计时）
        var remain = AsScanCache.RemainSeconds(id);
        if (remain > 0)
        {
            WriteJson(stream, 200, new { id, count = 0, cached = true, remainSec = remain });
            return;
        }
        int count;
        try
        {
            count = (await AnimeSharing.SearchWorkAsync(id)).Count;
        }
        catch (Exception)
        {
            WriteJson(stream, 200, new { id, count = -1 });
            return;
        }
        AsScanCache.Store(id, count);
        // 本次扫出无结果 → 返回新的倒计时（≈7 天）
        WriteJson(stream, 200, new { id, count, remainSec = count == 0 ? AsScanCache.RemainSeconds(id) : 0 });
    }

    private static string TrimSnippet(string snippet) =>
        string.Join('\n', snippet.Split('\n')
            .Select(l => string.Join(' ', l.Split(' ', StringSplitOptions.RemoveEmptyEntries)))
            .Where(l => l.Length > 0).Take(6));

    private static async Task ApiThumbAsync(NetworkStream stream, Request req)
    {
        var url = req.Query.GetValueOrDefault("url") ?? "";
        if (!IsSafeFetchUrl(url))
        {
            WriteBytes(stream, 400, "Bad Request", "text/plain", []);
            return;
        }
        try
        {
            using var client = Http.CreateClient(TimeSpan.FromSeconds(15));
            var bytes = await client.GetByteArrayAsync(url);
            WriteBytes(stream, 200, "OK", ContentType(url), bytes, ("Cache-Control", "max-age=86400"));
        }
        catch (Exception)
        {
            WriteBytes(stream, 404, "Not Found", "text/plain", []);
        }
    }

    private static async Task ApiPostUrlsAsync(NetworkStream stream, Request req)
    {
        var threadPath = req.Query.GetValueOrDefault("url") ?? "";
        List<string> urls;
        try
        {
            (urls, _) = await AnimeSharing.GetWorkDownUrlsAsync(threadPath);
        }
        catch (Exception e)
        {
            WriteJson(stream, 200, new { error = "解析失败：" + e.Message, hosts = Array.Empty<object>() });
            return;
        }
        // 按域名分组，保持出现顺序
        var groups = new Dictionary<string, List<string>>();
        foreach (var u in urls)
        {
            var host = HostOf(u);
            if (!groups.TryGetValue(host, out var list))
                groups[host] = list = [];
            list.Add(u);
        }
        WriteJson(stream, 200, new
        {
            hosts = groups.Select(g => new { host = g.Key, count = g.Value.Count, urls = g.Value }),
        });
    }

    private static string HostOf(string url)
    {
        try
        {
            var host = new Uri(url).Host.ToLowerInvariant();
            return host.StartsWith("www.") ? host[4..] : host;
        }
        catch (UriFormatException)
        {
            return url;
        }
    }

    private static async Task ApiCheckHostAsync(NetworkStream stream, Request req)
    {
        var urls = ReadStringArray(req.Body, "urls");
        if (urls.Count == 0)
        {
            WriteJson(stream, 400, new { error = "no urls" });
            return;
        }
        var valid = 0;
        using (var client = LinkChecker.MakeClient())
            foreach (var u in urls)
                if (await LinkChecker.CheckUrlAsync(u, client))
                    valid++;
        var status = valid == urls.Count ? "valid" : valid == 0 ? "invalid" : "partial";
        WriteJson(stream, 200, new { valid, total = urls.Count, status });
    }

    private static void ApiDownTargets(NetworkStream stream)
    {
        var libs = AppConfig.ReadMediaLibs()
            .Select(l => new { name = l.Name, folders = l.Folders.Where(f => f.Length > 0).ToList() })
            .Where(l => l.folders.Count > 0);
        WriteJson(stream, 200, new { libs });
    }

    private static void ApiEnqueue(NetworkStream stream, Request req)
    {
        string id = "", lib = "", folder = "";
        var urls = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(req.Body));
            var root = doc.RootElement;
            id = (root.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "").ToUpperInvariant();
            lib = root.TryGetProperty("lib", out var l) ? l.GetString() ?? "" : "";
            folder = root.TryGetProperty("folder", out var f) ? f.GetString() ?? "" : "";
            if (root.TryGetProperty("urls", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var u in arr.EnumerateArray())
                    if (u.GetString() is { Length: > 0 } s)
                        urls.Add(s);
        }
        catch (Exception)
        {
            // 解析失败按非法请求处理
        }
        if (!WorkIdRe.IsMatch(id) || urls.Count == 0)
        {
            WriteJson(stream, 400, new { error = "bad request" });
            return;
        }
        foreach (var downUrl in urls)
            Db.Execute(
                "INSERT OR REPLACE INTO \"download_list\" (\"UUID\", \"work_id\", \"url\", \"status\", \"long\", \"delete\") " +
                "VALUES (@uuid, @w, @url, '0', '0', '1')",
                ("@uuid", Guid.NewGuid().ToString()), ("@w", id), ("@url", downUrl));
        RecordWork(id, SearchCache.GetValueOrDefault(id));
        if (folder.Length > 0)
            DownloadEngine.SetWorkTargetPath(id, folder, lib.Length > 0 ? lib : null);
        DownloadEngine.Start();
        WriteJson(stream, 200, new { ok = true });
    }

    /// <summary>自动下载：立即以「搜索可用下载连接」('5') 占位入队，后台异步挑选最优源。
    /// 命中则占位行转正常下载分卷；全无可用则占位行转「无可用下载连接」('6')。</summary>
    private static void ApiAutoDownload(NetworkStream stream, Request req)
    {
        string id = "", lib = "", folder = "";
        try
        {
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(req.Body));
            var root = doc.RootElement;
            id = (root.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "").ToUpperInvariant();
            lib = root.TryGetProperty("lib", out var l) ? l.GetString() ?? "" : "";
            folder = root.TryGetProperty("folder", out var f) ? f.GetString() ?? "" : "";
        }
        catch (Exception)
        {
            // 解析失败按非法请求处理
        }
        if (!WorkIdRe.IsMatch(id))
        {
            WriteJson(stream, 400, new { error = "番号格式错误（RJ/BJ/VJ + 数字）" });
            return;
        }
        StartAutoDownload(id, lib, folder);
        WriteJson(stream, 200, new { ok = true });
    }

    /// <summary>
    /// 自动下载的入队本体（Web「自动下载」与 <see cref="DlsiteWatchService"/> 共用）：
    /// 立即以「搜索可用下载连接」占位入队，后台串行挑选 AS 论坛的最优源。
    /// </summary>
    internal static void StartAutoDownload(string id, string lib, string folder)
    {
        // 占位分卷：状态 '5'，url 用每行唯一的占位哨兵（download_list 以 url 为主键，若留空则同时
        // 只能存在一条占位行——用户在某作品仍在搜索连接时再加别的作品会因主键冲突而被静默丢弃）。
        // DownloadEngine 只领取 '0' 行，故占位哨兵不会被误当作直链下载；FileNameOf 会把哨兵显示为空名。
        var placeholder = Guid.NewGuid().ToString();
        Db.Execute(
            "INSERT INTO \"download_list\" (\"UUID\", \"work_id\", \"url\", \"status\", \"long\", \"delete\") " +
            "VALUES (@uuid, @w, @url, '5', '0', '1')",
            ("@uuid", placeholder), ("@w", id), ("@url", PlaceholderUrl(placeholder)));
        RecordWork(id, SearchCache.GetValueOrDefault(id));
        if (folder.Length > 0)
            DownloadEngine.SetWorkTargetPath(id, folder, lib.Length > 0 ? lib : null);
        _ = EnsureWorkMetaAsync(id);   // 后台补齐作品名（自动下载未经搜索，works.work_name 可能为空）
        // 串行排队解析：同一时间只搜索一个作品的下载连接；排队期间占位行('5')已在库，故仍显示在下载列表
        _ = Task.Run(async () =>
        {
            await ResolveGate.WaitAsync();
            try
            {
                if (!PlaceholderAlive(placeholder))
                    return;   // 排队等待期间用户已删除该作品 → 不再解析
                await AutoResolveLinksAsync(id, placeholder);
            }
            finally
            {
                ResolveGate.Release();
            }
        });
    }

    // 自动下载优先在前 5 个帖子里深挖 rapidgator；超过仍无 rapidgator 有效源才退用 katfile 等其它网盘
    private const int DeepSearchPosts = 5;

    /// <summary>后台逐帖抓取网盘链接并检测有效性，选出下载源：
    /// 每帖内优先用 rapidgator，失效的分卷用其它网盘同名分卷补齐（组合下载）；
    /// 跨帖深度搜索优先 rapidgator，扫满 5 帖仍无 rapidgator 才退用 katfile 等。
    /// 命中即把占位行替换为真实分卷并启动引擎；全程未命中则把占位行标记为「无可用下载连接」。</summary>
    private static async Task AutoResolveLinksAsync(string id, string placeholder)
    {
        try
        {
            // 7 天内已扫出"无结果"的作品：直接判为无可用连接，跳过 AS 搜索
            if (AsScanCache.HasFreshEmpty(id))
            {
                Db.Execute("UPDATE \"download_list\" SET \"status\" = '6' WHERE \"UUID\" = @u AND \"status\" = '5'", ("@u", placeholder));
                return;
            }
            var posts = await AnimeSharing.SearchWorkAsync(id);
            AsScanCache.Store(id, posts.Count);

            PostCombiner.PostAssembly? best = null;            // 最优「完整」组合（rapidgator 覆盖最多者）
            List<string>? bestPartial = null; var bestPartialCount = 0;   // 退路：凑不齐时用有效分卷最多的部分集
            var scanned = 0;

            using var client = LinkChecker.MakeClient();
            foreach (var post in posts)
            {
                if (!PlaceholderAlive(placeholder))
                    return;   // 占位行已被用户删除 → 中止
                List<string> urls;
                try { (urls, _) = await AnimeSharing.GetWorkDownUrlsAsync(post.Url); }
                catch (Exception) { continue; }
                if (urls.Count == 0)
                    continue;

                var asm = await PostCombiner.AssembleAsync(urls, client, () => PlaceholderAlive(placeholder));
                if (!PlaceholderAlive(placeholder))
                    return;
                if (asm == null)
                    continue;
                scanned++;

                if (asm.Complete)
                {
                    // 全部分卷都由 rapidgator 提供 → 最优，立即选定
                    if (asm.RapidgatorParts == asm.TotalParts)
                    {
                        best = asm;
                        break;
                    }
                    // 完整但混合（rapidgator + 其它网盘补齐失效分卷）：保留 rapidgator 覆盖最多者
                    if (best == null || asm.RapidgatorParts > best.RapidgatorParts)
                        best = asm;
                }
                else if (asm.Urls.Count > bestPartialCount)
                {
                    bestPartial = asm.Urls; bestPartialCount = asm.Urls.Count;
                }

                // 深度搜索止损：已扫满 5 帖且已有完整组合即采用
                // （若这 5 帖里都没 rapidgator，best 便是 katfile 等的完整集 → 自然实现"5 帖后退用 katfile"）
                if (scanned >= DeepSearchPosts && best != null)
                    break;
            }
            if (!PlaceholderAlive(placeholder))
                return;

            var chosen = best?.Urls ?? bestPartial;
            if (chosen == null || chosen.Count == 0)
            {
                Db.Execute("UPDATE \"download_list\" SET \"status\" = '6' WHERE \"UUID\" = @u", ("@u", placeholder));
                return;
            }
            if (best is { Complete: true } && best.RapidgatorParts < best.TotalParts)
                Logger.Info($"{id} 自动下载组合源：rapidgator {best.RapidgatorParts}/{best.TotalParts} 分卷有效，其余分卷用其它网盘同名分卷补齐");
            // 命中：占位行换成真实分卷（状态 '0'），启动引擎开始下载
            Db.Execute("DELETE FROM \"download_list\" WHERE \"UUID\" = @u", ("@u", placeholder));
            foreach (var downUrl in chosen)
                Db.Execute(
                    "INSERT OR REPLACE INTO \"download_list\" (\"UUID\", \"work_id\", \"url\", \"status\", \"long\", \"delete\") " +
                    "VALUES (@uuid, @w, @url, '0', '0', '1')",
                    ("@uuid", Guid.NewGuid().ToString()), ("@w", id), ("@url", downUrl));
            DownloadEngine.Start();
        }
        catch (Exception e)
        {
            Logger.Error($"{id} 自动下载解析失败: {e.Message}");
            Db.Execute("UPDATE \"download_list\" SET \"status\" = '6' WHERE \"UUID\" = @u AND \"status\" = '5'", ("@u", placeholder));
        }
    }

    /// <summary>单帖内组合出一份下载分卷集：以分卷最多的网盘为完整分卷全集，逐个分卷按
    /// rapidgator → katfile → 其它 的优先级挑首个「有效」链接（跨网盘同名分卷可互相补齐失效者）。</summary>
    // 组合下载算法已抽到 PostCombiner（WebServer 与 SearchPage 共用），此处仅保留 PlaceholderAlive 等 WebServer 专属逻辑。

    /// <summary>占位分卷是否仍存在且处于「搜索中」('5')——用户删除后后台解析即应停止。</summary>
    private static bool PlaceholderAlive(string placeholder)
    {
        var r = Db.Select("SELECT 1 FROM \"download_list\" WHERE \"UUID\" = @u AND \"status\" = '5'", ("@u", placeholder));
        return r is { Count: > 0 };
    }

    /// <summary>补齐 works 行的作品名等元数据（仅在为空时写入，避免覆盖已刮削的数据、不触碰目标库/状态）。
    /// 自动下载入队时未经作品搜索，works.work_name 常为空 → 下载页会退显番号，此处按番号拉取 DL API 补上。</summary>
    private static async Task EnsureWorkMetaAsync(string id)
    {
        if (!MetaBackfilled.TryAdd(id, 0))
            return;   // 已尝试过则跳过（去重，防轮询重复拉取）
        try
        {
            var work = SearchCache.GetValueOrDefault(id) ?? await DlsiteApi.GetWorkDataAsync(id);
            if (work == null || string.IsNullOrEmpty(work.WorkName))
                return;   // 确实查不到该作品数据 → 保留去重标记，不再重试（避免每秒轮询空拉）
            SearchCache[id] = work;
            Db.Execute(
                "UPDATE \"works\" SET \"work_name\" = @n, \"maker_id\" = @mi, \"maker_name\" = @mn, \"work_type\" = @t " +
                "WHERE \"work_id\" = @w AND (\"work_name\" IS NULL OR \"work_name\" = '')",
                ("@w", id), ("@n", work.WorkName), ("@mi", work.MakerId ?? ""),
                ("@mn", work.MakerName ?? ""), ("@t", work.WorkType ?? ""));
        }
        catch (Exception)
        {
            MetaBackfilled.TryRemove(id, out _);
        }
    }

    /// <summary>把 DL API 作品数据写入 works 表（镜像 SearchPage.RecordWork）。</summary>
    private static void RecordWork(string id, DlWork? work)
    {
        Db.Execute(
            "INSERT INTO \"works\" (\"work_id\", \"work_name\", \"maker_id\", \"maker_name\", \"work_type\", " +
            "\"intro_s\", \"age_category\", \"is_ana\", \"state\", \"down_time\") VALUES " +
            "(@w, @n, @mi, @mn, @t, @s, @a, @ana, '下载中', @time) " +
            "ON CONFLICT(\"work_id\") DO UPDATE SET " +
            "\"work_name\" = excluded.\"work_name\", \"maker_id\" = excluded.\"maker_id\", " +
            "\"maker_name\" = excluded.\"maker_name\", \"work_type\" = excluded.\"work_type\", " +
            "\"intro_s\" = excluded.\"intro_s\", \"age_category\" = excluded.\"age_category\", " +
            "\"is_ana\" = excluded.\"is_ana\", \"state\" = excluded.\"state\", \"down_time\" = excluded.\"down_time\", " +
            "\"folder\" = NULL, \"target\" = NULL, \"target_lib\" = NULL, " +
            "\"cover\" = NULL, \"meta_scanned\" = NULL",
            ("@w", id), ("@n", work?.WorkName ?? ""), ("@mi", work?.MakerId ?? ""),
            ("@mn", work?.MakerName ?? ""), ("@t", work?.WorkType ?? ""), ("@s", work?.IntroS ?? ""),
            ("@a", work?.AgeCategory ?? ""), ("@ana", work?.IsAna ?? ""),
            ("@time", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.ffffff")));
    }

    // ---------- API：下载 / 已下载 ----------

    private static void ApiDownloads(NetworkStream stream)
    {
        DownloadListActions.ClearFinished();   // 已入库的作品自动移出下载列表（取代「清除已完成」）
        var rows = Db.Select(
            "SELECT \"UUID\", \"work_id\", \"url\", \"status\", \"long\", \"error\", \"sub_path\", \"source\" FROM \"download_list\" ORDER BY rowid");
        var order = new List<string>();
        var grouped = new Dictionary<string, List<(string Uuid, string Url, string Status, string Long, string? Error, string? SubPath)>>();
        var sourceOf = new Dictionary<string, string?>();   // 作品 → 下载源（同一作品的文件来源一致，取首行）
        foreach (var row in rows ?? [])
        {
            var workId = row[1] as string ?? "";
            if (!grouped.TryGetValue(workId, out var list))
            {
                grouped[workId] = list = [];
                order.Add(workId);
                sourceOf[workId] = row[7] as string;
            }
            list.Add((row[0] as string ?? "", row[2] as string ?? "",
                row[3] as string ?? "", row[4]?.ToString() ?? "", row[5] as string, row[6] as string));
        }
        // 下载列表内番号对应的作品名（下载页以作品名代替番号显示；仅查列表内的番号，避免全表扫描）
        var nameMap = new Dictionary<string, string>();
        var ids = order.Where(w => w.Length > 0).ToList();
        if (ids.Count > 0)
        {
            var pars = ids.Select((v, idx) => ($"@w{idx}", (object?)v)).ToArray();
            var inClause = string.Join(",", ids.Select((_, idx) => $"@w{idx}"));
            var nameRows = Db.Select(
                $"SELECT \"work_id\", \"work_name\" FROM \"works\" WHERE \"work_id\" IN ({inClause})", pars);
            foreach (var nr in nameRows ?? [])
                nameMap[nr[0] as string ?? ""] = nr[1] as string ?? "";
            // 仍缺作品名的番号（多为自动下载入队、尚未刮削元数据）后台补齐，下一次轮询即显示作品名
            foreach (var wid in ids)
                if (string.IsNullOrEmpty(nameMap.GetValueOrDefault(wid)))
                    _ = EnsureWorkMetaAsync(wid);
        }
        var groups = order.Select(workId =>
        {
            var items = grouped[workId];
            var statuses = items.Select(it => it.Status).ToList();
            var (aggText, aggColor) = AggregateStatus(
                workId, items.Select(it => (it.Status, it.Error)).ToList(),
                FreeHostDownloader.WaitingUntilAll(items.Where(it => it.Status == "0").Select(it => it.Url)));
            var totalPct = 0;
            double totalSpeed = 0;
            var children = items.Select(it =>
            {
                var (pct, speed) = FileProgress(it.Uuid, it.Status, it.Long);
                totalPct += pct;
                if (speed is { } s) totalSpeed += s;
                var (text, color) = StatusMap.TryGetValue(it.Status, out var m)
                    ? m : ($"未知({it.Status})", "#cdd3de");
                // 解析失败：状态直接显示具体原因的简短标签（如"文件失效"/"流量用尽"），完整说明见 errorReason
                if (it.Status == "2")
                    text = ParseErrorLabel(it.Error);
                // 已完成、但存的是源站预览图而非原图
                else if (it.Status == "1" && DownloadEngine.IsThumb(it.Error))
                    text = "已完成（预览图）";
                // 网盘免费下载在等该网盘的下载间隔：写出几点轮到它（镜像 WPF）
                else if (it.Status == "0" && FreeHostDownloader.WaitingUntil(it.Url) is { } until)
                    text = $"免费间隔 {until:HH:mm}";
                // asmr 直链带作品内目录层级（sub_path 含子目录），用于前端构建目录树；
                // 论坛源无 sub_path，回退到 URL 解码后的文件名（位于根层级），对齐 WPF 下载页
                var rel = string.IsNullOrEmpty(it.SubPath) ? FileNameOf(it.Url) : it.SubPath!;
                var slash = rel.LastIndexOf('/');
                return new
                {
                    fileName = slash >= 0 ? rel[(slash + 1)..] : rel, relPath = rel,
                    url = it.Url, pct,
                    speed = speed is { } sp ? FormatSpeed(sp) : "",
                    statusText = text, color,
                    // 解析失败的分卷附带可读错误原因
                    errorReason = it.Status == "2" ? MapParseError(it.Error) : "",
                };
            }).ToList();
            var groupPct = DownloadEngine.UnzipProgress.TryGetValue(workId, out var unzip)
                ? unzip.Pct : (double)totalPct / Math.Max(1, items.Count);
            return new
            {
                id = workId, name = nameMap.GetValueOrDefault(workId, ""),
                // 一级折叠分区（按下载源，见 DownloadLanes.SectionOf）
                section = DownloadLanes.SectionOf(sourceOf[workId], items.Select(it => it.Url)),
                pct = (int)groupPct, statusText = aggText, color = aggColor,
                totalSpeed,
                speed = totalSpeed > 0 ? FormatSpeed(totalSpeed) : "",
                canReparse = statuses.Contains("2") && workId.Length > 0,
                // 有下载中/等待分卷可"停止"；有已暂停分卷可"下载"（继续）；非空番号始终可"删除"
                canPause = statuses.Any(s => s is "0" or "3") && workId.Length > 0,
                canResume = statuses.Contains("4") && workId.Length > 0,
                canDelete = workId.Length > 0,
                // 「跳转作品」：works 里有这一行才有详情可看（论坛源刚入队的占位番号可能还没有）
                canOpen = nameMap.ContainsKey(workId),
                children,
            };
        }).ToList();
        // 下载源分区的汇总（分区默认折叠，标题行上就要看出这个源正在下几个、等几个、速度多少）
        var sections = DownloadLanes.All
            .Select(lane => (lane, works: groups.Where(g => g.section == lane.Key).ToList()))
            .Where(x => x.works.Count > 0)
            .Select(x =>
            {
                var statuses = x.works.SelectMany(g => grouped[g.id]).ToList();
                var speed = x.works.Sum(g => g.totalSpeed);
                return new
                {
                    key = x.lane.Key, label = x.lane.Label, works = x.works.Count,
                    downloading = statuses.Count(it => it.Status == "3"),
                    waiting = statuses.Count(it => it.Status == "0"),
                    // 等待的分卷全在等网盘免费间隔时，写出最早几点轮到（否则为空）
                    freeWait = FreeHostDownloader.WaitingUntilAll(
                        statuses.Where(it => it.Status == "0").Select(it => it.Url)) is { } fw ? fw.ToString("HH:mm") : "",
                    failed = statuses.Count(it => it.Status == "2" && !DownloadEngine.IsSkipped(it.Error)),
                    done = statuses.Count(it => it.Status == "1"),
                    files = statuses.Count,
                    speed = speed > 0 ? FormatSpeed(speed) : "",
                };
            });
        // 工具栏按钮的可用性：与桌面端同源（DownloadListActions.FlagsOf），避免两端灰显规则漂移
        var flags = DownloadListActions.FlagsOf(
            grouped.Values.SelectMany(v => v).Select(it => (it.Status, it.Error)));
        WriteJson(stream, 200, new
        {
            engine = new { running = DownloadEngine.IsRunning, stopRequested = DownloadEngine.StopRequested },
            actions = new
            {
                canClearNoLink = flags.HasNoLink,
                canClearAll = flags.HasRows,
                canReparseAll = flags.CanReparse,
            },
            sections,
            groups,
        });
    }

    private static (string Text, string Color) AggregateStatus(
        string workId, List<(string Status, string? Error)> items, DateTime? freeUntil = null)
    {
        var statuses = items.Select(i => i.Status).ToList();
        var done = statuses.Count(s => s == "1");
        if (statuses.Contains("3"))
            return ($"下载中 {done}/{statuses.Count}", "#60a5fa");
        if (statuses.Contains("5"))
            return ("搜索可用下载连接", "#facc15");
        // 等待的分卷全在等网盘免费间隔：写出几点轮到，免得以为卡住了（同文件行）
        if (statuses.Contains("0") && freeUntil is { } until)
            return ($"免费间隔 {until:HH:mm} {done}/{statuses.Count}", "#facc15");
        if (statuses.Contains("0"))
            return ($"等待下载 {done}/{statuses.Count}", "#facc15");
        if (statuses.Contains("4"))
            return ($"已暂停 {done}/{statuses.Count}", "#9aa4b2");
        // 源站没有的文件（'2' + skipped）是已终结的跳过项，不是待处理的失败：
        // 只要还有别的文件下成了，作品照常走完并入库，状态里只标注跳过了几个
        var skipped = items.Count(i => i.Status == "2" && DownloadEngine.IsSkipped(i.Error));
        var fails = items.Where(i => i.Status == "2" && !DownloadEngine.IsSkipped(i.Error)).ToList();
        if (fails.Count > 0)
        {
            // 失败原因一致 → 显示该原因（如"文件失效"/"流量用尽"）；原因不一 → 显示失败数
            var labels = fails.Select(f => ParseErrorLabel(f.Error)).Distinct().ToList();
            if (labels.Count == 1)
                return (labels[0], "#f87171");
            return ($"{fails.Count} 个解析失败", "#f87171");
        }
        if (skipped > 0 && done == 0)
            return ("源站无文件", "#f87171");   // 整篇都没归档：没有任何文件可下，不会入库
        if (statuses.Contains("6"))
            return ("无可用下载连接", "#f87171");
        if (DownloadEngine.UnzipProgress.TryGetValue(workId, out var unzip))
        {
            if (unzip.State == "pending") return ("待解压", "#facc15");
            if (unzip.State == "movewait") return ("等待移动", "#facc15");
            if (unzip.State == "moving") return ($"移动中 {unzip.Pct}%", "#a78bfa");
            return ($"解压中 {unzip.Pct}%", "#60a5fa");
        }
        return (skipped > 0 ? $"已完成（跳过 {skipped}）" : "已完成", "#4ade80");
    }

    private static (int Pct, double? Speed) FileProgress(string uuid, string status, string dbLong)
    {
        if (status == "1")
            return (100, null);
        if (status == "3" && DownloadEngine.DownloadProgress.TryGetValue(uuid, out var info) && info.Total > 0)
            return ((int)(info.Downloaded * 100 / info.Total), info.Speed);
        var pct = int.TryParse(dbLong, out var p) ? p : 0;
        return (Math.Min(pct, 100), null);
    }

    private static string FormatSpeed(double speed) => speed switch
    {
        >= 1024 * 1024 => $"{speed / 1024 / 1024:F1} MB/s",
        >= 1024 => $"{speed / 1024:F0} KB/s",
        _ => $"{speed:F0} B/s",
    };

    // 自动下载占位行的哨兵 url（每行唯一，避免 url 主键冲突）；不是真实直链，展示时按空名处理。
    private const string PlaceholderUrlPrefix = "placeholder://";
    private static string PlaceholderUrl(string uuid) => PlaceholderUrlPrefix + uuid;

    private static string FileNameOf(string url)
    {
        if (url.StartsWith(PlaceholderUrlPrefix, StringComparison.Ordinal))
            return "";   // 占位哨兵：无真实文件名，展示为空（分组状态已说明"搜索/无可用下载连接"）
        var raw = url.TrimEnd('/').Split('/')[^1].Split('?')[0];
        // 直链文件名常为 URL 百分号编码（asmr.one 的日文名尤其如此），解码为明文显示（对齐 WPF）
        try { return Uri.UnescapeDataString(raw); }
        catch (Exception) { return raw; }
    }

    private static async Task ApiUsageAsync(NetworkStream stream)
    {
        try
        {
            JsonElement? value;
            using (var client = new DebridLinkClient())
                value = await client.DownloadLimitsAsync();
            double? current = null;
            double resetSeconds = 0;
            var hostUsage = new List<object>();
            if (value is { } v && v.ValueKind == JsonValueKind.Object)
            {
                if (v.TryGetProperty("usagePercent", out var usage) && usage.ValueKind == JsonValueKind.Object)
                {
                    if (usage.TryGetProperty("current", out var cur) && cur.ValueKind == JsonValueKind.Number)
                        current = cur.GetDouble();
                    // 逐网盘用量：域名键（含'.'）即该网盘用量百分比（若 API 提供）
                    foreach (var prop in usage.EnumerateObject())
                        if (prop.Name.Contains('.') && prop.Value.ValueKind == JsonValueKind.Number)
                            hostUsage.Add(new { host = prop.Name, percent = (int)Math.Round(prop.Value.GetDouble()) });
                }
                if (v.TryGetProperty("nextResetSeconds", out var reset) && reset.ValueKind == JsonValueKind.Object &&
                    reset.TryGetProperty("value", out var rv) && rv.ValueKind == JsonValueKind.Number)
                    resetSeconds = rv.GetDouble();
            }
            var (accountFull, exhausted) = ExhaustedHosts();
            WriteJson(stream, 200, new
            {
                percent = current is { } c ? (int)Math.Min(100, Math.Round(c)) : (int?)null,
                resetText = FormatReset(resetSeconds),
                hosts = hostUsage,               // 各网盘用量（API 提供时）
                accountFull,                     // 账户总流量已用尽（maxData）
                exhausted,                       // 流量已用尽的网盘域名（maxDataHost）
            });
        }
        catch (Exception)
        {
            WriteJson(stream, 200, new
            {
                percent = (int?)null, resetText = "",
                hosts = Array.Empty<object>(), accountFull = false, exhausted = Array.Empty<string>(),
            });
        }
    }

    private static string FormatReset(double seconds)
    {
        var s = (long)seconds;
        if (s <= 0) return "";
        var hours = s / 3600;
        var minutes = s % 3600 / 60;
        if (hours > 0) return $"{hours}h{minutes}m";
        return minutes > 0 ? $"{minutes}m" : "<1m";
    }

    private static void ApiEngine(NetworkStream stream, Request req)
    {
        var action = ReadStringField(req.Body, "action");
        if (action == "start") DownloadEngine.Start();
        else if (action == "stop") DownloadEngine.Stop();
        WriteJson(stream, 200, new { running = DownloadEngine.IsRunning, stopRequested = DownloadEngine.StopRequested });
    }

    private static void ApiReparse(NetworkStream stream, Request req)
    {
        var id = ReadStringField(req.Body, "id");
        if (id.Length > 0)
        {
            Db.Execute("UPDATE \"download_list\" SET \"status\" = '0', \"error\" = NULL WHERE \"work_id\" = @w AND \"status\" = '2'",
                ("@w", id));
            DownloadEngine.Start();
        }
        WriteJson(stream, 200, new { ok = true });
    }

    /// <summary>全部重新解析：所有解析失败分卷('2')重新排队；所有"无可用下载连接"占位('6')清空空结果缓存后重新自动解析。</summary>
    private static void ApiReparseAll(NetworkStream stream)
    {
        DownloadListActions.ReparseAll();   // 与桌面端下载页共用同一实现
        WriteJson(stream, 200, new { ok = true });
    }

    /// <summary>
    /// 把「无可用下载连接」占位行重新交给后台自动解析（串行排队，解析前再确认占位行还在）。
    /// 解析流水线（AS 搜索 + 组合下载）在本类里，故由 <see cref="DownloadListActions.ReparseAll"/> 经此入口共用。
    /// </summary>
    internal static void KickAutoResolve(string workId, string placeholderUuid) =>
        _ = Task.Run(async () =>
        {
            await ResolveGate.WaitAsync();
            try
            {
                if (PlaceholderAlive(placeholderUuid))
                    await AutoResolveLinksAsync(workId, placeholderUuid);
            }
            finally
            {
                ResolveGate.Release();
            }
        });

    /// <summary>手动组合下载：对指定帖子按 rapidgator 优先、跨网盘同名分卷补齐失效者组合出完整分卷集并入队。
    /// 组不齐完整档案（某分卷在所有网盘均失效）则不入队，回传缺口信息由前端提示。</summary>
    private static async Task ApiCombineAsync(NetworkStream stream, Request req)
    {
        string id = "", postUrl = "", lib = "", folder = "";
        try
        {
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(req.Body));
            var root = doc.RootElement;
            id = (root.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "").ToUpperInvariant();
            postUrl = root.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
            lib = root.TryGetProperty("lib", out var l) ? l.GetString() ?? "" : "";
            folder = root.TryGetProperty("folder", out var f) ? f.GetString() ?? "" : "";
        }
        catch (Exception)
        {
            // 解析失败按非法请求处理
        }
        if (!WorkIdRe.IsMatch(id) || postUrl.Length == 0)
        {
            WriteJson(stream, 400, new { ok = false, error = "请求参数错误" });
            return;
        }
        List<string> urls;
        try { (urls, _) = await AnimeSharing.GetWorkDownUrlsAsync(postUrl); }
        catch (Exception) { urls = []; }
        if (urls.Count == 0)
        {
            WriteJson(stream, 200, new { ok = false, error = "该帖未找到可用网盘链接" });
            return;
        }
        PostCombiner.PostAssembly? asm;
        using (var client = LinkChecker.MakeClient())
            asm = await PostCombiner.AssembleAsync(urls, client);
        if (asm == null || asm.Urls.Count == 0)
        {
            WriteJson(stream, 200, new { ok = false, error = "所有网盘分卷均失效，无可用下载" });
            return;
        }
        if (!asm.Complete)
        {
            WriteJson(stream, 200, new
            {
                ok = false,
                error = $"仅凑齐 {asm.Urls.Count}/{asm.TotalParts} 个分卷（部分分卷在所有网盘均失效），无法组合完整下载",
            });
            return;
        }
        // 完整：入队真实分卷（状态 '0'）
        foreach (var downUrl in asm.Urls)
            Db.Execute(
                "INSERT OR REPLACE INTO \"download_list\" (\"UUID\", \"work_id\", \"url\", \"status\", \"long\", \"delete\") " +
                "VALUES (@uuid, @w, @url, '0', '0', '1')",
                ("@uuid", Guid.NewGuid().ToString()), ("@w", id), ("@url", downUrl));
        RecordWork(id, SearchCache.GetValueOrDefault(id));
        if (folder.Length > 0)
            DownloadEngine.SetWorkTargetPath(id, folder, lib.Length > 0 ? lib : null);
        _ = EnsureWorkMetaAsync(id);
        if (asm.RapidgatorParts < asm.TotalParts)
            Logger.Info($"{id} 手动组合下载：rapidgator {asm.RapidgatorParts}/{asm.TotalParts} 分卷有效，其余分卷用其它网盘同名分卷补齐");
        DownloadEngine.Start();
        WriteJson(stream, 200, new { ok = true, total = asm.TotalParts, rapidgatorParts = asm.RapidgatorParts });
    }

    /// <summary>单独停止某作品下载（停到断点，镜像下载页"停止"）。</summary>
    private static void ApiPauseWork(NetworkStream stream, Request req)
    {
        var id = ReadStringField(req.Body, "id");
        if (id.Length > 0)
            DownloadEngine.PauseWork(id);
        WriteJson(stream, 200, new { ok = true });
    }

    /// <summary>单独继续某作品下载（解除暂停并启动引擎，镜像下载页"下载"）。</summary>
    private static void ApiResumeWork(NetworkStream stream, Request req)
    {
        var id = ReadStringField(req.Body, "id");
        if (id.Length > 0)
            DownloadEngine.ResumeWork(id);
        WriteJson(stream, 200, new { ok = true });
    }

    /// <summary>单独删除某作品（移除下载列表与作品记录、删下载缓存，镜像下载页"删除"）。</summary>
    private static void ApiDeleteWork(NetworkStream stream, Request req)
    {
        var id = ReadStringField(req.Body, "id");
        if (id.Length > 0)
            DownloadEngine.DeleteWork(id);
        WriteJson(stream, 200, new { ok = true });
    }

    /// <summary>把 debrid-link 解析失败错误码翻译为可读说明（镜像 DownloadPage.MapParseError）。</summary>
    private static string MapParseError(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
            return "解析失败（原因未知，可能是链接失效或网盘不支持）";
        return raw switch
        {
            // 直链源（asmr / fanbox）源站 404：不是解析失败，是源站压根没有这个文件
            "skipped" => "源站没有这个文件，已跳过",
            "badToken" => "debrid-link API Key 无效或已过期",
            "maxData" or "maxDataHost" => "debrid-link 流量额度已用尽",
            "maxLink" or "maxLinkHost" => "链接数超过 debrid-link 限制",
            "hostUnsupported" or "notDebrid" or "hostNotValid" or "noServer"
                => "该网盘不被 debrid-link 支持",
            "notFreeHost" or "hostNotFree" or "disabledHost" or "disabledServerHost"
                => "该网盘需要会员或已停用",
            "fileNotFound" or "fileUnavailable" or "notFound" or "fileError"
                => "文件已失效或被删除",
            // fanbox 投稿正文里的谷歌网盘链接（见 GoogleDriveClient）
            GoogleDriveClient.QuotaError => "该文件的谷歌网盘下载配额已用尽，通常 24 小时后恢复",
            GoogleDriveClient.GoneError => "谷歌网盘文件已失效、被删除或未公开分享",
            GoogleDriveClient.FetchError => "连不上谷歌网盘（检查代理设置）",
            FantiaApi.LoginError => "Fantia 未登录或登录已失效（请到「账号访问」登录后点「全部重新解析」）",
            FantiaApi.LockedError => "当前 Fantia 账号看不到这个文件（未加入对应的支援档位）",
            FantiaApi.FetchError => "连不上 fantia.jp（检查网络与代理设置）",
            DownloadEngine.RetryFailedError => "下载超时或连接中断，已自动重试 3 次仍失败（可点「全部重新解析」再试）",
            "floodDetected" => "请求过于频繁，请稍后再试",
            "badFileType" => "不支持的文件类型",
            // debrid-link 不可用时的网盘免费下载（见 FreeHostDownloader）
            FreeHostDownloader.UnsupportedError => "debrid-link 不可用，且该网盘不支持免费下载（只支持 rapidgator / katfile）",
            FreeHostDownloader.PremiumOnlyError => "该文件只能会员下载（rapidgator 免费账户只能下 500MB 以内的文件）",
            FreeHostDownloader.CaptchaError => "网盘免费下载的人机验证多次没有自动通过（可点「全部重新解析」再试）",
            FreeHostDownloader.BrowserError => "网盘免费下载需要内嵌浏览器（WebView2），这台电脑上打不开",
            FreeHostDownloader.FetchError => "网盘免费下载页面多次没有走通（可点「全部重新解析」再试）",
            _ => $"解析失败（{raw}）",
        };
    }

    /// <summary>该失败错误码是否为 debrid-link 流量额度用尽（maxData=账户级，maxDataHost=单网盘级）。</summary>
    private static bool IsTrafficError(string? error) => error is "maxData" or "maxDataHost";

    /// <summary>把 debrid-link 解析失败错误码翻译成简短状态标签（用于状态列；完整说明见 MapParseError，镜像 WPF）。</summary>
    private static string ParseErrorLabel(string? raw) => raw switch
    {
        "skipped" => "源站无此文件",
        "maxData" or "maxDataHost" => "流量用尽",
        "fileNotFound" or "fileUnavailable" or "notFound" or "fileError" => "文件失效",
        GoogleDriveClient.QuotaError => "云盘限额",
        GoogleDriveClient.GoneError => "云盘文件失效",
        GoogleDriveClient.FetchError => "云盘连不上",
        FantiaApi.LoginError => "需登录",
        FantiaApi.LockedError => "档位不足",
        FantiaApi.FetchError => "站点连不上",
        "hostUnsupported" or "notDebrid" or "hostNotValid" or "noServer" => "网盘不支持",
        "notFreeHost" or "hostNotFree" or "disabledHost" or "disabledServerHost" => "需会员",
        "badToken" => "Key 无效",
        DownloadEngine.RetryFailedError => "重试失败",
        "floodDetected" => "请求频繁",
        "badFileType" => "类型不支持",
        "maxLink" or "maxLinkHost" => "超链接数",
        FreeHostDownloader.UnsupportedError => "网盘不支持",
        FreeHostDownloader.PremiumOnlyError => "需会员",
        FreeHostDownloader.CaptchaError => "验证未通过",
        FreeHostDownloader.BrowserError => "浏览器不可用",
        FreeHostDownloader.FetchError => "免费下载失败",
        _ => "解析失败",
    };

    /// <summary>当前下载列表中因流量用尽而解析失败的网盘：AccountFull=账户总流量(maxData)，Hosts=各单网盘(maxDataHost)。</summary>
    private static (bool AccountFull, List<string> Hosts) ExhaustedHosts()
    {
        var rows = Db.Select(
            "SELECT DISTINCT \"url\", \"error\" FROM \"download_list\" WHERE \"status\" = '2' AND \"error\" IN ('maxData', 'maxDataHost')");
        var accountFull = false;
        var hosts = new List<string>();
        foreach (var r in rows ?? [])
        {
            var err = r[1] as string ?? "";
            if (err == "maxData")
                accountFull = true;
            else
            {
                var h = HostOf(r[0] as string ?? "");
                if (h.Length > 0 && !hosts.Contains(h))
                    hosts.Add(h);
            }
        }
        return (accountFull, hosts);
    }

    /// <summary>把作品移动到另一个媒体库（镜像详情页"移动媒体库"）。</summary>
    private static async Task ApiMoveWorkAsync(NetworkStream stream, Request req)
    {
        string id = "", lib = "", folder = "";
        try
        {
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(req.Body));
            var root = doc.RootElement;
            id = (root.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "").ToUpperInvariant();
            lib = root.TryGetProperty("lib", out var l) ? l.GetString() ?? "" : "";
            folder = root.TryGetProperty("folder", out var f) ? f.GetString() ?? "" : "";
        }
        catch (Exception)
        {
            // 解析失败按非法请求处理
        }
        if (id.Length == 0 || lib.Length == 0 || folder.Length == 0)
        {
            WriteJson(stream, 400, new { ok = false, message = "参数缺失" });
            return;
        }
        var (ok, message) = await MediaLibraryService.MoveWorkToLibraryAsync(id, lib, folder);
        WriteJson(stream, 200, new { ok, message });
    }

    /// <summary>彻底删除作品：作品文件夹与数据库记录一并清除（镜像详情页"删除作品"，不可恢复）。</summary>
    private static async Task ApiDeleteLibWorkAsync(NetworkStream stream, Request req)
    {
        var id = ReadStringField(req.Body, "id").ToUpperInvariant();
        if (id.Length == 0)
        {
            WriteJson(stream, 400, new { ok = false, message = "参数缺失" });
            return;
        }
        var (ok, message) = await MediaLibraryService.DeleteWorkAsync(id);
        WriteJson(stream, 200, new { ok, message });
    }

    /// <summary>媒体库作用域作品搜索（镜像 MediaLibPage.ShowScopedSearch）：
    /// 媒体库根搜全部库；指定 lib/genre/type 时限定该范围；按 RJ号/作品名 LIKE 匹配已品悦作品。</summary>
    private static void ApiSearchWorks(NetworkStream stream, Request req)
    {
        var kw = (req.Query.GetValueOrDefault("kw") ?? "").Trim();
        if (kw.Length == 0)
        {
            WriteJson(stream, 200, new { works = Array.Empty<object>() });
            return;
        }
        var lib = req.Query.GetValueOrDefault("lib");
        var genre = req.Query.GetValueOrDefault("genre");
        var type = req.Query.GetValueOrDefault("type");
        var conds = new List<string> { "\"works\".\"state\" = '已品悦'", "(\"works\".\"work_id\" LIKE @kw OR \"works\".\"work_name\" LIKE @kw)" };
        var pars = new List<(string, object?)> { ("@kw", $"%{kw}%") };
        var join = "";
        if (!string.IsNullOrEmpty(genre))
        {
            join = "JOIN \"work_genres\" g ON g.\"work_id\" = \"works\".\"work_id\" ";
            conds.Add("g.\"genre\" = @g");
            pars.Add(("@g", genre));
        }
        else if (!string.IsNullOrEmpty(type))
        {
            conds.Add("\"works\".\"work_type\" = @t");
            pars.Add(("@t", type));
        }
        else if (!string.IsNullOrEmpty(lib))
        {
            conds.Add("\"works\".\"library\" = @lib");
            pars.Add(("@lib", lib));
        }
        var rows = Db.Select(
            "SELECT \"works\".\"work_id\", \"works\".\"work_name\", \"works\".\"maker_name\", " +
            "\"works\".\"work_type\", \"works\".\"age_category\", \"works\".\"cover\" FROM \"works\" " +
            join + "WHERE " + string.Join(" AND ", conds) + " " +
            WorkOrderClause(GetInt(req, "sort")).Replace("{p}", "\"works\"."),
            pars.ToArray());
        WriteJson(stream, 200, new { works = WorksJson(rows) });
    }

    private static void ApiResearch(NetworkStream stream, Request req)
    {
        var id = ReadStringField(req.Body, "id");
        if (id.Length > 0)
            DownloadEngine.PurgeWorkDownload(id);
        WriteJson(stream, 200, new { ok = true });
    }

    // 清理动作与桌面端下载页共用 DownloadListActions 的同一实现
    private static void ApiClearAll(NetworkStream stream)
    {
        DownloadListActions.ClearAll();
        WriteJson(stream, 200, new { ok = true });
    }

    /// <summary>清除"无可用下载连接"（占位状态 '6'）的作品：删占位行，并清掉因此残留的"下载中"作品记录。</summary>
    private static void ApiClearNoLink(NetworkStream stream)
    {
        DownloadListActions.ClearNoLink();
        WriteJson(stream, 200, new { ok = true });
    }

    private static void ApiDownloaded(NetworkStream stream)
    {
        var rows = Db.Select(
            "SELECT \"work_id\", \"work_name\", \"maker_name\", \"work_type\", \"state\", \"down_time\" " +
            "FROM \"works\" ORDER BY \"down_time\" DESC, \"work_id\" DESC");
        var works = (rows ?? []).Select(r =>
        {
            var t = r[5]?.ToString() ?? "";
            return new
            {
                id = r[0] as string ?? "", name = r[1] as string ?? "", maker = r[2] as string ?? "",
                type = r[3] as string ?? "", state = r[4] as string ?? "",
                downTime = t.Length > 19 ? t[..19] : t,
            };
        });
        WriteJson(stream, 200, new { works });
    }

    private static void ApiMark(NetworkStream stream, Request req)
    {
        var id = ReadStringField(req.Body, "id");
        if (id.Length > 0)
            Db.Execute("UPDATE \"works\" SET \"state\" = '已品悦' WHERE \"work_id\" = @w AND \"state\" = '已下载'",
                ("@w", id));
        WriteJson(stream, 200, new { ok = true });
    }

    // ---------- API：设置 ----------

    private static void ApiSettings(NetworkStream stream)
    {
        var (proxyOpen, proxyHost, proxyPort, proxyType) = AppConfig.ReadProxySetting();
        var libs = AppConfig.ReadMediaLibs().Select(l => new { name = l.Name, folders = l.Folders });
        WriteJson(stream, 200, new
        {
            downpath = AppConfig.DownloadPath,
            autoDownload = AppConfig.AutoDownload,
            autoUnzip = AppConfig.AutoUnzip,
            unzipPasswords = AppConfig.UnzipPasswordsText,   // 压缩包密码而非账号凭据，可回传编辑
            proxy = new { open = proxyOpen == "True", host = proxyHost, port = proxyPort, type = proxyType },
            // 敏感值不回传明文，仅告知是否已设置（编辑时留空表示不修改）
            debridKeySet = AppConfig.DebridApiKey.Length > 0,
            freeHost = new { enabled = AppConfig.FreeHostEnabled, interval = AppConfig.FreeHostInterval },
            downProc = AppConfig.DownloadProcesses,
            minSpeed = AppConfig.MinSpeedKb,
            speedLimit = AppConfig.SpeedLimitKb,
            language = AppConfig.Language,
            languages = I18n.Languages.Select(l => new { code = l.Code, name = l.Name }),
            logLevel = AppConfig.Read("loglevel", "level", "info"),
            encoding = AppConfig.SysEncoding,
            mediaLibs = libs,                       // 只读：文件夹管理需桌面端原生选择器
            web = new { enabled = AppConfig.WebEnabled, port = AppConfig.WebPort, password = AppConfig.WebPassword.Length > 0 },
            imageHost = new
            {
                enabled = AppConfig.ImageHostEnabled,
                baseUrl = AppConfig.ImageHostBaseUrl,
                project = AppConfig.ImageHostProject,
                tokenSet = AppConfig.ImageHostToken.Length > 0,   // 同 debrid：敏感值不回传明文
                mapped = ImageHostService.MappedCount,
            },
        });
    }

    /// <summary>
    /// 设置页 Debrid-Link API Key 的「👁 查看」：要再输一次访问密码才回传明文。
    /// 没设访问密码时没有可核对的东西，一律拒绝（否则局域网里谁打开页面都能看）。
    /// 失败次数与登录共用同一套限流，免得这里成了绕开登录锁定的猜密码口子。
    /// </summary>
    private static void ApiDebridKeyReveal(NetworkStream stream, Request req)
    {
        lock (Sync)
        {
            if (DateTime.UtcNow < _lockoutUntil)
            {
                WriteJson(stream, 429, new { ok = false, error = "尝试过于频繁，请稍后再试" });
                return;
            }
        }
        if (!_authRequired)
        {
            WriteJson(stream, 200, new { ok = false, error = "未设置访问密码，无法在网页查看，请在桌面端查看" });
            return;
        }
        if (!TokensEqual(Sha256Hex(ReadStringField(req.Body, "password")), _expectedToken))
        {
            lock (Sync)
            {
                if (++_loginFails >= MaxLoginFails)
                {
                    _lockoutUntil = DateTime.UtcNow + LockoutWindow;
                    _loginFails = 0;
                }
            }
            WriteJson(stream, 200, new { ok = false, error = "密码错误" });
            return;
        }
        lock (Sync) { _loginFails = 0; }
        WriteJson(stream, 200, new { ok = true, key = AppConfig.DebridApiKey });
    }

    private static void ApiSettingsWrite(NetworkStream stream, Request req)
    {
        string section = "", key = "", value = "";
        try
        {
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(req.Body));
            var root = doc.RootElement;
            section = (root.TryGetProperty("section", out var s) ? s.GetString() ?? "" : "").ToLowerInvariant();
            key = (root.TryGetProperty("key", out var k) ? k.GetString() ?? "" : "").ToLowerInvariant();
            value = root.TryGetProperty("value", out var v) ? v.GetString() ?? "" : "";
        }
        catch (Exception)
        {
            // 解析失败按非法请求处理
        }
        if (!SettingsWhitelist.Contains($"{section}/{key}"))
        {
            WriteJson(stream, 400, new { error = "该项不可从网页修改" });
            return;
        }
        // 语言变更需在 UI 线程应用（会触发各页面实时重译）
        if (section == "language" && key == "lang")
            System.Windows.Application.Current?.Dispatcher.Invoke(() => I18n.ApplyLanguage(value));
        else
            AppConfig.Write(section, key, value);
        // 图床配置变了：内存映射按新的启用状态/地址重建，并顺手补传缺的封面
        if (section == "image_host")
        {
            ImageHostService.Invalidate();
            ImageHostService.Kick();
        }
        WriteJson(stream, 200, new { ok = true });
    }

    // ---------- API：媒体库管理 ----------

    /// <summary>媒体库列表（含文件夹），供设置页管理区渲染/刷新。</summary>
    private static void ApiMediaLibs(NetworkStream stream)
    {
        var libs = AppConfig.ReadMediaLibs().Select(l => new { name = l.Name, folders = l.Folders, view = l.View });
        WriteJson(stream, 200, new { libs });
    }

    /// <summary>媒体库卡片 ⚙ 设置里的改名 / 默认视图（字段缺省即不改）。改名成功回传新名字。</summary>
    private static void ApiLibUpdate(NetworkStream stream, Request req)
    {
        var name = ReadStringField(req.Body, "name").Trim();
        var rename = ReadStringField(req.Body, "rename").Trim();
        var view = ReadStringField(req.Body, "view");
        string? err = null;
        lock (LibSync)
        {
            if (view.Length > 0)
                MediaLibraryService.SetLibraryView(name, view);
            if (rename.Length > 0)
                err = MediaLibraryService.RenameLibrary(name, rename);
        }
        if (err != null) WriteJson(stream, 400, new { error = err });
        else WriteJson(stream, 200, new { ok = true, name = rename.Length > 0 ? rename : name });
    }

    private static void ApiLibCreate(NetworkStream stream, Request req)
    {
        var name = ReadStringField(req.Body, "name").Trim();
        if (name.Length == 0)
        {
            WriteJson(stream, 400, new { error = "名称不能为空" });
            return;
        }
        string? err = null;
        lock (LibSync)
        {
            var libs = AppConfig.ReadMediaLibs();
            if (libs.Any(l => l.Name == name))
                err = "已存在同名媒体库";
            else
            {
                libs.Add(new MediaLib { Name = name });
                AppConfig.WriteMediaLibs(libs);
            }
        }
        if (err != null) WriteJson(stream, 400, new { error = err });
        else WriteJson(stream, 200, new { ok = true });
    }

    private static void ApiLibDelete(NetworkStream stream, Request req)
    {
        var name = ReadStringField(req.Body, "name").Trim();
        bool removed;
        lock (LibSync)
        {
            var libs = AppConfig.ReadMediaLibs();
            removed = libs.RemoveAll(l => l.Name == name) > 0;
            if (removed) AppConfig.WriteMediaLibs(libs);
        }
        // 不删本地文件，仅解除作品与该库的关联（镜像桌面端 DeleteLib）
        if (removed)
            Db.Execute("UPDATE \"works\" SET \"library\" = NULL WHERE \"library\" = @l", ("@l", name));
        WriteJson(stream, 200, new { ok = removed });
    }

    private static void ApiLibAddFolder(NetworkStream stream, Request req)
    {
        var name = ReadStringField(req.Body, "name").Trim();
        var folderRaw = ReadStringField(req.Body, "folder").Trim();
        if (folderRaw.Length == 0)
        {
            WriteJson(stream, 400, new { error = "路径不能为空" });
            return;
        }
        string folder;
        try { folder = Path.GetFullPath(folderRaw); }
        catch (Exception) { WriteJson(stream, 400, new { error = "路径格式错误" }); return; }
        if (!Directory.Exists(folder))
        {
            WriteJson(stream, 400, new { error = "文件夹不存在或无法访问（需为运行本程序的电脑上的路径）" });
            return;
        }
        string? err = null;
        lock (LibSync)
        {
            var libs = AppConfig.ReadMediaLibs();
            var lib = libs.FirstOrDefault(l => l.Name == name);
            if (lib == null)
                err = "媒体库不存在";
            else if (libs.Any(l => l.Folders.Contains(folder)))
                err = "该文件夹已在某个媒体库中";
            else
            {
                lib.Folders.Add(folder);
                AppConfig.WriteMediaLibs(libs);
            }
        }
        if (err != null) WriteJson(stream, 400, new { error = err });
        else WriteJson(stream, 200, new { ok = true, folder });
    }

    private static void ApiLibRemoveFolder(NetworkStream stream, Request req)
    {
        var name = ReadStringField(req.Body, "name").Trim();
        var folder = ReadStringField(req.Body, "folder");
        var removed = false;
        lock (LibSync)
        {
            var libs = AppConfig.ReadMediaLibs();
            var lib = libs.FirstOrDefault(l => l.Name == name);
            if (lib != null && lib.Folders.Remove(folder))
            {
                removed = true;
                AppConfig.WriteMediaLibs(libs);
            }
        }
        WriteJson(stream, 200, new { ok = removed });
    }

    private static void ApiLibEditFolder(NetworkStream stream, Request req)
    {
        var name = ReadStringField(req.Body, "name").Trim();
        var folder = ReadStringField(req.Body, "folder");
        var to = ReadStringField(req.Body, "to");
        string? err;
        lock (LibSync)
            err = MediaLibraryService.ChangeLibraryFolder(name, folder, to);
        if (err != null) WriteJson(stream, 400, new { error = err });
        else WriteJson(stream, 200, new { ok = true });
    }

    private static void ApiLibScan(NetworkStream stream, Request req)
    {
        var name = ReadStringField(req.Body, "name").Trim();
        var force = ReadBoolField(req.Body, "force");
        MediaLibScanner.Scan(name, force);
        WriteJson(stream, 200, new { ok = true });
    }

    private static void ApiLibScanAll(NetworkStream stream)
    {
        MediaLibScanner.ScanAll();
        WriteJson(stream, 200, new { ok = true });
    }

    private static void ApiLibScanStatus(NetworkStream stream)
    {
        var (scanning, status) = MediaLibScanner.State();
        WriteJson(stream, 200, new { scanning, status });
    }

    private static async Task ApiDebridTestAsync(NetworkStream stream, Request req)
    {
        var key = ReadStringField(req.Body, "key");
        try
        {
            using var client = new DebridLinkClient(key.Length > 0 ? key : null);
            var info = await client.AccountInfosAsync();
            var ok = info is { } v && v.ValueKind == JsonValueKind.Object;
            WriteJson(stream, 200, new { ok });
        }
        catch (Exception)
        {
            WriteJson(stream, 200, new { ok = false });
        }
    }

    // ---------- API：图床 ----------

    /// <summary>
    /// 图床连接自检。地址/项目/Token 可由请求体临时覆盖（同 debrid 测试）：
    /// 设置页是失焦即存的，点"测试"时刚输入的值可能还没落库。
    /// </summary>
    private static async Task ApiImageHostTestAsync(NetworkStream stream, Request req)
    {
        var baseUrl = ReadStringField(req.Body, "baseUrl");
        var project = ReadStringField(req.Body, "project");
        var token = ReadStringField(req.Body, "token");
        var (ok, info, error) = await ImageHostClient.PingAsync(
            baseUrl.Length > 0 ? baseUrl : AppConfig.ImageHostBaseUrl,
            project.Length > 0 ? project : AppConfig.ImageHostProject,
            token.Length > 0 ? token : AppConfig.ImageHostToken);
        WriteJson(stream, 200, new { ok, error, name = info?.Name ?? "", count = info?.ImageCount ?? 0 });
    }

    /// <summary>手动触发封面迁移（未开启图床时为空操作；已在迁移中则忽略，不会并发跑两轮）。</summary>
    private static void ApiImageHostMigrate(NetworkStream stream)
    {
        ImageHostService.Kick();
        var (running, status) = ImageHostService.State();
        WriteJson(stream, 200, new { ok = true, running, status });
    }

    private static void ApiImageHostStatus(NetworkStream stream)
    {
        var (running, status) = ImageHostService.State();
        WriteJson(stream, 200, new
        {
            running,
            status,
            active = ImageHostService.Active,
            mapped = ImageHostService.MappedCount,
            pending = ImageHostService.PendingCount,
            idle = ImageHostService.IdleSummary(),
        });
    }

    // ---------- API：FANBOX（pawchive）----------

    /// <summary>
    /// 请求针对哪个来源 / 账号（作品搜索里 FANBOX 面板的几个来源），查询串或请求体里的 site：
    ///   空 → pawchive，返回 null；
    ///   cc:&lt;pixiv 用户号&gt;（只写 cc 即第一个账号）→ fanbox.cc 官方站，返回该用户号；
    ///   ft → Fantia 匿名，ft:&lt;Fantia 用户号&gt; → 用该账号访问 Fantia，返回 <see cref="FanboxSource.FantiaKey"/>。
    /// 账号不存在（已退出登录）时 FANBOX 当 pawchive、Fantia 当匿名处理——来源下拉里本来也不会再有这一项。
    /// </summary>
    private static string? FanboxAccount(Request req)
    {
        var site = req.Query.GetValueOrDefault("site") ?? "";
        if (site.Length == 0 && req.Body.Length > 0)
            site = ReadStringField(req.Body, "site");
        if (site == "ft" || site.StartsWith("ft:", StringComparison.Ordinal))
        {
            var ftUid = site.Length > 3 ? site[3..] : "";
            return FanboxSource.FantiaKey(FanboxSource.FantiaAccounts.Any(a => a.UserId == ftUid) ? ftUid : "");
        }
        if (site != "cc" && !site.StartsWith("cc:", StringComparison.Ordinal))
            return null;
        var accounts = FanboxSource.OfficialAccounts;
        var uid = site.Length > 3 ? site[3..] : accounts.FirstOrDefault()?.UserId;
        return uid != null && accounts.Any(a => a.UserId == uid) ? uid : null;
    }

    /// <summary>
    /// 搜索作家。pawchive 站点无服务端搜索接口，由 PawchiveApi 在本地缓存的作家表上过滤；
    /// 官方站走站点自己的作家搜索，关键字为空时列出「我赞助的 + 我关注的」作家。
    /// </summary>
    private static async Task ApiFanboxArtistsAsync(NetworkStream stream, Request req)
    {
        var account = FanboxAccount(req);
        var official = account != null;
        var keyword = (req.Query.GetValueOrDefault("q") ?? "").Trim();
        if (keyword.Length == 0 && account == null)
        {
            WriteJson(stream, 200, new { artists = Array.Empty<object>() });
            return;
        }
        var (list, error) = await FanboxSource.SearchArtistsAsync(account, keyword);
        WriteJson(stream, 200, new
        {
            artists = list.Select(a => new
            {
                id = a.Id, service = a.Service, name = a.Name, publicId = a.PublicId,
                favorited = a.Favorited,
                icon = a.IconUrl,
            }),
            error,
            mine = FanboxSource.ListsMine(account) && keyword.Length == 0,
            cachedAt = official ? "" : PawchiveApi.CreatorsCachedAt?.ToString("yyyy-MM-dd HH:mm") ?? "",
        });
    }

    /// <summary>作家主页的一批作品（pawchive 每页 50 条，官方站一次拼几页），附带本地已下载/下载中状态。</summary>
    private static async Task ApiFanboxPostsAsync(NetworkStream stream, Request req)
    {
        var account = FanboxAccount(req);
        var artistId = (req.Query.GetValueOrDefault("id") ?? "").Trim();
        if (!FanboxSource.IsArtistKey(account, artistId))
        {
            WriteJson(stream, 400, new { error = "bad request" });
            return;
        }
        var offset = GetInt(req, "o");
        var (posts, hasMore, error) = await FanboxSource.GetPostsAsync(account, artistId, offset);
        if (posts is null)
        {
            WriteJson(stream, 502, new { error = error ?? "获取作品列表失败" });
            return;
        }
        // 首页顺带带上作家信息，供标题与入队时落库使用
        object? artist = null;
        if (offset == 0 && await FanboxSource.GetArtistAsync(account, artistId) is { } a)
            artist = new { id = a.Id, service = a.Service, name = a.Name, publicId = a.PublicId, icon = a.IconUrl };

        var states = await FanboxSource.PostStatesAsync(account, artistId);
        WriteJson(stream, 200, new
        {
            artist,
            // 作家主页的「+ 监控作家 / ✓ 已监控」按钮据此定文案（监控只走 pawchive，官方站来源不显示该按钮）
            watched = FanboxSource.CanWatch(account) && FanboxWatchService.IsWatched(artistId),
            posts = posts.Select(p => new
            {
                id = p.Id, title = p.Title, published = p.Published, tags = p.Tags,
                files = p.Files.Count,
                // 正文里的谷歌网盘链接数：作品本体常常只在网盘上，卡片要标出来
                drive = GoogleDriveClient.LinksIn(p.Links).Count,
                cover = p.CoverUrl,
                state = states.GetValueOrDefault(p.Id, ""),
                // 官方站：当前账号的赞助档位看不到这篇（卡片置灰、不可选），fee 是所需金额
                locked = p.Locked, fee = p.FeeRequired,
            }),
            hasMore,
        });
    }

    /// <summary>
    /// 单篇作品详情（「查看内容」用）：正文 + 图片清单 + 非图片附件清单。
    ///
    /// 图片给出 thumb（预览：pawchive 800px / 官方站 thumbnailUrl），经 /api/fanbox/image 代理——
    /// 浏览器直连 pawchive 会被防护网关按浏览器 UA 403（见 PawchiveApi.UserAgent），官方站则要登录 cookie。
    /// </summary>
    private static async Task ApiFanboxPostAsync(NetworkStream stream, Request req)
    {
        var account = FanboxAccount(req);
        var artistId = (req.Query.GetValueOrDefault("id") ?? "").Trim();
        var postId = (req.Query.GetValueOrDefault("post") ?? "").Trim();
        if (!FanboxSource.IsArtistKey(account, artistId) || !IsPlainId(postId))
        {
            WriteJson(stream, 400, new { error = "bad request" });
            return;
        }
        var (post, error) = await FanboxSource.GetPostAsync(account, artistId, postId);
        if (post is null)
        {
            WriteJson(stream, 502, new { error = error ?? "获取作品详情失败" });
            return;
        }
        var images = post.Files.Where(f => PawchiveApi.IsImageName(f.Name)).ToList();
        var states = await FanboxSource.PostStatesAsync(account, artistId);
        WriteJson(stream, 200, new
        {
            id = post.Id, title = post.Title, published = post.Published,
            tags = post.Tags, content = post.Content, files = post.Files.Count,
            state = states.GetValueOrDefault(post.Id, ""),
            locked = post.Locked, fee = post.FeeRequired,
            images = images.Select(f => new { name = f.Name, thumb = f.Thumb }),
            // 压缩包 / PDF / 视频等：只报名字，下载走入队
            others = post.Files.Where(f => !PawchiveApi.IsImageName(f.Name)).Select(f => new { name = f.Name }),
            // 正文里的谷歌网盘链接：入队时会一并下载（文件夹展开成逐个文件），下完照常解压
            links = GoogleDriveClient.LinksIn(post.Links).Select(l => new
            {
                url = l.Url, folder = l.Kind == DriveLinkKind.Folder,
            }),
        });
    }

    /// <summary>
    /// 把选中的作品加入下载队列。请求只带作品号，作品详情由服务端重新拉取
    /// （pawchive 逐页翻到选中的作品都凑齐为止，官方站逐篇取详情），避免信任前端提交的文件清单。
    /// </summary>
    private static async Task ApiFanboxEnqueueAsync(NetworkStream stream, Request req)
    {
        var account = FanboxAccount(req);
        var artistId = ReadStringField(req.Body, "id").Trim();
        var wanted = ReadStringArray(req.Body, "posts").Where(IsPlainId).Distinct().ToList();
        var lib = ReadStringField(req.Body, "lib");
        var folder = ReadStringField(req.Body, "folder");
        if (!FanboxSource.IsArtistKey(account, artistId) || wanted.Count == 0)
        {
            WriteJson(stream, 400, new { error = "bad request" });
            return;
        }

        var artist = await FanboxSource.GetArtistAsync(account, artistId)
                     ?? new PawchiveArtist { Id = artistId, Service = FanboxSource.ServiceOf(account) };
        var picked = await FanboxSource.ResolveForEnqueueAsync(account, artistId, wanted);
        if (picked.Count == 0)
        {
            WriteJson(stream, 404, new { error = "未找到选中的作品" });
            return;
        }

        // 记下是哪个账号入的队：下载官方站 / Fantia 附件时用它的 cookie（pawchive 与匿名 Fantia 为匿名）
        FanboxSource.Use(account);
        var result = await FanboxService.EnqueuePostsAsync(
            artist, picked, folder.Length > 0 ? folder : null, lib.Length > 0 ? lib : null);
        if (!result.Ok)
        {
            WriteJson(stream, 200, new { ok = false, error = result.Error ?? "入队失败" });
            return;
        }
        WriteJson(stream, 200, new
        {
            ok = true, posts = result.PostCount, files = result.FileCount, skipped = result.Skipped,
        });
    }

    /// <summary>
    /// 站上图片（作家头像/未下载作品的缩略图）代理：浏览器直连站点会受跨域、防盗链与代理设置影响，
    /// 统一由本服务按配置的代理取回。只放行 pawchive 域名及其子域、fanbox.cc、pximg.net 与 fantia.jp。
    /// </summary>
    private static async Task ApiFanboxImageAsync(NetworkStream stream, Request req)
    {
        var url = req.Query.GetValueOrDefault("url") ?? "";
        if (!FanboxSource.IsImageUrl(url) || !IsSafeFetchUrl(url))
        {
            WriteBytes(stream, 400, "Bad Request", "text/plain", []);
            return;
        }
        try
        {
            // 官方站的预览图同样只对有权限的账号开放：带上来源账号（site=cc:用户号）
            var (bytes, type) = await FanboxSource.GetImageAsync(url, FanboxAccount(req));
            // 站点的头像/横幅不带扩展名且常返回 application/octet-stream，按图片交付浏览器才会渲染
            if (!type.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                type = ContentType(url);
            WriteBytes(stream, 200, "OK", type, bytes, ("Cache-Control", "max-age=86400"));
        }
        catch (Exception)
        {
            WriteBytes(stream, 404, "Not Found", "text/plain", []);
        }
    }

    // ---------- API：使用帮助 ----------

    /// <summary>
    /// 整本手册按章节回给前端（lang 缺省时跟随程序界面语言）。正文已在服务端转成 HTML（文字均已转义），
    /// 与桌面端共用 HelpManual 的同一个解析器；整本几十 KB，前端每种语言只取一次。
    /// </summary>
    private static void ApiHelp(NetworkStream stream, Request req)
    {
        var lang = HelpManual.NormalizeLang(req.Query.GetValueOrDefault("lang"));
        WriteJson(stream, 200, new
        {
            lang,
            languages = HelpManual.Languages.Select(l => new { code = l.Code, name = l.Name }),
            chapters = HelpManual.Chapters(lang).Select(c => new { no = c.No, title = c.Title, html = HelpManual.RenderHtml(c) }),
        });
    }

    private static void ApiHelpImage(NetworkStream stream, Request req)
    {
        var bytes = HelpManual.Image(req.Query.GetValueOrDefault("name") ?? "");
        if (bytes == null)
        {
            WriteJson(stream, 404, new { error = "not found" });
            return;
        }
        WriteBytes(stream, 200, "OK", "image/png", bytes, ("Cache-Control", "max-age=86400"));
    }

    // ---------- API：账号访问 ----------

    /// <summary>
    /// 各站点已登录的账号。只回用户号、账号名与自动维护结果，cookie 本身一律不回给浏览器——
    /// 外部访问可能没设密码，凭据不该经网页再流出去。账号只能经「在服务器上登录」（/api/stream?login=）加入。
    /// </summary>
    private static void ApiAccounts(NetworkStream stream)
    {
        WriteJson(stream, 200, new
        {
            sites = AccountService.Sites.Select(site => new
            {
                site, title = AccountService.Title(site), multi = AccountService.AllowsMultiple(site),
                accounts = AccountService.List(site).Select(a => new
                {
                    uid = a.UserId, name = a.Name, canKeep = a.CanKeep,
                    // 最近一次 cookie 自动维护的结果（AccountKeeper），没维护过为 null
                    keep = a.Keep is { } k
                        ? new { at = k.At.ToString("MM-dd HH:mm"), ok = k.Ok, message = k.Message }
                        : null,
                }),
            }),
        });
    }

    private static async Task ApiAccountCheckAsync(NetworkStream stream, Request req)
    {
        var (ok, message) = await AccountService.CheckAsync(
            ReadStringField(req.Body, "site"), ReadStringField(req.Body, "uid"));
        WriteJson(stream, 200, new { ok, message });
    }

    private static void ApiAccountLogout(NetworkStream stream, Request req)
    {
        AccountService.Remove(ReadStringField(req.Body, "site"), ReadStringField(req.Body, "uid"));
        WriteJson(stream, 200, new { ok = true });
    }

    // ---------- API：FANBOX 作家监控 ----------

    /// <summary>监控列表 + 全局开关/默认间隔 + 当前检查状态。</summary>
    private static void ApiFanboxWatches(NetworkStream stream)
    {
        var (busy, status) = FanboxWatchService.State();
        WriteJson(stream, 200, new
        {
            enabled = AppConfig.FanboxWatchEnabled,
            interval = AppConfig.FanboxWatchInterval,
            choices = FanboxWatchService.IntervalChoices,
            busy,
            status,
            summary = FanboxWatchService.IdleSummary(),
            watches = FanboxWatchService.All().Select(w => new
            {
                id = w.ArtistId, service = w.Service, name = w.Display,
                interval = w.IntervalMin, enabled = w.Enabled,
                lastCheck = w.LastCheck, lastResult = w.LastResult,
                downloaded = w.Downloaded, lib = w.TargetLib,
                next = FanboxWatchService.NextCheckText(w),
                icon = PawchiveApi.IconUrl(w.Service, w.ArtistId),
            }),
        });
    }

    /// <summary>
    /// 添加作家监控。作家名由服务端重新取（不信任前端提交，与 fanbox 入队同理）；
    /// backfill=true 时连该作家现有的作品一起下，否则只管今后新发布的。
    /// </summary>
    private static async Task ApiFanboxWatchAddAsync(NetworkStream stream, Request req)
    {
        var artistId = ReadStringField(req.Body, "id").Trim();
        if (!IsPlainId(artistId))
        {
            WriteJson(stream, 400, new { error = "bad request" });
            return;
        }
        var artist = await PawchiveApi.GetArtistAsync(PawchiveApi.FanboxService, artistId)
                     ?? new PawchiveArtist { Id = artistId, Service = PawchiveApi.FanboxService };
        var (ok, message) = await FanboxWatchService.AddAsync(
            artist,
            ReadIntField(req.Body, "interval"),
            ReadBoolField(req.Body, "backfill"),
            ReadStringField(req.Body, "lib"),
            ReadStringField(req.Body, "folder"));
        WriteJson(stream, 200, new { ok, message });
    }

    /// <summary>改间隔 / 暂停恢复 / 移除（remove=true 时其余字段忽略）。</summary>
    private static void ApiFanboxWatchUpdate(NetworkStream stream, Request req)
    {
        var artistId = ReadStringField(req.Body, "id").Trim();
        if (!IsPlainId(artistId))
        {
            WriteJson(stream, 400, new { error = "bad request" });
            return;
        }
        if (ReadBoolField(req.Body, "remove"))
        {
            FanboxWatchService.Remove(artistId);
            WriteJson(stream, 200, new { ok = true });
            return;
        }
        if (ReadIntField(req.Body, "interval") is var interval and > 0)
            FanboxWatchService.SetInterval(artistId, interval);
        if (ReadOptionalBoolField(req.Body, "enabled") is { } on)
            FanboxWatchService.SetEnabled(artistId, on);
        WriteJson(stream, 200, new { ok = true });
    }

    /// <summary>立即检查（id 为空 = 全部）；检查在后台跑，前端轮询 /api/fanbox/watches 看进度。</summary>
    private static void ApiFanboxWatchCheck(NetworkStream stream, Request req)
    {
        var artistId = ReadStringField(req.Body, "id").Trim();
        if (artistId.Length == 0)
            _ = FanboxWatchService.CheckAllAsync();
        else if (IsPlainId(artistId))
            _ = FanboxWatchService.CheckNowAsync(artistId);
        WriteJson(stream, 200, new { ok = true });
    }

    /// <summary>全局开关 / 默认轮询间隔。</summary>
    private static void ApiFanboxWatchConfig(NetworkStream stream, Request req)
    {
        if (ReadOptionalBoolField(req.Body, "enabled") is { } on)
            AppConfig.FanboxWatchEnabled = on;
        if (ReadIntField(req.Body, "interval") is var interval and > 0)
            AppConfig.FanboxWatchInterval = interval;
        if (AppConfig.FanboxWatchEnabled)
            FanboxWatchService.Kick();
        WriteJson(stream, 200, new { ok = true });
    }

    // ---------- API：Pixiv 收藏监控 ----------

    /// <summary>
    /// 已登录的 pixiv 账号（每个账号一张卡：已监控 / 未监控）+ 监控记录 + 全局设置 + 当前检查状态。
    /// 账号退出登录后监控行仍在（<c>hasAccount=false</c>），重新登录同一账号即可接着用。
    /// </summary>
    private static void ApiPixivWatches(NetworkStream stream)
    {
        var (busy, status) = PixivWatchService.State();
        WriteJson(stream, 200, new
        {
            enabled = AppConfig.PixivWatchEnabled,
            interval = AppConfig.PixivWatchInterval,
            pace = AppConfig.PixivWatchPace,
            busy,
            status,
            summary = PixivWatchService.IdleSummary(),
            accounts = SiteAccounts.List(AccountService.Pixiv).Select(a => new { uid = a.UserId, name = a.DisplayName }),
            watches = PixivWatchService.All().Select(w => new
            {
                uid = w.UserId, name = w.Display, hasAccount = w.HasAccount,
                interval = w.IntervalMin, enabled = w.Enabled,
                lastCheck = w.LastCheck, lastResult = w.LastResult,
                downloaded = w.Downloaded, lib = w.TargetLib,
                next = PixivWatchService.NextCheckText(w),
            }),
        });
    }

    /// <summary>为某个 pixiv 账号开启收藏监控；history=true 时连历史收藏一起（慢速）下。</summary>
    private static async Task ApiPixivWatchAddAsync(NetworkStream stream, Request req)
    {
        var uid = ReadStringField(req.Body, "uid").Trim();
        if (!IsPlainId(uid))
        {
            WriteJson(stream, 400, new { error = "bad request" });
            return;
        }
        var (ok, message) = await PixivWatchService.AddAsync(
            uid,
            ReadIntField(req.Body, "interval"),
            ReadBoolField(req.Body, "history"),
            ReadStringField(req.Body, "lib"),
            ReadStringField(req.Body, "folder"));
        WriteJson(stream, 200, new { ok, message });
    }

    /// <summary>改间隔 / 暂停恢复 / 移除（remove=true 时其余字段忽略）。</summary>
    private static void ApiPixivWatchUpdate(NetworkStream stream, Request req)
    {
        var uid = ReadStringField(req.Body, "uid").Trim();
        if (!IsPlainId(uid))
        {
            WriteJson(stream, 400, new { error = "bad request" });
            return;
        }
        if (ReadBoolField(req.Body, "remove"))
        {
            PixivWatchService.Remove(uid);
            WriteJson(stream, 200, new { ok = true });
            return;
        }
        if (ReadIntField(req.Body, "interval") is var interval and > 0)
            PixivWatchService.SetInterval(uid, interval);
        if (ReadOptionalBoolField(req.Body, "enabled") is { } on)
            PixivWatchService.SetEnabled(uid, on);
        WriteJson(stream, 200, new { ok = true });
    }

    /// <summary>立即检查（uid 为空 = 全部）；检查在后台跑，前端轮询 /api/pixiv/watches 看进度。</summary>
    private static void ApiPixivWatchCheck(NetworkStream stream, Request req)
    {
        var uid = ReadStringField(req.Body, "uid").Trim();
        if (uid.Length == 0)
            _ = PixivWatchService.CheckAllAsync();
        else if (IsPlainId(uid))
            _ = PixivWatchService.CheckNowAsync(uid);
        WriteJson(stream, 200, new { ok = true });
    }

    /// <summary>全局开关 / 默认轮询间隔 / 批量入队间隔（秒）。</summary>
    private static void ApiPixivWatchConfig(NetworkStream stream, Request req)
    {
        if (ReadOptionalBoolField(req.Body, "enabled") is { } on)
            AppConfig.PixivWatchEnabled = on;
        if (ReadIntField(req.Body, "interval") is var interval and > 0)
            AppConfig.PixivWatchInterval = interval;
        if (ReadIntField(req.Body, "pace") is var pace and > 0)
            AppConfig.PixivWatchPace = pace;
        if (AppConfig.PixivWatchEnabled)
            PixivWatchService.Kick();
        WriteJson(stream, 200, new { ok = true });
    }

    // ---------- API：DLsite 社团监控 ----------

    /// <summary>监控列表 + 全局开关/默认间隔 + 当前检查状态。</summary>
    private static void ApiDlsiteWatches(NetworkStream stream)
    {
        var (busy, status) = DlsiteWatchService.State();
        WriteJson(stream, 200, new
        {
            enabled = AppConfig.DlsiteWatchEnabled,
            interval = AppConfig.DlsiteWatchInterval,
            busy,
            status,
            summary = DlsiteWatchService.IdleSummary(),
            watches = DlsiteWatchService.All().Select(w => new
            {
                id = w.MakerId, name = w.Display,
                interval = w.IntervalMin, enabled = w.Enabled,
                lastCheck = w.LastCheck, lastResult = w.LastResult,
                downloaded = w.Downloaded, lib = w.TargetLib,
                pending = DlsiteWatchService.PendingCount(w.MakerId),
                next = DlsiteWatchService.NextCheckText(w),
                icon = DlsiteMakerIcon.IconUrl(w.MakerId),   // ci-en 公开 CDN，浏览器直接取
            }),
        });
    }

    /// <summary>添加社团监控；backfill=true 时连该社团现有的作品一起（各找一次源）下。</summary>
    private static async Task ApiDlsiteWatchAddAsync(NetworkStream stream, Request req)
    {
        var makerId = ReadStringField(req.Body, "id").Trim().ToUpperInvariant();
        if (!MakerIdRe.IsMatch(makerId))
        {
            WriteJson(stream, 400, new { error = "bad request" });
            return;
        }
        var (ok, message) = await DlsiteWatchService.AddAsync(
            makerId,
            ReadIntField(req.Body, "interval"),
            ReadBoolField(req.Body, "backfill"),
            ReadStringField(req.Body, "lib"),
            ReadStringField(req.Body, "folder"));
        WriteJson(stream, 200, new { ok, message });
    }

    /// <summary>改间隔 / 暂停恢复 / 移除（remove=true 时其余字段忽略）。</summary>
    private static void ApiDlsiteWatchUpdate(NetworkStream stream, Request req)
    {
        var makerId = ReadStringField(req.Body, "id").Trim().ToUpperInvariant();
        if (!MakerIdRe.IsMatch(makerId))
        {
            WriteJson(stream, 400, new { error = "bad request" });
            return;
        }
        if (ReadBoolField(req.Body, "remove"))
        {
            DlsiteWatchService.Remove(makerId);
            WriteJson(stream, 200, new { ok = true });
            return;
        }
        if (ReadIntField(req.Body, "interval") is var interval and > 0)
            DlsiteWatchService.SetInterval(makerId, interval);
        if (ReadOptionalBoolField(req.Body, "enabled") is { } on)
            DlsiteWatchService.SetEnabled(makerId, on);
        WriteJson(stream, 200, new { ok = true });
    }

    /// <summary>立即检查（id 为空 = 全部）；检查在后台跑，前端轮询 /api/dlsite/watches 看进度。</summary>
    private static void ApiDlsiteWatchCheck(NetworkStream stream, Request req)
    {
        var makerId = ReadStringField(req.Body, "id").Trim().ToUpperInvariant();
        if (makerId.Length == 0)
            _ = DlsiteWatchService.CheckAllAsync();
        else if (MakerIdRe.IsMatch(makerId))
            _ = DlsiteWatchService.CheckNowAsync(makerId);
        WriteJson(stream, 200, new { ok = true });
    }

    /// <summary>全局开关 / 默认轮询间隔。</summary>
    private static void ApiDlsiteWatchConfig(NetworkStream stream, Request req)
    {
        if (ReadOptionalBoolField(req.Body, "enabled") is { } on)
            AppConfig.DlsiteWatchEnabled = on;
        if (ReadIntField(req.Body, "interval") is var interval and > 0)
            AppConfig.DlsiteWatchInterval = interval;
        if (AppConfig.DlsiteWatchEnabled)
            DlsiteWatchService.Kick();
        WriteJson(stream, 200, new { ok = true });
    }

    // ---------- E-Hentai 数据源 ----------

    /// <summary>
    /// 搜索画廊。贴画廊链接时直接认出那一本并只返回它（等价于站内直达），
    /// 否则按关键字搜；next 为上一页返回的游标（站点是游标翻页，没有页号）。
    /// </summary>
    private static async Task ApiEhSearchAsync(NetworkStream stream, Request req)
    {
        var keyword = (req.Query.GetValueOrDefault("q") ?? "").Trim();
        var next = (req.Query.GetValueOrDefault("next") ?? "").Trim();
        if (keyword.Length == 0 && next.Length == 0)
        {
            WriteJson(stream, 400, new { error = "bad request" });
            return;
        }

        // 贴的是画廊链接：跳过搜索，直接取这一本的元数据
        if (next.Length == 0 && EhentaiApi.ParseGalleryInput(keyword) is { } direct)
        {
            var one = await EhentaiApi.GetGalleryAsync(direct);
            WriteJson(stream, 200, new
            {
                items = one is null ? Array.Empty<object>() : [EhGalleryJson(one)],
                next = "",
                error = one is null ? "未找到该画廊（可能已下架，或里站需要登录 cookie）" : null,
            });
            return;
        }

        var result = await EhentaiApi.SearchAsync(keyword, next);
        WriteJson(stream, 200, new
        {
            items = result.Items.Select(EhGalleryJson),
            next = result.Next,
            error = result.Error,
        });
    }

    /// <summary>
    /// 单本画廊详情（「查看内容」用）：元数据 + 该列表页的缩略图，按 p 分页。
    ///
    /// 缩略图在画廊页上现成就有（匿名访问是 20 张拼成的雪碧图），取它**不消耗看图额度**，
    /// 所以整本的缩略图都能列出来；前端下拉到哪儿才要哪一页（p=0,1,2…）。
    /// 图片本体不在这里给：每张的直链都要单独进它的图片页才拿得到，
    /// 那是 /api/eh/page 的事，点开哪张才解析哪张。
    /// </summary>
    private static async Task ApiEhGalleryAsync(NetworkStream stream, Request req)
    {
        if (ParseEhRef(req) is not { } gref)
        {
            WriteJson(stream, 400, new { error = "bad request" });
            return;
        }
        var p = Math.Max(0, GetInt(req, "p"));

        // 元数据只在第一页取；往后翻页前端已经有了，不必再多发一次 API 请求
        object? gallery = null;
        var total = 0;
        if (p == 0)
        {
            var meta = await EhentaiApi.GetGalleryAsync(gref);
            if (meta is null)
            {
                WriteJson(stream, 502, new { error = "获取画廊信息失败" });
                return;
            }
            gallery = EhGalleryJson(meta);
            total = meta.FileCount;
        }

        var pages = await EhentaiApi.GetListingPageAsync(gref, p);
        if (pages is null)
        {
            WriteJson(stream, 502, new { error = "获取画廊图片列表失败" });
            return;
        }
        WriteJson(stream, 200, new
        {
            gallery,
            total,
            images = pages.Where(x => x.HasThumb).Select(x => new
            {
                index = x.Index,
                name = x.FileName,
                // 图片页的 key：前端拿它回请 /api/eh/page 看大图（省得服务端再翻一次列表页找）
                key = EhKeyOf(x.Url),
                // 逐张缩略图直接给地址；雪碧图则给「整张图 + 该格的位置与尺寸」，由前端用 CSS 裁出那一格
                thumb = x.Thumb,
                sprite = x.SpriteUrl,
                x = x.SpriteX,
                w = x.TileW,
                h = x.TileH,
            }),
            hasMore = pages.Count >= EhentaiApi.ListingPageSize,
        });
    }

    /// <summary>从图片页地址 /s/&lt;key&gt;/&lt;gid&gt;-&lt;n&gt; 里取出 key。</summary>
    private static string EhKeyOf(string imagePageUrl)
    {
        var parts = imagePageUrl.Split('/');
        return parts.Length >= 2 ? parts[^2] : "";
    }

    /// <summary>
    /// 按页码现解析、现取该页大图并直接回给浏览器。
    ///
    /// 站点每张图的直链都藏在它自己的图片页里、与访问者 IP 绑定又会过期，没法预先算好交给前端。
    /// 做成「一个页码一个地址」之后，灯箱只要把页码排成数组就能前后翻，
    /// 而浏览器显示到哪张才请求哪张——既是天然的懒加载，也不会为没看的图白耗看图额度。
    /// </summary>
    private static async Task ApiEhPageImageAsync(NetworkStream stream, Request req)
    {
        var page = GetInt(req, "page");
        var key = (req.Query.GetValueOrDefault("key") ?? "").Trim();
        if (ParseEhRef(req) is not { } gref || page <= 0 || !IsEhToken(key))
        {
            WriteBytes(stream, 400, "Bad Request", "text/plain", []);
            return;
        }

        var pageUrl = $"{EhentaiApi.Origin}/s/{key.ToLowerInvariant()}/{gref.Gid}-{page}";
        var link = await EhentaiApi.ResolveImageAsync(pageUrl, AppConfig.EhentaiOriginal);
        if (!link.Ok || !IsEhentaiUrl(link.Url))
        {
            // 额度用尽单独回 429，前端好照实提示，不至于看着像图坏了
            if (link.Throttled)
                WriteBytes(stream, 429, "Too Many Requests", "text/plain",
                    Encoding.UTF8.GetBytes("image quota exceeded"));
            else
                WriteBytes(stream, 502, "Bad Gateway", "text/plain",
                    Encoding.UTF8.GetBytes("resolve failed"));
            return;
        }

        try
        {
            using var client = Http.CreateClient(TimeSpan.FromSeconds(60));
            using var request = new HttpRequestMessage(HttpMethod.Get, link.Url);
            EhentaiApi.ApplyHeaders(request);
            using var resp = await client.SendAsync(request);
            resp.EnsureSuccessStatusCode();
            var bytes = await resp.Content.ReadAsByteArrayAsync();
            var type = resp.Content.Headers.ContentType?.MediaType ?? "";
            // 站点会把「需要登录」当正常页面回（HTTP 200 + 登录页 HTML），别把它当图片送给浏览器
            if (!type.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                WriteBytes(stream, 502, "Bad Gateway", "text/plain",
                    Encoding.UTF8.GetBytes("not an image"));
                return;
            }
            WriteBytes(stream, 200, "OK", type, bytes, ("Cache-Control", "max-age=3600"));
        }
        catch (Exception)
        {
            WriteBytes(stream, 404, "Not Found", "text/plain", []);
        }
    }

    /// <summary>
    /// 把选中的画廊加入下载队列。请求只带 gid/token，元数据与图片清单由服务端重新拉取，
    /// 不信任前端提交的内容（与 fanbox 入队同理）。
    /// </summary>
    private static async Task ApiEhEnqueueAsync(NetworkStream stream, Request req)
    {
        var refs = new List<EhGalleryRef>();
        foreach (var item in ReadStringArray(req.Body, "items"))
        {
            // 前端提交的每一项形如 "gid:token"
            var parts = item.Split(':', 2);
            if (parts.Length == 2 && long.TryParse(parts[0], out var gid) && IsEhToken(parts[1]))
                refs.Add(new EhGalleryRef(gid, parts[1].ToLowerInvariant()));
        }
        var lib = ReadStringField(req.Body, "lib");
        var folder = ReadStringField(req.Body, "folder");
        if (refs.Count == 0)
        {
            WriteJson(stream, 400, new { error = "bad request" });
            return;
        }

        var galleries = await EhentaiApi.GetMetadataAsync(refs);
        if (galleries.Count == 0)
        {
            WriteJson(stream, 404, new { error = "未找到选中的画廊" });
            return;
        }
        var result = await EhentaiService.EnqueueGalleriesAsync(
            galleries, folder.Length > 0 ? folder : null, lib.Length > 0 ? lib : null);
        if (!result.Ok)
        {
            WriteJson(stream, 200, new { ok = false, error = result.Error ?? "入队失败" });
            return;
        }
        WriteJson(stream, 200, new
        {
            ok = true, galleries = result.GalleryCount, files = result.FileCount, skipped = result.Skipped,
        });
    }

    /// <summary>
    /// 站上图片（封面/缩略图）代理：浏览器直连站点受跨域与代理设置影响，统一由本服务按配置的代理取回，
    /// 顺带带上登录 cookie（里站的图也要）。只放行站点自身域与其图片 CDN。
    /// </summary>
    private static async Task ApiEhImageAsync(NetworkStream stream, Request req)
    {
        var url = req.Query.GetValueOrDefault("url") ?? "";
        if (!IsEhentaiUrl(url) || !IsSafeFetchUrl(url))
        {
            WriteBytes(stream, 400, "Bad Request", "text/plain", []);
            return;
        }
        try
        {
            using var client = Http.CreateClient(TimeSpan.FromSeconds(20));
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            EhentaiApi.ApplyHeaders(request);
            using var resp = await client.SendAsync(request);
            resp.EnsureSuccessStatusCode();
            var bytes = await resp.Content.ReadAsByteArrayAsync();
            var type = resp.Content.Headers.ContentType?.MediaType ?? "";
            if (!type.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                type = ContentType(url);
            WriteBytes(stream, 200, "OK", type, bytes, ("Cache-Control", "max-age=86400"));
        }
        catch (Exception)
        {
            WriteBytes(stream, 404, "Not Found", "text/plain", []);
        }
    }

    /// <summary>画廊元数据转成前端要的字段（两处搜索/详情共用，保证卡片与详情同源）。</summary>
    private static object EhGalleryJson(EhGallery g) => new
    {
        gid = g.Gid,
        token = g.Token,
        title = g.DisplayTitle,
        titleEn = g.Title,
        category = g.Category,
        uploader = g.Uploader,
        posted = g.PostedUnix > 0 ? g.Posted.ToString("yyyy-MM-dd") : "",
        files = g.FileCount,
        rating = g.Rating,
        expunged = g.Expunged,
        maker = EhentaiService.MakerNameOf(g),
        tags = g.Tags,
        cover = g.Thumb,
        state = EhentaiService.GalleryStates([g.Gid]).GetValueOrDefault(g.Gid.ToString(), ""),
    };

    /// <summary>从查询串里取 gid/token 并校验；不合法返回 null。</summary>
    private static EhGalleryRef? ParseEhRef(Request req)
    {
        var token = (req.Query.GetValueOrDefault("token") ?? "").Trim();
        return long.TryParse(req.Query.GetValueOrDefault("gid"), out var gid) && gid > 0 && IsEhToken(token)
            ? new EhGalleryRef(gid, token.ToLowerInvariant())
            : null;
    }

    // ---------- pixiv 数据源 ----------

    /// <summary>
    /// 搜索作品。贴作品链接/作品号时直接认出那一件并只返回它（等价于站内直达），
    /// 否则按关键字搜；p 为页号（站点按页翻页，每页 60 件）。
    /// </summary>
    /// <summary>
    /// 这次 pixiv 请求用哪个账号：查询串或请求体里 account=&lt;pixiv 用户号&gt;（来源下拉「pixiv（账号名）」），
    /// 空或已不存在 = 匿名。设进 <see cref="SiteAccounts"/> 上下文后只对调用它的路由方法有效。
    /// </summary>
    private static void UsePixivAccount(Request req)
    {
        var uid = req.Query.GetValueOrDefault("account") ?? "";
        if (uid.Length == 0 && req.Body.Length > 0)
            uid = ReadStringField(req.Body, "account");
        SiteAccounts.Use(AccountService.Pixiv,
            uid.Length > 0 && SiteAccounts.Find(AccountService.Pixiv, uid) != null ? uid : "");
    }

    private static async Task ApiPixivSearchAsync(NetworkStream stream, Request req)
    {
        UsePixivAccount(req);
        var keyword = (req.Query.GetValueOrDefault("q") ?? "").Trim();
        var page = Math.Max(1, GetInt(req, "p"));
        // 贴的是作品链接/作品号：跳过搜索，直接取这一件的详情
        if (page == 1 && PixivApi.ParseArtworkInput(keyword) is { } direct)
        {
            var (one, error) = await PixivApi.GetArtworkAsync(direct);
            WriteJson(stream, 200, new
            {
                items = one is null ? Array.Empty<object>() : [PixivArtworkJson(one)],
                total = one is null ? 0 : 1,
                page = 1,
                hasMore = false,
                error,
                anonymous = !PixivApi.HasCookie,
            });
            return;
        }

        // 留空 = 我的收藏，作者主页链接 = 该作者的作品（详情页点作者即填这个），其余按关键字/标签搜（PixivApi.QueryAsync）
        var result = await PixivApi.QueryAsync(keyword, page);
        WriteJson(stream, 200, new
        {
            items = result.Items.Select(PixivArtworkJson),
            total = result.Total,
            page = result.Page,
            hasMore = result.HasMore,
            error = result.Error,
            heading = result.Heading,
            // 未登录时站点会把 R-18 从结果里滤掉，前端据此提示一句，免得用户以为是搜索坏了
            anonymous = !PixivApi.HasCookie,
        });
    }

    /// <summary>
    /// 单件作品详情（「查看内容」用）：元数据 + 逐页图片地址。
    ///
    /// 图一律给 1200px 的 regular：原图动辄几 MB，一篇漫画几十张全取原图会把带宽占满，
    /// 而详情页只是预览（原图是下载时才取的）——与 FANBOX 详情页只给 800px 预览同理。
    /// </summary>
    private static async Task ApiPixivArtworkAsync(NetworkStream stream, Request req)
    {
        UsePixivAccount(req);
        var id = (req.Query.GetValueOrDefault("id") ?? "").Trim();
        if (id.Length == 0 || !id.All(char.IsAsciiDigit))
        {
            WriteJson(stream, 400, new { error = "bad request" });
            return;
        }

        var (artwork, error) = await PixivApi.GetArtworkAsync(id);
        if (artwork is null)
        {
            WriteJson(stream, 200, new { error = error ?? "获取作品信息失败" });
            return;
        }
        var pages = await PixivApi.GetPagesAsync(id);
        WriteJson(stream, 200, new
        {
            artwork = PixivArtworkJson(artwork),
            images = pages.Select(x => new { index = x.Index, url = x.Regular, w = x.Width, h = x.Height }),
        });
    }

    /// <summary>
    /// 把选中的作品加入下载队列。请求只带作品号，元数据与图片清单由服务端重新拉取，
    /// 不信任前端提交的内容（与 fanbox / E-Hentai 入队同理）。
    /// </summary>
    private static async Task ApiPixivEnqueueAsync(NetworkStream stream, Request req)
    {
        UsePixivAccount(req);
        var ids = ReadStringArray(req.Body, "items")
            .Where(x => x.Length > 0 && x.All(char.IsAsciiDigit))
            .Distinct()
            .ToList();
        var lib = ReadStringField(req.Body, "lib");
        var folder = ReadStringField(req.Body, "folder");
        if (ids.Count == 0)
        {
            WriteJson(stream, 400, new { error = "bad request" });
            return;
        }

        // 元数据由入队流程统一现取（见 PixivService.EnqueueByIdsAsync），这里只递作品号
        var result = await PixivService.EnqueueByIdsAsync(
            ids, folder.Length > 0 ? folder : null, lib.Length > 0 ? lib : null);
        if (!result.Ok)
        {
            WriteJson(stream, 200, new { ok = false, error = result.Error ?? "入队失败" });
            return;
        }
        WriteJson(stream, 200, new
        {
            ok = true, artworks = result.ArtworkCount, files = result.FileCount, skipped = result.Skipped,
        });
    }

    /// <summary>
    /// 站上图片代理。pixiv 的图片服务器带防盗链：浏览器直接引用 i.pximg.net 一律 403
    /// （缺 Referer），所以图必须由本服务按配置的代理取回再转交。只放行 pixiv 自家域。
    /// </summary>
    private static async Task ApiPixivImageAsync(NetworkStream stream, Request req)
    {
        var url = req.Query.GetValueOrDefault("url") ?? "";
        if (!PixivApi.IsPixivUrl(url) || !IsSafeFetchUrl(url))
        {
            WriteBytes(stream, 400, "Bad Request", "text/plain", []);
            return;
        }
        try
        {
            using var client = Http.CreateClient(TimeSpan.FromSeconds(20));
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            SiteAccounts.Use(AccountService.Pixiv, "");   // 取图只认 Referer，不必把登录 cookie 交给图床
            PixivApi.ApplyHeaders(request);
            using var resp = await client.SendAsync(request);
            resp.EnsureSuccessStatusCode();
            var bytes = await resp.Content.ReadAsByteArrayAsync();
            var type = resp.Content.Headers.ContentType?.MediaType ?? "";
            if (!type.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                type = ContentType(url);
            WriteBytes(stream, 200, "OK", type, bytes, ("Cache-Control", "max-age=86400"));
        }
        catch (Exception)
        {
            WriteBytes(stream, 404, "Not Found", "text/plain", []);
        }
    }

    /// <summary>作品元数据转成前端要的字段（搜索/详情共用，保证卡片与详情同源）。</summary>
    private static object PixivArtworkJson(PixivArtwork a) => new
    {
        id = a.Id,
        title = a.Title,
        cover = a.Thumb,
        userId = a.UserId,
        maker = PixivService.MakerNameOf(a),
        pages = a.PageCount,
        type = a.TypeName,
        restrict = a.RestrictName,
        ai = a.IsAi,
        posted = a.Created is { } t ? t.ToString("yyyy-MM-dd") : "",
        width = a.Width,
        height = a.Height,
        bookmarks = a.BookmarkCount,
        views = a.ViewCount,
        description = a.Description,
        tags = a.Tags,
        state = PixivService.ArtworkStates([a.Id]).GetValueOrDefault(a.Id, ""),
    };

    /// <summary>画廊令牌固定是 10 位十六进制；用作 URL 参数前先校验。</summary>
    private static bool IsEhToken(string token) =>
        token.Length == 10 && token.All(char.IsAsciiHexDigit);

    /// <summary>
    /// URL 是否指向 E-Hentai 体系内的主机：站点自身（表/里站）、元数据 API，
    /// 以及站点的图片 CDN（ehgt.org 与 H@H 网络的 hath.network）。
    /// </summary>
    private static bool IsEhentaiUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return false;
        var host = uri.Host.ToLowerInvariant();
        string[] roots = ["e-hentai.org", "exhentai.org", "ehgt.org", "hath.network"];
        return roots.Any(r => host == r || host.EndsWith("." + r, StringComparison.Ordinal));
    }

    /// <summary>pawchive 的作家号/作品号都是纯数字；用作 SQL/URL 参数前先校验。</summary>
    private static bool IsPlainId(string id) =>
        id.Length is > 0 and <= 32 && id.All(char.IsAsciiDigit);

    // ---------- 请求体解析 ----------

    private static string ReadStringField(byte[] body, string field)
    {
        try
        {
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(body));
            return doc.RootElement.TryGetProperty(field, out var v) ? v.GetString() ?? "" : "";
        }
        catch (Exception)
        {
            return "";
        }
    }

    private static bool ReadBoolField(byte[] body, string field)
    {
        try
        {
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(body));
            return doc.RootElement.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.True;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>取整数字段；缺失/非数字时返回 0（调用方据此判断"没传"）。</summary>
    private static int ReadIntField(byte[] body, string field)
    {
        try
        {
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(body));
            if (!doc.RootElement.TryGetProperty(field, out var v))
                return 0;
            return v.ValueKind switch
            {
                JsonValueKind.Number when v.TryGetInt32(out var n) => n,
                JsonValueKind.String when int.TryParse(v.GetString(), out var n) => n,
                _ => 0,
            };
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>取布尔字段，区分"没传"（null）与 false——只改其中一项的 PATCH 式请求要用到。</summary>
    private static bool? ReadOptionalBoolField(byte[] body, string field)
    {
        try
        {
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(body));
            if (!doc.RootElement.TryGetProperty(field, out var v))
                return null;
            return v.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static List<string> ReadStringArray(byte[] body, string field)
    {
        var list = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(body));
            if (doc.RootElement.TryGetProperty(field, out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var item in arr.EnumerateArray())
                    if (item.GetString() is { Length: > 0 } s)
                        list.Add(s);
        }
        catch (Exception)
        {
            // 解析失败返回空列表
        }
        return list;
    }

    // ---------- 排序子句（{p} 为表别名占位） ----------

    private static string MakerOrderClause(int sort) => sort switch
    {
        1 => "ORDER BY COUNT(*) ASC",
        2 => "ORDER BY {p}\"maker_name\" COLLATE NOCASE ASC",
        _ => "ORDER BY COUNT(*) DESC",
    };

    private static string WorkOrderClause(int sort) => sort switch
    {
        1 => "ORDER BY {p}\"sell_date\" ASC",
        2 => "ORDER BY {p}\"work_id\" DESC",
        3 => "ORDER BY {p}\"work_id\" ASC",
        _ => "ORDER BY {p}\"sell_date\" DESC",
    };

    private static int GetInt(Request req, string key) =>
        int.TryParse(req.Query.GetValueOrDefault(key), out var v) ? v : 0;

    // ---------- HTTP 响应写出 ----------

    private static void WriteJson(NetworkStream stream, int status, object payload,
        params (string Key, string Value)[] extra)
    {
        var json = JsonSerializer.Serialize(payload, JsonOpts);
        WriteBytes(stream, status, StatusText(status), "application/json; charset=utf-8",
            Encoding.UTF8.GetBytes(json), extra);
    }

    private static void WriteBytes(NetworkStream stream, int status, string statusText,
        string contentType, byte[] body, params (string Key, string Value)[] extra)
    {
        var sb = new StringBuilder();
        sb.Append("HTTP/1.1 ").Append(status).Append(' ').Append(statusText).Append("\r\n");
        sb.Append("Content-Type: ").Append(contentType).Append("\r\n");
        sb.Append("Content-Length: ").Append(body.Length).Append("\r\n");
        sb.Append("Connection: close\r\n");
        foreach (var (key, value) in extra)
            sb.Append(key).Append(": ").Append(value).Append("\r\n");
        sb.Append("\r\n");
        var head = Encoding.ASCII.GetBytes(sb.ToString());
        stream.Write(head, 0, head.Length);
        if (body.Length > 0)
            stream.Write(body, 0, body.Length);
        stream.Flush();
    }

    private static string StatusText(int status) => status switch
    {
        200 => "OK", 400 => "Bad Request", 401 => "Unauthorized", 403 => "Forbidden",
        404 => "Not Found", 429 => "Too Many Requests", 500 => "Internal Server Error", _ => "OK",
    };

    // ---------- 工具 ----------

    private static string Sha256Hex(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    /// <summary>取本机首个 IPv4 局域网地址（设置页展示访问地址用）；取不到返回 localhost。</summary>
    public static string LocalIPv4()
    {
        try
        {
            foreach (var ip in Dns.GetHostAddresses(Dns.GetHostName()))
                if (ip.AddressFamily == AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(ip))
                    return ip.ToString();
        }
        catch (Exception)
        {
            // 取本机地址失败时回退
        }
        return "127.0.0.1";
    }
}
