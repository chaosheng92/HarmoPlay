namespace HarmoPlay.Models;

/// <summary>记谱法：决定曲谱里的 #/b/^/,/' 是"目标音高"还是"物理按键"。</summary>
public enum NotationKind
{
    /// <summary>直接按键记谱（三角洲可视化谱 / D-hydra）：b=左键(降八度)、#=中键(升半音)、^=右键(升八度)。</summary>
    Physical = 0,

    /// <summary>固定音高记谱（JSON 曲谱）：中音 1=C4=MIDI 60，#/b 是半音升降，,/ ' 是八度，由程序自动选指法。</summary>
    Pitch = 1,
}

public static class NotationKindText
{
    public static string ToText(this NotationKind k) => k == NotationKind.Pitch ? "固定音高" : "直接按键";

    public static NotationKind FromText(string? text) =>
        string.Equals(text, "pitch", StringComparison.OrdinalIgnoreCase) ? NotationKind.Pitch : NotationKind.Physical;

    public static string ToId(this NotationKind k) => k == NotationKind.Pitch ? "pitch" : "physical";
}

/// <summary>演奏打断键：玩家按这些键时（移动 / 跳跃 / 切枪 / 背包）游戏里的口琴会中断，
/// 程序据此暂停演奏，避免继续往菜单界面里灌按键。</summary>
public sealed class InterruptKey
{
    public string Key { get; set; } = "W";
    public bool Enabled { get; set; } = true;
    public string Memo { get; set; } = "";

    public static List<InterruptKey> CreateDefaults() => new()
    {
        new InterruptKey { Key = "W", Memo = "前进" },
        new InterruptKey { Key = "A", Memo = "左移" },
        new InterruptKey { Key = "S", Memo = "后退" },
        new InterruptKey { Key = "D", Memo = "右移" },
        new InterruptKey { Key = "Space", Memo = "跳跃" },
        new InterruptKey { Key = "D1", Memo = "切枪 1" },
        new InterruptKey { Key = "D2", Memo = "切枪 2" },
        new InterruptKey { Key = "D3", Memo = "切枪 3" },
        new InterruptKey { Key = "D4", Memo = "切枪 4" },
        new InterruptKey { Key = "Tab", Memo = "打开背包" },
    };
}

