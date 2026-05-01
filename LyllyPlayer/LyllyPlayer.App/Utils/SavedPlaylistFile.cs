using System.IO;
using System.Text.Json;
using LyllyPlayer.Models;
using LyllyPlayer.Services;

namespace LyllyPlayer.Utils;

public readonly record struct SavedPlaylistFileLoadResult(
    bool Success,
    SavedPlaylist? Playlist,
    IReadOnlyList<PlaylistEntry> Entries,
    string? ErrorMessage);

public static class SavedPlaylistFile
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        MaxDepth = SafeJson.MaxDepth,
    };

    private static readonly JsonSerializerOptions ReadOptions = SafeJson.CreateDeserializerOptions();

    public static void Save(string path, SavedPlaylist playlist)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        var json = JsonSerializer.Serialize(playlist, JsonOptions);
        File.WriteAllText(path, json);
    }

    /// <summary>Loads a user playlist JSON file with size and depth limits. Does not execute code from the file.</summary>
    public static SavedPlaylistFileLoadResult TryLoadPlaylist(string path)
    {
        try
        {
            var json = SafeJson.ReadUtf8TextForJson(path, SafeJson.MaxPlaylistFileBytes);
            SavedPlaylist? pl;
            try
            {
                pl = JsonSerializer.Deserialize<SavedPlaylist>(json, ReadOptions);
            }
            catch (JsonException)
            {
                return new SavedPlaylistFileLoadResult(
                    false,
                    null,
                    Array.Empty<PlaylistEntry>(),
                    "This file is not valid JSON or does not match the LyllyPlayer saved playlist format.");
            }

            if (pl is null)
            {
                return new SavedPlaylistFileLoadResult(
                    false,
                    null,
                    Array.Empty<PlaylistEntry>(),
                    "The playlist file could not be read (unrecognized structure).");
            }

            var entries = ToEntries(pl);
            return new SavedPlaylistFileLoadResult(true, pl, entries, null);
        }
        catch (FileNotFoundException)
        {
            return new SavedPlaylistFileLoadResult(
                false,
                null,
                Array.Empty<PlaylistEntry>(),
                "The playlist file was not found.");
        }
        catch (InvalidDataException ex)
        {
            return new SavedPlaylistFileLoadResult(false, null, Array.Empty<PlaylistEntry>(), ex.Message);
        }
        catch (IOException ex)
        {
            return new SavedPlaylistFileLoadResult(
                false,
                null,
                Array.Empty<PlaylistEntry>(),
                $"Could not read the playlist file: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            return new SavedPlaylistFileLoadResult(
                false,
                null,
                Array.Empty<PlaylistEntry>(),
                $"Access denied when reading the playlist file: {ex.Message}");
        }
    }

    public static SavedPlaylist FromEntries(string name, string sourceType, string source, IReadOnlyList<PlaylistEntry> entries) =>
        PlaylistSnapshotBuilder.FromEntries(name, sourceType, source, entries);

    public static SavedPlaylist FromEntries(
        string name,
        string sourceType,
        string source,
        IReadOnlyList<PlaylistEntry> entries,
        IReadOnlyDictionary<string, SavedPlaylistOrigin>? originInfoByVideoId) =>
        PlaylistSnapshotBuilder.FromEntries(name, sourceType, source, entries, originInfoByVideoId);

    public static IReadOnlyList<PlaylistEntry> ToEntries(SavedPlaylist playlist) =>
        PlaylistSnapshotBuilder.ToEntries(playlist);
}

