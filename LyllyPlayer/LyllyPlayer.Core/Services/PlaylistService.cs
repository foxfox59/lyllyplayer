using System.Linq;
using LyllyPlayer.Models;

namespace LyllyPlayer.Services;

/// <summary>
/// In-memory playlist catalog (ordered entries + per-item origins). UI collections remain in the WPF layer.
/// </summary>
public sealed class PlaylistService
{
    private readonly List<string> _baseOrderVideoIds = new();

    public List<PlaylistEntry> Entries { get; } = new();

    public Dictionary<string, PlaylistOriginInfo> OriginByVideoId { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    public PlaylistOriginInfo? BaseOrigin { get; set; }

    /// <summary>
    /// Stable import/load order. Sort mutates <see cref="Entries"/> only; <see cref="PlaylistSortMode.None"/> restores this.
    /// </summary>
    public IReadOnlyList<string> BaseOrderVideoIds => _baseOrderVideoIds;

    public void Clear()
    {
        Entries.Clear();
        OriginByVideoId.Clear();
        BaseOrigin = null;
        _baseOrderVideoIds.Clear();
    }

    /// <summary>Replace catalog and treat the new sequence as the base (import/load) order.</summary>
    public void ReplaceEntries(IEnumerable<PlaylistEntry> newOrder)
    {
        Entries.Clear();
        Entries.AddRange(newOrder);
        CaptureBaseOrderFromEntries();
    }

    /// <summary>Reorder the catalog without changing the preserved base/import order.</summary>
    public void ReorderEntries(IEnumerable<PlaylistEntry> newOrder)
    {
        Entries.Clear();
        Entries.AddRange(newOrder);
        PruneBaseOrderToCurrentEntries();
    }

    /// <summary>Restore <see cref="Entries"/> to <see cref="BaseOrderVideoIds"/>. Returns false if unchanged/unavailable.</summary>
    public bool TryRestoreBaseOrder()
    {
        if (_baseOrderVideoIds.Count == 0 || Entries.Count <= 1)
            return false;

        var indexById = new Dictionary<string, int>(_baseOrderVideoIds.Count, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < _baseOrderVideoIds.Count; i++)
        {
            var id = _baseOrderVideoIds[i];
            if (string.IsNullOrWhiteSpace(id) || indexById.ContainsKey(id))
                continue;
            indexById[id] = i;
        }

        if (indexById.Count == 0)
            return false;

        // Stable sort: known ids follow base order; duplicates keep relative order; unknowns stay at the end.
        var decorated = new (PlaylistEntry Entry, int Orig)[Entries.Count];
        for (var i = 0; i < Entries.Count; i++)
            decorated[i] = (Entries[i], i);

        Array.Sort(decorated, (a, b) =>
        {
            var aId = a.Entry?.VideoId;
            var bId = b.Entry?.VideoId;
            var aKnown = !string.IsNullOrWhiteSpace(aId) && indexById.TryGetValue(aId, out var ai);
            var bKnown = !string.IsNullOrWhiteSpace(bId) && indexById.TryGetValue(bId, out var bi);
            if (aKnown && bKnown)
            {
                // TryGetValue assigned ai/bi when both known.
                var cmp = indexById[aId!].CompareTo(indexById[bId!]);
                return cmp != 0 ? cmp : a.Orig.CompareTo(b.Orig);
            }

            if (aKnown != bKnown)
                return aKnown ? -1 : 1;
            return a.Orig.CompareTo(b.Orig);
        });

        var changed = false;
        for (var i = 0; i < decorated.Length; i++)
        {
            if (!ReferenceEquals(Entries[i], decorated[i].Entry))
            {
                changed = true;
                break;
            }
        }

        if (!changed)
            return false;

        Entries.Clear();
        foreach (var item in decorated)
            Entries.Add(item.Entry);
        return true;
    }

    /// <summary>
    /// Replace the preserved base order (e.g. from a saved snapshot). Unknown ids are ignored;
    /// entries missing from the list are appended in current catalog order.
    /// </summary>
    public void SetBaseOrderVideoIds(IEnumerable<string>? videoIds)
    {
        _baseOrderVideoIds.Clear();
        if (videoIds is null)
        {
            CaptureBaseOrderFromEntries();
            return;
        }

        var present = new HashSet<string>(
            Entries.Where(e => e is not null && !string.IsNullOrWhiteSpace(e.VideoId)).Select(e => e.VideoId),
            StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in videoIds)
        {
            if (string.IsNullOrWhiteSpace(id) || !present.Contains(id) || !seen.Add(id))
                continue;
            _baseOrderVideoIds.Add(id);
        }

        foreach (var e in Entries)
        {
            if (e is null || string.IsNullOrWhiteSpace(e.VideoId) || !seen.Add(e.VideoId))
                continue;
            _baseOrderVideoIds.Add(e.VideoId);
        }
    }

    /// <summary>Append entries optionally skipping duplicates by VideoId (case-insensitive).</summary>
    public void AppendEntries(IEnumerable<PlaylistEntry> incoming, bool removeDuplicates)
    {
        foreach (var e in incoming)
        {
            if (removeDuplicates && Entries.Exists(x => string.Equals(x.VideoId, e.VideoId, StringComparison.OrdinalIgnoreCase)))
                continue;
            Entries.Add(e);
            if (!string.IsNullOrWhiteSpace(e.VideoId) &&
                !_baseOrderVideoIds.Exists(id => string.Equals(id, e.VideoId, StringComparison.OrdinalIgnoreCase)))
                _baseOrderVideoIds.Add(e.VideoId);
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
        PruneBaseOrderToCurrentEntries();
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
     /// Returns a snapshot even when empty (so it can overwrite old last-playlist.json).
    /// </summary>
    public SavedPlaylist? BuildSavedPlaylistSnapshot(string name, string sourceType, string source)
    {
        var originDict = OriginByVideoId.ToDictionary(
            k => k.Key,
            v => new SavedPlaylistOrigin(v.Value.Label, v.Value.Source),
            StringComparer.OrdinalIgnoreCase);

        return PlaylistSnapshotBuilder.FromEntries(
            name,
            sourceType,
            source,
            Entries.ToList(),
            originDict,
            _baseOrderVideoIds.Count > 0 ? _baseOrderVideoIds.ToList() : null);
    }

    private void CaptureBaseOrderFromEntries()
    {
        _baseOrderVideoIds.Clear();
        foreach (var e in Entries)
        {
            if (e is null || string.IsNullOrWhiteSpace(e.VideoId))
                continue;
            _baseOrderVideoIds.Add(e.VideoId);
        }
    }

    private void PruneBaseOrderToCurrentEntries()
    {
        if (_baseOrderVideoIds.Count == 0)
        {
            CaptureBaseOrderFromEntries();
            return;
        }

        var present = new HashSet<string>(
            Entries.Where(e => e is not null && !string.IsNullOrWhiteSpace(e.VideoId)).Select(e => e.VideoId),
            StringComparer.OrdinalIgnoreCase);
        _baseOrderVideoIds.RemoveAll(id => !present.Contains(id));

        var seen = new HashSet<string>(_baseOrderVideoIds, StringComparer.OrdinalIgnoreCase);
        foreach (var e in Entries)
        {
            if (e is null || string.IsNullOrWhiteSpace(e.VideoId) || !seen.Add(e.VideoId))
                continue;
            _baseOrderVideoIds.Add(e.VideoId);
        }
    }
}
