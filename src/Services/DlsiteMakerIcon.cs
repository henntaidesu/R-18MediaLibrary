using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using R18MediaLibrary.Core;

namespace R18MediaLibrary.Services;

/// <summary>
/// DLsite 社团头像：社团主页上那张头像其实来自 ci-en（DLsite 的创作者支援站）。
///
/// 社团主页 HTML 里没有这张图——它由页面上的
/// <c>&lt;tr data-vue-component="cien-creator-feed" data-maker-id="RG…"&gt;</c>
/// 异步渲染，真正的数据源是 ci-en 的查表接口：
/// <code>
/// GET https://media.ci-en.jp/dlsite/lookup/&lt;maker_id&gt;.json
///   200 → [{ "id":…, "name":…, "icon":"https://media.ci-en.jp/public/icon/creator/…/image-200-c.jpg", … }]
///   404 → 该社团没有 ci-en 账号，也就没有头像
/// </code>
/// 所以这里直接打这个接口，不去解析社团主页（HTML 抓不到，而且改版易碎）。
///
/// 查询结果进 <c>maker_icon</c> 表：<b>没有头像也要记一行（icon_url 存空串）</b>，
/// 否则每次进社团页都会为这些社团重新发一轮必然 404 的请求。
/// </summary>
public static class DlsiteMakerIcon
{
    private const string LookupFormat = "https://media.ci-en.jp/dlsite/lookup/{0}.json";

    /// <summary>逐个社团串行查，之间留的间隔——一次补齐可能是几百个社团，别把对方打疼。</summary>
    private static readonly TimeSpan Gap = TimeSpan.FromMilliseconds(150);

    private static readonly object Sync = new();
    private static Dictionary<string, string>? _map;   // maker_id -> 头像地址（空串 = 查过，没有）
    private static bool _running;

    /// <summary>
    /// 该社团的头像地址；没有头像、或还没查过都返回空串
    /// （两者对界面是一回事：都不画图，位置照留）。
    /// </summary>
    public static string IconUrl(string? makerId)
    {
        var id = makerId ?? "";
        return id.Length > 0 ? Map().GetValueOrDefault(id, "") : "";
    }

    /// <summary>
    /// 分组查询里取该社团的 DLsite 社团号（prefix 传表别名如 "w."）。
    /// 只认 DLsite 系来源：fanbox 的 maker_id 是 pawchive 作家号、E-Hentai 的是
    /// 「artist:/group: + 标签」、pixiv 的是站点用户号，都不是 RG 号，
    /// 拿去查 ci-en 只会白发一轮必然 404 的请求。
    /// </summary>
    public static string MakerIdExpr(string prefix) =>
        $"MAX(CASE WHEN IFNULL({prefix}\"source\", '') NOT IN " +
        $"('{FanboxService.SourceName}', '{EhentaiApi.SourceName}', '{PixivApi.SourceName}') " +
        $"THEN {prefix}\"maker_id\" END)";

    public static void Invalidate()
    {
        lock (Sync)
            _map = null;
    }

    private static Dictionary<string, string> Map()
    {
        lock (Sync)
        {
            if (_map != null)
                return _map;
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var rows = Db.Select("SELECT \"maker_id\", \"icon_url\" FROM \"maker_icon\"");
            foreach (var row in rows ?? [])
                if (row[0] is string id && id.Length > 0)
                    map[id] = row[1] as string ?? "";
            return _map = map;
        }
    }

    /// <summary>
    /// 后台补齐还没查过的社团头像（单实例串行，重复调用会被忽略）。
    /// 进社团页与启动时各调一次即可：已经查过的社团不会再发请求。
    /// </summary>
    public static void Kick()
    {
        lock (Sync)
        {
            if (_running)
                return;
            _running = true;
        }
        new Thread(() => RunAsync().GetAwaiter().GetResult())
            { IsBackground = true, Name = "dlsite-maker-icon" }.Start();
    }

    private static async Task RunAsync()
    {
        try
        {
            // 已入库作品里出现过的 DLsite 社团，减去查过的
            var rows = Db.Select(
                "SELECT DISTINCT \"maker_id\" FROM \"works\" " +
                "WHERE \"state\" = '已品悦' AND \"maker_id\" IS NOT NULL AND \"maker_id\" <> '' " +
                "AND IFNULL(\"source\", '') NOT IN (@fb, @eh, @px) " +
                "AND \"maker_id\" NOT IN (SELECT \"maker_id\" FROM \"maker_icon\")",
                ("@fb", FanboxService.SourceName), ("@eh", EhentaiApi.SourceName),
                ("@px", PixivApi.SourceName)) ?? [];
            var pending = rows.Select(r => r[0] as string ?? "").Where(id => id.Length > 0).ToList();
            if (pending.Count == 0)
                return;

            Logger.Info($"DLsite 社团头像补齐：{pending.Count} 个社团待查");
            using var client = Http.CreateClient(TimeSpan.FromSeconds(20));
            var found = 0;
            foreach (var makerId in pending)
            {
                var icon = await FetchAsync(client, makerId);
                Db.Execute(
                    "INSERT OR REPLACE INTO \"maker_icon\" (\"maker_id\", \"icon_url\", \"up_time\") " +
                    "VALUES (@m, @u, @t)",
                    ("@m", makerId), ("@u", icon),
                    ("@t", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")));
                if (icon.Length > 0)
                    found++;
                await Task.Delay(Gap);
            }
            Invalidate();
            Logger.Info($"DLsite 社团头像补齐完成：{pending.Count} 个社团，其中 {found} 个有头像");
            // 头像地址有了，顺带让图床把它们也收进去（未启用图床时是空操作）
            ImageHostService.Kick();
        }
        catch (Exception e)
        {
            Logger.Error($"DLsite 社团头像补齐出错：{e.Message}");
        }
        finally
        {
            lock (Sync)
                _running = false;
        }
    }

    /// <summary>查一个社团的头像地址；没有 ci-en 账号（404）或解析不出来都返回空串。</summary>
    private static async Task<string> FetchAsync(HttpClient client, string makerId)
    {
        try
        {
            using var resp = await client.GetAsync(string.Format(LookupFormat, makerId));
            if (!resp.IsSuccessStatusCode)
                return "";   // 404 = 没有 ci-en 账号，正常情况，不记日志免得刷屏
            var text = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return "";
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var icon = DlsiteApi.JStr(item, "icon");
                if (icon.Length > 0)
                    return icon;   // 一个社团可能挂多个创作者，取第一个有头像的
            }
            return "";
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            return "";
        }
    }
}

/// <summary>
/// 社团头像地址的统一解析入口：fanbox 作家走 pawchive（地址由作家号直接拼出），
/// DLsite 社团走 ci-en 查表缓存（见 <see cref="DlsiteMakerIcon"/>）。
/// 两种来源都优先给图床地址，取图方回退源站。
/// </summary>
public static class MakerIcons
{
    /// <returns>(源站地址, 图床地址)；源站地址为空串即该社团没有头像，界面留位不画。</returns>
    public static (string Source, string Host) Resolve(string? fanboxId, string? dlsiteId)
    {
        var fanbox = fanboxId ?? "";
        if (fanbox.Length > 0)
            return (FanboxService.MakerIconUrl(fanbox),
                ImageHostService.MakerIconUrl(PawchiveApi.FanboxService, fanbox) ?? "");

        var dlsite = dlsiteId ?? "";
        if (dlsite.Length == 0)
            return ("", "");
        var icon = DlsiteMakerIcon.IconUrl(dlsite);
        return (icon,
            icon.Length > 0 ? ImageHostService.MakerIconUrl(ImageHostService.DlsiteSource, dlsite) ?? "" : "");
    }
}
