using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using R18MediaLibrary.Core;

namespace R18MediaLibrary.Services;

/// <summary>「账号访问」页上的一个账号：站点、用户号、显示名、最近一次自动维护的结果、能否自动续期（有浏览器资料）。</summary>
public sealed record AccountInfo(string Site, string UserId, string Name, AccountKeeper.KeepStatus? Keep, bool CanKeep);

/// <summary>
/// 各站点的登录账号（一级导航「🔑 账号访问」，Web 与桌面共用）：FANBOX / Fantia / pixiv / E-Hentai。
///
/// 账号只能经「在服务器上登录」（<see cref="LoginBrowser"/>）加进来：人在本机的内嵌浏览器里登录，程序取 cookie。
/// 不提供手填 cookie，也不再有 E-Hentai 的账号密码代登（用户指定）。
/// pixiv 与 FANBOX 可以登多个账号、并行使用（作品搜索的来源下拉每个账号一项，见 <see cref="SiteAccounts"/>）；
/// E-Hentai 只留一个，再登录即替换。cookie 失效后由 <see cref="AccountKeeper"/> 从浏览器资料里自动续上。
///
/// 账号名（name）只是显示用：检测时顺手记下，鉴权从不看它。
/// </summary>
public static class AccountService
{
    public const string Fanbox = "fanbox";
    public const string Fantia = FantiaApi.SourceName;
    public const string Pixiv = "pixiv";
    public const string Ehentai = "ehentai";

    /// <summary>页面上的顺序。</summary>
    public static readonly string[] Sites = [Fanbox, Fantia, Pixiv, Ehentai];

    /// <summary>账号增删、改名后触发（任意线程；界面要自行切回 UI 线程）。作品搜索的来源下拉据此重建。</summary>
    public static event Action? Changed;

    public static string Title(string site) => site switch
    {
        Fanbox => "FANBOX",
        Fantia => "Fantia",
        Pixiv => "Pixiv",
        Ehentai => "E-Hentai",
        _ => site,
    };

    /// <summary>该站能否同时登多个账号（E-Hentai 只留一个）。</summary>
    public static bool AllowsMultiple(string site) => site != Ehentai;

    public static List<AccountInfo> List(string site) =>
        SiteAccounts.List(site)
            .Select(a => new AccountInfo(site, a.UserId, a.DisplayName, AccountKeeper.StatusOf(site, a.UserId),
                a.Profile.Length > 0))
            .ToList();

    /// <summary>
    /// 登录浏览器取到登录态后存成账号并检测（同站同用户号即更新）。
    /// 被取代的旧浏览器资料删掉；E-Hentai 只留这一个账号，其余一并移除。
    /// </summary>
    public static async Task<(bool Ok, string Message)> AddFromLoginAsync(string site, LoginBrowser.Harvest harvest,
        string profile)
    {
        var old = SiteAccounts.Find(site, harvest.UserId);
        if (old != null && old.Profile.Length > 0 && old.Profile != profile)
            LoginBrowser.DeleteProfile(old.Profile);
        if (!AllowsMultiple(site))
            foreach (var other in SiteAccounts.List(site).Where(a => a.UserId != harvest.UserId))
                Remove(site, other.UserId, notify: false);
        SiteAccounts.Save(new SiteAccount(site, harvest.UserId,
            harvest.Name.Length > 0 ? harvest.Name : old?.Name ?? "", harvest.Cookie, profile));
        AccountKeeper.Clear(site, harvest.UserId);
        InvalidateClients(site);
        var result = await CheckAsync(site, harvest.UserId);
        Changed?.Invoke();
        return result;
    }

    /// <summary>检测某账号的登录是否有效；有效时记下显示名。</summary>
    public static async Task<(bool Ok, string Message)> CheckAsync(string site, string userId)
    {
        var account = SiteAccounts.Find(site, userId);
        if (account == null)
            return (false, "没有这个账号");
        SiteAccounts.Use(site, userId);
        string name;
        (bool Ok, string Message) result;
        switch (site)
        {
            case Fanbox:
            {
                var (ok, n, message) = await FanboxOfficialApi.CheckLoginAsync();
                name = n;
                result = (ok, message);
                break;
            }
            case Fantia:
            {
                var (ok, _, n, message) = await FantiaApi.CheckLoginAsync();
                name = n;
                result = (ok, message);
                break;
            }
            case Pixiv:
            {
                var (ok, message) = await PixivApi.TestAsync();
                if (!ok || !message.Contains("已登录"))
                    return (false, message);
                name = await PixivApi.GetUserNameAsync(userId) ?? "";
                result = (true, "登录有效");
                break;
            }
            case Ehentai:
            {
                var (ok, message) = await EhentaiApi.TestAsync();
                // 表站匿名也算「连接正常」，要看到「已登录」才算 cookie 有效
                if (!ok || !message.Contains("已登录"))
                    return (false, ok ? "cookie 无效或已过期" : message);
                name = account.Name;
                result = (true, SiteAccounts.CookieValue(account.Cookie, "igneous").Length > 0
                    ? "登录有效（含里站 exhentai 权限）"
                    : "登录有效（未取得 igneous，该账号暂无里站 exhentai 权限）");
                break;
            }
            default:
                return (false, "未知站点");
        }
        if (result.Ok && name.Length > 0 && name != account.Name)
        {
            SiteAccounts.Save(account with { Name = name });
            Changed?.Invoke();
        }
        return result.Ok ? (true, $"{result.Message}，账号：{(name.Length > 0 ? name : userId)}") : result;
    }

    /// <summary>
    /// 退出登录：移除账号并删掉它的浏览器资料（否则资料里的登录态还留在盘上）。
    /// 自动维护只管表里的账号，移除后即不再续它。
    /// </summary>
    public static void Remove(string site, string userId, bool notify = true)
    {
        var account = SiteAccounts.Find(site, userId);
        if (account == null)
            return;
        SiteAccounts.Remove(site, userId);
        LoginBrowser.DeleteProfile(account.Profile);
        AccountKeeper.Clear(site, userId);
        InvalidateClients(site);
        Logger.Info($"已退出 {Title(site)} 账号 {account.DisplayName}");
        if (notify)
            Changed?.Invoke();
    }

    /// <summary>某账号的 cookie 被自动维护换新后调用（客户端按 cookie 缓存，旧的要丢掉）。</summary>
    internal static void InvalidateClients(string site)
    {
        switch (site)
        {
            case Fanbox: FanboxOfficialApi.Invalidate(); break;
            case Fantia: FantiaApi.Invalidate(); break;
            case Pixiv: PixivApi.Invalidate(); break;
            case Ehentai: EhentaiApi.Invalidate(); break;
        }
    }
}
