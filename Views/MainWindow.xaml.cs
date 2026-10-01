using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using HarmoPlay.Core;
using HarmoPlay.Models;
using Microsoft.Win32;

namespace HarmoPlay.Views;

public partial class MainWindow : Window
{
    public sealed class FolderChoice
    {
        public string? Id { get; init; }
        public string Name { get; init; } = "";
        public override string ToString() => Name;
    }

    public sealed class MapRow
    {
        public string Slot { get; init; } = "";
        public string Key { get; init; } = "";
        public string Detail { get; init; } = "";
    }

    public sealed class HotkeyRow
    {
        public HotkeySpec Spec { get; init; } = new();
        public string Desc => HotkeyActions.Describe(Spec.Action);
        public string Text => Spec.Text;
    }

    private readonly Library _lib;
    private readonly PlaybackEngine _engine = new();
    private readonly HotkeyManager _hotkeys = new();
    private OverlayWindow? _overlay;
    private ParsedScore? _parsed;
    private Song? _current;
    private List<Song> _viewSongs = new();
    private readonly List<HotkeyRow> _hotkeyRows = new();
    private bool _loading = true;
    private bool _dirty;
    private string _lastDownloadUrl = "";

    public MainWindow()
    {
        InitializeComponent();
        _lib = LibraryStore.Load();

        BuildUi();
        InitHotkeys();
        InitOverlay();
        RefreshFolders();
        RefreshSongs();
        UpdateKeyMapUi();

        _engine.NoteStarted += i => Dispatcher.InvokeAsync(() => OnNoteStarted(i));
        _engine.NoteFinished += i => Dispatcher.InvokeAsync(() => OnNoteFinished(i));
        _engine.Progress += (i, total) => Dispatcher.InvokeAsync(() =>
        {
            PrgSong.Value = total <= 0 ? 0 : (i + 1) * 100.0 / total;
        });
        _engine.Status += text => Dispatcher.InvokeAsync(() => SetStatus(text));
        _engine.Finished += () => Dispatcher.InvokeAsync(() =>
        {
            SetStatus("演奏完成");
            UpdatePlayButton();
        });
        _engine.PassFinished += p => Dispatcher.InvokeAsync(() => SetStatus($"第 {p} 遍完成"));

        _loading = false;
        Loaded += (_, _) =>
        {
            if (ListSongs.Items.Count > 0 && ListSongs.SelectedIndex < 0)
                ListSongs.SelectedIndex = 0;
        };
    }

    // ================================================================ 初始化

    private void BuildUi()
    {
        Title = "口琴谱演奏器 HarmoPlay v1.0";
        TxtVersion.Text = "v1.0 · 数据目录 " + LibraryStore.DataDir;
        TxtDataPath.Text = LibraryStore.DataDir;

        var p = _lib.Settings.Playback;
        TxtBpm.Text = "";
        SldSpeed.Value = Math.Clamp(p.Speed, 0.2, 2.0);
        TxtSpeed.Text = p.Speed.ToString("0.00") + "x";
        TxtGap.Text = p.GapMs.ToString();
        TxtLead.Text = p.LeadMs.ToString();
        TxtCountdown.Text = p.CountdownSeconds.ToString();
        TxtRepeat.Text = p.RepeatTimes.ToString();
        ChkLegato.IsChecked = p.LegatoSameKey;
        ChkWaitMode.IsChecked = p.WaitForInput;

        var o = _lib.Settings.Overlay;
        ChkOverlay.IsChecked = o.Visible;
        ChkOverlayVisible.IsChecked = o.Visible;
        ChkOverlayClickThrough.IsChecked = o.ClickThrough;
        ChkOverlayLanes.IsChecked = o.ShowLanes;
        ChkOverlayKeys.IsChecked = o.ShowKeyLetters;
        ChkOverlayTitle.IsChecked = o.ShowTitle;
        ChkHideWhilePlaying.IsChecked = o.HideWhilePlaying;
        SldOverlayOpacity.Value = Math.Clamp(o.Opacity, 0.2, 1.0);
        TxtOverlayOpacity.Text = o.Opacity.ToString("0.00");
        TxtOverlayStack.Text = o.MaxStack.ToString();

        _hotkeyRows.Clear();
        foreach (var h in _lib.Settings.Hotkeys)
            _hotkeyRows.Add(new HotkeyRow { Spec = h });
        ListHotkeys.ItemsSource = _hotkeyRows;

        var maps = KeyMap.Presets().Select(m => new { m.Id, m.Name }).ToList();
        ComboKeyMap.ItemsSource = maps;
        ComboKeyMap.DisplayMemberPath = "Name";
        ComboKeyMap.SelectedValuePath = "Id";
        ComboKeyMap.SelectedValue = _lib.Settings.KeyMapId == "custom" ? "delta" : _lib.Settings.KeyMapId;
        if (ComboKeyMap.SelectedIndex < 0) ComboKeyMap.SelectedIndex = 0;

        TxtEditHint.Text = "格式：1 2 3 4 5 6 7 8 ｜ #6 半音 ｜ b3 降调 ｜ ^1 升调 ｜ 5 - 延长 ｜ 0 休止 ｜ | 小节线";
        TxtSearchHint.Visibility = Visibility.Visible;

        // 记谱法
        ComboNotation.ItemsSource = new[] { "直接按键（可视化谱 / D-hydra）", "固定音高（鼠鼠转谱规范，自动选指法）" };
        ComboNotation.SelectedIndex = 0;

        // 悬浮窗：音游下落模式
        ComboOverlayMode.ItemsSource = new[] { "经典堆叠", "音游下落" };
        ComboOverlayMode.SelectedIndex = o.Mode == 1 ? 1 : 0;
        SldFallSpeed.Value = Math.Clamp(o.FallSpeed, 80, 600);
        TxtFallSpeed.Text = o.FallSpeed.ToString("0") + " px/s";
        TxtLookAhead.Text = o.LookAheadSeconds.ToString("0.#");
        ChkJudgmentLine.IsChecked = o.ShowJudgmentLine;
        ChkFallKeyHint.IsChecked = o.ShowFallKeyHint;

        // 更新与反馈
        TxtUpdateUrl.Text = _lib.Settings.UpdateUrl;
        TxtVersionInfo.Text = $"当前版本 {Core.UpdateService.CurrentVersion}　更新来源：仓库中的 update.json";
        TxtUpdateResult.Text = string.IsNullOrWhiteSpace(_lib.Settings.LastUpdateResult)
            ? "点「检查更新」从仓库读取最新版本信息（无网络时可跳过）。"
            : _lib.Settings.LastUpdateResult;
    }

    private void InitHotkeys()
    {
        _hotkeys.Pressed += (_, e) => Dispatcher.InvokeAsync(() => HandleHotkey(e.Action));
        var failed = _hotkeys.RegisterAll(_lib.Settings.Hotkeys);
        TxtHotkeyWarn.Text = failed.Count == 0 ? "" : "注册失败：" + string.Join("、", failed);
    }

    private void InitOverlay()
    {
        var s = _lib.Settings.Overlay;
        if (s.Visible) ShowOverlay();
    }

    // ================================================================ 曲谱库

    private void RefreshFolders()
    {
        var choices = new List<FolderChoice> { new() { Id = null, Name = "全部曲谱" } };
        choices.AddRange(_lib.Folders.Select(f => new FolderChoice { Id = f.Id, Name = f.Name }));
        ComboFolders.ItemsSource = choices;
        ComboFolders.SelectedIndex = 0;

        var editChoices = new List<FolderChoice> { new() { Id = null, Name = "（未分类）" } };
        editChoices.AddRange(_lib.Folders.Select(f => new FolderChoice { Id = f.Id, Name = f.Name }));
        ComboEditFolder.ItemsSource = editChoices;
        ComboEditFolder.SelectedIndex = 0;
    }

    private void RefreshSongs(string? keepId = null)
    {
        var query = TxtSearch.Text.Trim();
        IEnumerable<Song> q = _lib.Songs;
        if (ComboFolders.SelectedItem is FolderChoice { Id: { } fid })
            q = q.Where(s => s.FolderId == fid);
        if (query.Length > 0)
            q = q.Where(s => s.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                          || s.Artist.Contains(query, StringComparison.OrdinalIgnoreCase)
                          || s.Score.Contains(query, StringComparison.OrdinalIgnoreCase));

        _viewSongs = q.OrderBy(s => s.Name, StringComparer.CurrentCulture).ToList();
        var target = keepId ?? _current?.Id;
        ListSongs.ItemsSource = _viewSongs;
        if (target != null)
        {
            var idx = _viewSongs.FindIndex(s => s.Id == target);
            if (idx >= 0) ListSongs.SelectedIndex = idx;
        }
        SetStatus($"曲谱库共 {_lib.Songs.Count} 首，当前显示 {_viewSongs.Count} 首");
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (TxtSearchHint != null)
            TxtSearchHint.Visibility = string.IsNullOrEmpty(TxtSearch.Text) ? Visibility.Visible : Visibility.Collapsed;
        if (_loading) return;
        RefreshSongs();
    }

    private void OnFolderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        RefreshSongs();
    }

    private void OnSongSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (ListSongs.SelectedItem is Song song) SelectSong(song);
    }

    private void SelectSong(Song song)
    {
        _current = song;
        _lib.Settings.LastSongId = song.Id;

        TxtEditName.Text = song.Name;
        TxtEditArtist.Text = song.Artist;
        TxtEditBpm.Text = song.Bpm.ToString("0.##");
        TxtEditMeter.Text = song.Meter;
        TxtScore.Text = song.Score;
        if (ComboEditFolder.ItemsSource is IEnumerable<FolderChoice> fc)
        {
            var idx = fc.ToList().FindIndex(c => c.Id == song.FolderId);
            ComboEditFolder.SelectedIndex = Math.Max(idx, 0);
        }

        TxtSongTitle.Text = song.DisplayName;
        _dirty = false;
        ParseCurrent(updateEditorPreviewOnly: false);
        UpdateOverlayContent();
    }

    private void ParseCurrent(bool updateEditorPreviewOnly)
    {
        if (_current == null)
        {
            _parsed = null;
            ListPreview.ItemsSource = null;
            ListEditPreview.ItemsSource = null;
            return;
        }

        _parsed = ScoreParser.Parse(_current.Score, _current.Name, _current.Bpm,
            NotationKindText.FromText(_current.Notation));
        if (_parsed.Bpm <= 0) _parsed.Bpm = _current.Bpm;
        var map = _lib.EffectiveKeyMap;

        _loading = true;
        ComboNotation.SelectedIndex = _parsed.Notation == NotationKind.Pitch ? 1 : 0;
        _loading = false;

        if (!updateEditorPreviewOnly)
            ListPreview.ItemsSource = ScoreParser.PreviewLines(_parsed, map);
        ListEditPreview.ItemsSource = ScoreParser.PreviewLines(_parsed, map);

        TxtSongMeta.Text = $"{_parsed.Format} · {_parsed.NoteCount} 个音 · {_parsed.BarCount} 小节 · {_parsed.Bpm:0.#} BPM · {_parsed.TotalBeats:0.#} 拍";
        TxtParseInfo.Text = $"识别格式：{_parsed.Format}\n音符 {_parsed.NoteCount} 个（含休止 {_parsed.Notes.Count} 个）\n小节 {_parsed.BarCount} 个\nBPM {_parsed.Bpm:0.#} · 总时长约 {_parsed.TotalBeats * 60 / Math.Max(20, _parsed.Bpm) * _lib.Settings.Playback.Speed:0.0} 秒";
        TxtParseWarn.Text = _parsed.Warnings.Count == 0 ? "" : "提示：" + string.Join("；", _parsed.Warnings.Take(3));
    }

    // ================================================================ 播放

    private PlaybackOptions BuildOptions()
    {
        var p = _lib.Settings.Playback.Clone();
        if (double.TryParse(TxtBpm.Text.Trim(), out var bpm) && bpm > 10) p.BpmOverride = bpm;
        else p.BpmOverride = 0;

        p.Speed = Math.Clamp(SldSpeed.Value, 0.2, 2.0);
        p.GapMs = ParseInt(TxtGap.Text, p.GapMs, 0, 500);
        p.LeadMs = ParseInt(TxtLead.Text, p.LeadMs, 0, 500);
        p.CountdownSeconds = ParseInt(TxtCountdown.Text, p.CountdownSeconds, 0, 30);
        p.RepeatTimes = ParseInt(TxtRepeat.Text, p.RepeatTimes, 0, 999);
        p.LegatoSameKey = ChkLegato.IsChecked == true;
        p.WaitForInput = ChkWaitMode.IsChecked == true;
        return p;
    }

    private static int ParseInt(string text, int fallback, int min, int max)
        => int.TryParse(text.Trim(), out var v) ? Math.Clamp(v, min, max) : fallback;

    private void OnPlayPause(object sender, RoutedEventArgs e) => TogglePlay();

    private void TogglePlay()
    {
        if (_engine.IsRunning)
        {
            if (_engine.IsPaused) _engine.Resume();
            else _engine.Pause();
            UpdatePlayButton();
            return;
        }
        StartPlayback();
    }

    private void StartPlayback()
    {
        if (_current == null)
        {
            SetStatus("请先在左侧选择一首曲谱");
            return;
        }
        ParseCurrent(updateEditorPreviewOnly: false);
        if (_parsed == null || _parsed.Notes.Count == 0)
        {
            SetStatus("这首曲谱没有可演奏的音符");
            return;
        }

        var options = BuildOptions();
        double bpm = options.BpmOverride > 0 ? options.BpmOverride : (_parsed.Bpm > 0 ? _parsed.Bpm : _current.Bpm);
        _lib.Settings.Playback = options;
        LibraryStore.Save(_lib);

        if (_overlay != null && _lib.Settings.Overlay.HideWhilePlaying) _overlay.Hide();
        _engine.Play(_parsed.Notes, _lib.EffectiveKeyMap, _parsed.Notation, bpm, options);
        UpdatePlayButton();
        SetStatus(options.WaitForInput ? "跟练模式：按提示的键" : $"演奏中 · {bpm:0.#} BPM · 速度 {options.Speed:0.00}x");
    }

    private void OnStop(object sender, RoutedEventArgs e)
    {
        _engine.Stop();
        UpdatePlayButton();
        if (_overlay != null && _lib.Settings.Overlay.Visible && _lib.Settings.Overlay.HideWhilePlaying)
            _overlay.Show();
        SetStatus("已停止");
        if (_overlay != null) { _overlay.Canvas.CurrentIndex = -1; _overlay.Canvas.InvalidateVisual(); }
        TxtNowChord.Text = "—";
        TxtNowDetail.Text = "未播放";
        PrgSong.Value = 0;
    }

    private void OnPrevSong(object sender, RoutedEventArgs e) => StepSong(-1);

    private void OnNextSong(object sender, RoutedEventArgs e) => StepSong(1);

    private void StepSong(int delta)
    {
        if (_viewSongs.Count == 0) return;
        int idx = ListSongs.SelectedIndex + delta;
        if (idx < 0) idx = _viewSongs.Count - 1;
        if (idx >= _viewSongs.Count) idx = 0;
        ListSongs.SelectedIndex = idx;
        ListSongs.ScrollIntoView(_viewSongs[idx]);
        if (_engine.IsRunning)
        {
            _engine.Stop();
            StartPlayback();
        }
    }

    private void UpdatePlayButton()
    {
        BtnPlay.Content = _engine.IsRunning
            ? (_engine.IsPaused ? "▶ 继续" : "⏸ 暂停")
            : "▶ 播放 / 暂停";
        ChkWaitMode.IsChecked = _lib.Settings.Playback.WaitForInput;
    }

    private void OnNoteStarted(int index)
    {
        if (_parsed == null || index < 0 || index >= _parsed.Notes.Count) return;
        var note = _parsed.Notes[index];
        var chord = Chord.Resolve(note, _lib.EffectiveKeyMap, _parsed.Notation);

        TxtNowChord.Text = note.IsRest ? "休止" : chord.Text;
        TxtNowChord.Foreground = note.IsRest ? new SolidColorBrush(Color.FromRgb(0x8F, 0xA0, 0xB5)) : ToBrush(chord.Color);
        TxtNowDetail.Text = note.IsRest ? $"第 {index + 1} 个音 · {note.Beats:0.##} 拍" : $"{chord.Detail} · {note.Beats:0.##} 拍 · 第 {index + 1} 个";

        if (_overlay != null)
        {
            _overlay.Canvas.CurrentIndex = index;
            _overlay.Canvas.Paused = false;
            _overlay.Canvas.InvalidateVisual();
        }

        int line = note.Bar * 4;
        if (line < ListPreview.Items.Count)
        {
            ListPreview.SelectedIndex = line;
            ListPreview.ScrollIntoView(ListPreview.Items[line]);
        }
    }

    private void OnNoteFinished(int index)
    {
        if (_overlay != null)
        {
            _overlay.Canvas.CurrentIndex = index + 1;
            _overlay.Canvas.InvalidateVisual();
        }
    }

    private static SolidColorBrush ToBrush(string hex)
    {
        try
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze();
            return b;
        }
        catch
        {
            return new SolidColorBrush(Colors.White);
        }
    }

    private void OnSpeedChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        TxtSpeed.Text = SldSpeed.Value.ToString("0.00") + "x";
        _lib.Settings.Playback.Speed = SldSpeed.Value;
    }

    private void OnParamChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _lib.Settings.Playback = BuildOptions();
    }

    private void OnWaitModeChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _lib.Settings.Playback.WaitForInput = ChkWaitMode.IsChecked == true;
        SetStatus(ChkWaitMode.IsChecked == true ? "已切换到跟练模式（播放时不发按键，等你按）" : "已切换到自动演奏模式");
    }

    // ================================================================ 热键

    private void HandleHotkey(string action)
    {
        switch (action)
        {
            case DefaultHotkeys.PlayPause:
                TogglePlay();
                break;
            case DefaultHotkeys.Stop:
                OnStop(this, new RoutedEventArgs());
                break;
            case DefaultHotkeys.NextSong:
                StepSong(1);
                break;
            case DefaultHotkeys.PrevSong:
                StepSong(-1);
                break;
            case DefaultHotkeys.ToggleOverlay:
                ChkOverlay.IsChecked = ChkOverlay.IsChecked != true;
                OnOverlayToggle(this, new RoutedEventArgs());
                break;
            case DefaultHotkeys.LockOverlay:
                if (_overlay != null)
                {
                    _overlay.ClickThrough = !_overlay.ClickThrough;
                    _lib.Settings.Overlay.ClickThrough = _overlay.ClickThrough;
                    ChkOverlayClickThrough.IsChecked = _overlay.ClickThrough;
                    LibraryStore.Save(_lib);
                    SetStatus(_overlay.ClickThrough ? "悬浮窗已锁定（点击穿透）" : "悬浮窗已解锁：可以拖动 / Ctrl+滚轮缩放");
                }
                break;
            case DefaultHotkeys.ToggleWait:
                ChkWaitMode.IsChecked = ChkWaitMode.IsChecked != true;
                OnWaitModeChanged(this, new RoutedEventArgs());
                break;
            default:
                if (action.StartsWith(DefaultHotkeys.Quick))
                {
                    int n = int.Parse(action.Substring(DefaultHotkeys.Quick.Length));
                    var ids = _lib.Settings.QuickSongIds;
                    if (n - 1 < ids.Count && !string.IsNullOrEmpty(ids[n - 1]))
                    {
                        var song = _lib.Songs.FirstOrDefault(s => s.Id == ids[n - 1]);
                        if (song != null)
                        {
                            SelectSong(song);
                            StartPlayback();
                        }
                    }
                    else SetStatus($"快捷曲 {n} 还没有绑定（在曲谱上右键即可绑定）");
                }
                break;
        }
    }

    private void OnChangeHotkey(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not HotkeyRow row) return;
        var dlg = new HotkeyCaptureWindow(row.Spec) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        row.Spec.Key = dlg.CapturedKey.ToString();
        row.Spec.Modifiers = dlg.CapturedModifiers;
        bool needModifier = dlg.CapturedModifiers.Length == 0;
        if (needModifier)
            SetStatus("提示：未加 Ctrl/Alt 的热键可能和游戏按键冲突，建议加上");

        var failed = _hotkeys.RegisterAll(_lib.Settings.Hotkeys);
        TxtHotkeyWarn.Text = failed.Count == 0 ? "" : "注册失败：" + string.Join("、", failed);
        ListHotkeys.Items.Refresh();
        LibraryStore.Save(_lib);
    }

    // ================================================================ 悬浮窗

    private void ShowOverlay()
    {
        if (_overlay == null)
        {
            _overlay = new OverlayWindow();
            _overlay.AttachPlayback(_engine);
            _overlay.Canvas.Map = _lib.EffectiveKeyMap;
            _overlay.SettingsChanged += (_, _) =>
            {
                SyncOverlaySettingsFromWindow();
                LibraryStore.Save(_lib);
            };
        }
        _overlay.ApplySettings(_lib.Settings.Overlay);
        _overlay.Show();
        UpdateOverlayContent();
    }

    private void SyncOverlaySettingsFromWindow()
    {
        if (_overlay == null) return;
        var o = _lib.Settings.Overlay;
        o.Left = _overlay.Left;
        o.Top = _overlay.Top;
        o.Width = _overlay.Width;
        o.Height = _overlay.Height;
    }

    private void UpdateOverlayContent()
    {
        if (_overlay == null) return;
        _overlay.Canvas.Score = _parsed;
        _overlay.Canvas.Map = _lib.EffectiveKeyMap;
        _overlay.Canvas.Settings = _lib.Settings.Overlay;
        _overlay.Canvas.CurrentIndex = _engine.IsRunning ? _engine.CurrentIndex : -1;
        _overlay.Canvas.InvalidateVisual();
    }

    private void OnOverlayToggle(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        bool show = ChkOverlay.IsChecked == true;
        _lib.Settings.Overlay.Visible = show;
        ChkOverlayVisible.IsChecked = show;
        if (show) ShowOverlay();
        else _overlay?.Hide();
        LibraryStore.Save(_lib);
        SetStatus(show ? "悬浮窗已显示（Alt+L 可解锁拖动）" : "悬浮窗已隐藏");
    }

    private void OnOverlaySettingChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var o = _lib.Settings.Overlay;
        o.Visible = ChkOverlayVisible.IsChecked == true;
        o.ClickThrough = ChkOverlayClickThrough.IsChecked == true;
        o.ShowLanes = ChkOverlayLanes.IsChecked == true;
        o.ShowKeyLetters = ChkOverlayKeys.IsChecked == true;
        o.ShowTitle = ChkOverlayTitle.IsChecked == true;
        o.HideWhilePlaying = ChkHideWhilePlaying.IsChecked == true;
        o.Opacity = SldOverlayOpacity.Value;
        o.MaxStack = ParseInt(TxtOverlayStack.Text, o.MaxStack, 1, 30);

        // 音游下落模式
        o.Mode = ComboOverlayMode.SelectedIndex == 1 ? 1 : 0;
        o.FallSpeed = Math.Clamp(SldFallSpeed.Value, 80, 600);
        o.LookAheadSeconds = double.TryParse(TxtLookAhead.Text.Trim(), out var la) ? Math.Clamp(la, 0.5, 12) : o.LookAheadSeconds;
        o.ShowJudgmentLine = ChkJudgmentLine.IsChecked == true;
        o.ShowFallKeyHint = ChkFallKeyHint.IsChecked == true;
        TxtFallSpeed.Text = o.FallSpeed.ToString("0") + " px/s";
        TxtOverlayOpacity.Text = o.Opacity.ToString("0.00");
        ChkOverlay.IsChecked = o.Visible;

        if (o.Visible) ShowOverlay();
        else _overlay?.Hide();
        if (_overlay != null)
        {
            _overlay.ApplySettings(o);
            _overlay.Canvas.Settings = o;
            _overlay.Canvas.InvalidateVisual();
        }
        LibraryStore.Save(_lib);
    }

    private void OnUnlockOverlay(object sender, RoutedEventArgs e)
    {
        if (_overlay == null) ShowOverlay();
        if (_overlay == null) return;
        _overlay.ClickThrough = false;
        _lib.Settings.Overlay.ClickThrough = false;
        ChkOverlayClickThrough.IsChecked = false;
        _overlay.Show();
        SetStatus("悬浮窗已解锁：拖动窗口移动，Ctrl+滚轮缩放，Alt+L 锁定");
        LibraryStore.Save(_lib);
    }

    private void OnResetOverlayPosition(object sender, RoutedEventArgs e)
    {
        var o = _lib.Settings.Overlay;
        o.Left = 200;
        o.Top = 120;
        o.Width = 460;
        o.Height = 320;
        ShowOverlay();
        SetStatus("悬浮窗位置已复位");
    }

    private void OnPreviewOverlay(object sender, RoutedEventArgs e)
    {
        var o = _lib.Settings.Overlay;
        o.Visible = true;
        ChkOverlay.IsChecked = true;
        ChkOverlayVisible.IsChecked = true;
        ShowOverlay();
        SetStatus("已显示悬浮窗预览");
    }

    // ================================================================ 编辑

    private void OnScoreTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || _current == null) return;
        _current.Score = TxtScore.Text;
        _dirty = true;
        _parsed = ScoreParser.Parse(_current.Score, _current.Name, _current.Bpm,
            NotationKindText.FromText(_current.Notation));
        var map = _lib.EffectiveKeyMap;
        ListEditPreview.ItemsSource = ScoreParser.PreviewLines(_parsed, map);
        TxtParseInfo.Text = $"识别格式：{_parsed.Format}\n音符 {_parsed.NoteCount} 个\n小节 {_parsed.BarCount} 个\nBPM {(_parsed.Bpm > 0 ? _parsed.Bpm : _current.Bpm):0.#}";
        TxtParseWarn.Text = _parsed.Warnings.Count == 0 ? "" : "提示：" + string.Join("；", _parsed.Warnings.Take(3));
        UpdateOverlayContent();
    }

    private void OnEditMetaChanged(object sender, EventArgs e)
    {
        if (_loading || _current == null) return;
        _current.Name = TxtEditName.Text.Trim();
        _current.Artist = TxtEditArtist.Text.Trim();
        if (double.TryParse(TxtEditBpm.Text.Trim(), out var bpm) && bpm > 10) _current.Bpm = bpm;
        _current.Meter = string.IsNullOrWhiteSpace(TxtEditMeter.Text) ? "4/4" : TxtEditMeter.Text.Trim();
        if (ComboEditFolder.SelectedItem is FolderChoice fc) _current.FolderId = fc.Id;
        _dirty = true;
        TxtSongTitle.Text = _current.DisplayName;
    }

    private void OnNewSong(object sender, RoutedEventArgs e)
    {
        var song = new Song
        {
            Name = "新曲谱 " + (_lib.Songs.Count + 1),
            Bpm = 90,
            Meter = "4/4",
            Score = "TITLE=新曲谱\nBPM=90\n\n1 1 5 5 6 6 5 - | 4 4 3 3 2 2 1 -",
            Source = "本地",
        };
        _lib.Songs.Add(song);
        LibraryStore.Save(_lib);
        RefreshSongs(song.Id);
        SelectSong(song);
        Tabs.SelectedIndex = 1;
        SetStatus("已新建曲谱，先填曲名和简谱，然后点「保存到曲谱库」");
    }

    private void OnSaveSong(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        _current.Score = TxtScore.Text;
        OnEditMetaChanged(sender, e);
        LibraryStore.Save(_lib);
        _dirty = false;
        RefreshSongs(_current.Id);
        ParseCurrent(updateEditorPreviewOnly: false);
        UpdateOverlayContent();
        SetStatus($"已保存「{_current.Name}」");
    }

    private void OnSaveSongAs(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        var copy = new Song
        {
            Name = _current.Name + " 副本",
            Artist = _current.Artist,
            Bpm = _current.Bpm,
            Meter = _current.Meter,
            FolderId = _current.FolderId,
            Score = TxtScore.Text,
            Source = "本地",
        };
        _lib.Songs.Add(copy);
        LibraryStore.Save(_lib);
        RefreshSongs(copy.Id);
        SelectSong(copy);
        SetStatus("已另存为新曲谱");
    }

    private void OnBindQuick(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        if ((sender as FrameworkElement)?.Tag is not string tag || !int.TryParse(tag, out var n)) return;
        int idx = n - 1;
        var ids = _lib.Settings.QuickSongIds;
        while (ids.Count < 6) ids.Add(null!);
        ids[idx] = _current.Id;
        LibraryStore.Save(_lib);
        SetStatus($"已把「{_current.Name}」绑定到快捷键 Alt+{4 + idx}（共 6 个快捷曲位）");
    }

    private void OnDeleteSong(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        var r = MessageBox.Show($"确定删除曲谱「{_current.Name}」？", "删除确认",
            MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (r != MessageBoxResult.OK) return;
        _lib.Songs.Remove(_current);
        _current = null;
        LibraryStore.Save(_lib);
        RefreshSongs();
        SetStatus("已删除");
    }

    private void OnRenameSong(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        var dlg = new InputWindow("重命名曲谱", "新的曲名：", _current.Name) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        _current.Name = dlg.Value;
        TxtEditName.Text = dlg.Value;
        LibraryStore.Save(_lib);
        RefreshSongs(_current.Id);
        SetStatus("已重命名");
    }

    private void OnInsertExample(object sender, RoutedEventArgs e)
    {
        TxtScore.Text = "TITLE=小星星\nBPM=90\n\n1 1 5 5 6 6 5 - | 4 4 3 3 2 2 1 - |\n5 5 4 4 3 3 2 - | 5 5 4 4 3 3 2 - |\n1 1 5 5 6 6 5 - | 4 4 3 3 2 2 1 -";
        SetStatus("已插入示例简谱");
    }

    // ================================================================ 导入导出

    private void OnImportSquirrel(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择「鼠鼠口琴谱」的 library.json（或 catalog-v1.json 缓存）",
            Filter = "JSON 文件 (*.json)|*.json|所有文件 (*.*)|*.*",
            InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HarmonicaMacro"),
        };
        if (dlg.ShowDialog() != true) return;
        var (imported, skipped, msg) = LibraryStore.ImportSquirrelLibrary(_lib, dlg.FileName);
        LibraryStore.Save(_lib);
        RefreshFolders();
        RefreshSongs();
        MessageBox.Show(msg, "导入完成", MessageBoxButton.OK, MessageBoxImage.Information);
        SetStatus(msg);
    }

    private void OnImportFiles(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择简谱文件（可多选）",
            Filter = "简谱文本 (*.txt)|*.txt|JSON 曲谱 (*.json)|*.json|所有文件 (*.*)|*.*",
            Multiselect = true,
        };
        if (dlg.ShowDialog() != true) return;

        int total = 0;
        var messages = new List<string>();
        foreach (var file in dlg.FileNames)
        {
            if (file.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && !LibraryStore.IsSingleSongJson(file))
            {
                var (imp, _, msg) = LibraryStore.ImportSquirrelLibrary(_lib, file);
                total += imp;
                messages.Add(Path.GetFileName(file) + "：" + msg);
            }
            else
            {
                var (imp, msg) = LibraryStore.ImportScoreFiles(_lib, new[] { file });
                total += imp;
                messages.Add(Path.GetFileName(file) + "：" + msg);
            }
        }
        LibraryStore.Save(_lib);
        RefreshFolders();
        RefreshSongs();
        SetStatus($"共导入 {total} 首");
        MessageBox.Show(string.Join("\n", messages), "导入结果", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnExportTxt(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        var parsed = ScoreParser.Parse(TxtScore.Text, _current.Name, _current.Bpm,
            NotationKindText.FromText(_current.Notation));
        var dlg = new SaveFileDialog
        {
            Title = "导出为简谱文本",
            FileName = _current.Name + ".txt",
            Filter = "简谱文本 (*.txt)|*.txt",
        };
        if (dlg.ShowDialog() != true) return;
        File.WriteAllText(dlg.FileName, LibraryStore.ExportPlainText(_current, parsed), Encoding.UTF8);
        SetStatus("已导出：" + dlg.FileName);
    }

    private void OnExportJson(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        var parsed = ScoreParser.Parse(TxtScore.Text, _current.Name, _current.Bpm,
            NotationKindText.FromText(_current.Notation));
        var dlg = new SaveFileDialog
        {
            Title = "导出为 JSON 曲谱",
            FileName = _current.Name + ".json",
            Filter = "JSON 文件 (*.json)|*.json",
        };
        if (dlg.ShowDialog() != true) return;
        File.WriteAllText(dlg.FileName, LibraryStore.ExportJson(_current, parsed), Encoding.UTF8);
        SetStatus("已导出：" + dlg.FileName);
    }

    // ================================================================ 键位与设置

    private void UpdateKeyMapUi()
    {
        var map = _lib.EffectiveKeyMap;
        TxtMapName.Text = map.Name + "（" + (map.Id == "custom" ? "自定义" : "预设") + "）";

        var rows = new List<MapRow>();
        for (int i = 0; i < map.Keys.Count && i < 8; i++)
            rows.Add(new MapRow { Slot = $"{i + 1}", Key = ScoreParser.PrettyKey(map.Keys[i]), Detail = i == 7 ? "高音 1" : "琴键" });
        foreach (var m in map.Mods)
        {
            var mouse = new List<string>();
            if (m.MouseMiddle) mouse.Add("中键");
            if (m.MouseLeft) mouse.Add("左键");
            if (m.MouseRight) mouse.Add("右键");
            if (m.Ctrl) mouse.Add("Ctrl");
            if (m.Alt) mouse.Add("Alt");
            if (m.Shift) mouse.Add("Shift");
            rows.Add(new MapRow
            {
                Slot = string.IsNullOrEmpty(m.Prefix) ? "本音" : m.Prefix,
                Key = m.Prefix,
                Detail = m.Label + (mouse.Count > 0 ? " → 按住 " + string.Join("+", mouse) : ""),
            });
        }
        ListMapRows.ItemsSource = rows;
        TxtKeyMapNote.Text = map.Note;
    }

    private void OnKeyMapPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (ComboKeyMap.SelectedValue is not string id) return;
        _lib.Settings.KeyMapId = id;
        UpdateKeyMapUi();
        ParseCurrent(updateEditorPreviewOnly: false);
        UpdateOverlayContent();
        LibraryStore.Save(_lib);
        SetStatus("键位映射已切换：" + _lib.EffectiveKeyMap.Name);
    }

    private void OnOpenKeyMapEditor(object sender, RoutedEventArgs e)
    {
        var dlg = new KeyMapWindow(_lib.EffectiveKeyMap, ComboKeyMap.SelectedValue as string ?? "delta") { Owner = this };
        if (dlg.ShowDialog() != true) return;

        if (dlg.ResultIsCustom)
        {
            _lib.Settings.KeyMapId = "custom";
            _lib.Settings.CustomKeyMap = dlg.ResultMap;
        }
        else
        {
            _lib.Settings.KeyMapId = dlg.ResultMap.Id;
        }
        _loading = true;
        ComboKeyMap.SelectedValue = _lib.Settings.KeyMapId == "custom" ? "delta" : _lib.Settings.KeyMapId;
        _loading = false;
        UpdateKeyMapUi();
        ParseCurrent(updateEditorPreviewOnly: false);
        UpdateOverlayContent();
        LibraryStore.Save(_lib);
        SetStatus("键位映射已更新");
    }

    private void OnOpenDataDir(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = LibraryStore.DataDir,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            SetStatus("打开目录失败：" + ex.Message);
        }
    }

    private void OnSaveAll(object sender, RoutedEventArgs e)
    {
        _lib.Settings.Playback = BuildOptions();
        LibraryStore.Save(_lib);
        SetStatus("设置与曲谱库已保存");
    }

    private void OnResetSettings(object sender, RoutedEventArgs e)
    {
        var r = MessageBox.Show("恢复默认设置（曲谱库不受影响）？", "确认",
            MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (r != MessageBoxResult.OK) return;
        _lib.Settings = new AppSettings();
        LibraryStore.Save(_lib);
        _loading = true;
        BuildUi();
        InitHotkeys();
        InitOverlay();
        _loading = false;
        UpdateKeyMapUi();
        SetStatus("已恢复默认设置");
    }

    private void OnNotationChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _current == null) return;
        _current.Notation = ComboNotation.SelectedIndex == 1 ? "pitch" : "physical";
        _dirty = true;
        ParseCurrent(updateEditorPreviewOnly: false);
        UpdateOverlayContent();
        LibraryStore.Save(_lib);
        SetStatus($"记谱法已切换为「{NotationKindText.FromText(_current.Notation).ToText()}」，预览与演奏用同一套解析");
    }

    private void OnUpdateUrlChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        _lib.Settings.UpdateUrl = TxtUpdateUrl.Text.Trim();
    }

    private async void OnCheckUpdate(object sender, RoutedEventArgs e)
    {
        var url = TxtUpdateUrl.Text.Trim();
        _lib.Settings.UpdateUrl = url;
        TxtUpdateResult.Text = "正在检查更新…";
        SetStatus("正在检查更新…");

        var info = await Core.UpdateService.CheckAsync(url);
        _lastDownloadUrl = string.IsNullOrWhiteSpace(info.DownloadUrl) ? _lib.Settings.DownloadUrl : info.DownloadUrl;
        _lib.Settings.LastUpdateCheck = DateTime.Now;
        _lib.Settings.LastUpdateResult = info.Message;
        LibraryStore.Save(_lib);

        TxtUpdateResult.Text = info.Available
            ? "🎉 " + Core.UpdateService.Describe(info)
            : (info.Available ? "" : info.Message);
        TxtUpdateResult.Foreground = info.Available
            ? new SolidColorBrush(Color.FromRgb(0x4C, 0xD9, 0x64))
            : new SolidColorBrush(Color.FromRgb(0x9A, 0xA4, 0xB5));
        SetStatus(info.Message);

        if (info.Available)
        {
            var r = MessageBox.Show(Core.UpdateService.Describe(info) + "\n\n要现在打开下载页吗？",
                "发现新版本", MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (r == MessageBoxResult.Yes) OpenUrl(_lastDownloadUrl);
        }
    }

    private void OnOpenDownloadPage(object sender, RoutedEventArgs e)
        => OpenUrl(string.IsNullOrWhiteSpace(_lastDownloadUrl) ? _lib.Settings.DownloadUrl : _lastDownloadUrl);

    private void OnOpenIssues(object sender, RoutedEventArgs e)
    {
        var url = _lib.Settings.IssuesUrl;
        if (string.IsNullOrWhiteSpace(url) || url.Contains("你的用户名"))
        {
            MessageBox.Show("还没有配置 Issues 地址。\n\n上传 GitHub 后，在「键位与设置 → 更新与问题反馈」里把\n更新地址 / 下载页 / Issues 地址改成你自己的仓库地址即可。",
                "反馈问题", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        OpenUrl(url);
    }

    private void OnExportDiagnostics(object sender, RoutedEventArgs e)
    {
        try
        {
            var file = Core.Diagnostics.Export(_lib);
            SetStatus("诊断包已导出：" + file);
            MessageBox.Show("诊断包已导出（不含曲谱内容）：\n" + file +
                            "\n\n反馈时把它拖进 GitHub Issue 即可。", "导出诊断包",
                MessageBoxButton.OK, MessageBoxImage.Information);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "/select,\"" + file + "\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            SetStatus("导出诊断包失败：" + ex.Message);
        }
    }

    private void OnOpenFeedbackDir(object sender, RoutedEventArgs e) => OpenUrl(Core.Diagnostics.FeedbackDir);

    private void OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            SetStatus("打开链接失败：" + ex.Message);
        }
    }

    private void SetStatus(string text) => TxtStatus.Text = text;

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_dirty)
        {
            var r = MessageBox.Show("当前曲谱有未保存的修改，要保存吗？", "未保存",
                MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (r == MessageBoxResult.Cancel) { e.Cancel = true; return; }
            if (r == MessageBoxResult.Yes && _current != null)
            {
                _current.Score = TxtScore.Text;
                OnEditMetaChanged(this, new RoutedEventArgs());
            }
        }

        _engine.Stop();
        _engine.Dispose();
        _hotkeys.Dispose();
        SyncOverlaySettingsFromWindow();
        LibraryStore.Save(_lib);
        base.OnClosing(e);
    }
}
