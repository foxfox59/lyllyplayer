using System.IO;

namespace LyllyPlayer.Utils;

/// <summary>
/// Distinguishes local filesystem playlist inputs from YouTube / web playlist URLs so
/// "last loaded YouTube URL" memory and Load URL defaults are not polluted by .json paths, etc.
/// </summary>
public static class PlaylistSourcePathHeuristics
{
    public static bool LooksLikeLocalFilesystemSource(string? s)
    {
        var t = (s ?? "").Trim();
        if (string.IsNullOrEmpty(t))
            return false;

        // Any absolute http(s) URL is treated as non-local for this heuristic.
        if (Uri.TryCreate(t, UriKind.Absolute, out var abs) &&
            (string.Equals(abs.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(abs.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)))
            return false;

        if (Path.IsPathRooted(t))
            return true;

        if (t.StartsWith("\\\\", StringComparison.Ordinal))
            return true;

        // Typical Windows relative paths.
        if (t.Contains('\\', StringComparison.Ordinal))
            return true;

        var ext = Path.GetExtension(t);
        if (!string.IsNullOrEmpty(ext))
        {
            var e = ext.ToLowerInvariant();
            if (e == ".json" || e == ".m3u" || e == ".m3u8" || e == ".txt" || e == ".pls" || e == ".csv")
                return true;
        }

        return false;
    }

    /// <summary>
    /// True when the string is suitable to remember as the user's last YouTube playlist URL
    /// (https YouTube hosts, or a bare playlist id — but not a local path).
    /// </summary>
    public static bool IsStorableLastLoadedYoutubeUrl(string? s)
    {
        var t = (s ?? "").Trim();
        if (string.IsNullOrEmpty(t))
            return false;

        if (Uri.TryCreate(t, UriKind.Absolute, out var uri) &&
            (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)))
        {
            var host = (uri.Host ?? "").ToLowerInvariant();
            return host.Contains("youtube.com", StringComparison.Ordinal) ||
                   host.Contains("youtu.be", StringComparison.Ordinal) ||
                   host.Contains("music.youtube.com", StringComparison.Ordinal);
        }

        if (LooksLikeLocalFilesystemSource(t))
            return false;

        return true;
    }

    public static string SanitizePersistedLastYoutubeUrl(string? s)
        => IsStorableLastLoadedYoutubeUrl(s) ? (s ?? "").Trim() : "";
}
