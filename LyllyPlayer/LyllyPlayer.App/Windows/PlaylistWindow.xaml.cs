using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.Collections;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using Forms = System.Windows.Forms;
using LyllyPlayer.Models;
using LyllyPlayer.Utils;
using System.Threading;

namespace LyllyPlayer.Windows;

public partial class PlaylistWindow : Window
{
    private sealed class SortChoice
    {
        public PlaylistSortMode Mode { get; }
        public string Label { get; }
        public SortChoice(PlaylistSortMode mode, string label)
        {
            Mode = mode;
            Label = label;
        }
        public override string ToString() => Label;
    }

    private sealed class Win32OwnerWrapper : Forms.IWin32Window
    {
        public IntPtr Handle { get; }
        public Win32OwnerWrapper(IntPtr handle) => Handle = handle;
    }

    private Window GetDialogOwnerWindow()
        => System.Windows.Application.Current?.MainWindow ?? this;

    private readonly Func<string, Task> _loadUrlAsync;
    private readonly Func<(int count, int minLengthSeconds)> _getSearchDefaults;
    private readonly Action<int, int> _setSearchDefaults;
    private readonly Func<Window, CancellationToken, Task> _openYoutubeModalAsync;
    private readonly Func<PlaylistSortSpec, CancellationToken, Task> _applySortAsync;
    private readonly Func<bool> _getIsYoutubeSource;
    private readonly Func<string, string, Task> _savePlaylistToFileAsync;
    private readonly Func<string, Task> _loadPlaylistFromFileAsync;
    private readonly Func<IReadOnlyList<PlaylistEntry>, string?, string, CancellationToken, Task> _loadEntriesAsync;
    private readonly Func<CancellationToken, Task> _refreshAsync;
    private readonly Action? _capturePlaylistForCancelRestore;
    private readonly Action? _commitPlaylistCancelRestore;
    private readonly Action? _rollbackPlaylistCancelRestore;
    private readonly Action<string> _sourceChanged;
    private readonly Func<string> _getSource;
    private readonly Func<string> _getLastYoutubeUrl;
    private readonly Action<string> _setLastYoutubeUrl;
    private readonly Func<string> _getFfmpegPath;
    private readonly Func<bool> _getIncludeSubfoldersOnFolderLoad;
    private readonly Func<bool> _getReadMetadataOnLoad;
    private readonly Func<bool> _getKeepIncompletePlaylistOnCancel;
    private readonly Func<bool> _getRefreshOffersMetadataSkip;
    private readonly Func<CancellationToken, Task> _refreshLocalWithoutMetadataAsync;
    private readonly Action<string> _selectedVideoIdChanged;
    private readonly Func<string, Task> _doubleClickPlayAsync;
    private int _lastClickedIndex = -1;
    private int _centerRequestId;
    /// <summary>Avoid one frame at the top of the list before <see cref="CenterListBoxOnQueueItem"/> runs (initial open).</summary>
    private bool _suppressQueueListUntilInitialScroll;
    private DispatcherTimer? _initialScrollMaskFailsafeTimer;
    private int _busyCount;
    private string _busyMessage = "Loading...";
    private CancellationTokenSource? _busyCts;
    /// <summary>When true, busy overlay may offer "skip metadata" during slow folder or M3U loads; cancel handler records rollback vs skip.</summary>
    private volatile bool _busyShowsFolderMetadataSkip;
    /// <summary>When true, busy overlay offers "Stop search" (keep partial results if any).</summary>
    private volatile bool _busyShowsSearchStop;
    /// <summary>0 = unset, 1 = cancel (rollback playlist), 2 = skip metadata then reload the same folder or M3U without ffprobe.</summary>
    private int _folderMetadataBusyUserChoice;
    /// <summary>0 = unset, 1 = Cancel (full rollback), 2 = Stop search (keep playlist if non-empty).</summary>
    private int _searchOverlayDismissKind;

    private bool _chromeDragging;
    private System.Windows.Point _chromeDragStartScreen;
    private double _chromeDragStartLeft;
    private double _chromeDragStartTop;

    private ObservableCollection<QueueItem>? _queueSource;
    private CollectionViewSource? _queueViewSource;
    private string _playlistFilterQuery = "";
    private readonly DispatcherTimer _playlistFilterDebounceTimer;

    private bool _suppressSortUiEvents;
    private PlaylistSortSpec _lastSortSpec = new(PlaylistSortMode.None, PlaylistSortDirection.Asc);

    public PlaylistWindow(
        Func<string, Task> loadUrlAsync,
        Func<(int count, int minLengthSeconds)> getSearchDefaults,
        Action<int, int> setSearchDefaults,
        Func<Window, CancellationToken, Task> openYoutubeModalAsync,
        Func<PlaylistSortSpec, CancellationToken, Task> applySortAsync,
        Func<bool> getIsYoutubeSource,
        Func<string, string, Task> savePlaylistToFileAsync,
        Func<string, Task> loadPlaylistFromFileAsync,
        Func<IReadOnlyList<PlaylistEntry>, string?, string, CancellationToken, Task> loadEntriesAsync,
        Func<CancellationToken, Task> refreshAsync,
        Action? capturePlaylistForCancelRestore,
        Action? commitPlaylistCancelRestore,
        Action? rollbackPlaylistCancelRestore,
        Action<string> sourceChanged,
        Func<string> getSource,
        Func<string> getLastYoutubeUrl,
        Action<string> setLastYoutubeUrl,
        Func<string> getFfmpegPath,
        Func<bool> getIncludeSubfoldersOnFolderLoad,
        Func<bool> getReadMetadataOnLoad,
        Func<bool> getKeepIncompletePlaylistOnCancel,
        Func<bool> getRefreshOffersMetadataSkip,
        Func<CancellationToken, Task> refreshLocalWithoutMetadataAsync,
        Action<string> selectedVideoIdChanged,
        Func<string, Task> doubleClickPlayAsync
    )
    {
        _loadUrlAsync = loadUrlAsync;
        _getSearchDefaults = getSearchDefaults;
        _setSearchDefaults = setSearchDefaults;
        _openYoutubeModalAsync = openYoutubeModalAsync;
        _applySortAsync = applySortAsync;
        _getIsYoutubeSource = getIsYoutubeSource;
        _savePlaylistToFileAsync = savePlaylistToFileAsync;
        _loadPlaylistFromFileAsync = loadPlaylistFromFileAsync;
        _loadEntriesAsync = loadEntriesAsync;
        _refreshAsync = refreshAsync;
        _capturePlaylistForCancelRestore = capturePlaylistForCancelRestore;
        _commitPlaylistCancelRestore = commitPlaylistCancelRestore;
        _rollbackPlaylistCancelRestore = rollbackPlaylistCancelRestore;
        _sourceChanged = sourceChanged;
        _getSource = getSource;
        _getLastYoutubeUrl = getLastYoutubeUrl;
        _setLastYoutubeUrl = setLastYoutubeUrl;
        _getFfmpegPath = getFfmpegPath;
        _getIncludeSubfoldersOnFolderLoad = getIncludeSubfoldersOnFolderLoad;
        _getReadMetadataOnLoad = getReadMetadataOnLoad;
        _getKeepIncompletePlaylistOnCancel = getKeepIncompletePlaylistOnCancel;
        _getRefreshOffersMetadataSkip = getRefreshOffersMetadataSkip;
        _refreshLocalWithoutMetadataAsync = refreshLocalWithoutMetadataAsync;
        _selectedVideoIdChanged = selectedVideoIdChanged;
        _doubleClickPlayAsync = doubleClickPlayAsync;

        InitializeComponent();

        SourceTextBox.Text = _getSource();
        InitializeSortUi();

        _playlistFilterDebounceTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(120),
        };
        _playlistFilterDebounceTimer.Tick += (_, _) =>
        {
            try
            {
                _playlistFilterDebounceTimer.Stop();
                ApplyPlaylistFilterFromTextBox();
            }
            catch
            {
                // ignore
            }
        };
    }

    private void InitializeSortUi()
    {
        try
        {
            _suppressSortUiEvents = true;
            SortDescToggleButton.IsChecked = false;
            SortDescToggleButton.Content = "Asc";
            RefreshSortChoices();
        }
        catch { /* ignore */ }
        finally { _suppressSortUiEvents = false; }
    }

    public void RefreshSortChoices()
    {
        try
        {
            _suppressSortUiEvents = true;
            var isYoutube = false;
            try { isYoutube = _getIsYoutubeSource(); } catch { /* ignore */ }

            var items = new List<SortChoice>
            {
                new(PlaylistSortMode.None, "None"),
                new(PlaylistSortMode.NameOrTitle, isYoutube ? "Title" : "Name"),
                new(PlaylistSortMode.ChannelOrPath, isYoutube ? "Channel" : "Path + filename"),
                new(PlaylistSortMode.Duration, "Duration"),
            };

            var wantMode = _lastSortSpec.Mode;
            SortModeComboBox.ItemsSource = items;
            SortModeComboBox.DisplayMemberPath = "Label";
            SortModeComboBox.SelectedValuePath = "Mode";
            // Force the SelectionBoxItem to update when labels change (YouTube vs Local).
            try { SortModeComboBox.SelectedIndex = -1; } catch { /* ignore */ }
            SortModeComboBox.SelectedValue = wantMode;
        }
        catch { /* ignore */ }
        finally { _suppressSortUiEvents = false; }
    }

    public PlaylistSortSpec GetSortSpec()
    {
        try { return _lastSortSpec; } catch { return new PlaylistSortSpec(PlaylistSortMode.None, PlaylistSortDirection.Asc); }
    }

    public void SetSortSpec(PlaylistSortSpec spec)
    {
        try
        {
            _suppressSortUiEvents = true;
            _lastSortSpec = spec;
            try { SortModeComboBox.SelectedValue = spec.Mode; } catch { /* ignore */ }
            var desc = spec.Direction == PlaylistSortDirection.Desc;
            try { SortDescToggleButton.IsChecked = desc; } catch { /* ignore */ }
            try { SortDescToggleButton.Content = desc ? "Desc" : "Asc"; } catch { /* ignore */ }
        }
        catch { /* ignore */ }
        finally { _suppressSortUiEvents = false; }
    }

    private PlaylistSortSpec GetSortSpecFromUi()
    {
        var mode = PlaylistSortMode.None;
        try
        {
            if (SortModeComboBox.SelectedValue is PlaylistSortMode sm)
                mode = sm;
        }
        catch { /* ignore */ }
        var dir = (SortDescToggleButton.IsChecked ?? false) ? PlaylistSortDirection.Desc : PlaylistSortDirection.Asc;
        return new PlaylistSortSpec(mode, dir);
    }

    private async Task ApplySortFromUiAsync()
    {
        if (_busyCount > 0)
            return;
        var spec = GetSortSpecFromUi();
        if (spec.Equals(_lastSortSpec))
            return;
        _lastSortSpec = spec;

        using var cts = new CancellationTokenSource();
        try
        {
            Interlocked.Increment(ref _busyCount);
            SetLoadEnabled(false);
            SetBusy("Sorting...");
            await _applySortAsync(spec, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // ignore
        }
        catch
        {
            // ignore
        }
        finally
        {
            ClearBusy();
            Interlocked.Decrement(ref _busyCount);
            SetLoadEnabled(true);
        }
    }

    private async void SortModeComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSortUiEvents)
            return;
        await ApplySortFromUiAsync();
    }

    private async void SortDescToggleButton_OnChecked(object sender, RoutedEventArgs e)
    {
        try { SortDescToggleButton.Content = "Desc"; } catch { /* ignore */ }
        if (_suppressSortUiEvents)
            return;
        await ApplySortFromUiAsync();
    }

    private async void SortDescToggleButton_OnUnchecked(object sender, RoutedEventArgs e)
    {
        try { SortDescToggleButton.Content = "Asc"; } catch { /* ignore */ }
        if (_suppressSortUiEvents)
            return;
        await ApplySortFromUiAsync();
    }

    private void ChromeCloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        try { Close(); } catch { /* ignore */ }
    }

    private void ChromeBar_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
            return;

        try
        {
            _chromeDragging = true;
            _chromeDragStartLeft = Left;
            _chromeDragStartTop = Top;
            _chromeDragStartScreen = PointToScreen(e.GetPosition(this));

            CaptureMouse();
            MouseMove -= ChromeDrag_MouseMove;
            MouseLeftButtonUp -= ChromeDrag_MouseLeftButtonUp;
            MouseMove += ChromeDrag_MouseMove;
            MouseLeftButtonUp += ChromeDrag_MouseLeftButtonUp;

            e.Handled = true;
        }
        catch
        {
            _chromeDragging = false;
        }
    }

    private void ChromeDrag_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_chromeDragging)
            return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndChromeDrag();
            return;
        }

        try
        {
            var cur = PointToScreen(e.GetPosition(this));
            var dx = cur.X - _chromeDragStartScreen.X;
            var dy = cur.Y - _chromeDragStartScreen.Y;
            Left = _chromeDragStartLeft + dx;
            Top = _chromeDragStartTop + dy;
        }
        catch
        {
            EndChromeDrag();
        }
    }

    private void ChromeDrag_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => EndChromeDrag();

    private void EndChromeDrag()
    {
        if (!_chromeDragging)
            return;
        _chromeDragging = false;
        try { ReleaseMouseCapture(); } catch { /* ignore */ }
        MouseMove -= ChromeDrag_MouseMove;
        MouseLeftButtonUp -= ChromeDrag_MouseLeftButtonUp;
    }

    public void SetItemsSource(IEnumerable itemsSource)
    {
        if (itemsSource is not ObservableCollection<QueueItem> oc)
        {
            _queueSource = null;
            _queueViewSource = null;
            QueueListBox.ItemsSource = itemsSource;
            return;
        }

        if (_queueViewSource is null || !ReferenceEquals(_queueSource, oc))
        {
            _queueSource = oc;
            _queueViewSource = new CollectionViewSource { Source = _queueSource };
            _queueViewSource.View.Filter = QueueFilterPredicate;
            QueueListBox.ItemsSource = _queueViewSource.View;
        }

        try
        {
            _queueViewSource.View.Refresh();
        }
        catch
        {
            // ignore
        }
    }

    private bool QueueFilterPredicate(object obj)
    {
        if (obj is not QueueItem qi)
            return false;

        var q = (_playlistFilterQuery ?? "").Trim();
        if (q.Length == 0)
            return true;

        return MatchesPlaylistFilterTokens(q, qi);
    }

    private static bool MatchesPlaylistFilterTokens(string query, QueueItem qi)
    {
        var haystack = $"{qi.Title}\u001f{qi.Channel ?? ""}\u001f{qi.DisplayTitle}\u001f{qi.WebpageUrl}".ToLowerInvariant();
        foreach (var token in query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (token.Length == 0)
                continue;
            if (!haystack.Contains(token.ToLowerInvariant(), StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    private void ApplyPlaylistFilterFromTextBox()
    {
        try
        {
            _playlistFilterQuery = PlaylistFilterTextBox?.Text ?? "";
            _queueViewSource?.View.Refresh();
        }
        catch
        {
            // ignore
        }
    }

    private void PlaylistFilterTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        try
        {
            _playlistFilterDebounceTimer.Stop();
            _playlistFilterDebounceTimer.Start();
        }
        catch
        {
            // ignore
        }
    }

    private void PlaylistFilterClearButton_OnClick(object sender, RoutedEventArgs e) => ClearPlaylistViewFilter();

    /// <summary>Clears the view-only list filter (same as Clear). Used when a new playlist replaces the queue.</summary>
    public void ClearPlaylistViewFilter()
    {
        try
        {
            _playlistFilterDebounceTimer.Stop();
            _playlistFilterQuery = "";
            if (PlaylistFilterTextBox is not null)
                PlaylistFilterTextBox.Text = "";
            _queueViewSource?.View.Refresh();
        }
        catch
        {
            // ignore
        }
    }
    /// <summary>Plain filter box text for settings persistence (may be empty).</summary>
    public string GetPlaylistFilterText() => PlaylistFilterTextBox?.Text ?? "";

    /// <summary>Restore filter from settings after <see cref="SetItemsSource"/> so the view applies the same query.</summary>
    public void ApplyPersistedPlaylistFilter(string? persisted)
    {
        var t = persisted?.Trim() ?? "";
        if (t.Length > 500)
            t = t.Substring(0, 500);
        _playlistFilterQuery = t;
        try
        {
            if (PlaylistFilterTextBox is not null)
                PlaylistFilterTextBox.Text = t;
        }
        catch
        {
            // ignore
        }

        try
        {
            _queueViewSource?.View.Refresh();
        }
        catch
        {
            // ignore
        }
    }

    public void SetRefreshEnabled(bool enabled) => RefreshButton.IsEnabled = enabled;
    public void SetSourceText(string source)
    {
        try { SourceTextBox.Text = source ?? ""; } catch { /* ignore */ }
    }
    public void SetLoadEnabled(bool enabled)
    {
        if (_busyCount > 0)
            enabled = false;
        SearchYoutubeButton.IsEnabled = enabled;
        LoadUrlButton.IsEnabled = enabled;
        LoadM3uButton.IsEnabled = enabled;
        LoadFolderButton.IsEnabled = enabled;
        SavePlaylistButton.IsEnabled = enabled;
        LoadSavedButton.IsEnabled = enabled;
        try { PlaylistFilterTextBox.IsEnabled = enabled; } catch { /* ignore */ }
        try { PlaylistFilterClearButton.IsEnabled = enabled; } catch { /* ignore */ }
    }

    private void SetBusy(string? message, CancellationTokenSource? cancellable = null, bool showSkipFolderMetadata = false, bool showSearchStop = false)
    {
        _busyMessage = string.IsNullOrWhiteSpace(message) ? "Loading..." : message!.Trim();
        _busyCts = cancellable;
        _busyShowsSearchStop = showSearchStop && cancellable is not null;
        _busyShowsFolderMetadataSkip = showSkipFolderMetadata && cancellable is not null && !_busyShowsSearchStop;
        void Apply()
        {
            ClearBusyOverlayProgressCore();
            try { BusyOverlayText.Text = _busyMessage; } catch { /* ignore */ }
            try
            {
                BusyOverlaySkipFolderMetadataButton.Visibility = _busyShowsFolderMetadataSkip
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
            catch { /* ignore */ }
            try
            {
                BusyOverlayStopSearchButton.Visibility = _busyShowsSearchStop
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
            catch { /* ignore */ }
            try
            {
                BusyOverlayCancelButton.Visibility = cancellable is not null
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
            catch { /* ignore */ }
            try { BusyOverlay.Visibility = Visibility.Visible; } catch { /* ignore */ }
        }

        try
        {
            if (Dispatcher.CheckAccess())
                Apply();
            else
                Dispatcher.Invoke(Apply);
        }
        catch { /* ignore */ }
    }

    private void ClearBusy()
    {
        _busyCts = null;
        _busyShowsFolderMetadataSkip = false;
        _busyShowsSearchStop = false;
        void Apply()
        {
            ClearBusyOverlayProgressCore();
            try { BusyOverlaySkipFolderMetadataButton.Visibility = Visibility.Collapsed; } catch { /* ignore */ }
            try { BusyOverlayStopSearchButton.Visibility = Visibility.Collapsed; } catch { /* ignore */ }
            try { BusyOverlayCancelButton.Visibility = Visibility.Collapsed; } catch { /* ignore */ }
            try { BusyOverlay.Visibility = Visibility.Collapsed; } catch { /* ignore */ }
        }

        try
        {
            if (Dispatcher.CheckAccess())
                Apply();
            else
                Dispatcher.Invoke(Apply);
        }
        catch { /* ignore */ }
    }

    /// <summary>MainWindow reads after search cancellation to decide rollback vs keep partial.</summary>
    public int TakeSearchDismissKind() => Interlocked.Exchange(ref _searchOverlayDismissKind, 0);

    private void ClearBusyOverlayProgressCore()
    {
        try
        {
            BusyOverlayProgressBar.BeginAnimation(System.Windows.Controls.ProgressBar.ValueProperty, null);
            BusyOverlayProgressBar.IsIndeterminate = false;
            BusyOverlayProgressBar.Value = 0;
            BusyOverlayProgressBar.Visibility = Visibility.Collapsed;
        }
        catch { /* ignore */ }
    }

    public void ClearBusyOverlayProgress()
    {
        void Apply() => ClearBusyOverlayProgressCore();
        try
        {
            if (Dispatcher.CheckAccess())
                Apply();
            else
                Dispatcher.Invoke(Apply);
        }
        catch { /* ignore */ }
    }

    public void ReportBusyOverlayDeterminate(double value01)
    {
        var v = Math.Clamp(value01, 0, 1);
        void Apply()
        {
            try
            {
                // Default WPF ProgressBar template animates Value; rapid reports look "stuck" until we clear it.
                BusyOverlayProgressBar.BeginAnimation(System.Windows.Controls.ProgressBar.ValueProperty, null);
                BusyOverlayProgressBar.IsIndeterminate = false;
                BusyOverlayProgressBar.Maximum = 1;
                BusyOverlayProgressBar.Value = v;
                BusyOverlayProgressBar.Visibility = Visibility.Visible;
            }
            catch { /* ignore */ }
        }

        try
        {
            if (Dispatcher.CheckAccess())
                Apply();
            else
                Dispatcher.BeginInvoke(Apply, DispatcherPriority.Normal);
        }
        catch { /* ignore */ }
    }

    public void ReportBusyOverlayIndeterminate()
    {
        void Apply()
        {
            try
            {
                BusyOverlayProgressBar.BeginAnimation(System.Windows.Controls.ProgressBar.ValueProperty, null);
                BusyOverlayProgressBar.IsIndeterminate = true;
                BusyOverlayProgressBar.Value = 0;
                BusyOverlayProgressBar.Visibility = Visibility.Visible;
            }
            catch { /* ignore */ }
        }

        try
        {
            if (Dispatcher.CheckAccess())
                Apply();
            else
                Dispatcher.BeginInvoke(Apply, DispatcherPriority.Normal);
        }
        catch { /* ignore */ }
    }

    private IProgress<(int done, int total)>? CreateMetadataLoadProgress()
        => new Progress<(int done, int total)>(v =>
        {
            if (v.total <= 0)
                return;
            ReportBusyOverlayDeterminate((double)v.done / v.total);
        });

    private void BusyOverlayCancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_busyShowsSearchStop)
                Interlocked.Exchange(ref _searchOverlayDismissKind, 1);
            else if (_busyShowsFolderMetadataSkip)
                Interlocked.Exchange(ref _folderMetadataBusyUserChoice, 1);
            _busyCts?.Cancel();
        }
        catch { /* ignore */ }
    }

    private void BusyOverlayStopSearchButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Interlocked.Exchange(ref _searchOverlayDismissKind, 2);
            _busyCts?.Cancel();
        }
        catch { /* ignore */ }
    }

    private void BusyOverlaySkipFolderMetadataButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Interlocked.Exchange(ref _folderMetadataBusyUserChoice, 2);
            _busyCts?.Cancel();
        }
        catch { /* ignore */ }
    }

    public void ScrollToIndex(int index)
    {
        if (index < 0)
            return;
        if (_queueSource is null || index >= _queueSource.Count)
            return;

        try
        {
            QueueListBox.ScrollIntoView(_queueSource[index]);
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>Call before <see cref="Window.Show"/> so the first paint does not flash the top of the list before scroll-to-now-playing.</summary>
    public void BeginSuppressQueueListUntilInitialScroll()
    {
        try
        {
            _suppressQueueListUntilInitialScroll = true;
            QueueListBox.Opacity = 0;
            if (_initialScrollMaskFailsafeTimer is null)
            {
                _initialScrollMaskFailsafeTimer = new DispatcherTimer(DispatcherPriority.Background)
                {
                    Interval = TimeSpan.FromMilliseconds(350),
                };
                _initialScrollMaskFailsafeTimer.Tick += (_, _) =>
                {
                    try { _initialScrollMaskFailsafeTimer!.Stop(); } catch { /* ignore */ }
                    try { EndSuppressQueueListUntilInitialScroll(); } catch { /* ignore */ }
                };
            }
            try { _initialScrollMaskFailsafeTimer.Stop(); } catch { /* ignore */ }
            try { _initialScrollMaskFailsafeTimer.Start(); } catch { /* ignore */ }
        }
        catch
        {
            // ignore
        }
    }

    public void EndSuppressQueueListUntilInitialScroll()
    {
        try
        {
            if (!_suppressQueueListUntilInitialScroll)
                return;
            _suppressQueueListUntilInitialScroll = false;
            QueueListBox.Opacity = 1;
            try { _initialScrollMaskFailsafeTimer?.Stop(); } catch { /* ignore */ }
        }
        catch
        {
            // ignore
        }
    }

    public void CenterIndex(int index)
    {
        if (index < 0)
            return;
        if (_queueSource is null || index >= _queueSource.Count)
            return;

        var item = _queueSource[index];
        var req = Interlocked.Increment(ref _centerRequestId);
        CenterListBoxOnQueueItem(QueueListBox, item, req);
    }

    private async void LoadUrlButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_busyCount > 0)
                return;

            var initial = "";
            try { initial = _getLastYoutubeUrl() ?? ""; } catch { initial = ""; }
            var dlg = new LoadUrlDialog(initial) { Owner = GetDialogOwnerWindow() };
            if (dlg.ShowDialog() != true)
                return;

            var url = dlg.UrlText;
            if (string.IsNullOrWhiteSpace(url))
                return;

            // Update the read-only textbox to reflect the current source.
            SourceTextBox.Text = url;
            _sourceChanged(url);
            try { _setLastYoutubeUrl(url); } catch { /* ignore */ }
            Interlocked.Increment(ref _busyCount);
            SetLoadEnabled(false);
            SetBusy("Loading...");
            try
            {
                await _loadUrlAsync(url);
            }
            finally
            {
                ClearBusy();
                Interlocked.Decrement(ref _busyCount);
                SetLoadEnabled(true);
            }
        }
        catch
        {
            // ignore
        }
    }

    private async void SearchYoutubeButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_busyCount > 0)
                return;
            using var cts = new CancellationTokenSource();
            Interlocked.Increment(ref _busyCount);
            SetLoadEnabled(false);
            try
            {
                SetBusy("YouTube...");
                await _openYoutubeModalAsync(this, cts.Token);
            }
            finally
            {
                ClearBusy();
                Interlocked.Decrement(ref _busyCount);
                SetLoadEnabled(true);
            }
        }
        catch (Exception ex)
        {
            try
            {
                System.Windows.MessageBox.Show(this, $"YouTube failed.\n\n{ex.Message}", "YouTube", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch { /* ignore */ }
        }
    }

    private async void SavePlaylistButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_busyCount > 0)
                return;

            var s = (SourceTextBox.Text ?? "").Trim();
            var safe = string.IsNullOrWhiteSpace(s) ? "playlist" : MakeSafeFileName(s);
            if (safe.Length > 80) safe = safe.Substring(0, 80);

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Save playlist",
                Filter = "Playlist JSON (*.json)|*.json|All files (*.*)|*.*",
                AddExtension = true,
                DefaultExt = ".json",
                FileName = $"{safe}.json",
                OverwritePrompt = true
            };

            if (dlg.ShowDialog(GetDialogOwnerWindow()) != true)
                return;

            Interlocked.Increment(ref _busyCount);
            SetLoadEnabled(false);
            SetBusy("Saving...");
            try
            {
                // Persist playlist title (not the source URL/path). The MainWindow delegate will use its current
                // playlist title when displayName is empty.
                await _savePlaylistToFileAsync(dlg.FileName, "");
            }
            finally
            {
                ClearBusy();
                Interlocked.Decrement(ref _busyCount);
                SetLoadEnabled(true);
            }
        }
        catch
        {
            // ignore
        }
    }

    private async void LoadSavedButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_busyCount > 0)
                return;

            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Load saved playlist",
                Filter = "Playlist JSON (*.json)|*.json|All files (*.*)|*.*",
                CheckFileExists = true,
                CheckPathExists = true,
                Multiselect = false
            };

            if (dlg.ShowDialog(GetDialogOwnerWindow()) != true)
                return;

            Interlocked.Increment(ref _busyCount);
            SetLoadEnabled(false);
            SetBusy("Loading...");
            try
            {
                await _loadPlaylistFromFileAsync(dlg.FileName);
            }
            finally
            {
                ClearBusy();
                Interlocked.Decrement(ref _busyCount);
                SetLoadEnabled(true);
            }
        }
        catch
        {
            // ignore
        }
    }

    private static string MakeSafeFileName(string input)
    {
        var s = (input ?? "").Trim();
        if (string.IsNullOrWhiteSpace(s))
            return "playlist";
        foreach (var c in Path.GetInvalidFileNameChars())
            s = s.Replace(c, '_');
        s = s.Replace(':', '_');
        s = s.Replace('/', '_').Replace('\\', '_');
        s = s.Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ');
        while (s.Contains("  "))
            s = s.Replace("  ", " ");
        return s.Trim();
    }

    private async void LoadM3uButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_busyCount > 0)
                return;

            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select M3U playlist",
                Filter = "M3U playlist (*.m3u;*.m3u8)|*.m3u;*.m3u8|All files (*.*)|*.*",
                CheckFileExists = true,
                CheckPathExists = true,
                Multiselect = false
            };

            if (dlg.ShowDialog(GetDialogOwnerWindow()) != true)
                return;

            var path = dlg.FileName;
            var readMeta = false;
            try { readMeta = _getReadMetadataOnLoad(); } catch { readMeta = false; }

            using var cts = new CancellationTokenSource();
            Interlocked.Exchange(ref _folderMetadataBusyUserChoice, 0);
            Interlocked.Increment(ref _busyCount);
            SetLoadEnabled(false);
            try { _capturePlaylistForCancelRestore?.Invoke(); } catch { /* ignore */ }
            SetBusy("Loading...", cts, showSkipFolderMetadata: readMeta);
            try
            {
                var (entries, title) = await LocalPlaylistLoader.LoadM3uAsync(
                    path,
                    _getFfmpegPath(),
                    readMetadataOnLoad: readMeta,
                    cts.Token,
                    metadataProgress: readMeta ? CreateMetadataLoadProgress() : null).ConfigureAwait(true);
                SourceTextBox.Text = path;
                _sourceChanged(path);
                await _loadEntriesAsync(entries, title, path, cts.Token).ConfigureAwait(true);
                try { _commitPlaylistCancelRestore?.Invoke(); } catch { /* ignore */ }
            }
            catch (OperationCanceledException)
            {
                var choice = Interlocked.Exchange(ref _folderMetadataBusyUserChoice, 0);
                if (choice == 2 && readMeta)
                {
                    try
                    {
                        var (entriesFast, titleFast) = await LocalPlaylistLoader.LoadM3uAsync(
                            path,
                            _getFfmpegPath(),
                            readMetadataOnLoad: false,
                            CancellationToken.None).ConfigureAwait(true);
                        SourceTextBox.Text = path;
                        _sourceChanged(path);
                        await _loadEntriesAsync(entriesFast, titleFast, path, CancellationToken.None).ConfigureAwait(true);
                        try { _commitPlaylistCancelRestore?.Invoke(); } catch { /* ignore */ }
                    }
                    catch (OperationCanceledException)
                    {
                        try { _rollbackPlaylistCancelRestore?.Invoke(); } catch { /* ignore */ }
                    }
                    catch
                    {
                        try { _commitPlaylistCancelRestore?.Invoke(); } catch { /* ignore */ }
                    }
                }
                else
                {
                    try { _rollbackPlaylistCancelRestore?.Invoke(); } catch { /* ignore */ }
                }
            }
            catch
            {
                try { _commitPlaylistCancelRestore?.Invoke(); } catch { /* ignore */ }
            }
            finally
            {
                ClearBusy();
                Interlocked.Decrement(ref _busyCount);
                SetLoadEnabled(true);
            }
        }
        catch
        {
            // ignore (best-effort UI action)
        }
    }

    private async void LoadFolderButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_busyCount > 0)
                return;

            using var dlg = new Forms.FolderBrowserDialog
            {
                Description = "Select folder containing audio files",
                UseDescriptionForTitle = true,
                ShowNewFolderButton = false
            };

            var owner = new Win32OwnerWrapper(new System.Windows.Interop.WindowInteropHelper(GetDialogOwnerWindow()).Handle);
            var result = dlg.ShowDialog(owner);
            if (result != Forms.DialogResult.OK)
                return;

            var folder = dlg.SelectedPath;
            if (string.IsNullOrWhiteSpace(folder))
                return;

            var includeSub = false;
            try { includeSub = _getIncludeSubfoldersOnFolderLoad(); } catch { includeSub = false; }
            var readMeta = false;
            try { readMeta = _getReadMetadataOnLoad(); } catch { readMeta = false; }

            using var cts = new CancellationTokenSource();
            Interlocked.Exchange(ref _folderMetadataBusyUserChoice, 0);
            Interlocked.Increment(ref _busyCount);
            SetLoadEnabled(false);
            try { _capturePlaylistForCancelRestore?.Invoke(); } catch { /* ignore */ }
            SetBusy("Loading...", cts, showSkipFolderMetadata: readMeta);
            try
            {
                var entries = await LocalPlaylistLoader.LoadFolderAsync(
                    folder,
                    includeSub,
                    _getFfmpegPath(),
                    readMetadataOnLoad: readMeta,
                    cts.Token,
                    metadataProgress: readMeta ? CreateMetadataLoadProgress() : null).ConfigureAwait(true);
                SourceTextBox.Text = folder;
                _sourceChanged(folder);
                await _loadEntriesAsync(entries, Path.GetFileName(folder), folder, cts.Token).ConfigureAwait(true);
                try { _commitPlaylistCancelRestore?.Invoke(); } catch { /* ignore */ }
            }
            catch (OperationCanceledException)
            {
                var choice = Interlocked.Exchange(ref _folderMetadataBusyUserChoice, 0);
                if (choice == 2 && readMeta)
                {
                    try
                    {
                        var entriesFast = await LocalPlaylistLoader.LoadFolderAsync(
                            folder,
                            includeSub,
                            _getFfmpegPath(),
                            readMetadataOnLoad: false,
                            CancellationToken.None).ConfigureAwait(true);
                        SourceTextBox.Text = folder;
                        _sourceChanged(folder);
                        await _loadEntriesAsync(entriesFast, Path.GetFileName(folder), folder, CancellationToken.None)
                            .ConfigureAwait(true);
                        try { _commitPlaylistCancelRestore?.Invoke(); } catch { /* ignore */ }
                    }
                    catch (OperationCanceledException)
                    {
                        try { _rollbackPlaylistCancelRestore?.Invoke(); } catch { /* ignore */ }
                    }
                    catch
                    {
                        try { _commitPlaylistCancelRestore?.Invoke(); } catch { /* ignore */ }
                    }
                }
                else
                {
                    try { _rollbackPlaylistCancelRestore?.Invoke(); } catch { /* ignore */ }
                }
            }
            catch
            {
                try { _commitPlaylistCancelRestore?.Invoke(); } catch { /* ignore */ }
            }
            finally
            {
                ClearBusy();
                Interlocked.Decrement(ref _busyCount);
                SetLoadEnabled(true);
            }
        }
        catch
        {
            // ignore
        }
    }

    private async void RefreshButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_busyCount > 0)
            return;

        using var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _folderMetadataBusyUserChoice, 0);
        Interlocked.Increment(ref _busyCount);
        SetLoadEnabled(false);
        try { _capturePlaylistForCancelRestore?.Invoke(); } catch { /* ignore */ }
        var showSkipMeta = false;
        try { showSkipMeta = _getRefreshOffersMetadataSkip(); } catch { /* ignore */ }
        SetBusy("Refreshing...", cts, showSkipFolderMetadata: showSkipMeta);
        try
        {
            await _refreshAsync(cts.Token).ConfigureAwait(true);
            try { _commitPlaylistCancelRestore?.Invoke(); } catch { /* ignore */ }
        }
        catch (OperationCanceledException)
        {
            var choice = Interlocked.Exchange(ref _folderMetadataBusyUserChoice, 0);
            var readMeta = false;
            try { readMeta = _getReadMetadataOnLoad(); } catch { /* ignore */ }
            var offersSkip = false;
            try { offersSkip = _getRefreshOffersMetadataSkip(); } catch { /* ignore */ }

            if (choice == 2 && readMeta && offersSkip)
            {
                try
                {
                    await _refreshLocalWithoutMetadataAsync(CancellationToken.None).ConfigureAwait(true);
                    try { _commitPlaylistCancelRestore?.Invoke(); } catch { /* ignore */ }
                }
                catch (OperationCanceledException)
                {
                    try { _rollbackPlaylistCancelRestore?.Invoke(); } catch { /* ignore */ }
                }
                catch
                {
                    try { _commitPlaylistCancelRestore?.Invoke(); } catch { /* ignore */ }
                }
            }
            else
            {
                var keep = false;
                try { keep = _getKeepIncompletePlaylistOnCancel(); } catch { /* ignore */ }
                if (keep)
                    try { _commitPlaylistCancelRestore?.Invoke(); } catch { /* ignore */ }
                else
                    try { _rollbackPlaylistCancelRestore?.Invoke(); } catch { /* ignore */ }
            }
        }
        catch
        {
            try { _commitPlaylistCancelRestore?.Invoke(); } catch { /* ignore */ }
        }
        finally
        {
            ClearBusy();
            Interlocked.Decrement(ref _busyCount);
            SetLoadEnabled(true);
        }
    }

    // SourceTextBox is read-only; loading happens via dialog.

    private static bool TryGetLocalPath(string? webpageUrlOrPath, out string path)
    {
        return LocalPlaylistLoader.TryGetLocalPath(webpageUrlOrPath, out path);
    }

    private void QueueItemContextMenu_OnOpened(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is not ContextMenu cm)
                return;

            if (cm.Items.OfType<MenuItem>().FirstOrDefault() is not MenuItem mi)
                return;

            var qi = cm.PlacementTarget is FrameworkElement fe ? fe.DataContext as QueueItem : null;
            if (qi is null)
                return;

            mi.Header = TryGetLocalPath(qi.WebpageUrl, out _)
                ? "Open file location"
                : "Open in browser";
        }
        catch
        {
            // ignore
        }
    }

    private void OpenMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is not MenuItem mi)
                return;
            if (mi.Parent is not ContextMenu cm)
                return;
            if (cm.PlacementTarget is not FrameworkElement fe)
                return;
            if (fe.DataContext is not QueueItem qi)
                return;

            if (TryGetLocalPath(qi.WebpageUrl, out var path))
            {
                var args = $"/select,\"{path}\"";
                Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
                return;
            }

            var url = qi.WebpageUrl;
            if (string.IsNullOrWhiteSpace(url))
                return;

            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // ignore
        }
    }

    private void QueueListBox_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var idx = TryGetIndexFromOriginalSource(e.OriginalSource as DependencyObject);
        _lastClickedIndex = idx ?? -1;
        if (_lastClickedIndex < 0 || _lastClickedIndex >= QueueListBox.Items.Count)
            return;

        if (QueueListBox.Items[_lastClickedIndex] is not QueueItem qi)
            return;

        _selectedVideoIdChanged(qi.VideoId);
    }

    private async void QueueListBox_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Resolve row from the double-click hit (virtualized list + fast clicks can leave _lastClickedIndex stale).
        var idx = TryGetIndexFromOriginalSource(e.OriginalSource as DependencyObject);
        if (idx is null || idx.Value < 0 || idx.Value >= QueueListBox.Items.Count)
            return;

        if (QueueListBox.Items[idx.Value] is not QueueItem qi)
            return;

        await _doubleClickPlayAsync(qi.VideoId);
    }

    private int? TryGetIndexFromOriginalSource(DependencyObject? original)
    {
        if (original is null)
            return null;

        var cur = original;
        while (cur is not null && cur is not ListBoxItem)
            cur = VisualTreeHelper.GetParent(cur);

        if (cur is not ListBoxItem lbi)
            return null;

        return QueueListBox.ItemContainerGenerator.IndexFromContainer(lbi);
    }

    // (Moved to OpenMenuItem_OnClick)

    private void CenterListBoxOnQueueItem(System.Windows.Controls.ListBox listBox, QueueItem item, int requestId, int attempt = 0)
    {
        void Work()
        {
            var endInitialScrollMask = true;
            try
            {
                if (requestId != _centerRequestId)
                {
                    endInitialScrollMask = false;
                    return;
                }

                listBox.ApplyTemplate();
                var scrollViewer = FindListBoxScrollViewer(listBox);
                if (scrollViewer is null || scrollViewer.ViewportHeight <= 0)
                {
                    if (attempt < 3)
                    {
                        endInitialScrollMask = false;
                        listBox.Dispatcher.BeginInvoke(new Action(() => CenterListBoxOnQueueItem(listBox, item, requestId, attempt + 1)), DispatcherPriority.Loaded);
                    }
                    return;
                }

                listBox.ScrollIntoView(item);
                listBox.UpdateLayout();

                var viewIndex = -1;
                for (var i = 0; i < listBox.Items.Count; i++)
                {
                    if (ReferenceEquals(listBox.Items[i], item))
                    {
                        viewIndex = i;
                        break;
                    }
                }

                if (viewIndex < 0)
                    return;

                var container = listBox.ItemContainerGenerator.ContainerFromIndex(viewIndex) as FrameworkElement;
                if (container is null)
                {
                    if (attempt < 3)
                    {
                        endInitialScrollMask = false;
                        listBox.Dispatcher.BeginInvoke(new Action(() => CenterListBoxOnQueueItem(listBox, item, requestId, attempt + 1)), DispatcherPriority.Loaded);
                    }
                    return;
                }

                // Robust centering: compute relative to the ScrollViewer itself.
                // (Using template internals can produce bogus transforms under virtualization.)
                var p = container.TransformToAncestor(scrollViewer).Transform(new System.Windows.Point(0, 0));
                var itemMidInViewport = p.Y + (container.ActualHeight / 2.0);
                var delta = itemMidInViewport - (scrollViewer.ViewportHeight / 2.0);
                var target = scrollViewer.VerticalOffset + delta;

                var max = Math.Max(0, scrollViewer.ExtentHeight - scrollViewer.ViewportHeight);
                if (target < 0) target = 0;
                if (target > max) target = max;

                // If the computed delta is wildly off, fall back to ScrollIntoView only.
                if (double.IsNaN(target) || double.IsInfinity(target) || Math.Abs(delta) > scrollViewer.ExtentHeight + 1000)
                    return;

                scrollViewer.ScrollToVerticalOffset(target);

                if (attempt < 2 && Math.Abs(delta) > Math.Max(2, container.ActualHeight))
                {
                    endInitialScrollMask = false;
                    listBox.Dispatcher.BeginInvoke(new Action(() => CenterListBoxOnQueueItem(listBox, item, requestId, attempt + 1)), DispatcherPriority.Loaded);
                }
            }
            catch
            {
                // ignore
            }
            finally
            {
                if (endInitialScrollMask)
                    EndSuppressQueueListUntilInitialScroll();
            }
        }

        if (listBox.Dispatcher.CheckAccess() && attempt == 0)
            Work();
        else if (!listBox.Dispatcher.CheckAccess())
            listBox.Dispatcher.BeginInvoke(Work, DispatcherPriority.ContextIdle);
        else
            listBox.Dispatcher.BeginInvoke(Work, DispatcherPriority.Loaded);
    }

    private static ScrollViewer? FindListBoxScrollViewer(System.Windows.Controls.ListBox listBox)
    {
        if (listBox.Template?.FindName("PART_ScrollViewer", listBox) is ScrollViewer templated)
            return templated;
        return FindDescendant<ScrollViewer>(listBox);
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T t)
            return t;

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            var found = FindDescendant<T>(child);
            if (found is not null) return found;
        }
        return null;
    }
}


