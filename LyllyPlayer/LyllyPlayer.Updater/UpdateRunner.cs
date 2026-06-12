using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace LyllyPlayer.Updater;

internal sealed class UpdateRunner
{
    private const string UpdaterExeName = "LyllyPlayer.Updater.exe";

    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("LyllyPlayer.Updater", "1.0"));
        return http;
    }

    public async Task<int> RunAsync(CliOptions options, CancellationToken cancellationToken)
    {
        UpdateLog.Init(options.InstallDir);
        UpdateLog.Write($"targetVersion={options.TargetVersion} parentPid={options.ParentPid} restart={options.Restart}");

        var installDir = options.InstallDir;
        var mainExe = Path.Combine(installDir, "LyllyPlayer.exe");
        if (!File.Exists(mainExe))
        {
            UpdateLog.Write($"LyllyPlayer.exe not found in {installDir}");
            return 2;
        }

        var workRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LyllyPlayer",
            "updates",
            string.IsNullOrWhiteSpace(options.TargetVersion) ? "pending" : options.TargetVersion);
        var downloadPath = Path.Combine(workRoot, "download.zip");
        var stagingDir = Path.Combine(workRoot, "staging");
        var backupDir = installDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + ".backup";

        try
        {
            await WaitForAppExitAsync(installDir, options.ParentPid, cancellationToken).ConfigureAwait(false);

            Directory.CreateDirectory(workRoot);
            if (Directory.Exists(stagingDir))
                Directory.Delete(stagingDir, recursive: true);
            if (File.Exists(downloadPath))
                File.Delete(downloadPath);

            UpdateLog.Write("Downloading release ZIP…");
            await DownloadFileAsync(options.ZipUrl, downloadPath, cancellationToken).ConfigureAwait(false);

            var zipLen = new FileInfo(downloadPath).Length;
            var free = GetFreeBytes(Path.GetPathRoot(installDir) ?? installDir);
            if (free > 0 && free < zipLen * 3)
            {
                UpdateLog.Write($"Insufficient disk space. Need ~{zipLen * 3} bytes, have {free}.");
                return 5;
            }

            UpdateLog.Write("Extracting…");
            Directory.CreateDirectory(stagingDir);
            var extractedFiles = ZipExtract.ExtractToDirectory(downloadPath, stagingDir);
            var payloadDir = ResolvePayloadRoot(stagingDir);
            var payloadFiles = DirectorySync.CountFiles(payloadDir);
            UpdateLog.Write($"Extracted {extractedFiles} zip entries; payload root: {payloadDir} ({payloadFiles} files)");

            if (!File.Exists(Path.Combine(payloadDir, "LyllyPlayer.exe")))
            {
                UpdateLog.Write("Extracted payload missing LyllyPlayer.exe.");
                return 3;
            }

            if (Directory.Exists(backupDir))
            {
                UpdateLog.Write($"Removing previous backup: {backupDir}");
                Directory.Delete(backupDir, recursive: true);
            }

            UpdateLog.Write($"Backing up install folder to {backupDir}");
            DirectorySync.CopyDirectory(installDir, backupDir);

            var keepUpdater = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                UpdaterExeName,
            };

            try
            {
                UpdateLog.Write("Applying update…");
                DirectorySync.ClearDirectory(installDir, keepUpdater);
                var copied = DirectorySync.CopyDirectory(payloadDir, installDir, keepUpdater);

                var destUpdater = Path.Combine(installDir, UpdaterExeName);
                var pendingUpdater = Path.Combine(installDir, UpdaterSelfReplace.PendingFileName);
                var stageUpdater = UpdaterSelfReplace.TryStagePending(payloadDir, installDir, UpdaterExeName);
                if (stageUpdater)
                {
                    UpdateLog.Write($"Staged {UpdaterSelfReplace.PendingFileName} for replace after exit.");
                    UpdaterSelfReplace.Schedule(Process.GetCurrentProcess().Id, pendingUpdater, destUpdater);
                }

                UpdateLog.Write(stageUpdater
                    ? $"Update applied ({copied} files; {UpdaterExeName} will be replaced after this process exits)."
                    : $"Update applied ({copied} files copied; {UpdaterExeName} left in place — it is running).");
            }
            catch (Exception ex)
            {
                UpdateLog.Exception(ex, "Apply failed; restoring backup");
                UpdaterSelfReplace.TryDeletePending(installDir);
                try
                {
                    DirectorySync.ClearDirectory(installDir, keepUpdater);
                    var restored = DirectorySync.CopyDirectory(backupDir, installDir, keepUpdater);
                    UpdateLog.Write($"Backup restored ({restored} files; {UpdaterExeName} left in place).");
                }
                catch (Exception restoreEx)
                {
                    UpdateLog.Exception(restoreEx, "Restore failed");
                }
                return 4;
            }

            if (options.Restart)
            {
                UpdateLog.Write("Restarting LyllyPlayer…");
                Process.Start(new ProcessStartInfo
                {
                    FileName = mainExe,
                    WorkingDirectory = installDir,
                    UseShellExecute = true,
                });
            }

            return 0;
        }
        catch (Exception ex)
        {
            UpdateLog.Exception(ex, "Update failed");
            return 1;
        }
    }

    private static string ResolvePayloadRoot(string stagingDir)
    {
        if (File.Exists(Path.Combine(stagingDir, "LyllyPlayer.exe")))
            return stagingDir;

        string? shallowMatch = null;
        foreach (var exe in Directory.EnumerateFiles(stagingDir, "LyllyPlayer.exe", SearchOption.AllDirectories))
        {
            var dir = Path.GetDirectoryName(exe);
            if (string.IsNullOrEmpty(dir))
                continue;

            var relDepth = dir.Substring(stagingDir.Length).Trim(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var depth = string.IsNullOrEmpty(relDepth) ? 0 : relDepth.Split(Path.DirectorySeparatorChar).Length;
            if (depth <= 1)
                return dir;

            shallowMatch ??= dir;
        }

        return shallowMatch ?? stagingDir;
    }

    private static async Task DownloadFileAsync(string url, string destPath, CancellationToken cancellationToken)
    {
        var dir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        using (var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                   .ConfigureAwait(false))
        {
            resp.EnsureSuccessStatusCode();
            using (var input = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
            using (var output = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await input.CopyToAsync(output).ConfigureAwait(false);
            }
        }
    }

    private static async Task WaitForAppExitAsync(string installDir, int parentPid, CancellationToken cancellationToken)
    {
        UpdateLog.Write("Waiting for LyllyPlayer to exit…");
        var deadline = DateTime.UtcNow.AddMinutes(3);

        if (parentPid > 0)
        {
            try
            {
                using var parent = Process.GetProcessById(parentPid);
                await Task.Run(() => parent.WaitForExit(), cancellationToken).ConfigureAwait(false);
            }
            catch (ArgumentException)
            {
                /* already exited */
            }
        }

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var running = false;
            foreach (var proc in Process.GetProcessesByName("LyllyPlayer"))
            {
                using (proc)
                {
                    try
                    {
                        var path = proc.MainModule?.FileName;
                        if (path is not null
                            && path.StartsWith(installDir, StringComparison.OrdinalIgnoreCase))
                        {
                            running = true;
                            break;
                        }
                    }
                    catch { /* ignore access denied */ }
                }
            }

            if (!running)
            {
                UpdateLog.Write("No LyllyPlayer process in install folder.");
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                return;
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }

        UpdateLog.Write("Timed out waiting for LyllyPlayer to exit.");
        throw new TimeoutException("LyllyPlayer did not exit in time.");
    }

    private static long GetFreeBytes(string root)
    {
        try
        {
            var drive = new DriveInfo(root);
            return drive.AvailableFreeSpace;
        }
        catch
        {
            return -1;
        }
    }
}
