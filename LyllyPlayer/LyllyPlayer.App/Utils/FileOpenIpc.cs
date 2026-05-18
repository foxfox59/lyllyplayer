using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LyllyPlayer.Utils;

public static class FileOpenIpc
{
    // Per-user session: use Local\ namespace already used by our mutex.
    // Named pipes are already per-user by default; include a stable suffix anyway.
    private const string PipeName = "LyllyPlayer_OpenFile_9B8C4C2B0B984C1C8AB9D4B9E3B6A1C1";

    public static bool LooksLikeSupportedFileOpenArg(string? arg)
    {
        if (string.IsNullOrWhiteSpace(arg))
            return false;
        var s = arg.Trim().Trim('"');
        if (s.Length == 0)
            return false;

        var ext = "";
        try { ext = (Path.GetExtension(s) ?? "").Trim().ToLowerInvariant(); } catch { ext = ""; }
        if (ext is ".lyllylist" or ".lyllytheme")
            return true;
        return LocalPlaylistLoader.IsSupportedAudioExtension(ext);
    }

    public static string? TryGetFirstSupportedPathFromArgs(string[]? args)
    {
        if (args is null || args.Length == 0)
            return null;

        for (var i = 0; i < args.Length; i++)
        {
            var a = (args[i] ?? "").Trim().Trim('"');
            if (IsIgnorableProcessArgument(a))
                continue;

            var normalized = PlaylistDragDropHelper.TryNormalizeLocalAudioPath(a);
            if (!string.IsNullOrWhiteSpace(normalized) && LooksLikeSupportedFileOpenArg(normalized))
                return normalized;

            if (!LooksLikeSupportedFileOpenArg(a))
                continue;

            try { return Path.GetFullPath(a); } catch { return a; }
        }

        return null;
    }

    /// <summary>Parse the raw Win32 command line (handles quoted paths Explorer passes to "Open with").</summary>
    public static string? TryGetFirstSupportedPathFromCommandLine()
    {
        try
        {
            var raw = Environment.CommandLine;
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            var argv = SplitCommandLineBestEffort(raw);
            return TryGetFirstSupportedPathFromArgs(argv);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsIgnorableProcessArgument(string arg)
    {
        if (string.IsNullOrWhiteSpace(arg))
            return true;

        var s = arg.Trim();
        if (s.Length == 0)
            return true;

        // Skip the host executable path (first arg is usually the .exe).
        if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
            (s.Contains('\\') || s.Contains('/') || s.Contains(':')))
            return true;

        if (s.StartsWith("-", StringComparison.Ordinal))
            return true;

        if (s.StartsWith("/prefetch:", StringComparison.OrdinalIgnoreCase))
            return true;

        if (string.Equals(s, "/Embedding", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private static string[] SplitCommandLineBestEffort(string commandLine)
    {
        var acc = new List<string>();
        if (string.IsNullOrWhiteSpace(commandLine))
            return Array.Empty<string>();

        var s = commandLine;
        var i = 0;
        while (i < s.Length)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i]))
                i++;
            if (i >= s.Length)
                break;

            if (s[i] == '"')
            {
                i++;
                var start = i;
                while (i < s.Length && s[i] != '"')
                    i++;
                acc.Add(s[start..Math.Min(i, s.Length)]);
                if (i < s.Length && s[i] == '"')
                    i++;
                continue;
            }

            var start2 = i;
            while (i < s.Length && !char.IsWhiteSpace(s[i]))
                i++;
            acc.Add(s[start2..i]);
        }

        return acc.ToArray();
    }

    public static async Task<bool> TrySendOpenFileRequestAsync(string path, int timeoutMs = 400)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;
            var p = path.Trim().Trim('"');
            if (!LooksLikeSupportedFileOpenArg(p))
                return false;

            using var client = new NamedPipeClientStream(
                serverName: ".",
                pipeName: PipeName,
                direction: PipeDirection.Out,
                options: PipeOptions.Asynchronous);

            using var cts = new CancellationTokenSource();
            cts.CancelAfter(Math.Clamp(timeoutMs, 50, 5000));
            await client.ConnectAsync(cts.Token).ConfigureAwait(false);

            using var sw = new StreamWriter(client, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true)
            {
                AutoFlush = true
            };
            await sw.WriteLineAsync(p).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static IDisposable StartServerBestEffort(Action<string> onPath, CancellationToken ct)
    {
        var t = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                NamedPipeServerStream? server = null;
                try
                {
                    server = new NamedPipeServerStream(
                        PipeName,
                        PipeDirection.In,
                        maxNumberOfServerInstances: 1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    await server.WaitForConnectionAsync(ct).ConfigureAwait(false);

                    using var sr = new StreamReader(server, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
                    var line = await sr.ReadLineAsync(ct).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        var p = line.Trim().Trim('"');
                        if (LooksLikeSupportedFileOpenArg(p))
                        {
                            try { onPath(p); } catch { /* ignore */ }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    // ignore; next loop recreates server
                }
                finally
                {
                    try { server?.Dispose(); } catch { /* ignore */ }
                }
            }
        }, ct);

        return new DisposableAction(() =>
        {
            try { /* best-effort; ct should be canceled by owner */ } catch { /* ignore */ }
            try { _ = t; } catch { /* ignore */ }
        });
    }

    private sealed class DisposableAction(Action dispose) : IDisposable
    {
        public void Dispose()
        {
            try { dispose(); } catch { /* ignore */ }
        }
    }
}

