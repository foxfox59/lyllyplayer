using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace LyllyPlayer.Utils;

/// <summary>Human-readable app version string (same value persisted as <c>LastSavedByAppVersion</c> in settings.json).</summary>
public static class AppVersion
{
    public static string Current { get; } = Resolve();

    /// <summary>Full path to the process executable (use to verify Explorer "Open with" target).</summary>
    public static string ProcessExePath { get; } = Environment.ProcessPath ?? "(unknown)";

    public static string FileVersion { get; } = ResolveFileVersion();

    public static string ExeLastWriteUtc { get; } = ResolveExeLastWriteUtc();

    private static string ResolveFileVersion()
    {
        try
        {
            var p = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(p))
                return "(unknown)";
            return FileVersionInfo.GetVersionInfo(p).FileVersion ?? "(unknown)";
        }
        catch
        {
            return "(unknown)";
        }
    }

    private static string ResolveExeLastWriteUtc()
    {
        try
        {
            var p = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(p) || !File.Exists(p))
                return "(unknown)";
            return File.GetLastWriteTimeUtc(p).ToString("yyyy-MM-dd HH:mm:ss") + " UTC";
        }
        catch
        {
            return "(unknown)";
        }
    }

    public static void LogRunningBuildIdentity()
    {
        try
        {
            AppLog.Warn(
                $"RUNNING BUILD app={Current} fileVersion={FileVersion} pid={Environment.ProcessId} " +
                $"exe={ProcessExePath} exeMtime={ExeLastWriteUtc}");
        }
        catch { /* ignore */ }
    }

    private static string Resolve()
    {
        var asm = Assembly.GetEntryAssembly() ?? typeof(AppVersion).Assembly;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            var plus = info.IndexOf('+', StringComparison.Ordinal);
            return (plus >= 0 ? info[..plus] : info).Trim();
        }

        return asm.GetName().Version?.ToString(3) ?? "unknown";
    }
}
