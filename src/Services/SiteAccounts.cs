using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using R18MediaLibrary.Core;

namespace R18MediaLibrary.Services;

/// <summary>
/// 一个站点账号：<paramref name="UserId"/> 是站上的用户号（pixiv / FANBOX 为 pixiv 用户号，E-Hentai 为 member id），
/// 与站点一起构成主键——同一个账号登第二次只是更新，不会多出一行。
/// <paramref name="Cookie"/> 是整串 Cookie 请求头（如 <c>PHPSESSID=…</c>、<c>ipb_member_id=…; ipb_pass_hash=…; igneous=…</c>），
/// 直接塞进请求即可；<paramref name="Profile"/> 是登录时所用内嵌浏览器的资料名（<see cref="LoginBrowser"/>），
/// 自动维护靠它从浏览器里续 cookie。
/// </summary>
public sealed record SiteAccount(string Site, string UserId, string Name, string Cookie, string Profile)
{
    public string DisplayName => Name.Length > 0 ? Name : UserId;
}

/// <summary>
/// 各站点的登录账号（<c>site_accounts</c> 表）。pixiv 与 FANBOX 可登多个账号、并行使用：
/// 作品搜索的来源下拉里每个账号各一项，从哪个账号入队就用哪个账号下载（<c>download_list.account</c>）。
/// E-Hentai 只留一个账号（再登录即替换）。
///
/// 「这次请求用哪个账号」由 <see cref="Use"/> 设进 AsyncLocal 上下文：Web 路由、桌面视图的异步方法、下载线程
/// 在入口处设一次，往下的 PixivApi / FanboxOfficialApi / EhentaiApi 取 cookie 时都经 <see cref="Active"/> 读它，
/// 不必把账号参数一路传进每个接口函数。上下文没设时用该站第一个账号（后台任务、旧的下载记录）。
///
/// 账号只能经「在服务器上登录」加进来（不提供手填 cookie）；旧版手填在 conf 表里的 cookie 首次加载时迁进本表。
/// </summary>
public static class SiteAccounts
{
    private static readonly object Sync = new();
    private static List<SiteAccount>? _cache;

    // site → 用户号（"" = 明确匿名）。不可变字典：异步流之间各拿各的副本，互不串改
    private static readonly AsyncLocal<ImmutableDictionary<string, string>?> Current = new();

    /// <summary>
    /// 设定当前异步流里 <paramref name="site"/> 用哪个账号：用户号 = 该账号，"" = 匿名，null = 默认（第一个账号）。
    /// 在 async 方法里调用时只影响该方法及其往下的调用，方法返回即失效；在线程的同步循环里调用则一直有效到下次改写。
    /// </summary>
    public static void Use(string site, string? userId)
    {
        var map = Current.Value ?? ImmutableDictionary<string, string>.Empty;
        Current.Value = userId == null ? map.Remove(site) : map.SetItem(site, userId);
    }

    /// <summary>当前上下文里该站使用的账号；匿名或没有任何账号时为 null。</summary>
    public static SiteAccount? Active(string site)
    {
        if (Current.Value is { } map && map.TryGetValue(site, out var uid))
            return uid.Length == 0 ? null : Find(site, uid);
        return List(site).FirstOrDefault();
    }

    /// <summary>当前上下文里该站账号的用户号（入队时记进 download_list.account）；匿名为空串。</summary>
    public static string ActiveUserId(string site) => Active(site)?.UserId ?? "";

    public static List<SiteAccount> List(string site) => All().Where(a => a.Site == site).ToList();

    public static SiteAccount? Find(string site, string userId) =>
        All().FirstOrDefault(a => a.Site == site && a.UserId == userId);

    public static List<SiteAccount> All()
    {
        lock (Sync)
            return _cache ??= Load();
    }

    /// <summary>新增或更新一个账号（按 站点 + 用户号）。</summary>
    public static void Save(SiteAccount account)
    {
        Db.Execute("""
            INSERT INTO "site_accounts" ("site", "user_id", "name", "cookie", "profile", "updated")
            VALUES (@s, @u, @n, @c, @p, @t)
            ON CONFLICT ("site", "user_id") DO UPDATE SET
                "name" = excluded."name", "cookie" = excluded."cookie", "profile" = excluded."profile",
                "updated" = excluded."updated"
            """,
            ("@s", account.Site), ("@u", account.UserId), ("@n", account.Name), ("@c", account.Cookie),
            ("@p", account.Profile), ("@t", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")));
        Invalidate();
    }

    public static void Remove(string site, string userId)
    {
        Db.Execute("DELETE FROM \"site_accounts\" WHERE \"site\" = @s AND \"user_id\" = @u",
            ("@s", site), ("@u", userId));
        Invalidate();
    }

    private static void Invalidate()
    {
        lock (Sync)
            _cache = null;
    }

    /// <summary>从 Cookie 请求头里取某个 cookie 的值；没有返回空串。</summary>
    public static string CookieValue(string? header, string name)
    {
        foreach (var part in (header ?? "").Split(';'))
        {
            var kv = part.Trim();
            if (kv.StartsWith(name + "=", StringComparison.Ordinal))
                return kv[(name.Length + 1)..].Trim();
        }
        return "";
    }

    private static List<SiteAccount> Load()
    {
        Db.Execute("""
            CREATE TABLE IF NOT EXISTS "site_accounts" (
                "site" text NOT NULL, "user_id" text NOT NULL, "name" text, "cookie" text, "profile" text,
                "updated" text, PRIMARY KEY ("site", "user_id"))
            """);
        MigrateLegacy();
        var list = new List<SiteAccount>();
        // 按加入顺序：第一个即上下文未指定时的默认账号
        foreach (var r in Db.Select(
                     "SELECT \"site\", \"user_id\", \"name\", \"cookie\", \"profile\" FROM \"site_accounts\" ORDER BY rowid") ?? [])
            list.Add(new SiteAccount(r[0] as string ?? "", r[1] as string ?? "", r[2] as string ?? "",
                r[3] as string ?? "", r[4] as string ?? ""));
        return list;
    }

    /// <summary>
    /// 旧版把手填的 cookie 存在 conf 表（fanbox.sessid / pixiv.php_sessid / ehentai.member_id…）：
    /// 迁成账号行后清掉原值，连同 E-Hentai 存着的明文密码（不再有账号密码登录）。迁过即无旧值，不会重复迁。
    /// 迁来的账号没有浏览器资料（Profile 为空），失效后只能重新「在服务器上登录」。
    /// </summary>
    private static void MigrateLegacy()
    {
        void Put(string site, string uid, string name, string cookie)
        {
            if (uid.Length == 0)
                return;
            Db.Execute("""
                INSERT OR IGNORE INTO "site_accounts" ("site", "user_id", "name", "cookie", "profile", "updated")
                VALUES (@s, @u, @n, @c, '', @t)
                """, ("@s", site), ("@u", uid), ("@n", name), ("@c", cookie),
                ("@t", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")));
        }
        string R(string section, string key) => (AppConfig.Read(section, key, "") ?? "").Trim();

        var fanbox = R("fanbox", "sessid");
        if (fanbox.Length > 0)
        {
            Put(AccountService.Fanbox, PixivApi.UserIdFromSession(fanbox), R("fanbox", "username"), "FANBOXSESSID=" + fanbox);
            AppConfig.Write("fanbox", "sessid", "");
            AppConfig.Write("fanbox", "username", "");
        }
        var pixiv = R("pixiv", "php_sessid");
        if (pixiv.Length > 0)
        {
            Put(AccountService.Pixiv, PixivApi.UserIdFromSession(pixiv), R("pixiv", "username"), "PHPSESSID=" + pixiv);
            AppConfig.Write("pixiv", "php_sessid", "");
            AppConfig.Write("pixiv", "username", "");
        }
        var member = R("ehentai", "member_id");
        var hash = R("ehentai", "pass_hash");
        if (member.Length > 0 && hash.Length > 0)
        {
            var igneous = R("ehentai", "igneous");
            Put(AccountService.Ehentai, member, R("ehentai", "username"),
                $"ipb_member_id={member}; ipb_pass_hash={hash}" + (igneous.Length > 0 ? $"; igneous={igneous}" : ""));
        }
        foreach (var key in new[] { "member_id", "pass_hash", "igneous", "password", "username" })
            if (R("ehentai", key).Length > 0)
                AppConfig.Write("ehentai", key, "");
    }
}
