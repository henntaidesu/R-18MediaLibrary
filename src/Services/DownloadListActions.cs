using System;
using System.Collections.Generic;
using System.Linq;
using R18MediaLibrary.Core;

namespace R18MediaLibrary.Services;

/// <summary>
/// 下载列表工具栏的批量动作（清除无可用连接 / 清空列表 / 全部重新解析）、已完成作品的自动移出与按钮可用性判定。
/// 桌面端 <see cref="Views.DownloadPage"/> 与 Web 的 /api/clearnolink|clearall|reparseall 共用这一份，
/// 两端按钮按同一规则灰显、点下去做同一件事（UI 以 Web 为基准，见 CLAUDE.md「UI 对齐基准」）。
/// </summary>
public static class DownloadListActions
{
    /// <summary>工具栏按钮的可用性：由整张下载表的 (status, error) 算出。</summary>
    /// <param name="HasRows">列表非空 → 可「清空列表」。</param>
    /// <param name="HasNoLink">有「无可用下载连接」占位 → 可「清除无可用连接」。</param>
    /// <param name="CanReparse">有解析失败或无可用连接的行 → 可「全部重新解析」。</param>
    public readonly record struct Flags(bool HasRows, bool HasNoLink, bool CanReparse);

    /// <summary>按下载表里出现过的 (status, error) 算按钮可用性（调用方已读出整表时直接复用，不再查库）。</summary>
    public static Flags FlagsOf(IEnumerable<(string Status, string? Error)> items)
    {
        bool any = false, noLink = false, failed = false;
        foreach (var (status, _) in items)
        {
            any = true;
            if (status == "6") noLink = true;
            // 「源站无此文件」('2' + skipped) 也算可重新解析，源站日后补档时用户还能手动重试
            else if (status == "2") failed = true;
        }
        return new Flags(any, noLink, failed || noLink);
    }

    private static long _lastSweep;

    /// <summary>
    /// 已完成的作品自动移出下载列表（取代原「清除已完成」按钮）：作品已入媒体库（works.state = 已品悦），
    /// 且它的每条下载行都已终结（完成 '1'，或源站无此文件被跳过）、也不在解压/入库收尾中，就删掉这些下载行——
    /// 作品记录本身不动，之后在「已下载」里显示为「下载完成」。
    /// 只认已品悦：入库失败（移动媒体库出错，停在已下载）的作品要靠下载行在下次启动时重试，不能清。
    /// 两端的下载列表每秒刷新时调用；内部限流 3 秒一次，免得每次轮询都扫表。
    /// </summary>
    public static void ClearFinished()
    {
        var now = Environment.TickCount64;
        if (now - System.Threading.Interlocked.Read(ref _lastSweep) < 3000)
            return;
        System.Threading.Interlocked.Exchange(ref _lastSweep, now);
        var rows = Db.Select(
            "SELECT d.\"work_id\" FROM \"download_list\" d JOIN \"works\" w ON w.\"work_id\" = d.\"work_id\" " +
            "WHERE w.\"state\" = '已品悦' GROUP BY d.\"work_id\" " +
            "HAVING SUM(CASE WHEN d.\"status\" = '1' OR (d.\"status\" = '2' AND IFNULL(d.\"error\", '') = @sk) " +
            "THEN 0 ELSE 1 END) = 0",
            ("@sk", DownloadEngine.SkippedError));
        foreach (var row in rows ?? [])
        {
            var wid = row[0] as string ?? "";
            if (wid.Length == 0 || DownloadEngine.UnzipProgress.ContainsKey(wid))
                continue;   // 收尾线程还在跑（写正文/补元数据/回标），等它结束
            // 只删已终结的行：查询与删除之间若刚好又入队了新文件，新行留着
            Db.Execute(
                "DELETE FROM \"download_list\" WHERE \"work_id\" = @w AND " +
                "(\"status\" = '1' OR (\"status\" = '2' AND IFNULL(\"error\", '') = @sk))",
                ("@w", wid), ("@sk", DownloadEngine.SkippedError));
        }
    }

    /// <summary>清空列表：连带清掉那些只因排队而建的「下载中」作品记录（已入库的不动）。</summary>
    public static void ClearAll()
    {
        var rows = Db.Select("SELECT DISTINCT \"work_id\" FROM \"download_list\" WHERE \"status\" != '1'");
        foreach (var row in rows ?? [])
            ClearPlaceholderWork(row[0] as string ?? "");
        Db.Execute("DELETE FROM \"download_list\"");
    }

    /// <summary>清除「无可用下载连接」（占位状态 '6'）的作品：删占位行，并清掉因此残留的「下载中」作品记录。</summary>
    public static void ClearNoLink()
    {
        var rows = Db.Select("SELECT DISTINCT \"work_id\" FROM \"download_list\" WHERE \"status\" = '6'");
        Db.Execute("DELETE FROM \"download_list\" WHERE \"status\" = '6'");
        foreach (var row in rows ?? [])
        {
            var wid = row[0] as string ?? "";
            if (wid.Length == 0)
                continue;
            // 该作品已无任何下载行时，清除仅为占位而建的「下载中」作品记录（有真实下载/已入库的则保留）
            var remain = Db.Select("SELECT 1 FROM \"download_list\" WHERE \"work_id\" = @w", ("@w", wid));
            if (remain is not { Count: > 0 })
                ClearPlaceholderWork(wid);
        }
    }

    /// <summary>
    /// 全部重新解析：解析失败的分卷重新排队（经 debrid-link 再解析），
    /// 「无可用下载连接」占位行清掉 7 天空结果缓存后重新交给 AS 自动解析，最后启动下载。
    /// </summary>
    public static void ReparseAll()
    {
        Db.Execute("UPDATE \"download_list\" SET \"status\" = '0', \"error\" = NULL WHERE \"status\" = '2'");
        var rows = Db.Select("SELECT \"UUID\", \"work_id\" FROM \"download_list\" WHERE \"status\" = '6'");
        foreach (var r in rows ?? [])
        {
            var uuid = r[0] as string ?? "";
            var work = r[1] as string ?? "";
            if (uuid.Length == 0 || work.Length == 0)
                continue;
            AsScanCache.Clear(work);
            Db.Execute("UPDATE \"download_list\" SET \"status\" = '5' WHERE \"UUID\" = @u", ("@u", uuid));
            // 自动解析流水线（AS 搜索 + 组合下载 + 占位行存活判定）在 WebServer 里，两端共用同一入口
            WebServer.KickAutoResolve(work, uuid);
        }
        DownloadEngine.Start();
    }

    /// <summary>删除尚未下载完成时留下的占位作品行（已入库的不动，避免清下载列表把媒体库作品删没）。</summary>
    private static void ClearPlaceholderWork(string workId)
    {
        if (workId.Length == 0)
            return;
        Db.Execute("DELETE FROM \"works\" WHERE \"work_id\" = @w AND \"state\" = '下载中'", ("@w", workId));
    }
}
