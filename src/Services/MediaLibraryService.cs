using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using R18MediaLibrary.Core;

namespace R18MediaLibrary.Services;

/// <summary>媒体库扫描进度回调：(序号, 总数, RJ号, 是否成功)。</summary>
public delegate void BackfillProgress(int index, int total, string rj, bool ok);

/// <summary>
/// 媒体库导入与元数据补全（对应 Python 版 import_local_works.py）：
/// 扫描文件夹 RJ 号入库、移动作品到其它媒体库、DL API 与作品页两级元数据补全。
/// </summary>
public static class MediaLibraryService
{
    // 作品文件夹名：DLsite 的 RJ 号，或 fanbox 的 FB+作品号、E-Hentai 的 EH+画廊号、
    // pixiv 的 PX+作品号（都以作品号命名，扫描才能认出来）
    private static readonly Regex RjPattern =
        new(@"(?:RJ\d{6,}|FB\d+|EH\d+|PX\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// 该作品的图片是否直接摊在作品目录里——没有 DLsite 那套 DataSource 子目录，也没有
    /// description.txt 里的 [img:] 正文占位图。fanbox 投稿、E-Hentai 画廊与 pixiv 作品都是这样
    /// （一篇/一本动辄上百张），详情页据此只挂封面一张、不翻目录，要看图走「查看作品」。
    /// </summary>
    public static bool IsFlatImageWork(string workId) =>
        FanboxService.IsFanboxWorkId(workId) || EhentaiService.IsEhentaiWorkId(workId) ||
        PixivService.IsPixivWorkId(workId);

    /// <summary>只读取目录下的全部一级文件夹，返回 {RJ号: 文件夹绝对路径}。</summary>
    public static Dictionary<string, string> ScanTopRjFolders(string root)
    {
        var map = new Dictionary<string, string>();
        try
        {
            foreach (var path in Directory.GetDirectories(root))
            {
                var match = RjPattern.Match(Path.GetFileName(path));
                if (match.Success)
                    map[match.Value.ToUpperInvariant()] = Path.GetFullPath(path);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Logger.Error($"媒体库目录读取失败 {root}: {e.Message}");
        }
        return map;
    }

    /// <summary>
    /// 把 RJ 号列表写入 works 表并标记状态（可附带所属媒体库名与 {RJ号: 作品文件夹路径}），返回新增数。
    /// </summary>
    public static int ImportRjList(
        IEnumerable<string> rjList, string state,
        string? library = null, Dictionary<string, string>? folders = null)
    {
        var rows = Db.Select("SELECT \"work_id\", \"cover\" FROM \"works\"");
        var existing = new Dictionary<string, string?>();
        if (rows != null)
            foreach (var row in rows)
                existing[row[0] as string ?? ""] = row[1] as string;
        var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.ffffff");
        folders ??= new Dictionary<string, string>();
        var added = 0;
        foreach (var rj in rjList)
        {
            folders.TryGetValue(rj, out var folder);
            string? cover = null;
            if (folder != null && existing.TryGetValue(rj, out var oldCover) &&
                (string.IsNullOrEmpty(oldCover) || !File.Exists(oldCover)))
            {
                // 作品文件夹在程序外被移动过时封面路径失效（meta_scanned 已置位不会重新补全）：
                // 从新文件夹的 DataSource 重新定位封面（不联网，仅查本地已有图片）
                var newCover = DlsitePage.DownloadWorkImagesAsync(rj, [], folder)
                    .GetAwaiter().GetResult();
                if (newCover.Length > 0)
                    cover = newCover;
            }
            if (existing.ContainsKey(rj))
            {
                var sets = new List<string> { "\"state\" = @state" };
                var args = new List<(string, object?)> { ("@state", state), ("@rj", rj) };
                if (library != null)
                {
                    sets.Add("\"library\" = @lib");
                    args.Add(("@lib", library));
                }
                if (folder != null)
                {
                    sets.Add("\"folder\" = @folder");
                    args.Add(("@folder", folder));
                }
                if (cover != null)
                {
                    sets.Add("\"cover\" = @cover");
                    args.Add(("@cover", cover));
                }
                Db.Execute($"UPDATE \"works\" SET {string.Join(", ", sets)} WHERE \"work_id\" = @rj",
                    args.ToArray());
            }
            else
            {
                Db.Execute(
                    "INSERT INTO \"works\" (\"work_id\", \"state\", \"library\", \"folder\", \"down_time\") " +
                    "VALUES (@rj, @state, @lib, @folder, @now)",
                    ("@rj", rj), ("@state", state), ("@lib", (object?)library),
                    ("@folder", (object?)folder), ("@now", now));
                added++;
            }
        }
        return added;
    }

    /// <summary>导入一个媒体库文件夹：读取一级文件夹的 RJ 号入库并标记为已品悦，返回 (新增数, 总数)。</summary>
    public static (int Added, int Total) ImportMediaLib(string root, string? libName = null)
    {
        if (!Directory.Exists(root))
        {
            Logger.Error($"媒体库导入失败，目录不存在: {root}");
            return (0, 0);
        }
        var rjMap = ScanTopRjFolders(root);
        var rjList = rjMap.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        var added = ImportRjList(rjList, "已品悦", libName, rjMap);
        Logger.Info($"媒体库导入完成 {root}: 扫描到 {rjList.Count} 个，新增 {added} 个");
        return (added, rjList.Count);
    }

    private static string NormalizeRoot(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>
    /// 剔除某媒体库下磁盘上已不存在的作品：从 works 与 work_genres 删除其记录，返回被删除的 RJ 号列表。
    /// 仅在能正常访问的已登记根目录范围内剔除——根目录离线（如网络盘掉线）时跳过其作品，避免误删整库。
    /// </summary>
    public static List<string> PruneMissingWorks(string libName, IReadOnlyList<string> scannedRoots)
    {
        var roots = scannedRoots.Where(Directory.Exists).Select(NormalizeRoot).ToList();
        if (roots.Count == 0)
            return [];

        var rows = Db.Select(
            "SELECT \"work_id\", \"folder\" FROM \"works\" WHERE \"library\" = @lib", ("@lib", libName));
        if (rows == null)
            return [];

        var removed = new List<string>();
        foreach (var row in rows)
        {
            var rj = row[0] as string ?? "";
            var folder = row[1] as string;
            if (rj.Length == 0 || string.IsNullOrEmpty(folder))
                continue;  // 作品文件夹未知，无法判定是否缺失 → 保留
            var full = Path.GetFullPath(folder);
            if (Directory.Exists(full))
                continue;  // 文件夹仍在 → 保留
            // 仅剔除归属于本次已扫描根目录的作品；其根目录离线的作品无法判定 → 跳过
            var underScannedRoot = roots.Any(r =>
                string.Equals(full, r, StringComparison.OrdinalIgnoreCase) ||
                full.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            if (!underScannedRoot)
                continue;
            Db.Execute("DELETE FROM \"work_genres\" WHERE \"work_id\" = @rj", ("@rj", rj));
            Db.Execute("DELETE FROM \"works\" WHERE \"work_id\" = @rj", ("@rj", rj));
            removed.Add(rj);
        }
        if (removed.Count > 0)
            Logger.Info($"媒体库 {libName} 剔除已缺失作品 {removed.Count} 个: {string.Join(", ", removed)}");
        return removed;
    }

    /// <summary>
    /// 把作品文件夹移动到目标媒体库文件夹并改写 library/folder/cover，
    /// 移动成功后同步补全该作品元数据。返回 (是否成功, 新路径或失败原因)。
    /// </summary>
    public static async Task<(bool Ok, string Message)> MoveWorkToLibraryAsync(
        string workId, string targetLib, string targetFolder)
    {
        var src = Db.Scalar("SELECT \"folder\" FROM \"works\" WHERE \"work_id\" = @w", ("@w", workId)) as string;
        if (string.IsNullOrEmpty(src))
            return (false, "作品当前文件夹未知，无法移动");
        if (!Directory.Exists(src))
            return (false, $"作品文件夹不存在: {src}");
        var leaf = Path.GetFileName(Path.TrimEndingDirectorySeparator(src));
        var dest = Path.Combine(targetFolder, leaf);
        if (string.Equals(Path.GetFullPath(dest), Path.GetFullPath(src), StringComparison.OrdinalIgnoreCase))
            return (false, "目标位置与当前位置相同");
        if (Directory.Exists(dest))
            return (false, $"目标媒体库已存在同名文件夹: {dest}");
        try
        {
            Directory.CreateDirectory(targetFolder);
            Directory.Move(src, dest);
        }
        catch (Exception e)
        {
            Logger.Error(e, "移动作品到媒体库");
            return (false, e.Message);
        }

        // cover 随文件夹一起被移动，数据库里的绝对路径必须同步改写，否则主图无法显示
        Db.Execute(
            "UPDATE \"works\" SET \"library\" = @lib, \"folder\" = @dest, " +
            "\"cover\" = REPLACE(\"cover\", @src, @dest) WHERE \"work_id\" = @w",
            ("@lib", targetLib), ("@dest", dest), ("@src", src), ("@w", workId));
        Logger.Info($"{workId} 已移动到媒体库 {targetLib}: {dest}");

        // 新媒体库自动同步元数据：缺失时补全（图片已随文件夹一并移动）
        try
        {
            await BackfillWorksFromApiAsync(delaySeconds: 0, workIds: [workId]);
            await BackfillWorkPagesAsync(delaySeconds: 0, workIds: [workId]);
        }
        catch (Exception e)
        {
            Logger.Error(e, "移动后补全元数据");
        }
        return (true, dest);
    }

    /// <summary>
    /// 彻底删除一个作品（详情页「删除作品」）：磁盘上的作品文件夹连同
    /// works / work_genres / download_list / image_host 记录一并清除，不可恢复。
    ///
    /// 与 DownloadEngine.DeleteWork 的分工：那边是"从下载列表里移除"，故意不动已入库
    /// （已品悦）的 works 行与媒体库里的文件；这边才是"把这个作品从库里删掉"。
    /// 返回 (是否成功, 失败原因)。
    /// </summary>
    public static async Task<(bool Ok, string Message)> DeleteWorkAsync(string workId)
    {
        if (string.IsNullOrWhiteSpace(workId))
            return (false, "作品号为空");
        var rows = Db.Select(
            "SELECT \"folder\" FROM \"works\" WHERE \"work_id\" = @w", ("@w", workId));
        if (rows is not { Count: > 0 })
            return (false, "作品记录不存在");
        var folder = rows[0][0] as string;

        // 先让下载线程停手（边删边写会把文件锁住），顺带清掉下载列表与下载缓存目录
        DownloadEngine.DeleteWork(workId);

        // 作品文件夹：删不掉就整个中止，works 行保留着让用户重试——
        // 只删库不删盘会留下一堆孤儿目录，下次扫描媒体库又被原样导回来
        if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
        {
            if (!IsDeletableWorkFolder(workId, folder))
            {
                Logger.Error($"{workId} 删除作品：{folder} 不像该作品的目录，已中止");
                return (false, $"作品文件夹异常，未执行删除: {folder}");
            }
            try
            {
                Directory.Delete(folder, true);
                Logger.Info($"{workId} 已删除作品文件夹: {folder}");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Logger.Error($"{workId} 作品文件夹删除失败: {e.Message}");
                return (false, $"文件删除失败：{e.Message}");
            }
        }
        // 没有作品文件夹的作品，图片落在回退目录 images/<作品号> 里
        DlsitePage.RemoveWorkDataSource(workId);

        Db.Execute("DELETE FROM \"work_genres\" WHERE \"work_id\" = @w", ("@w", workId));
        Db.Execute("DELETE FROM \"works\" WHERE \"work_id\" = @w", ("@w", workId));
        Db.Execute("DELETE FROM \"download_list\" WHERE \"work_id\" = @w", ("@w", workId));
        await ImageHostService.DeleteWorkCoverAsync(workId);
        Logger.Info($"{workId} 已从媒体库删除（文件与数据库记录）");
        return (true, "");
    }

    /// <summary>
    /// 这个文件夹是否可以当作"该作品的目录"整个删掉。宁可不删也不能删错，两条任一成立即可：
    /// 叶子名含作品号（默认命名），或它是某个已登记媒体库根 / 下载缓存根的**子目录**
    /// （`down_list.folder_name = work_name` 时作品目录按作品名命名，认不出作品号）。
    /// 已登记的根目录本身、盘符根目录一律拒绝。
    /// </summary>
    private static bool IsDeletableWorkFolder(string workId, string folder)
    {
        string full;
        try
        {
            full = NormalizeRoot(folder);
        }
        catch (Exception e) when (e is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
        var leaf = Path.GetFileName(full);
        if (leaf.Length == 0)
            return false;   // 盘符根（"D:\"）之类，没有叶子名
        var named = leaf.Contains(workId, StringComparison.OrdinalIgnoreCase);
        var underRoot = false;
        foreach (var root in AppConfig.ReadMediaLibs().SelectMany(l => l.Folders)
                     .Append(AppConfig.DownloadPath))
        {
            if (string.IsNullOrEmpty(root))
                continue;
            string r;
            try
            {
                r = NormalizeRoot(root);
            }
            catch (Exception e) when (e is ArgumentException or IOException or NotSupportedException)
            {
                continue;   // 配置里的坏路径无法比较，跳过即可
            }
            if (string.Equals(r, full, StringComparison.OrdinalIgnoreCase))
                return false;   // 媒体库根/缓存根本身，绝不整个删掉
            if (full.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                underRoot = true;
        }
        return named || underRoot;
    }

    private static (string Sql, List<(string, object?)> Args) BuildConds(
        string? library, IReadOnlyList<string>? workIds)
    {
        var sql = "";
        var args = new List<(string, object?)>();
        if (library != null)
        {
            sql += " AND \"library\" = @lib";
            args.Add(("@lib", library));
        }
        if (workIds != null)
        {
            if (workIds.Count == 0)
            {
                sql += " AND 1=0";
            }
            else
            {
                var names = new List<string>();
                for (var i = 0; i < workIds.Count; i++)
                {
                    names.Add($"@id{i}");
                    args.Add(($"@id{i}", workIds[i]));
                }
                sql += $" AND \"work_id\" IN ({string.Join(",", names)})";
            }
        }
        return (sql, args);
    }

    /// <summary>
    /// 对 works 表中缺少作品名的记录逐个调用 DL API 补全字段，返回 (补全数, 未获取到数, 总数)。
    /// library 限定媒体库；workIds 限定作品；force 为 True 时无视已有数据强制重新获取。
    /// </summary>
    public static async Task<(int Filled, int Missed, int Total)> BackfillWorksFromApiAsync(
        double delaySeconds = 0.5, BackfillProgress? progress = null,
        string? library = null, bool force = false, IReadOnlyList<string>? workIds = null)
    {
        var (cond, args) = BuildConds(library, workIds);
        var sql = force
            ? $"SELECT \"work_id\" FROM \"works\" WHERE \"state\" = '已品悦'{cond}"
            // 已扫描过元数据（meta_scanned = '1'）的作品不再重新获取
            : "SELECT \"work_id\" FROM \"works\" WHERE (\"work_name\" IS NULL OR \"work_name\" = '') " +
              $"AND COALESCE(\"meta_scanned\", '') != '1'{cond}";
        var rows = Db.Select(sql, args.ToArray());
        if (rows == null)
            return (0, 0, 0);
        var rjList = rows.Select(r => r[0] as string ?? "").ToList();

        int filled = 0, missed = 0;
        for (var i = 0; i < rjList.Count; i++)
        {
            var rj = rjList[i];
            var work = await DlsiteApi.GetWorkDataAsync(rj);
            if (work != null)
            {
                Db.Execute(
                    "UPDATE \"works\" SET \"work_name\" = @n, \"maker_id\" = @mi, \"maker_name\" = @mn, " +
                    "\"work_type\" = @t, \"intro_s\" = @s, \"age_category\" = @a, \"is_ana\" = @ana " +
                    "WHERE \"work_id\" = @rj",
                    ("@n", work.WorkName), ("@mi", work.MakerId), ("@mn", work.MakerName),
                    ("@t", work.WorkType), ("@s", work.IntroS), ("@a", work.AgeCategory),
                    ("@ana", work.IsAna), ("@rj", rj));
                filled++;
            }
            else
            {
                missed++;  // DLsite 已下架或检索不到的作品，保持原样
            }
            progress?.Invoke(i + 1, rjList.Count, rj, work != null);
            if ((i + 1) % 50 == 0)
                Logger.Info($"DL API 数据补全进度: {i + 1}/{rjList.Count}（成功 {filled}）");
            if (delaySeconds > 0)
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds));
        }
        Logger.Info($"DL API 数据补全完成: 补全 {filled} 个，未获取到 {missed} 个，共 {rjList.Count} 个");
        return (filled, missed, rjList.Count);
    }

    // 作品页抓取可补全的 works 表列
    private static readonly string[] PageColumns =
    [
        "work_name", "maker_name", "sell_date", "series", "scenario", "illust",
        "voice_actor", "age_category", "work_type", "genre", "file_size",
    ];

    /// <summary>
    /// 对未标记 meta_scanned 的已品悦作品逐个抓取 DLsite 作品页，
    /// 补全字段、保存正文与标签，并下载全部图片到作品文件夹的数据源目录。
    /// 返回 (补全数, 失败数, 总数)。
    /// </summary>
    public static async Task<(int Filled, int Missed, int Total)> BackfillWorkPagesAsync(
        double delaySeconds = 1.0, BackfillProgress? progress = null,
        string? library = null, bool force = false, IReadOnlyList<string>? workIds = null)
    {
        var (cond, args) = BuildConds(library, workIds);
        var sql = force
            ? $"SELECT \"work_id\", \"folder\" FROM \"works\" WHERE \"state\" = '已品悦'{cond}"
            // meta_scanned 是"已完整扫描"（字段+图片+正文+标签）的唯一标记
            : "SELECT \"work_id\", \"folder\" FROM \"works\" WHERE \"state\" = '已品悦' " +
              $"AND COALESCE(\"meta_scanned\", '') != '1'{cond}";
        var rows = Db.Select(sql, args.ToArray());
        if (rows == null)
            return (0, 0, 0);

        int filled = 0, missed = 0;
        for (var i = 0; i < rows.Count; i++)
        {
            var rj = rows[i][0] as string ?? "";
            var workFolder = rows[i][1] as string;
            var (data, imageUrls, bodyText) = await DlsitePage.GetWorkPageAsync(rj);
            if (data != null)
            {
                if (force)
                    // 作品页仍可访问：删除旧数据源后全量重新下载；
                    // 获取不到的作品原有元数据保持不动
                    DlsitePage.RemoveWorkDataSource(rj, workFolder);
                var cover = await DlsitePage.DownloadWorkImagesAsync(rj, imageUrls, workFolder);
                DlsitePage.SaveWorkDescription(rj, bodyText, workFolder);
                if (data.Genres.Count > 0)
                {
                    Db.Execute("DELETE FROM \"work_genres\" WHERE \"work_id\" = @rj", ("@rj", rj));
                    foreach (var genre in data.Genres)
                        Db.Execute(
                            "INSERT OR IGNORE INTO \"work_genres\" (\"work_id\", \"genre\") VALUES (@rj, @g)",
                            ("@rj", rj), ("@g", genre));
                }
                var sets = new List<string>();
                var updateArgs = new List<(string, object?)> { ("@rj", rj) };
                var n = 0;
                foreach (var col in PageColumns)
                {
                    if (!data.Fields.TryGetValue(col, out var value) || string.IsNullOrEmpty(value))
                        continue;
                    sets.Add($"\"{col}\" = @v{n}");
                    updateArgs.Add(($"@v{n}", value));
                    n++;
                }
                if (cover.Length > 0)
                {
                    sets.Add("\"cover\" = @cover");
                    updateArgs.Add(("@cover", cover));
                }
                sets.Add("\"meta_scanned\" = '1'");
                Db.Execute($"UPDATE \"works\" SET {string.Join(", ", sets)} WHERE \"work_id\" = @rj",
                    updateArgs.ToArray());
                filled++;
            }
            else
            {
                missed++;
            }
            progress?.Invoke(i + 1, rows.Count, rj, data != null);
            if ((i + 1) % 50 == 0)
                Logger.Info($"作品页元数据补全进度: {i + 1}/{rows.Count}（成功 {filled}）");
            if (delaySeconds > 0)
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds));
        }
        Logger.Info($"作品页元数据补全完成: 补全 {filled} 个，失败 {missed} 个，共 {rows.Count} 个");
        // 封面就是在这一步落的盘：开了图床就顺手把新封面补传上去（未开启为空操作）
        if (filled > 0)
            ImageHostService.Kick();
        return (filled, missed, rows.Count);
    }
}
