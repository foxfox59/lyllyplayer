using System.Text;
using LyllyPlayer.Models;

namespace LyllyPlayer.Utils;

public static class PlaylistDeduper
{
    private static readonly string[] NoiseTokens =
    {
        "official video",
        "official music video",
        "lyrics",
        "lyric video",
        "audio",
        "visualizer",
    };

    public static IReadOnlyList<PlaylistEntry> DedupeByVideoIdOnly(IEnumerable<PlaylistEntry> entries)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<PlaylistEntry>();
        foreach (var e in entries ?? Array.Empty<PlaylistEntry>())
        {
            if (e is null)
                continue;
            if (string.IsNullOrWhiteSpace(e.VideoId))
                continue;
            if (!seen.Add(e.VideoId))
                continue;
            list.Add(e);
        }
        return list;
    }

    public static IReadOnlyList<PlaylistEntry> DedupeForSearch(IEnumerable<PlaylistEntry> entries)
    {
        // Stage A: hard de-dupe by VideoId.
        var stageA = DedupeByVideoIdOnly(entries);

        // Stage B: soft de-dupe by normalized title key.
        var seenTitle = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<PlaylistEntry>(stageA.Count);
        foreach (var e in stageA)
        {
            var key = BuildNormalizedTitleKey(e.Title);
            if (string.IsNullOrWhiteSpace(key))
            {
                list.Add(e);
                continue;
            }
            if (!seenTitle.Add(key))
                continue;
            list.Add(e);
        }
        return list;
    }

    public static string BuildNormalizedTitleKey(string? title)
    {
        var s = (title ?? "").Trim();
        if (string.IsNullOrWhiteSpace(s))
            return "";

        s = s.ToLowerInvariant();

        // Remove trailing bracket/paren suffixes like "(official video)" or "[lyrics]".
        s = TrimBracketParenSuffixes(s);

        // Strip a small safe list of common noise tokens.
        foreach (var tok in NoiseTokens)
            s = s.Replace(tok, "", StringComparison.Ordinal);

        // Collapse to alnum + spaces only.
        var sb = new StringBuilder(s.Length);
        var prevSpace = false;
        foreach (var ch in s)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
                prevSpace = false;
            }
            else
            {
                if (!prevSpace)
                {
                    sb.Append(' ');
                    prevSpace = true;
                }
            }
        }

        return sb
            .ToString()
            .Trim();
    }

    private static string TrimBracketParenSuffixes(string s)
    {
        // Best-effort: repeatedly remove a trailing (...) or [...] group.
        // We intentionally keep this simple and conservative.
        while (true)
        {
            s = s.TrimEnd();
            if (s.EndsWith(')'))
            {
                var open = s.LastIndexOf('(');
                if (open >= 0 && open < s.Length - 1)
                {
                    s = s.Substring(0, open);
                    continue;
                }
            }
            if (s.EndsWith(']'))
            {
                var open = s.LastIndexOf('[');
                if (open >= 0 && open < s.Length - 1)
                {
                    s = s.Substring(0, open);
                    continue;
                }
            }
            break;
        }
        return s.TrimEnd();
    }
}

