using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Linq;

namespace LyllyPlayer.Utils;

public static class LocalMetadataService
{
    public sealed record LocalInfo(string? Title, string? Artist, int? DurationSeconds);

    public static async Task<LocalInfo?> TryGetInfoAsync(string ffmpegPath, string filePath, CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return null;

            var lower = filePath.ToLowerInvariant();
            var isAudioWithTagLib = lower.EndsWith(".mp3")
                || lower.EndsWith(".flac")
                || lower.EndsWith(".m4a")
                || lower.EndsWith(".aac")
                || lower.EndsWith(".ogg")
                || lower.EndsWith(".opus");

            string? title = null;
            string? artist = null;

            // For ID3/Opus/Vorbis comment audio formats, use TagLibSharp as the primary tag reader.
            if (isAudioWithTagLib)
            {
                var (t2, a2) = TryReadWithTagLibSharp(filePath);
                if (!string.IsNullOrWhiteSpace(t2) || !string.IsNullOrWhiteSpace(a2))
                {
                    title = t2;
                    artist = a2;
                }
            }

            int? durationSeconds = null;
            var ffprobe = TryGetFfprobePath(ffmpegPath);
            bool ffprobeOk = false;

            if (!string.IsNullOrWhiteSpace(ffprobe))
            {
                ffprobeOk = await TryGetDurationAsync(ffprobe, filePath, ct).ContinueWith(t =>
                {
                    if (t.IsCompletedSuccessfully && t.Result is int dur)
                        durationSeconds = dur;
                    return t.IsCompletedSuccessfully;
                }, ct).ConfigureAwait(false);

                // If we have no tags yet, try ffprobe format+stream-level tags as a supplement.
                if ((string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(artist))
                    && !string.IsNullOrWhiteSpace(ffprobe))
                {
                    var (ft, fa) = TryGetTagsFromFfprobe(ffprobe, filePath);
                    if (!string.IsNullOrWhiteSpace(ft)) title = ft;
                    if (!string.IsNullOrWhiteSpace(fa)) artist = fa;
                }
            }

            if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(artist) && durationSeconds is null)
                return null;

            // If ffprobe produced replacement characters, try a managed tag reader.
            if (LooksMojibake(title) || LooksMojibake(artist))
            {
                try
                {
                    var (t2, a2) = TryReadWithTagLibSharp(filePath);
                    if (!string.IsNullOrWhiteSpace(t2)) title = t2;
                    if (!string.IsNullOrWhiteSpace(a2)) artist = a2;
                }
                catch { /* ignore */ }
            }

            return new LocalInfo(title, artist, durationSeconds);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<int?> TryGetDurationAsync(string ffprobe, string filePath, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffprobe,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                Arguments = $"-v error -print_format json -show_entries format=duration -i \"{filePath}\""
            };

            using var proc = Process.Start(psi);
            if (proc is null)
                return null;

            ChildToolProcessJob.TryAssign(proc);
            var readOutBytes = ReadAllBytesAsync(proc.StandardOutput.BaseStream, ct);
            var readErrBytes = ReadAllBytesAsync(proc.StandardError.BaseStream, ct);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(450));

            try
            {
                await proc.WaitForExitAsync(timeoutCts.Token);
            }
            catch
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return null;
            }

            var stdout = DecodeBestEffort(await readOutBytes);
            if (string.IsNullOrWhiteSpace(stdout))
                return null;

            using var doc = JsonDocument.Parse(stdout, SafeJson.CreateDocumentOptions());
            if (!doc.RootElement.TryGetProperty("format", out var format))
                return null;

            if (format.TryGetProperty("duration", out var durEl))
            {
                var ds = durEl.ValueKind == JsonValueKind.String ? durEl.GetString() : durEl.ToString();
                if (double.TryParse(ds, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var dsec))
                {
                    var rounded = (int)Math.Round(dsec);
                    if (rounded > 0)
                        return rounded;
                    if (rounded > 0) return rounded;
                }
            }
            return default;
        }
        catch
        {
            return default;
        }
    }

    private static (string? title, string? artist) TryGetTagsFromFfprobe(string ffprobe, string filePath)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffprobe,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                Arguments = $"-v error -print_format json -show_entries format_tags=title,artist,album_artist:stream_tags=title,artist,album_artist -i \"{filePath}\""
            };

            using var proc = Process.Start(psi);
            if (proc is null)
                return (null, null);

            ChildToolProcessJob.TryAssign(proc);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
            timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(450));

            var stdout = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();

            if (string.IsNullOrWhiteSpace(stdout))
                return (null, null);

            using var doc = JsonDocument.Parse(stdout, SafeJson.CreateDocumentOptions());
            if (!doc.RootElement.TryGetProperty("format", out var format))
                return (null, null);

            format.TryGetProperty("tags", out var tags);

            static string? TryGetTag(JsonElement tags, string name)
            {
                foreach (var prop in tags.EnumerateObject())
                {
                    if (!string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
                        continue;
                    var s = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() : prop.Value.ToString();
                    s = (s ?? "").Trim();
                    return string.IsNullOrWhiteSpace(s) ? null : s;
                }
                return null;
            }

            string? title = null;
            string? artist = null;
            if (tags.ValueKind != JsonValueKind.Undefined && tags.ValueKind != JsonValueKind.Null)
            {
                title = TryGetTag(tags, "title");
                artist = TryGetTag(tags, "artist") ?? TryGetTag(tags, "album_artist");
            }

            // If format-level tags didn't yield results, check stream-level tags.
            if ((string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist))
                && doc.RootElement.TryGetProperty("streams", out var streams))
            {
                foreach (var stream in streams.EnumerateArray())
                {
                    if (stream.TryGetProperty("tags", out var streamTags)
                        && streamTags.ValueKind != JsonValueKind.Undefined
                        && streamTags.ValueKind != JsonValueKind.Null)
                    {
                        if (string.IsNullOrWhiteSpace(title))
                            title = TryGetTag(streamTags, "title");
                        if (string.IsNullOrWhiteSpace(artist))
                            artist = TryGetTag(streamTags, "artist") ?? TryGetTag(streamTags, "album_artist");
                    }

                    if (!string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(artist))
                        break;
                }
            }

            return (title, artist);
        }
        catch
        {
            return (null, null);
        }
    }

    private static bool LooksMojibake(string? s)
        => !string.IsNullOrWhiteSpace(s) && s.Contains('\uFFFD');

    /// <summary>
    /// Synchronously reads title, artist, and duration from a file.
    /// TagLibSharp for tags + ffprobe for duration — all on the calling thread.
    /// </summary>
    public static (string? Title, string? Artist, int? DurationSeconds) ReadTagsSync(string ffmpegPath, string filePath)
    {
        try
        {
            using var f = TagLib.File.Create(filePath);
            var (title, artist) = ReadTagLibTitleArtist(f);

            // Read duration synchronously via ffprobe (fast, ~100ms)
            int? durationSeconds = ReadDurationSync(ffmpegPath, filePath);

            // Fallback: TagLib can often compute duration even when ffprobe fails (corrupt headers, odd MP3 layout, etc).
            if (durationSeconds is null)
            {
                try
                {
                    var ts = f.Properties?.Duration;
                    if (ts is { } d && d.TotalSeconds > 0.5)
                        durationSeconds = (int)Math.Round(d.TotalSeconds);
                }
                catch { /* ignore */ }
            }

            return (title, artist, durationSeconds);
        }
        catch
        {
            return default;
        }
    }

    /// <summary>Synchronously reads file duration via ffprobe (returns seconds as int).</summary>
    private static int? ReadDurationSync(string ffmpegPath, string filePath)
    {
        try
        {
            if (!File.Exists(filePath)) return default;
            var ffprobe = TryGetFfprobePath(ffmpegPath);
            if (string.IsNullOrWhiteSpace(ffprobe)) return default;

            var psi = new ProcessStartInfo
            {
                FileName = ffprobe,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                Arguments = $"-v error -print_format json -show_entries format=duration -i \"{filePath}\""
            };

            using var proc = Process.Start(psi);
            if (proc is null) return default;

            // Read both stdout and stderr concurrently using background tasks.
            // Without this, the stderr pipe buffer can fill up, causing ffprobe to
            // block indefinitely, which in turn hangs ReadToEnd() on stdout.
            var readStdoutTask = Task.Run(() => proc.StandardOutput.ReadToEnd());
            var readStderrTask = Task.Run(() => proc.StandardError.ReadToEnd());

            // ffprobe can hang on some malformed media. Cap runtime so duration probing can't stall enrichment forever.
            const int timeoutMs = 1500;
            var exited = proc.WaitForExit(timeoutMs);
            if (!exited)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return default;
            }

            // Drain output after exit (should be quick).
            if (!Task.WaitAll(new Task[] { readStdoutTask, readStderrTask }, millisecondsTimeout: 250))
                return default;

            var stdout = readStdoutTask.Result;

            if (string.IsNullOrWhiteSpace(stdout)) return null;

            using var doc = JsonDocument.Parse(stdout, SafeJson.CreateDocumentOptions());
            if (doc.RootElement.TryGetProperty("format", out var format)
                && format.TryGetProperty("duration", out var durEl))
            {
                var ds = durEl.ValueKind == JsonValueKind.String ? durEl.GetString() : durEl.ToString();
                if (double.TryParse(ds, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var dsec))
                {
                    var rounded = (int)Math.Round(dsec);
                    if (rounded > 0) return rounded;
                }
            }
            return default;
        }
        catch
        {
            return default;
        }
    }

    private static (string? title, string? artist) TryReadWithTagLibSharp(string path)
    {
        try
        {
            using var f = TagLib.File.Create(path);
            return ReadTagLibTitleArtist(f);
        }
        catch
        {
            return (null, null);
        }
    }

    private static (string? title, string? artist) ReadTagLibTitleArtist(TagLib.File f)
    {
        var title = (f.Tag?.Title ?? "").Trim();
        if (string.IsNullOrWhiteSpace(title)) title = null;

        var performers = f.Tag?.Performers ?? Array.Empty<string>();
        var joined = string.Join(", ", performers.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()));
        var artist = string.IsNullOrWhiteSpace(joined) ? null : joined;

        if (string.IsNullOrWhiteSpace(artist))
        {
            var albumArtists = f.Tag?.AlbumArtists ?? Array.Empty<string>();
            var aj = string.Join(", ", albumArtists.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()));
            artist = string.IsNullOrWhiteSpace(aj) ? null : aj;
        }

        if (LooksMojibake(title)) title = null;
        if (LooksMojibake(artist)) artist = null;

        return (title, artist);
    }

    private static async Task<byte[]> ReadAllBytesAsync(Stream s, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        try
        {
            await s.CopyToAsync(ms, 16 * 128, ct);
        }
        catch
        {
            // ignore
        }
        return ms.ToArray();
    }

    private static string DecodeBestEffort(byte[] bytes)
    {
        if (bytes is null || bytes.Length == 0)
            return "";

        // 1) Strict UTF-8 (ffprobe should emit UTF-8 JSON normally)
        try
        {
            var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            return strictUtf8.GetString(bytes);
        }
        catch
        {
            // fall through
        }

        // 2) Windows-1252 is a common "ANSI" fallback for broken tags on Windows
        try
        {
            return Encoding.GetEncoding(1252).GetString(bytes);
        }
        catch
        {
            // fall through
        }

        // 3) ISO-8859-1 as last resort
        try
        {
            return Encoding.GetEncoding("ISO-8859-1").GetString(bytes);
        }
        catch
        {
            return Encoding.UTF8.GetString(bytes);
        }
    }

    private static string? TryGetFfprobePath(string ffmpegPath)
    {
        try
        {
            var s = (ffmpegPath ?? "").Trim();
            if (string.IsNullOrWhiteSpace(s))
                return "ffprobe";

            if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && (s.Contains('\\') || s.Contains('/')))
            {
                try
                {
                    var dir = Path.GetDirectoryName(s);
                    if (!string.IsNullOrWhiteSpace(dir))
                    {
                        var candidate = Path.Combine(dir, "ffprobe.exe");
                        if (File.Exists(candidate))
                            return candidate;
                    }
                }
                catch { /* ignore */ }
            }

            return "ffprobe";
        }
        catch
        {
            return "ffprobe";
        }
    }
}
