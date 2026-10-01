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

    public bool IsRunning => _thread is { IsAlive: true };
    public bool IsPaused => _pause;
    public int CurrentIndex { get; private set; } = -1;
    public int CurrentPass { get; private set; }

    /// <summary>当前曲谱的记谱法。</summary>
    public NotationKind Notation => _notation;
    /// <summary>一拍多少毫秒（已计入速度倍率），供悬浮窗下落模式换算坐标。</summary>
    public double BeatMs => _beatMs;
    /// <summary>整首曲谱总时长（毫秒）。</summary>
    public double TotalMs { get; private set; }

    /// <summary>演奏时间轴上的当前位置（毫秒，暂停时冻结）。</summary>
    public double PositionMs
    {
        get
        {
            if (!IsRunning && !_watch.IsRunning) return 0;
            double now = _watch.Elapsed.TotalMilliseconds - _pauseOffsetMs;
            if (_pause) now -= _watch.Elapsed.TotalMilliseconds - _pauseStartMs;
            return Math.Max(0, now);
        }
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
        _beatMs = 60000.0 / _bpm / Math.Max(0.05, _options.Speed);
        TotalMs = notes.Count == 0 ? 0 : (notes[^1].StartBeat + notes[^1].Beats) * _beatMs;
        _stop = false;
        _pause = false;
        _pauseOffsetMs = 0;
        _pressed = Chord.Empty;
        _inProgress = -1;
        CurrentIndex = -1;
        CurrentPass = 0;
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

                if (pass == 1 && _options.CountdownSeconds > 0 && !waitMode)
                {
                    for (int s = _options.CountdownSeconds; s > 0 && !_stop; s--)
                    {
                        Status?.Invoke($"倒计时 {s} 秒…");
                        if (!Sleep(1000)) return;
                    }
                }

                Status?.Invoke(waitMode ? "跟练模式：请按出高亮的音" : "演奏中");

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
                        bool ok = WaitForUser(chord, startMs);
                        NoteStarted?.Invoke(i);
                        NoteFinished?.Invoke(i);
                        if (!ok) return;
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
                    if (!WaitUntil(Math.Max(holdEnd, startMs + Math.Max(_options.MinHoldMs, 0)))) return;

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
                Status?.Invoke($"第 {pass} 遍完成");

                if (_options.RepeatTimes > 0 && pass >= _options.RepeatTimes) break;
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

    /// <summary>跟练模式：等待用户按对（超时 20 秒则跳过）。</summary>
    private bool WaitForUser(Chord chord, double startMs)
    {
        Status?.Invoke($"请按 {chord.Detail}");
        double deadline = _watch.Elapsed.TotalMilliseconds - _pauseOffsetMs + 20000;
        while (!_stop)
        {
            if (_pause && !_gate.Wait(100)) continue;
            if (chord.IsKeyHeldByUser()) return true;
            if (_watch.Elapsed.TotalMilliseconds - _pauseOffsetMs > deadline)
            {
                Status?.Invoke($"超时跳过（应为 {chord.Detail}）");
                return true;
            }
            Thread.Sleep(4);
        }
        return false;
    }

    private bool WaitUntil(double targetMs)
    {
        while (true)
        {
            if (_stop) return false;
            if (_pause)
            {
                if (!_gate.Wait(100)) continue;
            }
            double now = _watch.Elapsed.TotalMilliseconds - _pauseOffsetMs;
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
