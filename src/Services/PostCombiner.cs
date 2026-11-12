using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace R18MediaLibrary.Services;

/// <summary>
/// 单帖「组合下载」算法：对一个 AS 帖子抓到的多网盘链接，按 rapidgator 优先、
/// 跨网盘同名分卷互相补齐失效者，组合出一套尽量完整、尽量来自 rapidgator 的分卷集。
/// WebServer(/api/combine、自动下载) 与 SearchPage(桌面端「组合下载」按钮) 共用此逻辑，保持单一实现。
/// </summary>
public static class PostCombiner
{
    /// <summary>单帖组合结果：Urls=选出的分卷链接，RapidgatorParts=其中来自 rapidgator 的分卷数，
    /// TotalParts=完整档案的分卷数，Complete=是否凑齐全部分卷。</summary>
    public sealed record PostAssembly(List<string> Urls, int RapidgatorParts, int TotalParts, bool Complete);

    /// <summary>
    /// 从一帖抓到的全部网盘链接里组合出一套分卷集。
    /// <paramref name="alive"/> 返回 false 时（如占位行被删除）中途取消，返回 null。
    /// </summary>
    public static async Task<PostAssembly?> AssembleAsync(
        List<string> urls, HttpClient client, Func<bool>? alive = null)
    {
        // 分卷标识(PartKey) -> 候选链接（各网盘同名分卷聚在一起，供互相补齐）
        var byPart = new Dictionary<string, List<(int Rank, string Host, string Url)>>();
        // 每个网盘覆盖的分卷集合，用于确定"完整档案"的分卷全集
        var hostParts = new Dictionary<string, HashSet<string>>();
        foreach (var u in urls)
        {
            var host = HostOf(u);
            var pk = PartKey(u);
            if (!byPart.TryGetValue(pk, out var list))
                byPart[pk] = list = [];
            list.Add((HostRank(host), host, u));
            if (!hostParts.TryGetValue(host, out var set))
                hostParts[host] = set = [];
            set.Add(pk);
        }
        // 完整分卷全集 = 拥有分卷最多的网盘（同帖同一上传者各网盘文件名一致，故分卷数最多者即完整档案）
        var required = hostParts.Values.OrderByDescending(s => s.Count).FirstOrDefault();
        if (required is not { Count: > 0 })
            return null;

        var chosen = new List<string>();
        var rgParts = 0;
        var cache = new Dictionary<string, bool>();   // 同一链接只检测一次
        foreach (var pk in required)
        {
            if (!byPart.TryGetValue(pk, out var cands))
                continue;
            foreach (var c in cands.OrderBy(c => c.Rank))   // rapidgator 优先，其次 katfile，再其它
            {
                if (alive != null && !alive())
                    return null;
                if (!cache.TryGetValue(c.Url, out var ok))
                {
                    ok = await LinkChecker.CheckUrlAsync(c.Url, client);
                    cache[c.Url] = ok;
                }
                if (ok)
                {
                    chosen.Add(c.Url);
                    if (c.Rank == 0)
                        rgParts++;
                    break;
                }
            }
        }
        if (chosen.Count == 0)
            return null;
        return new PostAssembly(chosen, rgParts, required.Count, chosen.Count == required.Count);
    }

    /// <summary>链接所属网盘的优先级（见 <see cref="HostRank"/>）。</summary>
    public static int UrlRank(string url) => HostRank(HostOf(url));

    /// <summary>网盘优先级：rapidgator=0（最优），katfile=1，其它=2。数值越小消耗的 debrid-link 流量越少，越优先。</summary>
    public static int HostRank(string host)
    {
        host = host.ToLowerInvariant();
        if (host.Contains("rapidgator") || host == "rg.to")
            return 0;
        if (host.Contains("katfile"))
            return 1;
        return 2;
    }

    /// <summary>跨网盘匹配"同一分卷"的标识：优先取文件名中的档案名（含 partN / .001 / .z01 等），
    /// 取不到再退回整段文件名。同帖各网盘同一分卷文件名一致，故据此可把失效分卷用其它网盘同名分卷补齐。</summary>
    private static string PartKey(string url)
    {
        var name = FileNameOf(url).ToLowerInvariant();
        if (name.EndsWith(".html"))
            name = name[..^5];
        var m = Regex.Match(name, @"[\w\-.]+\.(?:part\d+\.)?(?:rar|zip|7z|r\d+|z\d+|\d{3})");
        return m.Success ? m.Value : name;
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

    private static string FileNameOf(string url)
    {
        var raw = url.TrimEnd('/').Split('/')[^1].Split('?')[0];
        try { return Uri.UnescapeDataString(raw); }
        catch (Exception) { return raw; }
    }
}
