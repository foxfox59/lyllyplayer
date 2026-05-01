using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LyllyPlayer.Models;
using LyllyPlayer.Utils;

namespace LyllyPlayer.ShellServices;

public enum PlaylistSaveFormat
{
    M3U,
    SavedPlaylistJson,
}

public readonly record struct PlaylistSaveOutcome(PlaylistSaveFormat Format, string FileName);

/// <summary>
/// Narrow persistence service for playlist file formats (SavedPlaylist JSON + M3U/M3U8 export).
/// </summary>
public sealed class PlaylistFileService
{
    public PlaylistSaveOutcome SavePlaylist(
        string path,
        string playlistName,
        string sourceType,
        string source,
        IReadOnlyList<PlaylistEntry> entries,
        IReadOnlyDictionary<string, SavedPlaylistOrigin>? originInfoByVideoId,
        bool exportM3uIncludeYoutube,
        bool exportM3uPreferRelativePaths,
        bool exportM3uIncludeLyllyMetadata)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path is required.", nameof(path));

        var ext = (Path.GetExtension(path) ?? "").Trim().ToLowerInvariant();
        var fileName = "";
        try { fileName = Path.GetFileName(path); } catch { fileName = path; }

        if (ext is ".m3u" or ".m3u8")
        {
            M3uPlaylistFile.Save(
                path,
                playlistName,
                entries,
                new M3uPlaylistFile.ExportOptions(
                    IncludeYoutube: exportM3uIncludeYoutube,
                    PreferRelativePaths: exportM3uPreferRelativePaths,
                    IncludeLyllyMetadata: exportM3uIncludeLyllyMetadata),
                originInfoByVideoId: originInfoByVideoId);

            return new PlaylistSaveOutcome(PlaylistSaveFormat.M3U, fileName);
        }

        var pl = SavedPlaylistFile.FromEntries(
            playlistName,
            sourceType,
            source,
            entries,
            originInfoByVideoId: originInfoByVideoId);
        SavedPlaylistFile.Save(path, pl);
        return new PlaylistSaveOutcome(PlaylistSaveFormat.SavedPlaylistJson, fileName);
    }

    public SavedPlaylistFileLoadResult LoadSavedPlaylist(string path) => SavedPlaylistFile.TryLoadPlaylist(path);

    public static IReadOnlyDictionary<string, SavedPlaylistOrigin> BuildOriginInfoByVideoId(
        IReadOnlyDictionary<string, PlaylistOriginInfo> originByVideoId)
    {
        if (originByVideoId.Count == 0)
            return new Dictionary<string, SavedPlaylistOrigin>(StringComparer.OrdinalIgnoreCase);

        // Persist only the label + source that are part of SavedPlaylist format.
        return originByVideoId.ToDictionary(
            k => k.Key,
            v => new SavedPlaylistOrigin(v.Value.Label, v.Value.Source),
            StringComparer.OrdinalIgnoreCase);
    }
}

