using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using R18MediaLibrary.Core;

namespace R18MediaLibrary.Services;

/// <summary>社团的两个主页地址（都可能为空）。</summary>
public readonly record struct MakerHome(string Fanbox, string Pawchive)
{
    public bool IsEmpty => string.IsNullOrEmpty(Fanbox) && string.IsNullOrEmpty(Pawchive);
}

/// <summary>
/// 社团主页：给社团记一个 FANBOX 官方主页（<c>https://&lt;creatorId&gt;.fanbox.cc/</c>）和一个 pawchive 主页
/// （<c>https://pawchive.pw/fanbox/user/&lt;作家号&gt;</c>），社团卡上据此显示两个外链。
///
/// 按 <c>works.maker_name</c> 这个真名记（同 <see cref="MakerAliasService"/>），因为社团卡就是按它分组的。
/// fanbox 作家在下载时自动记上（<see cref="Kick"/>）：pawchive 地址由作家号直接拼出，
/// FANBOX 地址要 creatorId——入队时手里有就直接用（<see cref="Remember"/>），没有再问一次 pawchive 的作家资料。
///
/// <b>表里有这一行就不再自动填</b>，哪怕两个地址都是空串：那是用户在社团设置里亲手清掉的，
/// 自动补齐要是再填回去，用户就永远删不掉它（同 maker_icon 用空串表示"查过了"）。
/// 映射整表驻留内存：社团卡是成批渲染的，逐张查库等于把渲染又拖慢一遍。
/// </summary>
public static class MakerHomeService
{
    /// <summary>逐个作家串行问 pawchive，之间留的间隔（同 DlsiteMakerIcon，别把对方打疼）。</summary>
    private static readonly TimeSpan Gap = TimeSpan.FromMilliseconds(300);

    private static readonly object Sync = new();
    private static Dictionary<string, MakerHome>? _map;
    private static bool _running, _again;

    // 作家号 → creatorId：入队时已知的，补齐时直接用、不再问站点
    private static readonly ConcurrentDictionary<string, string> PublicIdHints = new();

    /// <summary>真名 → 主页。</summary>
    public static Dictionary<string, MakerHome> Map()
    {
        lock (Sync)
        {
            if (_map != null)
                return _map;
            var map = new Dictionary<string, MakerHome>(StringComparer.Ordinal);
            var rows = Db.Select("SELECT \"maker_name\", \"fanbox_url\", \"pawchive_url\" FROM \"maker_home\"");
            foreach (var row in rows ?? [])
                if (row[0] is string name && name.Length > 0)
                    map[name] = new MakerHome(row[1] as string ?? "", row[2] as string ?? "");
            return _map = map;
        }
    }

    /// <summary>该社团的主页；没有记录返回两个空串。</summary>
    public static MakerHome Get(string? makerName)
    {
        var name = makerName ?? "";
        return name.Length > 0 && Map().TryGetValue(name, out var home) ? home : new MakerHome("", "");
    }

    /// <summary>
    /// 手动设置主页（社团设置里保存）。两个都留空也照样记一行，表示"用户不要主页"，
    /// 自动补齐不会再把它填回来。地址不合法（非 http/https）返回错误文案、什么都不写。
    /// </summary>
    public static string? Set(string makerName, string? fanboxUrl, string? pawchiveUrl)
    {
        if (makerName.Length == 0)
            return "社团名为空";
        var fanbox = NormalizeUrl(fanboxUrl);
        var pawchive = NormalizeUrl(pawchiveUrl);
        if (fanbox == null || pawchive == null)
            return "主页地址必须是 http:// 或 https:// 开头的网址";
        Db.Execute(
            "INSERT OR REPLACE INTO \"maker_home\" (\"maker_name\", \"fanbox_url\", \"pawchive_url\", \"up_time\") " +
            "VALUES (@m, @f, @p, @t)",
            ("@m", makerName), ("@f", fanbox), ("@p", pawchive),
            ("@t", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")));
        Invalidate();
        return null;
    }

    /// <summary>
    /// 规整用户输入的地址：空 → 空串；省了协议头的补 https://；不是 http/https 的返回 null。
    /// 只放行 http/https——地址会原样进 Web 端的 href，<c>javascript:</c> 之类不能让它进库。
    /// </summary>
    public static string? NormalizeUrl(string? raw)
    {
        var v = (raw ?? "").Trim();
        if (v.Length == 0)
            return "";
        if (!v.Contains("://"))
            v = "https://" + v;
        return Uri.TryCreate(v, UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri.AbsoluteUri
            : null;
    }

    /// <summary>丢弃内存映射，下次读取重新载入。</summary>
    public static void Invalidate()
    {
        lock (Sync)
            _map = null;
    }

    /// <summary>
    /// 下载 fanbox 作品时调用：记下该作家的 creatorId（省掉补齐时的一次请求），再触发补齐。
    /// 作家已有主页记录的不会被改动。
    /// </summary>
    public static void Remember(PawchiveArtist artist)
    {
        if (artist.PublicId.Length > 0 && artist.MakerId.Length > 0)
            PublicIdHints[artist.MakerId] = artist.PublicId;
        Kick();
    }

    /// <summary>
    /// 后台给还没有主页记录的 fanbox 作家补上主页（单实例串行；运行中再被调用会在本轮结束后再跑一轮，
    /// 免得漏掉刚入队的作家）。启动时与每次 fanbox 入队后各调一次即可。
    /// </summary>
    public static void Kick()
    {
        lock (Sync)
        {
            if (_running)
            {
                _again = true;
                return;
            }
            _running = true;
        }
        new Thread(() => RunAsync().GetAwaiter().GetResult())
            { IsBackground = true, Name = "maker-home" }.Start();
    }

    private static async Task RunAsync()
    {
        while (true)
        {
            try
            {
                await FillOnceAsync();
            }
            catch (Exception e)
            {
                Logger.Error($"社团主页补齐出错：{e.Message}");
            }
            lock (Sync)
            {
                if (!_again)
                {
                    _running = false;
                    return;
                }
                _again = false;
            }
        }
    }

    private static async Task FillOnceAsync()
    {
        // fanbox 作品出现过、但还没有主页记录的作家（含正在下载的，下完入库时卡片上就已有链接）
        var rows = Db.Select(
            "SELECT \"maker_name\", MAX(\"maker_id\") FROM \"works\" " +
            "WHERE \"source\" = @fb AND \"maker_name\" IS NOT NULL AND \"maker_name\" <> '' " +
            "AND \"maker_id\" IS NOT NULL AND \"maker_id\" <> '' " +
            "AND \"maker_name\" NOT IN (SELECT \"maker_name\" FROM \"maker_home\") " +
            "GROUP BY \"maker_name\"",
            ("@fb", FanboxService.SourceName)) ?? [];
        var pending = rows
            .Select(r => (Name: r[0] as string ?? "", Id: r[1] as string ?? ""))
            .Where(p => p.Name.Length > 0 && p.Id.Length > 0)
            .ToList();
        if (pending.Count == 0)
            return;

        Logger.Info($"社团主页补齐：{pending.Count} 位 FANBOX 作家待补");
        var added = 0;
        foreach (var (name, makerId) in pending)
        {
            if (!PublicIdHints.TryGetValue(makerId, out var publicId))
            {
                var profile = await PawchiveApi.GetArtistAsync(PawchiveApi.FanboxService, makerId);
                await Task.Delay(Gap);
                if (profile == null)
                    continue;   // 站点没响应：这次不记，下一轮再问，免得把 FANBOX 链接永久记成空
                publicId = profile.PublicId;
            }
            var fanbox = FanboxOfficialApi.IsCreatorId(publicId) ? FanboxOfficialApi.CreatorPageUrl(publicId) : "";
            if (fanbox.Length > 0)
                await AdoptFanboxNameAsync(name, publicId);
            // INSERT OR IGNORE：补齐期间用户可能刚好手动设了主页，以用户的为准
            Db.Execute(
                "INSERT OR IGNORE INTO \"maker_home\" (\"maker_name\", \"fanbox_url\", \"pawchive_url\", \"up_time\") " +
                "VALUES (@m, @f, @p, @t)",
                ("@m", name), ("@f", fanbox),
                ("@p", PawchiveApi.ArtistPageUrl(PawchiveApi.FanboxService, makerId)),
                ("@t", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")));
            added++;
        }
        Invalidate();
        Logger.Info($"社团主页补齐完成：记下 {added} 位作家");
    }

    /// <summary>
    /// 社团卡显示 FANBOX 上的作家名，而不是 pawchive 归档的那个（pawchive 的名字常是旧名或 pixiv 名）。
    /// 写成显示名映射（<see cref="MakerAliasService"/>）而不是改 works.maker_name：真名是分组键，
    /// 而且用户之后仍能在社团设置里改掉它。已经设过显示名的不动——那是用户自己起的。
    /// 只在作家第一次记主页时做一次；取不到（Cloudflare / 网络）就作罢，照旧显示真名。
    /// </summary>
    private static async Task AdoptFanboxNameAsync(string makerName, string creatorId)
    {
        if (MakerAliasService.Map().ContainsKey(makerName))
            return;
        var creator = await FanboxOfficialApi.GetCreatorAsync(creatorId);
        await Task.Delay(Gap);
        var fanboxName = creator?.Name.Trim() ?? "";
        if (fanboxName.Length > 0 && fanboxName != makerName)
            MakerAliasService.Set(makerName, fanboxName);
    }
}
