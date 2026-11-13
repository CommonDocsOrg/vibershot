using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Vibershot.Editing.Video;

/// <summary>One subtitle: when it shows, in seconds, and what it says (lines separated by \n).</summary>
public sealed record SrtCue(double Start, double End, string Text);

/// <summary>Reads and writes SubRip (.srt) subtitle files.</summary>
public static partial class Srt
{
    [GeneratedRegex(@"(\d+):(\d{1,2}):(\d{1,2})[,.](\d{1,3})\s*-->\s*(\d+):(\d{1,2}):(\d{1,2})[,.](\d{1,3})")]
    private static partial Regex TimeLine();

    [GeneratedRegex(@"<[^>]+>|\{\\[^}]*\}")]
    private static partial Regex Markup();

    /// <summary>
    /// Parses a subtitle file. Blocks without a readable time line are skipped; formatting tags
    /// (&lt;i&gt;, {\an8}) are dropped because captions take their look from the track.
    /// </summary>
    public static List<SrtCue> Parse(string text)
    {
        var cues = new List<SrtCue>();
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').TrimStart('﻿').Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var match = TimeLine().Match(lines[i]);
            if (!match.Success) continue;
            double Seconds(int g) => int.Parse(match.Groups[g].Value, CultureInfo.InvariantCulture) * 3600 + int.Parse(match.Groups[g + 1].Value, CultureInfo.InvariantCulture) * 60
                + int.Parse(match.Groups[g + 2].Value, CultureInfo.InvariantCulture) + int.Parse(match.Groups[g + 3].Value.PadRight(3, '0'), CultureInfo.InvariantCulture) / 1000.0;
            var body = new List<string>();
            // The words run to the next blank line.
            for (i++; i < lines.Length && lines[i].Trim().Length > 0; i++) body.Add(Markup().Replace(lines[i], "").Trim());
            string words = string.Join("\n", body.Where(line => line.Length > 0));
            double start = Seconds(1), end = Seconds(5);
            if (words.Length > 0 && end > start) cues.Add(new SrtCue(start, end, words));
        }
        return cues;
    }

    /// <summary>Writes cues as a subtitle file, numbered from one, with CRLF line ends as players expect.</summary>
    public static string Write(IEnumerable<SrtCue> cues)
    {
        var text = new StringBuilder();
        int index = 1;
        foreach (var cue in cues)
        {
            text.Append(index++.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            text.Append(Stamp(cue.Start)).Append(" --> ").Append(Stamp(cue.End)).Append("\r\n");
            text.Append(cue.Text.Replace("\r\n", "\n").Replace("\n", "\r\n")).Append("\r\n\r\n");
        }
        return text.ToString();
    }

    private static string Stamp(double seconds)
    {
        long ms = (long)Math.Round(Math.Max(0, seconds) * 1000);
        return string.Create(CultureInfo.InvariantCulture, $"{ms / 3600000:00}:{ms / 60000 % 60:00}:{ms / 1000 % 60:00},{ms % 1000:000}");
    }
}
