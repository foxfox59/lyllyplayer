using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace LyllyPlayer.Models;

public sealed class QueueItem : INotifyPropertyChanged
{
    public QueueItem(PlaylistEntry entry, string? indexPrefix = null)
    {
        Entry = entry;
        IndexPrefix = indexPrefix;
    }

    public PlaylistEntry Entry { get; }
    public string VideoId => Entry.VideoId;
    public string Title => Entry.Title;
    public string? Channel => Entry.Channel;
    public string? IndexPrefix { get; }
    public string DisplayTitle
        => $"{(IsLocal && !string.IsNullOrWhiteSpace(IndexPrefix) ? IndexPrefix : "")}{(IsPremium ? "[PREMIUM] " : "")}{(string.IsNullOrWhiteSpace(Channel) ? Title : $"{Title} — {Channel}")}{(RequiresCookies ? " [auth]" : "")}{(IsUnavailable ? " (Not available)" : "")}";
    public int? DurationSeconds => Entry.DurationSeconds;
    public string WebpageUrl => Entry.WebpageUrl;
    private bool IsLocal => VideoId.StartsWith("local:", StringComparison.OrdinalIgnoreCase);

    private bool _isPremium;
    public bool IsPremium
    {
        get => _isPremium;
        set
        {
            if (_isPremium == value) return;
            _isPremium = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayTitle));
        }
    }

    private bool _isUnavailable;
    public bool IsUnavailable
    {
        get => _isUnavailable;
        set
        {
            if (_isUnavailable == value) return;
            _isUnavailable = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayTitle));
        }
    }

    private bool _isAgeRestricted;
    public bool IsAgeRestricted
    {
        get => _isAgeRestricted;
        set
        {
            if (_isAgeRestricted == value) return;
            _isAgeRestricted = value;
            OnPropertyChanged();
        }
    }

    private bool _isNowPlaying;
    public bool IsNowPlaying
    {
        get => _isNowPlaying;
        set
        {
            if (_isNowPlaying == value) return;
            _isNowPlaying = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Forwarded from <see cref="PlaylistEntry.RequiresCookies"/>.
    /// True when yt-dlp's flat metadata reported <c>availability = needs_auth</c> or <c>is_private</c>.
    /// These items will play only when browser cookies are configured; without them they produce 403s.
    /// </summary>
    public bool RequiresCookies => Entry.RequiresCookies;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}


