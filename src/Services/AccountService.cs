using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using R18MediaLibrary.Core;

namespace R18MediaLibrary.Services;

/// <summary>「账号访问」页上的一个站点：登录与否 + 显示用的账号名。</summary>
public sealed record AccountInfo(string Site, string Title, bool LoggedIn, string Account);

/// <summary>
/// 各站点的登录状态（一级导航「🔑 账号访问」，Web 与桌面共用）：FANBOX / pixiv / E-Hentai。
///
/// 三个站的鉴权都是 cookie：
///   FANBOX   —— FANBOXSESSID（填了之后作品搜索多一个「FANBOX（账号名）」来源，直连 fanbox.cc 官方站）；
///   pixiv    —— PHPSESSID（不填也能搜，但站点会把 R-18 从结果里滤掉）；
///   E-Hentai —— ipb_member_id / ipb_pass_hash / igneous（可用账号密码登录换取，见 EhentaiApi.LoginAsync）。
/// pixiv 与 FANBOX 的登录都带人机验证，程序里走不通，只能从浏览器复制 cookie；E-Hentai 两种都行，
/// 且账号密码按用户要求存库（ehentai.username / password），cookie 失效时可直接再点一次登录。
///
/// 账号名（username）只是「检测」时顺手记下的显示名，鉴权从不看它；cookie 一换就清掉，等下一次检测重新取，
/// 免得显示的是上一个账号的名字。
/// </summary>
public static class AccountService
{
    public const string Fanbox = "fanbox";
    public const string Pixiv = "pixiv";
    public const string Ehentai = "ehentai";

    /// <summary>页面上的顺序。</summary>
    public static readonly string[] Sites = [Fanbox, Pixiv, Ehentai];

    /// <summary>登录状态或账号名变了（任意线程触发；界面要自行切回 UI 线程）。作品搜索的来源下拉据此重建。</summary>
    public static event Action? Changed;

    public static bool IsLoggedIn(string site) => site switch
    {
        Fanbox => FanboxOfficialApi.HasCookie,
        Pixiv => PixivApi.HasCookie,
        Ehentai => EhentaiApi.HasCookie,
        _ => false,
    };

    /// <summary>显示用的账号名：检测时记下的名字，没有就退回 cookie 里能看出来的用户号。</summary>
    public static string DisplayName(string site)
    {
        switch (site)
        {
            case Fanbox:
                return FirstNonEmpty(AppConfig.FanboxUsername,
                    PixivApi.UserIdFromSession(AppConfig.FanboxSessionId), "已登录");
            case Pixiv:
                return FirstNonEmpty(AppConfig.PixivUsername,
                    PixivApi.UserIdFromSession(AppConfig.PixivSessionId), "已登录");
            case Ehentai:
                return FirstNonEmpty(AppConfig.EhentaiUsername,
                    AppConfig.EhentaiMemberId.Length > 0 ? "member_id " + AppConfig.EhentaiMemberId : "", "已登录");
            default:
                return "";
        }
    }

    private static string FirstNonEmpty(params string[] values)
    {
        foreach (var v in values)
            if (!string.IsNullOrEmpty(v))
                return v;
        return "";
    }

    public static AccountInfo Info(string site) => site switch
    {
        Fanbox => new AccountInfo(Fanbox, "FANBOX", IsLoggedIn(Fanbox), IsLoggedIn(Fanbox) ? DisplayName(Fanbox) : ""),
        Pixiv => new AccountInfo(Pixiv, "pixiv", IsLoggedIn(Pixiv), IsLoggedIn(Pixiv) ? DisplayName(Pixiv) : ""),
        Ehentai => new AccountInfo(Ehentai, "E-Hentai", IsLoggedIn(Ehentai),
            IsLoggedIn(Ehentai) ? DisplayName(Ehentai) : ""),
        _ => new AccountInfo(site, site, false, ""),
    };

    public static List<AccountInfo> All()
    {
        var list = new List<AccountInfo>();
        foreach (var site in Sites)
            list.Add(Info(site));
        return list;
    }

    /// <summary>
    /// 保存手工填写的 cookie。values 只认各站自己的字段名（见类注释），其余忽略；
    /// 值有变化才清显示名并重建客户端。
    /// </summary>
    public static void SaveCookies(string site, IReadOnlyDictionary<string, string> values)
    {
        var changed = false;
        void Put(string section, string key)
        {
            if (!values.TryGetValue(key, out var raw))
                return;
            var v = NormalizeCookie(raw, key);
            if ((AppConfig.Read(section, key, "") ?? "").Trim() == v)
                return;
            AppConfig.Write(section, key, v);
            changed = true;
        }

        switch (site)
        {
            case Fanbox:
                Put("fanbox", "sessid");
                if (changed)
                {
                    AppConfig.Write("fanbox", "username", "");
                    FanboxOfficialApi.Invalidate();
                }
                break;
            case Pixiv:
                Put("pixiv", "php_sessid");
                if (changed)
                {
                    AppConfig.Write("pixiv", "username", "");
                    PixivApi.Invalidate();
                }
                break;
            case Ehentai:
                Put("ehentai", "member_id");
                Put("ehentai", "pass_hash");
                Put("ehentai", "igneous");
                if (changed)
                    EhentaiApi.Invalidate();
                break;
        }
        if (changed)
            Changed?.Invoke();
    }

    /// <summary>
    /// 用户常把整段「FANBOXSESSID=xxx」甚至带分号的一整串 cookie 粘进来：认出对应字段就只取它的值。
    /// </summary>
    private static string NormalizeCookie(string raw, string key)
    {
        raw = (raw ?? "").Trim();
        var name = key switch
        {
            "sessid" => "FANBOXSESSID",
            "php_sessid" => "PHPSESSID",
            "member_id" => "ipb_member_id",
            "pass_hash" => "ipb_pass_hash",
            _ => key,
        };
        foreach (var part in raw.Split(';'))
        {
            var kv = part.Trim();
            if (kv.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
                return kv[(name.Length + 1)..].Trim();
        }
        return raw;
    }

    /// <summary>检测登录是否有效；有效时记下显示名。</summary>
    public static async Task<(bool Ok, string Message)> CheckAsync(string site)
    {
        switch (site)
        {
            case Fanbox:
            {
                var (ok, name, message) = await FanboxOfficialApi.CheckLoginAsync();
                if (ok && name.Length > 0 && name != AppConfig.FanboxUsername)
                {
                    AppConfig.Write("fanbox", "username", name);
                    Changed?.Invoke();
                }
                return (ok, ok ? $"{message}，账号：{name}" : message);
            }
            case Pixiv:
            {
                if (!PixivApi.HasCookie)
                    return (false, "未填写 PHPSESSID");
                var (ok, message) = await PixivApi.TestAsync();
                if (!ok || !message.Contains("已登录"))
                    return (false, message);
                var userId = PixivApi.UserIdFromSession(AppConfig.PixivSessionId);
                var name = await PixivApi.GetUserNameAsync(userId) ?? userId;
                if (name.Length > 0 && name != AppConfig.PixivUsername)
                {
                    AppConfig.Write("pixiv", "username", name);
                    Changed?.Invoke();
                }
                return (true, $"登录有效，账号：{name}");
            }
            case Ehentai:
                if (!EhentaiApi.HasCookie)
                    return (false, "未填写 ipb_member_id / ipb_pass_hash");
                return await EhentaiApi.TestAsync();
            default:
                return (false, "未知站点");
        }
    }

    /// <summary>
    /// E-Hentai 账号密码登录（站点里只有它能这样登）；成功即写好三个 cookie。
    /// 账号密码都存库；password 为空时用已保存的那个（网页端不回显密码，留空即沿用）。
    /// </summary>
    public static async Task<EhentaiApi.EhLoginResult> LoginEhentaiAsync(string username, string password)
    {
        AppConfig.Write("ehentai", "username", username.Trim());
        if (password.Length == 0)
            password = AppConfig.EhentaiPassword;
        else
            AppConfig.Write("ehentai", "password", password);
        var result = await EhentaiApi.LoginAsync(username, password);
        Changed?.Invoke();
        return result;
    }

    /// <summary>退出登录：清掉该站的 cookie 与显示名（E-Hentai 的账号密码留着，方便下次再登）。</summary>
    public static void Logout(string site)
    {
        switch (site)
        {
            case Fanbox:
                AppConfig.Write("fanbox", "sessid", "");
                AppConfig.Write("fanbox", "username", "");
                FanboxOfficialApi.Invalidate();
                break;
            case Pixiv:
                AppConfig.Write("pixiv", "php_sessid", "");
                AppConfig.Write("pixiv", "username", "");
                PixivApi.Invalidate();
                break;
            case Ehentai:
                AppConfig.Write("ehentai", "member_id", "");
                AppConfig.Write("ehentai", "pass_hash", "");
                AppConfig.Write("ehentai", "igneous", "");
                EhentaiApi.Invalidate();
                break;
            default:
                return;
        }
        Changed?.Invoke();
    }
}
