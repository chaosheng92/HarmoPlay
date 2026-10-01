using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using HarmoPlay.Core;
using HarmoPlay.Models;

namespace HarmoPlay.Views;

/// <summary>
/// 练习模式：不发送任何按键，读取你的按键并**实时判定**（音游式反馈）。
/// 判定依据是"当前音出现到你按下琴键的反应时间"：完美 ≤80ms、良好 ≤180ms、再慢或按错 = 错过。
/// </summary>
public partial class MainWindow
{
    private const int VK_LBUTTON = 0x01;
    private const int VK_RBUTTON = 0x02;
    private const int VK_MBUTTON = 0x04;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private bool _practiceMode;
    private bool _practiceHooked;
    private bool _overlayVisibleBeforePractice;

    private ParsedScore? _practiceScore;
    private Song? _practiceSong;
    private DateTime _practiceNoteShownAt = DateTime.UtcNow;

    private int _practicePerfect, _practiceGood, _practiceMiss;
    private int _practiceCombo, _practiceBestCombo;

    private readonly List<(Border Box, int Vk, TextBlock Label)> _practiceKeyVisuals = new();
    private DispatcherTimer? _practiceKeyTimer;

    // ================================================================ 模式切换

    private void OnModePlay(object sender, RoutedEventArgs e) => SetAppMode(practice: false);

    private void OnModePractice(object sender, RoutedEventArgs e) => SetAppMode(practice: true);

    private void SetAppMode(bool practice)
    {
        if (_practiceMode == practice) return;
        _practiceMode = practice;

        Tabs.Visibility = practice ? Visibility.Collapsed : Visibility.Visible;
        PracticePanel.Visibility = practice ? Visibility.Visible : Visibility.Collapsed;
        ButtonsHighlight(practice);

        if (practice)
        {
            _overlayVisibleBeforePractice = _lib.Settings.Overlay.Visible;
            _engine.Stop();
            _lib.Settings.Overlay.Visible = false;
            _overlay?.Hide();
            ChkOverlay.IsChecked = false;

            EnsurePracticeHooks();
            RefreshPracticeSongs();
            BuildPracticeKeys();
            ConfigPracticeCanvas();
            ResetPracticeStats();
            SetStatus("已进入练习模式：程序不会发送任何按键，只读取你的按键并实时判定");
            TxtModeHint.Text = "练习模式：把你的按键实时判定（完美 / 良好 / 错过），不发送任何按键";
        }
        else
        {
            StopPractice(silent: true);
            _lib.Settings.Overlay.Visible = _overlayVisibleBeforePractice;
            if (_overlayVisibleBeforePractice)
            {
                ChkOverlay.IsChecked = true;
                ShowOverlay();
            }
            SetStatus("已回到演奏模式");
            TxtModeHint.Text = "演奏模式：程序自动弹 / 跟谱提示　·　练习模式：不发送按键，实时判定你的每次按键";
        }
    }

    private void ButtonsHighlight(bool practice)
    {
        HighlightModeButton(BtnModePlay, !practice);
        HighlightModeButton(BtnModePractice, practice);
    }

    private static void HighlightModeButton(Button button, bool active)
    {
        button.Background = new SolidColorBrush(active
            ? Color.FromRgb(0x1F, 0x4E, 0x86)
            : Color.FromRgb(0x1E, 0x24, 0x30));
        button.BorderBrush = new SolidColorBrush(active
            ? Color.FromRgb(0x4A, 0x9D, 0xFF)
            : Color.FromRgb(0x2C, 0x35, 0x43));
        button.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
    }

    /// <summary>命令行 --practice：启动即进入练习模式。</summary>
    public void EnterPracticeMode() => SetAppMode(practice: true);

    /// <summary>
    /// 命令行 --timingtest：用"每拍一个音"的谱面（120 BPM = 每 500ms）实测时间轴。
    /// 倒计时不能被算进曲谱时间，否则开头几个音会抢跑（实测间隔会远小于 500ms）。
    /// </summary>
    public void RunTimingTest()
    {
        var parsed = ScoreParser.Parse("1 2 3 4 5 6 7 1'", "计时自检", 120, NotationKind.Pitch);
        var options = new PlaybackOptions
        {
            Speed = 1,
            CountdownSeconds = 3,
            LeadMs = 0,
            GapMs = 0,
            RepeatTimes = 1,
            WaitForInput = false,
        };

        DateTime first = DateTime.MaxValue;
        _engine.NoteStarted += i =>
        {
            var now = DateTime.UtcNow;
            if (i == 0) first = now;
            Program.Trace($"TIMINGTEST 音{i} 相对第 1 个音 +{(now - first).TotalMilliseconds:0} ms（预期 ≈ {i * 500}）");
        };
        _engine.Finished += () => Program.Trace("TIMINGTEST 播放结束");

        Program.Trace("TIMINGTEST 开始：3 秒倒计时后应有 8 个音，间隔应≈500ms");
        _engine.Play(parsed.Notes, _lib.EffectiveKeyMap, NotationKind.Pitch, 120, options);

        Dispatcher.InvokeAsync(async () =>
        {
            await Task.Delay(9000);
            _reallyExit = true;
            Close();
        }, DispatcherPriority.Background);
    }

    // ================================================================ 曲谱与参数

    private void RefreshPracticeSongs()
    {
        var songs = _lib.Songs.Where(s => s.Enabled).OrderBy(s => s.Name, StringComparer.CurrentCulture).ToList();
        ComboPracticeSong.ItemsSource = songs;
        ComboPracticeSong.DisplayMemberPath = "Name";
        var keep = _practiceSong?.Id ?? _current?.Id;
        var idx = keep == null ? -1 : songs.FindIndex(s => s.Id == keep);
        ComboPracticeSong.SelectedIndex = idx >= 0 ? idx : songs.Count > 0 ? 0 : -1;
    }

    private void OnPracticeSongChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_practiceMode) return;
        if (ComboPracticeSong.SelectedItem is Song song)
        {
            _practiceSong = song;
            StopPractice(silent: true);
            LoadPracticeScore(song);
        }
    }

    private void LoadPracticeScore(Song song)
    {
        var parsed = ScoreParser.Parse(song.Score, song.Name, song.Bpm, NotationKindText.FromText(song.Notation));
        _practiceScore = parsed;
        TxtPracticeNote.Text = parsed.Notes.Count == 0
            ? "这首曲谱没有可演奏的音"
            : $"当前：共 {parsed.Notes.Count} 个音　{BPM:0} BPM";
        ConfigPracticeCanvas();
    }

    private string BPM => _practiceSong == null ? "--" : _practiceSong.Bpm.ToString("0");

    private void OnPracticeSpeedChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtPracticeSpeed != null) TxtPracticeSpeed.Text = $"{e.NewValue:0.00}x";
        if (_practiceMode) ConfigPracticeCanvas();
    }

    private void OnPracticeSettingChanged(object sender, RoutedEventArgs e)
    {
        if (_practiceMode) ConfigPracticeCanvas();
    }

    private void ConfigPracticeCanvas()
    {
        if (PracticeCanvas == null) return;
        var baseSettings = _lib.Settings.Overlay;
        PracticeCanvas.Settings = new OverlaySettings
        {
            Visible = true,
            Mode = 1,                                   // 练习固定用音游下落
            ShowLanes = true,
            ShowKeyLetters = true,
            ShowTitle = true,
            ShowModifierColors = true,
            MaxStack = 7,
            FallSpeed = Math.Clamp(baseSettings.FallSpeed, 160, 240),
            LookAheadSeconds = Math.Clamp(baseSettings.LookAheadSeconds, 2.5, 3.5),
            ShowJudgmentLine = ChkPracticeMetronome.IsChecked == true,
            ShowFallKeyHint = true,
            HideWhilePlaying = false,
            Background = "#FF0B0F16",
            Opacity = 1.0,
        };
        PracticeCanvas.Map = _lib.EffectiveKeyMap;
        PracticeCanvas.Score = _practiceScore;
        PracticeCanvas.Playback = _engine;
        PracticeCanvas.Combo = _practiceCombo;
        PracticeCanvas.InvalidateVisual();
    }

    // ================================================================ 开始 / 停止

    private void OnPracticeStart(object sender, RoutedEventArgs e)
    {
        if (!_practiceMode)
        {
            SetAppMode(practice: true);
        }
        if (_practiceSong == null)
        {
            SetStatus("先选一首曲谱再开始练习");
            return;
        }

        LoadPracticeScore(_practiceSong);
        if (_practiceScore == null || _practiceScore.Notes.Count == 0)
        {
            SetStatus("这首曲谱没有可演奏的音");
            return;
        }

        ResetPracticeStats();
        var wait = ChkPracticeWait.IsChecked == true;
        var options = new PlaybackOptions
        {
            Speed = Math.Clamp(SldPracticeSpeed.Value, 0.4, 1.5),
            GapMs = 20,
            LeadMs = 10,
            CountdownSeconds = 3,
            RepeatTimes = 1,
            WaitForInput = true,                 // 练习=跟谱语义：只读按键、不注入
            FollowStrictKey = true,
            FollowRequireHold = false,
            FollowCountErrors = true,
            FollowTimeoutSeconds = wait ? 0 : 2, // 不等你时，2 秒没人按就自动过去
            LegatoSameKey = false,
            SuppressMouseModifiers = false,
        };

        ConfigPracticeCanvas();
        _engine.Play(_practiceScore.Notes, _lib.EffectiveKeyMap, _practiceScore.Notation,
            _practiceSong.Bpm, options);
        TxtPracticeVerdict.Text = "3…";
        TxtPracticeVerdict.Foreground = ToBrush("#8FA0B5");
        SetStatus(wait ? "练习开始：等你按对再前进" : "练习开始：按拍前进（2 秒没按会自动过去）");
    }

    private void OnPracticeStop(object sender, RoutedEventArgs e) => StopPractice(silent: false);

    private void StopPractice(bool silent)
    {
        _engine.Stop();
        PracticeCanvas.Combo = 0;
        PracticeCanvas.InvalidateVisual();
        if (!silent)
        {
            TxtPracticeVerdict.Text = "已停止";
            TxtPracticeVerdict.Foreground = ToBrush("#8FA0B5");
            SetStatus($"练习已停止：完美 {_practicePerfect} 良好 {_practiceGood} 错过 {_practiceMiss}，" +
                      $"最高连击 {_practiceBestCombo}，准确率 {PracticeAccuracy()}");
        }
    }

    private void ResetPracticeStats()
    {
        _practicePerfect = _practiceGood = _practiceMiss = 0;
        _practiceCombo = _practiceBestCombo = 0;
        _practiceNoteShownAt = DateTime.UtcNow;
        UpdatePracticeHud();
    }

    // ================================================================ 实时判定

    private void EnsurePracticeHooks()
    {
        if (_practiceHooked) return;
        _practiceHooked = true;

        _engine.NoteStarted += i => Dispatcher.InvokeAsync(() =>
        {
            if (!_practiceMode) return;
            _practiceNoteShownAt = DateTime.UtcNow;
            PracticeCanvas.CurrentIndex = i;
            var note = _practiceScore != null && i >= 0 && i < _practiceScore.Notes.Count ? _practiceScore.Notes[i] : null;
            if (note != null && !note.IsRest)
            {
                var chord = Chord.Resolve(note, _lib.EffectiveKeyMap, _practiceScore!.Notation);
                TxtPracticeNote.Text = $"当前：{chord.Detail}";
            }
        });

        _engine.FollowJudged += (i, verdict, detail) => Dispatcher.InvokeAsync(() =>
        {
            if (_practiceMode) PracticeJudged(verdict, detail);
        });

        _practiceKeyTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(33),
        };
        _practiceKeyTimer.Tick += (_, _) => UpdatePracticeKeys();
        _practiceKeyTimer.Start();
    }

    private void PracticeJudged(string verdict, string detail)
    {
        switch (verdict)
        {
            case "ok":
            {
                var ms = (DateTime.UtcNow - _practiceNoteShownAt).TotalMilliseconds;
                if (ms <= 80)
                {
                    _practicePerfect++;
                    _practiceCombo++;
                    BigVerdict("完美！", "#FFFFD23F");
                }
                else if (ms <= 180)
                {
                    _practiceGood++;
                    _practiceCombo++;
                    BigVerdict("良好", "#FF4CD964");
                }
                else
                {
                    _practiceGood++;
                    _practiceCombo++;
                    BigVerdict($"慢 {ms:0}ms", "#FFFFB300");
                }
                break;
            }
            case "hold":
                _practiceGood++;
                _practiceCombo++;
                BigVerdict("按住整拍 ✓", "#FF4CD964");
                break;
            case "wrong":
                _practiceMiss++;
                _practiceCombo = 0;
                BigVerdict("按错了", "#FFFF5A5A");
                break;
            default:   // miss
                _practiceMiss++;
                _practiceCombo = 0;
                BigVerdict("错过", "#FFFF5A5A");
                break;
        }

        _practiceBestCombo = Math.Max(_practiceBestCombo, _practiceCombo);
        PracticeCanvas.Combo = _practiceCombo;
        PracticeCanvas.InvalidateVisual();
        UpdatePracticeHud();
    }

    private void BigVerdict(string text, string color)
    {
        TxtPracticeVerdict.Text = text;
        TxtPracticeVerdict.Foreground = ToBrush(color);
    }

    private string PracticeAccuracy()
    {
        var total = _practicePerfect + _practiceGood + _practiceMiss;
        if (total == 0) return "--";
        var score = (_practicePerfect + _practiceGood * 0.7) / total * 100;
        return $"{score:0.#}%";
    }

    private void UpdatePracticeHud()
    {
        TxtPracticeCombo.Text = _practiceCombo.ToString();
        TxtPracticeAcc.Text = PracticeAccuracy();
        TxtPracticeStats.Text = $"完美 {_practicePerfect}　良好 {_practiceGood}　错过 {_practiceMiss}　最高连击 {_practiceBestCombo}";
    }

    // ================================================================ 按键可视化

    private void BuildPracticeKeys()
    {
        PracticeKeys.Children.Clear();
        _practiceKeyVisuals.Clear();

        var map = _lib.EffectiveKeyMap;
        for (int i = 0; i < map.Keys.Count && i < 8; i++)
        {
            if (!Enum.TryParse<Key>(map.Keys[i], true, out var key)) continue;
            AddPracticeKey(i == 7 ? "1'（高音）" : $"{i + 1}", KeyInterop.VirtualKeyFromKey(key));
        }
        AddPracticeKey("鼠标左键", VK_LBUTTON);
        AddPracticeKey("鼠标中键", VK_MBUTTON);
        AddPracticeKey("鼠标右键", VK_RBUTTON);
    }

    private void AddPracticeKey(string label, int vk)
    {
        var text = new TextBlock
        {
            Text = label,
            FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var box = new Border
        {
            Width = 96,
            Height = 44,
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(0, 0, 6, 6),
            Background = ToBrush("#FF16202E"),
            BorderBrush = ToBrush("#FF2C3543"),
            BorderThickness = new Thickness(1),
            Child = text,
        };
        PracticeKeys.Children.Add(box);
        _practiceKeyVisuals.Add((box, vk, text));
    }

    private void UpdatePracticeKeys()
    {
        if (!_practiceMode) return;
        foreach (var (box, vk, _) in _practiceKeyVisuals)
        {
            var down = (GetAsyncKeyState(vk) & 0x8000) != 0;
            box.Background = ToBrush(down ? "#FF2F6FB5" : "#FF16202E");
            box.BorderBrush = ToBrush(down ? "#FF7FC4FF" : "#FF2C3543");
        }
    }
}


