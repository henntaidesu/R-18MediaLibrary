using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using R18MediaLibrary.Core;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace R18MediaLibrary.Services;

/// <summary>
/// 解压作品压缩包（对应 Python 版 unzip.py）：
/// 优先用 Bandizip bz.exe（处理分卷/SFX 最稳），未安装时回退 SharpCompress（支持 RAR5）。
/// 解压后拍平嵌套目录、修复 Shift_JIS 文件名乱码，最后移动到媒体库并入库。
/// </summary>
public static class UnzipService
{
    // Bandizip 命令行工具
    private const string BandizipBz = @"C:\Program Files\Bandizip\bz.exe";

    private static readonly string[] ArchiveExts = [".zip", ".rar", ".exe"];

    static UnzipService()
    {
        // cp437 / Shift_JIS 等代码页编码需要显式注册
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>目录下（递归）所有压缩文件路径。</summary>
    public static List<string> GetAllArchiveFiles(string folder)
    {
        var result = new List<string>();
        if (!Directory.Exists(folder))
            return result;
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            if (ArchiveExts.Contains(Path.GetExtension(file).ToLowerInvariant()))
                result.Add(file);
        return result;
    }

    /// <summary>目录下所有非压缩包文件的总字节数，作为解压产出量估算解压进度。</summary>
    public static long ExtractedSize(string folder)
    {
        long total = 0;
        if (!Directory.Exists(folder))
            return 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                if (ArchiveExts.Contains(Path.GetExtension(file).ToLowerInvariant()))
                    continue;
                try { total += new FileInfo(file).Length; } catch (IOException) { }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return total;
    }

    /// <summary>解压一个压缩包到指定目录，返回是否成功。加密包按「解压密码库」里的密码逐个试。</summary>
    private static bool ExtractArchive(string filePath, string extractPath)
    {
        try
        {
            return File.Exists(BandizipBz)
                ? ExtractWithBandizip(filePath, extractPath)
                : ExtractWithSharpCompress(filePath, extractPath);
        }
        catch (Exception e)
        {
            Logger.Error(e, $"解压 {filePath}");
            return false;
        }
    }

    private static bool ExtractWithBandizip(string filePath, string extractPath)
    {
        // bz 返回非 0 多为输出文件被杀软/索引器临时占用（0x20 共享冲突），
        // -aoa 会覆盖已解出的部分，因此可安全重试，等占用释放后再来。
        // 但 .exe 多为作品自带可执行文件（仅极少数是自解压包），失败通常是"根本不是压缩包"
        // 而非占用冲突，没必要长等重试——单次尝试即可，让上层快速判定为普通文件跳过。
        var maxAttempts = Path.GetExtension(filePath).Equals(".exe", StringComparison.OrdinalIgnoreCase) ? 1 : 3;
        var lastCode = 0;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var (ok, exitCode, needsPassword) = RunBandizip(filePath, extractPath, null);
            if (ok)
                return true;
            // 加密包：等多久都没用，直接转去试密码库（只有占用冲突才值得等）
            if (needsPassword)
                return TryPasswords(filePath, pwd => RunBandizip(filePath, extractPath, pwd).Ok);
            lastCode = exitCode;
            if (attempt < maxAttempts)
            {
                Logger.Warning($"bz.exe 解压返回码 {lastCode}，可能文件被占用，{attempt}/{maxAttempts} 次后重试");
                Thread.Sleep(10000);
            }
        }
        throw new Exception($"bz.exe 解压失败，返回码 {lastCode}");
    }

    /// <summary>
    /// 跑一次 bz.exe。输出里的 0xa0000020（需要密码）/ 0xa0000021（密码错误）
    /// 是区分「加密包」与「文件被占用」的唯一依据——两者的返回码都是 2。
    /// </summary>
    private static (bool Ok, int ExitCode, bool NeedsPassword) RunBandizip(
        string filePath, string extractPath, string? password)
    {
        var psi = new ProcessStartInfo
        {
            FileName = BandizipBz,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        };
        psi.ArgumentList.Add("x");
        psi.ArgumentList.Add($"-o:{extractPath}");
        psi.ArgumentList.Add("-aoa");
        psi.ArgumentList.Add("-y");
        if (password != null)
            psi.ArgumentList.Add($"-p:{password}");
        psi.ArgumentList.Add(filePath);   // 压缩包必须放在最后（bz 的参数顺序要求）

        using var process = Process.Start(psi)!;
        var needsPassword = false;
        // 边跑边读：大包会逐个文件打印，输出攒满管道会把子进程卡死
        while (process.StandardOutput.ReadLine() is { } line)
            if (line.Contains("0xa0000020", StringComparison.Ordinal) ||
                line.Contains("0xa0000021", StringComparison.Ordinal))
                needsPassword = true;
        process.WaitForExit();
        return (process.ExitCode == 0, process.ExitCode, needsPassword);
    }

    private static bool ExtractWithSharpCompress(string filePath, string extractPath) =>
        IsEncryptedArchive(filePath)
            ? TryPasswords(filePath, pwd => SharpCompressExtract(filePath, extractPath, pwd))
            : SharpCompressExtract(filePath, extractPath, null);

    /// <summary>SharpCompress 回退：支持 zip / rar（含 RAR5 与分卷，需打开首卷）。</summary>
    private static bool SharpCompressExtract(string filePath, string extractPath, string? password)
    {
        try
        {
            using var archive = ArchiveFactory.OpenArchive(filePath, new ReaderOptions { Password = password });
            foreach (var entry in archive.Entries.Where(e => !e.IsDirectory))
                entry.WriteToDirectory(extractPath, new ExtractionOptions
                {
                    ExtractFullPath = true,
                    Overwrite = true,
                });
            return true;
        }
        catch (Exception e)
        {
            if (password == null)
                Logger.Error(e, $"解压 {filePath}");   // 试密码过程中的失败由 TryPasswords 统一汇报
            return false;
        }
    }

    /// <summary>压缩包里是否有加密条目（bz.exe 缺席时用来判断该不该上密码库）。</summary>
    private static bool IsEncryptedArchive(string filePath)
    {
        try
        {
            using var archive = ArchiveFactory.OpenArchive(filePath, new ReaderOptions());
            return archive.Entries.Any(e => !e.IsDirectory && e.IsEncrypted);
        }
        catch (Exception)
        {
            return false;   // 打不开就当普通包走，失败由正常流程记日志
        }
    }

    /// <summary>
    /// 逐条试密码解压，成功即止。日志只记密码的来源（同目录说明文件 / 密码库第几条），不写出密码本身。
    ///
    /// 先试压缩包同目录的「解压密码.txt / password.doc」之类说明文件里写的密码——网盘上的作品
    /// 十有八九是「压缩包 + 一个写着密码的小文件」一起打包下来的，那个密码就是为这个包准备的；
    /// 试不出来再走用户自己填的密码库。
    /// </summary>
    private static bool TryPasswords(string filePath, Func<string, bool> attempt)
    {
        var name = Path.GetFileName(filePath);
        var candidates = new List<(string Password, string Source)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (pwd, file) in SidecarPasswords(Path.GetDirectoryName(filePath) ?? ""))
            if (seen.Add(pwd))
                candidates.Add((pwd, $"同目录的 {file}"));
        var book = AppConfig.UnzipPasswords;
        for (var i = 0; i < book.Count; i++)
            if (seen.Add(book[i]))
                candidates.Add((book[i], $"密码库第 {i + 1} 条"));

        if (candidates.Count == 0)
        {
            Logger.Error($"{name} 需要解压密码，但密码库是空的、同目录也没有写着密码的说明文件" +
                         "（可在 系统设置 → 下载 → 解压密码库 里填）");
            return false;
        }
        Logger.Info($"{name} 需要解压密码，开始尝试 {candidates.Count} 条候选密码");
        foreach (var (password, source) in candidates)
            if (attempt(password))
            {
                Logger.Info($"{name} 已用{source}的密码解压");
                return true;
            }
        Logger.Error($"{name} 需要解压密码，但 {candidates.Count} 条候选密码都不匹配");
        return false;
    }

    // ---------- 同目录说明文件里的密码 ----------

    /// <summary>可能写着解压密码的说明文件扩展名（.doc 是 OLE 复合文档，按二进制挑字符串也读得出正文）。</summary>
    private static readonly string[] PasswordFileExts =
        [".txt", ".doc", ".docx", ".rtf", ".nfo", ".md", ".html", ".htm"];

    /// <summary>说明文件最大读多大——密码说明都只有几 KB，再大的多半不是说明文件。</summary>
    private const long MaxPasswordFileBytes = 1 << 20;

    /// <summary>纯文本说明文件：整篇没有「密码：」字样时，可以把独占一行的字符串本身当密码试。</summary>
    private static readonly string[] PlainTextExts = [".txt", ".nfo", ".md"];

    /// <summary>一个压缩包最多试多少条从说明文件里猜出来的密码。</summary>
    private const int MaxSidecarPasswords = 20;

    // 「密码：xxxx」「Unzip Password: xxxx」这类写法，取冒号/等号后面的第一段
    private static readonly Regex PasswordLinePattern = new(
        @"(?:解(?:压|壓)密(?:码|碼)|密(?:码|碼)|口令|パスワード|pass\s*word|password|passwd|pwd|pass)" +
        @"\s*(?:is)?\s*[:：=＝]?\s*([^\s:：=＝,，。、]{2,64})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // 整行就是一串没有空格的字符（形如 153792468 / a1B2c3!）：说明文件里常常只写密码本身
    private static readonly Regex LonePasswordPattern = new(
        @"^[\x21-\x7E]{4,64}$", RegexOptions.Compiled);

    /// <summary>
    /// 从压缩包同目录的说明文件里找候选密码（按文件名排序，取前 <see cref="MaxSidecarPasswords"/> 条）。
    ///
    /// 优先取「密码：xxx」这种明写出来的；整篇都没有明写的，才把「独占一行的无空格串」当候选，
    /// 免得把 .doc 里的字体名、软件版本号之类一股脑拿去试。
    /// </summary>
    private static List<(string Password, string File)> SidecarPasswords(string folder)
    {
        var found = new List<(string, string)>();
        if (folder.Length == 0 || !Directory.Exists(folder))
            return found;
        try
        {
            var files = Directory.EnumerateFiles(folder)
                .Where(f => PasswordFileExts.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var file in files)
            {
                if (found.Count >= MaxSidecarPasswords)
                    break;
                foreach (var pwd in PasswordsInFile(file))
                    if (seen.Add(pwd) && found.Count < MaxSidecarPasswords)
                        found.Add((pwd, Path.GetFileName(file)));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Logger.Error($"读取同目录密码说明文件失败: {e.Message}");
        }
        return found;
    }

    private static List<string> PasswordsInFile(string file)
    {
        var result = new List<string>();
        try
        {
            if (new FileInfo(file).Length is 0 or > MaxPasswordFileBytes)
                return result;
            var lines = ReadableLines(File.ReadAllBytes(file));
            foreach (var line in lines)
                foreach (Match m in PasswordLinePattern.Matches(line))
                    result.Add(m.Groups[1].Value);
            // 二进制文档（.doc/.docx）挑出来的字符串段里混着字体名、软件版本号，
            // 只有纯文本说明文件才值得把「独占一行的字符串」当密码试
            if (result.Count == 0 &&
                PlainTextExts.Contains(Path.GetExtension(file).ToLowerInvariant()))
                foreach (var line in lines)
                    if (LonePasswordPattern.IsMatch(line))
                        result.Add(line);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Logger.Error($"读取密码说明文件 {Path.GetFileName(file)} 失败: {e.Message}");
        }
        return result;
    }

    /// <summary>
    /// 从文件字节里抠出可读文本行。
    ///
    /// 纯文本按 UTF-8 解（解不动就按系统中文编码 GB18030 解）；.doc / .docx 这类二进制文档
    /// 没必要引一整个解析库——正文在里面是成段的 UTF-16LE 字符串，把这些串挑出来就够找密码了。
    /// </summary>
    private static List<string> ReadableLines(byte[] bytes)
    {
        var lines = new List<string>();
        void AddAll(string text)
        {
            foreach (var line in text.Split(['\r', '\n', '\t', '\0']))
                if (line.Trim() is { Length: > 0 } one)
                    lines.Add(one);
        }

        try
        {
            AddAll(new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes));
        }
        catch (DecoderFallbackException)
        {
            try
            {
                AddAll(Encoding.GetEncoding("GB18030").GetString(bytes));
            }
            catch (ArgumentException)
            {
                // 没有这个代码页就算了，UTF-16 段落一般已经够用
            }
            foreach (Match m in Utf16RunPattern.Matches(bytes.Length % 2 == 0
                         ? Encoding.Unicode.GetString(bytes)
                         : Encoding.Unicode.GetString(bytes, 0, bytes.Length - 1)))
                AddAll(m.Value);
        }
        return lines;
    }

    // 二进制文档里成段的可读文本（按 UTF-16LE 解出来后，连续 4 个以上可打印字符即视为一段）
    private static readonly Regex Utf16RunPattern = new(
        @"[^\p{C}]{4,}", RegexOptions.Compiled);

    /// <summary>
    /// 修复目录下文件名乱码：按 系统编码(默认 cp437) 编码再按 Shift_JIS 解码。
    /// 返回 true=已转码；false=文件名包含编码外字符，无需转码。
    /// </summary>
    public static bool FixEncoding(string workPath)
    {
        try
        {
            var sysEncoding = Encoding.GetEncoding(
                NormalizeEncodingName(AppConfig.SysEncoding),
                EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            var shiftJis = Encoding.GetEncoding(
                "shift_jis", EncoderFallback.ReplacementFallback,
                new DecoderReplacementFallback(""));  // 对应 Python 的 errors='ignore'
            return FixEncodingInner(workPath, sysEncoding, shiftJis);
        }
        catch (EncoderFallbackException)
        {
            return false;  // 文件名含编码外字符（如中文/日文），说明本来就没乱码
        }
        catch (Exception e)
        {
            Logger.Error(e, "文件名转码");
            return false;
        }
    }

    private static bool FixEncodingInner(string workPath, Encoding sysEncoding, Encoding shiftJis)
    {
        foreach (var entry in Directory.GetFileSystemEntries(workPath))
        {
            var name = Path.GetFileName(entry);
            if (Directory.Exists(entry))
                FixEncodingInner(entry, sysEncoding, shiftJis);  // 先处理子目录内容，再重命名子目录本身
            var fixedName = shiftJis.GetString(sysEncoding.GetBytes(name));
            if (fixedName.Length > 0 && fixedName != name)
            {
                var dest = Path.Combine(workPath, fixedName);
                if (Directory.Exists(entry))
                    Directory.Move(entry, dest);
                else
                    File.Move(entry, dest);
            }
        }
        return true;
    }

    private static string NormalizeEncodingName(string name) =>
        name.Trim().ToLowerInvariant() switch
        {
            "cp437" => "IBM437",
            "cp932" => "shift_jis",
            var n => n,
        };

    /// <summary>
    /// 解压后内容常被多包一层或多层目录（如 RJxxx\RJxxx\RJxxx），
    /// 沿"只有一个子目录"的链找到最终内容目录，把其内容移到根目录，并删除嵌套路径文件夹。
    /// </summary>
    public static void MoveToRoot(string workId, string folderPath)
    {
        try
        {
            var finalPath = folderPath;
            while (true)
            {
                var dirs = Directory.GetDirectories(finalPath);
                if (dirs.Length != 1)
                    break;
                finalPath = dirs[0];
            }
            if (string.Equals(Path.GetFullPath(finalPath), Path.GetFullPath(folderPath),
                    StringComparison.OrdinalIgnoreCase))
                return;
            var relative = Path.GetRelativePath(folderPath, finalPath);
            var topName = relative.Split(Path.DirectorySeparatorChar)[0];
            var topPath = Path.Combine(folderPath, topName);
            var pending = new List<(string Tmp, string Dst)>();  // 与嵌套目录重名的内容先用临时名
            foreach (var entry in Directory.GetFileSystemEntries(finalPath))
            {
                var name = Path.GetFileName(entry);
                var dst = Path.Combine(folderPath, name);
                if (File.Exists(dst) || Directory.Exists(dst))
                {
                    var tmp = dst + "_moving_tmp";
                    MoveEntry(entry, tmp);
                    pending.Add((tmp, dst));
                }
                else
                {
                    MoveEntry(entry, dst);
                }
            }
            Directory.Delete(topPath, true);
            foreach (var (tmp, dst) in pending)
                MoveEntry(tmp, dst);
            Logger.Info($"{workId} 已将最终目录内容移动到根目录");
        }
        catch (Exception e)
        {
            Logger.Error(e, "拍平嵌套目录");
        }
    }

    private static void MoveEntry(string source, string dest)
    {
        if (Directory.Exists(source))
            Directory.Move(source, dest);
        else
            File.Move(source, dest);
    }

    // 分卷包只解首卷，后续分卷由解压器自己带上（否则会逐个尝试后续分卷、刷一堆失败日志）
    private static readonly Regex NonFirstVolume =
        new(@"\.part0*([2-9]|\d{2,})\.rar$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>包中包最多解多少轮（异常增殖时的兜底，与 <see cref="Unzip"/> 同一量级）。</summary>
    private const int MaxNestedRounds = 20;

    /// <summary>
    /// 解压中转目录（建在作品目录下）：包里的东西先解到这里，拍平后立刻搬进作品根目录再删掉。
    /// 名字固定，上次中途中断留下的残留下一次能认出来接着收，不会在作品里堆一堆无名目录。
    /// </summary>
    private const string TempDirName = ".unzip_tmp";

    /// <summary>
    /// 就地解压目录里的压缩包（fanbox 投稿的附件常是压缩包，且常带密码）。
    ///
    /// 三件事：
    ///   1. **解到底**——网盘上的作品常是「外层一个壳包，里面才是本体（还可能带密码说明文件）」，
    ///      故按轮循环：每轮重扫一遍目录，把上一轮新解出来的包继续解，直到没有压缩包为止。
    ///   2. **解压结果一律放进作品根目录**——fanbox/网盘作品在库里本来就是「图片摊在作品目录里」
    ///      的形态（详情页与看图都按这个来），中间多套几层壳目录只会碍事。
    ///   3. **不留解压时建的目录**——包里的文件可能与已下载的图片重名，所以不能直接解到根目录，
    ///      而是先解到 <see cref="TempDirName"/> 再整体搬走、随手删掉；重名不覆盖，自动加 _2/_3。
    ///
    /// 返回是否全部解开；调用方无论成败都应继续入库，别让一个解不开的包把整篇作品挡在缓存目录里。
    /// </summary>
    public static bool ExtractArchivesInPlace(string workId, string folderPath)
    {
        var tmp = Path.Combine(folderPath, TempDirName);
        var failed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // 解不开的包，不再重试
        var moved = DrainTempDir(tmp, folderPath);   // 上次中断留下的半截结果，先收进根目录
        string? lastSignature = null;

        for (var round = 1; ; round++)
        {
            var archives = GetAllArchiveFiles(folderPath)
                .Where(a => !Path.GetExtension(a).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                .Where(a => !NonFirstVolume.IsMatch(a))
                .Where(a => !failed.Contains(a))
                .ToList();
            if (archives.Count == 0)
                break;
            // 本轮待解集合与上一轮完全相同 → 解压/删包都没推进，再转下去也是空转
            var signature = string.Join("|", archives.OrderBy(a => a, StringComparer.OrdinalIgnoreCase));
            if (signature == lastSignature)
            {
                Logger.Error($"{workId} 解压无进展（压缩包无法删除或反复重现），已中止：{archives[0]}");
                break;
            }
            lastSignature = signature;
            if (round > MaxNestedRounds)
            {
                Logger.Error($"{workId} 嵌套压缩包层数超过上限（{MaxNestedRounds}），已中止");
                break;
            }

            foreach (var archive in archives)
            {
                if (!File.Exists(archive))
                    continue;   // 已被上一个包当作分卷一并解掉
                Logger.Info($"{workId} 解压附件 {Path.GetFileName(archive)}");
                DiscardTempDir(tmp);   // 上一个包若解到一半失败，残留不能混进这一个包的结果
                if (!ExtractArchive(archive, tmp))
                {
                    failed.Add(archive);
                    DiscardTempDir(tmp);
                    continue;
                }
                MoveToRoot(workId, tmp);   // 包里常再套一层同名目录，拍平
                FixEncoding(tmp);          // SharpCompress 回退时的 Shift_JIS 乱码
                moved += DrainTempDir(tmp, folderPath);
                try
                {
                    File.Delete(archive);
                }
                catch (Exception e)
                {
                    Logger.Error(e, $"删除压缩包 {archive}");
                }
            }
        }

        if (moved > 0)
            Logger.Info($"{workId} 解压完成，{moved} 项内容已放入作品根目录");
        return failed.Count == 0;
    }

    /// <summary>
    /// 把中转目录里的东西搬进作品根目录，再把空掉的中转目录删掉，返回搬了几项。
    /// 目录本身用非递归删除：万一有文件没搬成功，宁可把目录留着也不能连内容一起删掉。
    /// </summary>
    private static int DrainTempDir(string tmp, string root)
    {
        if (!Directory.Exists(tmp))
            return 0;
        var moved = 0;
        try
        {
            foreach (var entry in Directory.GetFileSystemEntries(tmp))
            {
                MoveEntry(entry, UniquePath(root, Path.GetFileName(entry)));
                moved++;
            }
            Directory.Delete(tmp);
        }
        catch (Exception e)
        {
            Logger.Error(e, $"移动解压结果到作品根目录 {tmp}");
        }
        return moved;
    }

    /// <summary>丢弃中转目录里解了一半的残留（只有解压失败时才走这里，删的都是本次解出来的碎片）。</summary>
    private static void DiscardTempDir(string tmp)
    {
        if (!Directory.Exists(tmp))
            return;
        try
        {
            Directory.Delete(tmp, true);
        }
        catch (Exception e)
        {
            Logger.Error(e, $"清理解压残留 {tmp}");
        }
    }

    /// <summary>目录内不重名的落点：已存在就在名字后加 _2/_3…（扩展名保持在最后）。</summary>
    private static string UniquePath(string folder, string name)
    {
        var dest = Path.Combine(folder, name);
        if (!File.Exists(dest) && !Directory.Exists(dest))
            return dest;
        var ext = Path.GetExtension(name);
        var stem = name[..^ext.Length];
        for (var i = 2; ; i++)
        {
            dest = Path.Combine(folder, $"{stem}_{i}{ext}");
            if (!File.Exists(dest) && !Directory.Exists(dest))
                return dest;
        }
    }

    /// <summary>
    /// 解压一个作品：循环解出所有压缩包（解一轮删一轮，处理包中包），
    /// 完成后拍平目录、修复乱码，然后移动到媒体库并入库补全元数据。
    /// </summary>
    public static void Unzip(string workId)
    {
        try
        {
            // 与下载逻辑保持同一文件夹命名方式（RJ号 / 作品名称）
            var folderPath = DownloadEngine.WorkFolderPath(workId);
            Logger.Info($"{workId} 正在解压");
            string? lastSignature = null;   // 上一轮压缩包集合签名，用于检测"无进展"避免死循环
            var rounds = 0;
            // 已确认"解压失败、不是自解压包"的 exe（即作品自带的可执行文件）：
            // 不再当压缩包反复尝试，也绝不删除，否则会卡死整个收尾流程或破坏作品。
            var skipExe = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (true)
            {
                var archives = GetAllArchiveFiles(folderPath)
                    .Where(a => !skipExe.Contains(a)).ToList();
                if (archives.Count == 0)
                {
                    MoveToRoot(workId, folderPath);
                    var transcoded = FixEncoding(folderPath);
                    Logger.Info($"{workId} 解压成功");
                    Logger.Info(transcoded ? $"{workId} 转码成功" : $"{workId} 无需转码");
                    FinalizeIntoLibrary(workId, folderPath);
                    return;
                }
                // 防止无限循环：本轮压缩包集合与上一轮完全相同，说明解压/删除均未推进，中止
                var signature = string.Join("|",
                    archives.OrderBy(a => a, StringComparer.OrdinalIgnoreCase));
                if (signature == lastSignature)
                {
                    Logger.Error($"{workId} 解压无进展（压缩包无法删除或反复重现），已中止：{archives[0]}");
                    return;
                }
                lastSignature = signature;
                // 硬上限兜底：嵌套压缩包异常增殖时也能跳出
                if (++rounds > 100)
                {
                    Logger.Error($"{workId} 解压轮次超过上限（100），已中止");
                    return;
                }

                // 优先解真正的压缩包（zip/rar，含分卷）；只剩 exe 时才逐个试解。
                var realArchives = archives
                    .Where(a => !Path.GetExtension(a).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (realArchives.Count > 0)
                {
                    if (!ExtractArchive(realArchives[0], folderPath))
                        return;
                    Thread.Sleep(3000);
                    // 只删真正的压缩包/分卷，绝不删除解压出来的 exe（作品本体）
                    foreach (var archive in realArchives)
                        try
                        {
                            if (File.Exists(archive))
                                File.Delete(archive);
                        }
                        catch (Exception e)
                        {
                            Logger.Error(e, $"删除压缩包 {archive}");
                        }
                }
                else
                {
                    // 只剩 exe：可能是自解压包，也可能是作品自带 exe。试解一次区分：
                    // 成功 → 是自解压包，删掉它继续；失败 → 是作品文件，跳过保留、继续收尾。
                    var exe = archives[0];
                    if (ExtractArchive(exe, folderPath))
                    {
                        Thread.Sleep(3000);
                        try
                        {
                            if (File.Exists(exe))
                                File.Delete(exe);
                        }
                        catch (Exception e)
                        {
                            Logger.Error(e, $"删除自解压包 {exe}");
                        }
                    }
                    else
                    {
                        Logger.Warning($"{workId} {Path.GetFileName(exe)} 不是自解压压缩包，按作品文件保留");
                        skipExe.Add(exe);
                    }
                }
            }
        }
        catch (Exception e)
        {
            Logger.Error(e, "解压作品");
        }
    }

    /// <summary>
    /// 下载/解压成功后入库：从缓存目录移动到媒体库目标目录，标记为已品悦，
    /// 关联所属媒体库和文件夹，然后后台补全详细元数据。
    /// asmr.one 直链下载（无压缩包）也复用此收尾逻辑。
    /// </summary>
    internal static void FinalizeIntoLibrary(string workId, string folderPath)
    {
        // 解压完成后把作品从缓存目录移动到媒体库目标目录，后续都用最终目录
        folderPath = DownloadEngine.MoveToTargetFolder(workId, folderPath);
        // 优先用入队时记录的所属媒体库名；没有时再通过父目录匹配媒体库文件夹
        var libName = DownloadEngine.ReadWorkTargetLib(workId);
        if (libName == null)
        {
            var parent = Path.GetDirectoryName(Path.GetFullPath(folderPath));
            foreach (var lib in AppConfig.ReadMediaLibs())
            {
                if (lib.Folders.Any(f => string.Equals(
                        Path.GetFullPath(f), parent, StringComparison.OrdinalIgnoreCase)))
                {
                    libName = lib.Name;
                    break;
                }
            }
        }

        MediaLibraryService.ImportRjList(
            [workId], "已品悦", libName,
            new Dictionary<string, string> { [workId] = Path.GetFullPath(folderPath) });
        Logger.Info($"{workId} 已标记为已品悦，媒体库: {libName ?? "未关联"}");

        // 移动到媒体库后只补全本作品的元数据（DL API 字段 + 作品页正文/标签/图片）
        new Thread(() =>
        {
            try
            {
                Logger.Info($"{workId} 开始获取元数据");
                MediaLibraryService.BackfillWorksFromApiAsync(
                    delaySeconds: 0.5, workIds: [workId]).GetAwaiter().GetResult();
                MediaLibraryService.BackfillWorkPagesAsync(
                    delaySeconds: 1.0, workIds: [workId]).GetAwaiter().GetResult();
                Logger.Info($"{workId} 元数据获取完成");
            }
            catch (Exception e)
            {
                Logger.Error(e, "补全元数据");
            }
        }) { IsBackground = true, Name = $"backfill-{workId}" }.Start();
    }
}
