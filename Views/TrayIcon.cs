using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace HarmoPlay.Views;

/// <summary>
/// 右下角托盘图标（Shell_NotifyIcon 直连，不依赖 WinForms）。
/// 双击恢复主窗口，右键弹出菜单：显示主窗口 / 悬浮窗显隐 / 播放暂停 / 停止 / 说明与设置 / 退出。
/// 每次注册都写 startup.log，便于诊断"图标没出现"；资源管理器重启后会自动重新注册。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const int NIM_ADD = 0x00000000;
    private const int NIM_MODIFY = 0x00000001;
    private const int NIM_DELETE = 0x00000002;
    private const int NIM_SETVERSION = 0x00000004;

    private const int NIF_MESSAGE = 0x00000001;
    private const int NIF_ICON = 0x00000002;
    private const int NIF_TIP = 0x00000004;
    private const int NIF_INFO = 0x00000010;

    private const uint WM_APP = 0x8000;
    private const uint WM_TRAYICON = WM_APP + 1;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;

    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x00000010;
    private const uint LR_DEFAULTSIZE = 0x00000040;
    private const int IDI_APPLICATION = 32512;
    private const uint NOTIFYICON_VERSION_4 = 4;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIcon(int message, ref NOTIFYICONDATA data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImage(IntPtr hInst, string name, uint type, int cx, int cy, uint load);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr iconName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);

    private const int SW_HIDE = 0;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private readonly HwndSource _source;
    private readonly uint _taskbarCreated;
    private readonly IntPtr _hIcon;
    private readonly string _tip;
    private bool _added;
    private bool _disposed;

    private readonly Action _showWindow;
    private readonly Action _toggleOverlay;
    private readonly Action _playPause;
    private readonly Action _stop;
    private readonly Action _about;
    private readonly Action _exit;

    private ContextMenu? _menu;

    public TrayIcon(string tip, Action showWindow, Action toggleOverlay, Action playPause, Action stop, Action about, Action exit)
    {
        _tip = tip.Length > 120 ? tip[..120] : tip;
        _showWindow = showWindow;
        _toggleOverlay = toggleOverlay;
        _playPause = playPause;
        _stop = stop;
        _about = about;
        _exit = exit;

        // 用标准隐藏顶层窗口做托盘消息窗口（不用 HWND_MESSAGE：部分环境下 Shell_NotifyIcon 会拒绝，
        // 实测返回 False / err=5）。窗口不可见、不进任务栏、不抢焦点。
        var p = new HwndSourceParameters("HarmoPlay.TraySink")
        {
            Width = 0,
            Height = 0,
            PositionX = -32000,
            PositionY = -32000,
            WindowStyle = 0,
            ExtendedWindowStyle = 0x00000080,   // WS_EX_TOOLWINDOW
        };
        _source = new HwndSource(p);
        ShowWindow(_source.Handle, SW_HIDE);
        _source.AddHook(WndProc);
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");

        _hIcon = LoadTrayIcon();
        Program.Trace($"托盘：准备注册（提示=\"{_tip}\"，图标句柄=0x{_hIcon.ToInt64():X}，" +
                      $"消息窗口=0x{_source.Handle.ToInt64():X}，TaskbarCreated={_taskbarCreated}）");
        AddIcon();
    }

    private static IntPtr LoadTrayIcon()
    {
        var candidates = new List<string>();
        try
        {
            var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
            candidates.Add(Path.Combine(exeDir, "Assets", "app.ico"));
            candidates.Add(Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"));
            candidates.Add(Path.Combine(exeDir, "app.ico"));
        }
        catch
        {
            // 忽略
        }

        foreach (var path in candidates)
        {
            try
            {
                if (!File.Exists(path)) continue;
                var handle = LoadImage(IntPtr.Zero, path, IMAGE_ICON, 0, 0, LR_LOADFROMFILE | LR_DEFAULTSIZE);
                if (handle != IntPtr.Zero)
                {
                    Program.Trace("托盘：图标来自 " + path);
                    return handle;
                }
            }
            catch
            {
                // 试下一个
            }
        }

        Program.Trace("托盘：未找到 app.ico，使用系统默认图标");
        return LoadIcon(IntPtr.Zero, new IntPtr(IDI_APPLICATION));
    }

    private NOTIFYICONDATA BuildData()
    {
        var data = new NOTIFYICONDATA
        {
            hWnd = _source.Handle,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAYICON,
            hIcon = _hIcon,
            szTip = _tip,
            szInfo = "",
            szInfoTitle = "",
        };
        data.cbSize = Marshal.SizeOf<NOTIFYICONDATA>();
        return data;
    }

    private void AddIcon()
    {
        var data = BuildData();
        _added = Shell_NotifyIcon(NIM_ADD, ref data);
        var err = Marshal.GetLastWin32Error();

        var version = BuildData();
        version.uTimeoutOrVersion = NOTIFYICON_VERSION_4;
        Shell_NotifyIcon(NIM_SETVERSION, ref version);

        Program.Trace($"托盘：Shell_NotifyIcon(NIM_ADD) 返回 {_added}（cbSize={data.cbSize}，err={err}）");
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_taskbarCreated != 0 && (uint)msg == _taskbarCreated)
        {
            Program.Trace("托盘：收到 TaskbarCreated（资源管理器重启），重新注册图标");
            AddIcon();
            handled = true;
            return IntPtr.Zero;
        }

        if ((uint)msg == WM_TRAYICON)
        {
            switch (lParam.ToInt32())
            {
                case WM_LBUTTONDBLCLK:
                case WM_LBUTTONUP:
                    OnUi(_showWindow);
                    handled = true;
                    break;
                case WM_RBUTTONUP:
                    ShowMenu();
                    handled = true;
                    break;
            }
        }
        return IntPtr.Zero;
    }

    private void ShowMenu()
    {
        _menu ??= BuildMenu();
        _menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        _menu.IsOpen = true;
    }

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();
        menu.Items.Add(MenuItem("显示主窗口", _showWindow));
        menu.Items.Add(MenuItem("显示 / 隐藏悬浮窗", _toggleOverlay));
        menu.Items.Add(new System.Windows.Controls.Separator());
        menu.Items.Add(MenuItem("播放 / 暂停", _playPause));
        menu.Items.Add(MenuItem("停止", _stop));
        menu.Items.Add(new System.Windows.Controls.Separator());
        menu.Items.Add(MenuItem("说明与设置", _about));
        menu.Items.Add(MenuItem("退出", _exit));
        return menu;
    }

    private static System.Windows.Controls.MenuItem MenuItem(string header, Action action)
    {
        var item = new System.Windows.Controls.MenuItem { Header = header };
        item.Click += (_, _) => OnUi(action);
        return item;
    }

    private static void OnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null) { action(); return; }
        if (dispatcher.CheckAccess()) action();
        else dispatcher.Invoke(action);
    }

    /// <summary>气泡提示（Win10/11 以通知形式出现）。</summary>
    public void ShowBalloon(string title, string text)
    {
        try
        {
            var data = BuildData();
            data.uFlags = NIF_INFO | NIF_ICON | NIF_TIP;
            data.szInfoTitle = title.Length > 60 ? title[..60] : title;
            data.szInfo = text.Length > 250 ? text[..250] : text;
            data.uTimeoutOrVersion = 5000;
            var ok = Shell_NotifyIcon(NIM_MODIFY, ref data);
            Program.Trace($"托盘：气泡提示返回 {ok}");
        }
        catch (Exception ex)
        {
            Program.Trace("托盘：气泡提示失败 " + ex.Message);
        }
    }

    public bool IsAdded => _added;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            var data = BuildData();
            Shell_NotifyIcon(NIM_DELETE, ref data);
            Program.Trace("托盘：已注销图标");
            if (_hIcon != IntPtr.Zero) DestroyIcon(_hIcon);
            _source.Dispose();
        }
        catch
        {
            // 忽略
        }
    }
}

