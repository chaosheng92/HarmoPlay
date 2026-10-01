using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using HarmoPlay.Models;

namespace HarmoPlay.Core;

/// <summary>曲谱里的一个音（或休止符）。</summary>
public sealed class ScoreNote
{
    public int Index { get; set; }
    public string Raw { get; set; } = "";
    public bool IsRest { get; set; }
    /// <summary>1..8，8 = 高音 1。</summary>
    public int Degree { get; set; }
    /// <summary>规范化前缀："" / "#" / "b" / "^" / "#b" / "#^"</summary>
    public string Mods { get; set; } = "";
    /// <summary>-1 低八度、0 本音、+1 高八度。</summary>
    public int Octave { get; set; }
    public double Beats { get; set; } = 1;
    public int Bar { get; set; }
    public bool BarStart { get; set; }
    public bool Extension { get; set; }
    public double StartBeat { get; set; }

    public ScoreNote Clone() => (ScoreNote)MemberwiseClone();
}

public sealed class ParsedScore
{
    public string Title { get; set; } = "";
    public double Bpm { get; set; }
    public string Meter { get; set; } = "4/4";
    public string Format { get; set; } = "简谱";
    /// <summary>记谱法：固定音高（鼠鼠）或直接按键（可视化 / D-hydra）。</summary>
    public NotationKind Notation { get; set; } = NotationKind.Physical;
    public List<ScoreNote> Notes { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public double TotalBeats { get; set; }
    public int NoteCount => Notes.Count(n => !n.IsRest);
    public int BarCount => Notes.Count == 0 ? 0 : Notes.Max(n => n.Bar) + 1;

    public void Layout()
    {
        double t = 0;
        for (int i = 0; i < Notes.Count; i++)
        {
            Notes[i].Index = i;
            Notes[i].StartBeat = t;
            t += Notes[i].Beats;
        }
        TotalBeats = t;
    }
}

/// <summary>简谱文本解析：兼容「鼠鼠口琴谱」与「三角洲可视化/D-hydra」两类谱面。</summary>
public static class ScoreParser
{
    private static readonly Regex TokenRx = new(
        @"^(?<mods>[#b^]*)(?<deg>[0-9])(?<oct>[',]?)(?::(?<beats>[0-9]*\.?[0-9]+))?$",
        RegexOptions.Compiled);

    private static readonly Regex MetaRx = new(
        @"^\s*(?<k>TITLE|BPM|METER|BEAT|KEY|曲名|速度|拍号)\s*[=:：]\s*(?<v>.+?)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static ParsedScore Parse(string? text, string fallbackTitle = "", double fallbackBpm = 90,
        NotationKind? notation = null)
    {
        var score = new ParsedScore { Title = fallbackTitle, Bpm = fallbackBpm };
        if (string.IsNullOrWhiteSpace(text))
        {
            score.Warnings.Add("曲谱内容为空。");
            return score;
        }

        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        if (LooksLikeKeyColumnFormat(lines))
            ParseKeyColumn(lines, score);
        else
            ParsePlain(lines, score);

        score.Notation = notation ?? DetectNotation(text, lines);

        if (score.Notes.Count == 0)
        {
            score.Format = "无法识别";
            score.Warnings.Add("没有解析到任何音符，请检查简谱格式。");
            return score;
        }

        CleanupExtensions(score);
        RelabelBars(score);
        score.Layout();
        if (score.Notation == NotationKind.Pitch) score.Format += "·固定音高";
        return score;
    }

    /// <summary>
    /// 推测记谱法：带「键位/节奏」列 → 直接按键；有显式时值（1:0.5）→ 固定音高；
    /// 其余（TITLE=/BPM=、减号延长、光秃秃的数字）→ 直接按键。
    /// </summary>
    private static NotationKind DetectNotation(string text, string[] lines)
    {
        if (LooksLikeKeyColumnFormat(lines)) return NotationKind.Physical;
        if (Regex.IsMatch(text, @"[#b]?[0-7][',]?\s*:\s*[0-9]")) return NotationKind.Pitch;
        return NotationKind.Physical;
    }

    private static bool LooksLikeKeyColumnFormat(string[] lines)
    {
        bool keyCol = false, rhythmCol = false;
        foreach (var line in lines)
        {
            var t = line.TrimStart();
            if (t.StartsWith("键位")) keyCol = true;
            if (t.StartsWith("节奏")) rhythmCol = true;
        }
        return keyCol && rhythmCol;
    }

    /// <summary>普通简谱：1 2 3 / #6 / b3 / ^1 / 1:0.5 / 5 - - / 0 / |</summary>
    private static void ParsePlain(string[] lines, ParsedScore score)
    {
        int bar = 0;
        bool anyExplicit = false;
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith("//") || line.StartsWith("#!") || line.StartsWith(";")) continue;

            var meta = MetaRx.Match(line);
            if (meta.Success)
            {
                var key = meta.Groups["k"].Value.ToUpperInvariant();
                var val = meta.Groups["v"].Value.Trim();
                switch (key)
                {
                    case "TITLE":
                    case "曲名":
                        score.Title = val;
                        break;
                    case "BPM":
                    case "BEAT":
                    case "速度":
                        if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var bpm) && bpm > 10)
                            score.Bpm = bpm;
                        break;
                    case "METER":
                    case "拍号":
                        score.Meter = val;
                        break;
                }
                continue;
            }

            if (line.StartsWith("小节") || line.StartsWith("简谱") || line.StartsWith("键位") || line.StartsWith("节奏")
                || line.Contains("键位标记") || line.Contains("口琴谱"))
                continue;

            bool newPhrase = true;
            foreach (var piece in line.Split('|'))
            {
                // 注意：只按空白和中文逗号切分，ASCII 逗号是低八度记号（如 7,）
                var tokens = piece.Split(new[] { ' ', '\t', '　', '，' }, StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length == 0) continue;

                foreach (var token in tokens)
                {
                    if (token == "-" || token == "－" || token == "—")
                    {
                        var last = score.Notes.LastOrDefault();
                        if (last != null)
                        {
                            last.Beats += 1;
                            last.Raw += " -";
                        }
                        continue;
                    }

                    var m = TokenRx.Match(token);
                    if (!m.Success)
                    {
                        if (score.Warnings.Count < 20)
                            score.Warnings.Add($"无法识别的记号「{token}」已忽略。");
                        continue;
                    }

                    var deg = int.Parse(m.Groups["deg"].Value);
                    var octMark = m.Groups["oct"].Value;
                    var note = new ScoreNote
                    {
                        Raw = token,
                        Degree = deg,
                        Mods = KeyMap.Normalize(m.Groups["mods"].Value),
                        Octave = octMark == "," ? -1 : octMark.Length > 0 ? 1 : 0,
                        Beats = m.Groups["beats"].Success
                            ? double.Parse(m.Groups["beats"].Value, CultureInfo.InvariantCulture)
                            : 1,
                        IsRest = deg == 0,
                        Bar = bar,
                        BarStart = newPhrase,
                    };
                    if (note.Beats <= 0) note.Beats = 1;
                    if (m.Groups["beats"].Success) anyExplicit = true;
                    score.Notes.Add(note);
                    newPhrase = false;
                }
                bar++;
            }
        }

        score.Format = anyExplicit ? "简谱（带时值）" : "简谱（等拍）";
    }

    /// <summary>D-hydra 键位/节奏双列格式。</summary>
    private static void ParseKeyColumn(string[] lines, ParsedScore score)
    {
        int bar = -1;
        List<string>? pendingKeys = null;
        List<double>? pendingBeats = null;
        var keyToDegree = new Dictionary<char, int>
        {
            ['z'] = 1, ['x'] = 2, ['c'] = 3, ['v'] = 4, ['b'] = 5, ['n'] = 6, ['m'] = 7, [','] = 8,
        };

        void Flush()
        {
            if (pendingKeys == null || pendingBeats == null) { pendingKeys = null; pendingBeats = null; return; }
            int n = Math.Min(pendingKeys.Count, pendingBeats.Count);
            for (int i = 0; i < n; i++)
            {
                var token = pendingKeys[i];
                if (string.IsNullOrEmpty(token)) continue;
                var mods = "";
                var keyChar = char.ToLowerInvariant(token[0]);
                if (token.Length > 1)
                {
                    var suffix = token.Substring(1);
                    bool flat = suffix.Contains('-') || suffix.Contains('b');
                    bool up = suffix.Contains('+') || suffix.Contains('^');
                    bool sharp = suffix.Contains('#');
                    mods = (sharp ? "#" : "") + (up ? "^" : flat ? "b" : "");
                    if (sharp && flat) mods = "#b";
                    if (sharp && up) mods = "#^";
                }

                if (token == "0" || token == "-")
                {
                    score.Notes.Add(new ScoreNote { Raw = token, IsRest = true, Degree = 0, Beats = pendingBeats[i], Bar = bar, BarStart = i == 0 });
                    continue;
                }

                if (!keyToDegree.TryGetValue(keyChar, out var deg))
                {
                    if (score.Warnings.Count < 20) score.Warnings.Add($"键位列出现未知按键「{token}」，已忽略。");
                    continue;
                }
                score.Notes.Add(new ScoreNote
                {
                    Raw = token,
                    Degree = deg,
                    Mods = KeyMap.Normalize(mods),
                    Octave = 0,
                    Beats = pendingBeats[i] <= 0 ? 1 : pendingBeats[i],
                    Bar = Math.Max(bar, 0),
                    BarStart = i == 0,
                });
            }
            pendingKeys = null;
            pendingBeats = null;
        }

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith("小节"))
            {
                Flush();
                bar++;
                continue;
            }
            if (line.StartsWith("键位"))
            {
                var body = line.Substring(2).Trim();
                if (body.Contains("标记")) continue;
                pendingKeys = body.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).ToList();
                continue;
            }
            if (line.StartsWith("节奏"))
            {
                var body = line.Substring(2).Trim();
                pendingBeats = new List<double>();
                foreach (var tk in body.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (double.TryParse(tk, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0)
                        pendingBeats.Add(4.0 / v);   // 8 = 八分音符 → 0.5 拍
                    else
                        pendingBeats.Add(1);
                }
                Flush();
                continue;
            }

            var meta = MetaRx.Match(line);
            if (meta.Success)
            {
                var k = meta.Groups["k"].Value.ToUpperInvariant();
                var v = meta.Groups["v"].Value.Trim();
                if (k is "TITLE" or "曲名") score.Title = v;
                if (k is "BPM" or "BEAT" or "速度")
                    if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var bpm)) score.Bpm = bpm;
                continue;
            }
        }
        Flush();
        score.Format = "D-hydra 键位谱";
    }

    private static void CleanupExtensions(ParsedScore score)
    {
        for (int i = score.Notes.Count - 1; i >= 0; i--)
        {
            var n = score.Notes[i];
            if (n.Beats <= 0 || double.IsNaN(n.Beats) || double.IsInfinity(n.Beats))
                n.Beats = 1;
        }
    }

    private static void RelabelBars(ParsedScore score)
    {
        int bar = 0;
        bool start = true;
        foreach (var n in score.Notes)
        {
            if (n.BarStart && !start) bar++;
            n.Bar = bar;
            n.BarStart = start;
            start = false;
        }
    }

    // ---------------------------------------------------------------- 渲染

    /// <summary>把音高换算成一个按键名（用于预览/导出）。</summary>
    public static string KeyLetter(ScoreNote n, KeyMap map)
    {
        if (n.IsRest) return "0";
        int idx = Math.Clamp(n.Degree, 1, 8) - 1;
        var name = map.Keys.Count > idx ? map.Keys[idx] : "?";
        return PrettyKey(name);
    }

    public static string PrettyKey(string keyName) => keyName switch
    {
        "OemComma" => ",",
        "OemPeriod" => ".",
        "OemMinus" => "-",
        "OemPlus" => "=",
        "OemQuestion" => "/",
        "OemSemicolon" => ";",
        "Space" => "空格",
        "Tab" => "Tab",
        "Escape" => "Esc",
        "Return" or "Enter" => "Enter",
        "Back" => "退格",
        "Up" => "↑",
        "Down" => "↓",
        "Left" => "←",
        "Right" => "→",
        "D0" => "0",
        "D1" => "1", "D2" => "2", "D3" => "3", "D4" => "4",
        "D5" => "5", "D6" => "6", "D7" => "7", "D8" => "8", "D9" => "9",
        _ => keyName.ToUpperInvariant(),
    };

    /// <summary>修饰后缀（与 D-hydra 键位列一致：# 半音 / - 降调 / + 升调）。</summary>
    public static string ModSuffix(string mods) => mods switch
    {
        "#" => "#",
        "b" => "-",
        "^" => "+",
        "#b" => "#-",
        "#^" => "#+",
        _ => "",
    };

    public static string NoteLabel(ScoreNote n) => n.IsRest
        ? "0"
        : KeyMap.Normalize(n.Mods) + n.Degree + (n.Octave > 0 ? "^" : n.Octave < 0 ? "_" : "");

    /// <summary>曲谱预览：按小节切成多行文本。</summary>
    public static List<string> PreviewLines(ParsedScore score, KeyMap map)
    {
        var result = new List<string>();
        if (score.Notes.Count == 0) return result;

        foreach (var group in score.Notes.GroupBy(n => n.Bar).OrderBy(g => g.Key))
        {
            var notes = group.ToList();
            var sbSol = new StringBuilder();
            var sbKey = new StringBuilder();
            var sbDur = new StringBuilder();
            foreach (var n in notes)
            {
                var label = n.IsRest ? "0" : KeyMap.Normalize(n.Mods) + n.Degree + (n.Octave > 0 ? "'" : n.Octave < 0 ? "," : "");
                var chord = Chord.Resolve(n, map, score.Notation);
                sbSol.Append(label.PadRight(6));
                sbKey.Append((chord.Text + (chord.Midi >= 0 ? "(" + chord.PitchName + ")" : "")).PadRight(10));
                sbDur.Append((n.Beats.ToString("0.##", CultureInfo.InvariantCulture) + "拍").PadRight(6));
            }
            result.Add($"[{group.Key + 1:00}]  简谱 {sbSol.ToString().TrimEnd()}");
            result.Add($"      键位 {sbKey.ToString().TrimEnd()}");
            result.Add($"      时值 {sbDur.ToString().TrimEnd()}");
            result.Add("");
        }
        return result;
    }

    /// <summary>导出为可视化曲谱文本（可直接给口琴可视化工具用）。</summary>
    public static string ToPlainText(Song song, ParsedScore score)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(song.Name)) sb.AppendLine("TITLE=" + song.Name);
        sb.AppendLine("BPM=" + (score.Bpm > 0 ? score.Bpm.ToString("0.##", CultureInfo.InvariantCulture) : song.Bpm.ToString("0.##", CultureInfo.InvariantCulture)));
        sb.AppendLine();

        int bar = -1;
        foreach (var n in score.Notes)
        {
            if (n.Bar != bar)
            {
                if (bar >= 0) sb.AppendLine();
                bar = n.Bar;
            }
            if (n.IsRest)
            {
                sb.Append("0");
                if (n.Beats > 1) sb.Append(new string(' ', 0) + string.Concat(Enumerable.Repeat(" -", (int)Math.Round(n.Beats) - 1)));
                sb.Append(' ');
                continue;
            }
            sb.Append(KeyMap.Normalize(n.Mods) + n.Degree);
            if (n.Octave > 0) sb.Append('\'');
            else if (n.Octave < 0) sb.Append(',');
            if (Math.Abs(n.Beats - 1) > 0.001)
                sb.Append(':').Append(n.Beats.ToString("0.###", CultureInfo.InvariantCulture));
            sb.Append(' ');
        }
        return sb.ToString().TrimEnd() + Environment.NewLine;
    }
}
