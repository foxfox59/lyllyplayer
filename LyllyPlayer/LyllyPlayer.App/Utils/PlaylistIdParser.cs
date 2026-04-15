using System.Web;

namespace LyllyPlayer.Utils;

public static class PlaylistIdParser
{
    public static string? TryExtractPlaylistId(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        input = input.Trim();

        if (PlaylistSourcePathHeuristics.LooksLikeLocalFilesystemSource(input))
            return null;

        // Raw playlist ID (best-effort heuristic)
        if (!input.Contains("://") && !input.Contains("youtube", StringComparison.OrdinalIgnoreCase))
            return input;

        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri))
            return null;

        var query = HttpUtility.ParseQueryString(uri.Query);
        var list = query.Get("list");
        return string.IsNullOrWhiteSpace(list) ? null : list;
    }
}


