using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace LyllyPlayer.Updater;

/// <summary>
/// Replaces <c>LyllyPlayer.Updater.exe</c> after this process exits (cannot overwrite the running exe).
/// </summary>
internal static class UpdaterSelfReplace
{
    public const string PendingFileName = "LyllyPlayer.Updater.exe.pending";

    /// <summary>Write payload updater to <see cref="PendingFileName"/> when the release ships a new helper.</summary>
    public static bool TryStagePending(string payloadDir, string installDir, string updaterExeName)
    {
        var payloadUpdater = Path.Combine(payloadDir, updaterExeName);
        var pendingPath = Path.Combine(installDir, PendingFileName);

        if (!File.Exists(payloadUpdater))
        {
            TryDelete(pendingPath);
            return false;
        }

        File.Copy(payloadUpdater, pendingPath, overwrite: true);
        return true;
    }

    public static void Schedule(int updaterProcessId, string pendingPath, string destPath)
    {
        pendingPath = Path.GetFullPath(pendingPath);
        destPath = Path.GetFullPath(destPath);

        if (!File.Exists(pendingPath))
            return;

        var scriptPath = Path.Combine(
            Path.GetTempPath(),
            $"lyllyplayer-updater-replace-{Guid.NewGuid():N}.ps1");

        var script = new StringBuilder();
        script.AppendLine("$ErrorActionPreference = 'Stop'");
        script.AppendLine($"$waitPid = {updaterProcessId}");
        script.AppendLine($"$pending = '{EscapePsSingleQuoted(pendingPath)}'");
        script.AppendLine($"$dest = '{EscapePsSingleQuoted(destPath)}'");
        script.AppendLine("$self = $MyInvocation.MyCommand.Path");
        script.AppendLine("while (Get-Process -Id $waitPid -ErrorAction SilentlyContinue) { Start-Sleep -Milliseconds 250 }");
        script.AppendLine("Start-Sleep -Milliseconds 400");
        script.AppendLine("if (Test-Path -LiteralPath $pending) {");
        script.AppendLine("  Move-Item -LiteralPath $pending -Destination $dest -Force");
        script.AppendLine("}");
        script.AppendLine("Remove-Item -LiteralPath $self -Force -ErrorAction SilentlyContinue");

        File.WriteAllText(scriptPath, script.ToString(), Encoding.UTF8);

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (Process.Start(psi) is null)
            throw new InvalidOperationException("Failed to start deferred updater self-replace.");
    }

    public static void TryDeletePending(string installDir)
    {
        TryDelete(Path.Combine(installDir, PendingFileName));
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch { /* ignore */ }
    }

    private static string EscapePsSingleQuoted(string value)
        => (value ?? "").Replace("'", "''");
}
