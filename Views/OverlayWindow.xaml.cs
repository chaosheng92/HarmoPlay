using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using HarmoPlay.Core;
using HarmoPlay.Models;

namespace HarmoPlay.Views;

public partial class OverlayWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    public event EventHandler? SettingsChanged;

    private bool _clickThrough = true;
    private bool _applying;

    public OverlayWindow()
    {
        InitializeComponent();
        Surface.MouseLeftButtonDown += OnSurfaceMouseDown;
        Root.MouseLeftButtonDown += OnSurfaceMouseDown;        // 整块窗口都能拖
        Root.ContextMenuOpening += (_, _) => UpdateMenuHeaders();
        MouseWheel += OnWheel;
        LocationChanged += (_, _) => PersistBounds();
        SizeChanged += (_, _) => PersistBounds();
    }

    /// <summary>右键菜单的文案与状态同步（解锁后右键可切换模式 / 复位 / 隐藏）。</summary>
    private void UpdateMenuHeaders()
    {
        var s = Surface.Settings;
        MenuLock.Header = _clickThrough ? "解锁拖动（也可以按 Alt+L）" : "锁定（点击穿透，不挡游戏）";
        MenuMode.Header = s is { Mode: 1 } ? "切换到经典堆叠模式" : "切换到音游下落模式";
        Surface.Unlocked = !_clickThrough;
        Surface.InvalidateVisual();
    }

    /// <summary>当前窗口扩展样式（0x20 = 点击穿透，诊断用）。</summary>
    public string ExStyleHex => "0x" + GetWindowLong(new WindowInteropHelper(this).Handle, GWL_EXSTYLE).ToString("X8");

    /// <summary>让"锁定 / 解锁"的视觉提示（橙色边框、拖动条、右键菜单文案）与当前状态一致。</summary>
    public void RefreshLockVisual()
    {
        Surface.Unlocked = !_clickThrough;
        UpdateMenuHeaders();
        ApplyClickThrough();
    }

    private void OnMenuLock(object sender, RoutedEventArgs e)
    {
        ClickThrough = !ClickThrough;
        UpdateMenuHeaders();
    }

    private void OnMenuMode(object sender, RoutedEventArgs e)
    {
        if (Surface.Settings is not { } s) return;
        s.Mode = s.Mode == 1 ? 0 : 1;
        UpdateMenuHeaders();
        Surface.InvalidateVisual();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnMenuReset(object sender, RoutedEventArgs e)
    {
        if (Surface.Settings is not { } s) return;
        s.Left = 200;
        s.Top = 120;
        s.Width = 460;
        s.Height = s.Mode == 1 ? 420 : 320;
        ApplySettings(s);
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnMenuHide(object sender, RoutedEventArgs e)
    {
        if (Surface.Settings is not { } s) return;
        s.Visible = false;
        Hide();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public OverlayCanvas Canvas => Surface;

    public void AttachPlayback(PlaybackEngine engine) => Surface.Playback = engine;

    public bool ClickThrough
    {
        get => _clickThrough;
        set
        {
            _clickThrough = value;
            ApplyClickThrough();
            UpdateMenuHeaders();
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyClickThrough();
    }

    private void ApplyClickThrough()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        int style = GetWindowLong(handle, GWL_EXSTYLE);
        style |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        if (_clickThrough) style |= WS_EX_TRANSPARENT;
        else style &= ~WS_EX_TRANSPARENT;
        SetWindowLong(handle, GWL_EXSTYLE, style);
    }

    public void ApplySettings(OverlaySettings s)
    {
        _applying = true;
        try
        {
            Left = s.Left;
            Top = s.Top;
            Width = Math.Max(220, s.Width);
            Height = Math.Max(140, s.Height);
            Opacity = Math.Clamp(s.Opacity, 0.2, 1.0);
            Topmost = true;
            Root.Background = ToBrush(s.Background);
            Surface.Settings = s;
            _clickThrough = s.ClickThrough;
            Surface.Unlocked = !s.ClickThrough;
            ApplyClickThrough();
            UpdateMenuHeaders();
            Surface.InvalidateVisual();
        }
        finally
        {
            _applying = false;
        }
    }

    private static Brush ToBrush(string hex)
    {
        try
        {
            var c = (Color)ColorConverter.ConvertFromString(hex);
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }
        catch
        {
            return new SolidColorBrush(Color.FromArgb(0xCC, 0x10, 0x14, 0x1C));
        }
    }

    private void PersistBounds()
    {
        if (_applying) return;
        if (Surface.Settings is not { } s) return;
        s.Left = Left;
        s.Top = Top;
        s.Width = Width;
        s.Height = Height;
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnSurfaceMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_clickThrough) return;
        if (e.ClickCount == 2)
        {
            ToggleSize();
            return;
        }
        try
        {
            DragMove();
        }
        catch
        {
            // 拖动被取消
        }
    }

    private void ToggleSize()
    {
        var s = Surface.Settings;
        if (s == null) return;
        _applying = true;
        try
        {
            if (Math.Abs(s.Width - 460) < 1)
            {
                Width = 600;
                Height = s.Mode == 1 ? 520 : 420;
            }
            else
            {
                Width = 460;
                Height = s.Mode == 1 ? 420 : 320;
            }
            s.Width = Width;
            s.Height = Height;
        }
        finally
        {
            _applying = false;
        }
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (_clickThrough) return;
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        double factor = e.Delta > 0 ? 1.08 : 1 / 1.08;
        Width = Math.Clamp(Width * factor, 240, 1400);
        Height = Math.Clamp(Height * factor, 150, 900);
        e.Handled = true;
    }
}

/// <summary>
/// 悬浮窗绘制层，两种模式：
///   经典堆叠：8 条通道，音符块从下往上堆，最下面的是当前该弹的音；
///   音游下落：音符按节拍从上往下落，落到最下面的判定线时按键。
/// </summary>
public sealed class OverlayCanvas : FrameworkElement
{
    private static readonly Typeface UiFace = new("Microsoft YaHei UI");
    private static readonly Typeface MonoFace = new("Consolas");

    private readonly DispatcherTimer _timer;

    public OverlayCanvas()
    {
        SnapsToDevicePixels = true;
        _timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16),
        };
        _timer.Tick += (_, _) =>
        {
            if (Settings.Mode == 1 && Playback is { IsRunning: true }) InvalidateVisual();
        };
        _timer.Start();
    }

    public ParsedScore? Score { get; set; }
    public KeyMap Map { get; set; } = KeyMap.CreateDelta();
    public int CurrentIndex { get; set; } = -1;
    public OverlaySettings Settings { get; set; } = new();
    public string StatusText { get; set; } = "";
    public bool Paused { get; set; }
    public PlaybackEngine? Playback { get; set; }
    /// <summary>解锁状态（可拖动）：显示橙色边框与拖动提示。</summary>
    public bool Unlocked { get; set; }
    /// <summary>连击数（音游模式显示）。</summary>
    public int Combo { get; set; }

    private int _flashLane = -1;
    private DateTime _flashUntil = DateTime.MinValue;
    private int _lastIndex = -1;

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 4 || h <= 4) return;

        double headerH = Settings.ShowTitle ? 24 : 6;
        double footerH = 26;
        double lanesTop = headerH + 2;
        double lanesBottom = h - footerH;

        DrawHeader(dc, w, headerH);
        if (Settings.ShowLanes)
        {
            if (Settings.Mode == 1) DrawFalling(dc, w, lanesTop, lanesBottom);
            else DrawStacked(dc, w, lanesTop, lanesBottom);
        }
        if (Playback is { CountdownValue: > 0 }) DrawCountdown(dc, w, lanesTop, lanesBottom);
        DrawFooter(dc, w, h);
        if (Unlocked) DrawUnlockedFrame(dc, w, h);

        if (Score == null || Score.Notes.Count == 0)
        {
            var t = Text("选择一首曲谱后，这里会显示键位提示", 13, Brush("#7A8798"), UiFace);
            dc.DrawText(t, new Point((w - t.Width) / 2, (h - t.Height) / 2));
        }
    }

    // ------------------------------------------------------------ 头部 / 尾部

    private void DrawHeader(DrawingContext dc, double w, double headerH)
    {
        if (!Settings.ShowTitle) return;
        var title = Score?.Title;
        if (string.IsNullOrWhiteSpace(title)) title = "口琴谱演奏器";
        var bpm = Score?.Bpm > 0 ? $"{Score!.Bpm:0.#} BPM" : "";
        var mode = Settings.Mode == 1 ? "音游下落" : "经典堆叠";
        var play = Playback is { IsRunning: true } && Playback.Notation == NotationKind.Pitch ? "" : "";
        _ = play;
        var modeName = Playback?.FollowMode == true ? "跟谱" : "自动";
        var index = Playback is { IsRunning: true } ? Playback.CurrentIndex : CurrentIndex;
        var pos = Score != null && Score.Notes.Count > 0 ? $"{Math.Max(index + 1, 0)}/{Score.Notes.Count}" : "";

        var t = Text(title, 13, Brush("#E6EAF2"), UiFace, bold: true);
        dc.DrawText(t, new Point(2, 3));

        var right = Text($"{modeName}·{mode}  {bpm}  {pos}", 11, Brush("#8FA0B5"), MonoFace);
        dc.DrawText(right, new Point(Math.Max(2, w - right.Width - 2), 5));

        dc.DrawLine(new Pen(Brush("#26FFFFFF"), 1), new Point(0, headerH - 3), new Point(w, headerH - 3));
    }

    private void DrawFooter(DrawingContext dc, double w, double h)
    {
        string text;
        var brush = Brush("#9AA4B5");
        int index = Playback is { IsRunning: true } ? Playback.CurrentIndex : CurrentIndex;

        if (Paused || Playback is { IsPaused: true })
        {
            text = "已暂停 —— 按热键继续";
        }
        else if (!string.IsNullOrEmpty(StatusText))
        {
            text = StatusText;
        }
        else if (Score != null && index >= 0 && index < Score.Notes.Count)
        {
            var n = Score.Notes[index];
            var chord = Chord.Resolve(n, Map, Score.Notation);
            text = n.IsRest ? "休止" : $"现在弹：{chord.Detail}   {n.Beats:0.##} 拍";
            brush = Brush(chord.Color);
        }
        else if (Settings.Mode == 1)
        {
            text = "Alt+1 开始，音符落到判定线时按键";
        }
        else
        {
            text = "Alt+1 播放/暂停    Alt+2 停止    Alt+3 下一首";
        }

        var ft = Text(text, 12, brush, UiFace);
        dc.DrawText(ft, new Point(2, h - ft.Height - 3));

        if (Score != null && Score.Notes.Count > 0)
        {
            double progress;
            if (Playback is { IsRunning: true } && Playback.TotalMs > 0)
                progress = Math.Clamp(Playback.PositionMs / Playback.TotalMs, 0, 1);
            else
                progress = Math.Clamp((index + 1) / (double)Score.Notes.Count, 0, 1);
            dc.DrawRectangle(Brush("#22FFFFFF"), null, new Rect(0, h - 3, w, 3));
            dc.DrawRectangle(Brush("#FF4A9DFF"), null, new Rect(0, h - 3, w * progress, 3));
        }
    }

    // ------------------------------------------------------------ 经典堆叠

    private void DrawStacked(DrawingContext dc, double w, double top, double bottom)
    {
        const int lanes = 8;
        double gap = 3;
        double laneW = (w - gap * (lanes - 1)) / lanes;
        double laneH = Math.Max(10, bottom - top);

        for (int i = 0; i < lanes; i++)
        {
            double x = i * (laneW + gap);
            dc.DrawRoundedRectangle(Brush("#22FFFFFF"), null, new Rect(x, top, laneW, laneH), 5, 5);
        }

        if (Score == null) return;

        int now = Playback is { IsRunning: true } && Playback.CurrentIndex >= 0 ? Playback.CurrentIndex : Math.Max(CurrentIndex, 0);
        var perLane = new List<ScoreNote>[lanes];
        for (int i = 0; i < lanes; i++) perLane[i] = new List<ScoreNote>();
        int maxStack = Math.Max(1, Settings.MaxStack);

        for (int i = now; i < Score.Notes.Count; i++)
        {
            var n = Score.Notes[i];
            if (n.IsRest) continue;
            int lane = Math.Clamp(n.Degree, 1, 8) - 1;
            if (perLane[lane].Count >= maxStack) continue;
            perLane[lane].Add(n);
            if (perLane.All(l => l.Count >= maxStack)) break;
        }

        for (int i = 0; i < lanes; i++)
        {
            double x = i * (laneW + gap);
            double y = bottom;
            foreach (var n in perLane[i])
            {
                var chord = Chord.Resolve(n, Map, Score.Notation);
                double blockH = Math.Clamp(16 + n.Beats * 8, 18, 44);
                y -= blockH;
                var rect = new Rect(x + 1, y, laneW - 2, blockH - 3);
                DrawBlock(dc, rect, chord, n.Index == CurrentIndex || n.Index == now, Settings.ShowKeyLetters);
                y -= 3;
                if (y < top) break;
            }
        }

        dc.DrawLine(new Pen(Brush("#664A9DFF"), 2), new Point(0, bottom + 1), new Point(w, bottom + 1));

        if (now >= 0 && now < Score.Notes.Count && !Score.Notes[now].IsRest)
        {
            int cur = Math.Clamp(Score.Notes[now].Degree, 1, 8) - 1;
            double x = cur * (laneW + gap);
            dc.DrawRoundedRectangle(null, new Pen(Brush("#804A9DFF"), 1.5), new Rect(x, top, laneW, laneH), 5, 5);
        }
    }

    // ------------------------------------------------------------ 音游下落

    private void DrawFalling(DrawingContext dc, double w, double top, double bottom)
    {
        const int lanes = 8;
        double gap = 3;
        double laneW = (w - gap * (lanes - 1)) / lanes;

        for (int i = 0; i < lanes; i++)
        {
            double x = i * (laneW + gap);
            dc.DrawRoundedRectangle(Brush("#18FFFFFF"), null, new Rect(x, top, laneW, Math.Max(10, bottom - top)), 5, 5);
            dc.DrawLine(new Pen(Brush("#14FFFFFF"), 1), new Point(x + laneW + gap / 2, top), new Point(x + laneW + gap / 2, bottom));
        }

        // 判定线
        if (Settings.ShowJudgmentLine)
        {
            dc.DrawRectangle(Brush("#334A9DFF"), null, new Rect(0, bottom - 2, w, 4));
            dc.DrawLine(new Pen(Brush("#CC4A9DFF"), 2), new Point(0, bottom), new Point(w, bottom));
        }

        if (Score == null || Score.Notes.Count == 0) return;

        bool playing = Playback is { IsRunning: true };
        double nowMs = playing ? Playback!.PositionMs : 0;
        double beatMs = playing && Playback!.BeatMs > 0 ? Playback.BeatMs : Score.Bpm > 0 ? 60000 / Score.Bpm : 500;
        double speed = Math.Max(40, Settings.FallSpeed);
        double lookAheadMs = Math.Max(0.5, Settings.LookAheadSeconds) * 1000;
        int currentIndex = playing ? Playback!.CurrentIndex : CurrentIndex;
        var notation = Score.Notation;

        // 命中闪光：当前音变化时在判定线上亮一下
        if (playing && currentIndex != _lastIndex)
        {
            _lastIndex = currentIndex;
            if (currentIndex >= 0 && currentIndex < Score.Notes.Count && !Score.Notes[currentIndex].IsRest)
            {
                _flashLane = Math.Clamp(Score.Notes[currentIndex].Degree, 1, 8) - 1;
                _flashUntil = DateTime.Now.AddMilliseconds(220);
            }
        }

        // 已经过去的音：保留 250ms 的余韵
        foreach (var n in Score.Notes)
        {
            if (n.IsRest) continue;
            double startMs = n.StartBeat * beatMs;
            double endMs = startMs + n.Beats * beatMs;
            if (endMs < nowMs - 250) continue;
            if (startMs > nowMs + lookAheadMs) break;

            int lane = Math.Clamp(n.Degree, 1, 8) - 1;
            double x = lane * (laneW + gap);

            double yBottom = bottom - (startMs - nowMs) / 1000.0 * speed;
            double yTop = bottom - (endMs - nowMs) / 1000.0 * speed;
            if (yBottom < top) continue;
            // 判定线以下的余韵：贴住判定线，最多再露出一小截
            double clippedBottom = Math.Min(yBottom, bottom + 6);
            yTop = Math.Max(Math.Min(yTop, clippedBottom - 2), top);
            double blockH = Math.Max(6, clippedBottom - yTop);
            if (blockH < 1) continue;
            var rect = new Rect(x + 1, yTop, laneW - 2, blockH);

            var chord = Chord.Resolve(n, Map, notation);
            bool isCurrent = n.Index == currentIndex;
            bool passed = endMs < nowMs;
            DrawBlock(dc, rect, chord, isCurrent, Settings.ShowFallKeyHint && Settings.ShowKeyLetters, dimmed: passed);
        }

        // 判定点提示：当前该按的键
        if (currentIndex >= 0 && currentIndex < Score.Notes.Count && !Score.Notes[currentIndex].IsRest)
        {
            var n = Score.Notes[currentIndex];
            int lane = Math.Clamp(n.Degree, 1, 8) - 1;
            double x = lane * (laneW + gap);
            dc.DrawRoundedRectangle(null, new Pen(Brush("#AAFFFFFF"), 2),
                new Rect(x + 1, bottom - 26, laneW - 2, 24), 4, 4);
        }
        else if (!playing)
        {
            var t = Text("按 Alt+1 开始下落", 12, Brush("#7A8798"), UiFace);
            dc.DrawText(t, new Point((w - t.Width) / 2, top + 4));
        }

        // 命中闪光 + 连击
        if (_flashLane >= 0 && DateTime.Now < _flashUntil)
        {
            double x = _flashLane * (laneW + gap);
            var glow = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));
            glow.Freeze();
            dc.DrawRoundedRectangle(glow, null, new Rect(x + 1, bottom - 10, laneW - 2, 14), 4, 4);
        }

        if (Combo >= 3)
        {
            var c = Text($"{Combo} COMBO", 15, Brush("#FFFFD23F"), MonoFace, bold: true);
            dc.DrawText(c, new Point(Math.Max(4, (w - c.Width) / 2), top + 4));
        }
    }

    // ------------------------------------------------------------ 开播倒计时

    private void DrawCountdown(DrawingContext dc, double w, double top, double bottom)
    {
        int value = Playback?.CountdownValue ?? 0;
        if (value <= 0) return;

        double h = Math.Max(20, bottom - top);
        dc.DrawRoundedRectangle(Brush("#B8000000"), null, new Rect(0, top, w, h), 6, 6);

        var big = Text(value.ToString(), Math.Min(w, h) * 0.6, Brush("#FFD23F"), UiFace, bold: true);
        dc.DrawText(big, new Point((w - big.Width) / 2, top + (h - big.Height) / 2 - 12));

        var mode = Playback?.FollowMode == true ? "跟谱弹奏：等你按对再走" : "自动弹奏：程序即将按键";
        var tip = Text($"{value} 秒后开始 · {mode}", 13, Brush("#E6EAF2"), UiFace);
        double tipY = top + (h + big.Height) / 2 - 6;
        if (tipY + tip.Height < bottom)
            dc.DrawText(tip, new Point((w - tip.Width) / 2, tipY));
    }

    // ------------------------------------------------------------ 解锁可视化

    private void DrawUnlockedFrame(DrawingContext dc, double w, double h)
    {
        dc.DrawRoundedRectangle(null, new Pen(Brush("#CCFFB300"), 2), new Rect(1, 1, w - 2, h - 2), 9, 9);
        double barH = 20;
        dc.DrawRoundedRectangle(Brush("#33FFB300"), null, new Rect(1, 1, w - 2, barH), 9, 9);
        var grip = Text("≡  按住这里拖动我（鼠标放到游戏里也行）", 11, Brush("#FFFFC94D"), UiFace, bold: true);
        dc.DrawText(grip, new Point(Math.Max(4, (w - grip.Width) / 2), 3));
        var hint = Text("Ctrl+滚轮缩放 · 右键菜单 · Alt+L 锁定", 10, Brush("#B3FFC94D"), UiFace);
        dc.DrawText(hint, new Point(Math.Max(4, (w - hint.Width) / 2), h - hint.Height - 18));
    }

    // ------------------------------------------------------------ 公共绘制

    private void DrawBlock(DrawingContext dc, Rect rect, Chord chord, bool highlight, bool showKey, bool dimmed = false)
    {
        var color = ParseColor(chord.Color);
        byte alpha = dimmed ? (byte)0x55 : highlight ? (byte)0xF2 : (byte)0xC0;
        var fill = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
        fill.Freeze();

        var pen = highlight
            ? new Pen(Brushes.White, 2)
            : new Pen(new SolidColorBrush(Color.FromArgb(dimmed ? (byte)0x30 : (byte)0x70, 255, 255, 255)), 1);

        dc.DrawRoundedRectangle(fill, pen, rect, 4, 4);

        if (!showKey || rect.Width <= 14 || rect.Height < 11) return;
        var label = chord.Text;
        var ft = Text(label, Math.Min(14, rect.Width / 2.2), Brushes.White, MonoFace, bold: true);
        if (ft.Height + 2 <= rect.Height)
            dc.DrawText(ft, new Point(rect.X + (rect.Width - ft.Width) / 2, rect.Y + (rect.Height - ft.Height) / 2));
    }

    private FormattedText Text(string text, double size, Brush brush, Typeface face, bool bold = false)
    {
        return new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            bold ? new Typeface(face.FontFamily, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal) : face,
            size,
            brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
    }

    private static SolidColorBrush Brush(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    private static Color ParseColor(string hex)
    {
        try
        {
            return (Color)ColorConverter.ConvertFromString(hex);
        }
        catch
        {
            return Colors.White;
        }
    }
}
