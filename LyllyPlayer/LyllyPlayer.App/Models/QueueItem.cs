using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace LyllyPlayer.Models;

public sealed class QueueItem : INotifyPropertyChanged
{
    public QueueItem(PlaylistEntry entry, string? indexPrefix = null)
    {
        Entry = entry;
        IndexPrefix = indexPrefix;
    }

    private QueueItem() { }

    public PlaylistEntry? Entry { get; private set; }
    public string VideoId => Entry?.VideoId ?? "";
    public string Title => Entry?.Title ?? "";
    public string? Channel => Entry?.Channel;
    public string? IndexPrefix { get; }
    public bool IsQueued { get; init; }
    /// <summary>True when this base playlist row currently exists in queue (reorder instead of add).</summary>
    public bool IsInQueue { get; init; }
    /// <summary>0-based row index for base playlist items; null for queued items.</summary>
    public int? BaseIndex { get; init; }
    public Guid? QueueInstanceId { get; init; }
    public int? QueueOrdinal { get; set; }
    public string DisplayTitle
        => false
            ? ""
            : $"{(IsQueued && QueueOrdinal is int q ? $"Q{q}. " : "")}{(!string.IsNullOrWhiteSpace(IndexPrefix) ? IndexPrefix : "")}{(IsPremium ? "[PREMIUM] " : "")}{(string.IsNullOrWhiteSpace(Channel) ? Title : $"{Title} — {Channel}")}{(RequiresCookies ? " [auth]" : "")}{(IsUnavailable ? " (Not available)" : "")}";
    public int? DurationSeconds => Entry?.DurationSeconds;
    public string WebpageUrl => Entry?.WebpageUrl ?? "";
    private bool IsLocal
    {
        get
        {
            if (VideoId.StartsWith("local:", StringComparison.OrdinalIgnoreCase))
                return true;
            try { return Path.IsPathRooted(WebpageUrl); } catch { return false; }
        }
    }

    private bool IsStream
    {
        get
        {
            if (IsLocal) return false;
            var s = WebpageUrl;
            if (string.IsNullOrWhiteSpace(s)) return false;
            if (!Uri.TryCreate(s, UriKind.Absolute, out var uri)) return false;
            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                return false;
            var h = uri.Host ?? "";
            if (h.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) || h.Contains("youtu.be", StringComparison.OrdinalIgnoreCase))
                return false;
            return true;
        }
    }

    private bool IsYoutube
    {
        get
        {
            if (IsLocal) return false;
            var s = WebpageUrl;
            if (string.IsNullOrWhiteSpace(s)) return false;
            if (!Uri.TryCreate(s, UriKind.Absolute, out var uri)) return false;
            var h = uri.Host ?? "";
            return h.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) || h.Contains("youtu.be", StringComparison.OrdinalIgnoreCase);
        }
    }

    public string SourceGlyph
        => IsLocal ? "\uD83D\uDCC1" : (IsStream ? "\u2248" : (IsYoutube ? "\u25B6" : ""));

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
    public bool RequiresCookies => Entry?.RequiresCookies ?? false;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void UpdateEntry(PlaylistEntry entry)
    {
        Entry = entry;
        OnPropertyChanged(nameof(Entry));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Channel));
        OnPropertyChanged(nameof(DurationSeconds));
        OnPropertyChanged(nameof(WebpageUrl));
        OnPropertyChanged(nameof(DisplayTitle));
        OnPropertyChanged(nameof(SourceGlyph));
        OnPropertyChanged(nameof(RequiresCookies));
    }
}


