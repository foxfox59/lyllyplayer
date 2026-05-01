using System.IO;
using LibVLCSharp.Shared;
using LyllyPlayer.Utils;

namespace LyllyPlayer.Player;

/// <summary>Duration probing via LibVLC (replaces ffprobe for local files).</summary>
public static class VlcMetadataProvider
{
    /// <summary>Returns duration in seconds when known, otherwise <see langword="null"/>.</summary>
    public static async Task<int?> TryGetDurationSecondsAsync(string filePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return null;

        Media? media = null;
        try
        {
            LibVlcHost.EnsureInitialized();
            var lib = LibVlcHost.LibVLC;
            media = new Media(lib, filePath, FromType.FromPath);
            using var parseCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            parseCts.CancelAfter(TimeSpan.FromSeconds(12));
            await media.Parse(MediaParseOptions.ParseLocal, timeout: 12000, parseCts.Token).ConfigureAwait(false);
            var lenMs = media.Duration;
            if (lenMs <= 0)
                return null;
            var sec = (int)Math.Round(lenMs / 1000.0, MidpointRounding.AwayFromZero);
            return sec > 0 ? sec : null;
        }
        catch (Exception ex)
        {
            try { AppLog.Warn($"VLC duration probe failed for {filePath}: {ex.Message}"); } catch { /* ignore */ }
            return null;
        }
        finally
        {
            try { media?.Dispose(); } catch { /* ignore */ }
        }
    }
}
