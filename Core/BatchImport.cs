using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HarmoPlay.Models;

namespace HarmoPlay.Core;

public sealed class BatchImportReport
{
    public int Added { get; set; }
    public int Skipped { get; set; }
    public List<string> Messages { get; } = new();
    public List<string> Warnings { get; } = new();

    public string Summary =>
        $"新增 {Added} 首，跳过 {Skipped} 首" + (Warnings.Count > 0 ? $"，{Warnings.Count} 条提示" : "");
}

/// <summary>
/// 通用批量导入：不依赖任何软件的私有格式，支持
///   · CSV / TSV（Excel、WPS 直接另存即可，带表头）
///   · JSON：单个曲谱对象、对象数组、或 {"songs":[...]}
///   · 简谱 txt（每首一个文件，TITLE=/BPM= 头 + 谱面）
///   · 文件夹：递归导入上面所有格式
/// </summary>
public static class BatchImport
{
    /// <summary>CSV 表头别名（大小写、中英文都认）。</summary>
    private static readonly Dictionary<string, string[]> HeaderAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["name"] = new[] { "name", "title", "曲名", "歌名", "名称", "歌曲" },
        ["score"] = new[] { "score", "简谱", "谱面", "曲谱", "内容", "scoretext" },
        ["bpm"] = new[] { "bpm", "速度", "拍速", "tempo" },
        ["meter"] = new[] { "meter", "拍号", "节拍", "time" },
        ["artist"] = new[] { "artist", "歌手", "艺术家", "演唱", "作者" },
        ["folder"] = new[] { "folder", "分类", "分组", "文件夹", "专辑" },
        ["notation"] = new[] { "notation", "记谱", "记谱法", "notationkind" },
        ["enabled"] = new[] { "enabled", "启用", "是否启用" },
    };

    /// <summary>判断一个 json 是不是"简单曲谱列表"（我们自己/用户手写的），而不是原版软件的私有曲库格式。</summary>
    public static bool LooksLikePlainSongList(string path)
    {
        try
        {
            var text = File.ReadAllText(path, Encoding.UTF8);
            return !text.Contains("CatalogVersion", StringComparison.OrdinalIgnoreCase)
                && !text.Contains("PaidFormat", StringComparison.OrdinalIgnoreCase)
                && !text.Contains("\"Categories\"", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return true;
        }
    }

    public static BatchImportReport FromPaths(Library lib, IEnumerable<string> paths)
    {
        var report = new BatchImportReport();
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                var files = Directory.GetFiles(path, "*.*", SearchOption.AllDirectories)
                    .Where(f => IsSupported(f))
                    .OrderBy(f => f)
                    .ToList();
                if (files.Count == 0)
                {
                    report.Messages.Add($"{path}：目录里没有找到可导入的曲谱文件（支持 .csv/.tsv/.json/.txt）");
                    continue;
                }
                foreach (var f in files) ImportOne(lib, f, report);
                continue;
            }

            if (!File.Exists(path))
            {
                report.Messages.Add($"{path}：文件不存在");
                continue;
            }
            ImportOne(lib, path, report);
        }
        return report;
    }

    private static bool IsSupported(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".csv" or ".tsv" or ".json" or ".txt";
    }

    private static void ImportOne(Library lib, string path, BatchImportReport report)
    {
        var name = Path.GetFileName(path);
        try
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            switch (ext)
            {
                case ".csv":
                case ".tsv":
                    FromDelimited(lib, File.ReadAllText(path, Encoding.UTF8), ext == ".tsv" ? '\t' : '\0', report);
                    break;
                case ".json":
                    FromJson(lib, File.ReadAllText(path, Encoding.UTF8), report);
                    break;
                case ".txt":
                    ImportTxt(lib, path, report);
                    break;
                default:
                    report.Messages.Add($"{name}：不支持的格式");
                    break;
            }
        }
        catch (Exception ex)
        {
            report.Messages.Add($"{name}：读取失败（{ex.Message}）");
        }
    }

    // ------------------------------------------------------------ CSV / TSV

    /// <summary>导入 CSV/TSV。delimiter 传 '\0' 表示自动判断（, ; \t）。</summary>
    public static BatchImportReport FromDelimited(Library lib, string text, char delimiter, BatchImportReport report)
    {
        var rows = ParseDelimited(text, delimiter);
        if (rows.Count == 0)
        {
            report.Messages.Add("CSV 内容为空");
            return report;
        }

        // 找表头行：前 3 行里第一行能识别出"曲名"列的
        int headerIndex = -1;
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int r = 0; r < Math.Min(3, rows.Count); r++)
        {
            var candidate = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int c = 0; c < rows[r].Count; c++)
            {
                var cell = rows[r][c].Trim().Trim('"');
                foreach (var (key, aliases) in HeaderAliases)
                {
                    if (aliases.Any(a => string.Equals(a, cell, StringComparison.OrdinalIgnoreCase)))
                    {
                        candidate[key] = c;
                        break;
                    }
                }
            }
            if (candidate.ContainsKey("name") && candidate.ContainsKey("score"))
            {
                headerIndex = r;
                map = candidate;
                break;
            }
        }

        if (headerIndex < 0)
        {
            report.Messages.Add("没找到表头：第一行请写成 「曲名,简谱,BPM,拍号,歌手,分类」（中英文均可）");
            return report;
        }

        var folderCache = new Dictionary<string, SongFolder>(StringComparer.Ordinal);
        for (int r = headerIndex + 1; r < rows.Count; r++)
        {
            var row = rows[r];
            if (row.All(string.IsNullOrWhiteSpace)) continue;

            string Get(string key) => map.TryGetValue(key, out var idx) && idx < row.Count ? row[idx].Trim() : "";

            var title = Get("name");
            var score = Get("score");
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(score))
            {
                report.Skipped++;
                report.Messages.Add($"第 {r + 1} 行：缺少曲名或简谱，已跳过");
                continue;
            }

            double bpm = double.TryParse(Get("bpm"), NumberStyles.Any, CultureInfo.InvariantCulture, out var b) && b > 0 ? b : 90;
            var meter = Get("meter");
            if (string.IsNullOrWhiteSpace(meter)) meter = "4/4";
            var artist = Get("artist");
            var folderName = Get("folder");
            var notationText = Get("notation");
            var enabledText = Get("enabled");

            var notation = string.IsNullOrWhiteSpace(notationText)
                ? ScoreParser.Parse(score, title, bpm).Notation
                : NotationKindText.FromText(notationText);

            string? folderId = null;
            if (!string.IsNullOrWhiteSpace(folderName))
            {
                if (!folderCache.TryGetValue(folderName, out var folder))
                {
                    folder = lib.Folders.FirstOrDefault(f => f.Name == folderName);
                    if (folder == null)
                    {
                        folder = new SongFolder { Name = folderName };
                        lib.Folders.Add(folder);
                    }
                    folderCache[folderName] = folder;
                }
                folderId = folder.Id;
            }

            AddSong(lib, new Song
            {
                Name = title,
                Score = score,
                Bpm = bpm,
                Meter = meter,
                Artist = artist,
                FolderId = folderId,
                Notation = notation.ToId(),
                Enabled = !string.Equals(enabledText, "false", StringComparison.OrdinalIgnoreCase)
                          && enabledText != "0" && enabledText != "否",
                Source = "批量导入 CSV",
            }, report);
        }

        return report;
    }

    /// <summary>极简 CSV 解析：支持双引号包裹、"" 转义、字段内换行；自动判断分隔符。</summary>
    private static List<List<string>> ParseDelimited(string text, char delimiter)
    {
        text = text.TrimStart('\uFEFF');
        if (delimiter == '\0')
        {
            var firstLine = text.Split('\n').FirstOrDefault(l => l.Trim().Length > 0) ?? "";
            delimiter = firstLine.Contains('\t') ? '\t'
                      : firstLine.Count(c => c == ';') > firstLine.Count(c => c == ',') ? ';'
                      : ',';
        }

        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                    else inQuotes = false;
                }
                else cell.Append(ch);
                continue;
            }

            switch (ch)
            {
                case '"':
                    inQuotes = true;
                    break;
                case '\r':
                    break;
                case '\n':
                    row.Add(cell.ToString());
                    cell.Clear();
                    rows.Add(row);
                    row = new List<string>();
                    break;
                default:
                    if (ch == delimiter)
                    {
                        row.Add(cell.ToString());
                        cell.Clear();
                    }
                    else cell.Append(ch);
                    break;
            }
        }
        if (cell.Length > 0 || row.Count > 0)
        {
            row.Add(cell.ToString());
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>把整库导出成 CSV（可编辑后再导回，字段与导入完全对应）。</summary>
    public static string ToCsv(Library lib)
    {
        var sb = new StringBuilder();
        sb.AppendLine("曲名,简谱,BPM,拍号,歌手,分类,记谱,启用");
        foreach (var s in lib.Songs.OrderBy(s => s.Name, StringComparer.CurrentCulture))
        {
            var folder = lib.Folders.FirstOrDefault(f => f.Id == s.FolderId)?.Name ?? "";
            sb.Append(Csv(s.Name)).Append(',')
              .Append(Csv(s.Score)).Append(',')
              .Append(s.Bpm.ToString("0.##", CultureInfo.InvariantCulture)).Append(',')
              .Append(Csv(s.Meter)).Append(',')
              .Append(Csv(s.Artist)).Append(',')
              .Append(Csv(folder)).Append(',')
              .Append(Csv(NotationKindText.FromId(s.Notation))).Append(',')
              .Append(s.Enabled ? "true" : "false")
              .AppendLine();
        }
        return sb.ToString();
    }

    private static string Csv(string value)
    {
        value ??= "";
        if (value.Contains('"') || value.Contains(',') || value.Contains('\n') || value.Contains('\r'))
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        return value;
    }

    // ------------------------------------------------------------ JSON

    /// <summary>支持：单个曲谱对象 / 对象数组 / {"songs":[...]}（也兼容原版 library.json 的 Songs）。</summary>
    public static BatchImportReport FromJson(Library lib, string text, BatchImportReport? report = null)
    {
        report ??= new BatchImportReport();
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(text);
        }
        catch (Exception ex)
        {
            report.Messages.Add("JSON 解析失败：" + ex.Message);
            return report;
        }

        var list = new List<JsonObject>();
        if (node is JsonArray topArray)
        {
            list.AddRange(topArray.OfType<JsonObject>());
        }
        else if (node is JsonObject topObject)
        {
            var arr = (topObject["songs"] ?? topObject["Songs"]) as JsonArray;
            if (arr != null) list.AddRange(arr.OfType<JsonObject>());
            else list.Add(topObject);
        }

        if (list.Count == 0)
        {
            report.Messages.Add("JSON 里没有找到曲谱（可以是单个曲谱对象、对象数组，或 {\"songs\":[...]}）");
            return report;
        }

        foreach (var o in list)
        {
            var title = (o["Name"] ?? o["name"] ?? o["TITLE"] ?? o["曲名"])?.ToString() ?? "";
            var score = (o["Score"] ?? o["score"] ?? o["简谱"])?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(score))
            {
                report.Skipped++;
                report.Messages.Add("有一条缺少 Name / Score，已跳过");
                continue;
            }

            var bpm = double.TryParse((o["Bpm"] ?? o["bpm"] ?? o["BPM"])?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var b) && b > 0 ? b : 90;
            var notationText = (o["Notation"] ?? o["notation"])?.ToString() ?? "";
            AddSong(lib, new Song
            {
                Name = title,
                Score = score,
                Bpm = bpm,
                Meter = (o["Meter"] ?? o["meter"] ?? o["METER"])?.ToString() ?? "4/4",
                Artist = (o["Artist"] ?? o["artist"])?.ToString() ?? "",
                Notation = string.IsNullOrWhiteSpace(notationText)
                    ? ScoreParser.Parse(score, title, bpm).Notation.ToId()
                    : NotationKindText.FromText(notationText).ToId(),
                Enabled = true,
                Source = "批量导入 JSON",
            }, report);
        }
        return report;
    }

    // ------------------------------------------------------------ txt

    private static void ImportTxt(Library lib, string path, BatchImportReport report)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var text = File.ReadAllText(path, Encoding.UTF8);
        var parsed = ScoreParser.Parse(text, name);
        if (parsed.Notes.Count == 0)
        {
            report.Skipped++;
            report.Messages.Add($"{name}：没有解析到音符，已跳过");
            return;
        }
        AddSong(lib, new Song
        {
            Name = string.IsNullOrWhiteSpace(parsed.Title) ? name : parsed.Title,
            Score = text,
            Bpm = parsed.Bpm > 0 ? parsed.Bpm : 90,
            Notation = parsed.Notation.ToId(),
            Source = "批量导入 txt",
        }, report);
    }

    // ------------------------------------------------------------ 公共

    /// <summary>把"整份 txt 曲谱"（含 TITLE=/BPM= 头与 // 注释）里的谱面部分抽出来，用于校验。</summary>
    public static string CleanScoreText(string text)
    {
        var lines = text
            .Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l =>
            {
                var t = l.Trim();
                return t.Length > 0
                    && !t.StartsWith("//")
                    && !System.Text.RegularExpressions.Regex.IsMatch(t, @"^[A-Za-z_]+\s*=");
            });
        return string.Join("\n", lines);
    }

    private static void AddSong(Library lib, Song song, BatchImportReport report)
    {
        if (lib.Songs.Any(s => string.Equals(s.Name, song.Name, StringComparison.OrdinalIgnoreCase)))
        {
            report.Skipped++;
            report.Messages.Add($"{song.Name}：同名曲谱已存在，跳过（不会覆盖你自己的版本）");
            return;
        }

        // 顺手校验，把语法/音域问题作为提示列出来
        var parsed = ScoreParser.Parse(song.Score, song.Name, song.Bpm, NotationKindText.FromText(song.Notation));
        if (parsed.Notes.Count == 0)
        {
            report.Skipped++;
            report.Messages.Add($"{song.Name}：谱面解析不出音符，跳过");
            return;
        }

        // 只有"固定音高"记谱才按 AI 转谱规范严格校验；
        // 直接按键记谱（可视化/D-hydra）里 ^、-、8 都是合法的，不能套那套语法。
        var notation = NotationKindText.FromText(song.Notation);
        if (notation == NotationKind.Pitch)
        {
            var reportText = new ValidationReport { FileName = song.Name };
            ScoreValidator.ValidateScoreText(reportText, CleanScoreText(song.Score), song.Bpm, song.Meter);
            if (!reportText.Ok)
            {
                report.Skipped++;
                report.Messages.Add($"{song.Name}：校验未通过（{reportText.Errors.FirstOrDefault()}）");
                return;
            }
            foreach (var w in reportText.Warnings.Take(2))
                report.Warnings.Add($"{song.Name}：{w}");
        }
        else
        {
            report.Warnings.Add($"{song.Name}：直接按键记谱，已按该记谱解析出 {parsed.Notes.Count} 个音");
        }

        lib.Songs.Add(song);
        report.Added++;
    }
}
