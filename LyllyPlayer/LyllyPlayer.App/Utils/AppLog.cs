using LyllyPlayer.Settings;
using System.IO;
using System.Text;

namespace LyllyPlayer.Utils;

/// <summary>Whether an INFO line is shown at the Basic log level (see <see cref="AppLog.SetLevel"/>).</summary>
public enum AppLogInfoTier
{
    /// <summary>Playback/cache/resolve decisions most users care about.</summary>
    Crucial,
    /// <summary>Diagnostics (timing, window bounds, per-retry detail) — Verbose only.</summary>
    Diagnostic,
}

public static class AppLog
{
    private static readonly object Gate = new();
    private static readonly object FileGate = new();
    private static readonly LinkedList<string> Buffer = new();
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private const int MaxLines = 500;
    /// <summary>Max characters of stderr included when a tool exits with failure (full blob for paste debugging).</summary>
    private const int MaxToolStderrBlobChars = 32768;

    public static event EventHandler? Changed;

    public static string LogPath
        => Path.Combine(Path.GetDirectoryName(SettingsStore.GetSettingsPath())!, "app.log");

    /// <summary>0 = errors/warnings only, 1 = basic INFO, 2 = all INFO.</summary>
    private static int _infoLevel;

    /// <summary>Soft cap for <see cref="LogPath"/>; older bytes are dropped (tail preserved).</summary>
    private static long _maxLogDiskBytes = (long)SettingsStore.DefaultAppLogMaxMb * 1024 * 1024;

    /// <summary>Clamp 1–200 MB; applied at startup and from Options.</summary>
    public static void SetMaxDiskMegabytes(int mb)
        => _maxLogDiskBytes = (long)Math.Clamp(mb, 1, 200) * 1024 * 1024;

    public static string NormalizeLevelString(string? v)
    {
        var t = (v ?? "").Trim();
        if (string.Equals(t, "Basic", StringComparison.OrdinalIgnoreCase))
            return "Basic";
        if (string.Equals(t, "Verbose", StringComparison.OrdinalIgnoreCase))
            return "Verbose";
        return "ErrorsAndWarnings";
    }

    /// <summary>Persisted values: ErrorsAndWarnings, Basic, Verbose.</summary>
    public static void SetLevel(string storedLevel)
    {
        var n = NormalizeLevelString(storedLevel);
        _infoLevel = n switch
        {
            "Basic" => 1,
            "Verbose" => 2,
            _ => 0,
        };
    }

    public static void Info(string message, AppLogInfoTier tier = AppLogInfoTier.Diagnostic)
    {
        if (_infoLevel == 0)
            return;
        if (_infoLevel == 1 && tier != AppLogInfoTier.Crucial)
            return;
        Write("INFO", message);
    }

    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERROR", message);
    public static void Exception(Exception ex, string? context = null)
        => Write("ERROR", $"{(string.IsNullOrWhiteSpace(context) ? "" : context.Trim() + ": ")}{ex}");

    /// <summary>
    /// Classify one stderr line from ffmpeg, yt-dlp, ffprobe, etc. and log as WARN/ERROR when it looks like a warning or error.
    /// Use for long-running processes (e.g. ffmpeg) where stderr is streamed.
    /// </summary>
    public static void ToolStderrLine(string source, string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;
        var t = line.Trim();
        if (LooksLikeIgnorableYtdlpYoutubeAdvisory(t))
            return;
        if (LooksLikeToolErrorLine(t))
            Error($"{source}: {t}");
        else if (LooksLikeToolWarningLine(t))
            Warn($"{source}: {t}");
    }

    /// <summary>
    /// After a tool process exits, log stderr: on failure, one ERROR line with the full (truncated) stderr for paste debugging;
    /// on success, log lines that look like warnings or errors (e.g. yt-dlp can exit 0 with WARNING lines).
    /// </summary>
    public static void ToolStderrCompleted(string source, string stderr, int exitCode)
    {
        if (exitCode != 0)
        {
            if (string.IsNullOrWhiteSpace(stderr))
            {
                Error($"{source} exited with code {exitCode} (stderr empty).");
                return;
            }

            var t = stderr.Trim();
            // Multi-strategy resolve: yt-dlp often exits 1 on one -f/client before another succeeds — not worth ERROR spam.
            if (LooksLikeYtDlpBenignStrategyFailureStderr(t))
                return;

            Error($"{source} exit {exitCode}: {TruncateForLog(t, MaxToolStderrBlobChars)}");
            return;
        }

        if (string.IsNullOrWhiteSpace(stderr))
            return;

        foreach (var line in stderr.Split(new[] { '\r', '\n' }, StringSplitOptions.None))
        {
            var t = line.Trim();
            if (string.IsNullOrEmpty(t))
                continue;
            if (LooksLikeIgnorableYtdlpYoutubeAdvisory(t))
                continue;
            if (LooksLikeToolErrorLine(t))
                Error($"{source}: {t}");
            else if (LooksLikeToolWarningLine(t))
                Warn($"{source}: {t}");
        }
    }

    /// <summary>
    /// yt-dlp YouTube advisories that are expected when we avoid bundling JS runtimes or PO tokens — not actionable in-app.
    /// </summary>
    private static bool LooksLikeIgnorableYtdlpYoutubeAdvisory(string t)
    {
        if (string.IsNullOrWhiteSpace(t))
            return false;
        if (t.Contains("No supported JavaScript runtime", StringComparison.OrdinalIgnoreCase))
            return true;
        if (t.Contains("without a JS runtime", StringComparison.OrdinalIgnoreCase))
            return true;
        if (t.Contains("JS runtime has been deprecated", StringComparison.OrdinalIgnoreCase))
            return true;
        // PO tokens / client-specific skips — informational when yt-dlp still selects a working format.
        if (t.Contains("GVS PO Token", StringComparison.OrdinalIgnoreCase))
            return true;
        if (t.Contains("po_token=", StringComparison.OrdinalIgnoreCase))
            return true;
        // Do not suppress EJS/challenge warnings: when extraction fails they are the only actionable signal in app.log.
        // YouTube A/B experiments (SABR / android URL) — informational when another format still plays.
        if (t.Contains("SABR-only", StringComparison.OrdinalIgnoreCase))
            return true;
        if (t.Contains("missing a URL", StringComparison.OrdinalIgnoreCase) && t.Contains("android client", StringComparison.OrdinalIgnoreCase))
            return true;
        if (t.Contains("older than 90 days", StringComparison.OrdinalIgnoreCase))
            return true;
        if (t.Contains("strongly recommended to always use the latest version", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    /// <summary>
    /// yt-dlp tries several formats/clients; early failures often show challenge + "format not available" before a later attempt works.
    /// Do <b>not</b> treat "only images" as benign: that means no playable audio/video was exposed (e.g. n challenge / JS not solved).
    /// </summary>
    private static bool LooksLikeYtDlpBenignStrategyFailureStderr(string stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
            return false;

        if (stderr.Contains("Video unavailable", StringComparison.OrdinalIgnoreCase))
            return false;
        if (stderr.Contains("This video is not available", StringComparison.OrdinalIgnoreCase))
            return false;
        if (stderr.Contains("private video", StringComparison.OrdinalIgnoreCase))
            return false;
        if (stderr.Contains("This video is DRM", StringComparison.OrdinalIgnoreCase))
            return false;

        // Storyboard-only / thumbnails — not a recoverable "try next -f" case; must surface full stderr in app.log.
        if (stderr.Contains("Only images are available", StringComparison.OrdinalIgnoreCase))
            return false;

        if (stderr.Contains("Requested format is not available", StringComparison.OrdinalIgnoreCase))
            return true;
        if (stderr.Contains("challenge solving failed", StringComparison.OrdinalIgnoreCase))
            return true;
        if (stderr.Contains("Signature solving failed", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    private static bool LooksLikeToolErrorLine(string t)
    {
        if (t.Contains("[error]", StringComparison.OrdinalIgnoreCase))
            return true;
        if (t.Contains("[fatal]", StringComparison.OrdinalIgnoreCase))
            return true;
        if (t.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase))
            return true;
        if (t.Contains("]: Error ", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    private static bool LooksLikeToolWarningLine(string t)
    {
        if (t.Contains("[warning]", StringComparison.OrdinalIgnoreCase))
            return true;
        if (t.Contains("]: Warning ", StringComparison.OrdinalIgnoreCase))
            return true;
        if (t.StartsWith("WARNING:", StringComparison.OrdinalIgnoreCase))
            return true;
        if (t.Contains("deprecated", StringComparison.OrdinalIgnoreCase))
            return true;
        if (t.Contains("UserWarning", StringComparison.Ordinal))
            return true;
        return false;
    }

    private static string TruncateForLog(string s, int maxChars)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= maxChars)
            return s;
        return s[..maxChars] + $"\n… ({s.Length - maxChars} more characters truncated)";
    }

    public static IReadOnlyList<string> Snapshot()
    {
        lock (Gate)
        {
            return Buffer.ToList();
        }
    }

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
        try
        {
            lock (Gate)
            {
                Buffer.AddLast(line);
                while (Buffer.Count > MaxLines)
                    Buffer.RemoveFirst();
            }

            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            var text = line + Environment.NewLine;
            var incomingBytes = (long)Utf8NoBom.GetByteCount(text);
            lock (FileGate)
            {
                TryRollLogFileForIncomingAppend(incomingBytes);
                File.AppendAllText(LogPath, text, Utf8NoBom);
            }
        }
        catch
        {
            // ignore logging failures
        }

        try { Changed?.Invoke(null, EventArgs.Empty); } catch { /* ignore */ }
    }

    /// <summary>When the file would exceed <see cref="_maxLogDiskBytes"/>, replace it with a UTF-8 tail plus a marker line.</summary>
    private static void TryRollLogFileForIncomingAppend(long incomingUtf8Bytes)
    {
        if (_maxLogDiskBytes <= 0)
            return;

        var path = LogPath;
        if (incomingUtf8Bytes >= _maxLogDiskBytes)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // ignore
            }

            return;
        }

        try
        {
            if (!File.Exists(path))
                return;

            var len = new FileInfo(path).Length;
            if (len + incomingUtf8Bytes <= _maxLogDiskBytes)
                return;

            const long slackSearch = 8192;
            var allowTail = Math.Max(4096L, _maxLogDiskBytes - incomingUtf8Bytes - 256);
            // Avoid multi-hundred-megabyte allocations when the cap is large; repeated rolls still respect the cap.
            const long maxSingleRead = 48L * 1024 * 1024;
            var readChunk = Math.Min(len, Math.Min(allowTail + slackSearch, maxSingleRead));
            if (readChunk <= 0)
                return;

            var readFrom = len - readChunk;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            len = fs.Length;
            if (len + incomingUtf8Bytes <= _maxLogDiskBytes)
                return;

            readChunk = Math.Min(len, Math.Min(allowTail + slackSearch, maxSingleRead));
            readFrom = len - readChunk;
            if (readFrom < 0)
            {
                readFrom = 0;
                readChunk = len;
            }

            var toRead = (int)Math.Min(int.MaxValue / 2, readChunk);
            var buf = new byte[toRead];
            fs.Seek(readFrom, SeekOrigin.Begin);
            var totalRead = 0;
            while (totalRead < toRead)
            {
                var r = fs.Read(buf, totalRead, toRead - totalRead);
                if (r == 0)
                    break;
                totalRead += r;
            }

            if (totalRead <= 0)
            {
                fs.SetLength(0);
                return;
            }

            var cut = 0;
            var scan = Math.Min(totalRead, 8192);
            for (var i = 0; i < scan; i++)
            {
                if (buf[i] == (byte)'\n')
                {
                    cut = i + 1;
                    break;
                }
            }

            while (cut < totalRead && (buf[cut] & 0xC0) == 0x80)
                cut++;

            var tailLen = totalRead - cut;
            if (tailLen <= 0)
            {
                cut = 0;
                tailLen = totalRead;
                while (cut < totalRead && (buf[cut] & 0xC0) == 0x80)
                    cut++;
                tailLen = totalRead - cut;
            }

            const string marker = "…(earlier log truncated — size limit)…\r\n";
            var preamble = Utf8NoBom.GetBytes(marker);
            fs.Position = 0;
            fs.SetLength(0);
            fs.Write(preamble, 0, preamble.Length);
            fs.Write(buf, cut, tailLen);
            fs.Flush();
        }
        catch
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // ignore
            }
        }
    }
}
