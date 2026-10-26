using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using R18MediaLibrary.Core;

namespace R18MediaLibrary.Services;

/// <summary>
/// 账号 cookie 自动维护：启动后稍等片刻查一轮，之后每 <see cref="Interval"/> 再查一轮，
/// 对已登录的每个账号（<see cref="SiteAccounts"/>）逐个检测，失效了就自己续上，用户不必回来重新登录。
///
/// 续的来源是该账号登录时所用的浏览器资料（<see cref="LoginBrowser"/>）：后台开一个不显示的浏览器打开站点首页，
/// 浏览器手上的会话还有效就会被站点续期，读出来存回账号（E-Hentai 顺带重新领一次里站的 igneous）。
/// 浏览器那份也失效了（要重新过人机验证）就只能记下「需要重新登录」，程序替不了人；
/// 旧版手填迁来的账号没有浏览器资料，同样只能重新登录。
///
/// 每个账号最近一次维护的结果（<see cref="StatusOf"/>）显示在账号访问页的账号行上。
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

    private static string Key(string site, string userId) => site + ":" + userId;

    public static void Start() =>
        _timer ??= new Timer(_ => _ = RunAsync(), null, FirstDelay, Interval);

    public static void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    /// <summary>某账号最近一次自动维护的结果；还没维护过返回 null。</summary>
    public static KeepStatus? StatusOf(string site, string userId)
    {
        lock (Sync)
            return Status.GetValueOrDefault(Key(site, userId));
    }

    /// <summary>账号退出或重新登录后清掉它的维护记录。</summary>
    public static void Clear(string site, string userId)
    {
        lock (Sync)
            Status.Remove(Key(site, userId));
    }

    /// <summary>维护一轮（所有账号串行）；上一轮还没跑完就直接返回。</summary>
    public static async Task RunAsync()
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
            return;
        try
        {
            foreach (var account in SiteAccounts.All())
            {
                // 人正在登录窗口里登录时不插手：那边随时会写入同一站点的账号
                if (LoginBrowser.IsOpen)
                    break;
                KeepStatus result;
                try
                {
                    result = await MaintainAsync(account);
                }
                catch (Exception e)
                {
                    result = new KeepStatus(DateTime.Now, false, "维护出错：" + e.Message);
                }
                if (SiteAccounts.Find(account.Site, account.UserId) == null)
                    continue;   // 维护期间被退出登录了
                lock (Sync)
                    Status[Key(account.Site, account.UserId)] = result;
                var label = $"账号维护 {AccountService.Title(account.Site)} {account.DisplayName}：{result.Message}";
                if (result.Ok)
                    Logger.Info(label);
                else
                    Logger.Warning(label);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    private static async Task<KeepStatus> MaintainAsync(SiteAccount account)
    {
        var (site, uid) = (account.Site, account.UserId);
        var (ok, message) = await AccountService.CheckAsync(site, uid);
        if (ok)
            return new KeepStatus(DateTime.Now, true, "登录有效");
        if (account.Profile.Length == 0 || Application.Current is not { } app)
            return new KeepStatus(DateTime.Now, false, $"登录已失效（{message}），需要重新登录");

        // 存着的那份失效了：去该账号的浏览器资料里取它手上的（打开一次站点首页，站点会顺手续期）
        var harvest = await app.Dispatcher.InvokeAsync(() => LoginBrowser.ReadAsync(site, account.Profile)).Task.Unwrap();
        if (harvest == null || harvest.UserId != uid)
            return new KeepStatus(DateTime.Now, false, $"登录已失效（{message}），浏览器里的登录也已过期，需要重新登录");
        if (harvest.Cookie != account.Cookie)
        {
            SiteAccounts.Save(account with { Cookie = harvest.Cookie });
            AccountService.InvalidateClients(site);
        }
        (ok, message) = await AccountService.CheckAsync(site, uid);
        return ok
            ? new KeepStatus(DateTime.Now, true, "已从登录浏览器续上 cookie")
            : new KeepStatus(DateTime.Now, false, $"从登录浏览器取到的 cookie 也无效（{message}），需要重新登录");
    }
}
