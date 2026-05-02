using System.IO;
using System.Text.Json;

namespace LyllyPlayer.Utils;

public sealed class CacheManager
{
    private sealed record CacheIndexEntry(string VideoId, string Path, long Bytes, DateTime CachedAtUtc);
    private sealed record CacheIndex(List<CacheIndexEntry> Entries);

    private readonly string _cacheDir;
    private readonly string _indexPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, CacheIndexEntry> _byId = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _downloading = new(StringComparer.OrdinalIgnoreCase);
    private long _maxBytes;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        MaxDepth = SafeJson.MaxDepth,
    };

    private static readonly JsonSerializerOptions ReadOptions = SafeJson.CreateDeserializerOptions();

    public CacheManager(string cacheDir, long maxBytes)
    {
        _cacheDir = cacheDir;
        _indexPath = System.IO.Path.Combine(cacheDir, "cache-index.json");
        _maxBytes = Math.Max(0, maxBytes);
        Directory.CreateDirectory(_cacheDir);
        _ = LoadIndexBestEffort();
    }

    public void SetMaxBytes(long maxBytes) => _maxBytes = Math.Max(0, maxBytes);

    /// <summary>Fired when a new file is indexed under the same key as <see cref="TryGetCachedPath"/> (the string passed to <see cref="EnsureCachedAsync"/>).</summary>
    public event EventHandler<string>? CacheEntryAdded;

    public string? TryGetCachedPath(string videoId)
    {
        if (string.IsNullOrWhiteSpace(videoId))
            return null;

        lock (_byId)
        {
            if (!_byId.TryGetValue(videoId, out var e))
                return null;
            if (!File.Exists(e.Path))
                return null;
            return e.Path;
        }
    }

    /// <summary>
    /// Best-effort size of an in-progress or completed yt-dlp cache file for <paramref name="storeKey"/>
    /// (same key as <see cref="TryGetCachedPath"/> / <c>vp-cache-*</c> naming). Used for seek-bar cache overlay.
    /// </summary>
    public long TryGetPartialCacheBytes(string storeKey)
    {
        if (string.IsNullOrWhiteSpace(storeKey))
            return 0;

        try
        {
            var safeKey = string.Concat(storeKey.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_'));
            var prefix = $"vp-cache-{safeKey}.";
            if (!Directory.Exists(_cacheDir))
                return 0;

            long best = 0;
            foreach (var path in Directory.EnumerateFiles(_cacheDir))
            {
                var name = Path.GetFileName(path);
                if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;
                try
                {
                    var len = new FileInfo(path).Length;
                    if (len > best)
                        best = len;
                }
                catch
                {
                    // ignore
                }
            }

            return best;
        }
        catch
        {
            return 0;
        }
    }

    public async Task EnsureCachedAsync(
        string videoId,
        Func<CancellationToken, Task<string>> downloadToCacheAsync,
        CancellationToken ct,
        IEnumerable<string>? protectedPaths = null,
        Action<string>? onFailureMessage = null)
    {
        if (string.IsNullOrWhiteSpace(videoId))
            return;

        // Already cached?
        var existing = TryGetCachedPath(videoId);
        if (!string.IsNullOrWhiteSpace(existing))
            return;

        try { AppLog.Info($"Cache: enqueue {videoId}", AppLogInfoTier.Crucial); } catch { /* ignore */ }

        await _gate.WaitAsync(ct);
        try
        {
            // Double-check after acquiring gate.
            existing = TryGetCachedPath(videoId);
            if (!string.IsNullOrWhiteSpace(existing))
                return;

            if (_downloading.Contains(videoId))
                return;

            _downloading.Add(videoId);
        }
        finally
        {
            _gate.Release();
        }

        string? downloaded = null;
        try
        {
            try { AppLog.Info($"Cache: downloading {videoId}", AppLogInfoTier.Crucial); } catch { /* ignore */ }
            downloaded = await downloadToCacheAsync(ct);
            if (string.IsNullOrWhiteSpace(downloaded) || !File.Exists(downloaded))
                return;

            var fi = new FileInfo(downloaded);
            var entry = new CacheIndexEntry(videoId, fi.FullName, fi.Length, DateTime.UtcNow);

            await _gate.WaitAsync(ct);
            try
            {
                lock (_byId)
                    _byId[videoId] = entry;
                await SaveIndexBestEffort(ct);
                await EnforceMaxSizeFifoAsync(ct, protectedPaths);
            }
            finally
            {
                _gate.Release();
            }
            try { AppLog.Info($"Cache: stored {videoId} ({fi.Length / 1024 / 1024} MB) -> {fi.FullName}", AppLogInfoTier.Crucial); } catch { /* ignore */ }
            try { CacheEntryAdded?.Invoke(this, videoId); } catch { /* ignore */ }
        }
        catch (Exception ex)
        {
            // best-effort cache; ignore
            try { AppLog.Warn($"Cache: failed {videoId}. {ex.Message}"); } catch { /* ignore */ }
            try { onFailureMessage?.Invoke(ex.Message); } catch { /* ignore */ }
        }
        finally
        {
            await _gate.WaitAsync(CancellationToken.None);
            try
            {
                _downloading.Remove(videoId);
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    private async Task EnforceMaxSizeFifoAsync(CancellationToken ct, IEnumerable<string>? protectedPaths)
    {
        var max = _maxBytes;
        if (max <= 0)
            return;

        var protectedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (protectedPaths is not null)
        {
            foreach (var p in protectedPaths)
            {
                if (!string.IsNullOrWhiteSpace(p))
                    protectedSet.Add(p);
            }
        }

        List<CacheIndexEntry> entries;
        lock (_byId)
            entries = _byId.Values.ToList();

        // Refresh sizes + drop missing.
        var updated = new List<CacheIndexEntry>(entries.Count);
        foreach (var e in entries)
        {
            try
            {
                if (!File.Exists(e.Path))
                    continue;
                var fi = new FileInfo(e.Path);
                updated.Add(e with { Bytes = fi.Length });
            }
            catch
            {
                // ignore
            }
        }

        long total = 0;
        foreach (var e in updated)
            total += Math.Max(0, e.Bytes);

        if (total <= max)
        {
            ReplaceIndex(updated);
            await SaveIndexBestEffort(ct);
            return;
        }

        // FIFO: oldest first.
        var ordered = updated
            .OrderBy(e => e.CachedAtUtc)
            .ToList();

        foreach (var e in ordered)
        {
            if (total <= max)
                break;
            if (protectedSet.Contains(e.Path))
                continue;

            // Don't evict currently-downloading items.
            if (_downloading.Contains(e.VideoId))
                continue;

            try
            {
                File.Delete(e.Path);
                total -= Math.Max(0, e.Bytes);
                RemoveFromIndex(e.VideoId);
            }
            catch
            {
                // ignore
            }
        }

        await SaveIndexBestEffort(ct);
    }

    private void ReplaceIndex(List<CacheIndexEntry> entries)
    {
        lock (_byId)
        {
            _byId.Clear();
            foreach (var e in entries)
                _byId[e.VideoId] = e;
        }
    }

    private void RemoveFromIndex(string videoId)
    {
        lock (_byId)
            _byId.Remove(videoId);
    }

    private async Task LoadIndexBestEffort()
    {
        try
        {
            if (!File.Exists(_indexPath))
                return;

            var fi = new FileInfo(_indexPath);
            if (fi.Length > SafeJson.MaxGeneralAppJsonFileBytes)
                return;

            var json = await File.ReadAllTextAsync(_indexPath);
            CacheIndex parsed;
            try
            {
                parsed = JsonSerializer.Deserialize<CacheIndex>(json, ReadOptions)
                    ?? new CacheIndex(new List<CacheIndexEntry>());
            }
            catch (JsonException)
            {
                return;
            }
            ReplaceIndex(parsed.Entries);
        }
        catch
        {
            // ignore
        }
    }

    private async Task SaveIndexBestEffort(CancellationToken ct)
    {
        try
        {
            CacheIndex idx;
            lock (_byId)
                idx = new CacheIndex(_byId.Values.OrderBy(e => e.CachedAtUtc).ToList());

            var json = JsonSerializer.Serialize(idx, JsonOptions);
            Directory.CreateDirectory(_cacheDir);
            await File.WriteAllTextAsync(_indexPath, json, ct);
        }
        catch
        {
            // ignore
        }
    }
}

