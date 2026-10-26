using System;
using System.Collections.Generic;
using System.IO;
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
/// 账号访问的「在服务器上登录」：在运行本程序的这台电脑上开一个内嵌浏览器（WebView2）窗口打开站点登录页，
/// 人在窗口里过人机验证、输账号密码，程序每秒看一眼该站的会话 cookie，一变成登录态就自动存下、检测、关窗。
/// pixiv / FANBOX 的登录带 reCAPTCHA，程序拿账号密码直接请求过不去，这是不用 F12 复制 cookie 的唯一办法。
///
/// 桌面端直接在本机操作这个窗口；Web 端经 <see cref="GameStream.RunWindow"/> 把它串流到浏览器里操作
/// （同作品 exe 的远程串流）。同一时刻只开一个窗口，换站点就在原窗口里改去另一站的登录页。
///
/// 浏览器资料目录固定为 cache/webview2：登过一次 pixiv，FANBOX 再登时往往只需点一下「用 pixiv 登录」，
/// 已登录时打开登录页会直接跳回首页、当场取到 cookie。代理沿用系统设置里的 proxy。
/// 这份资料目录也是 cookie 自动维护的来源：<see cref="AccountKeeper"/> 发现程序里存的 cookie 失效时，
/// 用 <see cref="ReadSessionAsync"/> 在不显示的浏览器里打开站点首页、读出浏览器手上那份（站点会顺手续期）。
/// 除 <see cref="Forget"/> 外，所有方法都要在 UI 线程上调用。
/// </summary>
public static class LoginBrowser
{
    // 会话 cookie 登录后形如「pixiv 用户号_随机串」，匿名会话没有这个前缀——据此判断登没登上
    private static readonly Regex LoggedInSession = new(@"^\d+_", RegexOptions.Compiled);

    // ForgetUrls：退出登录时要清掉浏览器里哪些地址下的 cookie（pixiv 的账号中心另有一套）
    private sealed record Target(string Title, string LoginUrl, string CookieUrl, string CookieName, string Key,
        string[] ForgetUrls);

    private static readonly Dictionary<string, Target> Targets = new()
    {
        [AccountService.Fanbox] = new("FANBOX", "https://www.fanbox.cc/login?return_to=https%3A%2F%2Fwww.fanbox.cc%2F",
            "https://www.fanbox.cc/", "FANBOXSESSID", "sessid", ["https://www.fanbox.cc/"]),
        [AccountService.Pixiv] = new("pixiv", "https://accounts.pixiv.net/login?return_to=https%3A%2F%2Fwww.pixiv.net%2F",
            "https://www.pixiv.net/", "PHPSESSID", "php_sessid",
            ["https://www.pixiv.net/", "https://accounts.pixiv.net/"]),
    };

    private static string ProfileDir => Path.Combine(Environment.CurrentDirectory, "cache", "webview2");

    // 同一资料目录只能有一个浏览器环境：登录窗口与后台读 cookie 共用它
    private static Task<CoreWebView2Environment>? _env;
    private static Window? _hiddenHost;

    private static Window? _window;
    private static WebView2? _web;
    private static DispatcherTimer? _timer;
    private static string _site = "";
    private static bool _saving;
    private static readonly Dictionary<string, string> LastResult = [];

    /// <summary>一次登录有了结果（site, 成功与否, 提示文字）。在 UI 线程上触发。</summary>
    public static event Action<string, bool, string>? Finished;

    public static bool Supports(string site) => Targets.ContainsKey(site);

    /// <summary>用过「在服务器上登录」才有浏览器资料目录，也才谈得上从里面续 cookie。</summary>
    public static bool HasProfile => Directory.Exists(ProfileDir);

    /// <summary>登录窗口正开着（人正在登录）：自动维护此时不插手。</summary>
    public static bool IsOpen => _window != null;

    private static Task<CoreWebView2Environment> EnvAsync()
    {
        if (_env is { IsFaulted: false })
            return _env;
        Directory.CreateDirectory(ProfileDir);
        var options = new CoreWebView2EnvironmentOptions();
        var (proxyOn, proxy) = AppConfig.ReadProxy();
        if (proxyOn && proxy.Address != null)
            options.AdditionalBrowserArguments = $"--proxy-server={proxy.Address.Authority}";
        return _env = CoreWebView2Environment.CreateAsync(null, ProfileDir, options);
    }

    /// <summary>不显示的浏览器控制器：挂在一个从不显示的窗口下（控制器必须有父窗口）。</summary>
    private static async Task<CoreWebView2Controller> HiddenControllerAsync()
    {
        _hiddenHost ??= new Window { ShowInTaskbar = false, Width = 0, Height = 0, WindowStyle = WindowStyle.None };
        var parent = new WindowInteropHelper(_hiddenHost).EnsureHandle();
        var env = await EnvAsync();
        var controller = await env.CreateCoreWebView2ControllerAsync(parent);
        controller.IsVisible = false;
        return controller;
    }

    /// <summary>
    /// 在不显示的浏览器里打开站点首页，读出浏览器资料里该站的会话 cookie；不是登录态（或出错）返回 null。
    /// 打开一次首页即让站点按正常访问续期会话，读到的就是续过的那份。
    /// </summary>
    public static async Task<string?> ReadSessionAsync(string site)
    {
        if (!Targets.TryGetValue(site, out var target) || !HasProfile)
            return null;
        CoreWebView2Controller? controller = null;
        try
        {
            controller = await HiddenControllerAsync();
            var loaded = new TaskCompletionSource();
            controller.CoreWebView2.NavigationCompleted += (_, _) => loaded.TrySetResult();
            controller.CoreWebView2.Navigate(target.CookieUrl);
            await Task.WhenAny(loaded.Task, Task.Delay(TimeSpan.FromSeconds(30)));
            var cookies = await controller.CoreWebView2.CookieManager.GetCookiesAsync(target.CookieUrl);
            var session = cookies.Find(c => c.Name == target.CookieName)?.Value ?? "";
            return LoggedInSession.IsMatch(session) ? session : null;
        }
        catch (Exception e)
        {
            Logger.Warning($"后台读取 {target.Title} 浏览器 cookie 失败：{e.Message}");
            return null;
        }
        finally
        {
            controller?.Close();
        }
    }

    /// <summary>
    /// 退出登录时把浏览器资料里该站的 cookie 也清掉（任意线程可调）：否则下次「在服务器上登录」会直接用旧账号登上，
    /// 自动维护也会把刚退出的登录又续回来。
    /// </summary>
    public static void Forget(string site)
    {
        if (!Targets.TryGetValue(site, out var target) || !HasProfile ||
            Application.Current is not { } app)
            return;
        app.Dispatcher.InvokeAsync(async () =>
        {
            CoreWebView2Controller? controller = null;
            try
            {
                controller = await HiddenControllerAsync();
                var manager = controller.CoreWebView2.CookieManager;
                foreach (var url in target.ForgetUrls)
                    foreach (var c in await manager.GetCookiesAsync(url))
                        manager.DeleteCookie(c);
                Logger.Info($"已清除登录浏览器里的 {target.Title} cookie");
            }
            catch (Exception e)
            {
                Logger.Warning($"清除登录浏览器里的 {target.Title} cookie 失败：{e.Message}");
            }
            finally
            {
                controller?.Close();
            }
        });
    }

    /// <summary>该站最近一次登录的结果提示（串流结束时告诉网页「登录成功 / 窗口已关闭」）。</summary>
    public static string? ResultOf(string site) => LastResult.GetValueOrDefault(site);

    /// <summary>当前登录窗口的句柄；没开着为 0。</summary>
    public static IntPtr Handle => _window is { } w ? new WindowInteropHelper(w).Handle : IntPtr.Zero;

    /// <summary>打开（或切换到）某站的登录窗口，返回窗口句柄。WebView2 运行时缺失等失败时抛异常。</summary>
    public static IntPtr Open(string site)
    {
        if (!Targets.TryGetValue(site, out var target))
            throw new ArgumentException("不支持的站点：" + site);
        LastResult.Remove(site);
        _site = site;
        if (_window == null)
        {
            _web = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x18, 0x18, 0x1B) };
            _window = new Window
            {
                Width = 960, Height = 760, Content = _web,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Background = new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x1B)),
            };
            _window.Closed += (_, _) => OnClosed();
            _window.Show();
            _ = InitAsync(target);
        }
        else
        {
            if (_window.WindowState == WindowState.Minimized)
                _window.WindowState = WindowState.Normal;
            _window.Activate();
            if (_web?.CoreWebView2 != null)
                _web.CoreWebView2.Navigate(target.LoginUrl);
        }
        _window.Title = $"登录 {target.Title} — R-18MediaLibrary";
        _timer ??= new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => _ = PollAsync(),
            _window.Dispatcher);
        _timer.Start();
        Logger.Info($"打开登录浏览器：{target.Title}");
        return new WindowInteropHelper(_window).Handle;
    }

    private static async Task InitAsync(Target target)
    {
        try
        {
            var env = await EnvAsync();
            if (_web == null)
                return;
            await _web.EnsureCoreWebView2Async(env);
            // 期间可能已切到另一站：按当前站点打开
            _web.CoreWebView2.Navigate(Targets[_site].LoginUrl);
        }
        catch (Exception e)
        {
            Logger.Error($"登录浏览器初始化失败：{e.Message}");
            Finish(false, "内嵌浏览器启动失败（需要 Microsoft Edge WebView2 运行时）：" + e.Message);
        }
    }

    /// <summary>每秒看一眼当前站点的会话 cookie：成了登录态就存库、检测、关窗。</summary>
    private static async Task PollAsync()
    {
        if (_saving || _web?.CoreWebView2 == null || !Targets.TryGetValue(_site, out var target))
            return;
        List<CoreWebView2Cookie> cookies;
        try { cookies = await _web.CoreWebView2.CookieManager.GetCookiesAsync(target.CookieUrl); }
        catch (Exception) { return; }   // 窗口正在关
        var session = cookies.Find(c => c.Name == target.CookieName)?.Value ?? "";
        if (!LoggedInSession.IsMatch(session))
            return;
        _saving = true;
        try
        {
            var site = _site;
            AccountService.SaveCookies(site, new Dictionary<string, string> { [target.Key] = session });
            var (ok, message) = await AccountService.CheckAsync(site);
            Logger.Info($"登录浏览器取到 {target.Title} 的 cookie，检测{(ok ? "通过" : "未通过")}：{message}");
            if (site != _site)
                return;   // 检测期间换了站点，结果不算这一次的
            Finish(ok, ok ? message : "已取到 cookie，但检测未通过：" + message);
        }
        finally
        {
            _saving = false;
        }
    }

    private static void Finish(bool ok, string message)
    {
        var site = _site;
        LastResult[site] = message;
        Finished?.Invoke(site, ok, message);
        // 成功就关窗；失败（多半是 WebView2 起不来）也关，留着一个空窗口没有意义
        _window?.Close();
    }

    private static void OnClosed()
    {
        _timer?.Stop();
        try { _web?.Dispose(); }
        catch (Exception) { /* 浏览器进程已退出 */ }
        _web = null;
        _window = null;
        var site = _site;
        if (site.Length > 0 && !LastResult.ContainsKey(site))
        {
            // 没登完就被关掉（用户点了关闭 / 网页端点了关闭浏览器）
            LastResult[site] = "登录窗口已关闭，未完成登录";
            Finished?.Invoke(site, false, LastResult[site]);
        }
        _site = "";
    }
}
