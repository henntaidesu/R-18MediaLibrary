using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using R18MediaLibrary.Core;

namespace R18MediaLibrary.Services;

/// <summary>
/// 网页端「在服务器上登录」的串流：把登录浏览器（<see cref="LoginBrowser"/>）的画面推给网页、把网页上的操作送回浏览器。
/// 协议与作品 exe 的远程串流相同（<see cref="GameStream"/>，stream.js 不必区分），实现却不同：
///
/// 登录浏览器是本程序自己的 WebView2，所以画面直接用 <c>CapturePreviewAsync</c> 让浏览器自己出图，
/// 输入经 DevTools 协议（<c>Input.dispatchMouseEvent / dispatchKeyEvent / insertText</c>）送进页面——
/// 不抓屏幕、不用 SendInput。服务器上这一点是关键：远程桌面断开或锁屏时根本没有可抓的桌面，
/// <c>CopyFromScreen</c> 直接抛异常、SendInput 也注不进去（游戏串流在这种状态下本就不可用），
/// 而浏览器自己出图、自己收输入不依赖桌面，也不会动到服务器上真实的鼠标键盘。
/// 浏览器在被遮挡时会停止绘制，这一点由 LoginBrowser 的启动参数关掉。
///
/// 所有 WebView2 调用都在 UI 线程上，本类的读写线程经 Dispatcher 转过去。同一时刻只有一路。
/// </summary>
public static class LoginStream
{
    private const int FrameIntervalMs = 120;   // 登录页基本是静态的，约 8 帧/秒足够，画面不变不重发

    private static readonly object Sync = new();
    private static NetworkStream? _current;

    /// <summary>在调用线程上跑完一整路串流（握手已由 WebServer 完成、登录窗口已打开）。</summary>
    public static void Run(NetworkStream stream, string site)
    {
        lock (Sync)
        {
            try { _current?.Close(); }
            catch (Exception) { /* 已断开 */ }
            _current = stream;
        }
        var app = Application.Current;
        var sendLock = new object();
        var stopped = false;
        void Send(int op, byte[] payload)
        {
            lock (sendLock)
                GameStream.WebSocket.WriteFrame(stream, op, payload);
        }
        void SendText(object o) => Send(GameStream.WebSocket.OpText, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(o)));

        var writer = new Thread(() =>
        {
            try
            {
                byte[]? last = null;
                var gone = DateTime.MinValue;
                var sw = Stopwatch.StartNew();
                SendText(new { t = "status", text = "电脑上的浏览器启动中…" });
                while (!stopped)
                {
                    sw.Restart();
                    // 抓帧要限时：登录成功后窗口关闭、WebView 被释放时，正在进行的 CapturePreviewAsync 可能永远不返回，
                    // 一直等下去写线程就卡死，网页收不到「登录完成」，停在最后一帧没反应
                    var capture = app.Dispatcher.InvokeAsync(CaptureAsync).Task.Unwrap();
                    var (open, jpeg) = capture.Wait(3000)
                        ? capture.Result
                        : (app.Dispatcher.Invoke(() => LoginBrowser.IsOpen), null);
                    if (!open)
                    {
                        // 窗口关了（登录成功自动关 / 被关掉）：稍等一下结果写好，再告诉网页
                        if (gone == DateTime.MinValue)
                            gone = DateTime.Now;
                        else if ((DateTime.Now - gone).TotalSeconds > 1)
                        {
                            SendText(new { t = "exit", text = app.Dispatcher.Invoke(() => LoginBrowser.ResultOf(site)) });
                            return;
                        }
                    }
                    else if (jpeg != null && (last == null || !jpeg.AsSpan().SequenceEqual(last)))
                    {
                        if (last == null)
                            SendText(new { t = "status", text = "" });
                        var frame = new byte[jpeg.Length + 1];
                        frame[0] = 1;
                        jpeg.CopyTo(frame, 1);
                        Send(GameStream.WebSocket.OpBinary, frame);
                        last = jpeg;
                    }
                    var wait = FrameIntervalMs - (int)sw.ElapsedMilliseconds;
                    if (wait > 0)
                        Thread.Sleep(wait);
                }
            }
            catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
            {
                // 连接已断
            }
            catch (Exception e)
            {
                Logger.Error($"登录浏览器串流异常：{e.Message}");
            }
            finally
            {
                stopped = true;
                try { stream.Close(); }
                catch (Exception) { /* 已断开 */ }
            }
        }) { IsBackground = true, Name = "LoginStreamWriter" };
        writer.Start();

        try
        {
            stream.ReadTimeout = Timeout.Infinite;
            while (!stopped)
            {
                var msg = GameStream.WebSocket.ReadMessage(stream, out var opcode);
                if (msg == null || opcode == GameStream.WebSocket.OpClose)
                    break;
                if (opcode == GameStream.WebSocket.OpPing)
                {
                    Send(GameStream.WebSocket.OpPong, msg);
                    continue;
                }
                if (opcode != GameStream.WebSocket.OpText)
                    continue;
                var json = Encoding.UTF8.GetString(msg);
                app.Dispatcher.InvokeAsync(() => HandleInputAsync(json));
            }
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
        {
            // 浏览器关页/断网：登录窗口留着，重新打开串流可接着登
        }
        finally
        {
            stopped = true;
            writer.Join(2000);
            lock (Sync)
                if (_current == stream)
                    _current = null;
            Logger.Info($"登录浏览器串流结束：{site}");
        }
    }

    /// <summary>（UI 线程）抓一帧：窗口是否还开着 + JPEG（浏览器还在初始化时为 null）。</summary>
    private static async Task<(bool Open, byte[]? Jpeg)> CaptureAsync()
    {
        if (!LoginBrowser.IsOpen)
            return (false, null);
        if (LoginBrowser.Core is not { } core)
            return (true, null);
        try
        {
            using var ms = new MemoryStream();
            await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Jpeg, ms);
            return (true, ms.ToArray());
        }
        catch (Exception)
        {
            return (true, null);   // 正在导航/关窗
        }
    }

    // ---------- 输入（UI 线程） ----------

    private static int _buttons;   // 当前按着的鼠标键（DevTools 的 buttons 位掩码）
    private static int _modifiers; // 当前按着的修饰键（Alt=1 Ctrl=2 Meta=4 Shift=8）

    private static async Task HandleInputAsync(string json)
    {
        if (LoginBrowser.Core is not { } core)
            return;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { return; }
        using var _ = doc;
        var r = doc.RootElement;
        try
        {
            switch (Str(r, "t"))
            {
                case "m":
                    await MouseAsync(core, Str(r, "a"), (int)Num(r, "b"), Num(r, "x"), Num(r, "y"));
                    break;
                case "w":
                    var (vw, vh) = LoginBrowser.ViewportSize;
                    await Cdp(core, "Input.dispatchMouseEvent", new
                    {
                        type = "mouseWheel", x = vw / 2, y = vh / 2, deltaX = 0, deltaY = -Num(r, "d"),
                    });
                    break;
                case "k":
                    await KeyAsync(core, Str(r, "a") == "down", Str(r, "code"), Str(r, "key"));
                    break;
                case "text":
                    var text = Str(r, "s");
                    if (text.Length is > 0 and <= 500)
                        await Cdp(core, "Input.insertText", new { text });
                    break;
                case "quit":
                    LoginBrowser.Close();
                    break;
            }
        }
        catch (Exception)
        {
            // 页面正在跳转 / 窗口正在关：丢掉这一下输入
        }
    }

    private static async Task MouseAsync(CoreWebView2 core, string action, int button, double rx, double ry)
    {
        var (vw, vh) = LoginBrowser.ViewportSize;
        var x = Math.Clamp(rx, 0, 1) * vw;
        var y = Math.Clamp(ry, 0, 1) * vh;
        var (name, bit) = button switch { 1 => ("middle", 4), 2 => ("right", 2), _ => ("left", 1) };
        switch (action)
        {
            case "down":
                _buttons |= bit;
                await Cdp(core, "Input.dispatchMouseEvent", new
                {
                    type = "mousePressed", x, y, button = name, buttons = _buttons, clickCount = 1, modifiers = _modifiers,
                });
                break;
            case "up":
                _buttons &= ~bit;
                await Cdp(core, "Input.dispatchMouseEvent", new
                {
                    type = "mouseReleased", x, y, button = name, buttons = _buttons, clickCount = 1, modifiers = _modifiers,
                });
                break;
            default:
                await Cdp(core, "Input.dispatchMouseEvent", new
                {
                    type = "mouseMoved", x, y, button = "none", buttons = _buttons, modifiers = _modifiers,
                });
                break;
        }
    }

    /// <summary>
    /// 键盘：可打印字符（没按 Ctrl/Alt 时）直接 insertText，与键盘布局、输入法无关；
    /// 其余（Enter / 退格 / Tab / 方向键 / Ctrl+A 之类）按虚拟键码发 rawKeyDown / keyUp。
    /// </summary>
    private static async Task KeyAsync(CoreWebView2 core, bool down, string code, string key)
    {
        var mod = code switch
        {
            "AltLeft" or "AltRight" => 1,
            "ControlLeft" or "ControlRight" => 2,
            "MetaLeft" or "MetaRight" => 4,
            "ShiftLeft" or "ShiftRight" => 8,
            _ => 0,
        };
        if (mod != 0)
        {
            _modifiers = down ? _modifiers | mod : _modifiers & ~mod;
            return;
        }
        var printable = key.Length == 1 && (_modifiers & 3) == 0;
        if (printable)
        {
            if (down)
                await Cdp(core, "Input.insertText", new { text = key });
            return;
        }
        var vk = VirtualKey(code, key);
        if (vk == 0)
            return;
        // Enter 要带 text 才会提交表单
        var text = code is "Enter" or "NumpadEnter" ? "\r" : "";
        await Cdp(core, "Input.dispatchKeyEvent", new
        {
            type = down ? (text.Length > 0 ? "keyDown" : "rawKeyDown") : "keyUp",
            windowsVirtualKeyCode = vk, code, key, text = down ? text : "", modifiers = _modifiers,
        });
    }

    private static int VirtualKey(string code, string key)
    {
        switch (code)
        {
            case "Enter" or "NumpadEnter": return 0x0D;
            case "Backspace": return 0x08;
            case "Tab": return 0x09;
            case "Escape": return 0x1B;
            case "Space": return 0x20;
            case "ArrowLeft": return 0x25;
            case "ArrowUp": return 0x26;
            case "ArrowRight": return 0x27;
            case "ArrowDown": return 0x28;
            case "Delete": return 0x2E;
            case "Home": return 0x24;
            case "End": return 0x23;
            case "PageUp": return 0x21;
            case "PageDown": return 0x22;
        }
        if (code.StartsWith("Key") && code.Length == 4)
            return code[3];
        if (code.StartsWith("Digit") && code.Length == 6)
            return code[5];
        return key.Length == 1 && char.IsAsciiLetterOrDigit(key[0]) ? char.ToUpperInvariant(key[0]) : 0;
    }

    private static Task<string> Cdp(CoreWebView2 core, string method, object args) =>
        core.CallDevToolsProtocolMethodAsync(method, JsonSerializer.Serialize(args));

    private static string Str(JsonElement r, string name) =>
        r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static double Num(JsonElement r, string name) =>
        r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
}
