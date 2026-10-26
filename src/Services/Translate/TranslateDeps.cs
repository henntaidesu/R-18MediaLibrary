using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using R18MediaLibrary.Core;

namespace R18MediaLibrary.Services.Translate;

/// <summary>下载进度快照（设置页按 Changed 事件取一份来画）。</summary>
public sealed record DepProgress(
    bool Running,
    string TierId,
    int FileIndex,        // 第几个文件（1 起）
    int FileCount,
    string CurrentName,   // 当前文件的清单相对路径
    long CurrentHave,
    long CurrentTotal,
    long TotalHave,
    long TotalTotal,
    double SpeedBps,
    string Message,       // 面向用户的一句话状态
    bool Failed);

/// <summary>
/// 图片翻译依赖（模型文件）的下载器。
///
/// 与 <see cref="DownloadEngine"/> 分开而不是复用它：那套是按作品组织的队列（download_list 以
/// work_id 为中心、完成后要入库/解压/回访站点），模型下载没有作品、不入库、只有一条线性清单，
/// 硬塞进去反而要给每个环节加特例。这里要的只是「顺序下几个大文件 + 断点续传 + 能取消」。
///
/// 连接超时刻意设为无限：几 GB 的模型在慢线路上可能下几个小时，固定超时只会在中途砍断。
/// 掉线的兜底是断点续传——.part 留在盘上，下次从断点继续，不会白下。
/// </summary>
public static class TranslateDeps
{
    private const int AttemptsPerFile = 3;
    private const int BufferSize = 1 << 20;   // 1MB：大文件用大缓冲，减少系统调用

    private static readonly object Sync = new();
    private static CancellationTokenSource? _cts;
    private static DepProgress _progress = Idle();

    /// <summary>进度变化广播（约每秒两次，外加每个阶段切换时一次）。UI 侧需自行切回调度线程。</summary>
    public static event Action? Changed;

    public static bool Running { get { lock (Sync) return _cts != null; } }

    public static DepProgress Progress { get { lock (Sync) return _progress; } }

    private static DepProgress Idle() =>
        new(false, "", 0, 0, "", 0, 0, 0, 0, 0, "", false);

    /// <summary>请求中止当前下载。已下的 .part 会保留，下次接着下。</summary>
    public static void Cancel()
    {
        lock (Sync)
            _cts?.Cancel();
    }

    /// <summary>
    /// 下载某档位缺失的模型文件。同一时刻只允许一个下载任务（重复调用直接返回 false）。
    /// 返回 true 表示该档位依赖已全部就绪。
    /// </summary>
    public static async Task<bool> StartAsync(string tierId)
    {
        CancellationTokenSource cts;
        lock (Sync)
        {
            if (_cts != null)
                return false;
            _cts = cts = new CancellationTokenSource();
        }

        try
        {
            return await RunAsync(tierId, cts.Token);
        }
        catch (OperationCanceledException)
        {
            Report(tierId, "已取消下载", failed: false);
            Logger.Info("图片翻译依赖下载已取消");
            return false;
        }
        catch (Exception e)
        {
            Report(tierId, $"下载失败：{e.Message}", failed: true);
            Logger.Error(e, "图片翻译依赖下载失败");
            return false;
        }
        finally
        {
            lock (Sync)
            {
                _cts?.Dispose();
                _cts = null;
                _progress = _progress with { Running = false };
            }
            Changed?.Invoke();
        }
    }

    private static async Task<bool> RunAsync(string tierId, CancellationToken token)
    {
        var tier = TranslateModels.Tier(tierId);
        var files = TranslateModels.FilesOf(tierId);
        if (files.Count == 0)
        {
            Report(tierId, "依赖清单为空，无法下载", failed: true);
            return false;
        }

        // 先算还差多少、盘够不够——几 GB 的东西下到一半提示磁盘满是最糟糕的体验
        long need = 0;
        foreach (var f in files)
            if (TranslateModels.StateOf(f) != ModelFileState.Ready)
                need += Math.Max(0, Math.Max(f.Size, 0) - TranslateModels.HaveBytesOf(f));

        if (need == 0)
        {
            Report(tierId, "依赖已齐备", failed: false);
            return true;
        }

        Directory.CreateDirectory(TranslateModels.Root);
        if (!HasFreeSpace(TranslateModels.Root, need, out var free))
        {
            Report(tierId,
                $"磁盘空间不足：还需约 {TranslateModels.FormatSize(need)}，" +
                $"{TranslateModels.Root} 所在盘只剩 {TranslateModels.FormatSize(free)}",
                failed: true);
            return false;
        }

        Logger.Info(
            $"开始下载图片翻译依赖（{tier.Name}）：共 {files.Count} 个文件，" +
            $"预计还需 {TranslateModels.FormatSize(need)} → {TranslateModels.Root}");

        // 模型托管在 HuggingFace 等站点上，国内多半要走代理，故复用全局代理设置的客户端。
        // 超时设为无限，理由见类注释。
        using var client = Http.CreateClient(Timeout.InfiniteTimeSpan);

        var status = TranslateModels.Status(tierId);
        var baseHave = status.HaveBytes;
        var grandTotal = status.TotalBytes;

        for (var i = 0; i < files.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var file = files[i];

            if (TranslateModels.StateOf(file) == ModelFileState.Ready)
                continue;

            Exception? last = null;
            var done = false;
            for (var attempt = 1; attempt <= AttemptsPerFile && !done; attempt++)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    await DownloadOneAsync(client, file, tierId, i + 1, files.Count,
                        baseHave, grandTotal, token);
                    done = true;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception e)
                {
                    last = e;
                    Logger.Warning($"模型文件下载失败（第 {attempt}/{AttemptsPerFile} 次）：{file.Path} — {e.Message}");
                    if (attempt < AttemptsPerFile)
                    {
                        Report(tierId, $"{file.Path} 下载失败，{5 * attempt} 秒后重试…", failed: false);
                        await Task.Delay(TimeSpan.FromSeconds(5 * attempt), token);
                    }
                }
            }

            if (!done)
            {
                Report(tierId, $"下载失败：{file.Path} — {last?.Message}", failed: true);
                Logger.Error($"图片翻译依赖下载中止于 {file.Path}（已重试 {AttemptsPerFile} 次）");
                return false;
            }

            // 已完成文件的字节数并进基数，下一个文件的进度才能接着往上走
            baseHave = TranslateModels.Status(tierId).HaveBytes;
        }

        var ready = TranslateModels.IsReady(tierId);
        Report(tierId, ready ? "依赖已齐备，可以启用图片翻译" : "下载结束，但仍有文件缺失", failed: !ready);
        Logger.Info($"图片翻译依赖下载完成（{tier.Name}）：{(ready ? "全部就绪" : "仍有缺失")}");
        return ready;
    }

    private static async Task DownloadOneAsync(
        HttpClient client, ModelFile file, string tierId, int index, int count,
        long baseHave, long grandTotal, CancellationToken token)
    {
        var target = TranslateModels.LocalPath(file);
        var part = TranslateModels.PartPath(file);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        var resumeFrom = File.Exists(part) ? new FileInfo(part).Length : 0;

        using var request = new HttpRequestMessage(HttpMethod.Get, file.Url);
        if (resumeFrom > 0)
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(resumeFrom, null);

        using var response = await client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, token);

        // 服务端不认 Range（回 200 而不是 206）就只能从头下，否则会把两段拼成坏文件
        if (resumeFrom > 0 && response.StatusCode != HttpStatusCode.PartialContent)
        {
            Logger.Info($"服务端不支持断点续传，从头下载：{file.Path}");
            resumeFrom = 0;
            try { File.Delete(part); } catch (IOException) { /* 下面 Create 会再报一次 */ }
        }
        response.EnsureSuccessStatusCode();

        var remote = response.Content.Headers.ContentLength ?? 0;
        var total = resumeFrom + remote;              // 本文件的真实总大小（以服务端为准）
        if (total <= 0) total = Math.Max(file.Size, 0);

        var sw = Stopwatch.StartNew();
        long written = resumeFrom;
        var lastTick = 0L;
        var lastBytes = written;

        await using (var input = await response.Content.ReadAsStreamAsync(token))
        await using (var output = new FileStream(
            part, resumeFrom > 0 ? FileMode.Append : FileMode.Create,
            FileAccess.Write, FileShare.None, BufferSize))
        {
            var buffer = new byte[BufferSize];
            int read;
            while ((read = await input.ReadAsync(buffer, token)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), token);
                written += read;

                // 进度广播限流到每 500ms 一次：几 GB 的文件逐块刷 UI 只会把界面拖垮
                var now = sw.ElapsedMilliseconds;
                if (now - lastTick < 500)
                    continue;
                var speed = (written - lastBytes) * 1000.0 / Math.Max(1, now - lastTick);
                lastTick = now;
                lastBytes = written;
                Publish(new DepProgress(
                    true, tierId, index, count, file.Path, written, total,
                    baseHave + (written - resumeFrom), grandTotal, speed,
                    $"正在下载 {file.Path}（{index}/{count}）", false));
            }
        }

        token.ThrowIfCancellationRequested();

        // 服务端给了长度就按长度收货：短了说明连接中途断了，保留 .part 交给下一次续传
        if (remote > 0 && written != total)
            throw new IOException(
                $"文件不完整：收到 {TranslateModels.FormatSize(written)}，应为 {TranslateModels.FormatSize(total)}");

        if (!string.IsNullOrWhiteSpace(file.Sha256))
        {
            Publish(_progress with { Message = $"正在校验 {file.Path}…" });
            var actual = await Sha256Async(part, token);
            if (!string.Equals(actual, file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(part); } catch (IOException) { /* 校验失败本就要重下 */ }
                throw new IOException($"校验不通过（sha256 {actual[..12]}… != {file.Sha256[..12]}…）");
            }
        }
        else
        {
            Logger.Info($"清单未提供 sha256，跳过内容校验：{file.Path}");
        }

        if (File.Exists(target))
            File.Delete(target);
        File.Move(part, target);
        TranslateModels.MarkInstalled(file, new FileInfo(target).Length);
        Logger.Info($"模型文件就绪：{file.Path}（{TranslateModels.FormatSize(written)}）");
    }

    private static async Task<string> Sha256Async(string path, CancellationToken token)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize);
        var hash = await SHA256.HashDataAsync(stream, token);
        return Convert.ToHexString(hash);
    }

    private static bool HasFreeSpace(string dir, long need, out long free)
    {
        free = 0;
        try
        {
            free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dir))!).AvailableFreeSpace;
            // 留 512MB 余量，别把盘刚好塞满
            return free > need + (512L << 20);
        }
        catch (Exception e)
        {
            // 网络盘/映射盘可能查不到剩余空间，查不到就不拦，交给下载时的写入错误
            Logger.Warning($"无法检查磁盘剩余空间（{dir}）：{e.Message}");
            return true;
        }
    }

    private static void Report(string tierId, string message, bool failed)
    {
        var status = TranslateModels.Status(tierId);
        Publish(new DepProgress(
            Running, tierId, 0, status.TotalFiles, "", 0, 0,
            status.HaveBytes, status.TotalBytes, 0, message, failed));
    }

    private static void Publish(DepProgress progress)
    {
        lock (Sync)
            _progress = progress;
        Changed?.Invoke();
    }
}
