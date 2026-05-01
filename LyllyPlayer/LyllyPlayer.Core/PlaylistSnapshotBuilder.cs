using LyllyPlayer.Models;

namespace LyllyPlayer.Services;

/// <summary>
/// Builds <see cref="SavedPlaylist"/> snapshots and converts them back to <see cref="PlaylistEntry"/> lists.
/// File I/O stays in the app layer (<c>SavedPlaylistFile</c>, <c>LastPlaylistSnapshotStore</c>).
/// </summary>
public static class PlaylistSnapshotBuilder
{
    public static SavedPlaylist FromEntries(string name, string sourceType, string source, IReadOnlyList<PlaylistEntry> entries) =>
        FromEntries(name, sourceType, source, entries, originInfoByVideoId: null);

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
