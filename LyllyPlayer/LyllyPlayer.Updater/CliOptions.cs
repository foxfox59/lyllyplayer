using System;
using System.IO;

namespace LyllyPlayer.Updater;

internal sealed class CliOptions
{
    public string InstallDir { get; set; } = "";
    public string ZipUrl { get; set; } = "";
    public string TargetVersion { get; set; } = "";
    public int ParentPid { get; set; }
    public bool Restart { get; set; } = true;

    public static bool TryParse(string[] args, out CliOptions options, out string error)
    {
        options = new CliOptions();
        error = "";

        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (string.Equals(a, "--install-dir", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryReadValue(args, ref i, out var v)) { error = "Missing --install-dir value."; return false; }
                options.InstallDir = v;
                continue;
            }
            if (string.Equals(a, "--zip-url", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryReadValue(args, ref i, out var v)) { error = "Missing --zip-url value."; return false; }
                options.ZipUrl = v;
                continue;
            }
            if (string.Equals(a, "--target-version", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryReadValue(args, ref i, out var v)) { error = "Missing --target-version value."; return false; }
                options.TargetVersion = v;
                continue;
            }
            if (string.Equals(a, "--parent-pid", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryReadValue(args, ref i, out var v) || !int.TryParse(v, out var pid)) { error = "Invalid --parent-pid."; return false; }
                options.ParentPid = pid;
                continue;
            }
            if (string.Equals(a, "--no-restart", StringComparison.OrdinalIgnoreCase))
            {
                options.Restart = false;
                continue;
            }
        }

        if (string.IsNullOrWhiteSpace(options.InstallDir))
        {
            error = "Required: --install-dir";
            return false;
        }
        if (string.IsNullOrWhiteSpace(options.ZipUrl))
        {
            error = "Required: --zip-url";
            return false;
        }

        options.InstallDir = Path.GetFullPath(options.InstallDir.Trim());
        return true;
    }

    private static bool TryReadValue(string[] args, ref int index, out string value)
    {
        value = "";
        if (index + 1 >= args.Length)
            return false;
        value = args[++index];
        return !string.IsNullOrWhiteSpace(value);
    }
}
