using System.Windows.Input;

namespace HarmoPlay.Models;

/// <summary>
/// 固定音高模型（鼠鼠口琴谱规格）：
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

    /// <summary>按鼠鼠口琴谱的固定指法表选择一个物理按法。</summary>
    public static (Key Key, MouseMod Mouse, string KeyName) FingeringFor(int midi, KeyMap map)
    {
        string keyName;
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
