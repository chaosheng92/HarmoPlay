using System.IO;
using System.Net.Http;
using System.Text.Json.Nodes;

namespace HarmoPlay.Core;

public sealed class UpdateInfo
{
    public bool Available { get; init; }
    public string LatestVersion { get; init; } = "";
    public string Notes { get; init; } = "";
    public string DownloadUrl { get; init; } = "";
    public string Published { get; init; } = "";
    public bool Mandatory { get; init; }
    public string? Sha256 { get; init; }
    public string Message { get; init; } = "";
}

/// <summary>
/// 更新接口：从仓库里的 update.json 读取最新版本信息（支持 http/https/file）。
/// update.json 格式见 README「更新接口」。
/// </summary>
public static class UpdateService
{
    public static string CurrentVersion =>
        typeof(UpdateService).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    public static async Task<UpdateInfo> CheckAsync(string url, TimeSpan? timeout = null)
    {
        if (string.IsNullOrWhiteSpace(url))
            return new UpdateInfo { Message = "未配置更新地址。" };

        try
        {
            string json;
            if (url.StartsWith("file://", StringComparison.OrdinalIgnoreCase) || File.Exists(url))
            {
                var path = url.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
                    ? new Uri(url).LocalPath
                    : url;
                json = await File.ReadAllTextAsync(path).ConfigureAwait(false);
            }
            else
            {
                using var handler = new HttpClientHandler { AllowAutoRedirect = true };
                using var client = new HttpClient(handler) { Timeout = timeout ?? TimeSpan.FromSeconds(12) };
                client.DefaultRequestHeaders.UserAgent.ParseAdd("HarmoPlay/" + CurrentVersion);
                json = await client.GetStringAsync(url).ConfigureAwait(false);
            }

            var root = JsonNode.Parse(json) as JsonObject;
            if (root == null) return new UpdateInfo { Message = "update.json 不是有效的 JSON 对象。" };

            var latest = root["version"]?.ToString() ?? root["Version"]?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(latest))
                return new UpdateInfo { Message = "update.json 缺少 version 字段。" };

            var notes = root["notes"]?.ToString() ?? root["Notes"]?.ToString() ?? "";
            var download = root["download"]?.ToString() ?? root["url"]?.ToString() ?? "";
            var published = root["published"]?.ToString() ?? "";
            bool mandatory = false;
            if (root["mandatory"] is JsonValue mv) bool.TryParse(mv.ToString(), out mandatory);
            var sha = root["sha256"]?.ToString();

            bool newer = IsNewer(latest, CurrentVersion);
            return new UpdateInfo
            {
                Available = newer,
                LatestVersion = latest,
                Notes = notes,
                DownloadUrl = download,
                Published = published,
                Mandatory = mandatory,
                Sha256 = sha,
                Message = newer
                    ? $"发现新版本 {latest}（当前 {CurrentVersion}）"
                    : $"已是最新版本（{CurrentVersion}）",
            };
        }
        catch (Exception ex)
        {
            return new UpdateInfo { Message = "检查更新失败：" + ex.Message };
        }
    }

    public static bool IsNewer(string latest, string current)
    {
        static Version Parse(string s)
        {
            var clean = new string(s.Trim().TrimStart('v', 'V').TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
            return Version.TryParse(clean, out var v) ? v : new Version(0, 0, 0);
        }
        return Parse(latest) > Parse(current);
    }

    public static string Describe(UpdateInfo info)
    {
        if (!info.Available)
            return info.Message;

        var text = $"{info.Message}";
        if (!string.IsNullOrWhiteSpace(info.Published)) text += $"　发布：{info.Published}";
        if (info.Mandatory) text += "　（强制更新）";
        if (!string.IsNullOrWhiteSpace(info.Notes)) text += "\n更新内容：" + info.Notes;
        if (!string.IsNullOrWhiteSpace(info.DownloadUrl)) text += "\n下载：" + info.DownloadUrl;
        return text;
    }
}
