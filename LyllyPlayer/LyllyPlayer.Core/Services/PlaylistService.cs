using System.Linq;
using LyllyPlayer.Models;

namespace LyllyPlayer.Services;

/// <summary>
/// In-memory playlist catalog (ordered entries + per-item origins). UI collections remain in the WPF layer.
/// </summary>
public sealed class PlaylistService
{
    public List<PlaylistEntry> Entries { get; } = new();

    public Dictionary<string, PlaylistOriginInfo> OriginByVideoId { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    public PlaylistOriginInfo? BaseOrigin { get; set; }

    public void Clear()
    {
        Entries.Clear();
        OriginByVideoId.Clear();
        BaseOrigin = null;
    }

    public void ReplaceEntries(IEnumerable<PlaylistEntry> newOrder)
    {
        Entries.Clear();
        Entries.AddRange(newOrder);
    }

    /// <summary>Append entries optionally skipping duplicates by VideoId (case-insensitive).</summary>
    public void AppendEntries(IEnumerable<PlaylistEntry> incoming, bool removeDuplicates)
    {
        foreach (var e in incoming)
        {
            if (removeDuplicates && Entries.Exists(x => string.Equals(x.VideoId, e.VideoId, StringComparison.OrdinalIgnoreCase)))
                continue;
            Entries.Add(e);
        }
    }

    /// <summary>Remove entries matching predicate; returns removed VideoIds.</summary>
    public IReadOnlyList<string> RemoveWhere(Func<PlaylistEntry, bool> predicate)
    {
        var removed = new List<string>();
        for (var i = Entries.Count - 1; i >= 0; i--)
        {
            if (!predicate(Entries[i])) continue;
            removed.Add(Entries[i].VideoId);
            Entries.RemoveAt(i);
        }
        foreach (var id in removed)
            OriginByVideoId.Remove(id);
        return removed;
    }

    /// <summary>Remove entries whose VideoId is in <paramref name="invalidVideoIds"/> (case-insensitive).</summary>
    public IReadOnlyList<string> RemoveInvalidEntries(IEnumerable<string> invalidVideoIds)
    {
        var set = new HashSet<string>(invalidVideoIds ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        return RemoveWhere(e => set.Contains(e.VideoId));
    }

    /// <summary>
    /// Snapshot of the current catalog for autosave / last-playlist persistence.
    /// Returns null when empty.
    /// </summary>
    public SavedPlaylist? BuildSavedPlaylistSnapshot(string name, string sourceType, string source)
    {
        if (Entries.Count == 0)
            return null;

        var originDict = OriginByVideoId.ToDictionary(
            k => k.Key,
            v => new SavedPlaylistOrigin(v.Value.Label, v.Value.Source),
            StringComparer.OrdinalIgnoreCase);

        return PlaylistSnapshotBuilder.FromEntries(
            name,
            sourceType,
            source,
            Entries.ToList(),
            originDict);
    }
}
