using System;
using System.IO;
using LyllyPlayer.Models;

namespace LyllyPlayer.Utils;

public enum PlaylistOpenTargetKind
{
    None,
    LocalFile,
    Url,
}

public readonly record struct PlaylistOpenTarget(PlaylistOpenTargetKind Kind, string Value);

public static class PlaylistOpenTargetHelper
{
    private static bool LooksLikeYoutubeVideoId(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return false;
        var t = s.Trim();
        if (t.Length != 11)
            return false;
        for (var i = 0; i < t.Length; i++)
        {
            var c = t[i];
            var ok = (c >= 'a' && c <= 'z') ||
                     (c >= 'A' && c <= 'Z') ||
                     (c >= '0' && c <= '9') ||
                     c == '_' || c == '-';
            if (!ok) return false;
        }
        return true;
    }

    public static bool TryGetOpenTarget(QueueItem qi, out PlaylistOpenTarget target)
    {
        target = default;
        if (qi is null)
            return false;

        if (LocalPlaylistLoader.TryGetLocalPath(qi.WebpageUrl, out var path))
        {
            target = new PlaylistOpenTarget(PlaylistOpenTargetKind.LocalFile, path);
            return true;
        }

        var url = NormalizeHttpUrlOrNull(qi);
        if (!string.IsNullOrWhiteSpace(url))
        {
            target = new PlaylistOpenTarget(PlaylistOpenTargetKind.Url, url);
            return true;
        }

        return false;
    }

    public static string? NormalizeHttpUrlOrNull(QueueItem qi)
    {
        try
        {
            var url = (qi.WebpageUrl ?? "").Trim();
            if (Uri.TryCreate(url, UriKind.Absolute, out var u) &&
                (string.Equals(u.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(u.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
                return u.ToString();
        }
        catch { /* ignore */ }

        // Some code paths store a bare YouTube id; synthesize a watch URL.
        var vid = (qi.VideoId ?? "").Trim();
        if (LooksLikeYoutubeVideoId(vid))
            return $"https://www.youtube.com/watch?v={Uri.EscapeDataString(vid)}";

        return null;
    }

    public static string GetOpenMenuHeader(QueueItem qi)
    {
        if (qi is null)
            return "Open";

        try
        {
            if (LocalPlaylistLoader.TryGetLocalPath(qi.WebpageUrl, out _))
                return "Open file location";
        }
        catch { /* ignore */ }

        try
        {
            if (!string.IsNullOrWhiteSpace(NormalizeHttpUrlOrNull(qi)))
                return "Open source";
        }
        catch { /* ignore */ }

        return "Open";
    }
}

