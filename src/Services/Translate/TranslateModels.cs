using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using R18MediaLibrary.Core;

namespace R18MediaLibrary.Services.Translate;

/// <summary>依赖清单里的一个文件。Size 只是清单登记的预估值，仅用于「预计下载多少」与磁盘空间检查。</summary>
public sealed record ModelFile(string Path, string Url, long Size, string? Sha256);

/// <summary>一组同类模型文件（检测 / 识别 / 修复 / 翻译），按组展示下载进度。</summary>
public sealed record ModelComponent(string Id, string Name, List<ModelFile> Files);

/// <summary>一个可选档位：决定要下哪几组模型。</summary>
public sealed record ModelTier(string Id, string Name, string Note, List<string> Components);

/// <summary>单个文件的本地状态。</summary>
public enum ModelFileState { Missing, Partial, Ready }

/// <summary>某个档位的整体依赖状态（设置页据此决定能否启用功能）。</summary>
public sealed record DepStatus(
    bool Ready, int ReadyFiles, int TotalFiles, long HaveBytes, long TotalBytes);

/// <summary>
/// 图片翻译的依赖清单与本地安装状态。
///
/// 清单是数据不是代码：优先读「模型目录/translate-models.json」，没有才用内嵌的那一份——
/// 这样换镜像源（HuggingFace 在部分网络下取不到）或升级模型都不必重新发版。
///
/// 「装好了没有」不靠清单里的 size 判断（那只是预估值，跟真实文件差几个字节就会误判成没装好），
/// 而是靠下载成功时写下的安装记录 .installed.json：记录里的大小与磁盘上的一致才算就绪。
/// 手动放进来的文件没有记录，只要非空也认，方便用户离线拷模型进来。
/// </summary>
public static class TranslateModels
{
    /// <summary>默认档位；清单里没有该 id 时取清单的第一个。</summary>
    public const string DefaultTier = "full";

    private const string ManifestFileName = "translate-models.json";
    private const string InstalledFileName = ".installed.json";

    private static readonly object Sync = new();
    private static List<ModelTier>? _tiers;
    private static Dictionary<string, ModelComponent>? _components;
    private static string? _loadedFrom;
    private static Dictionary<string, long>? _installed;   // 相对路径 -> 下载完成时的字节数

    // ---------- 目录 ----------

    /// <summary>
    /// 模型根目录。默认放在工作目录下的 models/translate（与 log/、images/ 一样相对工作目录），
    /// 用户可在设置页改到别的盘——这几个模型动辄数 GB，C 盘未必放得下。
    /// </summary>
    public static string Root
    {
        get
        {
            var configured = AppConfig.TranslateModelPath;
            return configured.Length > 0
                ? System.IO.Path.GetFullPath(configured)
                : System.IO.Path.GetFullPath(System.IO.Path.Combine("models", "translate"));
        }
    }

    /// <summary>某个清单文件在本地的绝对路径。</summary>
    public static string LocalPath(ModelFile file)
    {
        var rel = file.Path.Replace('/', System.IO.Path.DirectorySeparatorChar);
        return System.IO.Path.GetFullPath(System.IO.Path.Combine(Root, rel));
    }

    /// <summary>下载中的临时文件（断点续传就落在这里，下完才改名到正式路径）。</summary>
    public static string PartPath(ModelFile file) => LocalPath(file) + ".part";

    // ---------- 清单 ----------

    /// <summary>丢弃已解析的清单与安装记录缓存（改了模型目录或换了清单文件后调用）。</summary>
    public static void Invalidate()
    {
        lock (Sync)
        {
            _tiers = null;
            _components = null;
            _loadedFrom = null;
            _installed = null;
        }
    }

    /// <summary>本次清单的来源，仅用于设置页/日志显示。</summary>
    public static string LoadedFrom
    {
        get { EnsureLoaded(); return _loadedFrom ?? "内置"; }
    }

    public static IReadOnlyList<ModelTier> Tiers
    {
        get { EnsureLoaded(); return _tiers!; }
    }

    /// <summary>按 id 取档位；取不到时退回默认档，再取不到就用第一档（清单永远至少有一档）。</summary>
    public static ModelTier Tier(string? id)
    {
        EnsureLoaded();
        return _tiers!.FirstOrDefault(t => t.Id == id)
            ?? _tiers!.FirstOrDefault(t => t.Id == DefaultTier)
            ?? _tiers![0];
    }

    /// <summary>某档位要用到的组件（按清单里的顺序）。</summary>
    public static List<ModelComponent> ComponentsOf(string? tierId)
    {
        EnsureLoaded();
        var list = new List<ModelComponent>();
        foreach (var id in Tier(tierId).Components)
            if (_components!.TryGetValue(id, out var comp))
                list.Add(comp);
        return list;
    }

    /// <summary>某档位要用到的全部文件。</summary>
    public static List<ModelFile> FilesOf(string? tierId) =>
        ComponentsOf(tierId).SelectMany(c => c.Files).ToList();

    private static void EnsureLoaded()
    {
        if (_tiers != null) return;
        lock (Sync)
        {
            if (_tiers != null) return;
            var (json, from) = ReadManifestJson();
            try
            {
                Parse(json, out _tiers, out _components);
                _loadedFrom = from;
            }
            catch (Exception e)
            {
                // 用户自己换的清单写坏了，不能让整个功能不可用：退回内置那份
                Logger.Error(e, $"图片翻译依赖清单解析失败（{from}），改用内置清单");
                Parse(EmbeddedJson(), out _tiers, out _components);
                _loadedFrom = "内置（外部清单解析失败）";
            }
        }
    }

    private static (string Json, string From) ReadManifestJson()
    {
        var external = System.IO.Path.Combine(Root, ManifestFileName);
        try
        {
            if (File.Exists(external))
                return (File.ReadAllText(external), external);
        }
        catch (Exception e)
        {
            Logger.Error(e, $"读取外部依赖清单失败：{external}");
        }
        return (EmbeddedJson(), "内置");
    }

    private static string EmbeddedJson()
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("." + ManifestFileName, StringComparison.OrdinalIgnoreCase));
        if (name == null)
            throw new InvalidOperationException($"内嵌资源缺失：{ManifestFileName}");
        using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static void Parse(
        string json, out List<ModelTier> tiers, out Dictionary<string, ModelComponent> components)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        components = new Dictionary<string, ModelComponent>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("components", out var comps) && comps.ValueKind == JsonValueKind.Object)
            foreach (var prop in comps.EnumerateObject())
            {
                var files = new List<ModelFile>();
                if (prop.Value.TryGetProperty("files", out var arr) && arr.ValueKind == JsonValueKind.Array)
                    foreach (var f in arr.EnumerateArray())
                    {
                        var path = Str(f, "path");
                        var url = Str(f, "url");
                        if (path.Length == 0 || url.Length == 0)
                            continue;
                        var size = f.TryGetProperty("size", out var s) && s.TryGetInt64(out var v) ? v : 0;
                        var sha = f.TryGetProperty("sha256", out var h) && h.ValueKind == JsonValueKind.String
                            ? h.GetString()
                            : null;
                        files.Add(new ModelFile(path, url, size, sha));
                    }
                if (files.Count > 0)
                    components[prop.Name] =
                        new ModelComponent(prop.Name, Str(prop.Value, "name", prop.Name), files);
            }

        tiers = [];
        if (root.TryGetProperty("tiers", out var ts) && ts.ValueKind == JsonValueKind.Array)
            foreach (var t in ts.EnumerateArray())
            {
                var id = Str(t, "id");
                if (id.Length == 0) continue;
                var ids = new List<string>();
                if (t.TryGetProperty("components", out var cs) && cs.ValueKind == JsonValueKind.Array)
                    foreach (var c in cs.EnumerateArray())
                        if (c.GetString() is { Length: > 0 } cid)
                            ids.Add(cid);
                tiers.Add(new ModelTier(id, Str(t, "name", id), Str(t, "note"), ids));
            }

        if (tiers.Count == 0)
            throw new InvalidOperationException("依赖清单里没有任何可用档位");
    }

    private static string Str(JsonElement el, string name, string fallback = "") =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? fallback
            : fallback;

    // ---------- 安装记录 ----------

    private static string InstalledPath => System.IO.Path.Combine(Root, InstalledFileName);

    private static Dictionary<string, long> Installed()
    {
        if (_installed != null) return _installed;
        lock (Sync)
        {
            if (_installed != null) return _installed;
            var map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (File.Exists(InstalledPath))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(InstalledPath));
                    foreach (var prop in doc.RootElement.EnumerateObject())
                        if (prop.Value.TryGetInt64(out var size))
                            map[prop.Name] = size;
                }
            }
            catch (Exception e)
            {
                Logger.Error(e, "读取模型安装记录失败，按未安装处理");
            }
            return _installed = map;
        }
    }

    /// <summary>记下某个文件已装好（下载线程在改名成功后调用）。</summary>
    internal static void MarkInstalled(ModelFile file, long size)
    {
        lock (Sync)
        {
            var map = Installed();
            map[file.Path] = size;
            SaveInstalled(map);
        }
    }

    /// <summary>抹掉某个文件的安装记录（删除依赖时调用）。</summary>
    internal static void MarkRemoved(ModelFile file)
    {
        lock (Sync)
        {
            var map = Installed();
            if (map.Remove(file.Path))
                SaveInstalled(map);
        }
    }

    private static void SaveInstalled(Dictionary<string, long> map)
    {
        try
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(InstalledPath, JsonSerializer.Serialize(map, JsonOpts));
        }
        catch (Exception e)
        {
            Logger.Error(e, "写入模型安装记录失败");
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    // ---------- 状态 ----------

    /// <summary>
    /// 单个文件的本地状态。判定顺序：有安装记录且大小对得上 → 就绪；
    /// 没有记录但文件非空也认就绪（用户离线拷进来的）；有非空 .part → 下了一半；否则缺失。
    /// </summary>
    public static ModelFileState StateOf(ModelFile file)
    {
        try
        {
            var path = LocalPath(file);
            if (File.Exists(path))
            {
                var len = new FileInfo(path).Length;
                if (len <= 0)
                    return ModelFileState.Missing;
                if (Installed().TryGetValue(file.Path, out var recorded))
                    return len == recorded ? ModelFileState.Ready : ModelFileState.Missing;
                return ModelFileState.Ready;
            }
            var part = PartPath(file);
            if (File.Exists(part) && new FileInfo(part).Length > 0)
                return ModelFileState.Partial;
        }
        catch (Exception e)
        {
            Logger.Error(e, $"检查模型文件状态失败：{file.Path}");
        }
        return ModelFileState.Missing;
    }

    /// <summary>本地已占用的字节数（正式文件按实际大小，半截文件按 .part 大小）。</summary>
    public static long HaveBytesOf(ModelFile file)
    {
        try
        {
            var path = LocalPath(file);
            if (File.Exists(path))
                return new FileInfo(path).Length;
            var part = PartPath(file);
            if (File.Exists(part))
                return new FileInfo(part).Length;
        }
        catch (Exception e)
        {
            Logger.Error(e, $"读取模型文件大小失败：{file.Path}");
        }
        return 0;
    }

    /// <summary>某档位的整体依赖状态。</summary>
    public static DepStatus Status(string? tierId)
    {
        var files = FilesOf(tierId);
        var ready = 0;
        long have = 0, total = 0;
        foreach (var f in files)
        {
            if (StateOf(f) == ModelFileState.Ready) ready++;
            var got = HaveBytesOf(f);
            have += got;
            // 清单 size 只是预估；真下得比预估多就以实际为准，免得进度条超过 100%
            total += Math.Max(f.Size, got);
        }
        return new DepStatus(files.Count > 0 && ready == files.Count, ready, files.Count, have, total);
    }

    /// <summary>该档位的依赖是否齐备——启用图片翻译的唯一前置条件。</summary>
    public static bool IsReady(string? tierId) => Status(tierId).Ready;

    /// <summary>删除某档位的模型文件（含没下完的 .part）；返回释放的字节数。</summary>
    public static long Remove(string? tierId)
    {
        long freed = 0;
        foreach (var file in FilesOf(tierId))
        {
            foreach (var path in new[] { LocalPath(file), PartPath(file) })
                try
                {
                    if (!File.Exists(path)) continue;
                    freed += new FileInfo(path).Length;
                    File.Delete(path);
                }
                catch (Exception e)
                {
                    Logger.Error(e, $"删除模型文件失败：{path}");
                }
            MarkRemoved(file);
        }
        Logger.Info($"已删除图片翻译依赖（{Tier(tierId).Name}），释放 {FormatSize(freed)}");
        return freed;
    }

    /// <summary>人类可读的体积文字（设置页与日志共用）。</summary>
    public static string FormatSize(long bytes)
    {
        if (bytes >= 1L << 30) return $"{bytes / (double)(1L << 30):0.##} GB";
        if (bytes >= 1L << 20) return $"{bytes / (double)(1L << 20):0.#} MB";
        if (bytes >= 1L << 10) return $"{bytes / (double)(1L << 10):0} KB";
        return $"{bytes} B";
    }
}
