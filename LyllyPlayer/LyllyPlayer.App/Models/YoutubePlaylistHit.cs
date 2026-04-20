namespace LyllyPlayer.Models;

public sealed record YoutubePlaylistHit(
    string Title,
    string UrlOrId,
    string? Channel,
    int? ItemCount
);

