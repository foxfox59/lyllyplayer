using System.Windows;
using LyllyPlayer.Updates;
using LyllyPlayer.Utils;

namespace LyllyPlayer;

public partial class MainWindow
{
    private async Task CheckAppUpdateNowAsync(bool offerInstall)
    {
        try
        {
            ShowInfoToast("Checking for LyllyPlayer updates…", ms: 1400);
            var result = await AppUpdateService.CheckForUpdateAsync().ConfigureAwait(true);
            _lastAppUpdateCheckUtc = DateTime.UtcNow;
            try { RequestPersistSnapshot(); } catch { /* ignore */ }

            if (!result.Success)
            {
                TopmostMessageBox.Show(
                    result.ErrorMessage ?? "Update check failed.",
                    GetAppTitleBase(),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            var installed = result.InstalledVersion ?? AppVersion.Current;
            var offer = result.Offer;
            if (offer is null)
            {
                TopmostMessageBox.Show(
                    "Update check failed (no release info).",
                    GetAppTitleBase(),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (!result.IsUpdateAvailable)
            {
                TopmostMessageBox.Show(
                    $"LyllyPlayer is up to date.\n\nInstalled: {installed}\nLatest: {offer.TagName}\n" +
                    $"Package: {offer.AssetName}",
                    GetAppTitleBase(),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            if (!offerInstall)
            {
                TopmostMessageBox.Show(
                    $"A newer LyllyPlayer is available.\n\nInstalled: {installed}\nLatest: {offer.TagName}\n" +
                    $"Package: {offer.AssetName}",
                    GetAppTitleBase(),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var choice = TopmostMessageBox.Show(
                $"Install LyllyPlayer {offer.Version} now?\n\n" +
                $"Installed: {installed}\nLatest: {offer.TagName}\n" +
                $"Download: {offer.AssetName}\n\n" +
                "The app will close while the updater replaces files in this folder. " +
                "A backup of the current folder is kept as LyllyPlayer.backup next to this install.",
                GetAppTitleBase(),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (choice != MessageBoxResult.Yes)
                return;

            await StartAppUpdateAndShutdownAsync(offer);
        }
        catch (Exception ex)
        {
            try { AppLog.Exception(ex, "App update check failed"); } catch { /* ignore */ }
            SetStatusMessage("ERROR", "Update check failed.");
        }
    }

    private Task StartAppUpdateAndShutdownAsync(AppReleaseOffer offer)
    {
        try
        {
            try { EnsurePlaybackShutdownBestEffort(); } catch { /* ignore */ }
            try { SaveSettingsSnapshot(); } catch { /* ignore */ }

            if (!AppUpdateService.TryLaunchUpdater(offer, out var err))
            {
                TopmostMessageBox.Show(
                    err ?? "Could not start the updater.",
                    GetAppTitleBase(),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return Task.CompletedTask;
            }

            try { AppLog.Warn($"Handing off to updater for {offer.Version} ({offer.DownloadUrl})"); } catch { /* ignore */ }
            System.Windows.Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            try { AppLog.Exception(ex, "Start app update failed"); } catch { /* ignore */ }
            TopmostMessageBox.Show(
                $"Could not start the update:\n\n{ex.Message}",
                GetAppTitleBase(),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }

        return Task.CompletedTask;
    }

    private void MaybeCheckAppUpdateOnStartup()
    {
        if (!_appUpdateCheckEnabled)
            return;
        if (!AppUpdateService.IsPortableUpdateSupported())
            return;
        if ((DateTime.UtcNow - _lastAppUpdateCheckUtc) < TimeSpan.FromDays(7))
            return;

        _ = Dispatcher.BeginInvoke(async () =>
        {
            try
            {
                await CheckAppUpdateNowAsync(offerInstall: false).ConfigureAwait(true);
            }
            catch { /* ignore */ }
        });
    }
}
