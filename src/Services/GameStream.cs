using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using NAudio.Wave;
using R18MediaLibrary.Core;

namespace R18MediaLibrary.Services;

/// <summary>
/// 游戏远程串流：作品里的 Windows 程序在本机运行，画面抓下来经 WebSocket 推给浏览器，
/// 浏览器的鼠标/触摸/键盘再回传过来用 SendInput 注入——"在网页上运行 exe"只能这么做，浏览器本身跑不了 Windows 程序。
///
/// 协议（同一条 WebSocket）：
/// - 服务端 → 浏览器，二进制帧首字节区分类型：1 = 一帧 JPEG 画面，2 = 一段 16 位交错 PCM 声音；
///   文本帧为 JSON 状态：{t:"status",text} / {t:"audio",rate,ch} / {t:"exit"}。
/// - 浏览器 → 服务端，文本帧 JSON：{t:"m",a:"move|down|up",b,x,y}（x/y 是 0~1 的画面相对坐标）、
///   {t:"w",d}（滚轮）、{t:"k",a:"down|up",code}（KeyboardEvent.code）、{t:"audio",on}、{t:"quit"}（关闭程序窗口）。
///
/// 只保留一路串流：屏幕、鼠标、键盘都只有一套，两路同时操作只会互相打架，新连接进来就把旧的踢掉。
/// 断开串流不结束游戏（可以重新连上继续玩），结束要走 quit。
/// </summary>
public static class GameStream
{
    private const int MaxWidth = 1280;      // 画面超过此宽度先缩小再编码，局域网带宽与手机解码都吃得消
    private const int FrameIntervalMs = 40; // 约 25 帧/秒
    private const long JpegQuality = 70;
    private const int WindowWaitSeconds = 60;

    private static readonly object Sync = new();
    private static Session? _current;

    /// <summary>
    /// 在调用线程上跑完一整路串流（握手已由 <see cref="WebServer"/> 完成）。
    /// <paramref name="exePath"/> 所在作品目录 <paramref name="workRoot"/> 下已有程序窗口时直接接管，否则先启动它。
    /// </summary>
    public static void Run(NetworkStream stream, string exePath, string workRoot)
    {
        var session = new Session(stream, exePath, workRoot);
        lock (Sync)
        {
            _current?.Stop();
            _current = session;
        }
        try
        {
            session.Run();
        }
        finally
        {
            lock (Sync)
            {
                if (_current == session)
                    _current = null;
            }
        }
    }

    private sealed class Session(NetworkStream stream, string exePath, string workRoot)
    {
        private readonly object _sendLock = new();
        private readonly ConcurrentQueue<byte[]> _audioOut = new();
        private readonly HashSet<ushort> _heldKeys = [];
        private readonly HashSet<int> _heldButtons = [];
        private volatile bool _stopped;
        private volatile bool _audioWanted;
        private IntPtr _hwnd;

        public void Stop()
        {
            _stopped = true;
            try { stream.Close(); }
            catch (Exception) { /* 已断开 */ }
        }

        public void Run()
        {
            Native.SetThreadDpiAwarenessContext(Native.PerMonitorAwareV2);
            stream.ReadTimeout = Timeout.Infinite;
            var writer = new Thread(WriterLoop) { IsBackground = true, Name = "GameStreamWriter" };
            writer.Start();
            WasapiLoopbackCapture? audio = null;
            try
            {
                while (!_stopped)
                {
                    var msg = WebSocket.ReadMessage(stream, out var opcode);
                    if (msg == null || opcode == WebSocket.OpClose)
                        break;
                    if (opcode == WebSocket.OpPing)
                    {
                        Send(WebSocket.OpPong, msg);
                        continue;
                    }
                    if (opcode != WebSocket.OpText)
                        continue;
                    HandleInput(Encoding.UTF8.GetString(msg), ref audio);
                }
            }
            catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
            {
                // 浏览器关页/断网
            }
            finally
            {
                _stopped = true;
                ReleaseHeld();
                try { audio?.StopRecording(); audio?.Dispose(); }
                catch (Exception) { /* 声卡已释放 */ }
                writer.Join(2000);
                Logger.Info($"游戏串流结束：{exePath}");
            }
        }

        // ---------- 画面 ----------

        private void WriterLoop()
        {
            Native.SetThreadDpiAwarenessContext(Native.PerMonitorAwareV2);
            try
            {
                _hwnd = FindOrLaunch();
                if (_hwnd == IntPtr.Zero)
                {
                    SendStatus("没有等到程序窗口出现，可能启动失败或程序没有窗口");
                    return;
                }
                Logger.Info($"游戏串流开始：{exePath}");
                SendStatus("");
                Native.BringToFront(_hwnd);
                byte[]? last = null;
                var exitedSince = DateTime.MinValue;
                var sw = Stopwatch.StartNew();
                while (!_stopped)
                {
                    sw.Restart();
                    DrainAudio();
                    if (!Native.IsWindow(_hwnd))
                    {
                        // 游戏换了窗口（启动器 → 本体、切换全屏重建窗口）就跟过去，真退出了才通知浏览器
                        var next = FindWindow(null);
                        if (next != IntPtr.Zero)
                        {
                            _hwnd = next;
                            exitedSince = DateTime.MinValue;
                        }
                        else
                        {
                            if (exitedSince == DateTime.MinValue)
                                exitedSince = DateTime.Now;
                            else if ((DateTime.Now - exitedSince).TotalSeconds > 3)
                            {
                                SendText(new { t = "exit" });
                                return;
                            }
                            Thread.Sleep(200);
                            continue;
                        }
                    }
                    var jpeg = Capture(_hwnd);
                    // 画面没变就不重发：菜单/对话停住时带宽几乎为零
                    if (jpeg != null && (last == null || !jpeg.AsSpan().SequenceEqual(last)))
                    {
                        var frame = new byte[jpeg.Length + 1];
                        frame[0] = 1;
                        jpeg.CopyTo(frame, 1);
                        Send(WebSocket.OpBinary, frame);
                        last = jpeg;
                    }
                    var wait = FrameIntervalMs - (int)sw.ElapsedMilliseconds;
                    if (wait > 0)
                        Thread.Sleep(wait);
                }
            }
            catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
            {
                // 发送失败即连接已断，读线程那边会收尾
            }
            catch (Exception e)
            {
                Logger.Error($"游戏串流异常：{e.Message}");
            }
            finally
            {
                Stop();
            }
        }

        private IntPtr FindOrLaunch()
        {
            var hwnd = FindWindow(null);
            if (hwnd != IntPtr.Zero)
                return hwnd;
            Process? proc;
            try
            {
                proc = Process.Start(new ProcessStartInfo(exePath)
                {
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(exePath)!,
                });
            }
            catch (Exception e)
            {
                SendStatus("启动失败：" + e.Message);
                return IntPtr.Zero;
            }
            SendStatus("程序启动中，等待窗口出现…");
            var deadline = DateTime.Now.AddSeconds(WindowWaitSeconds);
            while (!_stopped && DateTime.Now < deadline)
            {
                Thread.Sleep(300);
                hwnd = FindWindow(proc);
                if (hwnd != IntPtr.Zero)
                {
                    // 刚出现的窗口常是启动画面，稍等它稳定再取一次，取最大的那个
                    Thread.Sleep(800);
                    var again = FindWindow(proc);
                    return again != IntPtr.Zero ? again : hwnd;
                }
            }
            return IntPtr.Zero;
        }

        /// <summary>找作品的程序窗口：进程是刚启动的那个，或进程的 exe 落在作品目录里（覆盖"启动器再拉起本体"的情况）。取面积最大的可见顶层窗口。</summary>
        private IntPtr FindWindow(Process? proc)
        {
            var root = workRoot.TrimEnd('\\') + "\\";
            var pathCache = new Dictionary<uint, bool>();
            IntPtr best = IntPtr.Zero;
            long bestArea = 0;
            Native.EnumWindows((h, _) =>
            {
                if (!Native.IsWindowVisible(h) || Native.GetWindow(h, Native.GwOwner) != IntPtr.Zero)
                    return true;
                Native.GetWindowThreadProcessId(h, out var pid);
                if (!pathCache.TryGetValue(pid, out var mine))
                {
                    mine = (proc != null && pid == (uint)proc.Id) ||
                           (Native.ProcessPath(pid) is { } p && p.StartsWith(root, StringComparison.OrdinalIgnoreCase));
                    pathCache[pid] = mine;
                }
                if (!mine || !Native.GetWindowRect(h, out var r))
                    return true;
                long area = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
                if (area > bestArea && r.Right - r.Left > 100 && r.Bottom - r.Top > 100)
                {
                    best = h;
                    bestArea = area;
                }
                return true;
            }, IntPtr.Zero);
            return best;
        }

        private Bitmap? _raw, _scaled;
        private static readonly ImageCodecInfo JpegCodec =
            ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

        /// <summary>
        /// 抓窗口客户区。窗口在最前时直接从屏幕拷（最快，DirectX 画面也拷得到）；
        /// 被挡住时改用 PrintWindow(PW_RENDERFULLCONTENT)，免得把盖在上面的别的窗口拍进去。
        /// </summary>
        private byte[]? Capture(IntPtr hwnd)
        {
            if (Native.IsIconic(hwnd))
            {
                Native.ShowWindow(hwnd, Native.SwRestore);
                return null;
            }
            if (!Native.GetClientRect(hwnd, out var rc) || rc.Right <= 0 || rc.Bottom <= 0)
                return null;
            var origin = new Native.Point();
            Native.ClientToScreen(hwnd, ref origin);
            int w = rc.Right, h = rc.Bottom;
            if (_raw == null || _raw.Width != w || _raw.Height != h)
            {
                _raw?.Dispose();
                _raw = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            }
            using (var g = Graphics.FromImage(_raw))
            {
                if (Native.GetForegroundWindow() == hwnd)
                    g.CopyFromScreen(origin.X, origin.Y, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
                else
                {
                    var hdc = g.GetHdc();
                    try { Native.PrintWindow(hwnd, hdc, Native.PwClientOnly | Native.PwRenderFullContent); }
                    finally { g.ReleaseHdc(hdc); }
                }
            }
            var src = _raw;
            if (w > MaxWidth)
            {
                var sh = Math.Max(1, h * MaxWidth / w);
                if (_scaled == null || _scaled.Width != MaxWidth || _scaled.Height != sh)
                {
                    _scaled?.Dispose();
                    _scaled = new Bitmap(MaxWidth, sh, PixelFormat.Format24bppRgb);
                }
                using var g2 = Graphics.FromImage(_scaled);
                g2.InterpolationMode = InterpolationMode.Bilinear;
                g2.DrawImage(_raw, 0, 0, MaxWidth, sh);
                src = _scaled;
            }
            using var ms = new MemoryStream();
            using var ep = new EncoderParameters(1);
            ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, JpegQuality);
            src.Save(ms, JpegCodec, ep);
            return ms.ToArray();
        }

        // ---------- 声音 ----------

        /// <summary>系统声音回环采集（抓的是整机输出，不只是游戏）。采集回调里只入队，由写线程统一发送，免得网络阻塞卡住声卡回调。</summary>
        private WasapiLoopbackCapture? StartAudio()
        {
            try
            {
                var cap = new WasapiLoopbackCapture();
                var fmt = cap.WaveFormat;
                var isFloat = fmt.Encoding == WaveFormatEncoding.IeeeFloat ||
                              (fmt.Encoding == WaveFormatEncoding.Extensible && fmt.BitsPerSample == 32);
                if (!isFloat && fmt.BitsPerSample != 16)
                {
                    cap.Dispose();
                    SendStatus("声卡输出格式不支持，无法传声音");
                    return null;
                }
                var channels = fmt.Channels;
                var bytesPer = fmt.BitsPerSample / 8;
                cap.DataAvailable += (_, e) =>
                {
                    if (!_audioWanted || e.BytesRecorded == 0)
                        return;
                    // 统一成双声道 16 位：多声道只取前两路，单声道复制一份
                    var frames = e.BytesRecorded / (bytesPer * channels);
                    var outBuf = new byte[1 + frames * 4];
                    outBuf[0] = 2;
                    for (var i = 0; i < frames; i++)
                        for (var c = 0; c < 2; c++)
                        {
                            var srcCh = Math.Min(c, channels - 1);
                            var off = (i * channels + srcCh) * bytesPer;
                            short s = isFloat
                                ? (short)Math.Clamp(BitConverter.ToSingle(e.Buffer, off) * 32767f, -32768f, 32767f)
                                : BitConverter.ToInt16(e.Buffer, off);
                            BitConverter.TryWriteBytes(outBuf.AsSpan(1 + (i * 2 + c) * 2), s);
                        }
                    _audioOut.Enqueue(outBuf);
                    while (_audioOut.Count > 50)   // 发不出去就丢旧的，宁可断一下也不越积越延迟
                        _audioOut.TryDequeue(out var _dropped);
                };
                cap.StartRecording();
                SendText(new { t = "audio", rate = fmt.SampleRate, ch = 2 });
                return cap;
            }
            catch (Exception e)
            {
                SendStatus("声音采集失败：" + e.Message);
                return null;
            }
        }

        private void DrainAudio()
        {
            while (_audioOut.TryDequeue(out var chunk))
                Send(WebSocket.OpBinary, chunk);
        }

        // ---------- 输入 ----------

        private void HandleInput(string json, ref WasapiLoopbackCapture? audio)
        {
            JsonDocument doc;
            try { doc = JsonDocument.Parse(json); }
            catch (JsonException) { return; }
            using var _ = doc;
            var r = doc.RootElement;
            var t = Str(r, "t");
            if (t == "audio")
            {
                _audioWanted = r.TryGetProperty("on", out var on) && on.ValueKind == JsonValueKind.True;
                if (_audioWanted && audio == null)
                    audio = StartAudio();
                return;
            }
            var hwnd = _hwnd;
            if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd))
                return;
            switch (t)
            {
                case "quit":
                    Native.PostMessage(hwnd, Native.WmClose, IntPtr.Zero, IntPtr.Zero);
                    break;
                case "m":
                    MouseInput(hwnd, Str(r, "a"), Num(r, "b"), Dbl(r, "x"), Dbl(r, "y"));
                    break;
                case "w":
                    Native.SendMouse(0, 0, Native.MouseWheel, (uint)Math.Clamp(Num(r, "d"), -1200, 1200));
                    break;
                case "k":
                    KeyInput(hwnd, Str(r, "a"), Str(r, "code"));
                    break;
            }
        }

        private void MouseInput(IntPtr hwnd, string action, int button, double x, double y)
        {
            if (!Native.GetClientRect(hwnd, out var rc))
                return;
            var p = new Native.Point
            {
                X = (int)(Math.Clamp(x, 0, 1) * Math.Max(0, rc.Right - 1)),
                Y = (int)(Math.Clamp(y, 0, 1) * Math.Max(0, rc.Bottom - 1)),
            };
            Native.ClientToScreen(hwnd, ref p);
            if (action == "down")
                Native.BringToFront(hwnd);   // 点下去之前窗口必须在最前，否则点到盖在上面的别的窗口
            var (dx, dy) = Native.ToAbsolute(p.X, p.Y);
            uint flags = Native.MouseMove | Native.MouseAbsolute | Native.MouseVirtualDesk;
            if (action is "down" or "up")
            {
                var (down, up) = button switch
                {
                    1 => (Native.MouseMiddleDown, Native.MouseMiddleUp),
                    2 => (Native.MouseRightDown, Native.MouseRightUp),
                    _ => (Native.MouseLeftDown, Native.MouseLeftUp),
                };
                flags |= action == "down" ? down : up;
                if (action == "down") _heldButtons.Add(button);
                else _heldButtons.Remove(button);
            }
            Native.SendMouse(dx, dy, flags, 0);
        }

        private void KeyInput(IntPtr hwnd, string action, string code)
        {
            if (!KeyMap.TryGetValue(code, out var key))
                return;   // 只放行白名单里的键（Win 键等会动到整台电脑的键不放）
            if (action == "down")
            {
                if (Native.GetForegroundWindow() != hwnd)
                    Native.BringToFront(hwnd);
                _heldKeys.Add(key.Vk);
            }
            else
                _heldKeys.Remove(key.Vk);
            Native.SendKey(key.Vk, key.Extended, action != "down");
        }

        /// <summary>连接断开时把还按着的键/鼠标键松开，否则本机会一直"按住"着方向键。</summary>
        private void ReleaseHeld()
        {
            foreach (var vk in _heldKeys)
            {
                var ext = KeyMap.Values.FirstOrDefault(k => k.Vk == vk).Extended;
                Native.SendKey(vk, ext, true);
            }
            _heldKeys.Clear();
            foreach (var b in _heldButtons)
                Native.SendMouse(0, 0, b switch
                {
                    1 => Native.MouseMiddleUp,
                    2 => Native.MouseRightUp,
                    _ => Native.MouseLeftUp,
                }, 0);
            _heldButtons.Clear();
        }

        // ---------- 发送 ----------

        private void SendStatus(string text) => SendText(new { t = "status", text });

        private void SendText(object o) =>
            Send(WebSocket.OpText, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(o)));

        private void Send(int opcode, byte[] payload)
        {
            lock (_sendLock)
                WebSocket.WriteFrame(stream, opcode, payload);
        }

        private static string Str(JsonElement r, string name) =>
            r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

        private static int Num(JsonElement r, string name) =>
            r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? (int)v.GetDouble() : 0;

        private static double Dbl(JsonElement r, string name) =>
            r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
    }

    // ---------- 键位表：KeyboardEvent.code → 虚拟键码（+是否扩展键） ----------

    private static readonly Dictionary<string, (ushort Vk, bool Extended)> KeyMap = BuildKeyMap();

    private static Dictionary<string, (ushort, bool)> BuildKeyMap()
    {
        var m = new Dictionary<string, (ushort, bool)>(StringComparer.Ordinal);
        for (var c = 'A'; c <= 'Z'; c++) m["Key" + c] = (c, false);
        for (var d = 0; d <= 9; d++)
        {
            m["Digit" + d] = ((ushort)('0' + d), false);
            m["Numpad" + d] = ((ushort)(0x60 + d), false);
        }
        for (var f = 1; f <= 12; f++) m["F" + f] = ((ushort)(0x6F + f), false);
        m["Enter"] = (0x0D, false); m["NumpadEnter"] = (0x0D, true);
        m["Escape"] = (0x1B, false); m["Space"] = (0x20, false);
        m["Tab"] = (0x09, false); m["Backspace"] = (0x08, false);
        m["ShiftLeft"] = (0x10, false); m["ShiftRight"] = (0x10, false);
        m["ControlLeft"] = (0x11, false); m["ControlRight"] = (0x11, true);
        m["AltLeft"] = (0x12, false); m["AltRight"] = (0x12, true);
        m["ArrowLeft"] = (0x25, true); m["ArrowUp"] = (0x26, true);
        m["ArrowRight"] = (0x27, true); m["ArrowDown"] = (0x28, true);
        m["Insert"] = (0x2D, true); m["Delete"] = (0x2E, true);
        m["Home"] = (0x24, true); m["End"] = (0x23, true);
        m["PageUp"] = (0x21, true); m["PageDown"] = (0x22, true);
        m["Minus"] = (0xBD, false); m["Equal"] = (0xBB, false);
        m["Comma"] = (0xBC, false); m["Period"] = (0xBE, false); m["Slash"] = (0xBF, false);
        m["Semicolon"] = (0xBA, false); m["Quote"] = (0xDE, false); m["Backquote"] = (0xC0, false);
        m["BracketLeft"] = (0xDB, false); m["BracketRight"] = (0xDD, false); m["Backslash"] = (0xDC, false);
        m["NumpadAdd"] = (0x6B, false); m["NumpadSubtract"] = (0x6D, false);
        m["NumpadMultiply"] = (0x6A, false); m["NumpadDivide"] = (0x6F, true); m["NumpadDecimal"] = (0x6E, false);
        return m;
    }

    // ---------- 极简 WebSocket（RFC 6455，只需服务端那一半） ----------

    public static class WebSocket
    {
        public const int OpText = 1, OpBinary = 2, OpClose = 8, OpPing = 9, OpPong = 10;

        /// <summary>握手应答里的 Sec-WebSocket-Accept。</summary>
        public static string AcceptKey(string key) =>
            Convert.ToBase64String(System.Security.Cryptography.SHA1.HashData(
                Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));

        public static void WriteFrame(Stream s, int opcode, byte[] payload)
        {
            var len = payload.Length;
            var head = new byte[len < 126 ? 2 : len <= ushort.MaxValue ? 4 : 10];
            head[0] = (byte)(0x80 | opcode);
            if (len < 126)
                head[1] = (byte)len;
            else if (len <= ushort.MaxValue)
            {
                head[1] = 126;
                head[2] = (byte)(len >> 8);
                head[3] = (byte)len;
            }
            else
            {
                head[1] = 127;
                for (var i = 0; i < 8; i++)
                    head[9 - i] = (byte)((long)len >> (8 * i));
            }
            s.Write(head);
            s.Write(payload);
        }

        /// <summary>读一条完整消息（拼接分片）。连接关闭返回 null。浏览器发来的帧一定带掩码。</summary>
        public static byte[]? ReadMessage(Stream s, out int opcode)
        {
            opcode = 0;
            using var all = new MemoryStream();
            while (true)
            {
                var h = ReadExact(s, 2);
                if (h == null)
                    return null;
                var fin = (h[0] & 0x80) != 0;
                var op = h[0] & 0x0F;
                var masked = (h[1] & 0x80) != 0;
                long len = h[1] & 0x7F;
                if (len == 126)
                {
                    var e = ReadExact(s, 2);
                    if (e == null) return null;
                    len = (e[0] << 8) | e[1];
                }
                else if (len == 127)
                {
                    var e = ReadExact(s, 8);
                    if (e == null) return null;
                    len = 0;
                    for (var i = 0; i < 8; i++) len = (len << 8) | e[i];
                }
                if (len > 1 << 20)
                    return null;   // 输入消息都很小，超大帧直接当异常断开
                var mask = masked ? ReadExact(s, 4) : null;
                if (masked && mask == null)
                    return null;
                var data = ReadExact(s, (int)len);
                if (data == null)
                    return null;
                if (mask != null)
                    for (var i = 0; i < data.Length; i++)
                        data[i] ^= mask[i & 3];
                if (op >= 8)
                {
                    // 控制帧可夹在分片中间，单独返回
                    opcode = op;
                    return data;
                }
                if (op != 0)
                    opcode = op;
                all.Write(data);
                if (fin)
                    return all.ToArray();
            }
        }

        private static byte[]? ReadExact(Stream s, int n)
        {
            var buf = new byte[n];
            var read = 0;
            while (read < n)
            {
                var got = s.Read(buf, read, n - read);
                if (got <= 0)
                    return null;
                read += got;
            }
            return buf;
        }
    }

    // ---------- Win32 ----------

    private static class Native
    {
        public static readonly IntPtr PerMonitorAwareV2 = new(-4);
        public const uint GwOwner = 4;
        public const int SwRestore = 9;
        public const uint WmClose = 0x0010;
        public const uint PwClientOnly = 1, PwRenderFullContent = 2;
        public const uint MouseMove = 0x0001, MouseLeftDown = 0x0002, MouseLeftUp = 0x0004,
            MouseRightDown = 0x0008, MouseRightUp = 0x0010, MouseMiddleDown = 0x0020, MouseMiddleUp = 0x0040,
            MouseWheel = 0x0800, MouseVirtualDesk = 0x4000, MouseAbsolute = 0x8000;
        private const uint KeyExtended = 0x0001, KeyUp = 0x0002, KeyScanCode = 0x0008;

        [StructLayout(LayoutKind.Sequential)]
        public struct Rect { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct Point { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MouseInputData
        {
            public int Dx, Dy;
            public uint MouseData, Flags, Time;
            public IntPtr ExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KeyInputData
        {
            public ushort Vk, Scan;
            public uint Flags, Time;
            public IntPtr ExtraInfo;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)] public MouseInputData Mouse;
            [FieldOffset(0)] public KeyInputData Key;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Input
        {
            public uint Type;
            public InputUnion U;
        }

        public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out Rect r);
        [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out Rect r);
        [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref Point p);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr h);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint a, uint b, bool attach);
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int i);
        [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint type);
        [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint n, Input[] inputs, int size);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageName(IntPtr h, uint flags, StringBuilder name, ref uint size);

        public static string? ProcessPath(uint pid)
        {
            var h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
            if (h == IntPtr.Zero)
                return null;
            try
            {
                var sb = new StringBuilder(1024);
                uint size = (uint)sb.Capacity;
                return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
            }
            finally
            {
                CloseHandle(h);
            }
        }

        /// <summary>把窗口提到最前。后台进程直接 SetForegroundWindow 会被系统拒绝，借当前前台线程的输入队列绕过去。</summary>
        public static void BringToFront(IntPtr h)
        {
            if (IsIconic(h))
                ShowWindow(h, SwRestore);
            var fg = GetForegroundWindow();
            if (fg == h)
                return;
            var fgThread = GetWindowThreadProcessId(fg, out _);
            var me = GetCurrentThreadId();
            var attached = fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true);
            try
            {
                BringWindowToTop(h);
                SetForegroundWindow(h);
            }
            finally
            {
                if (attached)
                    AttachThreadInput(me, fgThread, false);
            }
        }

        /// <summary>屏幕坐标 → SendInput 的 0~65535 虚拟桌面绝对坐标（多显示器也对）。</summary>
        public static (int X, int Y) ToAbsolute(int x, int y)
        {
            int left = GetSystemMetrics(76), top = GetSystemMetrics(77);
            int w = Math.Max(2, GetSystemMetrics(78)), h = Math.Max(2, GetSystemMetrics(79));
            return ((int)((x - left) * 65535L / (w - 1)), (int)((y - top) * 65535L / (h - 1)));
        }

        public static void SendMouse(int dx, int dy, uint flags, uint data)
        {
            var input = new Input
            {
                Type = 0,
                U = new InputUnion { Mouse = new MouseInputData { Dx = dx, Dy = dy, MouseData = data, Flags = flags } },
            };
            SendInput(1, [input], Marshal.SizeOf<Input>());
        }

        /// <summary>按扫描码注入：DirectInput/RawInput 读的是扫描码，只发虚拟键码的话不少游戏收不到。</summary>
        public static void SendKey(ushort vk, bool extended, bool up)
        {
            var flags = KeyScanCode | (extended ? KeyExtended : 0) | (up ? KeyUp : 0);
            var input = new Input
            {
                Type = 1,
                U = new InputUnion { Key = new KeyInputData { Vk = vk, Scan = (ushort)MapVirtualKey(vk, 0), Flags = flags } },
            };
            SendInput(1, [input], Marshal.SizeOf<Input>());
        }
    }
}
