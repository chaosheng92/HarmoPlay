using System.IO;
using System.Reflection;
using System.Text;

namespace HarmoPlay.Core;

/// <summary>
/// 主界面左下角两个「自行下载」入口：示例曲谱、可直接给 AI 的转谱要求。
/// 内容是嵌入在 exe 里的资源（即使 Assets/Resources 文件夹被删掉也能导出）。
/// </summary>
public static class Downloads
{
    public const string SampleScoreFile = "示例曲谱-小星星.json";
    public const string SampleReadmeFile = "示例曲谱-说明.txt";
    public const string AiSpecFile = "AI转谱要求-给AI.txt";

    /// <summary>读取嵌入资源文本；失败时回退到程序目录下的 Resources 文件夹。</summary>
    public static string? ReadResource(string fileName)
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var name = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));
            if (name != null)
            {
                using var stream = asm.GetManifestResourceStream(name);
                if (stream != null)
                {
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    return reader.ReadToEnd();
                }
            }
        }
        catch
        {
            // 落回磁盘
        }

        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Resources", fileName);
            if (File.Exists(path)) return File.ReadAllText(path, Encoding.UTF8);
        }
        catch
        {
            // 忽略
        }
        return null;
    }

    /// <summary>把内容写到指定路径；返回是否成功。</summary>
    public static bool Export(string fileName, string targetPath, out string message)
    {
        var text = ReadResource(fileName);
        if (text == null)
        {
            message = $"找不到内置文件「{fileName}」，请重新下载完整发布包。";
            return false;
        }

        try
        {
            var dir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(targetPath, text, new UTF8Encoding(false));
            message = "已保存：" + targetPath;
            return true;
        }
        catch (Exception ex)
        {
            message = "保存失败：" + ex.Message;
            return false;
        }
    }

    /// <summary>把示例曲谱、示例说明、AI 转谱要求全部导出到一个目录（命令行 --export-docs 用）。</summary>
    public static string ExportAll(string dir)
    {
        var sb = new StringBuilder();
        try
        {
            Directory.CreateDirectory(dir);
        }
        catch (Exception ex)
        {
            return "创建目录失败：" + ex.Message;
        }

        foreach (var file in new[] { SampleScoreFile, SampleReadmeFile, AiSpecFile })
        {
            var ok = Export(file, Path.Combine(dir, file), out var message);
            sb.AppendLine((ok ? "✓ " : "✗ ") + message);
        }
        return sb.ToString();
    }
}
