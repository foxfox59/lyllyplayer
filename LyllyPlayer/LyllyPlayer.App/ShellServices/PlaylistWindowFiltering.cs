using System;
using LyllyPlayer.Models;

namespace LyllyPlayer.ShellServices;

public static class PlaylistWindowFiltering
{
    public static bool MatchesPlaylistFilterTokens(string query, QueueItem qi)
    {
        // Hot path for large playlists: this predicate is evaluated for every row on refresh.
        // Use cached, pre-lowercased haystack per item to avoid repeated string allocations.
        var haystack = qi.GetSearchHaystackLower();
        foreach (var token in query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (token.Length == 0)
                continue;
            if (!haystack.Contains(token.ToLowerInvariant(), StringComparison.Ordinal))
                return false;
        }

        return true;
    }
}

