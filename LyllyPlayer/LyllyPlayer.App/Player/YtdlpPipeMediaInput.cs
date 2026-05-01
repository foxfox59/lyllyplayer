using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using LibVLCSharp.Shared;
using LyllyPlayer.Utils;

namespace LyllyPlayer.Player;

/// <summary>
/// Feeds muxed audio from <c>yt-dlp -o -</c> stdout into LibVLC via <see cref="MediaInput"/>.
/// Blocking reads avoid signalling EOS while the producer is merely slow.
/// </summary>
public sealed class YtdlpPipeMediaInput : MediaInput
{
    private const int StderrTailMax = 12000;
    private const int FirstByteProbeMs = 8000;

    private readonly object _stderrLock = new();
    private readonly StringBuilder _stderrTail = new();

    private Process? _process;
    private Stream? _stdout;
    private byte[]? _prefix;
    private int _prefixOffset;
    private bool _closed;

    private YtdlpPipeMediaInput()
    {
    }

    /// <summary>Best-effort stderr tail for diagnostics (playback failure messages).</summary>
    public string GetStderrTail(int maxChars)
    {
        maxChars = Math.Clamp(maxChars, 256, StderrTailMax);
        lock (_stderrLock)
        {
            var s = _stderrTail.ToString();
            if (s.Length <= maxChars)
                return s;
            return s[^maxChars..];
        }
    }

    public static async Task<YtdlpPipeMediaInput> CreateWithClientProbeAsync(
        string youtubeWatchUrl,
        string ytDlpPath,
        Action<ProcessStartInfo> applyYtdlpLaunchPrefix,
        string ytdlpAudioFormat,
        bool ytDlpUsesCookiesFromBrowser,
        CancellationToken cancellationToken)
    {
        var url = (youtubeWatchUrl ?? "").Trim();
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("Watch URL required.", nameof(youtubeWatchUrl));

        var ytdlpExe = string.IsNullOrWhiteSpace(ytDlpPath) ? "yt-dlp" : ytDlpPath;
        var android = new[] { "--extractor-args", "youtube:player_client=android" };
        var web = new[] { "--extractor-args", "youtube:player_client=web" };
        var webEmbedded = new[] { "--extractor-args", "youtube:player_client=web_embedded" };
        var webSafari = new[] { "--extractor-args", "youtube:player_client=web_safari" };

        var attempts = ytDlpUsesCookiesFromBrowser
            ? new[] { webEmbedded, webSafari, web }
            : new[] { android, web };

        Exception? last = null;
        var scratch = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            for (var i = 0; i < attempts.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var attempt = attempts[i];
                var isLastClient = i == attempts.Length - 1;
                var useTimeoutProbe = !ytDlpUsesCookiesFromBrowser && !isLastClient;

                Process? proc = null;
                var stderrAccum = new StringBuilder();
                try
                {
                    proc = StartYtdlpProcess(url, ytdlpExe, applyYtdlpLaunchPrefix, attempt, ytdlpAudioFormat, stderrAccum);
                    var stdout = proc.StandardOutput.BaseStream;

                    int n;
                    if (useTimeoutProbe)
                    {
                        using var probeTimeout = new CancellationTokenSource(FirstByteProbeMs);
                        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, probeTimeout.Token);
                        try
                        {
                            n = await stdout.ReadAsync(scratch.AsMemory(0, scratch.Length), linked.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            if (cancellationToken.IsCancellationRequested)
                                throw;

                            try
                            {
                                AppLog.Warn(
                                    $"yt-dlp→LibVLC pipe: no data within {FirstByteProbeMs} ms (YouTube client attempt {i + 1}/{attempts.Length}); trying next client.");
                            }
                            catch { /* ignore */ }

                            TryThrowIfStableYoutubeFailure(stderrAccum);
                            KillProcessTree(proc);
                            proc = null;
                            last = new TimeoutException("First byte probe timed out.");
                            continue;
                        }
                    }
                    else
                    {
                        n = await stdout.ReadAsync(scratch.AsMemory(0, scratch.Length), cancellationToken).ConfigureAwait(false);
                    }

                    if (n <= 0)
                    {
                        TryThrowIfStableYoutubeFailure(stderrAccum);
                        KillProcessTree(proc);
                        proc = null;
                        last = new InvalidOperationException("yt-dlp stdout reached EOF before muxed data.");
                        continue;
                    }

                    var prefix = new byte[n];
                    Array.Copy(scratch, prefix, n);

                    var input = new YtdlpPipeMediaInput();
                    input.AttachRunningProcess(proc, stdout, prefix, stderrAccum);
                    proc = null; // ownership transferred
                    return input;
                }
                catch (OperationCanceledException)
                {
                    if (proc is not null)
                        KillProcessTree(proc);
                    throw;
                }
                catch (Exception ex)
                {
                    if (proc is not null)
                        KillProcessTree(proc);
                    last = ex;
                    TryThrowIfStableYoutubeFailure(stderrAccum);
                }
            }

            throw last ?? new InvalidOperationException("yt-dlp stdout pipe failed.");
        }
        finally
        {
            try { ArrayPool<byte>.Shared.Return(scratch); } catch { /* ignore */ }
        }
    }

    private void AttachRunningProcess(Process proc, Stream stdout, byte[] prefix, StringBuilder stderrTail)
    {
        _process = proc;
        _stdout = stdout;
        _prefix = prefix;
        _prefixOffset = 0;
        lock (_stderrLock)
        {
            _stderrTail.Clear();
            if (stderrTail.Length > 0)
                _stderrTail.Append(stderrTail);
        }
    }

    private static Process StartYtdlpProcess(
        string youtubeWatchUrl,
        string ytdlpExe,
        Action<ProcessStartInfo> applyYtdlpLaunchPrefix,
        string[] extractorArgs,
        string ytdlpAudioFormat,
        StringBuilder stderrCapture)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ytdlpExe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        applyYtdlpLaunchPrefix(psi);
        psi.ArgumentList.Add("--no-playlist");
        foreach (var a in extractorArgs)
            psi.ArgumentList.Add(a);
        psi.ArgumentList.Add("--socket-timeout");
        psi.ArgumentList.Add("30");
        psi.ArgumentList.Add("--retries");
        psi.ArgumentList.Add("6");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add(string.IsNullOrWhiteSpace(ytdlpAudioFormat) ? "bestaudio/best" : ytdlpAudioFormat);
        psi.ArgumentList.Add("--no-part");
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add("-");
        psi.ArgumentList.Add(youtubeWatchUrl);

        var proc = new Process { StartInfo = psi };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
                return;
            try
            {
                AppLog.ToolStderrLine("yt-dlp", e.Data);
                lock (stderrCapture)
                {
                    if (stderrCapture.Length > 0)
                        stderrCapture.AppendLine();
                    stderrCapture.Append(e.Data);
                    if (stderrCapture.Length > StderrTailMax)
                        stderrCapture.Remove(0, stderrCapture.Length - StderrTailMax);
                }
            }
            catch { /* ignore */ }
        };

        if (!proc.Start())
            throw new InvalidOperationException("Failed to start yt-dlp (stdout pipe).");

        ChildToolProcessJob.TryAssign(proc);
        proc.BeginErrorReadLine();
        return proc;
    }

    private static void TryThrowIfStableYoutubeFailure(StringBuilder stderrTail)
    {
        string tail;
        lock (stderrTail)
            tail = stderrTail.ToString();

        if (!YtDlpClient.IsStableYoutubePipeFailure(tail))
            return;

        var msg = tail.Trim();
        if (msg.Length > 2400)
            msg = msg[^2400..].Trim();
        throw new InvalidOperationException(string.IsNullOrEmpty(msg)
            ? "YouTube reported this video cannot be played."
            : msg);
    }

    private static void KillProcessTree(Process? p)
    {
        if (p is null)
            return;
        try { p.Kill(entireProcessTree: true); } catch { /* ignore */ }
        try { p.Dispose(); } catch { /* ignore */ }
    }

    public void ForceStop()
    {
        lock (_stderrLock)
        {
            _closed = true;
        }

        try
        {
            _stdout?.Dispose();
        }
        catch { /* ignore */ }

        _stdout = null;
        KillProcessTree(_process);
        _process = null;
    }

    public override bool Open(out ulong size)
    {
        size = ulong.MaxValue;
        lock (_stderrLock)
        {
            if (_closed)
                return false;
        }

        return _process is not null && _stdout is not null;
    }

    public override int Read(IntPtr buf, uint len)
    {
        if (len == 0)
            return 0;

        lock (_stderrLock)
        {
            if (_closed)
                return 0;
        }

        try
        {
            var want = (int)Math.Min(len, int.MaxValue);
            if (want <= 0)
                return 0;

            // Prefix replay (first probe chunk).
            if (_prefix is { Length: > 0 } pref && _prefixOffset < pref.Length)
            {
                var n = Math.Min(want, pref.Length - _prefixOffset);
                Marshal.Copy(pref, _prefixOffset, buf, n);
                _prefixOffset += n;
                if (_prefixOffset >= pref.Length)
                    _prefix = null;
                return n;
            }

            var stdout = _stdout;
            if (stdout is null)
                return 0;

            var tmp = ArrayPool<byte>.Shared.Rent(want);
            try
            {
                var read = stdout.Read(tmp, 0, want);
                if (read <= 0)
                    return 0;
                Marshal.Copy(tmp, 0, buf, read);
                return read;
            }
            finally
            {
                try { ArrayPool<byte>.Shared.Return(tmp); } catch { /* ignore */ }
            }
        }
        catch (IOException)
        {
            return 0;
        }
        catch (ObjectDisposedException)
        {
            return 0;
        }
        catch
        {
            return -1;
        }
    }

    public override bool Seek(ulong offset) => false;

    public override void Close()
    {
        ForceStop();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            ForceStop();
        base.Dispose(disposing);
    }
}
