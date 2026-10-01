using System.Text.Json.Serialization;

namespace HarmoPlay.Models;

/// <summary>曲谱分类（文件夹）。</summary>
public sealed class SongFolder
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public override string ToString() => Name;
}

/// <summary>一首曲谱。Score 为简谱文本（兼容鼠鼠口琴谱与三角洲可视化曲谱格式）。</summary>
public sealed class Song
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Artist { get; set; } = "";
    public string? FolderId { get; set; }
    public string Score { get; set; } = "";
    public double Bpm { get; set; } = 90;
    public string Meter { get; set; } = "4/4";
    public bool Enabled { get; set; } = true;
    public string Source { get; set; } = "本地";
    public string Memo { get; set; } = "";
    /// <summary>记谱法："physical"（直接按键）或 "pitch"（固定音高，鼠鼠转谱规范）。</summary>
    public string Notation { get; set; } = "physical";

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Artist) ? Name : $"{Name} · {Artist}";

    public override string ToString() => DisplayName;
}

/// <summary>悬浮窗设置。</summary>
public sealed class OverlaySettings
{
    public bool Visible { get; set; } = true;
    public double Left { get; set; } = 200;
    public double Top { get; set; } = 120;
    public double Width { get; set; } = 460;
    public double Height { get; set; } = 320;
    public double Opacity { get; set; } = 0.88;
    public bool ShowLanes { get; set; } = true;
    public bool ShowKeyLetters { get; set; } = true;
    public bool ShowTitle { get; set; } = true;
    public bool ShowModifierColors { get; set; } = true;
    public bool ClickThrough { get; set; } = true;
    public bool HideWhilePlaying { get; set; } = false;
    public int MaxStack { get; set; } = 7;
    public string Background { get; set; } = "#CC10141C";

    // ---- 音游下落模式 ----
    /// <summary>0 = 经典堆叠，1 = 音游下落。</summary>
    public int Mode { get; set; }
    /// <summary>下落速度（像素/秒）。</summary>
    public double FallSpeed { get; set; } = 260;
    /// <summary>音符提前多少秒进入视野。</summary>
    public double LookAheadSeconds { get; set; } = 4;
    public bool ShowJudgmentLine { get; set; } = true;
    public bool ShowFallKeyHint { get; set; } = true;
}

/// <summary>一条全局热键绑定。</summary>
public sealed class HotkeySpec
{
    public string Action { get; set; } = "";
    public string Key { get; set; } = "";
    /// <summary>Alt / Ctrl / Shift / Win 的组合，用 + 连接，可为空。</summary>
    public string Modifiers { get; set; } = "";

    [JsonIgnore]
    public string Text => string.IsNullOrWhiteSpace(Modifiers) ? Key : $"{Modifiers}+{Key}";
}

/// <summary>播放参数。</summary>
public sealed class PlaybackOptions
{
    /// <summary>0 表示使用曲谱自身 BPM。</summary>
    public double BpmOverride { get; set; }
    public double Speed { get; set; } = 1.0;
    public int GapMs { get; set; } = 30;
    public int LeadMs { get; set; } = 20;
    public int CountdownSeconds { get; set; }
    public int RepeatTimes { get; set; } = 1;
    public bool WaitForInput { get; set; }
    public bool LegatoSameKey { get; set; } = true;
    public int MinHoldMs { get; set; } = 60;
    /// <summary>只按白键、不发送任何鼠标修饰键（彻底不碰鼠标，代价是变调失效）。</summary>
    public bool SuppressMouseModifiers { get; set; }

    public PlaybackOptions Clone() => (PlaybackOptions)MemberwiseClone();
}

/// <summary>全局设置（存 settings.json）。</summary>
public sealed class AppSettings
{
    public string KeyMapId { get; set; } = "delta";
    public KeyMap CustomKeyMap { get; set; } = KeyMap.CreateDelta();
    public List<HotkeySpec> Hotkeys { get; set; } = DefaultHotkeys.CreateDefaults();
    public List<string> QuickSongIds { get; set; } = new() { null!, null!, null!, null!, null!, null! };
    public OverlaySettings Overlay { get; set; } = new();
    public PlaybackOptions Playback { get; set; } = new();
    public bool NightMode { get; set; } = true;
    public bool TopMostOverlay { get; set; } = true;
    public string? LastSongId { get; set; }
    public bool FirstRunDone { get; set; }

    // ---- 演奏打断（移动 / 跳跃 / 切枪 / 背包，可自定义）----
    public bool InterruptEnabled { get; set; } = true;
    public List<InterruptKey> InterruptKeys { get; set; } = InterruptKey.CreateDefaults();
    /// <summary>0 = 暂停并等我手动继续；1 = 松开这些键之后自动继续。</summary>
    public int InterruptBehavior { get; set; }
    public int InterruptResumeDelayMs { get; set; } = 600;

    // ---- 更新与反馈（仓库地址，上传 GitHub 后改这两处即可）----
    public string UpdateUrl { get; set; } = "https://raw.githubusercontent.com/你的用户名/HarmoPlay/main/update.json";
    public string DownloadUrl { get; set; } = "https://github.com/你的用户名/HarmoPlay/releases/latest";
    public string IssuesUrl { get; set; } = "https://github.com/你的用户名/HarmoPlay/issues/new/choose";
    public DateTime? LastUpdateCheck { get; set; }
    public string LastUpdateResult { get; set; } = "";
}

public static class DefaultHotkeys
{
    public const string PlayPause = "playpause";
    public const string Stop = "stop";
    public const string NextSong = "next";
    public const string PrevSong = "prev";
    public const string ToggleOverlay = "toggleoverlay";
    public const string LockOverlay = "lockoverlay";
    public const string ToggleWait = "togglewait";
    public const string PanicRelease = "panicrelease";
    public const string Quick = "quick";

    public static List<HotkeySpec> CreateDefaults() => new()
    {
        new HotkeySpec { Action = PlayPause,     Key = "D1", Modifiers = "Alt" },
        new HotkeySpec { Action = Stop,          Key = "D2", Modifiers = "Alt" },
        new HotkeySpec { Action = NextSong,      Key = "D3", Modifiers = "Alt" },
        new HotkeySpec { Action = PrevSong,      Key = "Up", Modifiers = "Alt" },
        new HotkeySpec { Action = ToggleOverlay, Key = "H",  Modifiers = "Alt" },
        new HotkeySpec { Action = LockOverlay,   Key = "L",  Modifiers = "Alt" },
        new HotkeySpec { Action = ToggleWait,    Key = "T",  Modifiers = "Alt" },
        new HotkeySpec { Action = PanicRelease,  Key = "D0", Modifiers = "Ctrl+Alt" },
        new HotkeySpec { Action = Quick + "1",   Key = "D4", Modifiers = "Alt" },
        new HotkeySpec { Action = Quick + "2",   Key = "D5", Modifiers = "Alt" },
        new HotkeySpec { Action = Quick + "3",   Key = "D6", Modifiers = "Alt" },
        new HotkeySpec { Action = Quick + "4",   Key = "D7", Modifiers = "Alt" },
        new HotkeySpec { Action = Quick + "5",   Key = "D8", Modifiers = "Alt" },
        new HotkeySpec { Action = Quick + "6",   Key = "D9", Modifiers = "Alt" },
    };
}

public static class HotkeyActions
{
    public static string Describe(string action) => action switch
    {
        DefaultHotkeys.PlayPause => "播放 / 暂停当前曲谱",
        DefaultHotkeys.Stop => "停止演奏",
        DefaultHotkeys.NextSong => "下一首",
        DefaultHotkeys.PrevSong => "上一首",
        DefaultHotkeys.ToggleOverlay => "显示 / 隐藏悬浮窗",
        DefaultHotkeys.LockOverlay => "锁定 / 解锁悬浮窗（点击穿透）",
        DefaultHotkeys.ToggleWait => "切换 自动演奏 / 跟练模式",
        DefaultHotkeys.PanicRelease => "急停：立刻松开所有按键与鼠标键",
        _ when action.StartsWith(DefaultHotkeys.Quick) =>
            $"快捷曲 {action.Substring(DefaultHotkeys.Quick.Length)}",
        _ => action
    };
}
