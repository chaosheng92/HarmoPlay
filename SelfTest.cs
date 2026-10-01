using System.IO;
using System.Text;
using HarmoPlay.Core;
using HarmoPlay.Models;

namespace HarmoPlay;

/// <summary>无界面自检：验证曲谱库、解析器与键位映射是否正常（--selftest）。</summary>
public static class SelfTest
{
    public static string Run()
    {
        var sb = new StringBuilder();
        void Log(string s) => sb.AppendLine(s);

        try
        {
            Log("== HarmoPlay 自检 ==");
            Log("数据目录：" + LibraryStore.DataDir);

            var lib = LibraryStore.Load();
            Log($"曲谱库：{lib.Songs.Count} 首，分类 {lib.Folders.Count} 个");
            Log($"键位预设：{lib.Settings.KeyMapId} -> {lib.EffectiveKeyMap.Name}");

            var map = lib.EffectiveKeyMap;
            Log("");
            Log("-- 键位映射表 --");
            for (int i = 0; i < map.Keys.Count; i++)
                Log($"  简谱 {i + 1} -> {ScoreParser.PrettyKey(map.Keys[i])}");
            foreach (var m in map.Mods)
                Log($"  前缀「{(string.IsNullOrEmpty(m.Prefix) ? "无" : m.Prefix)}」 {m.Label}  鼠标={m.Mouse} 颜色={m.Color}");

            Log("");
            Log("-- 解析测试 --");
            var samples = new (string title, string text)[]
            {
                ("鼠鼠格式", "5,:0.5 5,:0.5 | 6,:1 5,:1 1:1 | 7,:2 #6:1 b3:0.5"),
                ("可视化格式", "TITLE=小星星\nBPM=90\n1 1 5 5 6 6 5 - | 4 4 3 3 2 2 1 -"),
                ("D-hydra 格式", "BPM 135\n小节 12 (3/4)\n  简谱  #6. 2\n  键位  N-# X\n  节奏  8   4"),
            };
            foreach (var (title, text) in samples)
            {
                var p = ScoreParser.Parse(text, title, 90);
                Log($"  [{title}] 格式={p.Format} 记谱法={p.Notation.ToText()} 音符={p.NoteCount} 小节={p.BarCount} BPM={p.Bpm:0.#} 总拍={p.TotalBeats:0.##} 警告={p.Warnings.Count}");
                foreach (var n in p.Notes.Take(6))
                {
                    var c = Chord.Resolve(n, map, p.Notation);
                    Log($"      {n.Raw,-8} -> 键 {c.Text,-4} 组合 {c.Detail}");
                }
            }

            Log("");
            Log("-- 固定音高指法表（C3~C6） --");
            var fingering = new List<string>();
            int missing = 0;
            for (int midi = PitchFingering.MinMidi; midi <= PitchFingering.MaxMidi; midi++)
            {
                var (_, mouse, _) = PitchFingering.FingeringFor(midi, map);
                if (!fingering.Contains(PitchFingering.NoteName(midi))) fingering.Add(PitchFingering.NoteName(midi));
                var probe = new ScoreNote { Degree = 1, Mods = "", Octave = 0, IsRest = false };
                if (!PitchFingering.IsPlayable(midi)) missing++;
                _ = mouse;
            }
            Log($"  可演奏音域 {PitchFingering.NoteName(PitchFingering.MinMidi)} ~ {PitchFingering.NoteName(PitchFingering.MaxMidi)}，共 {fingering.Count} 个半音，越界 {missing} 个");
            foreach (var midi in new[] { 48, 49, 61, 72, 73, 84 })
            {
                var (_, mouse, key) = PitchFingering.FingeringFor(midi, map);
                Log($"      MIDI {midi} = {PitchFingering.NoteName(midi),-3} -> 键 {ScoreParser.PrettyKey(key),-2} 鼠标={mouse}");
            }

            Log("");
            Log("-- 固定音高 vs 直接按键 对照（同一记号的差异） --");
            foreach (var token in new[] { "b7", "b2", "#1", "1,", "1'", "#7'" })
            {
                var sp = ScoreParser.Parse(token, "p", 90, NotationKind.Pitch);
                var ph = ScoreParser.Parse(token, "p", 90, NotationKind.Physical);
                var cp = Chord.Resolve(sp.Notes[0], map, NotationKind.Pitch);
                var ch = Chord.Resolve(ph.Notes[0], map, NotationKind.Physical);
                Log($"      {token,-5} 固定音高={cp.Text,-5}{cp.PitchName,-4} | 直接按键={ch.Text,-5}{ch.PitchName}");
            }

            Log("");
            Log("-- 转谱规范校验（--validate） --");
            var tempDir = Path.Combine(LibraryStore.DataDir, "selftest");
            Directory.CreateDirectory(tempDir);
            var sampleJson = Path.Combine(tempDir, "validate-sample.json");
            File.WriteAllText(sampleJson, """
            {
              "Name": "校验样例",
              "Score": "1 1 5 5 | 6 6 5:2 | 4 4 3 3 | 2 2 1:2 |",
              "Bpm": 120,
              "Meter": "4/4",
              "Enabled": true
            }
            """, Encoding.UTF8);
            var badJson = Path.Combine(tempDir, "validate-bad.json");
            File.WriteAllText(badJson, """
            { "Name": "错误样例", "Score": "1 1 5 5 | #9:0.01 | 1:200", "Bpm": 900, "Meter": "4/4", "Enabled": "true" }
            """, Encoding.UTF8);
            var goodReport = ScoreValidator.ValidateJsonFile(sampleJson);
            var badReport = ScoreValidator.ValidateJsonFile(badJson);
            Log($"  合法样例：{(goodReport.Ok ? "通过" : "未通过")}（错误 {goodReport.Errors.Count}、警告 {goodReport.Warnings.Count}）");
            foreach (var info in goodReport.Infos) Log("      " + info);
            Log($"  违规样例：{(badReport.Ok ? "竟然通过（异常）" : "正确拦下")}（错误 {badReport.Errors.Count}）");
            foreach (var error in badReport.Errors) Log("      " + error);

            Log("");
            Log("-- 更新接口（本地文件模拟） --");
            var updateJson = Path.Combine(tempDir, "update-sample.json");
            File.WriteAllText(updateJson, """
            { "version": "9.9.9", "published": "2026-10-01", "notes": "自检模拟更新", "download": "https://example.com/dl", "mandatory": false }
            """, Encoding.UTF8);
            var upd = UpdateService.CheckAsync(updateJson).GetAwaiter().GetResult();
            Log($"  当前版本 {UpdateService.CurrentVersion} → 模拟清单 9.9.9：{(upd.Available ? "检测到新版本" : "未检测到")}；{upd.Message}");

            Log("");
            Log("-- 诊断包（问题反馈用） --");
            var lib2 = LibraryStore.Load();
            var diag = Diagnostics.Build(lib2);
            Log($"  诊断包 {diag.Length} 字符，含版本/系统/键位/悬浮窗/日志，不含曲谱内容：{!diag.Contains(lib2.Songs.FirstOrDefault()?.Score ?? "\u0000")}");

            Log("");
            Log("-- 曲谱库内容 --");
            int bad = 0;
            foreach (var song in lib.Songs.Take(30))
            {
                var p = ScoreParser.Parse(song.Score, song.Name, song.Bpm);
                if (p.Notes.Count == 0) bad++;
                Log($"  {song.DisplayName,-28} {p.Format,-16} 音符 {p.NoteCount,4}  警告 {p.Warnings.Count}");
            }
            Log($"解析失败（0 音符）的曲谱：{bad} 首");

            Log("");
            Log("-- 导出测试 --");
            var first = lib.Songs.FirstOrDefault();
            if (first != null)
            {
                var p = ScoreParser.Parse(first.Score, first.Name, first.Bpm);
                var txt = LibraryStore.ExportPlainText(first, p);
                Log("  " + first.Name + " 导出前 200 字：");
                Log("  " + txt.Substring(0, Math.Min(200, txt.Length)).Replace("\n", "\n  "));
            }

            Log("");
            Log("自检完成：全部通过");
        }
        catch (Exception ex)
        {
            Log("");
            Log("!! 自检失败：" + ex.GetType().Name + " " + ex.Message);
            Log(ex.ToString());
        }

        return sb.ToString();
    }
}
