using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using R18MediaLibrary.Core;

namespace R18MediaLibrary.Services;

/// <summary>一条 Pixiv 收藏监控记录（pixiv_watch 表的一行，一个 pixiv 账号一条）。</summary>
public class PixivWatch
{
    /// <summary>pixiv 用户号（即 site_accounts 里该账号的 user_id）。</summary>
    public string UserId { get; init; } = "";
    public string UserName { get; init; } = "";
    /// <summary>轮询间隔（分钟）。</summary>
    public int IntervalMin { get; init; } = 60;
    /// <summary>该条监控是否参与自动轮询（暂停的监控仍可手动「立即检查」）。</summary>
    public bool Enabled { get; init; } = true;
    /// <summary>上次检查时间（yyyy-MM-dd HH:mm:ss），空表示还没查过。</summary>
    public string LastCheck { get; init; } = "";
    /// <summary>
    /// 水位线：已处理到的最新收藏的收藏号（bookmarkData.id，随收藏先后递增）。
    /// 比它大的才算新收藏；0 表示连历史收藏也要下。
    /// </summary>
    public long LastBookmark { get; init; }
    public string TargetLib { get; init; } = "";
    public string TargetFolder { get; init; } = "";
    /// <summary>上次检查的结果文案（界面直接显示）。</summary>
    public string LastResult { get; init; } = "";
    /// <summary>累计自动下载的作品件数。</summary>
    public int Downloaded { get; init; }
    public string AddTime { get; init; } = "";

    /// <summary>界面上称呼这个账号用的名字：优先取账号表里的当前名字（改过名也跟着变），没有再用监控里存的。</summary>
    public string Display =>
        SiteAccounts.Find(AccountService.Pixiv, UserId)?.DisplayName is { Length: > 0 } name ? name
        : UserName.Length > 0 ? UserName : UserId;

    /// <summary>对应的 pixiv 账号还在不在（退出登录后监控行保留，重新登录同一账号即可接着用）。</summary>
    public bool HasAccount => SiteAccounts.Find(AccountService.Pixiv, UserId) != null;
}

/// <summary>
/// Pixiv 收藏监控：盯住某个 pixiv 账号的收藏（公开 + 非公开），按间隔轮询，
/// 发现新收藏就自动走 <see cref="PixivService.EnqueueByIdsAsync"/> 入队下载——与 <see cref="FanboxWatchService"/> 同构。
///
/// 判"新"看收藏号（bookmarkData.id）而不是作品的投稿日：收藏号随收藏先后递增，
/// 今天收藏的十年前的旧作也算新收藏；也不看"本地有没有这件"，免得把用户主动删掉的作品一次次下回来。
///
/// 获取历史收藏（开启监控时选「获取历史收藏」，水位线置 0）动辄上千件，所以入队一律走慢速批量模式：
/// 两件作品之间歇 <see cref="AppConfig.PixivWatchPace"/> 秒，429 时冷却 60 秒再试；
/// 按收藏先后从旧到新、每 <see cref="ChunkSize"/> 件一批，每批之后把水位线推到这批处理到的那一件——
/// 中途限流/断网/退出程序都不丢进度，下一轮从停下的地方接着来，已入队的那部分也能先开始下载。
///
/// 进程级静态、单实例串行，与 fanbox 监控各用各的闸：pixiv 的历史收藏一跑就是几个小时，不能把 fanbox 的检查也堵住。
/// </summary>
public static class PixivWatchService
{
    /// <summary>每批入队的件数：每批结束都推一次水位线、启动一次下载。</summary>
    private const int ChunkSize = 20;

    /// <summary>收藏列表每页条数（同站点收藏页）。</summary>
    private const int ListPageSize = PixivApi.BookmarkPageSize;

    /// <summary>翻页上限（每份 48 × 500 = 两万四千件，足够覆盖任何账号）。</summary>
    private const int MaxPages = 500;

    /// <summary>收藏列表翻页之间的间隔：翻历史收藏要连着请求上百页，同样不能连打。</summary>
    private static readonly TimeSpan ListPace = TimeSpan.FromSeconds(1);

    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);

    private static readonly object Sync = new();
    private static CancellationTokenSource? _cts;
    private static bool _busy;
    private static string _status = "";

    private const string Columns =
        "\"user_id\", \"user_name\", \"interval_min\", \"enabled\", \"last_check\", \"last_bookmark\", " +
        "\"target_lib\", \"target_folder\", \"last_result\", \"downloaded\", \"add_time\"";

    // ---------- 查询 ----------

    public static List<PixivWatch> All()
    {
        var rows = Db.Select($"SELECT {Columns} FROM \"pixiv_watch\" ORDER BY \"add_time\" ASC");
        return (rows ?? []).Select(Map).ToList();
    }

    public static PixivWatch? Get(string userId)
    {
        var rows = Db.Select($"SELECT {Columns} FROM \"pixiv_watch\" WHERE \"user_id\" = @u", ("@u", userId));
        return rows is { Count: > 0 } ? Map(rows[0]) : null;
    }

    public static (bool Busy, string Status) State()
    {
        lock (Sync)
            return (_busy, _status);
    }

    /// <summary>不在检查时的常驻提示；两端共用。</summary>
    public static string IdleSummary()
    {
        var all = All();
        if (all.Count == 0)
            return "";
        var live = all.Count(w => w.Enabled);
        var text = $"监控 {all.Count} 个账号的收藏" + (live < all.Count ? $"（{all.Count - live} 个已暂停）" : "");
        if (!AppConfig.PixivWatchEnabled)
            return text + "　自动轮询已关闭";
        var next = all.Where(w => w.Enabled).Select(DueAt).DefaultIfEmpty().Min();
        return next == default ? text : text + "　下次检查 " + NextText(next);
    }

    public static string NextCheckText(PixivWatch w)
    {
        if (!w.Enabled)
            return "";
        if (!AppConfig.PixivWatchEnabled)
            return "自动轮询已关闭";
        return NextText(DueAt(w));
    }

    private static string NextText(DateTime at) =>
        at <= DateTime.Now ? "即将检查" : at.ToString("MM-dd HH:mm");

    // ---------- 增删改 ----------

    /// <summary>
    /// 为某个 pixiv 账号开启收藏监控（已有则覆盖设置）。
    ///
    /// includeHistory=false 时把水位线放到该账号现有的最新一次收藏，于是只有"今后新收藏的"才会被自动下载；
    /// 要先问一次站点，问不到就不建——宁可让用户重试，也好过悄悄建成"下全部历史收藏"。
    /// includeHistory=true 时水位线置 0，首轮起把历史收藏按从旧到新分批慢速入队。
    /// </summary>
    public static async Task<(bool Ok, string Message)> AddAsync(
        string userId, int intervalMin, bool includeHistory, string? lib, string? folder)
    {
        var account = SiteAccounts.Find(AccountService.Pixiv, userId);
        if (account == null)
            return (false, "找不到这个 Pixiv 账号，请先在「账号访问」登录");
        SiteAccounts.Use(AccountService.Pixiv, userId);

        var baseline = 0L;
        if (!includeHistory)
        {
            // 每份各取一条：站点按收藏先后倒序返回，第一条就是最新的一次收藏（占位条目也带收藏号）
            foreach (var rest in new[] { "show", "hide" })
            {
                var slice = await PixivApi.GetBookmarkSliceAsync(userId, rest, 0, 1);
                if (slice.Error != null)
                    return (false, "获取该账号的收藏失败：" + slice.Error);
                baseline = Math.Max(baseline, slice.Oldest);
            }
            // 一件收藏都没有时水位线就是 0——今后的任何收藏都比它新，正是想要的
        }

        var interval = NormalizeInterval(intervalMin);
        var existing = Get(userId);
        Db.Execute(
            $"INSERT INTO \"pixiv_watch\" ({Columns}) VALUES " +
            "(@u, @n, @iv, '1', '', @lb, @lib, @folder, '', @dl, @time) " +
            "ON CONFLICT(\"user_id\") DO UPDATE SET " +
            "\"user_name\" = excluded.\"user_name\", \"interval_min\" = excluded.\"interval_min\", " +
            "\"enabled\" = '1', \"last_bookmark\" = excluded.\"last_bookmark\", " +
            "\"target_lib\" = excluded.\"target_lib\", \"target_folder\" = excluded.\"target_folder\"",
            ("@u", userId), ("@n", account.DisplayName), ("@iv", interval),
            ("@lb", baseline.ToString(CultureInfo.InvariantCulture)),
            ("@lib", lib ?? ""), ("@folder", folder ?? ""),
            ("@dl", existing?.Downloaded ?? 0), ("@time", Now()));

        Logger.Info($"pixiv 收藏监控已开启：{account.DisplayName}" +
                    $"（每 {interval} 分钟检查一次{(includeHistory ? "，含历史收藏" : "")}）");
        Start();
        Kick();
        return (true, includeHistory
            ? $"已开启监控，正在把历史收藏分批加入下载队列（每件间隔 {AppConfig.PixivWatchPace} 秒，数量多时要跑很久）"
            : "已开启监控，今后新收藏的作品会自动下载");
    }

    public static void Remove(string userId)
    {
        Db.Execute("DELETE FROM \"pixiv_watch\" WHERE \"user_id\" = @u", ("@u", userId));
        Logger.Info($"pixiv 收藏监控已移除：{userId}");
    }

    public static void SetEnabled(string userId, bool enabled)
    {
        Db.Execute("UPDATE \"pixiv_watch\" SET \"enabled\" = @e WHERE \"user_id\" = @u",
            ("@e", enabled ? "1" : "0"), ("@u", userId));
        if (enabled)
            Kick();
    }

    public static void SetInterval(string userId, int intervalMin)
    {
        Db.Execute("UPDATE \"pixiv_watch\" SET \"interval_min\" = @iv WHERE \"user_id\" = @u",
            ("@iv", NormalizeInterval(intervalMin)), ("@u", userId));
        Kick();
    }

    // ---------- 后台轮询 ----------

    public static void Start()
    {
        CancellationToken token;
        lock (Sync)
        {
            if (_cts != null)
                return;
            _cts = new CancellationTokenSource();
            token = _cts.Token;
        }
        _ = LoopAsync(token);
    }

    public static void Stop()
    {
        lock (Sync)
        {
            _cts?.Cancel();
            _cts = null;
        }
    }

    /// <summary>立刻扫一遍到点的监控；正在检查时是空操作。</summary>
    public static void Kick()
    {
        if (!AppConfig.PixivWatchEnabled)
            return;
        _ = RunExclusiveAsync(SweepDueAsync, LoopToken());
    }

    /// <summary>手动「立即检查」某个账号（不受总开关与到点与否影响）。</summary>
    public static Task CheckNowAsync(string userId) =>
        RunExclusiveAsync(async ct =>
        {
            if (Get(userId) is { } w)
                await CheckOneAsync(w, ct);
        }, LoopToken());

    /// <summary>手动「立即检查全部」（含已暂停的条目）。</summary>
    public static Task CheckAllAsync() =>
        RunExclusiveAsync(async ct =>
        {
            foreach (var w in All())
            {
                ct.ThrowIfCancellationRequested();
                await CheckOneAsync(w, ct);
            }
        }, LoopToken());

    /// <summary>
    /// 手动触发的检查也挂在轮询线程的取消令牌上：历史收藏一跑几个小时，退出程序时要能停在两件作品之间，
    /// 而不是在 60 秒的限流冷却里干等。
    /// </summary>
    private static CancellationToken LoopToken()
    {
        lock (Sync)
            return _cts?.Token ?? CancellationToken.None;
    }

    private static async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(StartupDelay, ct);
            while (!ct.IsCancellationRequested)
            {
                if (AppConfig.PixivWatchEnabled)
                    await RunExclusiveAsync(SweepDueAsync, ct);
                await Task.Delay(Tick, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // 退出程序
        }
        catch (Exception e)
        {
            Logger.Error(e, "pixiv 收藏监控轮询线程");
        }
    }

    private static async Task SweepDueAsync(CancellationToken ct)
    {
        foreach (var w in All().Where(w => w.Enabled && DueAt(w) <= DateTime.Now))
        {
            ct.ThrowIfCancellationRequested();
            await CheckOneAsync(w, ct);
        }
    }

    private static async Task RunExclusiveAsync(Func<CancellationToken, Task> body, CancellationToken ct)
    {
        lock (Sync)
        {
            if (_busy)
                return;
            _busy = true;
            _status = "正在准备…";
        }
        try
        {
            await body(ct);
        }
        catch (OperationCanceledException)
        {
            // 正常取消
        }
        catch (Exception e)
        {
            Logger.Error(e, "pixiv 收藏监控检查");
        }
        finally
        {
            lock (Sync)
            {
                _busy = false;
                _status = "";
            }
        }
    }

    /// <summary>检查一个账号：取新收藏 → 从旧到新分批慢速入队 → 每批之后推进水位线。</summary>
    private static async Task CheckOneAsync(PixivWatch w, CancellationToken ct)
    {
        var name = w.Display;
        var now = Now();
        if (!w.HasAccount)
        {
            Save(w.UserId, lastCheck: now, lastResult: "该账号已退出登录（在「账号访问」重新登录同一账号即可继续）");
            return;
        }
        // 没设下载缓存目录时连收藏列表都不必翻：翻完了也入不了队
        if (string.IsNullOrEmpty(AppConfig.DownloadPath))
        {
            Save(w.UserId, lastCheck: now, lastResult: "入队失败：尚未设置下载缓存目录（请在系统设置中填写）");
            return;
        }
        // 收藏（尤其非公开那份）只对本人可见，必须用这个账号自己的 cookie；
        // 本方法是 async，上下文只作用到它返回为止，也会随 Task.Run 带进入队线程
        SiteAccounts.Use(AccountService.Pixiv, w.UserId);

        SetStatus($"正在检查 {name} 的收藏…");
        var (found, error) = await CollectNewAsync(w, name, ct);
        if (found is null)
        {
            // 站点没响应：水位线不动，下一轮重新发现这批新收藏
            Save(w.UserId, lastCheck: now, lastResult: "检查失败：" + (error ?? "站点无响应"));
            return;
        }
        if (found.Count == 0)
        {
            Save(w.UserId, lastCheck: now, lastResult: "无新收藏");
            return;
        }

        var pace = TimeSpan.FromSeconds(AppConfig.PixivWatchPace);
        var folder = w.TargetFolder.Length > 0 ? w.TargetFolder : null;
        var lib = w.TargetLib.Length > 0 ? w.TargetLib : null;
        int done = 0, artworks = 0, files = 0, skipped = 0;
        string? stopped = null;
        Save(w.UserId, lastCheck: now);

        for (var start = 0; start < found.Count; start += ChunkSize)
        {
            // 批与批之间看一眼：监控被删掉或被暂停（且这一轮不是对暂停监控的手动检查）就收手，已入队的不受影响
            var current = Get(w.UserId);
            if (current is null || (w.Enabled && !current.Enabled))
            {
                stopped = "监控已暂停或移除";
                break;
            }

            var chunk = found.Skip(start).Take(ChunkSize).ToList();
            var offset = done;
            var result = await PixivService.EnqueueByIdsAsync(
                chunk.Select(a => a.Id).ToList(), folder, lib,
                (n, _, title) => SetStatus(
                    $"{name}：新收藏入队 {offset + n} / {found.Count}" +
                    (title.StartsWith("请求过于频繁", StringComparison.Ordinal) ? "（" + title + "）" : "…")),
                pace, ct);

            // 水位线只推到这批按顺序处理完的最后一件：停在中间时，没轮到的那几件下一轮还会被发现
            if (result.Processed > 0)
                Save(w.UserId,
                    lastBookmark: chunk[result.Processed - 1].BookmarkId,
                    addDownloaded: result.ArtworkCount);
            done += result.Processed;
            artworks += result.ArtworkCount;
            files += result.FileCount;
            skipped += result.Skipped;
            if (result.Aborted != null)
            {
                stopped = result.Aborted;
                break;
            }
        }

        var summary = artworks > 0
            ? $"新增 {artworks} 件 / {files} 个文件"
            : done > 0 ? "新收藏都已在库或队列中" : "未入队";
        if (skipped > 0 && artworks > 0)
            summary += $"，跳过 {skipped} 件";
        if (done - artworks - skipped is var failed and > 0)
            summary += $"，{failed} 件下不了（已删除或无权查看）";
        if (stopped != null && done < found.Count)
            summary += $"；{stopped}，剩余 {found.Count - done} 件下一轮继续";
        Save(w.UserId, lastCheck: Now(), lastResult: summary);
        if (artworks > 0)
            Logger.Info($"pixiv 收藏监控 {name}：自动下载 {artworks} 件 / {files} 个文件");
    }

    /// <summary>
    /// 取比水位线更新的收藏（公开、非公开两份各自往下翻），按收藏先后从旧到新排好。
    /// 站点按收藏先后倒序返回，某一页里最早的那次收藏不比水位线新就不必再往下翻。
    /// 任何一页取失败都整体作废（返回 null）——只翻了一半就推进水位线，会把没看到的收藏永久跳过。
    /// </summary>
    private static async Task<(List<PixivArtwork>? Items, string? Error)> CollectNewAsync(
        PixivWatch w, string name, CancellationToken ct)
    {
        var found = new Dictionary<string, PixivArtwork>();
        var read = 0;
        foreach (var rest in new[] { "show", "hide" })
        {
            var offset = 0;
            for (var page = 0; page < MaxPages; page++)
            {
                if (page > 0 || rest == "hide")
                    await Task.Delay(ListPace, ct);
                var slice = await PixivApi.GetBookmarkSliceAsync(w.UserId, rest, offset, ListPageSize);
                if (slice.Error != null)
                    return (null, slice.Error);
                if (slice.Raw == 0)
                    break;
                // 有条目却一个收藏号都没有：站点改了数据形状，此时判不了新旧，宁可报错也别每轮把全部收藏翻一遍
                if (slice.Oldest == 0)
                    return (null, "站点返回的收藏数据里没有收藏号，无法判断新旧");
                foreach (var a in slice.Items.Where(a => a.BookmarkId > w.LastBookmark))
                    found[a.Id] = a;
                read += slice.Raw;
                offset += slice.Raw;
                if (w.LastBookmark > 0)
                    SetStatus($"正在检查 {name} 的收藏…");
                else
                    SetStatus($"正在读取 {name} 的历史收藏（已读 {read} 件）…");
                if (slice.Oldest <= w.LastBookmark || slice.Raw < ListPageSize)
                    break;
            }
        }
        return (found.Values.OrderBy(a => a.BookmarkId).ToList(), null);
    }

    // ---------- 小工具 ----------

    private static DateTime DueAt(PixivWatch w)
    {
        if (w.LastCheck.Length == 0 ||
            !DateTime.TryParse(w.LastCheck, CultureInfo.InvariantCulture, DateTimeStyles.None, out var last))
            return DateTime.MinValue;
        return last.AddMinutes(w.IntervalMin);
    }

    private static int NormalizeInterval(int minutes) =>
        minutes <= 0
            ? AppConfig.PixivWatchInterval
            : Math.Clamp(minutes, FanboxWatchService.MinIntervalMin, FanboxWatchService.MaxIntervalMin);

    private static void Save(string userId, string? lastCheck = null, long? lastBookmark = null,
        string? lastResult = null, int addDownloaded = 0)
    {
        var sets = new List<string>();
        var args = new List<(string, object?)>();
        if (lastCheck != null)
        {
            sets.Add("\"last_check\" = @lc");
            args.Add(("@lc", lastCheck));
        }
        if (lastBookmark is { } lb)
        {
            sets.Add("\"last_bookmark\" = @lb");
            args.Add(("@lb", lb.ToString(CultureInfo.InvariantCulture)));
        }
        if (lastResult != null)
        {
            sets.Add("\"last_result\" = @lr");
            args.Add(("@lr", lastResult));
        }
        if (addDownloaded > 0)
        {
            sets.Add("\"downloaded\" = COALESCE(\"downloaded\", 0) + @add");
            args.Add(("@add", addDownloaded));
        }
        if (sets.Count == 0)
            return;
        args.Add(("@u", userId));
        Db.Execute($"UPDATE \"pixiv_watch\" SET {string.Join(", ", sets)} WHERE \"user_id\" = @u", args.ToArray());
    }

    private static void SetStatus(string text)
    {
        lock (Sync)
            _status = text;
    }

    private static string Now() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

    private static PixivWatch Map(object?[] r) => new()
    {
        UserId = r[0] as string ?? "",
        UserName = r[1] as string ?? "",
        IntervalMin = r[2] is null
            ? 60
            : Math.Clamp(Convert.ToInt32(r[2]), FanboxWatchService.MinIntervalMin, FanboxWatchService.MaxIntervalMin),
        Enabled = (r[3] as string ?? "1") != "0",
        LastCheck = r[4] as string ?? "",
        LastBookmark = long.TryParse(r[5] as string, out var lb) ? lb : 0,
        TargetLib = r[6] as string ?? "",
        TargetFolder = r[7] as string ?? "",
        LastResult = r[8] as string ?? "",
        Downloaded = r[9] is null ? 0 : Convert.ToInt32(r[9]),
        AddTime = r[10] as string ?? "",
    };
}
