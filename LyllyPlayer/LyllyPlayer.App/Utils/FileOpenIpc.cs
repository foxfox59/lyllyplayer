using System;
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
        return ext is ".lyllylist" or ".lyllytheme";
    }

    public static string? TryGetFirstSupportedPathFromArgs(string[]? args)
    {
        if (args is null || args.Length == 0)
            return null;

        for (var i = 0; i < args.Length; i++)
        {
            var a = (args[i] ?? "").Trim().Trim('"');
            if (!LooksLikeSupportedFileOpenArg(a))
                continue;
            return a;
        }
        return null;
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

