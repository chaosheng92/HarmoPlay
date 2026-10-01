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

    /// <summary>打断键列表的一行。</summary>
    public sealed class InterruptRow
    {
        public InterruptKey Model { get; init; } = new();
        public bool Enabled
        {
            get => Model.Enabled;
            set => Model.Enabled = value;
        }
        public string KeyText => ScoreParser.PrettyKey(Model.Key);
        public string Memo => string.IsNullOrWhiteSpace(Model.Memo) ? "（无说明）" : Model.Memo;
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
    private readonly List<InterruptRow> _interruptRows = new();
    private readonly System.Windows.Threading.DispatcherTimer _interruptTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(50),
    };
    private string _interruptedBy = "";
    private DateTime _interruptReleaseAt = DateTime.MinValue;
    private DateTime _playbackStartedAt = DateTime.MinValue;
    private int _combo;

    public MainWindow()
    {
        InitializeComponent();
        _lib = LibraryStore.Load();

        BuildUi();
        InitHotkeys();
        InitOverlay();
        RefreshFolders();
        RefreshSongs();
        RefreshKeyMapPanel();

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
        _engine.FollowJudged += (i, verdict, detail) =>
            Dispatcher.InvokeAsync(() => OnFollowJudged(i, verdict, detail));

        _interruptTimer.Tick += OnInterruptTick;
        _interruptTimer.Start();

        // 命令行：--locktest / --settings [分类序号]
        var argv = Environment.GetCommandLineArgs();

        // --locktest：连续锁定/解锁并记录扩展样式（验证可以锁定回去）
        if (argv.Any(a => a.Equals("--locktest", StringComparison.OrdinalIgnoreCase)))
            Dispatcher.InvokeAsync(async () =>
            {
                ShowOverlay();
                await Task.Delay(1200);
                void LogEx(string tag) => Program.Trace(
                    $"LOCKTEST {tag}: 设置锁定={_lib.Settings.Overlay.ClickThrough} 实时穿透={_overlay?.ClickThrough} " +
                    $"橙框解锁态={_overlay?.Canvas.Unlocked} 位置={(int)(_overlay?.Left ?? 0)},{(int)(_overlay?.Top ?? 0)} ex={_overlay?.ExStyleHex}");
                SetOverlayLocked(true); LogEx("① 锁定");
                SetOverlayLocked(false); LogEx("② 解锁");

                // 模拟一次"拖动"（手动拖动就是改 Left/Top，由 LocationChanged 触发持久化）
                if (_overlay != null)
                {
                    _overlay.Left += 60;
                    _overlay.Top += 40;
                }
                await Task.Delay(250);
                LogEx("③ 模拟拖动后");

                SetOverlayLocked(true); LogEx("④ 拖动后锁定");
                SetOverlayLocked(false); LogEx("⑤ 再解锁（应仍在拖动后的位置、且可继续拖）");
                SetOverlayLocked(true); LogEx("⑥ 再锁定");
                SetOverlayLocked(false); LogEx("⑦ 再解锁");
                _reallyExit = true;
                Close();
            }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);

        // --practice：启动即进入练习模式
        if (argv.Any(a => a.Equals("--practice", StringComparison.OrdinalIgnoreCase)))
            Dispatcher.InvokeAsync(() => EnterPracticeMode(), System.Windows.Threading.DispatcherPriority.ApplicationIdle);

        // --timingtest：验证倒计时不计入曲谱时间轴
        if (argv.Any(a => a.Equals("--timingtest", StringComparison.OrdinalIgnoreCase)))
            Dispatcher.InvokeAsync(RunTimingTest, System.Windows.Threading.DispatcherPriority.ApplicationIdle);

        // --practicetest [wait|time]：验证练习模式的前进方式，且绝不注入按键
        var pti = Array.FindIndex(argv, a => a.Equals("--practicetest", StringComparison.OrdinalIgnoreCase));
        if (pti >= 0)
        {
            var mode = pti + 1 < argv.Length ? argv[pti + 1] : "wait";
            Dispatcher.InvokeAsync(() => RunPracticeTest(mode), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }

        // --settings：启动后直接打开二级设置菜单
        int si = Array.FindIndex(argv, a => a.Equals("--settings", StringComparison.OrdinalIgnoreCase));
        if (si >= 0)
        {
            int tab = 0;
            if (si + 1 < argv.Length && int.TryParse(argv[si + 1], out var t)) tab = Math.Clamp(t, 0, 7);
            Dispatcher.InvokeAsync(() => OnOpenSettings(tab), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }

        _loading = false;
        Loaded += (_, _) =>
        {
            if (ListSongs.Items.Count > 0 && ListSongs.SelectedIndex < 0)
                ListSongs.SelectedIndex = 0;

            if (!_lib.Settings.DisclaimerShown)
            {
                _lib.Settings.DisclaimerShown = true;
                LibraryStore.Save(_lib);
                ShowDisclaimer(false, "首次使用：请先阅读免责声明");
            }
        };
    }

    // ================================================================ 免责声明 / 风险确认

    private void ShowDisclaimer(bool consent, string header)
    {
        var dlg = new DisclaimerWindow(new DisclaimerWindow.Mode
        {
            IsConsent = consent,
            Header = header,
            ShowDontAsk = consent,
        })
        { Owner = this };
        dlg.ShowDialog();
    }

    private void OnShowDisclaimer(object sender, RoutedEventArgs e)
        => ShowDisclaimer(false, "免责声明（完整版）");

    private void OnToggleDisclaimer(object sender, RoutedEventArgs e)
    {
        bool collapse = TxtDisclaimer.Visibility == Visibility.Visible;
        TxtDisclaimer.Visibility = collapse ? Visibility.Collapsed : Visibility.Visible;
        TxtDisclaimerMore.Visibility = collapse ? Visibility.Visible : Visibility.Collapsed;
        BtnDisclaimerToggle.Content = collapse ? "展开" : "收起";
    }

    /// <summary>开启自动弹奏前的风险确认；返回 false 表示用户选择"不同意"。</summary>
    /// <param name="switching">true = 正在把演奏方式切到自动弹奏；false = 直接开始播放。</param>
    private bool ConfirmAutoPlay(bool switching)
    {
        var s = _lib.Settings;
        if (switching && s.AutoPlayWarnDisabled) return true;   // 勾过"不再提醒"
        if (!switching && s.AutoPlayAccepted) return true;      // 已经同意过，播放时不再反复拦

        var dlg = new DisclaimerWindow(new DisclaimerWindow.Mode
        {
            IsConsent = true,
            Header = switching ? "开启「自动弹奏」前请确认风险" : "以「自动弹奏」开始前请确认风险",
            AgreeText = "同意并开启自动弹奏",
        })
        { Owner = this };

        bool agreed = dlg.ShowDialog() == true;
        s.AutoPlayAccepted = agreed;
        if (agreed) s.AutoPlayWarnDisabled = dlg.DontAskAgain;
        LibraryStore.Save(_lib);

        if (!agreed)
        {
            Program.Trace("用户不同意自动弹奏风险提示，已保持/切换为跟谱弹奏");
            SetStatus("你选择了「不同意」：改用「跟谱弹奏」（不发送任何按键，仅作谱面提示）");
        }
        return agreed;
    }

    /// <summary>用户不同意自动弹奏时，切回跟谱弹奏。</summary>
    private void ForceFollowMode()
    {
        _loading = true;
        ComboPlayMode.SelectedIndex = 1;
        _loading = false;
        _lib.Settings.Playback.WaitForInput = true;
        _lib.Settings.Playback.BeginnerMode = false;
        TxtPlayMode.Text = _lib.Settings.Playback.ModeText;
        LibraryStore.Save(_lib);
        RefreshFollowStats();
    }

    // ================================================================ 初始化

    private void BuildUi()
    {
        Title = $"口琴谱演奏器 HarmoPlay v{Core.UpdateService.CurrentVersion}";
        TxtVersion.Text = $"v{Core.UpdateService.CurrentVersion} (build {Core.UpdateService.BuildStamp}) · 数据目录 " + LibraryStore.DataDir;

        var p = _lib.Settings.Playback;
        TxtBpm.Text = "";
        SldSpeed.Value = Math.Clamp(p.Speed, 0.2, 2.0);
        ComboPlayMode.ItemsSource = new[] { "自动弹奏（程序自己按键）", "跟谱弹奏（连续判定）", "新手模式（按对才继续，永不跳过）" };
        ComboPlayMode.SelectedIndex = p.BeginnerMode ? 2 : p.WaitForInput ? 1 : 0;
        ComboOverlayModeBar.ItemsSource = new[] { "经典堆叠", "音游下落" };
        ComboOverlayModeBar.SelectedIndex = _lib.Settings.Overlay.Mode == 1 ? 1 : 0;
        TxtSpeed.Text = p.Speed.ToString("0.00") + "x";
        TxtGap.Text = p.GapMs.ToString();
        TxtLead.Text = p.LeadMs.ToString();
        TxtCountdown.Text = p.CountdownSeconds.ToString();
        TxtRepeat.Text = p.RepeatTimes.ToString();
        ChkLegato.IsChecked = p.LegatoSameKey;

        // 演奏方式：自动弹奏 / 跟谱弹奏
        ComboPlayMode.ItemsSource = new[] { "自动弹奏（程序自己按键）", "跟谱弹奏（连续判定）", "新手模式（按对才继续，永不跳过）" };
        ComboPlayMode.SelectedIndex = p.BeginnerMode ? 2 : p.WaitForInput ? 1 : 0;
        TxtPlayMode.Text = p.ModeText;


        var o = _lib.Settings.Overlay;
        ChkOverlay.IsChecked = o.Visible;




        TxtEditHint.Text = "格式：1 2 3 4 5 6 7 8 ｜ #6 半音 ｜ b3 降调 ｜ ^1 升调 ｜ 5 - 延长 ｜ 0 休止 ｜ | 小节线";
        TxtSearchHint.Visibility = Visibility.Visible;

        // 记谱法
        ComboNotation.ItemsSource = new[] { "直接按键（可视化谱 / D-hydra）", "固定音高（自动选指法）" };
        ComboNotation.SelectedIndex = 0;

        // 悬浮窗：音游下落模式


        // 更新与反馈


        // 演奏打断键


        // 输入安全


        // 启动时清理上一次可能残留的按键
        Core.InputGuard.ReleaseEverything(KeyVirtualKeys(_lib.EffectiveKeyMap), "启动清理");

    }

    private void InitHotkeys()
    {
        _hotkeys.Pressed += (_, e) => Dispatcher.InvokeAsync(() => HandleHotkey(e.Action));
        var failed = _hotkeys.RegisterAll(_lib.Settings.Hotkeys);
        Program.Trace($"热键注册：共 {_lib.Settings.Hotkeys.Count} 条，失败 {failed.Count} 条" +
                      (failed.Count > 0 ? "（" + string.Join("、", failed) + "）" : ""));
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
        // 0=自动弹奏（程序按键） 1=跟谱弹奏（连续判定，不注入） 2=新手模式（按对才继续）
        int playMode = Math.Clamp(ComboPlayMode.SelectedIndex, 0, 2);
        p.WaitForInput = playMode == 2;
        p.BeginnerMode = playMode == 2;
        p.SimulateKeys = playMode == 0;
        if (p.BeginnerMode)
        {
            p.FollowTimeoutSeconds = 0;   // 新手模式永不跳过
            p.FollowRequireHold = true;   // 新手模式必须按住整拍
        }

        return p;
    }

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

        // 只有「下拉确实停在自动弹奏」+「真的会发送模拟按键」时才需要风险确认。
        // 跟谱 / 新手模式、以及下拉未选中(-1)的情况一律不弹（宁可少问，不要误拦）。
        if (_lib.Settings.Playback.SimulateKeys
            && ComboPlayMode.SelectedIndex == 0
            && !ConfirmAutoPlay(switching: false))
        {
            ForceFollowMode();
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
        _playbackStartedAt = DateTime.Now;
        _interruptedBy = "";
        _combo = 0;
        if (_overlay != null) _overlay.Canvas.Combo = 0;
        _engine.Play(_parsed.Notes, _lib.EffectiveKeyMap, _parsed.Notation, bpm, options);
        UpdatePlayButton();
        SetStatus(options.WaitForInput
            ? $"跟谱弹奏：请按提示的键（严格判定 {(options.FollowStrictKey ? "开" : "关")}、超时 {options.FollowTimeoutSeconds} 秒）"
            : $"自动弹奏 · {bpm:0.#} BPM · 速度 {options.Speed:0.00}x");
        TxtPlayMode.Text = options.ModeText;
        RefreshFollowStats();
    }

    private void OnStop(object sender, RoutedEventArgs e)
    {
        _engine.Stop();
        UpdatePlayButton();
        if (_overlay != null && _lib.Settings.Overlay.Visible && _lib.Settings.Overlay.HideWhilePlaying)
            _overlay.Show();
        SetStatus("已停止");
        if (_overlay != null) { _overlay.Canvas.CurrentIndex = -1; _overlay.Canvas.Combo = 0; _overlay.Canvas.InvalidateVisual(); }
        _combo = 0;
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
        ComboPlayMode.SelectedIndex = _lib.Settings.Playback.WaitForInput ? 1 : 0;
        TxtPlayMode.Text = _lib.Settings.Playback.ModeText;
    }

    private void OnNoteStarted(int index)
    {
        if (_parsed == null || index < 0 || index >= _parsed.Notes.Count) return;
        var note = _parsed.Notes[index];
        var chord = Chord.Resolve(note, _lib.EffectiveKeyMap, _parsed.Notation);

        // 连击：自动弹奏按音符数累加；跟谱弹奏按"按对"累加（按错/漏掉会清零）
        if (!note.IsRest)
        {
            _combo++;
            if (_overlay != null) _overlay.Canvas.Combo = _combo;
        }

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

    private void OnPlayModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        int index = ComboPlayMode.SelectedIndex;
        Program.Trace($"演奏方式切换：index={index} text=\"{ComboPlayMode.SelectedItem as string}\"");
        // 没选中（-1）什么都不做：以前被 Math.Clamp 成 0（自动弹奏），于是会弹风险警告，
        // 拒绝后又把人踢回跟谱 → 表现为"切不到新手模式"。
        if (index < 0) return;
        int mode = index >= 2 ? 2 : index == 1 ? 1 : 0;
        bool beginner = mode == 2;

        // 只有「自动弹奏」需要风险确认；跟谱 / 新手模式一律不弹警告
        if (mode == 0 && !ConfirmAutoPlay(switching: true))
        {
            ForceFollowMode();
            return;
        }

        var p = _lib.Settings.Playback;
        // 三种方式的分工：
        //   自动弹奏：程序按键（SimulateKeys=true）
        //   跟谱弹奏：**连续**——谱面按拍走，程序只读键判定，漏了就记漏继续（不等待、不注入）
        //   新手模式：**按对才继续**——不按对就不走，按错/没按住就停在这一音
        p.WaitForInput = beginner;
        p.BeginnerMode = beginner;
        p.SimulateKeys = mode == 0;            // 只有自动弹奏会真的发送按键
        if (beginner)
        {
            p.FollowStrictKey = true;          // 必须按对才算过
            p.FollowRequireHold = true;        // 而且必须按住整拍
            p.FollowTimeoutSeconds = 0;        // 永不跳过：点不对 / 没按住就不继续
            // 新手默认放慢一点，方便跟手（可随时在右边「速度」里改回去）
            if (p.Speed > 0.8)
            {
                p.Speed = 0.8;
                _loading = true;
                SldSpeed.Value = 0.8;
                TxtSpeed.Text = "0.80x";
                _loading = false;
            }
        }
        TxtPlayMode.Text = p.ModeText;
        LibraryStore.Save(_lib);

        SetStatus(beginner
            ? "已切换到「新手模式」：不发送按键，必须按对并按住整拍才继续，按错就停在这一音（永不跳过）"
            : mode == 1
                ? "已切换到「跟谱弹奏（连续）」：不发送按键，谱面按拍连续走，你跟着弹，漏了记漏继续"
                : "已切换到「自动弹奏」：程序按 BPM 自动按键弹完整首（Alt+T 切换）");

        // 播放中切换需要重建引擎参数，直接重开一遍
        if (_engine.IsRunning)
        {
            _engine.Stop();
            StartPlayback();
        }
        else
        {
            RefreshFollowStats();
            UpdatePlayButton();
        }
    }

        private void OnFollowJudged(int index, string verdict, string detail)
    {
        RefreshFollowStats();
        if (verdict is "wrong" or "miss")
        {
            _combo = 0;
            if (_overlay != null) _overlay.Canvas.Combo = 0;
        }
        if (verdict == "wrong")
            TxtNowDetail.Text = "按错了：" + detail;
        else if (verdict == "miss")
            TxtNowDetail.Text = "漏掉了：" + detail;
    }

    private void RefreshFollowStats()
    {
        var p = _lib.Settings.Playback;
        TxtFollowStats.Text = p.WaitForInput
            ? $"✔ {_engine.FollowCorrect}　✖ {_engine.FollowWrong}　漏 {_engine.FollowMissed}"
            : "";
    }

    // ================================================================ 热键

    private void HandleHotkey(string action)
    {
        Program.Trace($"热键触发：{action}");
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
            case DefaultHotkeys.ToggleOverlay2:
                ToggleOverlayVisibility();
                break;
            case DefaultHotkeys.ShowMain:
                ShowMainWindow();
                break;
            case DefaultHotkeys.LockOverlay:
                // 以设置为准切换，避免界面/实例状态不同步导致"只能解锁、锁不回去"
                SetOverlayLocked(!_lib.Settings.Overlay.ClickThrough);
                break;
            case DefaultHotkeys.ToggleWait:
                // Alt+T 在 自动 → 跟谱 → 新手 之间循环
                ComboPlayMode.SelectedIndex = ComboPlayMode.SelectedIndex switch
                {
                    0 => 1,
                    1 => 2,
                    _ => 0,
                };
                break;
            case DefaultHotkeys.PanicRelease:
                OnPanicRelease(this, new RoutedEventArgs());
                break;
            case DefaultHotkeys.ToggleMode:
                SetOverlayMode(_lib.Settings.Overlay.Mode == 1 ? 0 : 1, true);
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
                PersistOverlayBounds();
                _settingsWindow?.RefreshFromLibrary();
                LibraryStore.Save(_lib);
            };
        }
        var overlaySettings = _lib.Settings.Overlay;
        if (!_lib.Settings.RememberOverlayPosition)
        {
            // 不记住位置：每次都用默认位置开场
            overlaySettings.Left = 200;
            overlaySettings.Top = 120;
        }
        _overlay.ApplySettings(overlaySettings);
        _overlay.Show();
        Program.Trace($"悬浮窗位置：Left={overlaySettings.Left:0} Top={overlaySettings.Top:0}" +
                      $"（记住位置={_lib.Settings.RememberOverlayPosition}）");
        UpdateOverlayContent();
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
        if (show) ShowOverlay();
        else _overlay?.Hide();
        LibraryStore.Save(_lib);
        SetStatus(show ? "悬浮窗已显示（Alt+L 可解锁拖动）" : "悬浮窗已隐藏");
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
            Title = "选择外部曲库 library.json（或 catalog-v1.json 缓存）",
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
            Title = "选择曲谱文件（可多选；支持 CSV / TSV / JSON / TXT）",
            Filter = "曲谱文件 (*.csv;*.tsv;*.json;*.txt)|*.csv;*.tsv;*.json;*.txt|" +
                     "批量表格 CSV/TSV (*.csv;*.tsv)|*.csv;*.tsv|" +
                     "JSON 曲谱 (*.json)|*.json|简谱文本 (*.txt)|*.txt|所有文件 (*.*)|*.*",
            Multiselect = true,
        };
        if (dlg.ShowDialog() != true) return;
        ImportPaths(dlg.FileNames);
    }

    /// <summary>批量导入（文件或文件夹）：CSV/TSV、JSON（单个/数组/{songs:[]}）、txt。</summary>
    private void ImportPaths(IEnumerable<string> paths)
    {
        var list = paths.ToList();
        // 原版 library.json（私有格式）仍走专用导入，其余走通用批量导入
        var squirrel = list.Where(f => f.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                                       && !LibraryStore.IsSingleSongJson(f)
                                       && !BatchImport.LooksLikePlainSongList(f)).ToList();
        var rest = list.Except(squirrel).ToList();

        int total = 0;
        var messages = new List<string>();

        foreach (var file in squirrel)
        {
            var (imp, _, msg) = LibraryStore.ImportSquirrelLibrary(_lib, file);
            total += imp;
            messages.Add(Path.GetFileName(file) + "：" + msg);
        }

        if (rest.Count > 0)
        {
            var report = BatchImport.FromPaths(_lib, rest);
            total += report.Added;
            messages.Add(report.Summary);
            messages.AddRange(report.Messages.Take(12).Select(m => "  · " + m));
            messages.AddRange(report.Warnings.Take(5).Select(w => "  ⚠ " + w));
        }

        LibraryStore.Save(_lib);
        RefreshFolders();
        RefreshSongs();
        SetStatus($"共导入 {total} 首");
        MessageBox.Show(string.Join("\n", messages), "导入结果", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>批量导入整个文件夹（递归找 .csv/.tsv/.json/.txt）。</summary>
    private void OnImportFolder(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = "选择要批量导入的文件夹（里面放 CSV / JSON / TXT 曲谱都行）",
            Multiselect = false,
        };
        if (dlg.ShowDialog() != true) return;
        ImportPaths(new[] { dlg.FolderName });
    }

    /// <summary>把整个曲谱库导出为 CSV（Excel 编辑后可原样导回）。</summary>
    private void OnExportAllCsv(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Title = "导出整个曲谱库为 CSV",
            FileName = "曲谱库导出.csv",
            Filter = "CSV 表格 (*.csv)|*.csv|所有文件 (*.*)|*.*",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            File.WriteAllText(dlg.FileName, BatchImport.ToCsv(_lib), new UTF8Encoding(true));
            SetStatus($"已导出 {_lib.Songs.Count} 首到 {dlg.FileName}（可用 Excel 编辑后导回）");
            var r = MessageBox.Show(
                $"已导出 {_lib.Songs.Count} 首曲谱：\n{dlg.FileName}\n\n" +
                "列：曲名 / 简谱 / BPM / 拍号 / 歌手 / 分类 / 记谱 / 启用\n" +
                "改完另存为 CSV（UTF-8）后用「导入曲谱文件…」导回即可。\n\n要打开所在文件夹吗？",
                "导出完成", MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (r == MessageBoxResult.Yes)
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = "/select,\"" + dlg.FileName + "\"",
                    UseShellExecute = true,
                });
            }
        }
        catch (Exception ex)
        {
            SetStatus("导出失败：" + ex.Message);
        }
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

                            // ================================================================ 悬浮窗位置

    /// <summary>统一切换悬浮窗模式（工具栏 / 设置页 / Alt+M 都走这里）。</summary>
    private void SetOverlayMode(int mode, bool announce)
    {
        mode = mode == 1 ? 1 : 0;
        _lib.Settings.Overlay.Mode = mode;
        _loading = true;
        ComboOverlayModeBar.SelectedIndex = mode;
        _loading = false;
        ShowOverlay();
        LibraryStore.Save(_lib);
        if (announce)
            SetStatus(mode == 1
                ? "悬浮窗已切换：音游下落模式（音符落到判定线时按键，Alt+M 可切回）"
                : "悬浮窗已切换：经典堆叠模式（最下面一块就是当前该弹的音）");
    }

    private void OnOverlayModeBarChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        SetOverlayMode(ComboOverlayModeBar.SelectedIndex, true);
    }

    // ================================================================ 左下角下载

    private void OnDownloadSampleScore(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Title = "保存示例曲谱（可直接导入）",
            FileName = Core.Downloads.SampleScoreFile,
            Filter = "曲谱 JSON (*.json)|*.json|所有文件 (*.*)|*.*",
        };
        if (dlg.ShowDialog() != true) return;

        if (!Core.Downloads.Export(Core.Downloads.SampleScoreFile, dlg.FileName, out var message))
        {
            SetStatus(message);
            MessageBox.Show(message, "示例曲谱", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 顺带把格式说明也写到同一个目录
        var dir = Path.GetDirectoryName(dlg.FileName) ?? LibraryStore.DataDir;
        Core.Downloads.Export(Core.Downloads.SampleReadmeFile, Path.Combine(dir, Core.Downloads.SampleReadmeFile), out _);

        SetStatus(message + "（同目录还附了「示例曲谱-说明.txt」）");
        OfferOpen(dir, dlg.FileName, "示例曲谱已保存");
    }

    private void OnDownloadAiSpec(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Title = "保存 AI 转谱要求",
            FileName = Core.Downloads.AiSpecFile,
            Filter = "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
        };
        if (dlg.ShowDialog() != true) return;

        if (!Core.Downloads.Export(Core.Downloads.AiSpecFile, dlg.FileName, out var message))
        {
            SetStatus(message);
            MessageBox.Show(message, "AI 转谱要求", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SetStatus(message + "　把它全文复制给 AI，再附上音频 / MIDI / 谱面图片即可");
        OfferOpen(Path.GetDirectoryName(dlg.FileName) ?? LibraryStore.DataDir, dlg.FileName, "AI 转谱要求已保存");
    }

    private void OfferOpen(string dir, string file, string title)
    {
        var r = MessageBox.Show($"{title}：\n{file}\n\n要打开所在文件夹吗？", title,
            MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (r != MessageBoxResult.Yes) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "/select,\"" + file + "\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            SetStatus("打开文件夹失败：" + ex.Message);
        }
    }

    // ================================================================ 悬浮窗显隐快捷键

    /// <summary>显示 / 隐藏悬浮窗（Alt+H 与备用快捷键都走这里）。</summary>
    private void ToggleOverlayVisibility()
    {
        ChkOverlay.IsChecked = ChkOverlay.IsChecked != true;
        OnOverlayToggle(this, new RoutedEventArgs());
    }

            /// <summary>录一个热键绑定（action 为空表示用备用槽）。</summary>
                                                        // ================================================================ 打断键 / 输入安全

    private static IEnumerable<int> KeyVirtualKeys(KeyMap map) =>
        map.Keys.Select(k => Enum.TryParse<Key>(k, true, out var key) ? KeyInterop.VirtualKeyFromKey(key) : 0)
                 .Where(vk => vk != 0);

                                        private void OnPanicRelease(object sender, RoutedEventArgs e)
    {
        _engine.PanicRelease("急停");
        _interruptedBy = "";
        UpdatePlayButton();
        SetStatus("已急停：演奏停止，所有按键与鼠标键都已强制松开");
    }

    /// <summary>打断检测：玩家按了移动 / 跳跃 / 切枪 / 背包键就暂停演奏。</summary>
    private void OnInterruptTick(object? sender, EventArgs e)
    {
        if (!_lib.Settings.InterruptEnabled) return;
        var keys = _lib.Settings.InterruptKeys.Where(k => k.Enabled).ToList();
        if (keys.Count == 0) return;

        if (!_engine.IsRunning)
        {
            _interruptedBy = "";
            return;
        }

        // 开播瞬间不判打断：手可能还按在开始热键上
        if ((DateTime.Now - _playbackStartedAt).TotalMilliseconds < 400) return;

        // 带 Alt/Ctrl/Win 的组合不判打断：那多半是程序自己的热键
        // （Alt+1 播放、Alt+4~9 快捷曲、Ctrl+Alt+0 急停…… 其中的数字键不该被当成"切枪"）
        bool modifierHeld = InputSender.IsDown(Key.LeftAlt) || InputSender.IsDown(Key.RightAlt)
                         || InputSender.IsDown(Key.LeftCtrl) || InputSender.IsDown(Key.RightCtrl)
                         || InputSender.IsDown(Key.LWin) || InputSender.IsDown(Key.RWin);
        if (modifierHeld) return;

        // 与琴键重复的按键不判打断（否则程序会自己打断自己）
        var noteKeys = _lib.EffectiveKeyMap.Keys;

        string? down = null;
        foreach (var item in keys)
        {
            if (noteKeys.Any(nk => nk.Equals(item.Key, StringComparison.OrdinalIgnoreCase))) continue;
            if (!Enum.TryParse<Key>(item.Key, true, out var key) || key == Key.None) continue;
            if (InputSender.IsDown(key))
            {
                down = ScoreParser.PrettyKey(item.Key) + (string.IsNullOrWhiteSpace(item.Memo) ? "" : $"（{item.Memo}）");
                break;
            }
        }

        if (down != null)
        {
            if (!_engine.IsPaused)
            {
                _engine.Pause();
                _interruptedBy = down;
                Program.Trace($"打断：检测到 {down}，暂停演奏（第 {_engine.CurrentIndex + 1} 个音）");
                SetStatus($"检测到「{down}」 → 已暂停演奏；" +
                          (_lib.Settings.InterruptBehavior == 1 ? "松开后自动继续" : "按 Alt+1 继续"));
                UpdatePlayButton();
            }
            _interruptReleaseAt = DateTime.MinValue;
            return;
        }

        if (_engine.IsPaused && _interruptedBy.Length > 0 && _lib.Settings.InterruptBehavior == 1)
        {
            if (_interruptReleaseAt == DateTime.MinValue)
            {
                _interruptReleaseAt = DateTime.Now;
            }
            else if ((DateTime.Now - _interruptReleaseAt).TotalMilliseconds >= _lib.Settings.InterruptResumeDelayMs)
            {
                _engine.Resume();
                Program.Trace($"打断：{_interruptedBy} 已松开，自动继续演奏");
                SetStatus($"「{_interruptedBy}」已松开，继续演奏");
                _interruptedBy = "";
                UpdatePlayButton();
            }
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

        // 关闭窗口：问一下是退出还是退到后台（托盘）
        if (!_reallyExit)
        {
            int action = _lib.Settings.CloseAction;
            if (action != 1 && action != 2)
            {
                var dlg = new ExitChoiceWindow { Owner = this };
                if (dlg.ShowDialog() != true) { e.Cancel = true; return; }
                action = dlg.Choice;
                if (dlg.Remember)
                {
                    _lib.Settings.CloseAction = action;
                    LibraryStore.Save(_lib);
                }
            }

            if (action == 2)
            {
                e.Cancel = true;
                HideToTray();
                return;
            }
        }

        _reallyExit = true;
        _tray?.Dispose();
        _tray = null;

        _engine.Stop();
        _engine.Dispose();
        Core.InputGuard.ReleaseEverything(KeyVirtualKeys(_lib.EffectiveKeyMap), "退出程序");
        _hotkeys.Dispose();
        PersistOverlayBounds();
        LibraryStore.Save(_lib);

        // 悬浮窗是独立顶层窗口：必须显式关掉，否则主窗口关闭后它会继续留在屏幕上（进程也不退出）
        try
        {
            _overlay?.Close();
        }
        catch
        {
            // 忽略
        }
        _overlay = null;
        Program.Trace("退出程序：已关闭主窗口与悬浮窗，准备结束进程");

        base.OnClosing(e);

        // 明确结束进程，避免残留（托盘图标 / 悬浮窗 / 播放线程）
        Dispatcher.InvokeAsync(() => System.Windows.Application.Current?.Shutdown(),
            System.Windows.Threading.DispatcherPriority.Background);
    }

    // ================================================================ 后台托盘

    private TrayIcon? _tray;
    private bool _reallyExit;

    /// <summary>最小化到右下角托盘：隐藏主窗口，悬浮窗与热键继续工作。
    /// 若托盘图标注册失败（系统策略 / 安全软件 / 沙箱限制），则**不隐藏窗口**并明确告知，
    /// 避免用户"窗口没了、图标也没有"找不到程序。</summary>
    private void HideToTray()
    {
        if (_tray == null)
        {
            var tray = new TrayIcon(
                "口琴谱演奏器：双击恢复窗口，右键可退出",
                showWindow: ShowMainWindow,
                toggleOverlay: ToggleOverlayVisibility,
                playPause: TogglePlay,
                stop: () => OnStop(this, new RoutedEventArgs()),
                about: ShowAboutFromTray,
                exit: () => { _reallyExit = true; Close(); });

            if (!tray.IsAdded)
            {
                tray.Dispose();
                Program.Trace("托盘：注册失败，放弃隐藏窗口（窗口继续保持显示）");
                SetStatus("托盘图标注册失败，窗口未隐藏（详见 startup.log）");
                MessageBox.Show(
                    "托盘图标注册失败，系统没有接受这个图标。\n\n" +
                    "为避免窗口收起来后找不到程序，主窗口会继续保持显示。\n\n" +
                    "你可以：\n" +
                    "· 用热键 Alt+Shift+H 随时显示主窗口（设置 → 全局热键 可改）\n" +
                    "· 或在「设置 → 数据与关于 → 点右上角关闭窗口时」改为「直接退出程序」\n" +
                    "· 若想用托盘：检查安全软件是否拦截，或换一台没装此类软件的环境试试\n\n" +
                    "详细错误码见程序目录的 startup.log。",
                    "无法最小化到托盘", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            _tray = tray;
        }

        Hide();
        PersistOverlayBounds();
        LibraryStore.Save(_lib);
        SetStatus("已最小化到右下角托盘（热键仍然可用，双击托盘图标可恢复窗口）");
        Program.Trace($"已最小化到托盘：主窗口可见={IsVisible}，悬浮窗可见={_overlay?.IsVisible}，托盘图标={_tray.IsAdded}");
        _tray.ShowBalloon("口琴谱演奏器仍在后台运行",
            "双击托盘图标恢复窗口；右键可显示 / 隐藏悬浮窗、播放暂停或退出。");
    }

    private void ShowMainWindow()
    {
        Show();
        WindowState = WindowState.Normal;
        ShowInTaskbar = true;
        Activate();
        Topmost = true;
        Topmost = false;
        SetStatus("已从托盘恢复窗口");
    }

    private void ShowAboutFromTray()
    {
        ShowMainWindow();
        OnOpenSettings(7);   // 数据与关于
    }

    // ================================================================ 二级设置菜单入口

    private SettingsWindow? _settingsWindow;

    private void OnOpenSettings(object sender, RoutedEventArgs e) => OnOpenSettings(0);

    private void OnOpenSettings(int initialCategory)
    {
        if (_settingsWindow != null)
        {
            _settingsWindow.Activate();
            return;
        }

        var ctx = new SettingsContext
        {
            Library = _lib,
            Engine = _engine,
            Hotkeys = _hotkeys,
            Overlay = () => _overlay,
            SetStatus = SetStatus,
            ShowOverlay = ShowOverlay,
            PanicRelease = () => OnPanicRelease(this, new RoutedEventArgs()),
            SetOverlayLocked = SetOverlayLocked,
            ImportFolder = () => { _settingsWindow?.Close(); OnImportFolder(this, new RoutedEventArgs()); },
            ExportAllCsv = () => OnExportAllCsv(this, new RoutedEventArgs()),
            MinimizeToTray = () => { Close(); },
            RefreshMain = () =>
            {
                ParseCurrent(updateEditorPreviewOnly: false);
                UpdateOverlayContent();
                UpdatePlayButton();
                RefreshKeyMapPanel();
                RefreshFollowStats();
            },
        };

        _settingsWindow = new SettingsWindow(ctx, initialCategory) { Owner = this };
        try
        {
            _settingsWindow.ShowDialog();
        }
        finally
        {
            _settingsWindow = null;
        }
        RefreshKeyMapPanel();
        UpdateOverlayContent();
        UpdatePlayButton();
        ComboOverlayModeBar.SelectedIndex = _lib.Settings.Overlay.Mode == 1 ? 1 : 0;
        ChkOverlay.IsChecked = _lib.Settings.Overlay.Visible;
        SetStatus("设置已更新");
    }

    /// <summary>锁定 / 解锁悬浮窗（锁定 = 点击穿透不挡游戏；解锁 = 可拖动、可右键）。</summary>
    private void SetOverlayLocked(bool locked)
    {
        _lib.Settings.Overlay.ClickThrough = locked;
        if (_overlay == null) ShowOverlay();          // 顺带确保悬浮窗存在
        if (_overlay != null)
        {
            _overlay.ClickThrough = locked;
            _overlay.RefreshLockVisual();
            _overlay.Show();
        }
        LibraryStore.Save(_lib);
        _settingsWindow?.RefreshFromLibrary();
        SetStatus(locked
            ? "悬浮窗已锁定：点击穿透，不挡游戏（Alt+L 解锁）"
            : "悬浮窗已解锁：按住橙色拖动条移动，Ctrl+滚轮缩放；再按 Alt+L 即可锁定");
        Program.Trace($"悬浮窗{(locked ? "锁定" : "解锁")}：ex={_overlay?.ExStyleHex} 设置值={_lib.Settings.Overlay.ClickThrough}");
    }

    /// <summary>把悬浮窗当前的位置/大小记回设置（拖动完、退出时调用）。</summary>
    private void PersistOverlayBounds()    {
        if (_overlay == null) return;
        var o = _lib.Settings.Overlay;
        o.Left = _overlay.Left;
        o.Top = _overlay.Top;
        o.Width = _overlay.Width;
        o.Height = _overlay.Height;
    }

    /// <summary>刷新主界面右侧的键位映射表。</summary>
    private void RefreshKeyMapPanel()
    {
        var map = _lib.EffectiveKeyMap;
        TxtMapName.Text = $"{map.Name}（{(map.Id == "custom" ? "自定义" : "预设")}；变调：左键=降八度、中键=升半音、右键=升八度" +
                          (map.AllowMouseCombos ? "，允许组合）" : "，不允许组合）");

        var rows = new List<MapRow>();
        for (int i = 0; i < map.Keys.Count && i < 8; i++)
            rows.Add(new MapRow
            {
                Slot = $"{i + 1}",
                Key = ScoreParser.PrettyKey(map.Keys[i]),
                Detail = i == 7 ? "高音 1" : "琴键",
            });

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
    }

    private static int ParseInt(string text, int fallback, int min, int max)
    {
        return int.TryParse(text.Trim(), out var v) ? Math.Clamp(v, min, max) : fallback;
    }
}















