using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using LyllyPlayer.Utils;

namespace LyllyPlayer.Windows;

public partial class LocalFilesModal : Window
{
    private sealed class Win32OwnerWrapper : System.Windows.Forms.IWin32Window
    {
        public IntPtr Handle { get; }
        public Win32OwnerWrapper(IntPtr handle) => Handle = handle;
    }

    private readonly Func<bool> _getAppendDefault;
    private readonly Action<bool> _setAppendDefault;
    private readonly Func<bool> _getRemoveDuplicatesDefault;
    private readonly Action<bool> _setRemoveDuplicatesDefault;
    private readonly Func<bool> _getReadMetadataOnLoad;
    private readonly Func<bool> _getIncludeSubfoldersOnFolderLoad;
    // private readonly Func<string, bool, bool, bool, CancellationToken, Task> _addFolderAsync;
    private readonly Func<string, bool, bool, bool, CancellationToken, IProgress<(int done, int total)>?, Task> _addFolderAsync;
    private readonly Func<IReadOnlyList<string>, bool, bool, CancellationToken, IProgress<(int done, int total)>?, Task> _addFilesAsync;

    private CancellationTokenSource? _cts;
    private volatile bool _skipMetadataRequested;

    public LocalFilesModal(
    Func<bool> getAppendDefault,
    Action<bool> setAppendDefault,
    Func<bool> getRemoveDuplicatesDefault,
    Action<bool> setRemoveDuplicatesDefault,
    Func<bool> getReadMetadataOnLoad,
    Func<bool> getIncludeSubfoldersOnFolderLoad,
    Func<string, bool, bool, bool, CancellationToken, IProgress<(int, int)>?, Task> addFolderAsync,  // <-- added parameter
    Func<IReadOnlyList<string>, bool, bool, CancellationToken, IProgress<(int, int)>?, Task> addFilesAsync)
    {
        _getAppendDefault = getAppendDefault;
        _setAppendDefault = setAppendDefault;
        _getRemoveDuplicatesDefault = getRemoveDuplicatesDefault;
        _setRemoveDuplicatesDefault = setRemoveDuplicatesDefault;
        _getReadMetadataOnLoad = getReadMetadataOnLoad;
        _getIncludeSubfoldersOnFolderLoad = getIncludeSubfoldersOnFolderLoad;
        _addFolderAsync = addFolderAsync;
        _addFilesAsync = addFilesAsync;

        InitializeComponent();

        var append = false;
        var dedupe = false;
        try { append = _getAppendDefault(); } catch { append = false; }
        try { dedupe = _getRemoveDuplicatesDefault(); } catch { dedupe = false; }
        try { AppendRadio.IsChecked = append; } catch { /* ignore */ }
        try { ReplaceRadio.IsChecked = !append; } catch { /* ignore */ }
        try { RemoveDuplicatesCheckBox.IsChecked = dedupe; } catch { /* ignore */ }
    }

    private bool GetAppend() => AppendRadio.IsChecked == true;
    private bool GetRemoveDuplicates() => RemoveDuplicatesCheckBox.IsChecked == true;

    private void SetStatus(string msg)
    {
        try { StatusTextBlock.Text = msg; } catch { /* ignore */ }
    }

    private void SetBusy(bool busy, bool showSkipMetadata)
    {
        try
        {
            AddFolderButton.IsEnabled = !busy;
            AddFilesButton.IsEnabled = !busy;
            ReplaceRadio.IsEnabled = !busy;
            AppendRadio.IsEnabled = !busy;
            RemoveDuplicatesCheckBox.IsEnabled = !busy;
            CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            SkipMetadataButton.Visibility = (busy && showSkipMetadata) ? Visibility.Visible : Visibility.Collapsed;
        }
        catch { /* ignore */ }
    }

    private async Task RunAsync(Func<CancellationToken, Task> work, bool showSkipMetadata)
    {
        try
        {
            _skipMetadataRequested = false;
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            SetBusy(true, showSkipMetadata);
            SetStatus("Loading…");
            await work(_cts.Token).ConfigureAwait(true);
            SetStatus("Done.");
        }
        catch (OperationCanceledException)
        {
            SetStatus(_skipMetadataRequested ? "Retrying without metadata…" : "Cancelled.");
        }
        catch (Exception ex)
        {
            try { AppLog.Exception(ex, "Local files modal operation failed"); } catch { /* ignore */ }
            SetStatus($"Error: {ex.Message}");
        }
        finally
        {
            SetBusy(false, showSkipMetadata: false);
        }
    }

    private void ChromeBar_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        try { if (e.ClickCount == 2) return; DragMove(); } catch { /* ignore */ }
    }

    private void ChromeCloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        try { Close(); } catch { /* ignore */ }
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        try { Close(); } catch { /* ignore */ }
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }
    }

    private void SkipMetadataButton_OnClick(object sender, RoutedEventArgs e)
    {
        _skipMetadataRequested = true;
        try { _cts?.Cancel(); } catch { /* ignore */ }
    }

    private void ReplaceRadio_OnChecked(object sender, RoutedEventArgs e)
    {
        try { _setAppendDefault(false); } catch { /* ignore */ }
    }

    private void AppendRadio_OnChecked(object sender, RoutedEventArgs e)
    {
        try { _setAppendDefault(true); } catch { /* ignore */ }
    }

    private void RemoveDuplicatesCheckBox_OnChecked(object sender, RoutedEventArgs e)
    {
        try { _setRemoveDuplicatesDefault(true); } catch { /* ignore */ }
    }

    private void RemoveDuplicatesCheckBox_OnUnchecked(object sender, RoutedEventArgs e)
    {
        try { _setRemoveDuplicatesDefault(false); } catch { /* ignore */ }
    }

    private async void AddFolderButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var dlg = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Select folder containing audio files",
                UseDescriptionForTitle = true,
                ShowNewFolderButton = false,
            };

            try
            {
                using var top = new TopmostDialogOwner(this);
                var hwnd = top.OwnerHwnd;
                if (hwnd != IntPtr.Zero)
                {
                    if (dlg.ShowDialog(new Win32OwnerWrapper(hwnd)) != System.Windows.Forms.DialogResult.OK)
                        return;
                }
                else
                {
                    if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                        return;
                }
            }
            catch
            {
                if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                    return;
            }

            var folder = dlg.SelectedPath;
            if (string.IsNullOrWhiteSpace(folder))
                return;

            var append = GetAppend();
            var dedupe = GetRemoveDuplicates();
            var readMeta = false;
            try { readMeta = _getReadMetadataOnLoad(); } catch { readMeta = false; }

            // Progress reporter updates the status text block
            var progress = new Progress<(int done, int total)>(p =>
            {
                SetStatus($"Loading metadata… ({p.done} / {p.total} files)");
            });

            await RunAsync(
            async ct =>
            {
                await _addFolderAsync(folder, append, dedupe, false, ct, progress).ConfigureAwait(true);
            },
            showSkipMetadata: readMeta).ConfigureAwait(true);

            // If the user requested skip-metadata mid-load, retry once without metadata.
            if (_skipMetadataRequested && readMeta)
            {
                _skipMetadataRequested = false;
                await RunAsync(
                    async ct =>
                    {
                        await _addFolderAsync(folder, append, dedupe, true, ct, progress).ConfigureAwait(true);
                    },
                    showSkipMetadata: false).ConfigureAwait(true);
            }
        }
        catch
        {
            // ignore
        }
    }

    private async void AddFilesButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select audio files",
                Filter = "Audio files|*.mp3;*.flac;*.wav;*.aac;*.m4a;*.wma;*.ogg;*.opus;*.aiff;*.alac|All files (*.*)|*.*",
                CheckFileExists = true,
                CheckPathExists = true,
                Multiselect = true
            };

            using var top = new TopmostDialogOwner(this);
            if (dlg.ShowDialog(top.OwnerWindow) != true)
                return;

            var files = dlg.FileNames?.Where(f => !string.IsNullOrWhiteSpace(f)).ToList() ?? new List<string>();
            if (files.Count == 0)
                return;

            var append = GetAppend();
            var dedupe = GetRemoveDuplicates();
            var progress = new Progress<(int done, int total)>(p =>
               SetStatus($"Loading metadata… ({p.done} / {p.total} files)"));

            await RunAsync(
                ct => _addFilesAsync(files, append, dedupe, ct, progress),
                showSkipMetadata: false);
        }
        catch
        {
            // ignore
        }
    }
}
