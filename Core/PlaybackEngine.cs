using System.Diagnostics;
using System.Windows.Input;
using HarmoPlay.Models;

namespace HarmoPlay.Core;

/// <summary>演奏引擎：按 BPM 定时发送按键（自动演奏）或等待用户按键（跟练）。</summary>
public sealed class PlaybackEngine : IDisposable
{
    private Thread? _thread;
    private volatile bool _stop;
    private volatile bool _pause;
    private readonly ManualResetEventSlim _gate = new(true);
    private readonly Stopwatch _watch = new();
    private long _pauseOffsetMs;
    private long _pauseStartMs;

    private IReadOnlyList<ScoreNote> _notes = Array.Empty<ScoreNote>();
    private KeyMap _map = KeyMap.CreateDelta();
    private PlaybackOptions _options = new();
    private double _bpm = 90;
    private NotationKind _notation = NotationKind.Physical;
    private double _beatMs = 500;

    /// <summary>用户设定的目标速度（新手模式会从更慢的速度逐遍逼近它）。</summary>
    private double _speedTarget = 1.0;
    /// <summary>当前这一遍实际用的速度倍率。</summary>
    public double CurrentSpeed { get; private set; } = 1.0;
    /// <summary>新手模式：已经检查过的音符数（只统计"有没有跟着按"，不判定对错）。</summary>
    public int BeginnerChecked { get; private set; }
    /// <summary>新手模式：用户在音长内跟着按对了的音符数。</summary>
    public int BeginnerFollowed { get; private set; }
    public double BeginnerFollowRate => BeginnerChecked == 0 ? 0 : BeginnerFollowed * 100.0 / BeginnerChecked;

    private Chord _pressed = Chord.Empty;
    private int _inProgress = -1;

    /// <summary>保护"按下/松开"这一对操作，避免 UI 线程（停止/暂停）与引擎线程抢着改状态导致按键卡住。</summary>
    private readonly object _inputLock = new();
    private readonly Thread _watchdog;
    private volatile bool _disposed;

    public PlaybackEngine()
    {
        _watchdog = new Thread(WatchdogLoop)
        {
            IsBackground = true,
            Name = "HarmoPlay.InputWatchdog",
            Priority = ThreadPriority.BelowNormal,
        };
        _watchdog.Start();
    }

    /// <summary>看门狗：只要引擎没在跑，就保证没有任何键/鼠标键被按住。</summary>
    private void WatchdogLoop()
    {
        while (!_disposed)
        {
            try
            {
                Thread.Sleep(250);
                if (IsRunning) continue;
                lock (_inputLock)
                {
                    if (_heldKeys.Count > 0 || _mouseHeld != MouseMod.None)
                    {
                        Program.Trace($"看门狗回收残留按键：keys={string.Join(",", _heldKeys)} 鼠标={_mouseHeld}");
                        ReleaseAll();
                    }
                }
            }
            catch (Exception ex)
            {
                Program.Trace("看门狗异常：" + ex.Message);
            }
        }
    }

    public event Action<int>? NoteStarted;
    public event Action<int>? NoteFinished;
    public event Action<int>? PassFinished;
    public event Action? Finished;
    public event Action<string>? Status;
    public event Action<int, int>? Progress;
    /// <summary>跟谱弹奏判定：index、结论（ok / wrong / miss / hold）、说明。</summary>
    public event Action<int, string, string>? FollowJudged;

    public int FollowCorrect { get; private set; }
    public int FollowWrong { get; private set; }
    public int FollowMissed { get; private set; }

    public void ResetFollowStats()
    {
        FollowCorrect = 0;
        FollowWrong = 0;
        FollowMissed = 0;
    }

    public bool IsRunning => _thread is { IsAlive: true };
    public bool IsPaused => _pause;
    public int CurrentIndex { get; private set; } = -1;
    public int CurrentPass { get; private set; }

    /// <summary>当前曲谱的记谱法。</summary>
    public NotationKind Notation => _notation;
    /// <summary>是否处于跟谱弹奏模式。</summary>
    public bool FollowMode => _options.WaitForInput;
    /// <summary>是否处于新手模式（按对才继续、永不跳过）。</summary>
    public bool BeginnerModeActive => _options.BeginnerMode;
    /// <summary>开播倒计时剩余秒数（0 = 没在倒计时），供悬浮窗显示大字。</summary>
    public int CountdownValue { get; private set; }
    /// <summary>一拍多少毫秒（已计入速度倍率），供悬浮窗下落模式换算坐标。</summary>
    public double BeatMs => _beatMs;
    /// <summary>整首曲谱总时长（毫秒）。</summary>
    public double TotalMs { get; private set; }

    /// <summary>音乐时间轴基准：倒计时结束后（以及每一遍开始时）把秒表对齐到这里，
    /// 否则倒计时的那几秒会被算成曲谱时间，导致开头的音"抢跑"。</summary>
    private double _timelineOffsetMs;

    /// <summary>演奏时间轴上的当前位置（毫秒，暂停时冻结）。
    /// 开播倒计时期间为**负值**（时间轴 0 点 = 倒计时结束那一刻）：谱面照常下落，但还没开始判定。</summary>
    public double PositionMs
    {
        get
        {
            // 等你按键时把画面冻住：音符停在判定线上不动，直到你按对/按住
            if (_freezeTimeline) return _freezeAt;
            if (!IsRunning && !_watch.IsRunning) return 0;
            double now = _watch.Elapsed.TotalMilliseconds - _timelineOffsetMs - _pauseOffsetMs;
            if (_pause) now -= _watch.Elapsed.TotalMilliseconds - _pauseStartMs;
            return now;
        }
    }

    private bool _freezeTimeline;
    private double _freezeAt;

    /// <summary>冻住曲谱时间轴（跟谱/新手模式等你按键时用，画面与判定都停住）。</summary>
    private void BeginFreeze(double? at = null)
    {
        _freezeAt = at ?? (_freezeTimeline ? _freezeAt : PositionMs);
        _freezeTimeline = true;
    }

    /// <summary>解冻：把等待的这段时间从时间轴里扣掉，后面音符仍按自己的节拍来。</summary>
    private void EndFreeze()
    {
        if (!_freezeTimeline) return;
        _timelineOffsetMs = _watch.Elapsed.TotalMilliseconds - _pauseOffsetMs - _freezeAt;
        _freezeTimeline = false;
    }

    public void Play(IReadOnlyList<ScoreNote> notes, KeyMap map, double bpm, PlaybackOptions options)
        => Play(notes, map, NotationKind.Physical, bpm, options);

    public void Play(IReadOnlyList<ScoreNote> notes, KeyMap map, NotationKind notation, double bpm, PlaybackOptions options)
    {
        Stop();
        _notes = notes;
        _map = map.Clone();
        _notation = notation;
        _bpm = bpm <= 0 ? 90 : bpm;
        _options = options.Clone();
        _speedTarget = Math.Max(0.2, _options.Speed);
        // 新手模式：从较慢的速度起步，每一遍自动加速一点，直到用户设定的目标速度
        CurrentSpeed = _options.BeginnerMode
            ? Math.Min(_speedTarget, Math.Max(0.3, _options.BeginnerStartSpeed))
            : _speedTarget;
        BeginnerChecked = 0;
        BeginnerFollowed = 0;
        _beatMs = 60000.0 / _bpm / CurrentSpeed;
        TotalMs = notes.Count == 0 ? 0 : (notes[^1].StartBeat + notes[^1].Beats) * _beatMs;
        _stop = false;
        _pause = false;
        _pauseOffsetMs = 0;
        _timelineOffsetMs = 0;
        _pressed = Chord.Empty;
        _inProgress = -1;
        CurrentIndex = -1;
        CurrentPass = 0;
        ResetFollowStats();
        _gate.Set();
        _thread = new Thread(Run) { IsBackground = true, Name = "HarmoPlay.Playback", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public void Pause()
    {
        if (!IsRunning || _pause) return;
        _pauseStartMs = _watch.ElapsedMilliseconds;
        _pause = true;
        _gate.Reset();
        ReleaseAll();
        Status?.Invoke("已暂停");
    }

    public void Resume()
    {
        if (!_pause) return;
        _pauseOffsetMs += _watch.ElapsedMilliseconds - _pauseStartMs;
        _pause = false;
        _gate.Set();
        if (_inProgress >= 0 && !_options.WaitForInput && !_pressed.IsEmpty)
            ApplyDown(_pressed);
        Status?.Invoke("演奏中");
    }

    public void Stop()
    {
        _stop = true;
        _pause = false;
        _gate.Set();
        var t = _thread;
        if (t != null && t.IsAlive && t != Thread.CurrentThread)
            t.Join(500);
        _thread = null;
        lock (_inputLock)
        {
            ReleaseAll();
        }
        CurrentIndex = -1;
        _inProgress = -1;
    }

    /// <summary>急停：停止 + 强制松开所有按键与鼠标键（含兜底扫描）。</summary>
    public void PanicRelease(string reason = "急停")
    {
        Stop();
        InputGuard.ReleaseEverything(NoteVirtualKeys(), reason);
    }

    /// <summary>当前键位预设里所有会用到的按键（急停/退出时兜底松开）。</summary>
    public IEnumerable<int> NoteVirtualKeys() =>
        _map.Keys.Select(k => Enum.TryParse<Key>(k, true, out var key) ? KeyInterop.VirtualKeyFromKey(key) : 0)
                  .Where(vk => vk != 0);

    /// <summary>解析音符对应的按键组合（含"绝不碰鼠标"选项）。</summary>
    private Chord ChordFor(ScoreNote note)
    {
        var chord = Chord.Resolve(note, _map, _notation);
        if (_options.SuppressMouseModifiers && chord.Mouse != MouseMod.None)
            chord = chord.WithoutMouse();
        return chord;
    }

    private void Run()
    {
        InputSender.BeginHighResolutionTimer();
        try
        {
            _watch.Restart();
            double beatMs = _beatMs;
            bool waitMode = _options.WaitForInput;
            int pass = 0;

            while (!_stop)
            {
                pass++;
                CurrentPass = pass;

                if (pass == 1 && _options.CountdownSeconds > 0)
                {
                    // 音乐时间轴的 0 点 = 倒计时结束的那一刻。
                    // 于是倒计时期间 PositionMs 为负：下落谱面会先把音符"落"到判定线上，
                    // 数到 0 时刚好落到线，才开始判定/演奏 —— 既不抢跑，也留出了准备时间。
                    _timelineOffsetMs = _watch.Elapsed.TotalMilliseconds + _options.CountdownSeconds * 1000.0;

                    for (int s = _options.CountdownSeconds; s > 0 && !_stop; s--)
                    {
                        CountdownValue = s;
                        Status?.Invoke($"倒计时 {s} 秒…（准备好，{(waitMode ? "跟谱弹奏" : "自动弹奏")}即将开始）");
                        // 精确等到这一秒结束（用时间轴坐标：-(s-1) 秒），避免 Sleep(1000) 累积误差
                        if (!WaitUntil(-(s - 1) * 1000.0)) { CountdownValue = 0; return; }
                    }
                    CountdownValue = 0;
                }
                else
                {
                    // 重复播放的每一遍：时间轴各自从这里起算
                    _timelineOffsetMs = _watch.Elapsed.TotalMilliseconds;
                }

                Status?.Invoke(waitMode ? "跟谱弹奏：请按出高亮的音" : "自动弹奏：程序正在按键");

                for (int i = 0; i < _notes.Count && !_stop; i++)
                {
                    if (_pause && !_gate.Wait(100)) continue;
                    if (_stop) return;

                    var note = _notes[i];
                    CurrentIndex = i;
                    Progress?.Invoke(i, _notes.Count);
                    double startMs = note.StartBeat * beatMs;
                    double endMs = (note.StartBeat + note.Beats) * beatMs;
                    double lead = i == 0 ? 0 : _options.LeadMs;

                    if (!WaitUntil(startMs - lead)) return;

                    if (note.IsRest)
                    {
                        NoteStarted?.Invoke(i);
                        if (!WaitUntil(endMs - _options.GapMs)) return;
                        NoteFinished?.Invoke(i);
                        continue;
                    }

                    var chord = ChordFor(note);
                    if (chord.Unplayable)
                    {
                        Status?.Invoke($"跳过超音域音「{note.Raw}」：{chord.Error}");
                        NoteStarted?.Invoke(i);
                        NoteFinished?.Invoke(i);
                        continue;
                    }

                    if (waitMode)
                    {
                        _inProgress = i;
                        NoteStarted?.Invoke(i);
                        if (!FollowPlay(note, chord, i)) return;
                        NoteFinished?.Invoke(i);
                        continue;
                    }

                    if (!_options.SimulateKeys)
                    {
                        // 练习：按时间轴走，只读取按键判定，绝不注入任何按键
                        _inProgress = i;
                        NoteStarted?.Invoke(i);
                        if (!JudgePlay(chord, i, endMs)) return;
                        NoteFinished?.Invoke(i);
                        continue;
                    }

                    if (!chord.SameAs(_pressed))
                    {
                        ReleaseAll();
                        ApplyDown(chord);
                        _pressed = chord;
                    }
                    _inProgress = i;
                    NoteStarted?.Invoke(i);

                    double holdEnd = endMs - _options.GapMs;
                    double holdTarget = Math.Max(holdEnd, startMs + Math.Max(_options.MinHoldMs, 0));

                    if (_options.BeginnerMode)
                    {
                        // 新手模式：程序照常按键，同时在音长里观察用户有没有跟着按（只统计，不判定）
                        BeginnerChecked++;
                        if (!WaitHoldAndWatchUser(chord, holdTarget)) return;
                    }
                    else if (!WaitUntil(holdTarget)) return;

                    bool chain = _options.LegatoSameKey && i + 1 < _notes.Count
                                 && !_notes[i + 1].IsRest
                                 && Math.Abs(_notes[i + 1].StartBeat - (note.StartBeat + note.Beats)) < 0.0005
                                 && ChordFor(_notes[i + 1]).SameAs(chord);
                    if (!chain)
                    {
                        ReleaseAll();
                        _pressed = Chord.Empty;
                    }
                    _inProgress = -1;
                    NoteFinished?.Invoke(i);
                }

                if (_stop) return;
                ReleaseAll();
                _pressed = Chord.Empty;
                PassFinished?.Invoke(pass);
                Status?.Invoke(_options.BeginnerMode
                    ? $"第 {pass} 遍完成：跟上 {BeginnerFollowed}/{BeginnerChecked}" +
                      (BeginnerChecked > 0 ? $"（{BeginnerFollowed * 100.0 / BeginnerChecked:0}%）" : "") +
                      $"，当前速度 {CurrentSpeed:0.00}x"
                    : $"第 {pass} 遍完成");

                if (_options.RepeatTimes > 0 && pass >= _options.RepeatTimes) break;

                // 新手模式：可选地每遍快一点（BeginnerSpeedStep > 0 才提速）
                if (_options.BeginnerMode && _options.BeginnerSpeedStep > 0 && CurrentSpeed < _speedTarget - 0.001)
                {
                    CurrentSpeed = Math.Min(_speedTarget, CurrentSpeed + Math.Max(0.05, _options.BeginnerSpeedStep));
                    _beatMs = 60000.0 / _bpm / Math.Max(0.05, CurrentSpeed);
                    BeginnerChecked = 0;
                    BeginnerFollowed = 0;
                    Status?.Invoke($"新手模式：下一遍提速到 {CurrentSpeed:0.00}x（目标 {_speedTarget:0.00}x）");
                }

                if (!Sleep(600)) return;
            }

            Finished?.Invoke();
        }
        catch (Exception ex)
        {
            Status?.Invoke("演奏中断：" + ex.Message);
        }
        finally
        {
            ReleaseAll();
            InputSender.EndHighResolutionTimer();
            _thread = null;
        }
    }

    /// <summary>跟谱弹奏：等用户按对当前的音（可配严格判定、按住整拍、按错提示、超时跳过）。</summary>
    private bool FollowPlay(ScoreNote note, Chord chord, int index)
    {
        // 关键：等用户的时候把"曲谱时间轴"冻住（把这段等待从时间轴里扣掉）。
        // 否则你卡在某个音上几秒后，时间轴已经跑远，按对的一瞬间后面几个音会一起冲出来
        // ——看起来就是"没按到也往下走"。
        double waitStart = _watch.Elapsed.TotalMilliseconds;
        BeginFreeze();
        try
        {
            return FollowPlayCore(note, chord, index);
        }
        finally
        {
            EndFreeze();
            _ = waitStart;
        }
    }

    private bool FollowPlayCore(ScoreNote note, Chord chord, int index)
    {
        Status?.Invoke($"跟谱弹奏：请弹 {chord.Detail}");
        // 新手模式：永不跳过（一直等你按对）；普通跟谱：按设定超时跳过
        double timeoutMs = _options.BeginnerMode
            ? double.MaxValue
            : _options.FollowTimeoutSeconds <= 0 ? double.MaxValue : _options.FollowTimeoutSeconds * 1000.0;
        double waited = 0;
        string? lastWrong = null;
        // 每个音都要求"新的一次按下"：如果本音要的键此刻已经被按住（上一个音还没松手），
        // 必须先松开再按才算数 —— 否则会出现"上个音没松手，后面的同键音也算对"。
        bool needFreshPress = chord.IsKeyHeldByUser();
        Program.Trace($"跟谱等待：第 {index + 1} 个音「{note.Raw}」应弹 {chord.Text}" +
                      $"（新手={_options.BeginnerMode} 超时={(timeoutMs > 1e12 ? "永不跳过" : timeoutMs / 1000 + "秒")}" +
                      (needFreshPress ? " 需要先松开当前按键" : "") + "）");

        while (!_stop)
        {
            if (_pause && !_gate.Wait(50)) continue;
            if (_stop) return false;

            bool stillHoldingFromBefore = needFreshPress && chord.IsKeyHeldByUser();
            if (!stillHoldingFromBefore) needFreshPress = false;

            bool hit = !stillHoldingFromBefore
                       && (_options.FollowStrictKey ? chord.IsHeldByUser() : chord.IsKeyHeldByUser());
            if (hit)
            {
                // 按对的一瞬间就解冻：谱面立刻继续往前走，这样你能看出这个音要按多久
                EndFreeze();
                FollowCorrect++;
                if (_options.FollowRequireHold)
                {
                    bool held = HoldForDuration(note, chord, index);
                    if (!held)
                    {
                        if (_options.BeginnerMode)
                        {
                            // 新手模式：没按住整拍就停在这个音，等你重新按对并按住（绝不跳过）
                            FollowCorrect--;
                            FollowMissed--;      // 撤销 HoldForDuration 记下的"漏"
                            Status?.Invoke($"「{note.Raw}」要按住整拍，请重新按对并按住不放");
                            WaitRelease(chord);
                            BeginFreeze(note.StartBeat * _beatMs);   // 回到这个音的位置再等你
                            continue;
                        }
                        return false;
                    }
                }
                FollowJudged?.Invoke(index, "ok", chord.Detail);
                Program.Trace($"跟谱完成：第 {index + 1} 个音（你等了 {waited:0} ms）");
                WaitRelease(chord);
                return true;
            }

            if (_options.FollowCountErrors)
            {
                var wrong = WrongKeyDown(chord);
                if (wrong != null && wrong != lastWrong)
                {
                    lastWrong = wrong;
                    FollowWrong++;
                    FollowJudged?.Invoke(index, "wrong", $"按了 {wrong}，应为 {chord.Text}");
                    Status?.Invoke($"按错了：你按的是 {wrong}，这里应该弹 {chord.Detail}");
                }
                else if (wrong == null)
                {
                    lastWrong = null;
                }
            }

            waited += 8;
            if (waited > timeoutMs)
            {
                FollowMissed++;
                FollowJudged?.Invoke(index, "miss", "超时跳过");
                Status?.Invoke($"超时，跳过「{note.Raw}」（应为 {chord.Detail}）");
                Program.Trace($"跟谱跳过：第 {index + 1} 个音「{note.Raw}」等了 {waited:0} ms 还没按对 → 跳过");
                return true;
            }
            Thread.Sleep(8);
        }
        return false;
    }

    /// <summary>
    /// 练习（只判定不注入）：音符按时间轴自动前进，程序在此期间只读取你的按键，
    /// 按对 = ok（再按时间差评完美/良好）、按错 = wrong、窗口内没按 = miss。
    /// </summary>
    private bool JudgePlay(Chord chord, int index, double endMs)
    {
        Status?.Invoke($"练习：请弹 {chord.Detail}");
        string? lastWrong = null;

        while (!_stop)
        {
            if (_pause && !_gate.Wait(20)) continue;
            if (_stop) return false;

            if (chord.IsHeldByUser())
            {
                FollowCorrect++;
                FollowJudged?.Invoke(index, "ok", chord.Detail);
                return true;
            }

            if (_options.FollowCountErrors)
            {
                var wrong = WrongKeyDown(chord);
                if (wrong != null && wrong != lastWrong)
                {
                    lastWrong = wrong;
                    FollowWrong++;
                    FollowJudged?.Invoke(index, "wrong", $"按了 {wrong}，应为 {chord.Text}");
                    Status?.Invoke($"按错了：你按的是 {wrong}，这里应该弹 {chord.Detail}");
                }
                else if (wrong == null)
                {
                    lastWrong = null;
                }
            }

            if (PositionMs >= endMs) break;   // 这个音的窗口结束了
            Thread.Sleep(6);
        }

        FollowMissed++;
        FollowJudged?.Invoke(index, "miss", "没按");
        return true;
    }

    /// <summary>等到 holdTarget，同时在音长里观察用户有没有跟着按对（新手模式：只统计不判定）。</summary>
    private bool WaitHoldAndWatchUser(Chord chord, double holdTarget)
    {
        bool followed = false;
        while (true)
        {
            if (_stop) return false;
            if (_pause && !_gate.Wait(20)) continue;

            if (!followed && chord.IsKeyHeldByUser())
            {
                followed = true;
                BeginnerFollowed++;
            }

            double now = _watch.Elapsed.TotalMilliseconds - _timelineOffsetMs - _pauseOffsetMs;
            double remain = holdTarget - now;
            if (remain <= 0) return true;
            if (!followed && remain > 4) Thread.Sleep(4);
            else if (remain > 16) Thread.Sleep(4);
            else if (remain > 2) Thread.Sleep(1);
            else Thread.SpinWait(150);
        }
    }

    /// <summary>按住整拍判定：按不够就记一次"漏"。</summary>
    private bool HoldForDuration(ScoreNote note, Chord chord, int index)    {
        double need = Math.Max(120, note.Beats * _beatMs);
        double held = 0;
        while (!_stop && held < need)
        {
            if (_pause && !_gate.Wait(50)) continue;
            if (!chord.IsKeyHeldByUser())
            {
                FollowMissed++;
                if (!_options.BeginnerMode)
                {
                    FollowJudged?.Invoke(index, "miss", "没按住整拍");
                    Status?.Invoke($"「{note.Raw}」没按住整拍（需要 {need:0} 毫秒）");
                }
                // 新手模式返回 false：调用方会撤销这次"漏"，停在这个音等你重按
                return !_options.BeginnerMode;
            }
            Thread.Sleep(8);
            held += 8;
        }
        FollowJudged?.Invoke(index, "hold", $"{need:0} 毫秒");
        return true;
    }

    /// <summary>等用户松开，避免同一次按键被下一个音重复计数。</summary>
    private void WaitRelease(Chord chord)
    {
        // 等用户松开（最多 6 秒，避免卡住）；即使超时，下一个音也会因为
        // "必须重新按下"的判定而要求一次新的按键。
        int guard = 0;
        while (!_stop && chord.IsKeyHeldByUser() && guard++ < 750)
            Thread.Sleep(8);
    }

    /// <summary>检测是否按了琴键里"别的"键（用于跟谱弹奏的按错提示）。</summary>
    private string? WrongKeyDown(Chord expected)
    {
        foreach (var name in _map.Keys)
        {
            if (!Enum.TryParse<Key>(name, true, out var key) || key == Key.None) continue;
            if (key == expected.Key) continue;
            if (InputSender.IsDown(key)) return ScoreParser.PrettyKey(name);
        }
        return null;
    }

    /// <summary>跟谱弹奏：等待用户按对（旧接口，保留兼容）。</summary>
    private bool WaitForUser(Chord chord, double startMs) => FollowPlay(new ScoreNote { Raw = "", Degree = 0 }, chord, -1);

    private bool WaitUntil(double targetMs)
    {
        while (true)
        {
            if (_stop) return false;
            if (_pause)
            {
                if (!_gate.Wait(100)) continue;
            }
            double now = _watch.Elapsed.TotalMilliseconds - _timelineOffsetMs - _pauseOffsetMs;
            double remain = targetMs - now;
            if (remain <= 0) return true;
            if (remain > 16) Thread.Sleep(5);
            else if (remain > 2) Thread.Sleep(1);
            else Thread.SpinWait(150);
        }
    }

    private bool Sleep(int ms)
    {
        int elapsed = 0;
        while (elapsed < ms)
        {
            if (_stop) return false;
            if (_pause) { _gate.Wait(100); continue; }
            Thread.Sleep(10);
            elapsed += 10;
        }
        return !_stop;
    }

    private void ApplyDown(Chord chord)
    {
        lock (_inputLock)
        {
            if (chord.IsEmpty) return;
            if (chord.Ctrl) Hold(0x11);
            if (chord.Alt) Hold(0x12);
            if (chord.Shift) Hold(0x10);
            InputSender.MouseDown(chord.Mouse);
            _mouseHeld |= chord.Mouse;
            Hold(KeyInterop.VirtualKeyFromKey(chord.Key));
        }
    }

    private void Hold(int vk)
    {
        if (vk == 0) return;
        if (!_heldKeys.Contains(vk))
        {
            _heldKeys.Add(vk);
            InputSender.KeyDown((ushort)vk);
        }
    }

    private void ReleaseAll()
    {
        lock (_inputLock)
        {
            // 只松开自己按下去的键，顺序与按下相反（先琴键，后修饰键）
            for (int i = _heldKeys.Count - 1; i >= 0; i--)
                InputSender.KeyUp((ushort)_heldKeys[i]);
            _heldKeys.Clear();
            if (_mouseHeld != MouseMod.None)
            {
                InputSender.MouseUp(_mouseHeld);
                _mouseHeld = MouseMod.None;
            }
        }
    }

    private readonly List<int> _heldKeys = new();
    private MouseMod _mouseHeld = MouseMod.None;

    public void Dispose()
    {
        _disposed = true;
        Stop();
        PanicRelease("程序退出");
        _gate.Dispose();
    }
}

