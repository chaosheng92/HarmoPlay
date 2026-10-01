using System.IO;
using System.Text;
using HarmoPlay.Models;

namespace HarmoPlay;

/// <summary>命令行模式（--import / --list / --help），便于批量导入与排查。</summary>
public static class Cli
{
    public static string Import(string path)
    {
        var sb = new StringBuilder();
        try
        {
            if (string.IsNullOrWhiteSpace(path))
                return "用法：HarmoPlay.exe --import <文件 | 文件夹>（支持 .csv/.tsv/.json/.txt）";

            var lib = Core.LibraryStore.Load();
            sb.AppendLine("数据目录：" + Core.LibraryStore.DataDir);
            sb.AppendLine("导入前曲谱数：" + lib.Songs.Count);

            // 原版 library.json 仍优先走专用导入（能带分类/统计）；其余一律走通用批量导入
            if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                && !Core.LibraryStore.IsSingleSongJson(path)
                && !Core.BatchImport.LooksLikePlainSongList(path))
            {
                var (imported, skipped, msg) = Core.LibraryStore.ImportSquirrelLibrary(lib, path);
                sb.AppendLine(msg);
            }
            else
            {
                var report = Core.BatchImport.FromPaths(lib, new[] { path });
                sb.AppendLine(report.Summary);
                foreach (var m in report.Messages) sb.AppendLine("  · " + m);
                foreach (var w in report.Warnings.Take(10)) sb.AppendLine("  ⚠ " + w);
            }

            Core.LibraryStore.Save(lib);
            sb.AppendLine("导入后曲谱数：" + lib.Songs.Count);
            sb.AppendLine("分类数：" + lib.Folders.Count);
        }
        catch (Exception ex)
        {
            sb.AppendLine("导入失败：" + ex);
        }
        return sb.ToString();
    }

    public static string ListSongs()
    {
        var sb = new StringBuilder();
        try
        {
            var lib = Core.LibraryStore.Load();
            sb.AppendLine("数据目录：" + Core.LibraryStore.DataDir);
            sb.AppendLine($"曲谱 {lib.Songs.Count} 首，分类 {lib.Folders.Count} 个");
            var folders = lib.Folders.ToDictionary(f => f.Id, f => f.Name);
            foreach (var song in lib.Songs)
            {
                var folder = song.FolderId != null && folders.TryGetValue(song.FolderId, out var n) ? n : "未分类";
                var parsed = Core.ScoreParser.Parse(song.Score, song.Name, song.Bpm);
                sb.AppendLine($"[{folder}] {song.DisplayName}  |  {parsed.Format}  {parsed.NoteCount} 音  {song.Bpm:0.#} BPM  ({song.Source})");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine("失败：" + ex);
        }
        return sb.ToString();
    }

    public static async Task<string> CheckUpdate(string? url)
    {
        var lib = Core.LibraryStore.Load();
        var target = string.IsNullOrWhiteSpace(url) ? lib.Settings.UpdateUrl : url;
        var sb = new StringBuilder();
        sb.AppendLine("更新接口检查");
        sb.AppendLine("当前版本：" + Core.UpdateService.CurrentVersion);
        sb.AppendLine("更新地址：" + target);
        sb.AppendLine();

        var info = await Core.UpdateService.CheckAsync(target);
        sb.AppendLine(Core.UpdateService.Describe(info));
        sb.AppendLine();
        sb.AppendLine("原始字段：");
        sb.AppendLine("  最新版本：" + info.LatestVersion);
        sb.AppendLine("  发布时间：" + info.Published);
        sb.AppendLine("  下载地址：" + info.DownloadUrl);
        sb.AppendLine("  sha256：" + (info.Sha256 ?? "(未提供)"));
        sb.AppendLine("  强制更新：" + (info.Mandatory ? "是" : "否"));

        lib.Settings.LastUpdateCheck = DateTime.Now;
        lib.Settings.LastUpdateResult = info.Message;
        Core.LibraryStore.Save(lib);
        return sb.ToString();
    }

    public static string Feedback()
    {
        var lib = Core.LibraryStore.Load();
        var file = Core.Diagnostics.Export(lib);
        return "诊断包已导出：\n" + file + "\n\n把它附到 GitHub Issue 即可（不含曲谱内容）。\n\n" + Core.Diagnostics.Build(lib);
    }

    public static string ExportDocs(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) dir = Path.Combine(Core.LibraryStore.DataDir, "文档与示例");
        var text = Core.Downloads.ExportAll(dir);
        return "导出示例曲谱与 AI 转谱要求：" + Environment.NewLine + text;
    }

    /// <summary>导出整个曲谱库为 CSV（可用 Excel/WPS 编辑后原样导回）。</summary>
    public static string ExportCsv(string path)
    {
        var lib = Core.LibraryStore.Load();
        if (string.IsNullOrWhiteSpace(path))
            path = Path.Combine(Core.LibraryStore.DataDir, "曲谱库导出.csv");
        try
        {
            File.WriteAllText(path, Core.BatchImport.ToCsv(lib), new UTF8Encoding(true));
        }
        catch (Exception ex)
        {
            return "导出失败：" + ex.Message;
        }
        return $"已导出 {lib.Songs.Count} 首曲谱到：\n{path}\n\n" +
               "用 Excel / WPS 打开编辑（列：曲名,简谱,BPM,拍号,歌手,分类,记谱,启用），\n" +
               "改完另存为 CSV（UTF-8）再用「导入曲谱文件…」或 --import 导回即可。";
    }

    public static string InputTest()
    {
        var report = Core.InputSelfTest.Run().ToString();
        try
        {
            File.WriteAllText(Path.Combine(Core.LibraryStore.DataDir, "input-test.log"), report, Encoding.UTF8);
        }
        catch { /* 忽略 */ }
        return report;
    }

    public static string Validate(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "用法：HarmoPlay.exe --validate <曲名.json|曲名.txt> [更多文件 ...]";

        var text = File.ReadAllText(path, Encoding.UTF8);

        // 简谱 txt / 内置 seed 格式（TITLE= / BPM= / // 注释 + 谱面）也能校验：
        // 抽出谱面部分，转成等效 JSON 再走同一套校验。
        if (!text.TrimStart().StartsWith("{"))
        {
            string Look(string key, string fallback)
            {
                var m = System.Text.RegularExpressions.Regex.Match(text,
                    $@"(?m)^\s*{key}\s*=\s*(.+?)\s*$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                return m.Success ? m.Groups[1].Value : fallback;
            }

            var scoreLines = text
                .Split('\n')
                .Select(l => l.TrimEnd('\r'))
                .Where(l =>
                {
                    var t = l.Trim();
                    return t.Length > 0
                        && !t.StartsWith("//")
                        && !System.Text.RegularExpressions.Regex.IsMatch(t, @"^[A-Za-z_]+\s*=");
                });

            var scoreText = string.Join("\n", scoreLines);
            var report = new Core.ValidationReport { FileName = Path.GetFileName(path) };
            report.Infos.Add("按简谱 txt / 内置 seed 格式校验（已自动跳过 TITLE=、BPM= 与 // 注释行）");
            Core.ScoreValidator.ValidateScoreText(report, scoreText,
                int.TryParse(Look("BPM", "90"), out var bpmValue) ? bpmValue : 90,
                Look("METER", "4/4"));
            return report.ToString();
        }

        return Core.ScoreValidator.ValidateJsonFile(path).ToString();
    }

    public static string Help() => """
        口琴谱演奏器 HarmoPlay —— 命令行参数

          HarmoPlay.exe                 启动图形界面
          --selftest                    运行自检并写出 selftest.log
          --import <文件|目录>          导入曲谱（.csv/.tsv/.json/.txt，或整个文件夹）
          --validate <曲名.json>        按转谱规范校验 AI 生成的曲谱文件
          --checkupdate                 检查更新（结果写到 update-check.log）
          --inputtest                   输入注入自检（判断"自动弹奏为什么没反应"）
          --export-docs [目录]           导出「示例曲谱」与「AI 转谱要求.txt」
          --export-csv [文件]            把整个曲谱库导出为 CSV（可编辑后再导回）
          --feedback                    导出诊断包（问题反馈用）到 feedback\ 目录
          --list                        列出曲谱库内容到 library.txt
          --help                        显示本帮助

        简谱格式说明（自动识别三种）：
          1) 三角洲可视化曲谱：TITLE=曲名 / BPM=90 / 1 2 3 4 5 6 7 8 / 5 - 延长 / 0 休止 / | 小节线
             变调前缀：b 降调(左键)  # 半音(中键)  ^ 升调(右键)  #b 半+降  #^ 半+升
          2) 固定音高：1:0.5 #6:1 b3:0.5（音:拍数）','=低八度 '''=高八度
          3) D-hydra 键位谱：含「键位 / 节奏」两列，自动识别

        演奏快捷键（可在「键位与设置」里改）：
          Alt+1 播放/暂停  Alt+2 停止  Alt+3 下一首  Alt+↑ 上一首
          Alt+H 显示/隐藏悬浮窗  Alt+L 锁定/解锁悬浮窗  Alt+Z 自动/跟练切换
          Alt+4~9 快捷曲 1~6
        """;
}


