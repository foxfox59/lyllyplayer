using Windows.Media.Control;

namespace LyllyPlayer.Utils;

/// <summary>
/// Observes which app owns the global media session (volume overlay / Bluetooth AVRCP).
/// </summary>
internal static class MediaSessionFocusHelper
{
    public const string LyllyPlayerAppUserModelId = "LyllyPlayer.LyllyPlayer.Foreground.1";

    public static string? TryGetCurrentSessionAppId()
    {
        try
        {
            var manager = GlobalSystemMediaTransportControlsSessionManager
                .RequestAsync()
                .AsTask()
                .ConfigureAwait(false)
                .GetAwaiter()
                .GetResult();
            return manager.GetCurrentSession()?.SourceAppUserModelId;
        }
        catch
        {
            return null;
        }
    }

    public static bool IsLyllyPlayerCurrent()
    {
        var id = TryGetCurrentSessionAppId();
        if (string.IsNullOrEmpty(id))
            return false;
        return id.Contains("LyllyPlayer", StringComparison.OrdinalIgnoreCase);
    }
}
