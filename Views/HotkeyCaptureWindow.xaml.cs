using System.Windows;
using System.Windows.Input;
using HarmoPlay.Models;

namespace HarmoPlay.Views;

public partial class HotkeyCaptureWindow : Window
{
    public HotkeyCaptureWindow(HotkeySpec current)
    {
        InitializeComponent();
        TxtNow.Text = string.IsNullOrWhiteSpace(current.Text) ? "等待按键…" : current.Text;
        Loaded += (_, _) => Focus();
    }

    public Key CapturedKey { get; private set; } = Key.None;
    public string CapturedModifiers { get; private set; } = "";
    /// <summary>用户点了「清除绑定」。</summary>
    public bool Cleared { get; private set; }

    private static bool IsModifierKey(Key key) => key is Key.LeftCtrl or Key.RightCtrl
        or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
        or Key.LWin or Key.RWin or Key.System or Key.None;

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            DialogResult = false;
            return;
        }
        if (IsModifierKey(key))
        {
            TxtNow.Text = Describe(Keyboard.Modifiers) + "…";
            e.Handled = true;
            return;
        }

        CapturedKey = key;
        CapturedModifiers = Describe(Keyboard.Modifiers);
        TxtNow.Text = (CapturedModifiers.Length > 0 ? CapturedModifiers + "+" : "") + key;
        BtnOk.IsEnabled = true;
        e.Handled = true;
    }

    private static string Describe(ModifierKeys mods)
    {
        var parts = new List<string>();
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        return string.Join("+", parts);
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        DialogResult = CapturedKey != Key.None;
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        Cleared = true;
        CapturedKey = Key.None;
        CapturedModifiers = "";
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
