using Windows.Media.Control;

namespace LyllyPlayer.Utils;

/// <summary>
/// Observes which app owns the global media session (volume overlay / Bluetooth AVRCP).
/// </summary>
internal static class MediaSessionFocusHelper
{
    public const string LyllyPlayerAppUserModelId = "LyllyPlayer.LyllyPlayer.Foreground.1";

    public static async Task<string?> TryGetCurrentSessionAppIdAsync()
    {
        try
        {
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask()
                .ConfigureAwait(false);
            return manager.GetCurrentSession()?.SourceAppUserModelId;
        }
        catch
        {
            return null;
        }
    }

    public static async Task<bool> IsLyllyPlayerCurrentAsync()
    {
        var id = await TryGetCurrentSessionAppIdAsync().ConfigureAwait(false);
        if (string.IsNullOrEmpty(id))
            return false;
        return id.Contains("LyllyPlayer", StringComparison.OrdinalIgnoreCase);
    }
}
