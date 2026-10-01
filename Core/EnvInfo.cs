using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace HarmoPlay.Core;

/// <summary>运行环境信息：会话、窗口站、桌面、完整性级别、前台窗口。用于诊断"按键为什么没反应"。</summary>
public static class EnvInfo
{
    private const int UOI_NAME = 2;
    private const uint DESKTOP_READOBJECTS = 0x0001;
    private const uint DESKTOP_SWITCHDESKTOP = 0x0100;
    private const int TokenIntegrityLevel = 25;
    private const uint TOKEN_QUERY = 0x0008;

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetProcessWindowStation();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetThreadDesktop(uint dwThreadId);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetUserObjectInformation(IntPtr hObj, int nIndex, StringBuilder pvInfo, int nLength, out int needed);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr hDesktop);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr proc, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int cls, IntPtr info, int len, out int ret);
    [DllImport("advapi32.dll")] private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint index);
    [DllImport("advapi32.dll")] private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);

    /// <summary>完整性级别（Low / Medium / High …）——决定 Windows 是否允许模拟输入。</summary>
    public static (string name, uint rid) Integrity
    {
        get
        {
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, out var token)) return ("未知", 0);
            try
            {
                GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out var len);
                if (len <= 0) return ("未知", 0);
                var buf = Marshal.AllocHGlobal(len);
                try
                {
                    if (!GetTokenInformation(token, TokenIntegrityLevel, buf, len, out _)) return ("未知", 0);
                    var sid = Marshal.ReadIntPtr(buf);
                    var count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
                    var rid = (uint)Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)(count - 1)));
                    var name = rid switch
                    {
                        0x0000 => "Untrusted",
                        0x1000 => "Low（低）",
                        0x2000 => "Medium（普通）",
                        0x2100 => "MediumPlus",
                        0x3000 => "High（管理员）",
                        0x4000 => "System",
                        _ => $"0x{rid:X}",
                    };
                    return (name, rid);
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            finally { CloseHandle(token); }
        }
    }

    public static int SessionId => Process.GetCurrentProcess().SessionId;

    private static string ObjName(IntPtr h)
    {
        if (h == IntPtr.Zero) return "(空)";
        var sb = new StringBuilder(256);
        return GetUserObjectInformation(h, UOI_NAME, sb, sb.Capacity * 2, out _) ? sb.ToString() : "(读取失败)";
    }

    public static string WindowStation => ObjName(GetProcessWindowStation());
    public static string Desktop => ObjName(GetThreadDesktop(GetCurrentThreadId()));

    public static string InputDesktop
    {
        get
        {
            var h = OpenInputDesktop(0, false, DESKTOP_READOBJECTS | DESKTOP_SWITCHDESKTOP);
            if (h == IntPtr.Zero) return $"(打不开，err={Marshal.GetLastWin32Error()})";
            try { return ObjName(h); }
            finally { CloseDesktop(h); }
        }
    }

    public static string ForegroundWindowText
    {
        get
        {
            var h = GetForegroundWindow();
            var sb = new StringBuilder(256);
            GetWindowText(h, sb, sb.Capacity);
            GetWindowThreadProcessId(h, out var pid);
            string proc = "?";
            try
            {
                var p = Process.GetProcessById((int)pid);
                proc = p.ProcessName;
            }
            catch { proc = "(已退出/不可访问)"; }
            return $"\"{sb}\"（{proc}）";
        }
    }

    public static bool IsElevated
    {
        get
        {
            try
            {
                using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                return new System.Security.Principal.WindowsPrincipal(identity)
                    .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }
    }
}
