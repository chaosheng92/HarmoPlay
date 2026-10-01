using System.IO;
using System.Text;
using HarmoPlay.Models;

namespace HarmoPlay.Core;

/// <summary>问题反馈用的诊断包：版本、系统、键位/悬浮窗设置、日志。不含曲谱内容。</summary>
public static class Diagnostics
{
    public static string FeedbackDir => Path.Combine(LibraryStore.DataDir, "feedback");

    public static string Export(Library lib)
    {
        Directory.CreateDirectory(FeedbackDir);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var file = Path.Combine(FeedbackDir, $"HarmoPlay-诊断-{stamp}.txt");
        File.WriteAllText(file, Build(lib), Encoding.UTF8);
        return file;
    }

    public static string Build(Library lib)
    {
        var sb = new StringBuilder();
        sb.AppendLine("==== 口琴谱演奏器 HarmoPlay 诊断信息 ====");
        sb.AppendLine("生成时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"));
        sb.AppendLine("程序版本：" + UpdateService.CurrentVersion);
        sb.AppendLine("运行目录：" + AppContext.BaseDirectory);
        sb.AppendLine("数据目录：" + LibraryStore.DataDir);
        sb.AppendLine("管理员运行：" + (IsElevated() ? "是" : "否"));
        sb.AppendLine();

        sb.AppendLine("---- 系统 ----");
        sb.AppendLine("OS：" + Environment.OSVersion.VersionString + "  x64=" + Environment.Is64BitOperatingSystem);
        sb.AppendLine(".NET：" + Environment.Version);
        sb.AppendLine("CPU：" + Environment.ProcessorCount + " 核");
        sb.AppendLine("屏幕：" + System.Windows.SystemParameters.PrimaryScreenWidth.ToString("0") + "x" +
                      System.Windows.SystemParameters.PrimaryScreenHeight.ToString("0") + "（逻辑像素）");
        sb.AppendLine();
        sb.AppendLine("---- 输入注入环境 ----");
        var (integrity, rid) = EnvInfo.Integrity;
        sb.AppendLine("程序完整性：" + integrity + "（Windows 会丢弃 Low 完整性进程的模拟输入）");
        sb.AppendLine("提权运行：" + (EnvInfo.IsElevated ? "是" : "否"));
        sb.AppendLine($"会话：{EnvInfo.SessionId}　窗口站：{EnvInfo.WindowStation}　线程桌面：{EnvInfo.Desktop}　当前输入桌面：{EnvInfo.InputDesktop}");
        sb.AppendLine("前台窗口：" + EnvInfo.ForegroundWindowText);
        if (rid == 0x1000)
            sb.AppendLine("⚠ 当前处于低完整性（沙箱 / 受限环境）——自动弹奏的按键会被系统丢弃。");
        var testLog = Path.Combine(LibraryStore.DataDir, "input-test.log");
        if (File.Exists(testLog))
        {
            try
            {
                var lines = File.ReadAllLines(testLog);
                var verdict = lines.FirstOrDefault(l => l.StartsWith("结果：")) ?? "(见 input-test.log)";
                sb.AppendLine("最近输入自检：" + verdict.Trim());
            }
            catch { /* 忽略 */ }
        }
        sb.AppendLine();

        sb.AppendLine("---- 键位映射 ----");
        var map = lib.EffectiveKeyMap;
        sb.AppendLine("预设：" + map.Id + " / " + map.Name);
        sb.AppendLine("琴键：" + string.Join(" ", map.Keys));
        foreach (var m in map.Mods)
            sb.AppendLine($"  前缀「{(string.IsNullOrEmpty(m.Prefix) ? "本音" : m.Prefix)}」 {m.Label} 鼠标={m.Mouse}");
        sb.AppendLine();

        sb.AppendLine("---- 演奏参数 ----");
        var p = lib.Settings.Playback;
        sb.AppendLine($"BPM覆盖={p.BpmOverride} 速度={p.Speed:0.00} Gap={p.GapMs} Lead={p.LeadMs} 倒计时={p.CountdownSeconds} 重复={p.RepeatTimes} 跟练={p.WaitForInput} 同键连音={p.LegatoSameKey}");
        sb.AppendLine();

        sb.AppendLine("---- 悬浮窗 ----");
        var o = lib.Settings.Overlay;
        sb.AppendLine($"模式={(o.Mode == 1 ? "音游下落" : "经典堆叠")} 下落速度={o.FallSpeed} 提前={o.LookAheadSeconds}s 判定线={o.ShowJudgmentLine}");
        sb.AppendLine($"可见={o.Visible} 穿透={o.ClickThrough} 不透明度={o.Opacity:0.00} 位置=({o.Left:0},{o.Top:0}) 尺寸={o.Width:0}x{o.Height:0}");
        sb.AppendLine();

        sb.AppendLine("---- 曲谱库 ----");
        sb.AppendLine($"曲谱 {lib.Songs.Count} 首，分类 {lib.Folders.Count} 个（不导出曲谱内容）");
        sb.AppendLine();

        sb.AppendLine("---- 热键 ----");
        foreach (var h in lib.Settings.Hotkeys)
            sb.AppendLine($"  {HotkeyActions.Describe(h.Action),-34} {h.Text}");
        sb.AppendLine();

        sb.AppendLine("---- 最近日志 ----");
        AppendTail(sb, Path.Combine(AppContext.BaseDirectory, "startup.log"), 60);
        AppendTail(sb, Path.Combine(LibraryStore.DataDir, "error.log"), 40);

        sb.AppendLine();
        sb.AppendLine("反馈时请一并说明：游戏名与显示模式（窗口化/无边框/独占全屏）、是否以管理员运行、");
        sb.AppendLine("是否开了跟练模式、复现步骤、以及悬浮窗截图。");
        return sb.ToString();
    }

    private static void AppendTail(StringBuilder sb, string path, int maxLines)
    {
        try
        {
            if (!File.Exists(path)) return;
            var lines = File.ReadAllLines(path);
            sb.AppendLine($"# {Path.GetFileName(path)}（最后 {Math.Min(maxLines, lines.Length)} 行）");
            foreach (var line in lines.Skip(Math.Max(0, lines.Length - maxLines)))
                sb.AppendLine("  " + line);
        }
        catch (Exception ex)
        {
            sb.AppendLine($"# 读取 {Path.GetFileName(path)} 失败：{ex.Message}");
        }
    }

    private static bool IsElevated()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }
}
