using System;
using System.Collections.Generic;
using LyllyPlayer.Models;

namespace LyllyPlayer.ShellServices;

public static class PlaylistWindowSorting
{
    public sealed class SortChoice
    {
        public PlaylistSortMode Mode { get; }
        public string Label { get; }

        public SortChoice(PlaylistSortMode mode, string label)
        {
            Mode = mode;
            Label = label;
        }

        public override string ToString() => Label;
    }

    public static IReadOnlyList<SortChoice> BuildSortChoices(bool isYoutube)
    {
        return new List<SortChoice>
        {
            new(PlaylistSortMode.None, "None"),
            new(PlaylistSortMode.NameOrTitle, isYoutube ? "Title" : "Name"),
            new(PlaylistSortMode.ChannelOrPath, "Source"),
            new(PlaylistSortMode.Duration, "Duration"),
        };
    }

    public static PlaylistSortSpec CoerceSpec(PlaylistSortSpec spec)
    {
        try { return spec; }
        catch { return new PlaylistSortSpec(PlaylistSortMode.None, PlaylistSortDirection.Asc); }
    }
}

