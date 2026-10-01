using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using HarmoPlay.Core;
using HarmoPlay.Models;
using Microsoft.Win32;
using HotkeyRow = HarmoPlay.Views.MainWindow.HotkeyRow;
using InterruptRow = HarmoPlay.Views.MainWindow.InterruptRow;

namespace HarmoPlay.Views;

/// <summary>设置窗口（二级设置菜单）需要的共享上下文，由主窗口提供。</summary>
public sealed class SettingsContext
{
    public required Library Library { get; init; }
    public required PlaybackEngine Engine { get; init; }
    public required HotkeyManager Hotkeys { get; init; }
    public required Func<OverlayWindow?> Overlay { get; init; }
    public required Action<string> SetStatus { get; init; }
    public required Action ShowOverlay { get; init; }
    public required Action PanicRelease { get; init; }
    /// <summary>锁定 / 解锁悬浮窗。</summary>
    public required Action<bool> SetOverlayLocked { get; init; }
    /// <summary>立刻最小化到右下角托盘。</summary>
    public required Action MinimizeToTray { get; init; }
    /// <summary>让主窗口重新解析预览、刷新工具条与右侧面板。</summary>
    public required Action RefreshMain { get; init; }
}

/// <summary>
/// 二级设置菜单：左侧分类（键位与变调 / 悬浮窗 / 演奏与跟谱 / 打断键 / 输入安全 / 全局热键 / 更新与反馈 / 数据与关于）。
/// 入口在主窗口右上角「⚙ 设置」。
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsContext _ctx;
    private readonly List<HotkeyRow> _hotkeyRows = new();
    private readonly List<InterruptRow> _interruptRows = new();
    private bool _loading = true;
    private string _lastDownloadUrl = "";

    private Library _lib => _ctx.Library;
    private PlaybackEngine _engine => _ctx.Engine;
    private HotkeyManager _hotkeys => _ctx.Hotkeys;
    private OverlayWindow? _overlay => _ctx.Overlay();

    public SettingsWindow(SettingsContext ctx, int initialCategory = 0)
    {
        _ctx = ctx;
        InitializeComponent();
        LoadFromLibrary();
        if (initialCategory > 0) SelectCategory(initialCategory);
        TxtSettingsHint.Text = "设置会立即保存；也可以直接关掉这个窗口";
    }

    private void SetStatus(string text) => _ctx.SetStatus(text);
    private void ShowOverlay() => _ctx.ShowOverlay();

    private void OnCloseSettings(object sender, RoutedEventArgs e)
    {
        SyncOverlaySettingsFromWindow();
        LibraryStore.Save(_lib);
        _ctx.RefreshMain();
        Close();
    }

    private void OnPanicRelease(object sender, RoutedEventArgs e) => _ctx.PanicRelease();

    private void OnCloseActionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        _lib.Settings.CloseAction = Math.Clamp(ComboCloseAction.SelectedIndex, 0, 2);
        LibraryStore.Save(_lib);
        SetStatus(_lib.Settings.CloseAction switch
        {
            1 => "关闭窗口时将直接退出程序",
            2 => "关闭窗口时将最小化到右下角托盘（图标留在通知区域）",
            _ => "关闭窗口时每次询问",
        });
    }

    private void OnMinimizeToTray(object sender, RoutedEventArgs e) => _ctx.MinimizeToTray();

    /// <summary>把 Assets\Seed 里缺失的内置曲谱补回来（自己删过的不会被强塞，除非文件还存在）。</summary>
    private void OnReseedSamples(object sender, RoutedEventArgs e)
    {
        var added = LibraryStore.SeedSamples(_lib, force: false);
        _lib.Settings.SeedVersion = LibraryStore.CurrentSeedVersion;
        LibraryStore.Save(_lib);
        _ctx.RefreshMain();
        LoadFromLibrary();
        SetStatus(added > 0
            ? $"已补齐 {added} 首内置曲谱（可在「曲谱库」的「示例曲谱」分类里找到）"
            : "内置曲谱已是最新，没有需要补齐的（同名曲谱已存在）");
    }

    /// <summary>把设置读进界面（打开设置窗口时、以及恢复默认后调用）。</summary>
    public void LoadFromLibrary()
    {
        _loading = true;
        ComboCloseAction.ItemsSource = new[] { "每次询问我", "直接退出程序", "最小化到右下角托盘" };
        ComboCloseAction.SelectedIndex = Math.Clamp(_lib.Settings.CloseAction, 0, 2);
        TxtVersionInfo.Text = $"当前版本 {Core.UpdateService.CurrentVersion}　更新来源：仓库中的 update.json";
        TxtDataPath.Text = LibraryStore.DataDir;

        var p = _lib.Settings.Playback;
        ChkFollowStrict.IsChecked = p.FollowStrictKey;
        ChkFollowHold.IsChecked = p.FollowRequireHold;
        ChkFollowErrors.IsChecked = p.FollowCountErrors;
        TxtFollowTimeout.Text = p.FollowTimeoutSeconds.ToString();

        var o = _lib.Settings.Overlay;
        ChkRememberOverlayPos.IsChecked = _lib.Settings.RememberOverlayPosition;
        ChkOverlayVisible.IsChecked = o.Visible;
        ChkOverlayClickThrough.IsChecked = o.ClickThrough;
        ChkOverlayLanes.IsChecked = o.ShowLanes;
        ChkOverlayKeys.IsChecked = o.ShowKeyLetters;
        ChkOverlayTitle.IsChecked = o.ShowTitle;
        ChkHideWhilePlaying.IsChecked = o.HideWhilePlaying;
        SldOverlayOpacity.Value = Math.Clamp(o.Opacity, 0.2, 1.0);
        TxtOverlayOpacity.Text = o.Opacity.ToString("0.00");
        TxtOverlayStack.Text = o.MaxStack.ToString();
        ComboOverlayMode.ItemsSource = new[] { "经典堆叠（音符块从下往上堆）", "音游下落（音符落到判定线）" };
        ComboOverlayMode.SelectedIndex = o.Mode == 1 ? 1 : 0;
        SldFallSpeed.Value = Math.Clamp(o.FallSpeed, 80, 600);
        TxtFallSpeed.Text = o.FallSpeed.ToString("0") + " px/s";
        TxtLookAhead.Text = o.LookAheadSeconds.ToString("0.#");
        ChkJudgmentLine.IsChecked = o.ShowJudgmentLine;
        ChkFallKeyHint.IsChecked = o.ShowFallKeyHint;
        RefreshOverlayPosBoxes();
        RefreshLockButton();

        _hotkeyRows.Clear();
        foreach (var h in _lib.Settings.Hotkeys)
            _hotkeyRows.Add(new HotkeyRow { Spec = h });
        ListHotkeys.ItemsSource = _hotkeyRows;

        var maps = KeyMap.Presets().Select(m => new KeyMapChoice { Id = m.Id, Name = m.Name }).ToList();
        maps.Add(new KeyMapChoice { Id = "custom", Name = "自定义映射（键位映射窗口里保存）" });
        ComboKeyMap.ItemsSource = maps;
        ComboKeyMap.SelectedValuePath = "Id";
        ComboKeyMap.SelectedValue = _lib.Settings.KeyMapId;
        if (ComboKeyMap.SelectedIndex < 0) ComboKeyMap.SelectedIndex = 0;
        UpdateKeyMapUi();

        TxtUpdateUrl.Text = _lib.Settings.UpdateUrl;
        TxtUpdateResult.Text = string.IsNullOrWhiteSpace(_lib.Settings.LastUpdateResult)
            ? "点「检查更新」从仓库读取最新版本信息（无网络时可跳过）。"
            : _lib.Settings.LastUpdateResult;

        ChkInterruptEnabled.IsChecked = _lib.Settings.InterruptEnabled;
        ComboInterruptBehavior.ItemsSource = new[] { "暂停，等我手动继续", "松开这些键后自动继续" };
        ComboInterruptBehavior.SelectedIndex = _lib.Settings.InterruptBehavior == 1 ? 1 : 0;
        TxtInterruptDelay.Text = _lib.Settings.InterruptResumeDelayMs.ToString();
        RefreshInterruptList();

        ChkSuppressMouse.IsChecked = p.SuppressMouseModifiers;
        TxtInputGuard.Text = $"已就绪（急停热键 {(_lib.Settings.Hotkeys.FirstOrDefault(h => h.Action == DefaultHotkeys.PanicRelease)?.Text ?? "Ctrl+Alt+0")}）";
        RefreshOverlayHotkeyText();

        Core.InputGuard.Released += reason => Dispatcher.InvokeAsync(() =>
        {
            TxtInputGuard.Text = $"最近一次强制松开：{reason}（{DateTime.Now:HH:mm:ss}）";
        });

        _loading = false;
    }

private void OnAddInterrupt(object sender, RoutedEventArgs e)
    {
        var dlg = new HotkeyCaptureWindow(new HotkeySpec { Key = "W", Modifiers = "" }) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        _lib.Settings.InterruptKeys.Add(new InterruptKey { Key = dlg.CapturedKey.ToString(), Memo = "自定义" });
        RefreshInterruptList();
        LibraryStore.Save(_lib);
    }

private void OnCenterOverlay(object sender, RoutedEventArgs e)
    {
        var o = _lib.Settings.Overlay;
        double sw = SystemParameters.PrimaryScreenWidth;
        o.Left = Math.Max(0, (sw - o.Width) / 2);
        o.Top = Math.Max(0, (SystemParameters.PrimaryScreenHeight - o.Height) / 2 - 40);
        ShowOverlay();
        _loading = true;
        TxtOverlayX.Text = o.Left.ToString("0");
        TxtOverlayY.Text = o.Top.ToString("0");
        _loading = false;
        LibraryStore.Save(_lib);
        SetStatus("悬浮窗已居中");
    }

private void OnChangeHotkey(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not HotkeyRow row) return;
        var dlg = new HotkeyCaptureWindow(row.Spec) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        if (dlg.Cleared)
        {
            row.Spec.Key = "";
            row.Spec.Modifiers = "";
        }
        else
        {
            row.Spec.Key = dlg.CapturedKey.ToString();
            row.Spec.Modifiers = dlg.CapturedModifiers;
            if (dlg.CapturedModifiers.Length == 0)
                SetStatus("提示：未加 Ctrl/Alt 的热键可能和游戏按键冲突，建议加上");
        }

        var failed = _hotkeys.RegisterAll(_lib.Settings.Hotkeys);
        TxtHotkeyWarn.Text = failed.Count == 0 ? "" : "注册失败：" + string.Join("、", failed);
        ListHotkeys.Items.Refresh();
        RefreshOverlayHotkeyText();
        LibraryStore.Save(_lib);
    }

private void OnChangeInterrupt(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not InterruptRow row) return;
        var dlg = new HotkeyCaptureWindow(new HotkeySpec { Key = row.Model.Key, Modifiers = "" }) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        row.Model.Key = dlg.CapturedKey.ToString();
        if (!string.IsNullOrEmpty(dlg.CapturedModifiers))
            SetStatus("打断键只支持单键；组合键请配到「全局热键」里");
        ListInterrupts.Items.Refresh();
        UpdateInterruptWarn();
        LibraryStore.Save(_lib);
    }

private async void OnCheckUpdate(object sender, RoutedEventArgs e)
    {
        var url = TxtUpdateUrl.Text.Trim();
        _lib.Settings.UpdateUrl = url;
        TxtUpdateResult.Text = "正在检查更新…";
        SetStatus("正在检查更新…");

        var info = await Core.UpdateService.CheckAsync(url);
        _lastDownloadUrl = string.IsNullOrWhiteSpace(info.DownloadUrl) ? _lib.Settings.DownloadUrl : info.DownloadUrl;
        _lib.Settings.LastUpdateCheck = DateTime.Now;
        _lib.Settings.LastUpdateResult = info.Message;
        LibraryStore.Save(_lib);

        TxtUpdateResult.Text = info.Available
            ? "🎉 " + Core.UpdateService.Describe(info)
            : (info.Available ? "" : info.Message);
        TxtUpdateResult.Foreground = info.Available
            ? new SolidColorBrush(Color.FromRgb(0x4C, 0xD9, 0x64))
            : new SolidColorBrush(Color.FromRgb(0x9A, 0xA4, 0xB5));
        SetStatus(info.Message);

        if (info.Available)
        {
            var r = MessageBox.Show(Core.UpdateService.Describe(info) + "\n\n要现在打开下载页吗？",
                "发现新版本", MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (r == MessageBoxResult.Yes) OpenUrl(_lastDownloadUrl);
        }
    }

private void OnClearOverlayHotkey2(object sender, RoutedEventArgs e)
        => EditOverlayHotkey(DefaultHotkeys.ToggleOverlay2, clearFirst: true);

private void OnExportDiagnostics(object sender, RoutedEventArgs e)
    {
        try
        {
            var file = Core.Diagnostics.Export(_lib);
            SetStatus("诊断包已导出：" + file);
            MessageBox.Show("诊断包已导出（不含曲谱内容）：\n" + file +
                            "\n\n反馈时把它拖进 GitHub Issue 即可。", "导出诊断包",
                MessageBoxButton.OK, MessageBoxImage.Information);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "/select,\"" + file + "\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            SetStatus("导出诊断包失败：" + ex.Message);
        }
    }

private void OnFollowSettingChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var p = _lib.Settings.Playback;
        p.FollowStrictKey = ChkFollowStrict.IsChecked == true;
        p.FollowRequireHold = ChkFollowHold.IsChecked == true;
        p.FollowCountErrors = ChkFollowErrors.IsChecked == true;
        p.FollowTimeoutSeconds = ParseInt(TxtFollowTimeout.Text, p.FollowTimeoutSeconds, 0, 600);
        LibraryStore.Save(_lib);
    }

private void OnInputSafetyChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _lib.Settings.Playback.SuppressMouseModifiers = ChkSuppressMouse.IsChecked == true;
        LibraryStore.Save(_lib);
        SetStatus(ChkSuppressMouse.IsChecked == true
            ? "已开启「不发送鼠标修饰键」：只按白键，不会再碰鼠标（升降调失效）"
            : "已关闭「不发送鼠标修饰键」：会按住左/中/右键来完成升降调");
    }

private void OnInputSelfTest(object sender, RoutedEventArgs e)
    {
        SetStatus("正在做输入自检（会发一个无害的 F24 测试键）…");
        Task.Run(() => Core.InputSelfTest.Run()).ContinueWith(t =>
        {
            Core.InputTestResult r;
            try
            {
                r = t.Result;
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => SetStatus("输入自检失败：" + ex.Message));
                return;
            }
            Dispatcher.Invoke(() =>
            {
                SetStatus(r.Verdict);
                TxtInputGuard.Text = r.InjectedSeen ? "最近自检：注入正常 ✓" : "最近自检：注入被系统丢弃 ✗";
                MessageBox.Show(r.ToString(), "输入自检",
                    MessageBoxButton.OK, r.InjectedSeen ? MessageBoxImage.Information : MessageBoxImage.Warning);
            });
        });
    }

private void OnInterruptItemChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        UpdateInterruptWarn();
        LibraryStore.Save(_lib);
    }

private void OnInterruptSettingChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _lib.Settings.InterruptEnabled = ChkInterruptEnabled.IsChecked == true;
        _lib.Settings.InterruptBehavior = ComboInterruptBehavior.SelectedIndex == 1 ? 1 : 0;
        _lib.Settings.InterruptResumeDelayMs = ParseInt(TxtInterruptDelay.Text, _lib.Settings.InterruptResumeDelayMs, 100, 10000);
        LibraryStore.Save(_lib);
    }

private void OnKeyMapPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (ComboKeyMap.SelectedValue is not string id) return;
        _lib.Settings.KeyMapId = id;
        UpdateKeyMapUi();
        _ctx.RefreshMain();
        LibraryStore.Save(_lib);
        SetStatus("键位映射已切换：" + _lib.EffectiveKeyMap.Name);
    }

private void OnNudgeDown(object sender, RoutedEventArgs e) => NudgeOverlay(0, 10);

private void OnNudgeLeft(object sender, RoutedEventArgs e) => NudgeOverlay(-10, 0);

private void OnNudgeRight(object sender, RoutedEventArgs e) => NudgeOverlay(10, 0);

private void OnNudgeUp(object sender, RoutedEventArgs e) => NudgeOverlay(0, -10);

private void OnOpenDataDir(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = LibraryStore.DataDir,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            SetStatus("打开目录失败：" + ex.Message);
        }
    }

private void OnOpenDownloadPage(object sender, RoutedEventArgs e)
        => OpenUrl(string.IsNullOrWhiteSpace(_lastDownloadUrl) ? _lib.Settings.DownloadUrl : _lastDownloadUrl);

private void OnOpenFeedbackDir(object sender, RoutedEventArgs e) => OpenUrl(Core.Diagnostics.FeedbackDir);

private void OnOpenIssues(object sender, RoutedEventArgs e)
    {
        var url = _lib.Settings.IssuesUrl;
        if (string.IsNullOrWhiteSpace(url) || url.Contains("你的用户名"))
        {
            MessageBox.Show("还没有配置 Issues 地址。\n\n上传 GitHub 后，在「键位与设置 → 更新与问题反馈」里把\n更新地址 / 下载页 / Issues 地址改成你自己的仓库地址即可。",
                "反馈问题", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        OpenUrl(url);
    }

private void OnOpenKeyMapEditor(object sender, RoutedEventArgs e)
    {
        var dlg = new KeyMapWindow(_lib.EffectiveKeyMap, ComboKeyMap.SelectedValue as string ?? "delta") { Owner = this };
        if (dlg.ShowDialog() != true) return;

        if (dlg.ResultIsCustom)
        {
            _lib.Settings.KeyMapId = "custom";
            _lib.Settings.CustomKeyMap = dlg.ResultMap;
        }
        else
        {
            _lib.Settings.KeyMapId = dlg.ResultMap.Id;
        }
        _loading = true;
        ComboKeyMap.SelectedValue = _lib.Settings.KeyMapId;
        if (ComboKeyMap.SelectedIndex < 0) ComboKeyMap.SelectedIndex = 0;
        _loading = false;
        UpdateKeyMapUi();
        _ctx.RefreshMain();
        LibraryStore.Save(_lib);
        SetStatus("键位映射已更新");
    }

private void OnOverlayPosChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        var o = _lib.Settings.Overlay;
        if (double.TryParse(TxtOverlayX.Text.Trim(), out var x)) o.Left = x;
        if (double.TryParse(TxtOverlayY.Text.Trim(), out var y)) o.Top = y;
        if (_overlay != null)
        {
            _overlay.Left = o.Left;
            _overlay.Top = o.Top;
        }
        LibraryStore.Save(_lib);
    }

private void OnOverlaySettingChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var o = _lib.Settings.Overlay;
        o.Visible = ChkOverlayVisible.IsChecked == true;
        o.ClickThrough = ChkOverlayClickThrough.IsChecked == true;
        o.ShowLanes = ChkOverlayLanes.IsChecked == true;
        o.ShowKeyLetters = ChkOverlayKeys.IsChecked == true;
        o.ShowTitle = ChkOverlayTitle.IsChecked == true;
        o.HideWhilePlaying = ChkHideWhilePlaying.IsChecked == true;
        o.Opacity = SldOverlayOpacity.Value;
        o.MaxStack = ParseInt(TxtOverlayStack.Text, o.MaxStack, 1, 30);
        _lib.Settings.RememberOverlayPosition = ChkRememberOverlayPos.IsChecked == true;

        // 音游下落模式
        o.Mode = ComboOverlayMode.SelectedIndex == 1 ? 1 : 0;
        _loading = true;
        _loading = false;
        o.FallSpeed = Math.Clamp(SldFallSpeed.Value, 80, 600);
        o.LookAheadSeconds = double.TryParse(TxtLookAhead.Text.Trim(), out var la) ? Math.Clamp(la, 0.5, 12) : o.LookAheadSeconds;
        o.ShowJudgmentLine = ChkJudgmentLine.IsChecked == true;
        o.ShowFallKeyHint = ChkFallKeyHint.IsChecked == true;
        TxtFallSpeed.Text = o.FallSpeed.ToString("0") + " px/s";
        TxtOverlayOpacity.Text = o.Opacity.ToString("0.00");
        _ctx.RefreshMain();

        if (o.Visible) ShowOverlay();
        else _overlay?.Hide();
        if (_overlay != null)
        {
            _overlay.ApplySettings(o);
            _overlay.Canvas.Settings = o;
            _overlay.Canvas.InvalidateVisual();
        }
        LibraryStore.Save(_lib);
    }

private void OnPreviewOverlay(object sender, RoutedEventArgs e)
    {
        var o = _lib.Settings.Overlay;
        o.Visible = true;
        ChkOverlayVisible.IsChecked = true;
        ShowOverlay();
        SetStatus("已显示悬浮窗预览");
    }

private void OnRemoveInterrupt(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not InterruptRow row) return;
        _lib.Settings.InterruptKeys.Remove(row.Model);
        RefreshInterruptList();
        LibraryStore.Save(_lib);
    }

private void OnResetInterrupts(object sender, RoutedEventArgs e)
    {
        _lib.Settings.InterruptKeys = InterruptKey.CreateDefaults();
        RefreshInterruptList();
        LibraryStore.Save(_lib);
        SetStatus("打断键已恢复默认：W A S D 空格 1 2 3 4 Tab");
    }

private void OnResetOverlayPosition(object sender, RoutedEventArgs e)
    {
        var o = _lib.Settings.Overlay;
        o.Left = 200;
        o.Top = 120;
        o.Width = 460;
        o.Height = 320;
        ShowOverlay();
        SetStatus("悬浮窗位置已复位");
    }

private void OnResetSettings(object sender, RoutedEventArgs e)
    {
        var r = MessageBox.Show("恢复默认设置（曲谱库不受影响）？", "确认",
            MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (r != MessageBoxResult.OK) return;
        _lib.Settings = new AppSettings();
        LibraryStore.Save(_lib);
        _loading = true;
        LoadFromLibrary();
        _hotkeys.RegisterAll(_lib.Settings.Hotkeys);
        _ctx.RefreshMain();
        SetStatus("已恢复默认设置");
    }

private void OnSaveAll(object sender, RoutedEventArgs e)
    {
        LibraryStore.Save(_lib);
        SetStatus("设置与曲谱库已保存");
    }

private void OnSetOverlayHotkey(object sender, RoutedEventArgs e)
        => EditOverlayHotkey(DefaultHotkeys.ToggleOverlay);

private void OnSetOverlayHotkey2(object sender, RoutedEventArgs e)
        => EditOverlayHotkey(DefaultHotkeys.ToggleOverlay2);

private void OnUnlockOverlay(object sender, RoutedEventArgs e)
    {
        bool lockedNow = _lib.Settings.Overlay.ClickThrough;   // true = 当前锁定 → 点完变解锁
        _ctx.SetOverlayLocked(!lockedNow);
        ChkOverlayClickThrough.IsChecked = !lockedNow;
        RefreshLockButton();
    }

    private void RefreshLockButton()
    {
        BtnLockOverlay.Content = _lib.Settings.Overlay.ClickThrough
            ? "解锁悬浮窗（可拖动 / 缩放）"
            : "锁定悬浮窗（点击穿透，不挡游戏）";
    }

private void OnUpdateUrlChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        _lib.Settings.UpdateUrl = TxtUpdateUrl.Text.Trim();
    }

private void RefreshInterruptList()
    {
        _interruptRows.Clear();
        foreach (var k in _lib.Settings.InterruptKeys)
            _interruptRows.Add(new InterruptRow { Model = k });
        ListInterrupts.ItemsSource = null;
        ListInterrupts.ItemsSource = _interruptRows;
        UpdateInterruptWarn();
    }

private void UpdateInterruptWarn()
    {
        var noteKeys = _lib.EffectiveKeyMap.Keys
            .Select(k => k.Equals("OemComma", StringComparison.OrdinalIgnoreCase) ? "OemComma" : k.ToUpperInvariant())
            .ToHashSet();
        var clashes = _lib.Settings.InterruptKeys
            .Where(k => k.Enabled && noteKeys.Contains(k.Key.ToUpperInvariant()))
            .Select(k => ScoreParser.PrettyKey(k.Key))
            .ToList();

        TxtInterruptWarn.Text = clashes.Count == 0
            ? ""
            : "注意：打断键 " + string.Join("、", clashes) +
              " 与琴键重复，演奏时会自己把自己打断，建议换一个键。";
    }

private void RefreshOverlayPosBoxes()
    {
        var o = _lib.Settings.Overlay;
        _loading = true;
        TxtOverlayX.Text = o.Left.ToString("0");
        TxtOverlayY.Text = o.Top.ToString("0");
        _loading = false;
    }

private void EditOverlayHotkey(string action, bool clearFirst = false)
    {
        var spec = FindHotkey(action);
        if (spec == null)
        {
            spec = new HotkeySpec { Action = action };
            _lib.Settings.Hotkeys.Add(spec);
        }

        if (clearFirst)
        {
            spec.Key = "";
            spec.Modifiers = "";
        }
        else
        {
            var dlg = new HotkeyCaptureWindow(spec) { Owner = this };
            if (dlg.ShowDialog() != true) return;
            if (dlg.Cleared)
            {
                spec.Key = "";
                spec.Modifiers = "";
            }
            else
            {
                spec.Key = dlg.CapturedKey.ToString();
                spec.Modifiers = dlg.CapturedModifiers;
                if (spec.Modifiers.Length == 0)
                    SetStatus("提示：没加 Ctrl/Alt 的热键可能和游戏按键冲突，建议加上");
            }
        }

        var failed = _hotkeys.RegisterAll(_lib.Settings.Hotkeys);
        TxtHotkeyWarn.Text = failed.Count == 0 ? "" : "注册失败：" + string.Join("、", failed);
        ListHotkeys.Items.Refresh();
        RefreshOverlayHotkeyText();
        LibraryStore.Save(_lib);
        SetStatus(string.IsNullOrWhiteSpace(spec.Key)
            ? $"已清除「{HotkeyActions.Describe(action)}」的快捷键"
            : $"「{HotkeyActions.Describe(action)}」已设为 {spec.Text}" +
              (failed.Count > 0 ? "（注册失败，可能被其它程序占用）" : ""));
    }

private HotkeySpec? FindHotkey(string action) =>
        _lib.Settings.Hotkeys.FirstOrDefault(h => string.Equals(h.Action, action, StringComparison.Ordinal));

private void RefreshOverlayHotkeyText()
    {
        var main = FindHotkey(DefaultHotkeys.ToggleOverlay);
        var spare = FindHotkey(DefaultHotkeys.ToggleOverlay2);
        TxtOverlayHotkey.Text = $"显示 / 隐藏快捷键：{main?.Text ?? "（未设置）"}" +
                                $"　｜　备用：{spare?.Text ?? "（未设置，可自己设）"}";
    }

private void NudgeOverlay(double dx, double dy)
    {
        var o = _lib.Settings.Overlay;
        o.Left = Math.Max(-200, o.Left + dx);
        o.Top = Math.Max(-200, o.Top + dy);
        if (_overlay != null)
        {
            _overlay.Left = o.Left;
            _overlay.Top = o.Top;
        }
        _loading = true;
        TxtOverlayX.Text = o.Left.ToString("0");
        TxtOverlayY.Text = o.Top.ToString("0");
        _loading = false;
        LibraryStore.Save(_lib);
        SetStatus($"悬浮窗位置：{o.Left:0}, {o.Top:0}（Alt+L 解锁后也可直接拖）");
    }

private void OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            SetStatus("打开链接失败：" + ex.Message);
        }
    }

private static int ParseInt(string text, int fallback, int min, int max)
        => int.TryParse(text.Trim(), out var v) ? Math.Clamp(v, min, max) : fallback;

private void SyncOverlaySettingsFromWindow()
    {
        if (_overlay == null) return;
        var o = _lib.Settings.Overlay;
        o.Left = _overlay.Left;
        o.Top = _overlay.Top;
        o.Width = _overlay.Width;
        o.Height = _overlay.Height;
    }

    private void UpdateKeyMapUi()
    {
        var map = _lib.EffectiveKeyMap;
        ComboKeyMap.SelectedValue = map.Id == "custom" ? "custom" : map.Id;
        TxtKeyMapNote.Text = map.Note;
    }

    /// <summary>主窗口改过设置（例如拖动悬浮窗）后，让本窗口重新读一遍设置。</summary>
    public void RefreshFromLibrary() => LoadFromLibrary();

    /// <summary>切到指定分类（0=键位与变调 … 7=数据与关于）。</summary>
    public void SelectCategory(int index) =>
        SettingsTabs.SelectedIndex = Math.Clamp(index, 0, SettingsTabs.Items.Count - 1);

}




