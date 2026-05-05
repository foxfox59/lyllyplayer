using System.IO;
using System.Text.Json;
using LyllyPlayer.Models;

namespace LyllyPlayer.Utils;

public static class LastPlaylistSnapshotStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        MaxDepth = SafeJson.MaxDepth,
    };

    private static readonly JsonSerializerOptions ReadOptions = SafeJson.CreateDeserializerOptions();

    public static string GetSnapshotPath()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LyllyPlayer"
        );
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "last-playlist.json");
    }

    public static void Save(SavedPlaylist snap)
    {
        var path = GetSnapshotPath();
        var json = JsonSerializer.Serialize(snap, JsonOptions);
        File.WriteAllText(path, json);
    }

    /// <summary>Deletes <c>last-playlist.json</c> if it exists.</summary>
    public static void Clear()
    {
        try
        {
            var path = GetSnapshotPath();
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // ignore
        }
    }

    /// <param name="fileWasUnreadable">
    /// True when <c>last-playlist.json</c> exists but could not be parsed (oversized, invalid JSON, etc.).
    /// </param>
    public static SavedPlaylist? TryLoad(out bool fileWasUnreadable)
    {
        fileWasUnreadable = false;
        try
        {
            var path = GetSnapshotPath();
            if (!File.Exists(path))
                return null;

            var json = SafeJson.ReadUtf8TextForJson(path, SafeJson.MaxGeneralAppJsonFileBytes);
            SavedPlaylist? snap;
            try
            {
                snap = JsonSerializer.Deserialize<SavedPlaylist>(json, ReadOptions);
            }
            catch (JsonException)
            {
                fileWasUnreadable = true;
                return null;
            }

            if (snap is null)
                fileWasUnreadable = true;
            return snap;
        }
        catch (InvalidDataException)
        {
            fileWasUnreadable = true;
            return null;
        }
        catch
        {
            return null;
        }
    }
}

