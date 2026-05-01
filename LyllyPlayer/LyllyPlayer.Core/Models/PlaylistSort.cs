namespace LyllyPlayer.Models;

public enum PlaylistSortMode
{
    None = 0,
    /// <summary>Title for YouTube, filename fallback for local.</summary>
    NameOrTitle = 1,
    /// <summary>Channel for YouTube, full path+filename for local.</summary>
    ChannelOrPath = 2,
    Duration = 3,
}

public enum PlaylistSortDirection
{
    Asc = 0,
    Desc = 1,
}

public readonly record struct PlaylistSortSpec(PlaylistSortMode Mode, PlaylistSortDirection Direction);
