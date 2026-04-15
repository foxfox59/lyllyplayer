namespace LyllyPlayer.Models;

public sealed record PlaylistEntry(
    string VideoId,
    string Title,
    string? Channel,
    int? DurationSeconds,
    string WebpageUrl,
    /// <summary>
    /// True when yt-dlp's <c>--flat-playlist</c> reported <c>availability</c> = <c>needs_auth</c> or the video is private.
    /// These entries appear in search/playlist metadata but will fail without <c>--cookies-from-browser</c>.
    /// </summary>
    bool RequiresCookies = false
);


