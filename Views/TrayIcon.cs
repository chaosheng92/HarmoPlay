using System.Drawing;
using System.Windows.Forms;

namespace HarmoPlay.Views;

/// <summary>
/// 右下角（通知区域）托盘图标：关闭窗口时选择"最小化到后台"后创建，
/// 双击恢复主窗口，右键菜单可显示窗口 / 切换悬浮窗 / 播放暂停 / 停止 / 退出。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;

    public TrayIcon(string title, Action showWindow, Action toggleOverlay, Action playPause, Action stop, Action about, Action exit)
    {
        var (icon, fromExe) = LoadAppIcon();
        Program.Trace($"托盘图标创建：来源={(fromExe ? "exe 自带图标" : "系统默认图标")}，标题={title}");
        _icon = new NotifyIcon
        {
            Text = title.Length > 62 ? title[..62] : title,
            Icon = icon,
            Visible = true,
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("显示主窗口(&O)", null, (_, _) => OnUi(showWindow));
        menu.Items.Add("显示 / 隐藏悬浮窗(&H)", null, (_, _) => OnUi(toggleOverlay));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("播放 / 暂停(&P)", null, (_, _) => OnUi(playPause));
        menu.Items.Add("停止(&S)", null, (_, _) => OnUi(stop));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("说明与设置(&A)", null, (_, _) => OnUi(about));
        menu.Items.Add("退出(&X)", null, (_, _) => OnUi(exit));
        _icon.ContextMenuStrip = menu;
        _icon.DoubleClick += (_, _) => OnUi(showWindow);
    }

    /// <summary>托盘回调统一切回 UI 线程，避免跨线程操作 WPF 控件。</summary>
    private static void OnUi(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null) { action(); return; }
        if (dispatcher.CheckAccess()) action();
        else dispatcher.Invoke(action);
    }

    public void ShowBalloon(string title, string text)
    {
        try
        {
            _icon.ShowBalloonTip(3500, title, text, ToolTipIcon.Info);
        }
        catch
        {
            // 气泡提示失败不影响使用
        }
    }

    private static (Icon icon, bool fromExe) LoadAppIcon()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path))
            {
                var icon = Icon.ExtractAssociatedIcon(path);
                if (icon != null) return (icon, true);
            }
        }
        catch
        {
            // 落回系统图标
        }
        return (SystemIcons.Application, false);
    }

    public void Dispose()
    {
        try
        {
            _icon.Visible = false;
            _icon.Dispose();
        }
        catch
        {
            // 忽略
        }
    }
}

