using System.Runtime.InteropServices;
using System.Windows.Input;
using HarmoPlay.Models;

namespace HarmoPlay.Core;

/// <summary>基于 SendInput 的按键 / 鼠标模拟（扫描码模式，兼容性更好）。</summary>
public static class InputSender
{
    private const uint INPUT_MOUSE = 0;
    private const uint INPUT_KEYBOARD = 1;

    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_SCANCODE = 0x0008;

    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern ushort MapVirtualKey(uint uCode, uint uMapType);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint uMilliseconds);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint uMilliseconds);

    private static readonly HashSet<int> ExtendedVks = new()
    {
        0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28,       // PgUp PgDn End Home 方向键
        0x2D, 0x2E, 0x2F,                                     // Insert Delete Divide
        0x5B, 0x5C, 0x5D,                                     // Win Apps
        0xA3, 0xA5,                                           // RControl RMenu
        0x90,                                                 // NumLock
    };

    public static bool UseScanCodes { get; set; } = true;

    /// <summary>累计真正发出去的模拟输入条数（自检用：练习模式应当为 0）。</summary>
    public static int TotalSent { get; private set; }

    public static void ResetCounter() => TotalSent = 0;

    public static void BeginHighResolutionTimer() => TimeBeginPeriod(1);
    public static void EndHighResolutionTimer() => TimeEndPeriod(1);

    public static void KeyDown(ushort vk) => SendKey(vk, false);
    public static void KeyUp(ushort vk) => SendKey(vk, true);

    private static void SendKey(ushort vk, bool up)
    {
        if (vk == 0) return;
        var input = new INPUT { type = INPUT_KEYBOARD };
        input.U.ki.wVk = UseScanCodes ? (ushort)0 : vk;
        input.U.ki.wScan = UseScanCodes ? MapVirtualKey(vk, 0) : (ushort)0;
        uint flags = up ? KEYEVENTF_KEYUP : 0;
        if (UseScanCodes) flags |= KEYEVENTF_SCANCODE;
        if (ExtendedVks.Contains(vk)) flags |= KEYEVENTF_EXTENDEDKEY;
        input.U.ki.dwFlags = flags;
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    public static void MouseDown(MouseMod mod)
    {
        if (mod.HasFlag(MouseMod.Left)) SendMouse(MOUSEEVENTF_LEFTDOWN);
        if (mod.HasFlag(MouseMod.Right)) SendMouse(MOUSEEVENTF_RIGHTDOWN);
        if (mod.HasFlag(MouseMod.Middle)) SendMouse(MOUSEEVENTF_MIDDLEDOWN);
    }

    public static void MouseUp(MouseMod mod)
    {
        if (mod.HasFlag(MouseMod.Left)) SendMouse(MOUSEEVENTF_LEFTUP);
        if (mod.HasFlag(MouseMod.Right)) SendMouse(MOUSEEVENTF_RIGHTUP);
        if (mod.HasFlag(MouseMod.Middle)) SendMouse(MOUSEEVENTF_MIDDLEUP);
    }

    private static void SendMouse(uint flag)
    {
        var input = new INPUT { type = INPUT_MOUSE };
        input.U.mi.dwFlags = flag;
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    /// <summary>
    /// 测试接缝：非 null 时用它代替真实键盘/鼠标状态。仅供自检开关（如 --followtest）使用，
    /// 目的是让"跟谱/新手模式的判定逻辑"能在无法注入按键的环境（Low 完整性沙箱）里被确定性测试。
    /// 正常运行时为 null，行为与以前完全一致。
    /// </summary>
    internal static Func<Key, bool>? KeyStateOverride;
    internal static Func<MouseMod, bool>? MouseStateOverride;

    /// <summary>读取按键是否处于按下状态（用于跟练模式）。</summary>
    public static bool IsDown(Key key)
    {
        if (KeyStateOverride != null) return KeyStateOverride(key);
        int vk = KeyInterop.VirtualKeyFromKey(key);
        return (GetAsyncKeyState(vk) & 0x8000) != 0;
    }

    public static bool IsMouseDown(MouseMod mod)
    {
        if (MouseStateOverride != null) return MouseStateOverride(mod);
        bool ok = true;
        if (mod.HasFlag(MouseMod.Left)) ok &= (GetAsyncKeyState(0x01) & 0x8000) != 0;
        if (mod.HasFlag(MouseMod.Right)) ok &= (GetAsyncKeyState(0x02) & 0x8000) != 0;
        if (mod.HasFlag(MouseMod.Middle)) ok &= (GetAsyncKeyState(0x04) & 0x8000) != 0;
        return ok;
    }
}
