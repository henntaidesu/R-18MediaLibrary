using System.IO;
using R18MediaLibrary.Core;

namespace R18MediaLibrary.Services.Translate;

/// <summary>
/// 图片翻译功能的总闸。
///
/// 这个功能不是人人都要，模型又有好几个 GB，所以设计成「按需下载 + 手动启用」：
/// 没下依赖的用户完全不受影响（连模型都不会去加载）。由此带来一条必须守住的规矩——
/// **"功能开没开"一律问本类的 <see cref="Enabled"/>，不要直接读 AppConfig 里那个勾选项**。
/// 用户可能勾了开关却把模型目录删了，或者换了档位而新档位还没下；
/// 只有「勾了 + 依赖齐备」两个条件同时成立才算真的可用。
/// </summary>
public static class TranslateService
{
    /// <summary>译文图存放在作品目录下的这个子目录里，与原图一一对应、互不覆盖。</summary>
    public const string TranslatedDirName = "translated";

    /// <summary>当前档位的依赖是否齐备。</summary>
    public static bool DepsReady => TranslateModels.IsReady(AppConfig.TranslateModelTier);

    /// <summary>功能是否真的可用：用户勾了开关，且当前档位的模型依赖已下齐。</summary>
    public static bool Enabled => AppConfig.TranslateEnabledSetting && DepsReady;

    /// <summary>
    /// 该作品是否适合翻译。目前限定为「图片平铺型作品」——E-Hentai 画廊与 FANBOX 投稿，
    /// 它们的图片直接摊在作品目录里。DLsite 的音声/视频作品没有可翻译的页面图。
    /// </summary>
    public static bool CanTranslate(string workId) =>
        Enabled && MediaLibraryService.IsFlatImageWork(workId);

    /// <summary>某张原图对应的译文图路径（作品目录/translated/同名文件）。</summary>
    public static string TranslatedPathFor(string workFolder, string imageFileName) =>
        Path.Combine(workFolder, TranslatedDirName, imageFileName);

    /// <summary>该作品的译文目录（可能不存在）。</summary>
    public static string TranslatedDirOf(string workFolder) =>
        Path.Combine(workFolder, TranslatedDirName);

    /// <summary>
    /// 面向设置页的一句话状态：说清楚现在为什么能用/不能用。
    /// </summary>
    public static string StatusText()
    {
        var tier = TranslateModels.Tier(AppConfig.TranslateModelTier);
        var status = TranslateModels.Status(tier.Id);
        if (status.TotalFiles == 0)
            return "依赖清单为空，无法使用";
        if (!status.Ready)
            return $"依赖未就绪（{tier.Name}：{status.ReadyFiles}/{status.TotalFiles} 个文件，" +
                   $"已下载 {TranslateModels.FormatSize(status.HaveBytes)} / " +
                   $"约 {TranslateModels.FormatSize(status.TotalBytes)}）";
        return AppConfig.TranslateEnabledSetting
            ? $"已启用（{tier.Name}，模型目录 {TranslateModels.Root}）"
            : $"依赖已就绪（{tier.Name}），但功能未启用";
    }
}
