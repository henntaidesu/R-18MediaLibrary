using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using R18MediaLibrary.Core;

namespace R18MediaLibrary.Services;

/// <summary>
/// 账号访问的「在服务器上登录」——程序取得站点登录态的唯一途径（FANBOX / Fantia / pixiv / E-Hentai）（不提供手填 cookie）：
/// 在运行本程序的这台电脑上开一个内嵌浏览器（WebView2）窗口打开站点登录页，人在窗口里过人机验证、输账号密码，
/// 程序每秒看一眼该站的 cookie，一变成登录态就整理成 Cookie 请求头交给 <see cref="AccountService.AddFromLoginAsync"/>，
/// 存成一个账号（<see cref="SiteAccounts"/>）后关窗。pixiv / FANBOX 的登录带 reCAPTCHA，E-Hentai 的论坛登录常触发验证码，
/// 程序拿账号密码直接请求都走不通。
///
/// 桌面端直接在本机操作这个窗口；Web 端经 <see cref="LoginStream"/> 把它串流到浏览器里操作
/// （协议同作品 exe 的远程串流，但画面与输入都直接走 WebView2，不依赖服务器桌面）。同一时刻只开一个窗口，再开就关掉旧的。
///
/// <b>每次登录用一个新的浏览器资料（profile）</b>，账号与资料一一对应：同一站点登多个账号时互不串号，
/// 资料里留着的登录态也只属于这一个账号——<see cref="AccountKeeper"/> 发现某账号的 cookie 失效时，
/// 用 <see cref="ReadAsync"/> 在不显示的浏览器里打开这个资料的站点首页、读出浏览器手上那份（站点会顺手续期）。
/// 账号退出登录或被同号的新登录取代时，旧资料整个删掉（<see cref="DeleteProfile"/>）。
///
/// E-Hentai 要两步：论坛登录后 .e-hentai.org 上有 ipb_member_id / ipb_pass_hash，但里站 exhentai.org 是另一个顶级域，
/// 浏览器不会把它们带过去；故把这两个 cookie 复制到 .exhentai.org，再打开一次里站，里站才种下 igneous
/// （值为 mystery 时等于没拿到，是站点在说「不认这个会话」）。
///
/// 资料目录 cache/webview2，代理沿用系统设置里的 proxy。除 <see cref="DeleteProfile"/> 外，所有方法都要在 UI 线程上调用。
/// </summary>
public static class LoginBrowser
{
    // pixiv / FANBOX 的会话 cookie 登录后形如「pixiv 用户号_随机串」，匿名会话没有这个前缀——据此判断登没登上
    private static readonly Regex LoggedInSession = new(@"^\d+_", RegexOptions.Compiled);

    // HomeUrl：后台续 cookie 时打开的页面；CookieUrl：读会话 cookie 的地址；CookieName：会话 cookie 名（E-Hentai 为空，另行处理）
    private sealed record Target(string Title, string LoginUrl, string HomeUrl, string CookieUrl, string CookieName);

    private static readonly Dictionary<string, Target> Targets = new()
    {
        [AccountService.Fanbox] = new("FANBOX", "https://www.fanbox.cc/login?return_to=https%3A%2F%2Fwww.fanbox.cc%2F",
            "https://www.fanbox.cc/", "https://www.fanbox.cc/", "FANBOXSESSID"),
        // Fantia 的登录在 fantia.jp 上（_session_id），匿名访客也有这个 cookie，登没登上要问一次 /api/v1/me
        [AccountService.Fantia] = new("Fantia", "https://fantia.jp/sessions/signin",
            "https://fantia.jp/", "https://fantia.jp/", "_session_id"),
        [AccountService.Pixiv] = new("Pixiv", "https://accounts.pixiv.net/login?return_to=https%3A%2F%2Fwww.pixiv.net%2F",
            "https://www.pixiv.net/", "https://www.pixiv.net/", "PHPSESSID"),
        [AccountService.Ehentai] = new("E-Hentai", "https://forums.e-hentai.org/index.php?act=Login&CODE=00",
            "https://forums.e-hentai.org/", "https://e-hentai.org/", ""),
    };

    /// <summary>从浏览器里取到的登录态：账号的用户号、整串 Cookie 请求头、能顺手取到的显示名（取不到为空）。</summary>
    public sealed record Harvest(string UserId, string Cookie, string Name);

    private static string ProfileDir => Path.Combine(Environment.CurrentDirectory, "cache", "webview2");

    // 同一资料目录只能有一个浏览器环境：登录窗口与后台读 cookie 共用它（各自用不同的 profile）
    private static Task<CoreWebView2Environment>? _env;
    private static Window? _hiddenHost;

    private static Window? _window;
    private static WebView2? _web;
    private static DispatcherTimer? _timer;
    private static string _site = "";
    private static string _profile = "";
    private static bool _saving;
    private static readonly Dictionary<string, string> LastResult = [];

    /// <summary>一次登录有了结果（site, 成功与否, 提示文字）。在 UI 线程上触发。</summary>
    public static event Action<string, bool, string>? Finished;

    public static bool Supports(string site) => Targets.ContainsKey(site);

    /// <summary>该站最近一次登录的结果提示（串流结束时告诉网页「登录成功 / 窗口已关闭」）。</summary>
    public static string? ResultOf(string site) => LastResult.GetValueOrDefault(site);

    /// <summary>当前登录窗口的句柄；没开着为 0。</summary>
    public static IntPtr Handle => _window is { } w ? new WindowInteropHelper(w).Handle : IntPtr.Zero;

    /// <summary>登录窗口正开着（人正在登录）：自动维护此时不插手。</summary>
    public static bool IsOpen => _window != null;

    /// <summary>关掉登录窗口（网页端串流里点「关闭浏览器」）。</summary>
    public static void Close() => _window?.Close();

    /// <summary>当前登录窗口里的浏览器（还在初始化或没开着为 null）：网页端串流（<see cref="LoginStream"/>）直接抓它的画面、往里送输入。</summary>
    public static CoreWebView2? Core => _web?.CoreWebView2;

    /// <summary>浏览器视口的 CSS 像素尺寸（WPF 的 DIP 即 CSS px），网页端的相对坐标按它换算。</summary>
    public static (double Width, double Height) ViewportSize => _web is { } w ? (w.ActualWidth, w.ActualHeight) : (0, 0);

    /// <summary>
    /// 这台电脑能不能开登录浏览器；能则返回 null，否则返回给人看的原因。
    /// Windows Server 不一定装了 WebView2 运行时；以服务/计划任务（会话 0）运行时根本没有可显示窗口的桌面。
    /// </summary>
    public static string? CheckAvailable()
    {
        if (System.Diagnostics.Process.GetCurrentProcess().SessionId == 0)
            return "本程序运行在系统服务会话（会话 0）里，无法打开浏览器窗口：请在已登录的 Windows 用户桌面里运行本程序";
        try
        {
            CoreWebView2Environment.GetAvailableBrowserVersionString();
            return null;
        }
        catch (WebView2RuntimeNotFoundException)
        {
            return "这台电脑没有安装 Microsoft Edge WebView2 运行时，请先安装：https://go.microsoft.com/fwlink/p/?LinkId=2124703";
        }
        catch (Exception e)
        {
            return "内嵌浏览器不可用：" + e.Message;
        }
    }

    /// <summary>打开某站的登录窗口（用一个全新的浏览器资料），返回窗口句柄。已开着的登录窗口先关掉。</summary>
    public static IntPtr Open(string site)
    {
        if (!Targets.TryGetValue(site, out var target))
            throw new ArgumentException("不支持的站点：" + site);
        if (CheckAvailable() is { } unavailable)
            throw new InvalidOperationException(unavailable);
        _window?.Close();
        LastResult.Remove(site);
        _site = site;
        _profile = "a" + Guid.NewGuid().ToString("N")[..12];
        _web = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x18, 0x18, 0x1B) };
        _window = new Window
        {
            Width = 960, Height = 760, Content = _web,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Background = new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x1B)),
            Title = $"登录 {target.Title} — R-18MediaLibrary",
        };
        _window.Closed += (_, _) => OnClosed();
        _window.Show();
        _ = InitAsync(target);
        _timer ??= new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => _ = PollAsync(),
            _window.Dispatcher);
        _timer.Start();
        Logger.Info($"打开登录浏览器：{target.Title}");
        return new WindowInteropHelper(_window).Handle;
    }

    private static Task<CoreWebView2Environment> EnvAsync()
    {
        if (_env is { IsFaulted: false })
            return _env;
        Directory.CreateDirectory(ProfileDir);
        // 关掉「窗口被遮挡就停止绘制」：服务器上远程桌面断开/锁屏时窗口算被遮挡，浏览器停画，
        // 网页端串流（CapturePreviewAsync）就只能拿到一张不动的旧画面
        var args = "--disable-features=CalculateNativeWinOcclusion --disable-backgrounding-occluded-windows";
        var (proxyOn, proxy) = AppConfig.ReadProxy();
        if (proxyOn && proxy.Address != null)
            args += $" --proxy-server={proxy.Address.Authority}";
        var options = new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = args };
        return _env = CoreWebView2Environment.CreateAsync(null, ProfileDir, options);
    }

    private static async Task InitAsync(Target target)
    {
        var web = _web;
        try
        {
            var env = await EnvAsync();
            var options = env.CreateCoreWebView2ControllerOptions();
            options.ProfileName = _profile;
            if (web == null || web != _web)
                return;
            await web.EnsureCoreWebView2Async(env, options);
            web.CoreWebView2.Navigate(target.LoginUrl);
        }
        catch (Exception e)
        {
            Logger.Error($"登录浏览器初始化失败：{e.Message}");
            Finish(false, "内嵌浏览器启动失败（需要 Microsoft Edge WebView2 运行时）：" + e.Message);
        }
    }

    /// <summary>每秒看一眼当前站点的 cookie：成了登录态就存成账号、检测、关窗。</summary>
    private static async Task PollAsync()
    {
        if (_saving || _web?.CoreWebView2 is not { } core)
            return;
        _saving = true;
        try
        {
            var site = _site;
            var profile = _profile;
            var harvest = await HarvestAsync(core, site);
            if (harvest == null || site != _site)
                return;
            var (ok, message) = await AccountService.AddFromLoginAsync(site, harvest, profile);
            Logger.Info($"登录浏览器取到 {Targets[site].Title} 账号 {harvest.UserId}，检测{(ok ? "通过" : "未通过")}：{message}");
            if (site == _site)
                Finish(ok, ok ? message : "已登录，但检测未通过：" + message);
        }
        catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException or System.Runtime.InteropServices.COMException)
        {
            // 窗口正在关
        }
        finally
        {
            _saving = false;
        }
    }

    /// <summary>
    /// 从浏览器里取该站的登录态；还没登上返回 null。E-Hentai 会顺带打开一次里站领 igneous（见类注释）。
    /// </summary>
    private static async Task<Harvest?> HarvestAsync(CoreWebView2 core, string site)
    {
        var target = Targets[site];
        var manager = core.CookieManager;
        if (site == AccountService.Fantia)
            return await HarvestFantiaAsync(manager, target);
        if (site != AccountService.Ehentai)
        {
            var session = (await manager.GetCookiesAsync(target.CookieUrl)).Find(c => c.Name == target.CookieName)?.Value ?? "";
            if (!LoggedInSession.IsMatch(session))
                return null;
            return new Harvest(PixivApi.UserIdFromSession(session), $"{target.CookieName}={session}", "");
        }

        var forum = await manager.GetCookiesAsync(target.CookieUrl);
        var member = forum.Find(c => c.Name == "ipb_member_id")?.Value ?? "";
        var hash = forum.Find(c => c.Name == "ipb_pass_hash")?.Value ?? "";
        if (member.Length == 0 || member == "0" || !member.All(char.IsAsciiDigit) || hash.Length == 0)
            return null;
        // 论坛首页写着「Logged in as: 账号名」，离开论坛前顺手取下来当显示名
        var name = await ScrapeEhentaiNameAsync(core);
        var igneous = await ExhentaiIgneousAsync(core, member, hash);
        var cookie = $"ipb_member_id={member}; ipb_pass_hash={hash}" + (igneous.Length > 0 ? $"; igneous={igneous}" : "");
        return new Harvest(member, cookie, name);
    }

    // 上次问过 /api/v1/me 却还是匿名的会话值：登录过程中每秒轮询一次，值没变就不必再问站点
    private static string _fantiaProbed = "";

    /// <summary>
    /// Fantia：会话 cookie 匿名时就有，看不出登没登上，只能拿它去问 /api/v1/me（登录后 Rails 会换一个新会话值，
    /// 所以只在值变了时才问）。整串取 fantia.jp 上的 cookie——年龄确认等状态也在里面，取详情要用。
    /// </summary>
    private static async Task<Harvest?> HarvestFantiaAsync(CoreWebView2CookieManager manager, Target target)
    {
        var cookies = await manager.GetCookiesAsync(target.CookieUrl);
        var session = cookies.Find(c => c.Name == target.CookieName)?.Value ?? "";
        if (session.Length == 0 || session == _fantiaProbed)
            return null;
        var header = string.Join("; ", cookies.Select(c => $"{c.Name}={c.Value}"));
        var (definite, userId, name) = await FantiaApi.ProbeSessionAsync(header);
        if (userId.Length == 0)
        {
            if (definite)
                _fantiaProbed = session;   // 站点明说是匿名会话；没得到答复的（网络抖动）下一秒再问
            return null;
        }
        return new Harvest(userId, header, name);
    }

    private static async Task<string> ScrapeEhentaiNameAsync(CoreWebView2 core)
    {
        try
        {
            var json = await core.ExecuteScriptAsync(
                @"(((document.body && document.body.innerText) || '').match(/Logged in as:?\s*([^\r\n]+)/) || [])[1] || ''");
            return (JsonSerializer.Deserialize<string>(json) ?? "").Trim();
        }
        catch (Exception)
        {
            return "";
        }
    }

    /// <summary>把 ipb 两个 cookie 复制到里站域，再打开一次里站领 igneous；没拿到（或为 mystery）返回空串。</summary>
    private static async Task<string> ExhentaiIgneousAsync(CoreWebView2 core, string member, string hash)
    {
        var manager = core.CookieManager;
        string Read(List<CoreWebView2Cookie> list) =>
            list.Find(c => c.Name == "igneous")?.Value is { Length: > 0 } v &&
            !v.Equals("mystery", StringComparison.OrdinalIgnoreCase) ? v : "";

        var existing = Read(await manager.GetCookiesAsync("https://exhentai.org/"));
        if (existing.Length > 0)
            return existing;
        foreach (var (name, value) in new[] { ("ipb_member_id", member), ("ipb_pass_hash", hash) })
        {
            var c = manager.CreateCookie(name, value, ".exhentai.org", "/");
            c.Expires = DateTime.Now.AddYears(1);
            c.IsSecure = true;
            manager.AddOrUpdateCookie(c);
        }
        await NavigateAsync(core, "https://exhentai.org/");
        return Read(await manager.GetCookiesAsync("https://exhentai.org/"));
    }

    private static async Task NavigateAsync(CoreWebView2 core, string url)
    {
        var loaded = new TaskCompletionSource();
        void OnDone(object? s, CoreWebView2NavigationCompletedEventArgs e) => loaded.TrySetResult();
        core.NavigationCompleted += OnDone;
        try
        {
            core.Navigate(url);
            await Task.WhenAny(loaded.Task, Task.Delay(TimeSpan.FromSeconds(30)));
        }
        finally
        {
            core.NavigationCompleted -= OnDone;
        }
    }

    // ---------- 后台：不显示的浏览器 ----------

    /// <summary>挂在一个从不显示的窗口下的浏览器控制器（控制器必须有父窗口），用指定的资料。</summary>
    private static async Task<CoreWebView2Controller> HiddenControllerAsync(string profile)
    {
        _hiddenHost ??= new Window { ShowInTaskbar = false, Width = 0, Height = 0, WindowStyle = WindowStyle.None };
        var parent = new WindowInteropHelper(_hiddenHost).EnsureHandle();
        var env = await EnvAsync();
        var options = env.CreateCoreWebView2ControllerOptions();
        options.ProfileName = profile;
        var controller = await env.CreateCoreWebView2ControllerAsync(parent, options);
        controller.IsVisible = false;
        return controller;
    }

    /// <summary>
    /// 用某账号的浏览器资料在后台打开站点首页，读出浏览器手上的登录态（打开一次即让站点按正常访问续期）。
    /// 资料里已不是登录态（过期了，要重新过人机验证）或出错时返回 null。
    /// </summary>
    public static async Task<Harvest?> ReadAsync(string site, string profile)
    {
        if (!Targets.TryGetValue(site, out var target) || profile.Length == 0)
            return null;
        CoreWebView2Controller? controller = null;
        try
        {
            controller = await HiddenControllerAsync(profile);
            await NavigateAsync(controller.CoreWebView2, target.HomeUrl);
            return await HarvestAsync(controller.CoreWebView2, site);
        }
        catch (Exception e)
        {
            Logger.Warning($"后台读取 {target.Title} 浏览器登录态失败：{e.Message}");
            return null;
        }
        finally
        {
            controller?.Close();
        }
    }

    /// <summary>
    /// 删掉某个账号的浏览器资料（任意线程可调）：账号退出登录、或被同号的新登录取代时调用，
    /// 免得旧资料里的登录态留在盘上。删除在该资料的浏览器全部关闭后由运行时完成。
    /// </summary>
    public static void DeleteProfile(string profile)
    {
        if (profile.Length == 0 || Application.Current is not { } app)
            return;
        app.Dispatcher.InvokeAsync(async () =>
        {
            CoreWebView2Controller? controller = null;
            try
            {
                controller = await HiddenControllerAsync(profile);
                controller.CoreWebView2.CookieManager.DeleteAllCookies();
                controller.CoreWebView2.Profile.Delete();
                Logger.Info($"已删除登录浏览器资料 {profile}");
            }
            catch (Exception e)
            {
                Logger.Warning($"删除登录浏览器资料 {profile} 失败：{e.Message}");
            }
            finally
            {
                controller?.Close();
            }
        });
    }

    // ---------- 收尾 ----------

    private static void Finish(bool ok, string message)
    {
        var site = _site;
        LastResult[site] = message;
        // 先关窗再通知：成功就关窗；失败（多半是 WebView2 起不来）也关，留着一个空窗口没有意义。
        // 顺序不能反——订阅方可能弹阻塞式对话框（桌面账号页），先通知的话窗口要等人点了「确定」才关，
        // 网页端的串流也就一直等不到结束
        _window?.Close();
        Finished?.Invoke(site, ok, message);
    }

    private static void OnClosed()
    {
        _timer?.Stop();
        var web = _web;
        _web = null;
        _window = null;
        try { web?.Dispose(); }
        catch (Exception) { /* 浏览器进程已退出 */ }
        var site = _site;
        if (site.Length > 0 && !LastResult.ContainsKey(site))
        {
            // 没登完就被关掉（用户点了关闭 / 网页端点了关闭浏览器）：这个新资料没用上，删掉
            LastResult[site] = "登录窗口已关闭，未完成登录";
            DeleteProfile(_profile);
            Finished?.Invoke(site, false, LastResult[site]);
        }
        _site = "";
    }
}
