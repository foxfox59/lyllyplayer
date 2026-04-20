using System.IO;
using System.Text.Json;
using LyllyPlayer.Models;

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

    public static SavedPlaylist FromEntries(string name, string sourceType, string source, IReadOnlyList<PlaylistEntry> entries)
    {
        return FromEntries(name, sourceType, source, entries, originInfoByVideoId: null);
    }

    public static SavedPlaylist FromEntries(
        string name,
        string sourceType,
        string source,
        IReadOnlyList<PlaylistEntry> entries,
        IReadOnlyDictionary<string, SavedPlaylistOrigin>? originInfoByVideoId)
    {
        name = string.IsNullOrWhiteSpace(name) ? "Playlist" : name.Trim();
        source = string.IsNullOrWhiteSpace(source) ? "" : source.Trim();
        var id = Guid.NewGuid().ToString("N");
        var created = DateTime.UtcNow;
        var list = new List<SavedPlaylistEntry>();
        foreach (var e in entries ?? Array.Empty<PlaylistEntry>())
        {
            if (e is null)
                continue;
            if (string.IsNullOrWhiteSpace(e.VideoId))
                continue;
            list.Add(new SavedPlaylistEntry(
                VideoId: e.VideoId,
                Title: e.Title ?? "",
                Channel: e.Channel,
                Url: e.WebpageUrl ?? ""
            ));
        }

        IReadOnlyDictionary<string, SavedPlaylistOrigin>? originInfos = null;
        try
        {
            if (originInfoByVideoId is not null && originInfoByVideoId.Count > 0)
            {
                // Only persist origins for entries present in the file, and only when it differs from base.
                var baseName = (name ?? "").Trim();
                var baseSource = (source ?? "").Trim();
                var dict = new Dictionary<string, SavedPlaylistOrigin>(StringComparer.OrdinalIgnoreCase);
                foreach (var e in list)
                {
                    if (originInfoByVideoId.TryGetValue(e.VideoId, out var o) && o is not null)
                    {
                        var label = (o.Label ?? "").Trim();
                        var src = (o.Source ?? "").Trim();
                        var labelDiffers = !string.IsNullOrWhiteSpace(label) && !string.Equals(label, baseName, StringComparison.OrdinalIgnoreCase);
                        var sourceDiffers = !string.IsNullOrWhiteSpace(src) && !string.Equals(src, baseSource, StringComparison.OrdinalIgnoreCase);
                        if (labelDiffers || sourceDiffers)
                            dict[e.VideoId] = new SavedPlaylistOrigin(label, src);
                    }
                }
                originInfos = dict.Count > 0 ? dict : null;
            }
        }
        catch
        {
            originInfos = null;
        }

        return new SavedPlaylist(
            Id: id,
            Name: name ?? "Playlist",
            CreatedUtc: created,
            SourceType: sourceType,
            Source: source ?? "",
            Entries: list,
            OriginByVideoId: null,
            OriginInfoByVideoId: originInfos
        );
    }

    public static IReadOnlyList<PlaylistEntry> ToEntries(SavedPlaylist playlist)
    {
        var list = new List<PlaylistEntry>();
        foreach (var e in playlist.Entries ?? Array.Empty<SavedPlaylistEntry>())
        {
            if (e is null)
                continue;
            if (string.IsNullOrWhiteSpace(e.VideoId))
                continue;
            var url = string.IsNullOrWhiteSpace(e.Url) ? $"https://www.youtube.com/watch?v={e.VideoId}" : e.Url;
            list.Add(new PlaylistEntry(
                VideoId: e.VideoId,
                Title: string.IsNullOrWhiteSpace(e.Title) ? "(untitled)" : e.Title,
                Channel: e.Channel,
                DurationSeconds: null,
                WebpageUrl: url
            ));
        }
        return list;
    }
}

