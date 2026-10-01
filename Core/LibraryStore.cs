using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using HarmoPlay.Models;

namespace HarmoPlay.Core;

public sealed class Library
{
    public List<SongFolder> Folders { get; set; } = new();
    public List<Song> Songs { get; set; } = new();
    public AppSettings Settings { get; set; } = new();

    public KeyMap EffectiveKeyMap =>
        Settings.KeyMapId == "custom"
            ? Settings.CustomKeyMap
            : KeyMap.ById(Settings.KeyMapId, Settings.CustomKeyMap);
}

/// <summary>曲谱库 / 设置的读写与导入导出。</summary>
public static class LibraryStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
    };

    private static string? _dataDir;

    /// <summary>
    /// 数据目录：优先放在程序同目录（绿色便携），不可写时依次回退到
    /// %APPDATA% → %LOCALAPPDATA% → 文档 → 临时目录。
    /// </summary>
    public static string DataDir
    {
        get
        {
            if (_dataDir != null) return _dataDir;

            foreach (var candidate in Candidates())
            {
                try
                {
                    Directory.CreateDirectory(candidate);
                    var probe = Path.Combine(candidate, ".write-test");
                    File.WriteAllText(probe, "ok");
                    File.Delete(probe);
                    _dataDir = candidate;
                    return _dataDir;
                }
                catch
                {
                    // 换下一个候选目录
                }
            }

            _dataDir = Path.GetTempPath();
            return _dataDir;
        }
    }

    private static IEnumerable<string> Candidates()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "data");

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrEmpty(appData)) yield return Path.Combine(appData, "HarmoPlay");

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(local)) yield return Path.Combine(local, "HarmoPlay");

        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (!string.IsNullOrEmpty(docs)) yield return Path.Combine(docs, "HarmoPlay");

        yield return Path.Combine(Path.GetTempPath(), "HarmoPlay");
    }

    public static string LibraryPath => Path.Combine(DataDir, "library.json");
    public static string SettingsPath => Path.Combine(DataDir, "settings.json");
    public static string SeedDir => Path.Combine(AppContext.BaseDirectory, "Assets", "Seed");

    public static Library Load()
    {
        var lib = new Library();
        try
        {
            if (File.Exists(LibraryPath))
            {
                var json = File.ReadAllText(LibraryPath, Encoding.UTF8);
                var loaded = JsonSerializer.Deserialize<Library>(json, JsonOpts);
                if (loaded != null) lib = loaded;
            }
        }
        catch (Exception)
        {
            TryBackup(LibraryPath);
        }

        lib.Folders ??= new List<SongFolder>();
        lib.Songs ??= new List<Song>();
        lib.Settings ??= new AppSettings();

        // settings.json 优先（单独保存设置，便于备份与手动改）
        try
        {
            if (File.Exists(SettingsPath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath, Encoding.UTF8), JsonOpts);
                if (s != null) lib.Settings = s;
            }
        }
        catch (Exception)
        {
            TryBackup(SettingsPath);
        }

        lib.Settings.Hotkeys ??= DefaultHotkeys.CreateDefaults();
        // 版本升级后补齐新增的热键条目（已存在的保持用户设置）
        foreach (var def in DefaultHotkeys.CreateDefaults())
        {
            if (lib.Settings.Hotkeys.All(h => !string.Equals(h.Action, def.Action, StringComparison.Ordinal)))
                lib.Settings.Hotkeys.Add(def);
        }
        lib.Settings.Overlay ??= new OverlaySettings();
        lib.Settings.Playback ??= new PlaybackOptions();
        lib.Settings.CustomKeyMap ??= KeyMap.CreateDelta();

        if (!lib.Settings.FirstRunDone)
        {
            SeedSamples(lib, force: lib.Songs.Count == 0);
            lib.Settings.FirstRunDone = true;
            Save(lib);
        }
        return lib;
    }

    public static void Save(Library lib)
    {
        try
        {
            File.WriteAllText(LibraryPath, JsonSerializer.Serialize(lib, JsonOpts), Encoding.UTF8);
        }
        catch (Exception)
        {
            // 忽略写入失败（只读目录等）
        }
        try
        {
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(lib.Settings, JsonOpts), Encoding.UTF8);
        }
        catch (Exception)
        {
            // 忽略
        }
    }

    private static void TryBackup(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Copy(path, path + ".bad", true);
        }
        catch { /* ignore */ }
    }

    // ------------------------------------------------------------ 示例曲谱

    /// <summary>首次运行时释放内置示例曲谱。</summary>
    public static int SeedSamples(Library lib, bool force = false)
    {
        if (!Directory.Exists(SeedDir)) return 0;
        var folder = lib.Folders.FirstOrDefault(f => f.Name == "示例曲谱");
        if (folder == null)
        {
            folder = new SongFolder { Name = "示例曲谱" };
            lib.Folders.Add(folder);
        }

        int added = 0;
        foreach (var file in Directory.GetFiles(SeedDir, "*.txt").OrderBy(f => f))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (!force && lib.Songs.Any(s => s.Name == name)) continue;
            var text = File.ReadAllText(file, Encoding.UTF8);
            var parsed = ScoreParser.Parse(text, name);
            lib.Songs.Add(new Song
            {
                Name = string.IsNullOrWhiteSpace(parsed.Title) ? name : parsed.Title,
                Score = text,
                Bpm = parsed.Bpm > 0 ? parsed.Bpm : 90,
                FolderId = folder.Id,
                Source = "内置示例",
            });
            added++;
        }
        return added;
    }

    // ------------------------------------------------------------ 导入

    /// <summary>导入外部曲库的 library.json / catalog 缓存。</summary>
    public static (int imported, int skipped, string message) ImportSquirrelLibrary(Library lib, string path)
    {
        int imported = 0, skipped = 0;
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8));
            if (node is not JsonObject root) return (0, 0, "不是有效的 JSON。");

            var folderIdMap = new Dictionary<string, string>();
            foreach (var key in new[] { "Folders", "Categories" })
            {
                if (root[key] is not JsonArray arr) continue;
                foreach (var f in arr.OfType<JsonObject>())
                {
                    var oldId = f["Id"]?.GetValue<string>() ?? Guid.NewGuid().ToString("N");
                    var name = f["Name"]?.GetValue<string>() ?? "未命名分类";
                    var exist = lib.Folders.FirstOrDefault(x => x.Name == name);
                    if (exist == null)
                    {
                        exist = new SongFolder { Name = name };
                        lib.Folders.Add(exist);
                    }
                    folderIdMap[oldId] = exist.Id;
                }
            }

            if (root["Songs"] is not JsonArray songs)
                return (0, 0, "没有找到 Songs 数组。");

            foreach (var s in songs.OfType<JsonObject>())
            {
                var score = s["Score"]?.GetValue<string>() ?? "";
                if (string.IsNullOrWhiteSpace(score)) { skipped++; continue; }

                var name = s["Name"]?.GetValue<string>() ?? "未命名";
                var artist = s["Artist"]?.GetValue<string>() ?? "";
                if (lib.Songs.Any(x => x.Name == name && x.Artist == artist)) { skipped++; continue; }

                string? folderId = null;
                var oldFolder = s["FolderId"]?.GetValue<string>() ?? s["CategoryId"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(oldFolder) && folderIdMap.TryGetValue(oldFolder, out var mapped))
                    folderId = mapped;

                lib.Songs.Add(new Song
                {
                    Name = name,
                    Artist = artist,
                    FolderId = folderId,
                    Score = score,
                    Bpm = ReadDouble(s["Bpm"], 90),
                    Meter = s["Meter"]?.GetValue<string>() ?? "4/4",
                    Enabled = !(s["Enabled"] is JsonValue ev) || ReadBool(ev, true),
                    Source = "导入·外部曲库",
                    Notation = "pitch",
                });
                imported++;
            }
        }
        catch (Exception ex)
        {
            return (imported, skipped, "导入失败：" + ex.Message);
        }

        var msg = $"导入 {imported} 首" + (skipped > 0 ? $"，跳过 {skipped} 首（重复或无曲谱内容）" : "");
        return (imported, skipped, msg);
    }

    /// <summary>导入 .txt 简谱文件（三角洲可视化 / D-hydra / 简谱文本）与单曲 .json。</summary>
    public static (int imported, string message) ImportScoreFiles(Library lib, IEnumerable<string> files)
    {
        int imported = 0;
        var folder = lib.Folders.FirstOrDefault(f => f.Name == "导入曲谱");
        if (folder == null)
        {
            folder = new SongFolder { Name = "导入曲谱" };
            lib.Folders.Add(folder);
        }

        var errors = new List<string>();
        foreach (var file in files)
        {
            try
            {
                var text = File.ReadAllText(file, Encoding.UTF8);
                bool isJson = file.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
                NotationKind notation;

                if (isJson)
                {
                    var (song, message) = ParseSingleSongJson(text, Path.GetFileNameWithoutExtension(file));
                    if (song == null)
                    {
                        errors.Add($"{Path.GetFileName(file)}（{message}）");
                        continue;
                    }
                    song.FolderId = folder.Id;
                    song.Source = "导入·AI转谱 JSON";
                    lib.Songs.Add(song);
                    imported++;
                    continue;
                }

                var parsed = ScoreParser.Parse(text, Path.GetFileNameWithoutExtension(file));
                if (parsed.Notes.Count == 0)
                {
                    errors.Add(Path.GetFileName(file) + "（没有解析到音符）");
                    continue;
                }
                notation = parsed.Notation;
                lib.Songs.Add(new Song
                {
                    Name = string.IsNullOrWhiteSpace(parsed.Title) ? Path.GetFileNameWithoutExtension(file) : parsed.Title,
                    Score = text,
                    Bpm = parsed.Bpm > 0 ? parsed.Bpm : 90,
                    FolderId = folder.Id,
                    Source = "导入·" + parsed.Format,
                    Notation = notation.ToId(),
                });
                imported++;
            }
            catch (Exception ex)
            {
                errors.Add($"{Path.GetFileName(file)}（{ex.Message}）");
            }
        }

        var msg = $"导入 {imported} 个曲谱文件";
        if (errors.Count > 0) msg += "；失败：" + string.Join("、", errors.Take(5));
        return (imported, msg);
    }

    /// <summary>解析单个曲谱 JSON（转谱规范格式：Name/Score/Bpm/Meter/Enabled）。</summary>
    public static (Song? song, string message) ParseSingleSongJson(string text, string fallbackName)
    {
        try
        {
            var root = JsonNode.Parse(text) as JsonObject;
            if (root == null) return (null, "JSON 根节点不是对象");
            if (root["Score"] is not JsonValue scoreNode) return (null, "没有 Score 字段");

            var name = root["Name"]?.ToString();
            return (new Song
            {
                Name = string.IsNullOrWhiteSpace(name) ? fallbackName : name,
                Score = scoreNode.ToString(),
                Bpm = ReadDouble(root["Bpm"], 90),
                Meter = root["Meter"]?.ToString() ?? "4/4",
                Enabled = root["Enabled"] is not JsonValue ev || ReadBool(ev, true),
                Source = "导入·AI转谱 JSON",
                Notation = "pitch",
            }, "ok");
        }
        catch (Exception ex)
        {
            return (null, "JSON 解析失败：" + ex.Message);
        }
    }

    /// <summary>该 JSON 是不是「单曲谱」而不是曲谱库（含 Songs 数组）。</summary>
    public static bool IsSingleSongJson(string path)
    {
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8)) as JsonObject;
            return root != null && root["Songs"] == null && root["Score"] != null;
        }
        catch
        {
            return false;
        }
    }

    private static bool ReadBool(JsonNode node, bool fallback)
    {
        try
        {
            return node.GetValue<bool>();
        }
        catch
        {
            return bool.TryParse(node.ToString(), out var v) ? v : fallback;
        }
    }

    private static double ReadDouble(JsonNode? node, double fallback)
    {
        if (node == null) return fallback;
        try
        {
            return node.GetValue<double>();
        }
        catch
        {
            return double.TryParse(node.ToString(), out var v) ? v : fallback;
        }
    }

    // ------------------------------------------------------------ 导出

    public static string ExportPlainText(Song song, ParsedScore score) => ScoreParser.ToPlainText(song, score);

    public static string ExportJson(Song song, ParsedScore score)
    {
        var obj = new JsonObject
        {
            ["Name"] = song.Name,
            ["Artist"] = song.Artist,
            ["Bpm"] = song.Bpm,
            ["Meter"] = song.Meter,
            ["Score"] = song.Score,
        };
        return obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }
}


