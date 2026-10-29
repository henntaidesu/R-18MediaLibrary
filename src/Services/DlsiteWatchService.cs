using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using R18MediaLibrary.Core;

namespace R18MediaLibrary.Services;

/// <summary>一条 DLsite 社团监控记录（dlsite_watch 表的一行）。</summary>
public class DlsiteWatch
{
    public string MakerId { get; init; } = "";
    public string MakerName { get; init; } = "";
    /// <summary>轮询间隔（分钟）。</summary>
    public int IntervalMin { get; init; } = 360;
    /// <summary>该条监控是否参与自动轮询（暂停的监控仍可手动「立即检查」）。</summary>
    public bool Enabled { get; init; } = true;
    /// <summary>上次检查时间（yyyy-MM-dd HH:mm:ss），空表示还没查过。</summary>
    public string LastCheck { get; init; } = "";
    public string TargetLib { get; init; } = "";
    public string TargetFolder { get; init; } = "";
    /// <summary>上次检查的结果文案（界面直接显示）。</summary>
    public string LastResult { get; init; } = "";
    /// <summary>累计自动入队的作品数。</summary>
    public int Downloaded { get; init; }
    public string AddTime { get; init; } = "";

    /// <summary>界面上称呼这个社团用的名字（没抓到名字时退回社团号）。</summary>
    public string Display => MakerName.Length > 0 ? MakerName : MakerId;
}

/// <summary>
/// DLsite 社团监控：盯住几个社团（RG 号），按各自的间隔重抓社团作品列表，
/// 出现没见过的作品号就去找下载源并自动入队——SOU 音声且优先 asmr.one 时走 asmr.one 直链，
/// 其余走 AS 论坛（与「自动下载」同一条流水线：<see cref="WebServer.StartAutoDownload"/>）。
///
/// 与 fanbox 作家监控（<see cref="FanboxWatchService"/>）同构，几点不同、不能改：
/// (1) 判"新"看的是「这个作品号见没见过」（dlsite_watch_works），不是发布时间——社团页没有可比的时间水位线。
///     添加监控时把现有作品全部记为 base，于是用户事后删掉的作品不会被下回来。
/// (2) 新作品刚上架时论坛多半还没有资源，所以没找到源的新作品留在 pending，之后每轮
///     （至少隔 <see cref="RetryGap"/>，手动检查不受限）再找一次，<see cref="PendingDays"/> 天仍无资源就作废。
///     「全部下载」带进来的已有作品（backfill）只找一次，找不到就记 nosource，不反复骚扰论坛。
/// (3) 作品列表任何一页没取到都整轮作废、不记任何作品——只记了半份，没看到的老作品下一轮会被当成新作品下回来。
/// (4) 入队前用 DL API 核对作品真属于这个社团（社团页上可能混着别家的推荐作品），不是就记 done 跳过。
/// </summary>
public static class DlsiteWatchService
{
    public const int MinIntervalMin = 10;
    public const int MaxIntervalMin = 7 * 24 * 60;

    /// <summary>翻页上限：社团页一页 30 件左右，200 页足够任何社团。</summary>
    private const int MaxPages = 200;

    /// <summary>没找到下载源的新作品，两次重找之间至少隔多久（手动「立即检查」不受限）。</summary>
    private static readonly TimeSpan RetryGap = TimeSpan.FromHours(3);

    /// <summary>新作品等资源的期限：过了还没有就放弃（state=expired）。</summary>
    private const int PendingDays = 30;

    /// <summary>两次 AS 论坛搜索之间的间隔，同搜索页社团网格的 AS 扫描（3 秒）。</summary>
    private static readonly TimeSpan AsGap = TimeSpan.FromSeconds(3);

    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);

    private const string StateBase = "base";
    private const string StatePending = "pending";
    private const string StateBackfill = "backfill";
    private const string StateQueued = "queued";
    private const string StateDone = "done";
    private const string StateNoSource = "nosource";
    private const string StateExpired = "expired";

    private static readonly object Sync = new();
    private static CancellationTokenSource? _cts;
    private static bool _busy;
    private static string _status = "";

    private const string Columns =
        "\"maker_id\", \"maker_name\", \"interval_min\", \"enabled\", \"last_check\", " +
        "\"target_lib\", \"target_folder\", \"last_result\", \"downloaded\", \"add_time\"";

    // ---------- 查询 ----------

    public static List<DlsiteWatch> All()
    {
        var rows = Db.Select(
            $"SELECT {Columns} FROM \"dlsite_watch\" ORDER BY \"maker_name\" COLLATE NOCASE ASC");
        return (rows ?? []).Select(Map).ToList();
    }

    public static DlsiteWatch? Get(string makerId)
    {
        var rows = Db.Select(
            $"SELECT {Columns} FROM \"dlsite_watch\" WHERE \"maker_id\" = @m", ("@m", makerId));
        return rows is { Count: > 0 } ? Map(rows[0]) : null;
    }

    /// <summary>该社团是否已在监控中（搜索页社团网格的按钮据此切换文案）。</summary>
    public static bool IsWatched(string makerId) =>
        Convert.ToInt64(Db.Scalar(
            "SELECT COUNT(*) FROM \"dlsite_watch\" WHERE \"maker_id\" = @m", ("@m", makerId)) ?? 0L) > 0;

    /// <summary>还在等资源的新作品数（卡片上显示）。</summary>
    public static int PendingCount(string makerId) =>
        Convert.ToInt32(Db.Scalar(
            "SELECT COUNT(*) FROM \"dlsite_watch_works\" WHERE \"maker_id\" = @m AND \"state\" IN (@p, @b)",
            ("@m", makerId), ("@p", StatePending), ("@b", StateBackfill)) ?? 0L);

    public static (bool Busy, string Status) State()
    {
        lock (Sync)
            return (_busy, _status);
    }

    /// <summary>不在检查时的常驻提示；两端共用这一句。</summary>
    public static string IdleSummary()
    {
        var all = All();
        if (all.Count == 0)
            return "";
        var live = all.Count(w => w.Enabled);
        var text = $"监控 {all.Count} 个社团" + (live < all.Count ? $"（{all.Count - live} 个已暂停）" : "");
        if (!AppConfig.DlsiteWatchEnabled)
            return text + "　自动轮询已关闭";
        var next = all.Where(w => w.Enabled).Select(DueAt).DefaultIfEmpty().Min();
        return next == default ? text : text + "　下次检查 " + NextText(next);
    }

    public static string NextCheckText(DlsiteWatch w)
    {
        if (!w.Enabled)
            return "";
        if (!AppConfig.DlsiteWatchEnabled)
            return "自动轮询已关闭";
        return NextText(DueAt(w));
    }

    private static string NextText(DateTime at) =>
        at <= DateTime.Now ? "即将检查" : at.ToString("MM-dd HH:mm");

    // ---------- 增删改 ----------

    /// <summary>
    /// 添加（或覆盖）一条社团监控。先把社团现有的作品列表整份抓下来：
    /// includeExisting=false 时全部记为 base（只管今后的新作品），true 时记为 backfill（首轮各找一次源）。
    /// 列表抓不全就不建——宁可让用户重试，也好过把没抓到的老作品当成新作品。
    /// </summary>
    public static async Task<(bool Ok, string Message)> AddAsync(
        string makerId, int intervalMin, bool includeExisting, string? lib, string? folder)
    {
        makerId = makerId.Trim().ToUpperInvariant();
        if (!System.Text.RegularExpressions.Regex.IsMatch(makerId, @"^RG\d+$"))
            return (false, "社团号无效");

        var works = await ListAllAsync(makerId, CancellationToken.None);
        if (works is null)
            return (false, "获取该社团的作品列表失败，请稍后重试");

        // 社团名：社团页的作品列表里没有，取第一件作品的 DL API 数据
        var name = Get(makerId)?.MakerName ?? "";
        foreach (var id in works.Take(3))
        {
            if (name.Length > 0)
                break;
            var data = await DlsiteApi.GetWorkDataAsync(id);
            if (data != null && string.Equals(data.MakerId, makerId, StringComparison.OrdinalIgnoreCase))
                name = data.MakerName;
        }

        var interval = NormalizeInterval(intervalMin);
        var existing = Get(makerId);
        var now = Now();
        Db.Execute(
            $"INSERT INTO \"dlsite_watch\" ({Columns}) VALUES " +
            "(@m, @n, @iv, '1', '', @lib, @folder, '', @dl, @time) " +
            "ON CONFLICT(\"maker_id\") DO UPDATE SET " +
            "\"maker_name\" = excluded.\"maker_name\", \"interval_min\" = excluded.\"interval_min\", " +
            "\"enabled\" = '1', \"target_lib\" = excluded.\"target_lib\", " +
            "\"target_folder\" = excluded.\"target_folder\"",
            ("@m", makerId), ("@n", name), ("@iv", interval),
            ("@lib", lib ?? ""), ("@folder", folder ?? ""),
            ("@dl", existing?.Downloaded ?? 0), ("@time", now));

        // 重新添加即重新定基线：以前记过的作品状态一并作废
        Db.Execute("DELETE FROM \"dlsite_watch_works\" WHERE \"maker_id\" = @m", ("@m", makerId));
        var state = includeExisting ? StateBackfill : StateBase;
        foreach (var id in works)
            InsertWork(makerId, id, state, now);

        Logger.Info($"DLsite 社团监控已添加：{(name.Length > 0 ? name : makerId)}" +
            $"（现有 {works.Count} 件作品，每 {interval} 分钟检查一次{(includeExisting ? "，含现有作品" : "")}）");
        _ = DlsiteMakerIcon.EnsureAsync(makerId);   // 被监控的社团未必有作品入库，头像要单独查
        Start();
        Kick();
        return (true, includeExisting
            ? $"已添加监控，正在为该社团现有的 {works.Count} 件作品查找下载源"
            : "已添加监控，今后发布的新作品会自动下载");
    }

    public static void Remove(string makerId)
    {
        Db.Execute("DELETE FROM \"dlsite_watch\" WHERE \"maker_id\" = @m", ("@m", makerId));
        Db.Execute("DELETE FROM \"dlsite_watch_works\" WHERE \"maker_id\" = @m", ("@m", makerId));
        Logger.Info($"DLsite 社团监控已移除：{makerId}");
    }

    public static void SetEnabled(string makerId, bool enabled)
    {
        Db.Execute("UPDATE \"dlsite_watch\" SET \"enabled\" = @e WHERE \"maker_id\" = @m",
            ("@e", enabled ? "1" : "0"), ("@m", makerId));
        if (enabled)
            Kick();
    }

    public static void SetInterval(string makerId, int intervalMin)
    {
        Db.Execute("UPDATE \"dlsite_watch\" SET \"interval_min\" = @iv WHERE \"maker_id\" = @m",
            ("@iv", NormalizeInterval(intervalMin)), ("@m", makerId));
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

    public static void Kick()
    {
        if (!AppConfig.DlsiteWatchEnabled)
            return;
        _ = RunExclusiveAsync(ct => SweepDueAsync(ct), LoopToken());
    }

    /// <summary>手动「立即检查」某个社团（不受总开关、到点与否、重找间隔影响）。</summary>
    public static Task CheckNowAsync(string makerId) =>
        RunExclusiveAsync(async ct =>
        {
            if (Get(makerId) is { } w)
                await CheckOneAsync(w, force: true, ct);
        }, LoopToken());

    public static Task CheckAllAsync() =>
        RunExclusiveAsync(async ct =>
        {
            foreach (var w in All())
            {
                ct.ThrowIfCancellationRequested();
                await CheckOneAsync(w, force: true, ct);
            }
        }, LoopToken());

    /// <summary>手动检查也挂轮询线程的取消令牌：一轮可能要搜几十个作品，退出程序时要能停下。</summary>
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
                if (AppConfig.DlsiteWatchEnabled)
                    await RunExclusiveAsync(SweepDueAsync, ct);
                await Task.Delay(Tick, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // 退出程序 / 关闭轮询
        }
        catch (Exception e)
        {
            Logger.Error(e, "DLsite 社团监控轮询线程");
        }
    }

    private static async Task SweepDueAsync(CancellationToken ct)
    {
        foreach (var w in All().Where(w => w.Enabled && DueAt(w) <= DateTime.Now))
        {
            ct.ThrowIfCancellationRequested();
            await CheckOneAsync(w, force: false, ct);
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
            Logger.Error(e, "DLsite 社团监控检查");
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

    /// <summary>检查一个社团：重抓作品列表 → 记下新作品 → 为待处理的作品找源入队。</summary>
    private static async Task CheckOneAsync(DlsiteWatch w, bool force, CancellationToken ct)
    {
        SetStatus($"正在检查 {w.Display}…");
        var now = Now();

        var listed = await ListAllAsync(w.MakerId, ct);
        if (listed is null)
        {
            Save(w.MakerId, lastCheck: now, lastResult: "检查失败（DLsite 无响应）");
            return;
        }

        var known = KnownWorks(w.MakerId);
        var fresh = listed.Where(id => !known.ContainsKey(id)).ToList();
        foreach (var id in fresh)
            InsertWork(w.MakerId, id, StatePending, now);
        if (fresh.Count > 0)
            Logger.Info($"DLsite 社团监控 {w.Display}：发现 {fresh.Count} 件新作品（{string.Join(", ", fresh)}）");

        // 待处理的作品：新作品（pending）+ 「全部下载」带进来的已有作品（backfill），按作品号从旧到新
        var todo = Db.Select(
            "SELECT \"work_id\", \"state\", \"first_seen\", \"last_try\" FROM \"dlsite_watch_works\" " +
            "WHERE \"maker_id\" = @m AND \"state\" IN (@p, @b) ORDER BY \"work_id\"",
            ("@m", w.MakerId), ("@p", StatePending), ("@b", StateBackfill)) ?? [];

        int queued = 0, waiting = 0, expired = 0;
        var searched = false;
        for (var i = 0; i < todo.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var id = todo[i][0] as string ?? "";
            var state = todo[i][1] as string ?? "";
            var firstSeen = ParseTime(todo[i][2] as string);
            var lastTry = ParseTime(todo[i][3] as string);

            if (state == StatePending && firstSeen != default && firstSeen.AddDays(PendingDays) < DateTime.Now)
            {
                SetWorkState(w.MakerId, id, StateExpired, null);
                expired++;
                continue;
            }
            if (!force && state == StatePending && lastTry != default && lastTry + RetryGap > DateTime.Now)
            {
                waiting++;
                continue;
            }

            if (searched)
                await Task.Delay(AsGap, ct);   // 每次真去问站点之前歇一下，同搜索页 AS 扫描的节奏
            SetStatus($"{w.Display}：正在为 {id} 查找下载源（{i + 1}/{todo.Count}）…");
            var (outcome, didSearch) = await TryEnqueueAsync(w, id, ct);
            searched = didSearch;
            switch (outcome)
            {
                case Outcome.Queued:
                    SetWorkState(w.MakerId, id, StateQueued, Now());
                    queued++;
                    break;
                case Outcome.Skip:
                    SetWorkState(w.MakerId, id, StateDone, Now());
                    break;
                case Outcome.NoSource when state == StateBackfill:
                    SetWorkState(w.MakerId, id, StateNoSource, Now());
                    break;
                default:
                    // 没找到源 / 站点出错：新作品留着下一轮再找（backfill 出错也留着）
                    SetWorkState(w.MakerId, id, state, Now());
                    if (state == StatePending)
                        waiting++;
                    break;
            }
        }

        var parts = new List<string>();
        parts.Add(fresh.Count > 0 ? $"新作品 {fresh.Count} 件" : "无新作品");
        if (queued > 0)
            parts.Add($"入队 {queued} 件");
        if (waiting > 0)
            parts.Add($"{waiting} 件等待资源");
        if (expired > 0)
            parts.Add($"{expired} 件超过 {PendingDays} 天无资源已放弃");
        Save(w.MakerId, lastCheck: now, lastResult: string.Join("，", parts), addDownloaded: queued);
        if (queued > 0)
            Logger.Info($"DLsite 社团监控 {w.Display}：自动入队 {queued} 件作品");
    }

    private enum Outcome { Queued, Skip, NoSource, Error }

    /// <summary>
    /// 为一件作品找下载源并入队。已在库 / 队列、被标不喜欢、不属于这个社团的直接 Skip；
    /// SOU 且优先 asmr.one 且账号已配置、asmr.one 也有这件 → asmr.one 直链；否则搜 AS 论坛，有帖子就交给自动下载流水线。
    /// 返回的 Searched 表示这次是否真的向论坛发了搜索（决定下一件之前要不要歇）。
    /// </summary>
    private static async Task<(Outcome, bool Searched)> TryEnqueueAsync(DlsiteWatch w, string id, CancellationToken ct)
    {
        if (InWorks(id) || Dislikes.Lookup([id]).Count > 0)
            return (Outcome.Skip, false);

        var data = await DlsiteApi.GetWorkDataAsync(id);
        ct.ThrowIfCancellationRequested();
        if (data != null && data.MakerId.Length > 0
            && !string.Equals(data.MakerId, w.MakerId, StringComparison.OrdinalIgnoreCase))
            return (Outcome.Skip, false);   // 社团页上混进来的别家作品

        var isSou = string.Equals(data?.WorkType, "SOU", StringComparison.OrdinalIgnoreCase);
        if (isSou && AppConfig.SouUsesAsmr
            && !string.IsNullOrEmpty(AppConfig.AsmrUsername) && !string.IsNullOrEmpty(AppConfig.AsmrPassword))
        {
            try
            {
                var detail = await AsmrApi.GetWorkDetailAsync(AsmrApi.RjToId(id));
                if (detail is { Files.Count: > 0 })
                {
                    var res = await AsmrService.EnqueueByRjAsync(id, data?.WorkName ?? "",
                        NullIfEmpty(w.TargetFolder), NullIfEmpty(w.TargetLib));
                    if (res.Ok)
                        return (Outcome.Queued, false);
                    Logger.Warning($"DLsite 社团监控 {id} asmr.one 入队失败：{res.Error}");
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Logger.Warning($"DLsite 社团监控 {id} 查询 asmr.one 失败：{e.Message}");
            }
            // asmr.one 没有 → 回退 AS 论坛
        }

        // 新作品等的就是论坛出资源，故不看 7 天「无结果」缓存，直接搜；搜完照常写回缓存
        List<AsSearchResult> posts;
        try
        {
            posts = await AnimeSharing.SearchWorkAsync(id);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            AsScanCache.Store(id, -1);
            Logger.Warning($"DLsite 社团监控 {id} 搜索 AS 论坛失败：{e.Message}");
            return (Outcome.Error, true);
        }
        AsScanCache.Store(id, posts.Count);
        if (posts.Count == 0)
            return (Outcome.NoSource, true);

        WebServer.StartAutoDownload(id, w.TargetLib, w.TargetFolder);
        return (Outcome.Queued, true);
    }

    /// <summary>
    /// 抓社团的全部作品号（按页面顺序去重）。任何一页没取到都返回 null，见类注释第 (3) 条。
    /// 全量翻页而不是"见到旧的就停"：不依赖社团页的排序，一轮也不过几次请求。
    /// </summary>
    private static async Task<List<string>?> ListAllAsync(string makerId, CancellationToken ct)
    {
        var ids = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var page = 1; page <= MaxPages; page++)
        {
            ct.ThrowIfCancellationRequested();
            var batch = await DlsiteApi.TryGetMakerWorksAsync(makerId, page);
            if (batch is not { } b)
                return null;
            if (b.Works.Count == 0)
                return page == 1 ? null : ids;   // 第一页就空：多半是社团号不对或页面改版，不能当"没作品"
            foreach (var work in b.Works)
                if (seen.Add(work.WorkId))
                    ids.Add(work.WorkId);
            if (!b.HasMore)
                break;
        }
        return ids;
    }

    // ---------- 小工具 ----------

    private static Dictionary<string, string> KnownWorks(string makerId)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in Db.Select(
                     "SELECT \"work_id\", \"state\" FROM \"dlsite_watch_works\" WHERE \"maker_id\" = @m",
                     ("@m", makerId)) ?? [])
            map[r[0] as string ?? ""] = r[1] as string ?? "";
        return map;
    }

    private static void InsertWork(string makerId, string workId, string state, string now) =>
        Db.Execute(
            "INSERT OR IGNORE INTO \"dlsite_watch_works\" (\"maker_id\", \"work_id\", \"state\", \"first_seen\", \"last_try\") " +
            "VALUES (@m, @w, @s, @t, '')",
            ("@m", makerId), ("@w", workId), ("@s", state), ("@t", now));

    private static void SetWorkState(string makerId, string workId, string state, string? lastTry)
    {
        if (lastTry == null)
            Db.Execute(
                "UPDATE \"dlsite_watch_works\" SET \"state\" = @s WHERE \"maker_id\" = @m AND \"work_id\" = @w",
                ("@s", state), ("@m", makerId), ("@w", workId));
        else
            Db.Execute(
                "UPDATE \"dlsite_watch_works\" SET \"state\" = @s, \"last_try\" = @t " +
                "WHERE \"maker_id\" = @m AND \"work_id\" = @w",
                ("@s", state), ("@t", lastTry), ("@m", makerId), ("@w", workId));
    }

    private static bool InWorks(string workId) =>
        Convert.ToInt64(Db.Scalar(
            "SELECT COUNT(*) FROM \"works\" WHERE \"work_id\" = @w", ("@w", workId)) ?? 0L) > 0;

    private static DateTime DueAt(DlsiteWatch w)
    {
        var last = ParseTime(w.LastCheck);
        return last == default ? DateTime.MinValue : last.AddMinutes(w.IntervalMin);
    }

    private static DateTime ParseTime(string? text) =>
        !string.IsNullOrEmpty(text) &&
        DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
            ? t
            : default;

    private static int NormalizeInterval(int minutes) =>
        minutes <= 0
            ? AppConfig.DlsiteWatchInterval
            : Math.Clamp(minutes, MinIntervalMin, MaxIntervalMin);

    private static string? NullIfEmpty(string s) => s.Length > 0 ? s : null;

    private static void Save(string makerId, string? lastCheck = null, string? lastResult = null, int addDownloaded = 0)
    {
        var sets = new List<string>();
        var args = new List<(string, object?)>();
        if (lastCheck != null)
        {
            sets.Add("\"last_check\" = @lc");
            args.Add(("@lc", lastCheck));
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
        args.Add(("@m", makerId));
        Db.Execute(
            $"UPDATE \"dlsite_watch\" SET {string.Join(", ", sets)} WHERE \"maker_id\" = @m",
            args.ToArray());
    }

    private static void SetStatus(string text)
    {
        lock (Sync)
            _status = text;
    }

    private static string Now() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

    private static DlsiteWatch Map(object?[] r) => new()
    {
        MakerId = r[0] as string ?? "",
        MakerName = r[1] as string ?? "",
        IntervalMin = r[2] is null ? 360 : Math.Clamp(Convert.ToInt32(r[2]), MinIntervalMin, MaxIntervalMin),
        Enabled = (r[3] as string ?? "1") != "0",
        LastCheck = r[4] as string ?? "",
        TargetLib = r[5] as string ?? "",
        TargetFolder = r[6] as string ?? "",
        LastResult = r[7] as string ?? "",
        Downloaded = r[8] is null ? 0 : Convert.ToInt32(r[8]),
        AddTime = r[9] as string ?? "",
    };
}
