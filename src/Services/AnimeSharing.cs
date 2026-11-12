using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using R18MediaLibrary.Core;
using HtmlAgilityPack;

namespace R18MediaLibrary.Services;

/// <summary>Anime-sharing 论坛搜索结果的一条帖子。</summary>
public class AsSearchResult
{
    public string Title { get; init; } = "";
    public string Url { get; init; } = "";       // threads/xxx 相对路径
    public string Minor { get; init; } = "";     // 作者 · 日期 · 回复数 · 版块
    public string Snippet { get; init; } = "";   // 帖子摘要
    public string Thumb { get; init; } = "";     // 缩略图地址
}

/// <summary>
/// Anime-sharing 论坛：搜索作品帖子 + 从帖子提取网盘下载链接
/// （对应 Python 版 get_as_work_upgroup_url.py / get_webdrive_url.py）。
/// </summary>
public static class AnimeSharing
{
    /// <summary>按番号搜索论坛，逐条解析搜索结果列表。</summary>
    public static async Task<List<AsSearchResult>> SearchWorkAsync(string workId)
    {
        var results = new List<AsSearchResult>();
        try
        {
            using var client = Http.CreateClient();
            var url = $"https://www.anime-sharing.com/search/52383544/?q={workId}&o=relevance";
            var html = await client.GetStringAsync(url);
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var items = doc.DocumentNode.SelectNodes(
                "//*[contains(@class,'block-body')]//*[contains(@class,'block-row')]");
            if (items == null)
                return results;

            foreach (var item in items)
            {
                var titleA = item.SelectSingleNode(".//*[contains(@class,'contentRow-title')]//a");
                var href = titleA?.GetAttributeValue("href", "") ?? "";
                if (href.Length == 0 || !href.Contains("/threads/"))
                    continue;
                var title = SqueezeText(titleA!);

                // 去掉 ?q= 查询串和 #post 锚点，保留 threads/xxx 相对路径
                href = Uri.UnescapeDataString(href);
                href = href.Split('?')[0].Split('#')[0].Trim('/');

                // 作者 · 日期 · 回复数 · 版块
                var minorNodes = item.SelectNodes(".//*[contains(@class,'contentRow-minor')]//li");
                var minor = minorNodes == null
                    ? ""
                    : string.Join(" · ", minorNodes.Select(SqueezeText).Where(x => x.Length > 0));

                // 帖子摘要（Title / Brand / Size / Release Date 等详情）
                var snippetNode = item.SelectSingleNode(".//*[contains(@class,'contentRow-snippet')]");
                var snippet = snippetNode == null ? "" : HtmlEntity.DeEntitize(snippetNode.InnerText).Trim();

                // 作品缩略图
                var thumbNode = item.SelectSingleNode(
                    ".//*[contains(@class,'structItem-iconContainer')]//img[contains(@class,'thread-thumbnail')]");
                var thumb = thumbNode?.GetAttributeValue("src", "") ?? "";

                results.Add(new AsSearchResult
                {
                    Title = title, Url = href, Minor = minor, Snippet = snippet, Thumb = thumb,
                });
            }
        }
        catch (Exception e)
        {
            Logger.Error(e, "AS 论坛搜索");
        }
        return results;
    }

    /// <summary>
    /// 从帖子页面提取被 debrid-link 支持的网盘下载链接。
    /// 返回 (链接列表, 帖子标题)；多于一个链接时过滤掉 mp3 链接（除非全是 mp3）。
    /// </summary>
    public static async Task<(List<string> Urls, string? Title)> GetWorkDownUrlsAsync(string threadPath)
    {
        try
        {
            using var client = Http.CreateClient();
            var url = $"https://www.anime-sharing.com/{threadPath}/";
            var html = await client.GetStringAsync(url);
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var titleNode = doc.DocumentNode.SelectSingleNode("//h1[contains(@class,'p-title-value')]");
            var title = titleNode == null ? null : HtmlEntity.DeEntitize(titleNode.InnerText).Trim();
            if (title is { Length: > 40 })
                title = title[..40] + "\n" + title[40..];

            var domains = await DebridLinkClient.SupportedDomainsAsync();
            var hrefList = new List<string>();
            var seen = new HashSet<string>();
            foreach (var xpath in new[]
                     {
                         "//span[contains(@class,'bbcode-box-content')]//a",
                         "//div[contains(@class,'bbWrapper')]//a",
                     })
            {
                var anchors = doc.DocumentNode.SelectNodes(xpath);
                if (anchors == null)
                    continue;
                foreach (var a in anchors)
                {
                    var href = a.GetAttributeValue("href", "");
                    // 去重并保持原始顺序
                    if (href.Length > 0 && DebridLinkClient.IsSupported(href, domains) && seen.Add(href))
                        hrefList.Add(href);
                }
            }

            if (hrefList.Count == 0)
                return ([], title);
            if (hrefList.Count > 1)
            {
                var notMp3 = hrefList.Where(h => !h.Contains("mp3")).ToList();
                if (notMp3.Count > 0)
                    hrefList = notMp3;
            }
            // 消耗量低的网盘优先（rapidgator → katfile → 其它）；同档保持帖子里的原始顺序（OrderBy 稳定）。
            // 之后各处按出现顺序取「第一个可用的网盘」，排序一次即全部生效
            return (hrefList.OrderBy(PostCombiner.UrlRank).ToList(), title);
        }
        catch (Exception e)
        {
            Logger.Error(e, "AS 帖子链接提取");
            return ([], null);
        }
    }

    private static string SqueezeText(HtmlNode node) =>
        string.Join(' ', HtmlEntity.DeEntitize(node.InnerText)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
