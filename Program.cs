using System.IO;
using System.Text;
using System.Windows;

namespace HarmoPlay;

/// <summary>自定义入口点：负责启动追踪、自检模式与顶层异常兜底。</summary>
public static class Program
{
    public static string TracePath => Path.Combine(AppContext.BaseDirectory, "startup.log");

    [STAThread]
    public static void Main(string[] args)
    {
        Trace("Main 进入，参数：" + string.Join(" ", args));

        if (args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            string text;
            try
            {
                text = SelfTest.Run();
            }
            catch (Exception ex)
            {
                text = "自检崩溃：" + ex;
            }
            WriteReport("selftest.log", text);
            Trace("自检结束");
            return;
        }

        if (args.Any(a => a.Equals("--import", StringComparison.OrdinalIgnoreCase)))
        {
            var idx = Array.FindIndex(args, a => a.Equals("--import", StringComparison.OrdinalIgnoreCase));
            var path = idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : "";
            WriteReport("import.log", Cli.Import(path));
            return;
        }

        if (args.Any(a => a.Equals("--list", StringComparison.OrdinalIgnoreCase)))
        {
            WriteReport("library.txt", Cli.ListSongs());
            return;
        }

        if (args.Any(a => a.Equals("--validate", StringComparison.OrdinalIgnoreCase)))
        {
            var files = CollectAfter(args, "--validate");
            var text = files.Count == 0 ? Cli.Validate("") : string.Join("\n\n", files.Select(Cli.Validate));
            WriteReport("validate.log", text);
            Console.WriteLine(text);
            return;
        }

        if (args.Any(a => a.Equals("--checkupdate", StringComparison.OrdinalIgnoreCase)))
        {
            var url = CollectAfter(args, "--checkupdate").FirstOrDefault();
            var text = Cli.CheckUpdate(url).GetAwaiter().GetResult();
            WriteReport("update-check.log", text);
            Console.WriteLine(text);
            return;
        }

        if (args.Any(a => a.Equals("--feedback", StringComparison.OrdinalIgnoreCase)))
        {
            var text = Cli.Feedback();
            WriteReport("feedback.log", text);
            Console.WriteLine(text);
            return;
        }

        if (args.Any(a => a.Equals("--inputtest", StringComparison.OrdinalIgnoreCase)))
        {
            var text = Cli.InputTest();
            WriteReport("input-test.log", text);
            Console.WriteLine(text);
            return;
        }

        if (args.Any(a => a.Equals("--export-docs", StringComparison.OrdinalIgnoreCase)))
        {
            var dir = CollectAfter(args, "--export-docs").FirstOrDefault() ?? "";
            var text = Cli.ExportDocs(dir);
            WriteReport("export-docs.log", text);
            Console.WriteLine(text);
            return;
        }

        if (args.Any(a => a.Equals("--export-csv", StringComparison.OrdinalIgnoreCase)))
        {
            var file = CollectAfter(args, "--export-csv").FirstOrDefault() ?? "";
            var text = Cli.ExportCsv(file);
            WriteReport("export-csv.log", text);
            Console.WriteLine(text);
            return;
        }

        if (args.Any(a => a.Equals("--help", StringComparison.OrdinalIgnoreCase)))
        {
            WriteReport("help.txt", Cli.Help());
            return;
        }

        try
        {
            var app = new App();
            app.InitializeComponent();
            Trace("App 已初始化，开始 Run");
            app.Run();
            Trace("Run 正常返回");
        }
        catch (Exception ex)
        {
            Trace("致命异常：" + ex);
            try
            {
                MessageBox.Show("程序启动失败：\n" + ex.Message + "\n\n详细信息见：\n" + TracePath,
                    "口琴谱演奏器", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch { /* 忽略 */ }
        }
    }

    public static void Trace(string message)
    {
        try
        {
            File.AppendAllText(TracePath, $"[{DateTime.Now:HH:mm:ss.fff}] {message}\n", Encoding.UTF8);
        }
        catch { /* 忽略 */ }
    }

    private static void WriteReport(string fileName, string text)
    {
        try
        {
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, fileName), text, Encoding.UTF8);
        }
        catch
        {
            try
            {
                File.WriteAllText(Path.Combine(Core.LibraryStore.DataDir, fileName), text, Encoding.UTF8);
            }
            catch { /* 忽略 */ }
        }
    }

    /// <summary>取某个开关后面跟的所有参数（直到下一个 -- 开头）。</summary>
    private static List<string> CollectAfter(string[] args, string flag)
    {
        var result = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].Equals(flag, StringComparison.OrdinalIgnoreCase)) continue;
            for (int j = i + 1; j < args.Length && !args[j].StartsWith("--"); j++)
                result.Add(args[j]);
        }
        return result;
    }
}

