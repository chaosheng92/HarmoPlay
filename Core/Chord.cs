using System.Windows.Input;
using HarmoPlay.Models;

namespace HarmoPlay.Core;

/// <summary>一个音最终要按下的「按键 + 修饰键」组合。</summary>
public sealed class Chord
{
    public static readonly Chord Empty = new() { Key = Key.None };

    public Key Key { get; init; } = Key.None;
    public MouseMod Mouse { get; init; }
    public bool Ctrl { get; init; }
    public bool Alt { get; init; }
    public bool Shift { get; init; }
    public string Color { get; init; } = "#FFFFFF";
    public string ModLabel { get; init; } = "本音";

    /// <summary>固定音高记谱时的目标 MIDI（-1 = 未计算）。</summary>
    public int Midi { get; init; } = -1;
    /// <summary>超出 C3~C6 或无法表达时置位。</summary>
    public bool Unplayable { get; init; }
    public string Error { get; init; } = "";

    public bool IsEmpty => Key == Key.None;

    public string KeyText => IsEmpty ? "—" : ScoreParser.PrettyKey(Key.ToString()).ToUpperInvariant();

    public string Suffix => ScoreParser.ModSuffix(ModText(Mouse));

    public string Text => KeyText + Suffix;

    public string PitchName => Midi >= 0 ? PitchFingering.NoteName(Midi) : "";

    /// <summary>如「中键 + Z」。</summary>
    public string Detail
    {
        get
        {
            if (Unplayable) return Error;
            var parts = new List<string>();
            if (Mouse.HasFlag(MouseMod.Middle)) parts.Add("中键");
            if (Mouse.HasFlag(MouseMod.Left)) parts.Add("左键");
            if (Mouse.HasFlag(MouseMod.Right)) parts.Add("右键");
            if (Ctrl) parts.Add("Ctrl");
            if (Alt) parts.Add("Alt");
            if (Shift) parts.Add("Shift");
            parts.Add(KeyText);
            var text = string.Join(" + ", parts);
            return Midi >= 0 ? $"{text}   （{PitchName}）" : text;
        }
    }

    private static string ModText(MouseMod m) => m switch
    {
        MouseMod.None => "",
        MouseMod.Left => "b",
        MouseMod.Right => "^",
        MouseMod.Middle => "#",
        MouseMod.Left | MouseMod.Middle => "#b",
        MouseMod.Right | MouseMod.Middle => "#^",
        _ => "",
    };

    public bool SameAs(Chord other) =>
        Key == other.Key && Mouse == other.Mouse && Ctrl == other.Ctrl && Alt == other.Alt && Shift == other.Shift;

    /// <summary>去掉鼠标修饰键（用户选择"绝不碰鼠标"时使用，会丢失变调）。</summary>
    public Chord WithoutMouse() => new()
    {
        Key = Key,
        Mouse = MouseMod.None,
        Ctrl = Ctrl,
        Alt = Alt,
        Shift = Shift,
        Midi = Midi,
        Color = Mouse == MouseMod.None ? Color : "#FFFFFF",
        ModLabel = Mouse == MouseMod.None ? ModLabel : "本音（已忽略变调）",
    };

    public static Chord Resolve(ScoreNote note, KeyMap map, NotationKind notation)
    {
        if (note.IsRest) return Empty;
        return notation == NotationKind.Pitch ? ResolvePitch(note, map) : ResolvePhysical(note, map);
    }

    /// <summary>固定音高记谱：算目标音高，再按规格表自动选指法（会避开游戏按不出的组合）。</summary>
    private static Chord ResolvePitch(ScoreNote note, KeyMap map)
    {
        if (!PitchFingering.TryToMidi(note.Degree, note.Mods, note.Octave, out var midi, out var error))
            return new Chord { Key = Key.None, Unplayable = true, Error = error, Color = "#FF5A5A", ModLabel = "超音域" };

        if (!PitchFingering.TryFingering(midi, map, out var key, out var mouse, out var fingeringError))
            return new Chord { Key = Key.None, Unplayable = true, Error = fingeringError, Midi = midi, Color = "#FF5A5A", ModLabel = "弹不出" };

        return new Chord
        {
            Key = key,
            Mouse = mouse,
            Midi = midi,
            Color = PitchFingering.ColorFor(mouse),
            ModLabel = PitchFingering.LabelFor(mouse),
        };
    }

    /// <summary>直接按键记谱（三角洲可视化 / D-hydra）：前缀就是物理按键。</summary>
    private static Chord ResolvePhysical(ScoreNote note, KeyMap map)
    {
        int idx = Math.Clamp(note.Degree, 1, 8) - 1;
        var keyName = map.Keys.Count > idx ? map.Keys[idx] : "Z";
        if (!Enum.TryParse<Key>(keyName, true, out var key)) key = Key.Z;

        bool l = false, r = false, m = false, ctrl = false, alt = false, shift = false;
        string color = "#FFFFFF";
        string modLabel = "本音";

        void Apply(AccidentalMod? mod)
        {
            if (mod == null) return;
            l |= mod.MouseLeft;
            r |= mod.MouseRight;
            m |= mod.MouseMiddle;
            ctrl |= mod.Ctrl;
            alt |= mod.Alt;
            shift |= mod.Shift;
            if (mod.MouseLeft || mod.MouseRight || mod.MouseMiddle || mod.Ctrl || mod.Alt || mod.Shift)
            {
                color = mod.Color;
                modLabel = mod.Label;
            }
        }

        Apply(map.ModFor(note.Mods));
        if (note.Octave > 0) Apply(map.ModFor("'"));
        if (note.Octave < 0) Apply(map.ModFor(","));

        var mouse = (l ? MouseMod.Left : 0) | (r ? MouseMod.Right : 0) | (m ? MouseMod.Middle : 0);
        int midi = -1;
        if (!note.IsRest) TryPhysicalMidi(note, mouse, out midi);

        return new Chord
        {
            Key = key,
            Mouse = mouse,
            Ctrl = ctrl,
            Alt = alt,
            Shift = shift,
            Color = color,
            ModLabel = modLabel,
            Midi = midi,
        };
    }

    /// <summary>直接按键记谱下估算实际音高（按当前键位预设的鼠标含义，仅用于显示）。</summary>
    private static bool TryPhysicalMidi(ScoreNote note, MouseMod mouse, out int midi)
    {
        midi = -1;
        int idx = Math.Clamp(note.Degree, 1, 8) - 1;
        int semitone = idx == 7 ? 12 : new[] { 0, 2, 4, 5, 7, 9, 11 }[idx];
        if (mouse.HasFlag(MouseMod.Left)) semitone -= 12;   // 左键 = 降八度
        if (mouse.HasFlag(MouseMod.Right)) semitone += 12;  // 右键 = 升八度
        if (mouse.HasFlag(MouseMod.Middle)) semitone += 1;  // 中键 = 升半音
        midi = PitchFingering.BaseC4 + semitone;
        return PitchFingering.IsPlayable(midi);
    }

    /// <summary>跟练模式：检查用户是否正按住这个组合。</summary>
    public bool IsHeldByUser()
    {
        if (IsEmpty) return true;
        if (Mouse.HasFlag(MouseMod.Left) && !IsVirtualDown(0x01)) return false;
        if (Mouse.HasFlag(MouseMod.Right) && !IsVirtualDown(0x02)) return false;
        if (Mouse.HasFlag(MouseMod.Middle) && !IsVirtualDown(0x04)) return false;
        if (Ctrl && !(InputSender.IsDown(Key.LeftCtrl) || InputSender.IsDown(Key.RightCtrl))) return false;
        if (Alt && !(InputSender.IsDown(Key.LeftAlt) || InputSender.IsDown(Key.RightAlt))) return false;
        if (Shift && !(InputSender.IsDown(Key.LeftShift) || InputSender.IsDown(Key.RightShift))) return false;
        return InputSender.IsDown(Key);
    }

    /// <summary>跟练模式：只要主键按着就算按下（鼠标修饰可能被游戏占用，放宽判定）。</summary>
    public bool IsKeyHeldByUser() => !IsEmpty && InputSender.IsDown(Key);

    private static bool IsVirtualDown(int vk) => InputSender.IsMouseDown(vk switch
    {
        0x01 => MouseMod.Left,
        0x02 => MouseMod.Right,
        _ => MouseMod.Middle,
    });
}
