using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using LyllyPlayer.Player;
using NAudio.Wave;

namespace LyllyPlayer.Utils;

public static class LocalMetadataService
{
    public sealed record LocalInfo(string? Title, string? Artist, int? DurationSeconds);

    public static async Task<LocalInfo?> TryGetInfoAsync(string filePath, CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return null;

            var lower = filePath.ToLowerInvariant();
            var isAudioWithTagLib = lower.EndsWith(".mp3")
                || lower.EndsWith(".flac")
                || lower.EndsWith(".m4a")
                || lower.EndsWith(".aac")
                || lower.EndsWith(".ogg")
                || lower.EndsWith(".opus");

            string? title = null;
            string? artist = null;
            int? durationSeconds = null;

            // "Real" duration (container/decoder derived), without LibVLC.
            // Prefer Media Foundation on Windows for accuracy/robustness across VBR MP3 etc.
            durationSeconds = await TryGetDurationSecondsWithMediaFoundationAsync(filePath, ct).ConfigureAwait(false);

            // Prefer TagLib# for local durations (broad format support and does not require LibVLC init).
            // For ID3/Opus/Vorbis comment audio formats, also use TagLib# as the primary tag reader.
            // (We still try TagLib# for other file types too, but only apply title/artist when it's a known audio tag container.)
            try
            {
                var (t2, a2, d2) = TryReadWithTagLibSharp(filePath);
                if (isAudioWithTagLib && (!string.IsNullOrWhiteSpace(t2) || !string.IsNullOrWhiteSpace(a2)))
                {
                    title = t2;
                    artist = a2;
                }

                // Only use TagLib duration as a fallback if Media Foundation couldn't provide one.
                if (durationSeconds is null && d2 is int dd && dd > 0)
                    durationSeconds = dd;
            }
            catch { /* ignore */ }

            // Avoid LibVLC duration probing here (can destabilize some systems).

            if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(artist) && durationSeconds is null)
                return null;

            if (LooksMojibake(title) || LooksMojibake(artist))
            {
                try
                {
                    var (t2, a2, _) = TryReadWithTagLibSharp(filePath);
                    if (!string.IsNullOrWhiteSpace(t2)) title = t2;
                    if (!string.IsNullOrWhiteSpace(a2)) artist = a2;
                }
                catch { /* ignore */ }
            }

            return new LocalInfo(title, artist, durationSeconds);
        }
        catch
        {
            return null;
        }
    }

    private static Task<int?> TryGetDurationSecondsWithMediaFoundationAsync(string filePath, CancellationToken ct)
    {
        // MediaFoundationReader is synchronous; wrap for async call sites.
        return Task.Run(() =>
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    return (int?)null;

                using var r = new MediaFoundationReader(filePath);
                var ts = r.TotalTime;
                if (ts.TotalSeconds > 0.5)
                    return (int)Math.Round(ts.TotalSeconds);
                return (int?)null;
            }
            catch
            {
                return (int?)null;
            }
        }, ct);
    }

    private static bool LooksMojibake(string? s)
        => !string.IsNullOrWhiteSpace(s) && s.Contains('\uFFFD');

    /// <summary>
    /// Synchronously reads title, artist, and duration from a file.
    /// TagLibSharp for tags + LibVLC parse for duration — all on the calling thread.
    /// </summary>
    public static (string? Title, string? Artist, int? DurationSeconds) ReadTagsSync(string filePath)
    {
        try
        {
            using var f = TagLib.File.Create(filePath);
            var (title, artist) = ReadTagLibTitleArtist(f);
            int? durationSeconds = null;
            try
            {
                var ts = f.Properties?.Duration;
                if (ts is { } d && d.TotalSeconds > 0.5)
                    durationSeconds = (int)Math.Round(d.TotalSeconds);
            }
            catch { /* ignore */ }

            // Prefer Media Foundation over LibVLC for "real" duration if TagLib didn't provide it.
            if (durationSeconds is null && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                try
                {
                    using var r = new MediaFoundationReader(filePath);
                    var ts2 = r.TotalTime;
                    if (ts2.TotalSeconds > 0.5)
                        durationSeconds = (int)Math.Round(ts2.TotalSeconds);
                }
                catch { /* ignore */ }
            }

            return (title, artist, durationSeconds);
        }
        catch
        {
            return default;
        }
    }

    private static (string? title, string? artist, int? durationSeconds) TryReadWithTagLibSharp(string path)
    {
        try
        {
            using var f = TagLib.File.Create(path);
            var (t, a) = ReadTagLibTitleArtist(f);
            int? d = null;
            try
            {
                var ts = f.Properties?.Duration;
                if (ts is { } dur && dur.TotalSeconds > 0.5)
                    d = (int)Math.Round(dur.TotalSeconds);
            }
            catch { /* ignore */ }
            return (t, a, d);
        }
        catch
        {
            return (null, null, null);
        }
    }

    private static (string? title, string? artist) ReadTagLibTitleArtist(TagLib.File f)
    {
        var title = (f.Tag?.Title ?? "").Trim();
        if (string.IsNullOrWhiteSpace(title)) title = null;

        var performers = f.Tag?.Performers ?? Array.Empty<string>();
        var joined = string.Join(", ", performers.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()));
        var artist = string.IsNullOrWhiteSpace(joined) ? null : joined;

        if (string.IsNullOrWhiteSpace(artist))
        {
            var albumArtists = f.Tag?.AlbumArtists ?? Array.Empty<string>();
            var aj = string.Join(", ", albumArtists.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()));
            artist = string.IsNullOrWhiteSpace(aj) ? null : aj;
        }

        if (LooksMojibake(title)) title = null;
        if (LooksMojibake(artist)) artist = null;

        return (title, artist);
    }

}
