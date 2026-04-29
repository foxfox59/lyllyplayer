using System.Text.RegularExpressions;

namespace LyllyPlayer.Utils;

/// <summary>
/// Parses standard LRC lyrics files into timed line records.
/// LRC format: [mm:ss.xx]Lyric text
/// Handles optional metadata tags (e.g. [ti:Title], [ar:Artist]) which are ignored.
/// </summary>
public static class LrcParser
{
    /// <summary>A single lyric line with its start time in seconds.</summary>
    public sealed record TimedLine(double Seconds, string Text);

    /// <summary>Parses LRC-formatted lyrics text into timed lines, sorted by time.</summary>
    /// <param name="lrcText">The raw LRC text content.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A sorted list of timed lines. Returns empty if parsing fails or input is null/empty.</returns>
    public static IReadOnlyList<TimedLine> Parse(string? lrcText, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(lrcText))
            return Array.Empty<TimedLine>();

        cancellationToken.ThrowIfCancellationRequested();

        var lines = new List<TimedLine>(32);

        foreach (var rawLine in lrcText.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var timedLine = TryParseLine(line);
            if (timedLine is not null)
                lines.Add(timedLine);
        }

        // Sort by timestamp (ascending); lines with the same timestamp preserve their original order.
        if (lines.Count > 1)
            lines.Sort((a, b) => a.Seconds.CompareTo(b.Seconds));

        // Add an empty vanity line unless the first lyric starts at 0:00
        if (lines.Count > 1 && lines[0].Seconds > 0)
        {

            lines.Insert(0, new TimedLine(0, " "));
        }
        return lines;
    }

    /// <summary>
    /// Gets the current lyric line text at the given playback position.
    /// Returns the text of the line whose timestamp is the largest value <= positionSeconds.
    /// Returns null if no lyrics are loaded or position is before the first line.
    /// </summary>
    public static string? GetCurrentLine(IReadOnlyList<TimedLine> lines, double positionSeconds)
    {
        if (lines.Count == 0 || positionSeconds < 0)
            return null;

        // Binary search for the last line where line.Seconds <= positionSeconds.
        int lo = 0;
        int hi = lines.Count - 1;
        int result = -1;

        while (lo <= hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (lines[mid].Seconds <= positionSeconds)
            {
                result = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return result >= 0 ? lines[result].Text : null;
    }

    /// <summary>
    /// Gets both the current and next lyric lines at the given playback position.
    /// Useful for displaying current + preview of upcoming line.
    /// </summary>
    public static (string Current, string? Next)? GetCurrentAndNextLine(IReadOnlyList<TimedLine> lines, double positionSeconds)
    {
        if (lines.Count == 0 || positionSeconds < 0)
            return null;

        int result = -1;
        int lo = 0;
        int hi = lines.Count - 1;

        while (lo <= hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (lines[mid].Seconds <= positionSeconds)
            {
                result = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        if (result < 0)
            return null;

        var current = lines[result].Text;
        var next = (result + 1 < lines.Count) ? lines[result + 1].Text : null;

        return (current, next);
    }

    /// <summary>
    /// Tries to extract metadata tags (TI: title, AR: artist) from LRC text.
    /// Returns a tuple of (Artist, Title) — whichever is found. Returns null if neither tag exists.
    /// Tag names are case-insensitive.
    /// </summary>
    public static (string? Artist, string? Title)? TryExtractMetadata(string lrcText)
    {
        string? artist = null;
        string? title = null;

        foreach (var rawLine in lrcText.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();

            // Match [ar:Artist] or [AR:Artist]
            var arMatch = Regex.Match(line, @"^\[ar:(.+)\]$", RegexOptions.IgnoreCase);
            if (arMatch.Success)
            {
                artist = arMatch.Groups[1].Value.Trim();
                continue;
            }

            // Match [ti:Title] or [TI:Title]
            var tiMatch = Regex.Match(line, @"^\[tr:(.+)\]$", RegexOptions.IgnoreCase);
            if (tiMatch.Success)
            {
                title = tiMatch.Groups[1].Value.Trim();
                continue;
            }
        }

        if (!string.IsNullOrEmpty(artist) || !string.IsNullOrEmpty(title))
            return (artist, title);
        return null;
    }

    /// <summary>
    /// Tries to parse a single LRC line into a TimedLine.
    /// Returns null if the line doesn't contain a valid timestamp.
    /// Handles: [mm:ss.xx], [mm:ss], and lines with multiple timestamps.
    /// </summary>
    private static TimedLine? TryParseLine(string line)
    {
        // Match the first timestamp in the line.
        // LRC format: [mm:ss.xx] or [mm:ss]
        // Some files have multiple timestamps per line: [00:12.50][00:12.60]text
        var match = Regex.Match(line, @"\[(\d{1,2}):(\d{2})(?:\.(\d{1,3}))?\]");
        if (!match.Success)
            return null;

        var minutes = int.Parse(match.Groups[1].Value);
        var seconds = int.Parse(match.Groups[2].Value);
        var millis = 0;

        if (match.Groups[3].Success)
        {
            var msStr = match.Groups[3].Value.PadRight(3, '0').Substring(0, 3);
            millis = int.Parse(msStr);
        }

        var timeSeconds = minutes * 60.0 + seconds + millis / 1000.0;

        // Extract text: everything after the last timestamp bracket.
        var textStart = line.LastIndexOf(']');
        if (textStart < 0)
            return null;

        textStart++;
        if (textStart >= line.Length)
            return null;

        var text = line.Substring(textStart).Trim();
        if (string.IsNullOrWhiteSpace(text))
            return null;

        return new TimedLine(timeSeconds, text);
    }
}
