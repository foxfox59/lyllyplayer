using System.IO;
using System.Text;

using LyllyPlayer.Models;

namespace LyllyPlayer.Utils;

public static class M3uPlaylistFile
{
    public sealed record ExportOptions(
        bool IncludeYoutube,
        bool PreferRelativePaths,
        bool IncludeLyllyMetadata
    );

    public static void Save(
        string path,
        string playlistName,
        IReadOnlyList<PlaylistEntry> entries,
        ExportOptions options,
        IReadOnlyDictionary<string, SavedPlaylistOrigin>? originInfoByVideoId = null
    )
    {
        var exportDir = "";
        try { exportDir = Path.GetDirectoryName(path) ?? ""; } catch { exportDir = ""; }

        var sb = new StringBuilder(Math.Max(1024, entries.Count * 64));
        sb.AppendLine("#EXTM3U");

        // Global metadata (comments only; ignored by standard readers).
        if (options.IncludeLyllyMetadata)
        {
            sb.Append("#LYLLY:NAME=").Append(E(playlistName)).AppendLine();
            sb.Append("#LYLLY:EXPORTED_UTC=").Append(E(DateTime.UtcNow.ToString("O"))).AppendLine();
        }

        foreach (var e in entries)
        {
            if (e is null) continue;

            var isLocal = e.VideoId.StartsWith("local:", StringComparison.OrdinalIgnoreCase);
            var isYoutube = !isLocal && (e.WebpageUrl?.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) == true ||
                                         e.WebpageUrl?.Contains("youtu.be", StringComparison.OrdinalIgnoreCase) == true);

            if (isYoutube && !options.IncludeYoutube)
                continue;

            var uriOrPath = (e.WebpageUrl ?? "").Trim();
            if (string.IsNullOrWhiteSpace(uriOrPath))
                continue;

            // Prefer relative paths for local files under the export folder.
            if (options.PreferRelativePaths && isLocal)
            {
                try
                {
                    if (Path.IsPathRooted(uriOrPath) &&
                        !string.IsNullOrWhiteSpace(exportDir) &&
                        uriOrPath.StartsWith(exportDir, StringComparison.OrdinalIgnoreCase))
                    {
                        uriOrPath = Path.GetRelativePath(exportDir, uriOrPath);
                    }
                }
                catch { /* ignore */ }
            }

            if (options.IncludeLyllyMetadata)
            {
                sb.Append("#LYLLY:VIDEOID=").Append(E(e.VideoId)).AppendLine();
                sb.Append("#LYLLY:URL=").Append(E(e.WebpageUrl ?? "")).AppendLine();
                if (originInfoByVideoId is not null && originInfoByVideoId.TryGetValue(e.VideoId, out var origin))
                {
                    sb.Append("#LYLLY:ORIGIN_LABEL=").Append(E(origin.Label)).AppendLine();
                    sb.Append("#LYLLY:ORIGIN_SOURCE=").Append(E(origin.Source)).AppendLine();
                }
            }

            var dur = e.DurationSeconds is int d && d > 0 ? d : -1;
            var title = MakeExtInfTitle(e);
            sb.Append("#EXTINF:").Append(dur).Append(',').Append(title).AppendLine();
            sb.AppendLine(uriOrPath);
        }

        // M3U8: UTF-8 without BOM is the safest default.
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static string MakeExtInfTitle(PlaylistEntry e)
    {
        var t = (e.Title ?? "").Trim();
        var c = (e.Channel ?? "").Trim();
        var display = string.IsNullOrWhiteSpace(c) ? t : $"{t} — {c}";
        display = display.Replace("\r", " ").Replace("\n", " ").Trim();
        return string.IsNullOrWhiteSpace(display) ? "Unknown" : display;
    }

    private static string E(string s)
    {
        // Keep it simple and safe for comments: single-line + URL-escaped.
        var t = (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        return Uri.EscapeDataString(t);
    }
}

