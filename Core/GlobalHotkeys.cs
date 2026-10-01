using System.Runtime.InteropServices;
using System.Windows.Input;

namespace HarmoPlay.Core;

public sealed class HotkeyEventArgs : EventArgs
{
    public HotkeyEventArgs(int id, string action)
    {
        Id = id;
        Action = action;
    }

    public int Id { get; }
    public string Action { get; }
}

/// <summary>全局热键注册（RegisterHotKey + 消息窗口）。</summary>
public sealed class HotkeyManager : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int MOD_ALT = 0x0001;
    private const int MOD_CONTROL = 0x0002;
    private const int MOD_SHIFT = 0x0004;
    private const int MOD_WIN = 0x0008;
    private const int MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, int vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly System.Windows.Interop.HwndSource _source;
    private readonly Dictionary<int, string> _ids = new();

    public HotkeyManager()
    {
        var p = new System.Windows.Interop.HwndSourceParameters("HarmoPlay.HotkeySink")
        {
            Width = 0,
            Height = 0,
            PositionX = 0,
            PositionY = 0,
            WindowStyle = 0,
            ParentWindow = new IntPtr(-3), // HWND_MESSAGE
        };
        _source = new System.Windows.Interop.HwndSource(p);
        _source.AddHook(Hook);
    }

    public event EventHandler<HotkeyEventArgs>? Pressed;

    public IReadOnlyCollection<string> FailedActions { get; private set; } = Array.Empty<string>();

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            if (_ids.TryGetValue(id, out var action))
            {
                Pressed?.Invoke(this, new HotkeyEventArgs(id, action));
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    public void UnregisterAll()
    {
        foreach (var id in _ids.Keys.ToList())
            UnregisterHotKey(_source.Handle, id);
        _ids.Clear();
    }

    /// <summary>注册一组热键；返回注册失败的条目。</summary>
    public List<string> RegisterAll(IEnumerable<Models.HotkeySpec> specs)
    {
        UnregisterAll();
        var failed = new List<string>();
        int nextId = 100;
        foreach (var spec in specs)
        {
            if (string.IsNullOrWhiteSpace(spec.Key)) continue;
            if (!TryParse(spec, out var mods, out var key))
            {
                failed.Add($"{spec.Text}（无法识别）");
                continue;
            }
            int vk = KeyInterop.VirtualKeyFromKey(key);
            if (vk == 0) { failed.Add($"{spec.Text}（无法识别）"); continue; }

            int id = nextId++;
            int flags = ToNative(mods) | MOD_NOREPEAT;
            if (RegisterHotKey(_source.Handle, id, flags, vk))
                _ids[id] = spec.Action;
            else
                failed.Add($"{spec.Text}（已被其它程序占用）");
        }
        FailedActions = failed;
        return failed;
    }

    public static bool TryParse(Models.HotkeySpec spec, out ModifierKeys mods, out Key key)
    {
        mods = ModifierKeys.None;
        key = Key.None;
        if (string.IsNullOrWhiteSpace(spec.Key)) return false;
        if (!Enum.TryParse<Key>(spec.Key, true, out key)) return false;

        foreach (var part in (spec.Modifiers ?? "").Split(new[] { '+', ' ', ',' }, StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.Trim().ToLowerInvariant())
            {
                case "alt": mods |= ModifierKeys.Alt; break;
                case "ctrl":
                case "control": mods |= ModifierKeys.Control; break;
                case "shift": mods |= ModifierKeys.Shift; break;
                case "win": mods |= ModifierKeys.Windows; break;
            }
        }
        return true;
    }

    private static int ToNative(ModifierKeys mods)
    {
        int flags = 0;
        if (mods.HasFlag(ModifierKeys.Alt)) flags |= MOD_ALT;
        if (mods.HasFlag(ModifierKeys.Control)) flags |= MOD_CONTROL;
        if (mods.HasFlag(ModifierKeys.Shift)) flags |= MOD_SHIFT;
        if (mods.HasFlag(ModifierKeys.Windows)) flags |= MOD_WIN;
        return flags;
    }

    public void Dispose()
    {
        UnregisterAll();
        _source.Dispose();
    }
}
