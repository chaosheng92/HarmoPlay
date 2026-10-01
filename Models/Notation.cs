namespace HarmoPlay.Models;

/// <summary>记谱法：决定曲谱里的 #/b/^/,/' 是"目标音高"还是"物理按键"。</summary>
public enum NotationKind
{
    /// <summary>直接按键记谱（三角洲可视化谱 / D-hydra）：b=左键(降八度)、#=中键(升半音)、^=右键(升八度)。</summary>
    Physical = 0,

    /// <summary>固定音高记谱（鼠鼠口琴谱 JSON）：中音 1=C4=MIDI 60，#/b 是半音升降，,/ ' 是八度，由程序自动选指法。</summary>
    Pitch = 1,
}

public static class NotationKindText
{
    public static string ToText(this NotationKind k) => k == NotationKind.Pitch ? "固定音高" : "直接按键";

    public static NotationKind FromText(string? text) =>
        string.Equals(text, "pitch", StringComparison.OrdinalIgnoreCase) ? NotationKind.Pitch : NotationKind.Physical;

    public static string ToId(this NotationKind k) => k == NotationKind.Pitch ? "pitch" : "physical";
}
