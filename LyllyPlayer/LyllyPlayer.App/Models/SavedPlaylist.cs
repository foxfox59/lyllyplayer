namespace LyllyPlayer.Models;

public sealed record SavedPlaylistOrigin(
    string Label,
    string Source
);

public sealed record SavedPlaylist(
    string Id,
    string Name,
    DateTime CreatedUtc,
    string SourceType,
    string Source,
    IReadOnlyList<SavedPlaylistEntry> Entries,
    /// <summary>
    /// Optional per-item origin label. Keyed by VideoId. Missing keys imply <see cref="Name"/>.
    /// Legacy (label only); kept for backward compatibility.
    /// </summary>
    IReadOnlyDictionary<string, string>? OriginByVideoId = null,
    /// <summary>
    /// Optional per-item origin info (label + source ID/URL/path). Keyed by VideoId.
    /// Missing keys imply (<see cref="Name"/>, <see cref="Source"/>).
    /// </summary>
    IReadOnlyDictionary<string, SavedPlaylistOrigin>? OriginInfoByVideoId = null
);

public sealed record SavedPlaylistEntry(
    string VideoId,
    string Title,
    string? Channel,
    string Url
);

