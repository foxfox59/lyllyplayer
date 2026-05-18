using System.Runtime.Versioning;
using Windows.Media;

namespace LyllyPlayer.Utils;

/// <summary>
/// Windows System Media Transport Controls (Bluetooth AVRCP, lock screen, taskbar).
/// </summary>
[SupportedOSPlatform("windows10.0.17763.0")]
public sealed class SystemMediaTransportService : IDisposable
{
    private SystemMediaTransportControls? _smtc;
    private IntPtr _windowHandle;

    public bool IsAvailable => _smtc is not null;

    public event EventHandler? PlayPausePressed;
    public event EventHandler? NextPressed;
    public event EventHandler? PrevPressed;

    public bool TryInitialize(IntPtr windowHandle, bool forceRecreate = false)
    {
        if (windowHandle == IntPtr.Zero)
            return false;

        if (!forceRecreate && _smtc is not null && _windowHandle == windowHandle)
            return true;

        TearDown();

        try
        {
            _windowHandle = windowHandle;
            _smtc = SystemMediaTransportControlsInterop.GetForWindow(windowHandle);
            if (_smtc is null)
            {
                try { AppLog.Warn("System media transport: GetForWindow returned null"); } catch { /* ignore */ }
                _windowHandle = IntPtr.Zero;
                return false;
            }

            _smtc.IsEnabled = true;
            _smtc.IsPlayEnabled = true;
            _smtc.IsPauseEnabled = true;
            _smtc.IsStopEnabled = false;
            _smtc.IsNextEnabled = true;
            _smtc.IsPreviousEnabled = true;
            _smtc.PlaybackStatus = MediaPlaybackStatus.Stopped;
            _smtc.ButtonPressed += OnButtonPressed;
            try { AppLog.Info($"System media transport: session registered (hwnd=0x{windowHandle.ToInt64():X})"); } catch { /* ignore */ }
            return true;
        }
        catch (Exception ex)
        {
            try { AppLog.Warn($"System media transport: init failed ({ex.Message})"); } catch { /* ignore */ }
            TearDown();
            return false;
        }
    }

    /// <summary>Register or refresh the session when playback starts. Avoid <paramref name="forceRecreate"/> on every track change.</summary>
    public void ActivateForPlayback(IntPtr windowHandle, bool hasTrack, bool isPlaying, string? title, string? artist, bool forceRecreate = false)
    {
        TryInitialize(windowHandle, forceRecreate);
        UpdateSession(hasTrack, isPlaying, title, artist);

        if (!hasTrack || !isPlaying)
            return;

        _ = ReclaimFocusIfNeededAsync(hasTrack, isPlaying, title, artist);
    }

    /// <summary>Lightweight metadata refresh when the track changes (no SMTC tear-down).</summary>
    public void RefreshNowPlaying(bool hasTrack, bool isPlaying, string? title, string? artist)
    {
        UpdateSession(hasTrack, isPlaying, title, artist);
        if (!hasTrack || !isPlaying)
            return;
        _ = ReclaimFocusIfNeededAsync(hasTrack, isPlaying, title, artist);
    }

    private async Task ReclaimFocusIfNeededAsync(bool hasTrack, bool isPlaying, string? title, string? artist)
    {
        try
        {
            if (await MediaSessionFocusHelper.IsLyllyPlayerCurrentAsync().ConfigureAwait(false))
                return;

            var other = await MediaSessionFocusHelper.TryGetCurrentSessionAppIdAsync().ConfigureAwait(false);
            try { AppLog.Info($"System media transport: reclaiming focus (current={other ?? "unknown"})"); } catch { /* ignore */ }

            UpdateSession(hasTrack, isPlaying, title, artist);
        }
        catch
        {
            // ignore
        }
    }

    public void UpdateSession(bool hasTrack, bool isPlaying, string? title = null, string? artist = null)
    {
        if (_smtc is null)
            return;

        try
        {
            _smtc.PlaybackStatus = !hasTrack
                ? MediaPlaybackStatus.Stopped
                : isPlaying
                    ? MediaPlaybackStatus.Playing
                    : MediaPlaybackStatus.Paused;
        }
        catch
        {
            // ignore
        }

        try
        {
            var updater = _smtc.DisplayUpdater;
            updater.Type = MediaPlaybackType.Music;
            updater.AppMediaId = "LyllyPlayer";
            if (hasTrack)
            {
                var music = updater.MusicProperties;
                music.Title = string.IsNullOrWhiteSpace(title) ? "Unknown" : title;
                music.Artist = artist ?? string.Empty;
                music.AlbumArtist = music.Artist;
            }

            updater.Update();
        }
        catch
        {
            // ignore
        }
    }

    public void Dispose()
    {
        TearDown();
    }

    private void TearDown()
    {
        try
        {
            if (_smtc is not null)
            {
                _smtc.ButtonPressed -= OnButtonPressed;
                try
                {
                    _smtc.IsEnabled = false;
                    _smtc.PlaybackStatus = MediaPlaybackStatus.Stopped;
                    _smtc.DisplayUpdater.Update();
                }
                catch { /* ignore */ }
            }
        }
        catch { /* ignore */ }

        _smtc = null;
        _windowHandle = IntPtr.Zero;
    }

    private void OnButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        try
        {
            try { AppLog.Info($"System media transport: button {args.Button}"); } catch { /* ignore */ }

            switch (args.Button)
            {
                case SystemMediaTransportControlsButton.Play:
                case SystemMediaTransportControlsButton.Pause:
                case SystemMediaTransportControlsButton.Stop:
                    PlayPausePressed?.Invoke(this, EventArgs.Empty);
                    break;
                case SystemMediaTransportControlsButton.Next:
                    NextPressed?.Invoke(this, EventArgs.Empty);
                    break;
                case SystemMediaTransportControlsButton.Previous:
                    PrevPressed?.Invoke(this, EventArgs.Empty);
                    break;
            }
        }
        catch
        {
            // ignore
        }
    }
}
