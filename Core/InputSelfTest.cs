using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace HarmoPlay.Core;

public sealed class InputTestResult
{
    public bool HookInstalled { get; set; }
    public bool InjectedSeen { get; set; }
    public int EventsSeen { get; set; }
    public string Integrity { get; set; } = "";
    public int SessionId { get; set; }
    public string WindowStation { get; set; } = "";
    public string Desktop { get; set; } = "";
    public string InputDesktop { get; set; } = "";
    public string Foreground { get; set; } = "";
    public bool Elevated { get; set; }
    public string Verdict { get; set; } = "";
    public List<string> Advice { get; } = new();

    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.AppendLine("==== 输入注入自检 ====");
        sb.AppendLine($"结果：{Verdict}");
        sb.AppendLine();
        sb.AppendLine("-- 环境 --");
        sb.AppendLine($"程序完整性：{Integrity}{(Elevated ? "（已提权）" : "")}");
        sb.AppendLine($"会话：{SessionId}　窗口站：{WindowStation}　线程桌面：{Desktop}");
        sb.AppendLine($"当前输入桌面：{InputDesktop}");
        sb.AppendLine($"前台窗口：{Foreground}");
        sb.AppendLine();
        sb.AppendLine("-- 注入测试 --");
        sb.AppendLine($"键盘钩子安装：{(HookInstalled ? "成功" : "失败")}");
        sb.AppendLine($"注入的测试按键（F24）被系统看到：{(InjectedSeen ? "是" : "否")}（收到 {EventsSeen} 个事件）");
        if (Advice.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("-- 建议 --");
            foreach (var a in Advice) sb.AppendLine("· " + a);
        }
        return sb.ToString();
    }
}

/// <summary>
/// 输入自检：在自己进程里装一个低级键盘钩子，然后发一个无害的测试按键（F24），
/// 看事件是否真的进入系统输入队列。用于判断"自动弹奏为什么没反应"。
/// </summary>
public static class InputSelfTest
{
    private const int WH_KEYBOARD_LL = 13;
    private const ushort TestKey = 0x87;   // VK_F24：几乎不会被任何程序占用

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll")] private static extern bool PeekMessage(out MSG msg, IntPtr hWnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG msg);

    public static InputTestResult Run(int waitMs = 500)
    {
        var result = new InputTestResult();
        var (integrity, rid) = EnvInfo.Integrity;
        result.Integrity = integrity;
        result.SessionId = EnvInfo.SessionId;
        result.WindowStation = EnvInfo.WindowStation;
        result.Desktop = EnvInfo.Desktop;
        result.InputDesktop = EnvInfo.InputDesktop;
        result.Foreground = EnvInfo.ForegroundWindowText;
        result.Elevated = EnvInfo.IsElevated;

        int seen = 0;
        HookProc proc = (nCode, wParam, lParam) =>
        {
            if (nCode >= 0)
            {
                var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                if (data.vkCode == TestKey) Interlocked.Increment(ref seen);
            }
            return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        };

        IntPtr hook = IntPtr.Zero;
        var ready = new ManualResetEventSlim(false);
        // 钩子必须在有消息循环的线程上安装并泵消息
        var thread = new Thread(() =>
        {
            hook = SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(null), 0);
            ready.Set();
            if (hook == IntPtr.Zero) return;

            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < waitMs + 400)
            {
                while (PeekMessage(out var msg, IntPtr.Zero, 0, 0, 1))
                {
                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }
                Thread.Sleep(5);
            }
            UnhookWindowsHookEx(hook);
        })
        { IsBackground = true, Name = "HarmoPlay.InputSelfTest" };
        thread.Start();

        if (!ready.Wait(2000))
        {
            result.HookInstalled = false;
        }
        else
        {
            result.HookInstalled = hook != IntPtr.Zero;
        }

        InputSender.KeyDown(TestKey);
        Thread.Sleep(60);
        InputSender.KeyUp(TestKey);

        thread.Join(Math.Max(2000, waitMs + 1500));
        result.EventsSeen = seen;
        result.InjectedSeen = seen > 0;

        // ---- 结论与建议 ----
        if (!result.HookInstalled)
        {
            result.Verdict = "无法安装键盘钩子，自检不可用";
            result.Advice.Add("可能有安全软件拦截了键盘钩子；把本程序加入白名单后重试。");
        }
        else if (result.InjectedSeen)
        {
            result.Verdict = "注入正常 ✓ 系统能收到本程序模拟的按键";
            result.Advice.Add("如果游戏里仍然没反应：确认游戏用「无边框窗口」显示、勾掉「禁用全屏优化」，");
            result.Advice.Add("并以管理员身份运行本程序（游戏以管理员启动时必须如此）。");
        }
        else
        {
            result.Verdict = "注入被系统丢弃 ✗ 模拟按键没有进入系统输入队列";
            if (rid == 0x1000)
            {
                result.Advice.Add("当前程序运行在 Low 完整性（常见于沙箱 / 受限运行环境）。");
                result.Advice.Add("Windows 会丢弃低完整性进程的模拟输入 —— 请直接双击 exe 运行（普通用户即可），");
                result.Advice.Add("不要在沙箱或受限容器里运行。");
            }
            if (!result.InputDesktop.Equals(result.Desktop, StringComparison.OrdinalIgnoreCase))
            {
                result.Advice.Add($"当前输入桌面（{result.InputDesktop}）与程序所在桌面（{result.Desktop}）不同：");
                result.Advice.Add("说明屏幕正被锁屏 / UAC 提权界面 / 其它桌面接管，此时无法注入，回到正常桌面再试。");
            }
            if (!result.Elevated)
                result.Advice.Add("若游戏以管理员身份运行，请右键本程序「以管理员身份运行」后重试。");
            result.Advice.Add("若以上都正常仍失败，多为安全软件 / 反作弊在拦截模拟输入，请查看其拦截记录或临时放行。");
        }

        return result;
    }
}
