using System;
using System.Collections.Generic;
using System.Linq;
using R18MediaLibrary.Core;

namespace R18MediaLibrary.Services;

/// <summary>
/// 下载列表工具栏的批量动作（清除已完成 / 清除无可用连接 / 清空列表 / 全部重新解析）与按钮可用性判定。
/// 桌面端 <see cref="Views.DownloadPage"/> 与 Web 的 /api/cleardone|clearnolink|clearall|reparseall 共用这一份，
/// 两端按钮按同一规则灰显、点下去做同一件事（UI 以 Web 为基准，见 CLAUDE.md「UI 对齐基准」）。
/// </summary>
public static class DownloadListActions
{
    /// <summary>工具栏按钮的可用性：由整张下载表的 (status, error) 算出。</summary>
    /// <param name="HasRows">列表非空 → 可「清空列表」。</param>
    /// <param name="HasDone">有已终结分卷（已完成，或源站无此文件而跳过）→ 可「清除已完成」。</param>
    /// <param name="HasNoLink">有「无可用下载连接」占位 → 可「清除无可用连接」。</param>
    /// <param name="CanReparse">有解析失败或无可用连接的行 → 可「全部重新解析」。</param>
    public readonly record struct Flags(bool HasRows, bool HasDone, bool HasNoLink, bool CanReparse);

    /// <summary>按下载表里出现过的 (status, error) 算按钮可用性（调用方已读出整表时直接复用，不再查库）。</summary>
    public static Flags FlagsOf(IEnumerable<(string Status, string? Error)> items)
    {
        bool any = false, done = false, noLink = false, failed = false;
        foreach (var (status, error) in items)
        {
            any = true;
            if (status == "1") done = true;
            else if (status == "6") noLink = true;
            else if (status == "2")
            {
                failed = true;
                // 「源站无此文件」('2' + skipped) 是已终结的跳过项，和已完成分卷一样归「清除已完成」管；
                // 仍算可重新解析，源站日后补档时用户还能手动重试
                if (DownloadEngine.IsSkipped(error)) done = true;
            }
        }
        return new Flags(any, done, noLink, failed || noLink);
    }

    /// <summary>
    /// 清除已完成：按"已终结"清——下载完成（'1'）与源站根本没有该文件而被跳过（'2' + skipped）
    /// 都不会再有任何变化。整篇都被跳过的作品（列表里显示「源站无文件」）因此同样被清掉，
    /// 否则它既不是 '1' 也不是 '6'，两个清除按钮都够不着，只能一条条手动删。
    /// 正在解压（待解压/解压中）的作品尚未真正结束，其分卷虽已终结也保留。
    /// </summary>
    public static void ClearDone()
    {
        var args = new List<(string, object?)> { ("@sk", DownloadEngine.SkippedError) };
        var where = "(\"status\" = '1' OR (\"status\" = '2' AND IFNULL(\"error\", '') = @sk))";
        var unzipping = DownloadEngine.UnzipProgress.Keys.ToList();
        if (unzipping.Count > 0)
        {
            var names = new List<string>();
            for (var i = 0; i < unzipping.Count; i++)
            {
                names.Add($"@u{i}");
                args.Add(($"@u{i}", unzipping[i]));
            }
            where += $" AND \"work_id\" NOT IN ({string.Join(",", names)})";
        }
        // 先记下受影响的番号：全部被跳过的作品清完会一行不剩，要连带清掉只为排队而建的「下载中」记录
        var affected = Db.Select(
            $"SELECT DISTINCT \"work_id\" FROM \"download_list\" WHERE {where}", args.ToArray());
        Db.Execute($"DELETE FROM \"download_list\" WHERE {where}", args.ToArray());
        foreach (var row in affected ?? [])
        {
            var wid = row[0] as string ?? "";
            if (wid.Length == 0)
                continue;
            var remain = Db.Select("SELECT 1 FROM \"download_list\" WHERE \"work_id\" = @w", ("@w", wid));
            if (remain is not { Count: > 0 })
                ClearPlaceholderWork(wid);
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
