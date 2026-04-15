namespace LyllyPlayer.Models;

public sealed record SavedPlaylist(
    string Id,
    string Name,
    DateTime CreatedUtc,
    string SourceType,
    string Source,
    IReadOnlyList<SavedPlaylistEntry> Entries
);

public sealed record SavedPlaylistEntry(
    string VideoId,
    string Title,
    string? Channel,
    string Url
);

