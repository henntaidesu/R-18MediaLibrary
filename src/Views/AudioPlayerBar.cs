using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using R18MediaLibrary.Core;
using NAudio.Wave;

namespace R18MediaLibrary.Views;

/// <summary>
/// 底部音频播放条：播放/暂停 + 播放列表 + 文件名 + （⏮ 进度条 ⏭）+ 时间 + 关闭。
///
/// 用 NAudio 播放（替代 WPF MediaElement），以便对每个文件施加任意增益做响度归一：
/// 播放前测量文件 RMS 响度，算出把它拉到统一目标（约 -18 dBFS）所需的增益，并做峰值限幅防削波，
/// 这样大声/小声的文件都落在同一响度。增益按 路径|大小|修改时间 缓存，避免重复扫描大文件。
/// 同文件夹的音频组成播放队列，支持上一曲/下一曲、自动续播与播放列表跳转。
/// </summary>
public class AudioPlayerBar : UserControl
{
    private const double TargetDbfs = -18.0;   // 响度归一目标（dBFS，RMS 近似）
    private const double PeakLimit = 0.97;      // 增益后峰值上限，防削波
    private const string PlayIcon = "▶";
    private const string PauseIcon = "⏸";
    private const string SleepIcon = "⏱";
    // 睡眠定时预设：分钟（0=关闭）。到点暂停当前音频，保留进度。
    private static readonly (string Label, int Minutes)[] SleepPresets =
        [("关闭定时", 0), ("15 分钟", 15), ("30 分钟", 30), ("45 分钟", 45), ("60 分钟", 60), ("90 分钟", 90)];

    private static readonly ConcurrentDictionary<string, float> GainCache = new();

    private readonly Button _playButton = new() { Content = PlayIcon, Width = 48, VerticalAlignment = VerticalAlignment.Center, ToolTip = "播放 / 暂停" };
    private readonly Button _playlistButton = new() { Content = "☰", Width = 40, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0), ToolTip = "播放列表" };
    private readonly Button _sleepButton = new() { Content = SleepIcon, Width = 56, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0), ToolTip = "睡眠定时" };
    private readonly Button _prevButton = new() { Content = "⏮", Width = 40, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), ToolTip = "上一曲" };
    private readonly Button _nextButton = new() { Content = "⏭", Width = 40, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), ToolTip = "下一曲" };
    private readonly TextBlock _title = new()
    {
        VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis,
        Margin = new Thickness(12, 0, 12, 0),
    };
    private readonly Slider _slider = new() { Minimum = 0, Maximum = 0, IsMoveToPointEnabled = true };
    private readonly TextBlock _time = new()
    {
        Text = "00:00 / 00:00", VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(8, 0, 8, 0),
    };
    private readonly Button _closeButton = new()
    {
        Content = "✕", Width = 32, VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(8, 0, 0, 0), ToolTip = "关闭",
    };
    private readonly Popup _playlistPopup = new() { Placement = PlacementMode.Top, StaysOpen = false, AllowsTransparency = true };
    private readonly StackPanel _playlistPanel = new();
    private readonly Popup _sleepPopup = new() { Placement = PlacementMode.Top, StaysOpen = false, AllowsTransparency = true };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _sleepTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTime? _sleepUntil;   // 睡眠定时到点时刻；null=未设定

    private WaveOutEvent? _output;
    private AudioFileReader? _reader;
    private GainSampleProvider? _gain;
    private int _trackGen;

    private bool _playing;
    private bool _dragging;
    private bool _hasMedia;

    private List<(string Path, string Title)> _queue = [];
    private int _index = -1;

    public AudioPlayerBar()
    {
        Visibility = Visibility.Collapsed;

        var border = new Border
        {
            Background = TryFindResource("BaseBrush") as Brush ?? Brushes.Black,
            BorderBrush = TryFindResource("BorderBrush") as Brush ?? Brushes.Gray,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(12, 10, 12, 10),   // Web .abar
        };
        var bar = new Grid();
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });            // 播放/暂停
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });            // 播放列表
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });        // 文件名
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });            // 上一曲
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // 进度条
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });            // 下一曲
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });            // 时间
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });            // 睡眠定时
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });            // 关闭

        _playButton.Click += (_, _) => Toggle();
        Grid.SetColumn(_playButton, 0);
        bar.Children.Add(_playButton);

        _playlistButton.Click += (_, _) => TogglePlaylist();
        Grid.SetColumn(_playlistButton, 1);
        bar.Children.Add(_playlistButton);

        if (TryFindResource("TextBrush") is Brush textBrush)
            _title.Foreground = textBrush;
        Grid.SetColumn(_title, 2);
        bar.Children.Add(_title);

        _prevButton.Click += (_, _) => Previous();
        Grid.SetColumn(_prevButton, 3);
        bar.Children.Add(_prevButton);

        _slider.VerticalAlignment = VerticalAlignment.Center;
        _slider.PreviewMouseDown += (_, _) => _dragging = true;
        _slider.PreviewMouseUp += (_, _) =>
        {
            if (_reader != null)
                _reader.CurrentTime = TimeSpan.FromMilliseconds(Math.Clamp(_slider.Value, 0, _slider.Maximum));
            _dragging = false;
        };
        Grid.SetColumn(_slider, 4);
        bar.Children.Add(_slider);

        _nextButton.Click += (_, _) => Next();
        Grid.SetColumn(_nextButton, 5);
        bar.Children.Add(_nextButton);

        if (TryFindResource("CaptionText") is Style captionStyle)
            _time.Style = captionStyle;
        Grid.SetColumn(_time, 6);
        bar.Children.Add(_time);

        _sleepButton.Click += (_, _) => ToggleSleepMenu();
        Grid.SetColumn(_sleepButton, 7);
        bar.Children.Add(_sleepButton);

        _closeButton.Click += (_, _) => Stop();
        Grid.SetColumn(_closeButton, 8);
        bar.Children.Add(_closeButton);

        border.Child = bar;
        Content = border;

        BuildPlaylistPopup();
        BuildSleepPopup();

        _sleepTimer.Tick += (_, _) => SleepTick();

        _timer.Tick += (_, _) =>
        {
            if (_reader == null)
                return;
            if (!_dragging)
                _slider.Value = _reader.CurrentTime.TotalMilliseconds;
            _time.Text = $"{Fmt(_reader.CurrentTime)} / {Fmt(_reader.TotalTime)}";
        };
    }

    // ---------- 播放列表弹窗 ----------

    private void BuildPlaylistPopup()
    {
        var box = new Border
        {
            Background = TryFindResource("BaseBrush") as Brush ?? Brushes.Black,
            BorderBrush = TryFindResource("BorderBrush") as Brush ?? Brushes.Gray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),   // Web .apopup
            MinWidth = 300,
            MaxHeight = 360,
            Margin = new Thickness(0, 0, 0, 6),
        };
        box.Child = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _playlistPanel,
            Padding = new Thickness(6),
        };
        _playlistPopup.Child = box;
        _playlistPopup.PlacementTarget = _playlistButton;
    }

    private void TogglePlaylist()
    {
        if (_playlistPopup.IsOpen)
        {
            _playlistPopup.IsOpen = false;
            return;
        }
        RefreshPlaylist();
        _playlistPopup.IsOpen = true;
    }

    private void RefreshPlaylist()
    {
        _playlistPanel.Children.Clear();
        var accent = TryFindResource("AccentBrush") as Brush ?? Brushes.SteelBlue;   // .aprow.cur：accent-dim
        var caption = TryFindResource("CaptionBrush") as Brush ?? Brushes.Gray;
        for (var i = 0; i < _queue.Count; i++)
        {
            var current = i == _index;
            var row = new Border
            {
                Background = current ? accent : Brushes.Transparent,
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 8, 10, 8),   // .aprow：内距 8/10
                Margin = new Thickness(0, 1, 0, 1),
                Cursor = Cursors.Hand,
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var num = new TextBlock
            {
                Text = current ? "▶" : (i + 1).ToString(),
                Foreground = current ? Brushes.White : caption,
                VerticalAlignment = VerticalAlignment.Center,
            };
            grid.Children.Add(num);
            var name = new TextBlock
            {
                Text = _queue[i].Title,
                Foreground = current ? Brushes.White : (TryFindResource("TextBrush") as Brush ?? Brushes.White),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(name, 1);
            grid.Children.Add(name);
            row.Child = grid;
            var idx = i;
            row.MouseLeftButtonUp += (_, _) => JumpTo(idx);
            if (!current)
            {
                row.MouseEnter += (_, _) => row.Background = TryFindResource("ButtonBrush") as Brush ?? Brushes.DimGray;
                row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
            }
            _playlistPanel.Children.Add(row);
        }
        if (_queue.Count == 0)
            _playlistPanel.Children.Add(new TextBlock { Text = I18n.Tr("播放列表为空"), Margin = new Thickness(10), Foreground = caption });
    }

    private void JumpTo(int i)
    {
        if (i < 0 || i >= _queue.Count)
            return;
        _playlistPopup.IsOpen = false;
        _index = i;
        PlayCurrent();
    }

    // ---------- 睡眠定时 ----------

    private void BuildSleepPopup()
    {
        var panel = new StackPanel { MinWidth = 160 };
        var caption = TryFindResource("CaptionBrush") as Brush ?? Brushes.Gray;
        var text = TryFindResource("TextBrush") as Brush ?? Brushes.White;
        foreach (var (label, minutes) in SleepPresets)
        {
            var row = new Border
            {
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 7, 12, 7),
                Margin = new Thickness(0, 1, 0, 1),
                Cursor = Cursors.Hand,
                Background = Brushes.Transparent,
            };
            row.Child = new TextBlock
            {
                Text = I18n.Tr(label),
                Foreground = minutes == 0 ? caption : text,
            };
            var m = minutes;
            row.MouseLeftButtonUp += (_, _) => { _sleepPopup.IsOpen = false; SetSleep(m); };
            row.MouseEnter += (_, _) => row.Background = TryFindResource("ButtonBrush") as Brush ?? Brushes.DimGray;
            row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
            panel.Children.Add(row);
        }
        var box = new Border
        {
            Background = TryFindResource("BaseBrush") as Brush ?? Brushes.Black,
            BorderBrush = TryFindResource("BorderBrush") as Brush ?? Brushes.Gray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),   // Web .apopup
            Padding = new Thickness(6),
            Margin = new Thickness(0, 0, 0, 6),
            Child = panel,
        };
        _sleepPopup.Child = box;
        _sleepPopup.PlacementTarget = _sleepButton;
    }

    private void ToggleSleepMenu() => _sleepPopup.IsOpen = !_sleepPopup.IsOpen;

    /// <summary>设定睡眠定时（分钟，0=取消）。到点暂停当前音频并保留进度。</summary>
    private void SetSleep(int minutes)
    {
        if (minutes <= 0)
        {
            ClearSleep();
            return;
        }
        _sleepUntil = DateTime.Now.AddMinutes(minutes);
        _sleepTimer.Start();
        UpdateSleepButton();
    }

    private void ClearSleep()
    {
        _sleepTimer.Stop();
        _sleepUntil = null;
        _sleepButton.Content = SleepIcon;
        _sleepButton.ToolTip = "睡眠定时";
    }

    private void SleepTick()
    {
        if (_sleepUntil == null)
        {
            _sleepTimer.Stop();
            return;
        }
        var remain = _sleepUntil.Value - DateTime.Now;
        if (remain <= TimeSpan.Zero)
        {
            ClearSleep();
            // 到点暂停（保留进度，可继续播放），不触发自动续播
            if (_playing && _output != null)
            {
                _output.Pause();
                _playing = false;
                _playButton.Content = PlayIcon;
            }
            return;
        }
        UpdateSleepButton();
    }

    private void UpdateSleepButton()
    {
        if (_sleepUntil == null)
            return;
        var remain = _sleepUntil.Value - DateTime.Now;
        if (remain < TimeSpan.Zero)
            remain = TimeSpan.Zero;
        _sleepButton.Content = Fmt(remain);
        _sleepButton.ToolTip = $"将在 {Fmt(remain)} 后暂停";
    }

    // ---------- 播放控制 ----------

    /// <summary>播放单个音频文件（无队列）。</summary>
    public void Play(string path, string title) => PlayQueue([(path, title)], 0);

    /// <summary>以播放队列方式播放：传入同文件夹的音频列表与起始索引，支持上一曲/下一曲与自动续播。</summary>
    public void PlayQueue(IReadOnlyList<(string Path, string Title)> queue, int index)
    {
        _queue = [.. queue];
        _index = _queue.Count == 0 ? -1 : Math.Clamp(index, 0, _queue.Count - 1);
        if (_index < 0)
            return;
        PlayCurrent();
    }

    private void PlayCurrent()
    {
        var (path, title) = _queue[_index];
        var gen = ++_trackGen;
        DisposePlayer();
        Visibility = Visibility.Visible;
        _title.Text = title;
        _title.ToolTip = title;
        try
        {
            _reader = new AudioFileReader(path);
        }
        catch (Exception)
        {
            _reader = null;
            Next();   // 打开失败：跳到下一曲，避免卡在坏文件
            return;
        }
        _gain = new GainSampleProvider(_reader) { Gain = 1f };
        _output = new WaveOutEvent();
        _output.PlaybackStopped += OnPlaybackStopped;
        _output.Init(_gain);

        _slider.Maximum = _reader.TotalTime.TotalMilliseconds;   // 时长开头即已知，进度条范围正确
        _slider.Value = 0;
        _time.Text = $"00:00 / {Fmt(_reader.TotalTime)}";

        ApplyGain(path, gen);

        _output.Play();
        _playing = true;
        _hasMedia = true;
        _playButton.Content = PauseIcon;
        UpdateNavButtons();
        _timer.Start();
        if (_playlistPopup.IsOpen)
            RefreshPlaylist();
    }

    private void ApplyGain(string path, int gen)
    {
        var key = CacheKey(path);
        if (GainCache.TryGetValue(key, out var cached))
        {
            if (_gain != null)
                _gain.Gain = cached;
            return;
        }
        Task.Run(() =>
        {
            var g = Measure(path);
            GainCache[key] = g;
            Dispatcher.Invoke(() =>
            {
                if (gen == _trackGen && _gain != null)
                    _gain.Gain = g;
            });
        });
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (sender == _output)
            Next();   // 仅自然播放结束会走到这里：自动续播下一曲
    }

    private void Next()
    {
        if (_index >= 0 && _index < _queue.Count - 1)
        {
            _index++;
            PlayCurrent();
        }
        else
        {
            _playing = false;
            _playButton.Content = PlayIcon;
            _timer.Stop();
        }
    }

    private void Previous()
    {
        if (!_hasMedia || _reader == null)
            return;
        if (_reader.CurrentTime.TotalSeconds > 3 || _index <= 0)
        {
            _reader.CurrentTime = TimeSpan.Zero;
            if (!_playing)
                Toggle();
            return;
        }
        _index--;
        PlayCurrent();
    }

    private void UpdateNavButtons()
    {
        _prevButton.IsEnabled = _queue.Count > 1;
        _nextButton.IsEnabled = _index < _queue.Count - 1;
    }

    private void Toggle()
    {
        if (!_hasMedia || _output == null || _reader == null)
            return;
        if (_playing)
        {
            _output.Pause();
            _playing = false;
            _playButton.Content = PlayIcon;
        }
        else
        {
            if (_reader.CurrentTime >= _reader.TotalTime)
                _reader.CurrentTime = TimeSpan.Zero;
            _output.Play();
            _playing = true;
            _playButton.Content = PauseIcon;
            _timer.Start();
        }
    }

    /// <summary>停止播放并隐藏播放条，可重复调用。</summary>
    public void Stop()
    {
        _timer.Stop();
        _playlistPopup.IsOpen = false;
        _sleepPopup.IsOpen = false;
        ClearSleep();
        DisposePlayer();
        _queue = [];
        _index = -1;
        _playing = false;
        _hasMedia = false;
        _playButton.Content = PlayIcon;
        Visibility = Visibility.Collapsed;
    }

    private void DisposePlayer()
    {
        if (_output != null)
        {
            _output.PlaybackStopped -= OnPlaybackStopped;
            try { _output.Stop(); _output.Dispose(); }
            catch (Exception) { /* 关闭设备异常忽略 */ }
            _output = null;
        }
        if (_reader != null)
        {
            try { _reader.Dispose(); }
            catch (Exception) { /* 释放异常忽略 */ }
            _reader = null;
        }
        _gain = null;
    }

    // ---------- 响度测量 ----------

    private static float Measure(string path)
    {
        try
        {
            using var reader = new AudioFileReader(path);
            var buffer = new float[reader.WaveFormat.SampleRate * Math.Max(1, reader.WaveFormat.Channels)];
            double sumSquares = 0;
            long count = 0;
            float peak = 0;
            int read;
            while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
                for (var i = 0; i < read; i++)
                {
                    var s = buffer[i];
                    sumSquares += (double)s * s;
                    var abs = Math.Abs(s);
                    if (abs > peak)
                        peak = abs;
                    count++;
                }
            if (count == 0)
                return 1f;
            var rms = Math.Sqrt(sumSquares / count);
            if (rms < 1e-6)
                return 1f;
            var gainDb = TargetDbfs - 20 * Math.Log10(rms);
            var gain = Math.Pow(10, gainDb / 20);
            if (peak > 1e-6 && peak * gain > PeakLimit)
                gain = PeakLimit / peak;
            return (float)Math.Clamp(gain, 0.05, 8.0);
        }
        catch (Exception)
        {
            return 1f;
        }
    }

    private static string CacheKey(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            return $"{path}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";
        }
        catch (Exception)
        {
            return path;
        }
    }

    private static string Fmt(TimeSpan time) =>
        $"{(int)time.TotalMinutes:D2}:{time.Seconds:D2}";

    /// <summary>对采样流施加线性增益并安全限幅在 [-1, 1]。</summary>
    private sealed class GainSampleProvider(ISampleProvider source) : ISampleProvider
    {
        public float Gain { get; set; } = 1f;
        public WaveFormat WaveFormat => source.WaveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            var read = source.Read(buffer, offset, count);
            var g = Gain;
            if (g != 1f)
                for (var i = 0; i < read; i++)
                {
                    var s = buffer[offset + i] * g;
                    buffer[offset + i] = s > 1f ? 1f : s < -1f ? -1f : s;
                }
            return read;
        }
    }
}
