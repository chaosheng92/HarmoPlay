namespace HarmoPlay.Models;

/// <summary>鼠标修饰键（三角洲行动口琴用鼠标键切换变调）。</summary>
[Flags]
public enum MouseMod
{
    None = 0,
    Left = 1,
    Right = 2,
    Middle = 4,
}

/// <summary>一个"变调/修饰"档位：前缀 → 要同时按住的鼠标键或键盘修饰键。</summary>
public sealed class AccidentalMod
{
    /// <summary>规范化前缀："" / "#" / "b" / "^" / "#b" / "#^" / "'" / ","</summary>
    public string Prefix { get; set; } = "";
    public string Label { get; set; } = "本音";
    public bool MouseLeft { get; set; }
    public bool MouseRight { get; set; }
    public bool MouseMiddle { get; set; }
    public bool Ctrl { get; set; }
    public bool Alt { get; set; }
    public bool Shift { get; set; }
    /// <summary>悬浮窗配色（与游戏灯带颜色一致）。</summary>
    public string Color { get; set; } = "#FFFFFF";

    public MouseMod Mouse => (MouseLeft ? MouseMod.Left : 0)
                           | (MouseRight ? MouseMod.Right : 0)
                           | (MouseMiddle ? MouseMod.Middle : 0);

    public AccidentalMod Clone() => (AccidentalMod)MemberwiseClone();
}

/// <summary>曲谱音符 → 游戏按键 的映射表（可自定义）。</summary>
public sealed class KeyMap
{
    public string Id { get; set; } = "delta";
    public string Name { get; set; } = "";
    public string Note { get; set; } = "";
    /// <summary>8 个琴键，索引 0..7 对应简谱 1..8（8 = 高音 1）。取值为 WPF Key 名称。</summary>
    public List<string> Keys { get; set; } = new();
    public List<AccidentalMod> Mods { get; set; } = new();

    public AccidentalMod ModFor(string prefix)
    {
        var p = Normalize(prefix);
        foreach (var m in Mods)
            if (Normalize(m.Prefix) == p) return m;
        // 未定义的组合：退化到逐个字符的并集
        var union = new AccidentalMod { Prefix = p, Label = p };
        foreach (var ch in p)
        {
            var one = Mods.FirstOrDefault(x => Normalize(x.Prefix) == ch.ToString());
            if (one == null) continue;
            union.MouseLeft |= one.MouseLeft;
            union.MouseRight |= one.MouseRight;
            union.MouseMiddle |= one.MouseMiddle;
            union.Ctrl |= one.Ctrl;
            union.Alt |= one.Alt;
            union.Shift |= one.Shift;
            if (union.MouseMiddle || union.MouseLeft || union.MouseRight)
                union.Color = one.Color;
        }
        return union;
    }

    /// <summary>把 "#b" / "b#" / "^#" 之类的写法归一化。</summary>
    public static string Normalize(string prefix)
    {
        if (string.IsNullOrEmpty(prefix)) return "";
        bool sharp = false, flat = false, up = false, low = false, high = false;
        foreach (var ch in prefix)
        {
            switch (ch)
            {
                case '#': sharp = true; break;
                case 'b': flat = true; break;
                case '^': up = true; break;
                case ',': low = true; break;
                case '\'':
                case '`': high = true; break;
            }
        }
        if (low) flat = true;
        if (high) up = true;

        if (sharp) return up ? "#^" : flat ? "#b" : "#";
        if (up) return "^";
        if (flat) return "b";
        return "";
    }

    public KeyMap Clone() => new()
    {
        Id = Id,
        Name = Name,
        Note = Note,
        Keys = new List<string>(Keys),
        Mods = Mods.Select(m => m.Clone()).ToList(),
    };

    private static AccidentalMod Mod(string prefix, string label, string color,
        bool l = false, bool r = false, bool m = false) =>
        new() { Prefix = prefix, Label = label, Color = color, MouseLeft = l, MouseRight = r, MouseMiddle = m };

    /// <summary>三角洲行动 · 口琴 8 键：z x c v b n m ,，变调靠鼠标键。</summary>
    public static KeyMap CreateDelta() => new()
    {
        Id = "delta",
        Name = "三角洲行动 · 口琴 8 键",
        Note = "8 个琴键 z x c v b n m ,（简谱 1~8，其中 8 = 高音 1）；" +
               "变调靠鼠标：左键=降调(低音)、中键=半音、右键=升调(高音)，中键+左/右键=半+降 / 半+升。",
        Keys = new List<string> { "Z", "X", "C", "V", "B", "N", "M", "OemComma" },
        Mods = new List<AccidentalMod>
        {
            Mod("",    "本音",     "#FFFFFF"),
            Mod("b",   "降调/低音", "#4CD964", l: true),
            Mod("#",   "半音",     "#B06CF0", m: true),
            Mod("^",   "升调/高音", "#4A9DFF", r: true),
            Mod("#b",  "半音+降调", "#FFD23F", l: true, m: true),
            Mod("#^",  "半音+升调", "#FF5A5A", r: true, m: true),
            Mod("'",   "高音",     "#4A9DFF", r: true),
            Mod(",",   "低音",     "#4CD964", l: true),
        },
    };

    /// <summary>通用预设：数字键 1~8，不带变调（适合自己改键）。</summary>
    public static KeyMap CreateNumberRow() => new()
    {
        Id = "numbers",
        Name = "通用 · 主键盘数字 1~8",
        Note = "简谱 1~8 直接映射到主键盘 1~8，不带任何变调，可自行修改。",
        Keys = new List<string> { "D1", "D2", "D3", "D4", "D5", "D6", "D7", "D8" },
        Mods = new List<AccidentalMod>
        {
            Mod("",   "本音", "#FFFFFF"),
            Mod("b",  "降调", "#4CD964"),
            Mod("#",  "半音", "#B06CF0"),
            Mod("^",  "升调", "#4A9DFF"),
            Mod("#b", "半+降", "#FFD23F"),
            Mod("#^", "半+升", "#FF5A5A"),
            Mod("'",  "高音", "#4A9DFF"),
            Mod(",",  "低音", "#4CD964"),
        },
    };

    /// <summary>通用预设：字母键 A S D F G H J K。</summary>
    public static KeyMap CreateLetterRow() => new()
    {
        Id = "letters",
        Name = "通用 · 字母 A S D F G H J K",
        Note = "简谱 1~8 映射到 A S D F G H J K，适合把口琴键位改成字母键的游戏。",
        Keys = new List<string> { "A", "S", "D", "F", "G", "H", "J", "K" },
        Mods = new List<AccidentalMod>
        {
            Mod("",   "本音", "#FFFFFF"),
            Mod("b",  "降调", "#4CD964"),
            Mod("#",  "半音", "#B06CF0"),
            Mod("^",  "升调", "#4A9DFF"),
            Mod("#b", "半+降", "#FFD23F"),
            Mod("#^", "半+升", "#FF5A5A"),
            Mod("'",  "高音", "#4A9DFF"),
            Mod(",",  "低音", "#4CD964"),
        },
    };

    public static List<KeyMap> Presets() => new()
    {
        CreateDelta(), CreateNumberRow(), CreateLetterRow(),
    };

    public static KeyMap ById(string id, KeyMap? custom = null)
    {
        if (id == "custom" && custom != null) return custom;
        var hit = Presets().FirstOrDefault(p => p.Id == id);
        return hit ?? CreateDelta();
    }
}
