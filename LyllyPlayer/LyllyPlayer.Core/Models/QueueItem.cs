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
    private int? _queueOrdinal;
    public int? QueueOrdinal
    {
        get => _queueOrdinal;
        set
        {
            if (_queueOrdinal == value) return;
            _queueOrdinal = value;
            InvalidateCaches();
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayTitle));
        }
    }
    private string? _displayTitleCache;
    public string DisplayTitle
    {
        get
        {
            // This is bound for *every* row; cache aggressively and invalidate on updates.
            if (_displayTitleCache is not null)
                return _displayTitleCache;

            var prefix = (IsQueued && QueueOrdinal is int q ? $"Q{q}. " : "");
            var indexPrefix = (!string.IsNullOrWhiteSpace(IndexPrefix) ? IndexPrefix : "");
            var premium = (IsPremium ? "[PREMIUM] " : "");
            var main = (string.IsNullOrWhiteSpace(Channel) ? Title : $"{Title} — {Channel}");
            var auth = (RequiresCookies ? " [auth]" : "");
            var unavailable = (IsUnavailable ? " (Not available)" : "");
            _displayTitleCache = $"{prefix}{indexPrefix}{premium}{main}{auth}{unavailable}";
            return _displayTitleCache;
        }
    }
    public int? DurationSeconds => Entry?.DurationSeconds;
    public string WebpageUrl => Entry?.WebpageUrl ?? "";

    private bool? _isLocalCache;
    private bool IsLocal
    {
        get
        {
            if (_isLocalCache is bool b)
                return b;
            if (VideoId.StartsWith("local:", StringComparison.OrdinalIgnoreCase))
                return (_isLocalCache = true).Value;
            try { return (_isLocalCache = Path.IsPathRooted(WebpageUrl)).Value; } catch { return (_isLocalCache = false).Value; }
        }
    }

    private bool? _isStreamCache;
    private bool IsStream
    {
        get
        {
            if (_isStreamCache is bool b)
                return b;
            if (IsLocal) return false;
            var s = WebpageUrl;
            if (string.IsNullOrWhiteSpace(s)) return false;
            if (!Uri.TryCreate(s, UriKind.Absolute, out var uri)) return (_isStreamCache = false).Value;
            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                return (_isStreamCache = false).Value;
            var h = uri.Host ?? "";
            if (h.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) || h.Contains("youtu.be", StringComparison.OrdinalIgnoreCase))
                return (_isStreamCache = false).Value;
            return (_isStreamCache = true).Value;
        }
    }

    private bool? _isYoutubeCache;
    private bool IsYoutube
    {
        get
        {
            if (_isYoutubeCache is bool b)
                return b;
            if (IsLocal) return false;
            var s = WebpageUrl;
            if (string.IsNullOrWhiteSpace(s)) return false;
            if (!Uri.TryCreate(s, UriKind.Absolute, out var uri)) return (_isYoutubeCache = false).Value;
            var h = uri.Host ?? "";
            return (_isYoutubeCache = (h.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) || h.Contains("youtu.be", StringComparison.OrdinalIgnoreCase))).Value;
        }
    }

    private string? _sourceGlyphCache;
    public string SourceGlyph
        => _sourceGlyphCache ??= (IsLocal ? "\uD83D\uDCC1" : (IsStream ? "\u2248" : (IsYoutube ? "\u25B6" : "")));

    private string? _searchHaystackLower;
    public string GetSearchHaystackLower()
    {
        if (_searchHaystackLower is not null)
            return _searchHaystackLower;
        _searchHaystackLower = $"{Title}\u001f{Channel ?? ""}\u001f{DisplayTitle}\u001f{WebpageUrl}".ToLowerInvariant();
        return _searchHaystackLower;
    }

    private void InvalidateCaches()
    {
        _displayTitleCache = null;
        _sourceGlyphCache = null;
        _searchHaystackLower = null;
        _isLocalCache = null;
        _isStreamCache = null;
        _isYoutubeCache = null;
    }

    private bool _isPremium;
    public bool IsPremium
    {
        get => _isPremium;
        set
        {
            if (_isPremium == value) return;
            _isPremium = value;
            InvalidateCaches();
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
            InvalidateCaches();
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
        InvalidateCaches();
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
