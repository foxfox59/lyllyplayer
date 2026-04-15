using System.IO;

namespace LyllyPlayer.Utils;

public enum ToolPathSource
{
    /// <summary>Saved path in settings points to an existing file.</summary>
    Explicit,
    /// <summary>No valid saved path; resolved via PATH search.</summary>
    Path,
    /// <summary>Not found.</summary>
    Missing
}

/// <summary>Resolves yt-dlp, ffmpeg, node.exe for Options UI and <see cref="Player.YtDlpClient"/>.</summary>
public static class ToolPathResolver
{
    public sealed record Resolution(
        ToolPathSource Source,
        /// <summary>Full path to exe when known; otherwise command name for display.</summary>
        string DisplayText,
        /// <summary>Path passed to ProcessStartInfo.FileName when possible; else command name.</summary>
        string EffectiveFileName,
        bool IsFound);

    /// <param name="savedPath">User setting (may be empty, or a bare command name).</param>
    /// <param name="pathSearchBase">e.g. "yt-dlp", "ffmpeg", "node"</param>
    public static Resolution Resolve(string? savedPath, string pathSearchBase)
    {
        var search = string.IsNullOrWhiteSpace(pathSearchBase) ? "yt-dlp" : pathSearchBase.Trim();
        var raw = (savedPath ?? "").Trim();

        if (!string.IsNullOrEmpty(raw))
        {
            if (LooksLikePath(raw))
            {
                try
                {
                    var full = Path.GetFullPath(raw);
                    if (File.Exists(full))
                        return new Resolution(ToolPathSource.Explicit, full, full, true);
                }
                catch { /* ignore */ }
            }
            else
            {
                // Bare command stored (e.g. "yt-dlp") — treat like PATH.
                if (TryFindOnPath(raw, out var found))
                    return new Resolution(ToolPathSource.Path, found, found, true);
                return new Resolution(ToolPathSource.Missing, raw, raw, false);
            }

            // Saved path looked like a path but file missing — fall through to PATH.
        }

        if (TryFindOnPath(search, out var pathFound))
            return new Resolution(ToolPathSource.Path, pathFound, pathFound, true);

        var fallback = search.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? search : search + ".exe";
        return new Resolution(ToolPathSource.Missing, search, fallback, false);
    }

    private static bool LooksLikePath(string s)
        => s.Contains('\\') || s.Contains('/') || s.Contains(':');

    /// <summary>Find <paramref name="exeName"/> on PATH (no slashes in name).</summary>
    public static bool TryFindOnPath(string? exeName, out string fullPath)
    {
        fullPath = "";
        var name = string.IsNullOrWhiteSpace(exeName) ? "yt-dlp" : exeName.Trim();
        if (name.Contains('\\') || name.Contains('/'))
            return false;

        var candidates = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? new[] { name }
            : new[] { name, name + ".exe" };

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                foreach (var c in candidates)
                {
                    var p = Path.Combine(dir, c);
                    if (File.Exists(p))
                    {
                        fullPath = p;
                        return true;
                    }
                }
            }
            catch
            {
                // ignore broken PATH entries
            }
        }

        return false;
    }
}
