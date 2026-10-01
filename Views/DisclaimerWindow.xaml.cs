using System.Windows;

namespace HarmoPlay.Views;

/// <summary>
/// 免责声明 / 自动弹奏风险确认窗口。
/// 三种用法：
///   · 首次启动展示完整声明（只有一个"我已阅读"按钮）
///   · 开启自动弹奏前确认（同意 / 不同意）
///   · 从主界面"完整声明"按钮查看
/// </summary>
public partial class DisclaimerWindow : Window
{
    public sealed class Mode
    {
        public bool IsConsent { get; init; }
        public string Header { get; init; } = "使用前请阅读：模拟按键与封号风险";
        public string AgreeText { get; init; } = "同意并开启自动弹奏";
        public bool ShowDontAsk { get; init; } = true;
    }

    public DisclaimerWindow(Mode mode)
    {
        InitializeComponent();
        IsConsent = mode.IsConsent;
        TxtHeader.Text = mode.Header;
        TxtBody.Text = FullText;
        ChkDontAsk.Visibility = mode.ShowDontAsk ? Visibility.Visible : Visibility.Collapsed;
        ChkDontAsk.IsChecked = false;

        if (mode.IsConsent)
        {
            BtnAgree.Content = mode.AgreeText;
            BtnDisagree.Content = "不同意（改用跟谱弹奏）";
            BtnDisagree.Visibility = Visibility.Visible;
        }
        else
        {
            BtnAgree.Content = "我已阅读并同意";
            BtnDisagree.Visibility = Visibility.Collapsed;
        }
    }

    public bool IsConsent { get; }
    public bool DontAskAgain => ChkDontAsk.IsChecked == true;

    public static string FullText => string.Join("\n", new[]
    {
        "【本软件做什么】",
        "口琴谱演奏器（HarmoPlay）是一个本地离线的简谱曲谱工具。它的「自动弹奏」功能会通过 Windows 的",
        "SendInput 接口模拟键盘按键与鼠标按键，替你把曲谱弹出来；「跟谱弹奏」功能不发送任何按键，",
        "只把悬浮窗 / 主界面当作谱面提示，等你按对当前的音再走到下一个。",
        "",
        "【风险提示（请务必阅读）】",
        "1. 在很多游戏里，任何形式的第三方自动操作（宏、脚本、模拟输入）都被用户协议禁止。",
        "   使用「自动弹奏」可能被反作弊判定为违规，导致账号被警告、限制功能、封禁或回档。",
        "2. 判定标准、处罚力度完全由游戏厂商决定，也可能随版本变化；本软件无法保证不被检测。",
        "3. 鼠标变调会按下左 / 中 / 右键，若演奏时鼠标停在别的窗口上，可能造成误点击。",
        "4. 「跟谱弹奏」不发送任何按键，仅作谱面提示，通常不涉及上述风险。",
        "5. 因使用本软件产生的一切后果（包括账号处罚、数据损失等）由使用者自行承担。",
        "",
        "【本软件不做什么】",
        "· 不联网（只有你主动点「检查更新」时才会读取一次仓库里的 update.json）",
        "· 不读取游戏内存、不注入游戏进程、不修改任何游戏文件、不解锁任何付费内容",
        "",
        "【建议】",
        "· 想练琴或只是看谱：用「跟谱弹奏」，既安全又不会误触鼠标键盘。",
        "· 确实要自动弹奏，请自行评估风险，并尽量在单机 / 自建房间 / 允许的环境里使用。",
        "",
        "点击下方按钮表示你已理解并接受上述内容。",
    });

    private void OnAgree(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnDisagree(object sender, RoutedEventArgs e) => DialogResult = false;
}
