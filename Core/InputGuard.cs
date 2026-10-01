using HarmoPlay.Models;

namespace HarmoPlay.Core;

/// <summary>
/// 输入安全网：负责把「所有可能被按住的键 / 鼠标键」强制松开。
/// 场景：急停、程序退出、崩溃恢复、启动时清理上一次残留。
/// </summary>
public static class InputGuard
{
    private static readonly object Sync = new();

    /// <summary>Shift / Ctrl / Alt / LWin / RWin</summary>
    private static readonly int[] ModifierVks = { 0x10, 0x11, 0x12, 0x5B, 0x5C };

    private static readonly MouseMod AllMouse = MouseMod.Left | MouseMod.Right | MouseMod.Middle;

    public static int ReleaseCount { get; private set; }
    public static string LastReason { get; private set; } = "";

    public static event Action<string>? Released;

    /// <summary>强制松开修饰键、鼠标三键以及调用方给出的所有按键（重复发送一次以确保生效）。</summary>
    public static void ReleaseEverything(IEnumerable<int>? extraKeys = null, string reason = "")
    {
        lock (Sync)
        {
            var keys = new List<int>(ModifierVks);
            if (extraKeys != null)
                keys.AddRange(extraKeys.Where(k => k != 0));
            keys = keys.Distinct().ToList();

            for (int pass = 0; pass < 2; pass++)
            {
                foreach (var vk in keys) InputSender.KeyUp((ushort)vk);
                InputSender.MouseUp(AllMouse);
            }

            ReleaseCount++;
            LastReason = reason;
            Program.Trace($"InputGuard 松开全部输入（{reason}）按键={string.Join(",", keys)} 第 {ReleaseCount} 次");
            Released?.Invoke(reason);
        }
    }
}
