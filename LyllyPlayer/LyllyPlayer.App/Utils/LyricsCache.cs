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
    private const int MaxFileBytes = 512 * 1024; // 512 KB max file size

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

    /// <summary>A single lyrics cache entry with LRC text and fetch timestamp.</summary>
    private sealed class CacheEntry
    {
        public string LrcText { get; }
        public DateTime FetchedAtUtc { get; }
        public bool IsMiss { get; }

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
