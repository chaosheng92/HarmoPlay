using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace HarmoPlay;

public partial class App : Application
{
    public static string LogPath => Path.Combine(Core.LibraryStore.DataDir, "error.log");

    public App()
    {
        AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
        {
            if (e.Exception is NullReferenceException or ArgumentNullException
                or InvalidOperationException or TypeInitializationException)
                Report("FirstChance", e.Exception);
        };
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Program.Trace("OnStartup");

        DispatcherUnhandledException += (_, args) =>
        {
            Report("Dispatcher", args.Exception);
            Program.Trace("Dispatcher 异常：" + args.Exception.Message);
            MessageBox.Show("程序出现异常：\n" + args.Exception.Message + "\n\n详细信息见：\n" + LogPath,
                "口琴谱演奏器", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                Report("AppDomain", ex);
                Program.Trace("AppDomain 异常：" + ex.Message);
            }
        };
    }

    public static void Report(string source, Exception ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}: {ex}\n\n", Encoding.UTF8);
        }
        catch
        {
            try
            {
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "HarmoPlay-error.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}: {ex}\n\n", Encoding.UTF8);
            }
            catch { /* 忽略 */ }
        }
    }
}
