using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using R18MediaLibrary.Core;

namespace R18MediaLibrary.Services;

/// <summary>
/// 账号 cookie 自动维护：启动后稍等片刻查一轮，之后每 <see cref="Interval"/> 再查一轮，
/// 对「当前登着」的站点逐个检测，失效了就自己续上，用户不必回来重新粘 cookie。
///
///   pixiv / FANBOX —— 从登录浏览器（<see cref="LoginBrowser"/>）的资料目录里取：后台开一个不显示的浏览器打开站点首页，
///                     浏览器手上的会话还有效就会被站点续期，读出来存回程序里。浏览器那份也失效了（要重新过人机验证）
///                     就只能记下「需重新登录」，程序替不了人。
///   E-Hentai       —— 用存着的账号密码（ehentai.username / password）重新登录换 cookie。
///
/// 只维护登着的站：没登过或用户点了「退出登录」的站一概不碰，免得把刚退出的登录又续回来。
/// 每个站点最近一次维护的结果（<see cref="StatusOf"/>）显示在账号访问页的卡片上。
/// </summary>
public static class AccountKeeper
{
    private static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    public sealed record KeepStatus(DateTime At, bool Ok, string Message);

    private static readonly object Sync = new();
    private static readonly Dictionary<string, KeepStatus> Status = [];
    private static Timer? _timer;
    private static int _running;

    public static void Start() =>
        _timer ??= new Timer(_ => _ = RunAsync(), null, FirstDelay, Interval);

    public static void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    /// <summary>该站最近一次自动维护的结果；还没维护过返回 null。</summary>
    public static KeepStatus? StatusOf(string site)
    {
        lock (Sync)
            return Status.GetValueOrDefault(site);
    }

    /// <summary>退出登录后清掉该站的维护记录（卡片上不再显示）。</summary>
    public static void Clear(string site)
    {
        lock (Sync)
            Status.Remove(site);
    }

    /// <summary>维护一轮（所有站点串行）；上一轮还没跑完就直接返回。</summary>
    public static async Task RunAsync()
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
            return;
        try
        {
            foreach (var site in AccountService.Sites)
            {
                KeepStatus? result;
                try
                {
                    result = await MaintainAsync(site);
                }
                catch (Exception e)
                {
                    result = new KeepStatus(DateTime.Now, false, "维护出错：" + e.Message);
                }
                if (result == null)
                    continue;
                lock (Sync)
                    Status[site] = result;
                if (result.Ok)
                    Logger.Info($"账号维护 {site}：{result.Message}");
                else
                    Logger.Warning($"账号维护 {site}：{result.Message}");
            }
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    /// <summary>维护一个站点；不归它管（没登录 / 正在登录窗口里登录）返回 null。</summary>
    private static async Task<KeepStatus?> MaintainAsync(string site)
    {
        if (!AccountService.IsLoggedIn(site))
            return null;
        if (site == AccountService.Ehentai)
            return await MaintainEhentaiAsync();
        if (LoginBrowser.IsOpen)
            return null;

        var (ok, message) = await AccountService.CheckAsync(site);
        if (ok)
            return new KeepStatus(DateTime.Now, true, "登录有效");
        if (!LoginBrowser.HasProfile || Application.Current is not { } app)
            return new KeepStatus(DateTime.Now, false, $"登录已失效（{message}），需要重新登录");

        // 程序里那份失效了：去登录浏览器里取它手上的（打开一次站点首页，站点会顺手续期）
        var session = await app.Dispatcher.InvokeAsync(() => LoginBrowser.ReadSessionAsync(site)).Task.Unwrap();
        if (session == null)
            return new KeepStatus(DateTime.Now, false, $"登录已失效（{message}），浏览器里的登录也已过期，需要重新登录");
        AccountService.SaveCookies(site, new Dictionary<string, string>
        {
            [site == AccountService.Fanbox ? "sessid" : "php_sessid"] = session,
        });
        (ok, message) = await AccountService.CheckAsync(site);
        return ok
            ? new KeepStatus(DateTime.Now, true, "已从登录浏览器续上 cookie")
            : new KeepStatus(DateTime.Now, false, $"从登录浏览器取到的 cookie 也无效（{message}），需要重新登录");
    }

    private static async Task<KeepStatus> MaintainEhentaiAsync()
    {
        var (ok, message) = await AccountService.CheckAsync(AccountService.Ehentai);
        // TestAsync 在表站上匿名也算「连接正常」，要看到「已登录」才算 cookie 有效
        if (ok && message.Contains("已登录"))
            return new KeepStatus(DateTime.Now, true, "登录有效");
        if (AppConfig.EhentaiUsername.Length == 0 || AppConfig.EhentaiPassword.Length == 0)
            return new KeepStatus(DateTime.Now, false, $"登录可能已失效（{message}），没有保存账号密码，无法自动重新登录");
        var r = await AccountService.LoginEhentaiAsync(AppConfig.EhentaiUsername, "");
        return r.Ok
            ? new KeepStatus(DateTime.Now, true, "已用保存的账号密码重新登录")
            : new KeepStatus(DateTime.Now, false, "自动重新登录失败：" + r.Message);
    }
}
