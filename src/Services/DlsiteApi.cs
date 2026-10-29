using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using R18MediaLibrary.Core;
using HtmlAgilityPack;

namespace R18MediaLibrary.Services;

/// <summary>DL suggest API 返回的作品数据（对应 Python 版 get_work_data 的 dict）。</summary>
public class DlWork
{
    public string WorkName { get; init; } = "";
    public string WorkNo { get; init; } = "";
    public string MakerName { get; init; } = "";
    public string MakerId { get; init; } = "";
    public string WorkType { get; init; } = "";
    public string IntroS { get; init; } = "";
    public string AgeCategory { get; init; } = "";
    public string IsAna { get; init; } = "";
}

/// <summary>搜索输入的类型：作品号（RJ/BJ/VJ）/ 社团号（RG）/ DLsite 搜索筛选列表页（fsr）/ 无法识别。</summary>
public enum SearchKind { Work, Maker, Catalog, Invalid }

/// <summary>社团作品列表中的一条作品。</summary>
public class DlMakerWork
{
    public string WorkId { get; init; } = "";   // RJ/BJ/VJ 号
    public string Title { get; set; } = "";
    public string Thumb { get; set; } = "";      // 封面缩略图 URL
}

/// <summary>DLsite 公开 suggest API 客户端（对应 Python 版 DLapi_call.py）。</summary>
public static class DlsiteApi
{
    /// <summary>调用 DL suggest API，返回该 RJ 号作品的全部数据；未获取到返回 null。</summary>
    public static async Task<DlWork?> GetWorkDataAsync(string workId)
    {
        try
        {
            using var client = Http.CreateClient(TimeSpan.FromSeconds(15));
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var url = $"https://www.dlsite.com/suggest/?term={workId}&site=adult-jp&time={timestamp}&touch=0&_={timestamp + 10}";
            var text = await client.GetStringAsync(url);
            using var doc = JsonDocument.Parse(text);
            if (!doc.RootElement.TryGetProperty("work", out var works) ||
                works.ValueKind != JsonValueKind.Array)
                return null;

            JsonElement? first = null;
            foreach (var work in works.EnumerateArray())
            {
                first ??= work;
                // 优先返回 workno 完全匹配的作品
                if (string.Equals(JStr(work, "workno"), workId, StringComparison.OrdinalIgnoreCase))
                    return Parse(work);
            }
            // suggest 只覆盖 adult-jp，VJ（pro 站）与部分新作品搜不到 → 退用作品信息接口取作品名
            return await GetInfoAsync(client, workId) ?? (first is { } f ? Parse(f) : null);
        }
        catch (Exception e)
        {
            Logger.Error(e, "DLsite suggest API");
            return null;
        }
    }

    /// <summary>作品信息接口（product/info/ajax）：各分站作品都能查到，但不给社团名。查不到返回 null。</summary>
    private static async Task<DlWork?> GetInfoAsync(HttpClient client, string workId)
    {
        try
        {
            var text = await client.GetStringAsync(
                $"https://www.dlsite.com/maniax/product/info/ajax?product_id={Uri.EscapeDataString(workId)}&cdn_cache_min=1");
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (!string.Equals(p.Name, workId, StringComparison.OrdinalIgnoreCase) ||
                    p.Value.ValueKind != JsonValueKind.Object)
                    continue;
                var name = JStr(p.Value, "work_name");
                if (name.Length == 0) return null;
                return new DlWork
                {
                    WorkName = name, WorkNo = p.Name,
                    MakerId = JStr(p.Value, "maker_id"), WorkType = JStr(p.Value, "work_type"),
                    AgeCategory = JStr(p.Value, "age_category"), IsAna = JStr(p.Value, "is_ana"),
                };
            }
        }
        catch (Exception e)
        {
            Logger.Error(e, "DLsite product info API");
        }
        return null;
    }

    /// <summary>仅取作品名称（文件夹按作品名命名时使用），失败返回 null。</summary>
    public static async Task<string?> GetWorkNameAsync(string workId)
    {
        var work = await GetWorkDataAsync(workId);
        return string.IsNullOrEmpty(work?.WorkName) ? null : work!.WorkName;
    }

    private static DlWork Parse(JsonElement work) => new()
    {
        WorkName = JStr(work, "work_name"),
        WorkNo = JStr(work, "workno"),
        MakerName = JStr(work, "maker_name"),
        MakerId = JStr(work, "maker_id"),
        WorkType = JStr(work, "work_type"),
        IntroS = JStr(work, "intro_s"),
        AgeCategory = JStr(work, "age_category"),
        IsAna = JStr(work, "is_ana"),
    };

    // ---------- 输入识别 ----------

    private static readonly Regex WorkRe = new(@"^(?:RJ|BJ|VJ)\d+$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MakerRe = new(@"^RG\d+$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex UrlWorkRe = new(@"product_id/((?:RJ|BJ|VJ)\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex UrlMakerRe = new(@"maker_id/(RG\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>DLsite 搜索/筛选/分类列表页（任意版块下的 fsr 路径，如 /maniax/fsr/、/home/fsr/）。</summary>
    private static readonly Regex CatalogUrlRe = new(@"dlsite\.com/[^/]+/fsr/", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// 识别搜索框输入：作品号 / 社团号 / DLsite 链接。
    /// DLsite 链接优先提取 product_id（作品）/ maker_id（社团）；其余任意 DLsite 列表页（fsr 搜索/筛选/分类/排行等）整页作为目录列表解析。
    /// 返回归一化后的大写编号，或（Catalog）原始 URL。
    /// </summary>
    public static (SearchKind Kind, string Id) ParseSearchInput(string raw)
    {
        raw = (raw ?? "").Trim();
        if (raw.Length == 0)
            return (SearchKind.Invalid, "");
        // 任意 DLsite 链接均可用
        if (raw.Contains("dlsite.com", StringComparison.OrdinalIgnoreCase))
        {
            // 搜索/筛选/分类列表页（fsr）：整页作为目录列表解析。
            // 须先于 maker_id 提取，否则带 maker_id 筛选的 fsr URL 会被误判为社团搜索。
            if (CatalogUrlRe.IsMatch(raw))
                return (SearchKind.Catalog, raw);
            // 具体作品/社团链接
            var m = UrlWorkRe.Match(raw);
            if (m.Success)
                return (SearchKind.Work, m.Groups[1].Value.ToUpperInvariant());
            m = UrlMakerRe.Match(raw);
            if (m.Success)
                return (SearchKind.Maker, m.Groups[1].Value.ToUpperInvariant());
            // 其他任意 DLsite 列表页（分类/排行/搜索结果等）：整页作为目录列表解析
            return (SearchKind.Catalog, raw);
        }
        // 纯编号
        var up = raw.ToUpperInvariant();
        if (WorkRe.IsMatch(up))
            return (SearchKind.Work, up);
        if (MakerRe.IsMatch(up))
            return (SearchKind.Maker, up);
        return (SearchKind.Invalid, "");
    }

    // ---------- 社团作品列表 ----------

    /// <summary>每页作品数（DLsite 社团页默认约 30）。</summary>
    public const int MakerPageSize = 30;

    /// <summary>
    /// 抓取某社团（RG 号）的作品列表，按页返回（page 从 1 开始）。返回作品列表与是否还有下一页。
    ///
    /// 说明：DLsite 无公开的"社团作品"JSON 接口，这里抓取社团页 HTML 解析。
    /// 页面结构若改版需调整下面的 URL / 解析；逻辑集中在本方法便于维护。
    /// </summary>
    public static async Task<(List<DlMakerWork> Works, bool HasMore)> GetMakerWorksAsync(string makerId, int page) =>
        await TryGetMakerWorksAsync(makerId, page) ?? ([], false);

    /// <summary>
    /// 同 <see cref="GetMakerWorksAsync"/>，但请求失败返回 null 而不是空列表——
    /// 社团监控要分清「这页没作品」与「这页没取到」，后者若当成前者，没取到的作品下一轮会被误认成新作品。
    /// </summary>
    public static async Task<(List<DlMakerWork> Works, bool HasMore)?> TryGetMakerWorksAsync(string makerId, int page)
    {
        try
        {
            using var client = Http.CreateClient(TimeSpan.FromSeconds(20));
            // 社团作品列表页（服务端渲染，按 /page/N 翻页）
            var url = page <= 1
                ? $"https://www.dlsite.com/maniax/circle/profile/=/maker_id/{makerId}.html"
                : $"https://www.dlsite.com/maniax/circle/profile/=/maker_id/{makerId}/page/{page}.html";
            var html = await client.GetStringAsync(url);
            return ParseWorkListHtml(html, page);
        }
        catch (Exception e)
        {
            Logger.Error(e, "DLsite maker works");
            return null;
        }
    }

    /// <summary>
    /// 抓取 DLsite 搜索/筛选列表页（fsr URL）的作品列表，按页返回（page 从 1 开始）。
    /// 解析方式与社团页完全一致，供搜索页复用同一作品网格展示。
    /// </summary>
    public static async Task<(List<DlMakerWork> Works, bool HasMore)> GetCatalogWorksAsync(string searchUrl, int page)
    {
        try
        {
            using var client = Http.CreateClient(TimeSpan.FromSeconds(20));
            var html = await client.GetStringAsync(BuildCatalogPageUrl(searchUrl, page));
            return ParseWorkListHtml(html, page);
        }
        catch (Exception e)
        {
            Logger.Error(e, "DLsite catalog works");
            return ([], false);
        }
    }

    private static readonly Regex PageSegRe = new(@"/page/\d+", RegexOptions.Compiled);

    /// <summary>把 fsr URL 中的 /page/N 段替换为目标页号；若无该段则在末尾补一个。</summary>
    private static string BuildCatalogPageUrl(string url, int page)
    {
        if (PageSegRe.IsMatch(url))
            return PageSegRe.Replace(url, $"/page/{page}", 1);
        return url.TrimEnd('/') + $"/page/{page}";
    }

    /// <summary>
    /// 解析作品列表页 HTML（社团页 / fsr 搜索页通用）：找所有 product_id 链接去重，
    /// 合并标题/缩略图并按 RJ 约定覆盖封面，返回本页作品与是否还有下一页。
    /// </summary>
    private static (List<DlMakerWork> Works, bool HasMore) ParseWorkListHtml(string html, int page)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        // 找所有指向 product_id 的链接，按编号去重并合并标题/缩略图
        var byId = new Dictionary<string, DlMakerWork>();
        var order = new List<string>();
        var anchors = doc.DocumentNode.SelectNodes("//a[contains(@href,'/product_id/')]");
        if (anchors != null)
            foreach (var a in anchors)
            {
                var mm = UrlWorkRe.Match(a.GetAttributeValue("href", ""));
                if (!mm.Success)
                    continue;
                var id = mm.Groups[1].Value.ToUpperInvariant();
                if (!byId.TryGetValue(id, out var work))
                {
                    work = new DlMakerWork { WorkId = id };
                    byId[id] = work;
                    order.Add(id);
                }
                // 缩略图：优先 data-src（懒加载），其次 src
                var img = a.SelectSingleNode(".//img");
                if (work.Thumb.Length == 0 && img != null)
                    work.Thumb = NormalizeThumb(
                        FirstNonEmpty(img.GetAttributeValue("data-src", ""), img.GetAttributeValue("src", "")));
                // 标题：title 属性 / img alt / 链接文本
                if (work.Title.Length == 0)
                {
                    var title = FirstNonEmpty(
                        a.GetAttributeValue("title", ""),
                        img?.GetAttributeValue("alt", "") ?? "",
                        HtmlEntity.DeEntitize(a.InnerText)?.Trim() ?? "");
                    work.Title = title;
                }
            }
        var result = order.Select(id => byId[id]).ToList();

        // 缩略图：DLsite 封面图是懒加载，抓 img 标签往往拿到占位图；
        // 这里按 DLsite 约定的封面路径直接用 RJ 号构造，更可靠（BJ/VJ 保留抓取到的）。
        foreach (var w in result)
        {
            var computed = ThumbFromWorkId(w.WorkId);
            if (computed.Length > 0)
                w.Thumb = computed;
        }

        // 是否还有下一页：存在指向 page/(page+1) 的分页链接
        var hasMore = doc.DocumentNode.SelectSingleNode(
            $"//a[contains(@href,'/page/{page + 1}')]") != null;
        return (result, hasMore && result.Count > 0);
    }

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? "";

    private static readonly Regex RjNumRe = new(@"^RJ(\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// 按 DLsite 约定从 RJ 号构造封面图 URL：
    /// 分组目录 = (编号/1000 + 1) * 1000（保持相同位宽），文件名 = {RJ号}_img_main.jpg。
    /// 例：RJ267412 → .../RJ268000/RJ267412_img_main.jpg。仅支持 RJ（同人），其它返回空串。
    /// </summary>
    private static string ThumbFromWorkId(string id)
    {
        var m = RjNumRe.Match(id);
        if (!m.Success)
            return "";
        var numStr = m.Groups[1].Value;
        if (!long.TryParse(numStr, out var num))
            return "";
        var group = (num / 1000 + 1) * 1000;
        var groupStr = group.ToString().PadLeft(numStr.Length, '0');
        return $"https://img.dlsite.jp/modpub/images2/work/doujin/RJ{groupStr}/RJ{numStr}_img_main.jpg";
    }

    /// <summary>补全缩略图 URL：协议相对（//）补 https:，站内相对（/）补域名。</summary>
    private static string NormalizeThumb(string src)
    {
        if (string.IsNullOrWhiteSpace(src))
            return "";
        if (src.StartsWith("//"))
            return "https:" + src;
        if (src.StartsWith("/"))
            return "https://www.dlsite.com" + src;
        return src;
    }

    /// <summary>取 JSON 属性为字符串（数字/布尔等也转为字符串），缺失为空串。</summary>
    internal static string JStr(JsonElement element, string prop)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(prop, out var v))
            return "";
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Null or JsonValueKind.Undefined => "",
            _ => v.GetRawText(),
        };
    }
}
