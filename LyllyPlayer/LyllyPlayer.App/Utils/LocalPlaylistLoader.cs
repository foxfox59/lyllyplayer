using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Linq;
using System.Threading;
using LyllyPlayer.Models;

namespace LyllyPlayer.Utils;

public static class LocalPlaylistLoader
{
    /// <summary>Same ordering as Explorer “Name” column (numeric runs compare numerically).</summary>
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int StrCmpLogicalW(string? psz1, string? psz2);

    private static int CompareExplorerLogicalFileOrDirName(string fullPathA, string fullPathB)
    {
        try
        {
            static string LastSegment(string p)
            {
                var t = p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return Path.GetFileName(t);
            }

            return StrCmpLogicalW(LastSegment(fullPathA), LastSegment(fullPathB));
        }
        catch
        {
            return string.Compare(fullPathA, fullPathB, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static readonly HashSet<string> SupportedAudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".flac", ".m4a", ".aac", ".ogg", ".opus", ".wma", ".aiff", ".aif", ".aifc"
    };

    private const int MetadataLoadParallelism = 6;

    /// <summary>Fast path: no ffprobe; filenames only.</summary>
    public static List<PlaylistEntry> LoadFolder(string folder, bool includeSubfolders, string ffmpegPath, bool readMetadataOnLoad = false)
    {
        if (readMetadataOnLoad)
            throw new InvalidOperationException("Use LoadFolderAsync when readMetadataOnLoad is true (ffprobe-based).");

        return LoadFolderCoreSync(folder, includeSubfolders, CancellationToken.None);
    }

    public static async Task<List<PlaylistEntry>> LoadFolderAsync(
        string folder,
        bool includeSubfolders,
        string ffmpegPath,
        bool readMetadataOnLoad,
        CancellationToken ct = default,
        IProgress<(int done, int total)>? metadataProgress = null)
    {
        ct.ThrowIfCancellationRequested();
        if (!readMetadataOnLoad)
            return await Task.Run(() => LoadFolderCoreSync(folder, includeSubfolders, ct), ct).ConfigureAwait(false);

        var paths = EnumerateAudioFiles(folder, includeSubfolders, ct);
        if (paths.Count == 0)
            return new List<PlaylistEntry>();

        var total = paths.Count;
        var done = 0;
        using var sem = new SemaphoreSlim(MetadataLoadParallelism, MetadataLoadParallelism);
        async Task<PlaylistEntry> TrackAsync(string file)
        {
            var e = await MapFileWithMetadataAsync(file, ffmpegPath, sem, ct).ConfigureAwait(false);
            var d = Interlocked.Increment(ref done);
            try { metadataProgress?.Report((d, total)); } catch { /* ignore */ }
            return e;
        }

        var results = await Task.WhenAll(paths.Select(TrackAsync)).ConfigureAwait(false);
        return results.ToList();
    }

    /// <summary>
    /// Lists audio files for a folder load. Order is depth-first: files in each directory, then each immediate
    /// subdirectory (recursively when <paramref name="includeSubfolders"/>). Names use Explorer-style logical
    /// comparison (<c>StrCmpLogicalW</c>) so e.g. “40 …” sorts before “1000 …”.
    /// </summary>
    private static List<string> EnumerateAudioFiles(string folder, bool includeSubfolders, CancellationToken ct = default)
    {
        var results = new List<string>();
        try
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                return results;
            CollectAudioFilesDepthFirst(folder, includeSubfolders, results, ct);
        }
        catch
        {
            // ignore
        }

        return results;
    }

    private static void CollectAudioFilesDepthFirst(string directory, bool recurseIntoSubfolders, List<string> results, CancellationToken ct)
    {
        foreach (var file in ListSortedAudioFilesDirectlyInDirectory(directory, ct))
            results.Add(file);

        if (!recurseIntoSubfolders)
            return;

        string[] subdirs;
        try
        {
            subdirs = Directory.GetDirectories(directory);
        }
        catch
        {
            return;
        }

        if (subdirs.Length == 0)
            return;

        Array.Sort(subdirs, CompareExplorerLogicalFileOrDirName);
        foreach (var sub in subdirs)
        {
            ct.ThrowIfCancellationRequested();
            CollectAudioFilesDepthFirst(sub, true, results, ct);
        }
    }

    private static List<string> ListSortedAudioFilesDirectlyInDirectory(string directory, CancellationToken ct)
    {
        var acc = new List<string>();
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(directory);
        }
        catch
        {
            return acc;
        }

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var ext = Path.GetExtension(file);
                if (!SupportedAudioExtensions.Contains(ext))
                    continue;
                if (!File.Exists(file))
                    continue;
                acc.Add(file);
            }
            catch
            {
                // ignore
            }
        }

        acc.Sort(CompareExplorerLogicalFileOrDirName);
        return acc;
    }

    private static List<PlaylistEntry> LoadFolderCoreSync(string folder, bool includeSubfolders, CancellationToken ct = default)
    {
        var list = new List<PlaylistEntry>();
        foreach (var file in EnumerateAudioFiles(folder, includeSubfolders, ct))
        {
            try
            {
                var title = Path.GetFileNameWithoutExtension(file);
                list.Add(new PlaylistEntry(
                    VideoId: LocalIdFromPath(file),
                    Title: title,
                    Channel: null,
                    DurationSeconds: null,
                    WebpageUrl: file
                ));
            }
            catch
            {
                // ignore bad entries
            }
        }

        return list;
    }

    private static async Task<PlaylistEntry> MapFileWithMetadataAsync(
        string file,
        string ffmpegPath,
        SemaphoreSlim sem,
        CancellationToken ct)
    {
        var title = Path.GetFileNameWithoutExtension(file);
        string? artist = null;
        int? duration = null;

        await sem.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var info = await LocalMetadataService.TryGetInfoAsync(ffmpegPath, file, ct).ConfigureAwait(false);
            if (info is not null)
            {
                if (!string.IsNullOrWhiteSpace(info.Title))
                    title = info.Title!.Trim();
                artist = string.IsNullOrWhiteSpace(info.Artist) ? null : info.Artist.Trim();
                duration = info.DurationSeconds is > 0 ? info.DurationSeconds : null;
            }
        }
        catch
        {
            // keep filename title
        }
        finally
        {
            try { sem.Release(); } catch { /* ignore */ }
        }

        return new PlaylistEntry(
            VideoId: LocalIdFromPath(file),
            Title: title,
            Channel: artist,
            DurationSeconds: duration,
            WebpageUrl: file
        );
    }

    /// <summary>Fast path: no ffprobe.</summary>
    public static (List<PlaylistEntry> entries, string? title) LoadM3u(string m3uPath, string ffmpegPath, bool readMetadataOnLoad = false)
    {
        if (readMetadataOnLoad)
            throw new InvalidOperationException("Use LoadM3uAsync when readMetadataOnLoad is true (ffprobe-based).");

        return LoadM3uSyncNoMetadata(m3uPath, CancellationToken.None);
    }

    public static Task<(List<PlaylistEntry> entries, string? title)> LoadM3uAsync(
        string m3uPath,
        string ffmpegPath,
        bool readMetadataOnLoad,
        CancellationToken ct = default,
        IProgress<(int done, int total)>? metadataProgress = null)
    {
        ct.ThrowIfCancellationRequested();
        if (!readMetadataOnLoad)
            return Task.Run(() => LoadM3uSyncNoMetadata(m3uPath, ct), ct);

        return LoadM3uWithMetadataAsync(m3uPath, ffmpegPath, ct, metadataProgress);
    }

    private static async Task<(List<PlaylistEntry> entries, string? title)> LoadM3uWithMetadataAsync(
        string m3uPath,
        string ffmpegPath,
        CancellationToken ct,
        IProgress<(int done, int total)>? metadataProgress)
    {
        var (paths, titleOut) = CollectM3uAudioPaths(m3uPath, ct);
        if (paths.Count == 0)
            return (new List<PlaylistEntry>(), titleOut);

        var total = paths.Count;
        var done = 0;
        using var sem = new SemaphoreSlim(MetadataLoadParallelism, MetadataLoadParallelism);
        async Task<PlaylistEntry> TrackAsync(M3uAudioRow row)
        {
            var e = await MapM3uRowWithMetadataAsync(row, ffmpegPath, sem, ct).ConfigureAwait(false);
            var d = Interlocked.Increment(ref done);
            try { metadataProgress?.Report((d, total)); } catch { /* ignore */ }
            return e;
        }

        var entries = (await Task.WhenAll(paths.Select(TrackAsync)).ConfigureAwait(false)).ToList();
        return (entries, titleOut);
    }

    private sealed record M3uAudioRow(string Path, string DefaultTitle);

    private static (List<M3uAudioRow> paths, string? playlistTitle) CollectM3uAudioPaths(string m3uPath, CancellationToken ct = default)
    {
        var rows = new List<M3uAudioRow>();
        var dir = "";
        try { dir = Path.GetDirectoryName(m3uPath) ?? ""; } catch { dir = ""; }

        string? pendingTitle = null;
        foreach (var raw in File.ReadLines(m3uPath))
        {
            ct.ThrowIfCancellationRequested();
            var line = (raw ?? "").Trim();
            if (string.IsNullOrWhiteSpace(line))
                continue;

            if (line.StartsWith("#", StringComparison.Ordinal))
            {
                if (line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
                {
                    var comma = line.IndexOf(',');
                    if (comma >= 0 && comma + 1 < line.Length)
                        pendingTitle = line[(comma + 1)..].Trim();
                    else
                        pendingTitle = null;
                }
                continue;
            }

            var p = line;
            if (Uri.TryCreate(p, UriKind.Absolute, out var u) &&
                (string.Equals(u.Scheme, "http", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(u.Scheme, "https", StringComparison.OrdinalIgnoreCase)))
            {
                var title = !string.IsNullOrWhiteSpace(pendingTitle)
                    ? pendingTitle!
                    : u.Host;
                pendingTitle = null;
                rows.Add(new M3uAudioRow(Path: p, DefaultTitle: title));
                continue;
            }

            try
            {
                if (!Path.IsPathRooted(p) && !string.IsNullOrWhiteSpace(dir))
                    p = Path.GetFullPath(Path.Combine(dir, p));
            }
            catch
            {
                pendingTitle = null;
                continue;
            }

            try
            {
                var ext = Path.GetExtension(p);
                if (!SupportedAudioExtensions.Contains(ext))
                {
                    pendingTitle = null;
                    continue;
                }

                if (!File.Exists(p))
                {
                    pendingTitle = null;
                    continue;
                }

                var title = !string.IsNullOrWhiteSpace(pendingTitle)
                    ? pendingTitle!
                    : Path.GetFileNameWithoutExtension(p);
                pendingTitle = null;
                rows.Add(new M3uAudioRow(Path: p, DefaultTitle: title));
            }
            catch
            {
                pendingTitle = null;
            }
        }

        string? titleOut = null;
        try { titleOut = Path.GetFileNameWithoutExtension(m3uPath); } catch { /* ignore */ }
        return (rows, titleOut);
    }

    private static async Task<PlaylistEntry> MapM3uRowWithMetadataAsync(
        M3uAudioRow row,
        string ffmpegPath,
        SemaphoreSlim sem,
        CancellationToken ct)
    {
        if (Uri.TryCreate(row.Path, UriKind.Absolute, out var u) &&
            (string.Equals(u.Scheme, "http", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(u.Scheme, "https", StringComparison.OrdinalIgnoreCase)))
        {
            return new PlaylistEntry(
                VideoId: StreamIdFromUrl(row.Path),
                Title: row.DefaultTitle,
                Channel: null,
                DurationSeconds: null,
                WebpageUrl: row.Path
            );
        }

        var title = row.DefaultTitle;
        string? artist = null;
        int? duration = null;

        await sem.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var info = await LocalMetadataService.TryGetInfoAsync(ffmpegPath, row.Path, ct).ConfigureAwait(false);
            if (info is not null)
            {
                if (!string.IsNullOrWhiteSpace(info.Title))
                    title = info.Title!.Trim();
                artist = string.IsNullOrWhiteSpace(info.Artist) ? null : info.Artist.Trim();
                duration = info.DurationSeconds is > 0 ? info.DurationSeconds : null;
            }
        }
        catch
        {
            // keep defaults
        }
        finally
        {
            try { sem.Release(); } catch { /* ignore */ }
        }

        return new PlaylistEntry(
            VideoId: LocalIdFromPath(row.Path),
            Title: title,
            Channel: artist,
            DurationSeconds: duration,
            WebpageUrl: row.Path
        );
    }

    private static (List<PlaylistEntry> entries, string? title) LoadM3uSyncNoMetadata(string m3uPath, CancellationToken ct = default)
    {
        var entries = new List<PlaylistEntry>();
        var dir = "";
        try { dir = Path.GetDirectoryName(m3uPath) ?? ""; } catch { dir = ""; }

        string? pendingTitle = null;
        foreach (var raw in File.ReadLines(m3uPath))
        {
            ct.ThrowIfCancellationRequested();
            var line = (raw ?? "").Trim();
            if (string.IsNullOrWhiteSpace(line))
                continue;

            if (line.StartsWith("#", StringComparison.Ordinal))
            {
                if (line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
                {
                    var comma = line.IndexOf(',');
                    if (comma >= 0 && comma + 1 < line.Length)
                        pendingTitle = line[(comma + 1)..].Trim();
                    else
                        pendingTitle = null;
                }
                continue;
            }

            var p = line;
            if (Uri.TryCreate(p, UriKind.Absolute, out var u) &&
                (string.Equals(u.Scheme, "http", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(u.Scheme, "https", StringComparison.OrdinalIgnoreCase)))
            {
                var title = !string.IsNullOrWhiteSpace(pendingTitle)
                    ? pendingTitle!
                    : u.Host;
                pendingTitle = null;

                entries.Add(new PlaylistEntry(
                    VideoId: StreamIdFromUrl(p),
                    Title: title,
                    Channel: null,
                    DurationSeconds: null,
                    WebpageUrl: p
                ));
                continue;
            }

            try
            {
                if (!Path.IsPathRooted(p) && !string.IsNullOrWhiteSpace(dir))
                    p = Path.GetFullPath(Path.Combine(dir, p));
            }
            catch
            {
                pendingTitle = null;
                continue;
            }

            try
            {
                var ext = Path.GetExtension(p);
                if (!SupportedAudioExtensions.Contains(ext))
                {
                    pendingTitle = null;
                    continue;
                }

                if (!File.Exists(p))
                {
                    pendingTitle = null;
                    continue;
                }

                var title = !string.IsNullOrWhiteSpace(pendingTitle)
                    ? pendingTitle!
                    : Path.GetFileNameWithoutExtension(p);
                pendingTitle = null;

                entries.Add(new PlaylistEntry(
                    VideoId: LocalIdFromPath(p),
                    Title: title,
                    Channel: null,
                    DurationSeconds: null,
                    WebpageUrl: p
                ));
            }
            catch
            {
                pendingTitle = null;
            }
        }

        string? titleOut = null;
        try { titleOut = Path.GetFileNameWithoutExtension(m3uPath); } catch { /* ignore */ }
        return (entries, titleOut);
    }

    public static bool TryGetLocalPath(string? webpageUrlOrPath, out string path)
    {
        path = "";
        if (string.IsNullOrWhiteSpace(webpageUrlOrPath))
            return false;

        var s = webpageUrlOrPath.Trim();

        try
        {
            if (Uri.TryCreate(s, UriKind.Absolute, out var uri) &&
                uri.IsFile &&
                !string.IsNullOrWhiteSpace(uri.LocalPath))
            {
                var p = uri.LocalPath;
                if (File.Exists(p))
                {
                    path = p;
                    return true;
                }

                return false;
            }
        }
        catch
        {
            // ignore
        }

        try
        {
            if (File.Exists(s))
            {
                path = s;
                return true;
            }
        }
        catch
        {
            // ignore
        }

        return false;
    }

    private static string LocalIdFromPath(string path)
    {
        try
        {
            var normalized = Path.GetFullPath(path).ToLowerInvariant();
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
            var hex = Convert.ToHexString(bytes);
            var shortHex = hex.Length >= 12 ? hex[..12] : hex;
            var file = Path.GetFileNameWithoutExtension(path);
            file = string.IsNullOrWhiteSpace(file) ? "track" : file;
            return $"local:{file}:{shortHex}";
        }
        catch
        {
            return $"local:{Guid.NewGuid():N}";
        }
    }

    private static string StreamIdFromUrl(string url)
    {
        try
        {
            var normalized = (url ?? "").Trim();
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
            var hex = Convert.ToHexString(bytes);
            var shortHex = hex.Length >= 12 ? hex[..12] : hex;
            return $"stream:{shortHex}";
        }
        catch
        {
            return $"stream:{Guid.NewGuid():N}";
        }
    }
}
