using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using LyllyPlayer.Updates;

namespace LyllyPlayer.Utils;

public sealed class AppUpdateCheckResult
{
    public bool Success { get; init; }
    public bool IsUpdateAvailable { get; init; }
    public string? InstalledVersion { get; init; }
    public AppReleaseOffer? Offer { get; init; }
    public string? ErrorMessage { get; init; }
}

public static class AppUpdateService
{
    private static readonly GitHubAppReleaseClient ReleaseClient = new();

    public static string GetInstallDirectory()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe))
            return AppContext.BaseDirectory;
        return Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory;
    }

    public static bool IsPortableUpdateSupported()
    {
        var exe = Environment.ProcessPath ?? "";
        var normalized = exe.Replace('/', '\\');
        if (normalized.Contains(@"\bin\DebugDev\", StringComparison.OrdinalIgnoreCase))
            return false;
        if (normalized.Contains(@"\bin\Debug\", StringComparison.OrdinalIgnoreCase))
            return false;
        if (normalized.Contains(@"\obj\", StringComparison.OrdinalIgnoreCase))
            return false;
        return File.Exists(Path.Combine(GetInstallDirectory(), AppUpdateConstants.UpdaterExeName));
    }

    public static async Task<AppUpdateCheckResult> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        var installed = AppVersion.Current;
        if (!IsPortableUpdateSupported())
        {
            return new AppUpdateCheckResult
            {
                Success = false,
                InstalledVersion = installed,
                ErrorMessage =
                    "In-app updates are only available for the portable release folder " +
                    $"(with {AppUpdateConstants.UpdaterExeName} next to LyllyPlayer.exe). " +
                    "Development builds must be updated manually.",
            };
        }

        try
        {
            var offer = await ReleaseClient.TryGetLatestPortableOfferAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (offer is null)
            {
                return new AppUpdateCheckResult
                {
                    Success = false,
                    InstalledVersion = installed,
                    ErrorMessage = "Could not find a portable ZIP for this PC on the latest GitHub release.",
                };
            }

            var newer = AppVersionComparer.IsNewer(offer.Version, installed);
            return new AppUpdateCheckResult
            {
                Success = true,
                IsUpdateAvailable = newer,
                InstalledVersion = installed,
                Offer = offer,
            };
        }
        catch (Exception ex)
        {
            return new AppUpdateCheckResult
            {
                Success = false,
                InstalledVersion = installed,
                ErrorMessage = ex.Message,
            };
        }
    }

    public static bool TryLaunchUpdater(AppReleaseOffer offer, out string? error)
    {
        error = null;
        var installDir = GetInstallDirectory();
        var updaterPath = Path.Combine(installDir, AppUpdateConstants.UpdaterExeName);
        if (!File.Exists(updaterPath))
        {
            error = $"{AppUpdateConstants.UpdaterExeName} was not found next to LyllyPlayer.exe.";
            return false;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = updaterPath,
                WorkingDirectory = installDir,
                UseShellExecute = true,
            };
            psi.ArgumentList.Add("--install-dir");
            psi.ArgumentList.Add(installDir);
            psi.ArgumentList.Add("--zip-url");
            psi.ArgumentList.Add(offer.DownloadUrl);
            psi.ArgumentList.Add("--target-version");
            psi.ArgumentList.Add(offer.Version);
            psi.ArgumentList.Add("--parent-pid");
            psi.ArgumentList.Add(Environment.ProcessId.ToString());

            if (Process.Start(psi) is null)
            {
                error = "Failed to start the updater.";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static string DescribeRuntime()
        => $"{GitHubAppReleaseClient.GetPortableRuntimeId()} ({RuntimeInformation.ProcessArchitecture})";
}
