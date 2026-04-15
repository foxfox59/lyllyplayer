using System.IO;
using System.Text.Json;
using LyllyPlayer.Models;
using LyllyPlayer.Utils;

namespace LyllyPlayer.Settings;

public static class PlaylistCacheStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        MaxDepth = SafeJson.MaxDepth,
    };

    private static readonly JsonSerializerOptions ReadOptions = SafeJson.CreateDeserializerOptions();

    public sealed record PlaylistCache(
        string PlaylistId,
        string? PlaylistTitle,
        DateTimeOffset SavedAtUtc,
        List<PlaylistEntry> Entries
    );

    public static string GetCachePath()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LyllyPlayer"
        );

        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "playlist-cache.json");
    }

    public static PlaylistCache? Load()
    {
        try
        {
            var path = GetCachePath();
            if (!File.Exists(path))
                return null;

            var json = SafeJson.ReadUtf8TextForJson(path, SafeJson.MaxGeneralAppJsonFileBytes);
            var cache = JsonSerializer.Deserialize<PlaylistCache>(json, ReadOptions);
            if (cache is null)
            {
                try { AppLog.Warn("playlist-cache.json deserialized to null; ignoring."); } catch { /* ignore */ }
                return null;
            }

            if (cache.Entries is null || cache.Entries.Count == 0)
                try { AppLog.Warn("playlist-cache.json has no entries."); } catch { /* ignore */ }

            return cache;
        }
        catch (InvalidDataException ex)
        {
            try { AppLog.Warn($"playlist-cache.json rejected: {ex.Message}"); } catch { /* ignore */ }
            return null;
        }
        catch (JsonException ex)
        {
            try { AppLog.Warn($"playlist-cache.json invalid JSON: {ex.Message}"); } catch { /* ignore */ }
            return null;
        }
        catch
        {
            return null;
        }
    }

    public static void Save(PlaylistCache cache)
    {
        var path = GetCachePath();
        var json = JsonSerializer.Serialize(cache, JsonOptions);
        File.WriteAllText(path, json);
    }
}

