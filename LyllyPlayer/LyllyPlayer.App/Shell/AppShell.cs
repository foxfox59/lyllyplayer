using LyllyPlayer.Services;
using LyllyPlayer.ShellServices;

namespace LyllyPlayer.Shell;

/// <summary>
/// Composition root for the WPF shell: groups reusable services so UI code can stay thin.
/// (Playback engine and UI timers remain in the WPF layer for now.)
/// </summary>
public sealed class AppShell
{
    public PlaylistService Playlist { get; } = new();

    public PlayOrderService PlayOrder { get; } = new();

    public SettingsService Settings { get; } = new();

    public LyricsService Lyrics { get; } = new();

    public PlaylistFileService PlaylistFiles { get; } = new();
}

