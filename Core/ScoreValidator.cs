using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using HarmoPlay.Models;

namespace HarmoPlay.Core;

public sealed class ValidationReport
{
    public bool Ok { get; set; } = true;
    public List<string> Errors { get; } = new();
    public List<string> Warnings { get; } = new();
    public List<string> Infos { get; } = new();
    public string FileName { get; set; } = "";

    public void Error(string message)
    {
        Ok = false;
        Errors.Add(message);
    }

    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.AppendLine("曲谱校验报告（转谱规范 第十一节）");
        sb.AppendLine("文件：" + FileName);
        sb.AppendLine("结果：" + (Ok ? "✅ 通过" : "❌ 未通过"));
        sb.AppendLine();

        foreach (var info in Infos) sb.AppendLine("  · " + info);
        if (Infos.Count > 0) sb.AppendLine();

        if (Warnings.Count > 0)
        {
            sb.AppendLine($"⚠ 警告 {Warnings.Count} 条：");
            foreach (var warning in Warnings) sb.AppendLine("  - " + warning);
            sb.AppendLine();
        }

        if (Errors.Count > 0)
        {
            sb.AppendLine($"✖ 错误 {Errors.Count} 条：");
            foreach (var error in Errors) sb.AppendLine("  - " + error);
            sb.AppendLine();
        }

        if (Ok) sb.AppendLine("可以导入：主界面「导入曲谱文件…」选择该 .json，或在编辑页粘贴 Score 文本。");
        return sb.ToString();
    }
}

/// <summary>按转谱规范校验 JSON 曲谱（字段、语法、时值、音域、小节拍数）。</summary>
public static class ScoreValidator
{
    private static readonly Regex TokenRx = new(
        @"^(?<mods>[#b]?)(?<deg>[0-7])(?<oct>[',]?)(?::(?<beats>[0-9]+(?:\.[0-9]+)?))?$",
        RegexOptions.Compiled);

    private static readonly Regex MeterRx = new(@"^\s*(\d+)\s*/\s*(\d+)\s*$", RegexOptions.Compiled);

    private static readonly HashSet<string> AllowedFields = new(StringComparer.Ordinal)
    {
        "Name", "Score", "Bpm", "Meter", "Enabled",
    };

    public static ValidationReport ValidateJsonFile(string path)
    {
        var report = new ValidationReport { FileName = Path.GetFileName(path) };
        string text;
        try
        {
            text = File.ReadAllText(path, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            report.Error("无法读取文件：" + ex.Message);
            return report;
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(text);
        }
        catch (Exception ex)
        {
            report.Error("JSON 解析失败：" + ex.Message);
            return report;
        }

        if (node is not JsonObject obj)
        {
            report.Error("JSON 根节点必须是对象。");
            return report;
        }

        foreach (var key in obj.Select(kv => kv.Key))
            if (!AllowedFields.Contains(key))
                report.Warnings.Add($"出现了规范之外的字段「{key}」（规范只允许 Name/Score/Bpm/Meter/Enabled）。");

        string name = "";
        if (obj["Name"] is JsonValue nv)
        {
            name = nv.ToString();
            if (string.IsNullOrWhiteSpace(name)) report.Warnings.Add("Name 为空。");
        }
        else report.Error("缺少字符串字段 Name。");
        report.FileName = string.IsNullOrWhiteSpace(name) ? Path.GetFileName(path) : name + ".json";

        if (obj["Score"] is not JsonValue sv)
        {
            report.Error("缺少字符串字段 Score。");
            return report;
        }
        var score = sv.ToString();

        double bpm = 120;
        if (obj["Bpm"] is JsonValue bv)
        {
            if (!double.TryParse(bv.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out bpm))
                report.Error($"Bpm 不是数字：{bv}");
            else if (bpm is < 20 or > 400)
                report.Error($"Bpm={bpm} 超出 20~400。");
        }
        else report.Error("缺少数字字段 Bpm。");

        var meter = obj["Meter"] is JsonValue mv ? mv.ToString() : "4/4";
        if (obj["Meter"] is null) report.Warnings.Add("缺少 Meter，按 4/4 处理。");

        if (obj["Enabled"] is JsonValue ev)
        {
            var raw = ev.ToString();
            if (!bool.TryParse(raw, out _)) report.Error($"Enabled 必须是布尔值 true/false，当前是「{raw}」。");
        }
        else report.Warnings.Add("缺少 Enabled，按 true 处理。");

        ValidateScoreText(report, score, bpm, meter);
        return report;
    }

    /// <summary>校验 Score 文本本体。</summary>
    public static void ValidateScoreText(ValidationReport report, string score, double bpm, string meter)
    {
        if (string.IsNullOrWhiteSpace(score))
        {
            report.Error("Score 为空。");
            return;
        }

        // 只允许 ASCII 半角
        foreach (var ch in score)
        {
            bool asciiOk = ch is >= ' ' and <= '~';
            if (!asciiOk && ch is not ('\n' or '\r' or '\t'))
            {
                report.Error($"Score 含非 ASCII 字符「{ch}」（禁止中文标点、♯♭、上下圆点等）。");
                break;
            }
            if (ch is '，' or '；' or '：' or '、')
                report.Error("Score 含中文标点。");
        }

        var tokens = Regex.Split(score.Trim(), @"[\s]+").Where(t => t.Length > 0).ToList();

        var bars = new List<double> { 0 };
        var barNoteCount = new List<int> { 0 };
        int noteCount = 0, restCount = 0, unplayable = 0, comboBlocked = 0, needsCombo = 0;
        double totalBeats = 0;
        int minMidi = int.MaxValue, maxMidi = int.MinValue;
        var usesMiddle = false;
        var usesOctave = false;
        var problems = new List<string>();
        var blockedSamples = new List<string>();
        var comboSamples = new List<string>();
        var deltaMap = KeyMap.CreateDelta();   // 按三角洲口琴 8 键的实际键位判断

        foreach (var token in tokens)
        {
            if (token == "|")
            {
                bars.Add(0);
                barNoteCount.Add(0);
                continue;
            }

            var m = TokenRx.Match(token);
            if (!m.Success)
            {
                problems.Add($"不符合语法的记号「{token}」");
                if (problems.Count >= 12) break;
                continue;
            }

            var deg = int.Parse(m.Groups["deg"].Value);
            var mods = m.Groups["mods"].Value;
            var octMark = m.Groups["oct"].Value;
            int octave = octMark == "," ? -1 : octMark.Length > 0 ? 1 : 0;
            double beats = m.Groups["beats"].Success
                ? double.Parse(m.Groups["beats"].Value, CultureInfo.InvariantCulture)
                : 1;

            if (deg == 0 && (mods.Length > 0 || octMark.Length > 0))
                problems.Add($"休止符不能带修饰：「{token}」");

            if (beats < 0.03125 || beats > 64)
                problems.Add($"「{token}」时值 {beats} 超出 0.03125~64");

            bars[^1] += beats;
            totalBeats += beats;
            barNoteCount[^1]++;

            if (deg == 0) { restCount++; continue; }
            noteCount++;

            if (PitchFingering.TryToMidi(deg, mods, octave, out var midi, out var error))
            {
                if (!PitchFingering.TryFingering(midi, deltaMap, out _, out var mouse, out var fingeringError))
                {
                    comboBlocked++;
                    if (blockedSamples.Count < 8)
                        blockedSamples.Add($"「{token}」={PitchFingering.NoteName(midi)}");
                    continue;
                }

                minMidi = Math.Min(minMidi, midi);
                maxMidi = Math.Max(maxMidi, midi);
                if (mouse.HasFlag(MouseMod.Middle)) usesMiddle = true;
                if (mouse.HasFlag(MouseMod.Left) || mouse.HasFlag(MouseMod.Right)) usesOctave = true;
                if (PitchFingering.IsMouseCombo(mouse))
                {
                    needsCombo++;
                    if (comboSamples.Count < 8)
                        comboSamples.Add($"「{token}」={PitchFingering.NoteName(midi)}");
                }
            }
            else
            {
                unplayable++;
                if (unplayable <= 5) problems.Add($"「{token}」{error}");
            }
        }

        if (problems.Count > 0)
        {
            report.Error($"Score 有 {problems.Count} 处问题：" + string.Join("；", problems));
        }

        // 小节拍数
        double beatsPerBar = 4;
        var mm = MeterRx.Match(meter ?? "4/4");
        if (mm.Success)
        {
            double n = double.Parse(mm.Groups[1].Value, CultureInfo.InvariantCulture);
            double d = double.Parse(mm.Groups[2].Value, CultureInfo.InvariantCulture);
            beatsPerBar = n * 4.0 / d;
        }
        else
        {
            report.Warnings.Add($"Meter「{meter}」不是 n/d 形式，按 4/4 校验。");
        }

        var badBars = new List<string>();
        for (int i = 0; i < bars.Count; i++)
        {
            bool isLast = i == bars.Count - 1;
            bool empty = barNoteCount[i] == 0;
            if (empty && isLast) continue;
            if (Math.Abs(bars[i] - beatsPerBar) > 0.01)
                badBars.Add($"第 {i + 1} 小节 {bars[i]:0.###} 拍（应为 {beatsPerBar:0.###}）");
        }
        if (badBars.Count > 0)
            report.Warnings.Add("小节拍数不齐（弱起 / 结尾小节属正常，其余需检查）：" + string.Join("、", badBars.Take(8)));

        double seconds = bpm > 0 ? totalBeats * 60.0 / bpm : 0;
        report.Infos.Add($"音符 {noteCount} 个，休止 {restCount} 个，共 {noteCount + restCount} 个记号，{bars.Count - (barNoteCount[^1] == 0 ? 1 : 0)} 小节");
        report.Infos.Add($"总拍数 {totalBeats:0.###} 拍 · BPM {bpm:0.#} · 预计时长 {seconds:0.0} 秒（{TimeSpan.FromSeconds(seconds):mm\\:ss}）");
        if (minMidi != int.MaxValue)
            report.Infos.Add($"音域 {PitchFingering.NoteName(minMidi)} ~ {PitchFingering.NoteName(maxMidi)}（MIDI {minMidi}~{maxMidi}，允许 48~84）");
        report.Infos.Add($"指法：{(usesMiddle ? "需要中键" : "不需要中键")}、{(usesOctave ? "需要左/右键八度" : "不需要八度键")}");
        if (comboBlocked > 0)
        {
            report.Warnings.Add(
                $"有 {comboBlocked} 个音需要「中键 + 左/右键」同时按，而当前键位设置不允许组合键，游戏里弹不出这些音：" +
                string.Join("、", blockedSamples) + (comboBlocked > blockedSamples.Count ? " …" : ""));
            report.Warnings.Add("处理办法：把这段旋律整体移调，或在「键位映射」里勾上「允许中键+左键/右键同时按」。");
        }

        if (needsCombo > 0)
        {
            report.Warnings.Add(
                $"有 {needsCombo} 个音必须同时按住鼠标组合键（技术上传得出来，但人手很难做到，" +
                "自动弹奏没问题、自己跟谱弹奏基本按不了）：" +
                string.Join("、", comboSamples) + (needsCombo > comboSamples.Count ? " …" : ""));
            report.Warnings.Add("建议：把这段旋律移调到中音区（C4~B4 的黑键只需按住中键，不需要组合），或改用相邻自然音。");
        }

        var playableCount = PitchFingering.PlayablePitches(KeyMap.CreateDelta()).Count;
        report.Infos.Add(comboBlocked > 0
            ? $"键位可演奏性：C3~C6 共 37 个半音中可演奏 {playableCount} 个（其余需要组合键，而组合键已被关闭）。"
            : $"键位可演奏性：C3~C6 共 37 个半音全部可演奏" + (needsCombo > 0 ? $"（其中 {needsCombo} 个需要鼠标组合键）" : "（都不需要组合键）") + "。");

        if (unplayable > 0) report.Error($"有 {unplayable} 个音超出 C3~C6，必须整体移调后再交付。");
    }
}

