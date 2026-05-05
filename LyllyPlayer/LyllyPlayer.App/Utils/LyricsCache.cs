using System.Text.Json;

namespace LyllyPlayer.Utils;

/// <summary>
/// Flat JSON file cache for lyrics. Stores lyrics by key with fetch timestamp and TTL.
/// Lyrics are short-lived (30 days) since they rarely change but aren't permanent.
/// </summary>
public static class LyricsCache
{
    private const string FileName = "lyrics-cache.json";
    private const int DefaultTtlDays = 30;
    private const int MissTtlHours = 24;

    /// <summary>How long a cached "no lyrics" result suppresses re-fetching (same value as internal miss expiry).</summary>
    public const int MissEntryTtlHours = MissTtlHours;
    // NOTE: This used to be a "poor man's exploit guard" but it also prevented legitimate caches from loading
    // at startup (forcing network fetches and making lyrics appear late). Keep best-effort parsing instead.
    private const int MaxFileBytes = int.MaxValue;

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        MaxDepth = SafeJson.MaxDepth,
    };

    private static readonly JsonSerializerOptions ReadOptions = SafeJson.CreateDeserializerOptions();

    private static string? _cacheFilePath;
    private static readonly object _gate = new();

    /// <summary>Thread-safe dictionary of lyrics entries, keyed by cache key.</summary>
    private static readonly Dictionary<string, CacheEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes the cache with the base directory path.
    /// Should be called once during app startup.
    /// </summary>
    public static void Initialize(string appDataDir)
    {
        _cacheFilePath = System.IO.Path.Combine(appDataDir, FileName);
        LoadBestEffort();
    }

    /// <summary>Gets the lyrics LRC text for the given cache key, or null if not cached/expired.</summary>
    public static string? Get(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return null;

        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var entry))
                return null;

            if (entry.IsExpired())
            {
                _entries.Remove(key);
                return null;
            }

            return entry.IsMiss ? null : entry.LrcText;
        }
    }

    /// <summary>Returns true if the given key is cached as a "miss" (no acceptable lyrics) and not expired.</summary>
    public static bool IsMiss(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return false;

        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var entry))
                return false;

            if (entry.IsExpired())
            {
                _entries.Remove(key);
                return false;
            }

            return entry.IsMiss;
        }
    }

    /// <summary>Saves lyrics to the cache with the given key. Creates/updates the entry and persists to disk.</summary>
    public static void Set(string key, string lrcText)
    {
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(lrcText))
            return;

        lock (_gate)
        {
            _entries[key] = new CacheEntry(lrcText);
        }

        SaveBestEffort();
    }

    /// <summary>Caches an explicit "miss" (no acceptable lyrics) for the given key.</summary>
    public static void SetMiss(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;

        lock (_gate)
        {
            _entries[key] = CacheEntry.CreateMiss();
        }

        SaveBestEffort();
    }

    /// <summary>
    /// Copies a cached entry (LRC text or explicit miss) to another key if the destination is missing or expired.
    /// Used when a YouTube row is replaced by a local file: <c>yt_…</c> → <c>lyr_…</c> (local id).
    /// </summary>
    public static void TryMigrateLyricsEntry(string? fromKey, string toKey)
    {
        if (string.IsNullOrWhiteSpace(toKey))
            return;
        if (string.IsNullOrWhiteSpace(fromKey) || string.Equals(fromKey, toKey, StringComparison.OrdinalIgnoreCase))
            return;

        lock (_gate)
        {
            if (_entries.TryGetValue(toKey, out var dest) && !dest.IsExpired())
                return;

            if (!_entries.TryGetValue(fromKey, out var src) || src.IsExpired())
                return;

            _entries[toKey] = new CacheEntry(src.LrcText, src.FetchedAtUtc, src.IsMiss);
        }

        SaveBestEffort();
    }

    /// <summary>Migrates lyrics stored under the YouTube cache key to the local playlist entry key.</summary>
    public static void TryMigrateYoutubeLyricsToLocalEntry(string youtubeVideoId, string localPlaylistVideoId)
    {
        if (string.IsNullOrWhiteSpace(youtubeVideoId) || string.IsNullOrWhiteSpace(localPlaylistVideoId))
            return;
        TryMigrateLyricsEntry($"yt_{youtubeVideoId}", $"lyr_{localPlaylistVideoId}");
    }

    /// <summary>Removes the lyrics entry for the given key.</summary>
    public static void Remove(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;

        lock (_gate)
        {
            _entries.Remove(key);
        }

        SaveBestEffort();
    }

    /// <summary>Clears all cached lyrics.</summary>
    public static void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
        }

        SaveBestEffort();
    }

    /// <summary>Gets the number of cached lyrics entries.</summary>
    public static int Count
    {
        get
        {
            lock (_gate)
                return _entries.Count;
        }
    }

    private static void LoadBestEffort()
    {
        if (string.IsNullOrWhiteSpace(_cacheFilePath) || !System.IO.File.Exists(_cacheFilePath))
            return;

        try
        {
            var fileInfo = new System.IO.FileInfo(_cacheFilePath);
            if (fileInfo.Length > MaxFileBytes)
                return;

            var json = System.IO.File.ReadAllText(_cacheFilePath);
            if (string.IsNullOrWhiteSpace(json))
                return;

            var parsed = JsonSerializer.Deserialize<Dictionary<string, CacheEntry>>(json, ReadOptions);
            if (parsed == null)
                return;

            lock (_gate)
            {
                _entries.Clear();
                foreach (var kvp in parsed)
                {
                    if (kvp.Value != null && !kvp.Value.IsExpired())
                        _entries[kvp.Key] = kvp.Value;
                }
            }
        }
        catch
        {
            // Ignore parse errors — best effort only
        }
    }

    private static void SaveBestEffort()
    {
        if (string.IsNullOrWhiteSpace(_cacheFilePath))
            return;

        try
        {
            Dictionary<string, CacheEntry> snapshot;
            lock (_gate)
            {
                snapshot = new Dictionary<string, CacheEntry>(_entries);
            }

            var json = JsonSerializer.Serialize(snapshot, WriteOptions);
            System.IO.File.WriteAllText(_cacheFilePath, json);
        }
        catch
        {
            // Ignore write errors — best effort only
        }
    }

    /// <summary>
    /// A single lyrics cache entry with LRC text and fetch timestamp.
    /// Must be JSON-deserializable so the cache actually loads on startup.
    /// </summary>
    private sealed class CacheEntry
    {
        // Setters are required for System.Text.Json to deserialize.
        public string LrcText { get; set; } = "";
        public DateTime FetchedAtUtc { get; set; } = DateTime.UtcNow;
        public bool IsMiss { get; set; }

        public CacheEntry() { }

        public CacheEntry(string lrcText, DateTime? fetchedAtUtc = null, bool isMiss = false)
        {
            LrcText = lrcText;
            FetchedAtUtc = fetchedAtUtc ?? DateTime.UtcNow;
            IsMiss = isMiss;
        }

        public static CacheEntry CreateMiss() => new CacheEntry(lrcText: "MISS", fetchedAtUtc: DateTime.UtcNow, isMiss: true);

        public bool IsExpired()
        {
            var age = DateTime.UtcNow - FetchedAtUtc;
            if (IsMiss)
                return age.TotalHours > MissTtlHours;
            return age.TotalDays > DefaultTtlDays;
        }
    }
}
