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

            var ffprobe = TryGetFfprobePath(ffmpegPath);
            if (string.IsNullOrWhiteSpace(ffprobe))
                return null;

            var psi = new ProcessStartInfo
            {
                FileName = ffprobe,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                Arguments = $"-v error -print_format json -show_entries format=duration:format_tags=title,artist,album_artist -i \"{filePath}\""
            };

            using var proc = Process.Start(psi);
            if (proc is null)
                return null;

            ChildToolProcessJob.TryAssign(proc);
            // Read output without blocking UI; enforce timeout/cancellation.
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

            var stdoutBytes = await readOutBytes;
            var errBytes = await readErrBytes;
            var errText = DecodeBestEffort(errBytes);
            AppLog.ToolStderrCompleted("ffprobe", errText, proc.ExitCode);

            var stdout = DecodeBestEffort(stdoutBytes);
            if (string.IsNullOrWhiteSpace(stdout))
                return null;

            using var doc = JsonDocument.Parse(stdout, SafeJson.CreateDocumentOptions());
            if (!doc.RootElement.TryGetProperty("format", out var format))
                return null;
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

            int? durationSeconds = null;
            try
            {
                if (format.TryGetProperty("duration", out var durEl))
                {
                    var ds = durEl.ValueKind == JsonValueKind.String ? durEl.GetString() : durEl.ToString();
                    if (double.TryParse(ds, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var dsec))
                    {
                        var rounded = (int)Math.Round(dsec);
                        if (rounded > 0)
                            durationSeconds = rounded;
                    }
                }
            }
            catch { /* ignore */ }

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

    private static bool LooksMojibake(string? s)
        => !string.IsNullOrWhiteSpace(s) && s.Contains('\uFFFD');

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
            await s.CopyToAsync(ms, 16 * 1024, ct);
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

