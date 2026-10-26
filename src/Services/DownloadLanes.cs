using System.Collections.Generic;
using System.Linq;

namespace R18MediaLibrary.Services;

/// <summary>
/// 下载源分道：每个下载源一条独立的「车道」，各有自己的一组下载线程（见 DownloadEngine.Run）。
/// 这样 E-Hentai 冷却 60 秒、谷歌网盘配额、debrid-link 解析慢等问题只卡住本站，其它站照常并行下载。
/// 下载列表的一级折叠分区也按这里分（两端共用：Web 经 /api/downloads 的 sections，WPF DownloadPage）。
/// </summary>
public static class DownloadLanes
{
    public const string AnimeSharing = "anime-sharing";
    public const string Asmr = "asmr";
    public const string Pawchive = "pawchive";
    public const string Fanbox = "fanbox";
    public const string Drive = "gdrive";
    public const string Ehentai = "ehentai";
    public const string Pixiv = "pixiv";

    /// <summary>全部车道（顺序即下载列表分区的显示顺序）与显示名。</summary>
    public static readonly (string Key, string Label)[] All =
    [
        (AnimeSharing, "Anime-sharing"),
        (Asmr, "asmr.one"),
        (Pawchive, "pawchive"),
        (Fanbox, "FANBOX"),
        (Drive, "谷歌网盘"),
        (Ehentai, "E-Hentai"),
        (Pixiv, "pixiv"),
    ];

    public static string LabelOf(string key) => All.FirstOrDefault(l => l.Key == key).Label ?? key;

    /// <summary>
    /// 单个文件走哪条车道。fanbox 来源一分为三：pawchive 附件、官方站附件（fanbox.cc）、
    /// 正文里的谷歌网盘链接——三者是不同的站点，限流/配额互不相干。
    /// </summary>
    public static string LaneOf(string? source, string url) => source switch
    {
        "asmr" => Asmr,
        EhentaiApi.SourceName => Ehentai,
        PixivApi.SourceName => Pixiv,
        FanboxService.SourceName when GoogleDriveClient.Parse(url) is not null => Drive,
        FanboxService.SourceName when FanboxOfficialApi.IsOfficialUrl(url) => Fanbox,
        FanboxService.SourceName => Pawchive,
        _ => AnimeSharing,
    };

    /// <summary>
    /// 作品在下载列表里归哪个分区：按作品本身的站点，而不是逐个文件——一篇投稿里混着的谷歌网盘文件
    /// 仍随投稿归在 pawchive / FANBOX 下（只有车道是分开的），整篇都是网盘文件时才归到「谷歌网盘」。
    /// </summary>
    public static string SectionOf(string? source, IEnumerable<string> urls)
    {
        var lanes = urls.Select(u => LaneOf(source, u)).ToList();
        if (lanes.Count == 0)
            return LaneOf(source, "");
        return lanes.FirstOrDefault(l => l != Drive) ?? Drive;
    }

    /// <summary>领取任务时按车道筛 download_list 的 SQL 条件（fanbox 的三条车道还要再按 url 细分，见 LaneOf）。</summary>
    internal static string SourceFilter(string lane) => lane switch
    {
        AnimeSharing => "IFNULL(\"source\", '') NOT IN ('asmr', 'fanbox', 'ehentai', 'pixiv')",
        Pawchive or Fanbox or Drive => "\"source\" = 'fanbox'",
        _ => "\"source\" = '" + lane + "'",   // 车道键即来源名（asmr / ehentai / pixiv），常量非用户输入
    };
}
