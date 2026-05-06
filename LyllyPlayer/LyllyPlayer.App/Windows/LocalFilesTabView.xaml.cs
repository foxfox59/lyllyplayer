using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using LyllyPlayer.Utils;

namespace LyllyPlayer.Windows;

public partial class LocalFilesTabView : System.Windows.Controls.UserControl
{
    private sealed class Win32OwnerWrapper : System.Windows.Forms.IWin32Window
    {
        public IntPtr Handle { get; }
        public Win32OwnerWrapper(IntPtr handle) => Handle = handle;
    }

    private Func<bool>? _getAppendDefault;
    private Action<bool>? _setAppendDefault;
    private Func<bool>? _getRemoveDuplicatesDefault;
    private Action<bool>? _setRemoveDuplicatesDefault;
    private Func<bool>? _getReadMetadataOnLoad;
    private Func<bool>? _getIncludeSubfoldersOnFolderLoad;
    private Func<string, bool, bool, bool, CancellationToken, IProgress<(int done, int total)>?, Task>? _addFolderAsync;
    private Func<IReadOnlyList<string>, bool, bool, CancellationToken, IProgress<(int done, int total)>?, Task>? _addFilesAsync;

    private CancellationTokenSource? _cts;
    private volatile bool _skipMetadataRequested;

    public LocalFilesTabView()
    {
        InitializeComponent();
    }

    public void Initialize(
        Func<bool> getAppendDefault,
        Action<bool> setAppendDefault,
        Func<bool> getRemoveDuplicatesDefault,
        Action<bool> setRemoveDuplicatesDefault,
        Func<bool> getReadMetadataOnLoad,
        Func<bool> getIncludeSubfoldersOnFolderLoad,
        Func<string, bool, bool, bool, CancellationToken, IProgress<(int, int)>?, Task> addFolderAsync,
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

        var append = false;
        var dedupe = false;
        try { append = _getAppendDefault?.Invoke() ?? false; } catch { append = false; }
        try { dedupe = _getRemoveDuplicatesDefault?.Invoke() ?? false; } catch { dedupe = false; }
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
            try { AppLog.Exception(ex, "Local files tab operation failed"); } catch { /* ignore */ }
            SetStatus($"Error: {ex.Message}");
        }
        finally
        {
            SetBusy(false, showSkipMetadata: false);
        }
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
        try { _setAppendDefault?.Invoke(false); } catch { /* ignore */ }
    }

    private void AppendRadio_OnChecked(object sender, RoutedEventArgs e)
    {
        try { _setAppendDefault?.Invoke(true); } catch { /* ignore */ }
    }

    private void RemoveDuplicatesCheckBox_OnChecked(object sender, RoutedEventArgs e)
    {
        try { _setRemoveDuplicatesDefault?.Invoke(true); } catch { /* ignore */ }
    }

    private void RemoveDuplicatesCheckBox_OnUnchecked(object sender, RoutedEventArgs e)
    {
        try { _setRemoveDuplicatesDefault?.Invoke(false); } catch { /* ignore */ }
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
                var owner = Window.GetWindow(this);
                var hwnd = owner is not null ? new System.Windows.Interop.WindowInteropHelper(owner).Handle : IntPtr.Zero;
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
            try { readMeta = _getReadMetadataOnLoad?.Invoke() ?? false; } catch { readMeta = false; }

            var progress = new Progress<(int done, int total)>(p =>
            {
                SetStatus($"Loading metadata… ({p.done} / {p.total} files)");
            });

            await RunAsync(
                async ct =>
                {
                    if (_addFolderAsync is null) throw new InvalidOperationException("Local tab not initialized.");
                    await _addFolderAsync(folder, append, dedupe, false, ct, progress).ConfigureAwait(true);
                },
                showSkipMetadata: readMeta).ConfigureAwait(true);

            if (_skipMetadataRequested && readMeta)
            {
                _skipMetadataRequested = false;
                await RunAsync(
                    async ct =>
                    {
                        if (_addFolderAsync is null) throw new InvalidOperationException("Local tab not initialized.");
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

            if (dlg.ShowDialog(Window.GetWindow(this)) != true)
                return;

            var files = dlg.FileNames?.Where(f => !string.IsNullOrWhiteSpace(f)).ToList() ?? new List<string>();
            if (files.Count == 0)
                return;

            var append = GetAppend();
            var dedupe = GetRemoveDuplicates();
            var progress = new Progress<(int done, int total)>(p =>
                SetStatus($"Loading metadata… ({p.done} / {p.total} files)"));

            await RunAsync(
                ct =>
                {
                    if (_addFilesAsync is null) throw new InvalidOperationException("Local tab not initialized.");
                    return _addFilesAsync(files, append, dedupe, ct, progress);
                },
                showSkipMetadata: false);
        }
        catch
        {
            // ignore
        }
    }
}

