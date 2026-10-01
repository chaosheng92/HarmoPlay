using System.Windows.Input;

namespace HarmoPlay.Models;

/// <summary>
/// 固定音高模型（AI 转谱规范）：
///   中音 1 = C4 = MIDI 60；1~7 = C D E F G A B；#/b 为半音升降；英文逗号 / 单引号 为低 / 高八度。
///   可演奏音域 C3~C6 = MIDI 48~84（37 个连续半音）。
///   物理按键：Z X C V B N M = 1~7，英文逗号键 = 1'；鼠标左键 = 降八度、中键 = 升半音、右键 = 升八度，
///   中键可与左/右键组合，左键与右键不可同时使用。
/// </summary>
public static class PitchFingering
{
    public const int MinMidi = 48;   // C3 = 1,
    public const int MaxMidi = 84;   // C6 = #7'
    public const int BaseC4 = 60;

    private static readonly int[] NaturalSemitones = { 0, 2, 4, 5, 7, 9, 11 }; // 简谱 1..7
    private static readonly string[] Names = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

    public static bool IsPlayable(int midi) => midi is >= MinMidi and <= MaxMidi;

    public static string NoteName(int midi)
    {
        if (midi < 0) return "?";
        int octave = midi / 12 - 1;
        return Names[midi % 12] + octave;
    }

    /// <summary>把简谱记号换算成目标 MIDI 音高。</summary>
    public static bool TryToMidi(int degree, string mods, int octave, out int midi, out string error)
    {
        midi = 0;
        error = "";

        // 8 = 高音 1（可视化谱写法）
        if (degree == 8)
        {
            degree = 1;
            octave += 1;
        }
        if (degree is < 1 or > 7)
        {
            error = $"简谱数字 {degree} 不是 1~7";
            return false;
        }

        int semitone = NaturalSemitones[degree - 1];
        foreach (var ch in mods)
        {
            if (ch == '#') semitone += 1;
            else if (ch == 'b') semitone -= 1;
        }

        midi = BaseC4 + semitone + 12 * octave;
        if (!IsPlayable(midi))
        {
            error = $"{NoteName(midi)}（MIDI {midi}）超出可演奏音域 C3~C6（MIDI 48~84）";
            return false;
        }
        return true;
    }

    /// <summary>按固定指法表选择一个物理按法。</summary>
    public static (Key Key, MouseMod Mouse, string KeyName) FingeringFor(int midi, KeyMap map)
    {        string keyName;
        MouseMod mouse;

        if (midi == MaxMidi) // C6 = #7' = M + 右键 + 中键
        {
            keyName = KeyAt(map, 6);
            mouse = MouseMod.Right | MouseMod.Middle;
        }
        else
        {
            int octave = midi >= 72 ? 1 : midi >= 60 ? 0 : -1;
            int semitone = midi - (BaseC4 + 12 * octave); // 0..11
            bool sharp = !NaturalSemitones.Contains(semitone);
            int natural = sharp ? semitone - 1 : semitone;
            int degree = Array.IndexOf(NaturalSemitones, natural) + 1;

            if (octave == 1 && degree == 1)
            {
                // 1' = 英文逗号键（规格表写法）
                keyName = KeyAt(map, 7);
                mouse = sharp ? MouseMod.Middle : MouseMod.None;
            }
            else
            {
                keyName = KeyAt(map, degree - 1);
                mouse = octave switch
                {
                    -1 => MouseMod.Left,
                    1 => MouseMod.Right,
                    _ => MouseMod.None,
                };
                if (sharp) mouse |= MouseMod.Middle;
            }
        }

        if (!Enum.TryParse<Key>(keyName, true, out var key)) key = Key.Z;
        return (key, mouse, keyName);
    }

    private static string KeyAt(KeyMap map, int index) =>
        index >= 0 && index < map.Keys.Count ? map.Keys[index] : "Z";

    /// <summary>是否属于「中键 + 左/右键」的组合按法（部分游戏按不出来）。</summary>
    public static bool IsMouseCombo(MouseMod mouse) =>
        mouse.HasFlag(MouseMod.Middle) && (mouse.HasFlag(MouseMod.Left) || mouse.HasFlag(MouseMod.Right));

    /// <summary>
    /// 选定指法。规则：
    ///   1) 若开启「优先简单指法」（默认），先用 {本音/中键/左键/右键} 里找不用组合的按法；
    ///   2) 找不到且允许组合 → 用规格表的组合指法（技术可行，但人手很难同时按出）；
    ///   3) 找不到且不允许组合 → 判定为无法演奏并说明原因。
    /// </summary>
    public static bool TryFingering(int midi, KeyMap map, out Key key, out MouseMod mouse, out string error)
    {
        key = Key.Z;
        mouse = MouseMod.None;
        error = "";

        if (!IsPlayable(midi))
        {
            error = $"{NoteName(midi)}（MIDI {midi}）超出可演奏音域 C3~C6";
            return false;
        }

        var (k, m, _) = FingeringFor(midi, map);
        if (!IsMouseCombo(m))
        {
            key = k;
            mouse = m;
            return true;
        }

        if (map.PreferSimpleFingering && TryFindSimpleFingering(midi, map, out key, out mouse))
            return true;

        if (map.AllowMouseCombos)
        {
            key = k;
            mouse = m;
            return true;
        }

        key = k;
        mouse = m;
        error = $"{NoteName(midi)} 需要「中键 + 左/右键」组合，而当前键位设置不允许组合键";
        return false;
    }

    /// <summary>该音最终是否必须用组合键（用来自检/校验里提醒用户）。</summary>
    public static bool NeedsMouseCombo(int midi, KeyMap map) =>
        TryFingering(midi, map, out _, out var mouse, out _) && IsMouseCombo(mouse);

    /// <summary>只在 {本音 / 中键 / 左键 / 右键} 里找指法，不含任何组合。</summary>
    private static bool TryFindSimpleFingering(int midi, KeyMap map, out Key key, out MouseMod mouse)
    {
        key = Key.Z;
        mouse = MouseMod.None;
        MouseMod[] candidates = { MouseMod.None, MouseMod.Middle, MouseMod.Left, MouseMod.Right };

        for (int i = 0; i < 8; i++)
        {
            if (!Enum.TryParse<Key>(KeyAt(map, i), true, out var k)) continue;
            int basePitch = i == 7 ? BaseC4 + 12 : BaseC4 + NaturalSemitones[i];

            foreach (var candidate in candidates)
            {
                int delta = candidate == MouseMod.Middle ? 1 : candidate == MouseMod.Left ? -12 : candidate == MouseMod.Right ? 12 : 0;
                if (basePitch + delta != midi) continue;
                key = k;
                mouse = candidate;
                return true;
            }
        }
        return false;
    }

    /// <summary>在当前键位约束下，从 C3 到 C6 哪些半音是弹得出来的（用于文档与提示）。</summary>
    public static List<int> PlayablePitches(KeyMap map)
    {
        var list = new List<int>();
        for (int midi = MinMidi; midi <= MaxMidi; midi++)
            if (TryFingering(midi, map, out _, out _, out _)) list.Add(midi);
        return list;
    }

    public static List<int> UnplayablePitches(KeyMap map)
    {
        var list = new List<int>();
        for (int midi = MinMidi; midi <= MaxMidi; midi++)
            if (!TryFingering(midi, map, out _, out _, out _)) list.Add(midi);
        return list;
    }

    /// <summary>鼠标组合 → 悬浮窗颜色（与游戏灯带一致）。</summary>
    public static string ColorFor(MouseMod mouse) => mouse switch
    {
        MouseMod.None => "#FFFFFF",
        MouseMod.Left => "#4CD964",
        MouseMod.Middle => "#B06CF0",
        MouseMod.Right => "#4A9DFF",
        MouseMod.Left | MouseMod.Middle => "#FFD23F",
        MouseMod.Right | MouseMod.Middle => "#FF5A5A",
        _ => "#FFFFFF",
    };

    public static string LabelFor(MouseMod mouse) => mouse switch
    {
        MouseMod.None => "本音",
        MouseMod.Left => "降八度(左键)",
        MouseMod.Middle => "升半音(中键)",
        MouseMod.Right => "升八度(右键)",
        MouseMod.Left | MouseMod.Middle => "降八度+半音",
        MouseMod.Right | MouseMod.Middle => "升八度+半音",
        _ => "本音",
    };
}


