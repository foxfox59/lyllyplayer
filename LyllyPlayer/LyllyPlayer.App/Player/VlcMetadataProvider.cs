using LyllyPlayer.Utils;

namespace LyllyPlayer.Player;

/// <summary>Duration probing for local paths (delegates to Media Foundation — not LibVLC).</summary>
public static class VlcMetadataProvider
{
    /// <inheritdoc cref="LocalMetadataService.TryGetDurationSecondsAsync"/>
    public static Task<int?> TryGetDurationSecondsAsync(string filePath, CancellationToken ct = default)
        => LocalMetadataService.TryGetDurationSecondsAsync(filePath, ct);
}
