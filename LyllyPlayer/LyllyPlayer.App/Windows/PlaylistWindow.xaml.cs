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
using LyllyPlayer.ShellServices;
using System.Threading;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace LyllyPlayer.Windows;

public partial class PlaylistWindow : Window
{
    // (open-source/url normalization moved to PlaylistOpenTargetHelper)

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
    private readonly Func<string, int, int, bool, bool, CancellationToken, Task> _searchYoutubeVideosAsync;
    private readonly Func<string, int, CancellationToken, Task<IReadOnlyList<YoutubePlaylistHit>>> _searchYoutubePlaylistsAsync;
    private readonly Func<int, CancellationToken, Task<IReadOnlyList<YoutubePlaylistHit>>> _listYoutubeAccountPlaylistsAsync;
    private readonly Func<string, bool, bool, CancellationToken, Task> _importYoutubePlaylistAsync;
    private readonly Func<string, CancellationToken, Task<int?>> _tryGetYoutubePlaylistItemCountAsync;
    private readonly Func<string, CancellationToken, Task> _openUrlAsync;
    private readonly Func<string> _getLastYoutubeUrl;
    private readonly Action<string> _setLastYoutubeUrl;
    private readonly Func<bool> _getYoutubeImportAppendDefault;
    private readonly Action<bool> _setYoutubeImportAppendDefault;
    private readonly Func<bool> _getLocalImportAppendDefault;
    private readonly Action<bool> _setLocalImportAppendDefault;
    private readonly Func<bool> _getLocalImportRemoveDuplicatesDefault;
    private readonly Action<bool> _setLocalImportRemoveDuplicatesDefault;
    private readonly Func<string, bool, bool, bool, CancellationToken, IProgress<(int done, int total)>?, Task> _addLocalFolderAsync;
    private readonly Func<IReadOnlyList<string>, bool, bool, CancellationToken, IProgress<(int done, int total)>?, Task> _addLocalFilesAsync;
    private readonly Func<PlaylistSortSpec, CancellationToken, Task> _applySortAsync;
    private readonly Func<bool> _getIsYoutubeSource;
    private readonly Func<string, string, Task> _savePlaylistToFileAsync;
    private readonly Func<string, Task> _loadPlaylistFromFileAsync;
    private readonly Func<CancellationToken, Task> _newPlaylistAsync;
    private readonly Func<IReadOnlyList<PlaylistEntry>, string?, string, CancellationToken, Task> _loadEntriesAsync;
    private readonly Func<CancellationToken, Task> _refreshAsync;
    private readonly Func<CancellationToken, Task<int>> _cleanInvalidItemsAsync;
    private readonly Func<CancellationToken, Task<int>> _removeDuplicatesAsync;
    private readonly Action? _capturePlaylistForCancelRestore;
    private readonly Action? _commitPlaylistCancelRestore;
    private readonly Action? _rollbackPlaylistCancelRestore;
    private readonly Action<string> _sourceChanged;
    private readonly Func<string> _getSource;
    private readonly Func<string> _getFfmpegPath;
    private readonly Func<bool> _getIncludeSubfoldersOnFolderLoad;
    private readonly Func<bool> _getReadMetadataOnLoad;
    private readonly Func<bool> _getKeepIncompletePlaylistOnCancel;
    private readonly Func<bool> _getRefreshOffersMetadataSkip;
    private readonly Func<CancellationToken, Task> _refreshLocalWithoutMetadataAsync;
    private readonly Action<string> _selectedVideoIdChanged;
    private readonly Func<string, Task> _doubleClickPlayAsync;
    private readonly Func<PlaylistEntry, Task> _addToQueueAsync;
    private readonly Func<Guid, Task> _removeQueuedInstanceAsync;
    private readonly Func<IReadOnlyList<string>, CancellationToken, Task> _handleDroppedLocalPathsAsync;
    private readonly Func<IReadOnlyList<string>, CancellationToken, Task> _handleDroppedUrlsAsync;
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
    /// <summary>0 = unset, 1 = cancel (rollback playlist), 2 = skip metadata then reload the same folder or M3U without LibVLC metadata pass.</summary>
    private int _folderMetadataBusyUserChoice;
    /// <summary>0 = unset, 1 = Cancel (full rollback), 2 = Stop search (keep playlist if non-empty).</summary>
    private int _searchOverlayDismissKind;

    // Chrome dragging uses Window.DragMove() now so WM_MOVING-based snapping can engage.

    private ObservableCollection<QueueItem>? _queueSource;
    private CollectionViewSource? _queueViewSource;
    // private CollectionViewSource? _queuedViewSource;
    private string _playlistFilterQuery = "";
    private readonly DispatcherTimer _playlistFilterDebounceTimer;

    private bool _suppressSortUiEvents;
    private PlaylistSortSpec _lastSortSpec = new(PlaylistSortMode.None, PlaylistSortDirection.Asc);
    ObservableCollection<QueueItem>? _playlistItemsSource;
    CollectionViewSource? _playlistViewSource;

    public IEnumerable<QueueItem>? PlaylistItems => PlaylistListBox.ItemsSource as IEnumerable<QueueItem>;
    public IEnumerable<QueueItem>? QueueItems => QueuedListBox.ItemsSource as IEnumerable<QueueItem>;

    public PlaylistWindow(PlaylistWindowOps ops)
        : this(
            ops.LoadUrlAsync,
            ops.GetSearchDefaults,
            ops.SetSearchDefaults,
            ops.SearchYoutubeVideosAsync,
            ops.SearchYoutubePlaylistsAsync,
            ops.ListYoutubeAccountPlaylistsAsync,
            ops.ImportYoutubePlaylistAsync,
            ops.TryGetYoutubePlaylistItemCountAsync,
            ops.OpenUrlAsync,
            ops.GetLastYoutubeUrl,
            ops.SetLastYoutubeUrl,
            ops.GetYoutubeImportAppendDefault,
            ops.SetYoutubeImportAppendDefault,
            ops.GetLocalImportAppendDefault,
            ops.SetLocalImportAppendDefault,
            ops.GetLocalImportRemoveDuplicatesDefault,
            ops.SetLocalImportRemoveDuplicatesDefault,
            ops.AddLocalFolderAsync,
            ops.AddLocalFilesAsync,
            ops.ApplySortAsync,
            ops.GetIsYoutubeSource,
            ops.SavePlaylistToFileAsync,
            ops.LoadPlaylistFromFileAsync,
            ops.NewPlaylistAsync,
            ops.LoadEntriesAsync,
            ops.RefreshAsync,
            ops.CleanInvalidItemsAsync,
            ops.RemoveDuplicatesAsync,
            ops.CapturePlaylistForCancelRestore,
            ops.CommitPlaylistCancelRestore,
            ops.RollbackPlaylistCancelRestore,
            ops.SourceChanged,
            ops.GetSource,
            ops.GetFfmpegPath,
            ops.GetIncludeSubfoldersOnFolderLoad,
            ops.GetReadMetadataOnLoad,
            ops.GetKeepIncompletePlaylistOnCancel,
            ops.GetRefreshOffersMetadataSkip,
            ops.RefreshLocalWithoutMetadataAsync,
            ops.SelectedVideoIdChanged,
            ops.DoubleClickPlayAsync,
            ops.AddToQueueAsync,
            ops.RemoveQueuedInstanceAsync,
            ops.HandleDroppedLocalPathsAsync,
            ops.HandleDroppedUrlsAsync)
    {
    }

    public PlaylistWindow(
        Func<string, Task> loadUrlAsync,
        Func<(int count, int minLengthSeconds)> getSearchDefaults,
        Action<int, int> setSearchDefaults,
        Func<string, int, int, bool, bool, CancellationToken, Task> searchYoutubeVideosAsync,
        Func<string, int, CancellationToken, Task<IReadOnlyList<YoutubePlaylistHit>>> searchYoutubePlaylistsAsync,
        Func<int, CancellationToken, Task<IReadOnlyList<YoutubePlaylistHit>>> listYoutubeAccountPlaylistsAsync,
        Func<string, bool, bool, CancellationToken, Task> importYoutubePlaylistAsync,
        Func<string, CancellationToken, Task<int?>> tryGetYoutubePlaylistItemCountAsync,
        Func<string, CancellationToken, Task> openUrlAsync,
        Func<string> getLastYoutubeUrl,
        Action<string> setLastYoutubeUrl,
        Func<bool> getYoutubeImportAppendDefault,
        Action<bool> setYoutubeImportAppendDefault,
        Func<bool> getLocalImportAppendDefault,
        Action<bool> setLocalImportAppendDefault,
        Func<bool> getLocalImportRemoveDuplicatesDefault,
        Action<bool> setLocalImportRemoveDuplicatesDefault,
        Func<string, bool, bool, bool, CancellationToken, IProgress<(int done, int total)>?, Task> addLocalFolderAsync,
        Func<IReadOnlyList<string>, bool, bool, CancellationToken, IProgress<(int done, int total)>?, Task> addLocalFilesAsync,
        Func<PlaylistSortSpec, CancellationToken, Task> applySortAsync,
        Func<bool> getIsYoutubeSource,
        Func<string, string, Task> savePlaylistToFileAsync,
        Func<string, Task> loadPlaylistFromFileAsync,
        Func<CancellationToken, Task> newPlaylistAsync,
        Func<IReadOnlyList<PlaylistEntry>, string?, string, CancellationToken, Task> loadEntriesAsync,
        Func<CancellationToken, Task> refreshAsync,
        Func<CancellationToken, Task<int>> cleanInvalidItemsAsync,
        Func<CancellationToken, Task<int>> removeDuplicatesAsync,
        Action? capturePlaylistForCancelRestore,
        Action? commitPlaylistCancelRestore,
        Action? rollbackPlaylistCancelRestore,
        Action<string> sourceChanged,
        Func<string> getSource,
        Func<string> getFfmpegPath,
        Func<bool> getIncludeSubfoldersOnFolderLoad,
        Func<bool> getReadMetadataOnLoad,
        Func<bool> getKeepIncompletePlaylistOnCancel,
        Func<bool> getRefreshOffersMetadataSkip,
        Func<CancellationToken, Task> refreshLocalWithoutMetadataAsync,
        Action<string> selectedVideoIdChanged,
        Func<string, Task> doubleClickPlayAsync,
        Func<PlaylistEntry, Task> addToQueueAsync,
        Func<Guid, Task> removeQueuedInstanceAsync,
        Func<IReadOnlyList<string>, CancellationToken, Task> handleDroppedLocalPathsAsync,
        Func<IReadOnlyList<string>, CancellationToken, Task> handleDroppedUrlsAsync
    )
    {
        _loadUrlAsync = loadUrlAsync;
        _getSearchDefaults = getSearchDefaults;
        _setSearchDefaults = setSearchDefaults;
        _searchYoutubeVideosAsync = searchYoutubeVideosAsync;
        _searchYoutubePlaylistsAsync = searchYoutubePlaylistsAsync;
        _listYoutubeAccountPlaylistsAsync = listYoutubeAccountPlaylistsAsync;
        _importYoutubePlaylistAsync = importYoutubePlaylistAsync;
        _tryGetYoutubePlaylistItemCountAsync = tryGetYoutubePlaylistItemCountAsync;
        _openUrlAsync = openUrlAsync;
        _getLastYoutubeUrl = getLastYoutubeUrl;
        _setLastYoutubeUrl = setLastYoutubeUrl;
        _getYoutubeImportAppendDefault = getYoutubeImportAppendDefault;
        _setYoutubeImportAppendDefault = setYoutubeImportAppendDefault;
        _getLocalImportAppendDefault = getLocalImportAppendDefault;
        _setLocalImportAppendDefault = setLocalImportAppendDefault;
        _getLocalImportRemoveDuplicatesDefault = getLocalImportRemoveDuplicatesDefault;
        _setLocalImportRemoveDuplicatesDefault = setLocalImportRemoveDuplicatesDefault;
        _addLocalFolderAsync = addLocalFolderAsync;
        _addLocalFilesAsync = addLocalFilesAsync;
        _applySortAsync = applySortAsync;
        _getIsYoutubeSource = getIsYoutubeSource;
        _savePlaylistToFileAsync = savePlaylistToFileAsync;
        _loadPlaylistFromFileAsync = loadPlaylistFromFileAsync;
        _newPlaylistAsync = newPlaylistAsync;
        _loadEntriesAsync = loadEntriesAsync;
        _refreshAsync = refreshAsync;
        _cleanInvalidItemsAsync = cleanInvalidItemsAsync;
        _removeDuplicatesAsync = removeDuplicatesAsync;
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
        _addToQueueAsync = addToQueueAsync;
        _removeQueuedInstanceAsync = removeQueuedInstanceAsync;
        _handleDroppedLocalPathsAsync = handleDroppedLocalPathsAsync;
        _handleDroppedUrlsAsync = handleDroppedUrlsAsync;

        InitializeComponent();

        UpdateTitleFromSource(_getSource());
        InitializeSortUi();

        // Wire tab content (created in XAML) with backend callbacks.
        try
        {
            var searchDefaults = _getSearchDefaults();
            YoutubeTab.Initialize(
                searchVideosAsync: _searchYoutubeVideosAsync,
                searchPlaylistsAsync: _searchYoutubePlaylistsAsync,
                listAccountPlaylistsAsync: _listYoutubeAccountPlaylistsAsync,
                importPlaylistAsync: _importYoutubePlaylistAsync,
                tryGetPlaylistItemCountAsync: _tryGetYoutubePlaylistItemCountAsync,
                getLastUrl: _getLastYoutubeUrl,
                setLastUrl: _setLastYoutubeUrl,
                openUrlAsync: _openUrlAsync,
                searchDefaults: searchDefaults,
                importAppendDefault: _getYoutubeImportAppendDefault(),
                setImportAppendDefault: _setYoutubeImportAppendDefault);
        }
        catch { /* ignore */ }

        try
        {
            LocalTab.Initialize(
                getAppendDefault: _getLocalImportAppendDefault,
                setAppendDefault: _setLocalImportAppendDefault,
                getRemoveDuplicatesDefault: _getLocalImportRemoveDuplicatesDefault,
                setRemoveDuplicatesDefault: _setLocalImportRemoveDuplicatesDefault,
                getReadMetadataOnLoad: _getReadMetadataOnLoad,
                getIncludeSubfoldersOnFolderLoad: _getIncludeSubfoldersOnFolderLoad,
                addFolderAsync: _addLocalFolderAsync,
                addFilesAsync: _addLocalFilesAsync);
        }
        catch { /* ignore */ }

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

    private void PlaylistListBox_OnPreviewDragOver(object sender, System.Windows.DragEventArgs e)
    {
        try
        {
            if (_busyCount > 0)
            {
                e.Effects = System.Windows.DragDropEffects.None;
                e.Handled = true;
                return;
            }

            var data = e.Data;
            if (data is null)
            {
                e.Effects = System.Windows.DragDropEffects.None;
                e.Handled = true;
                return;
            }

            if (PlaylistDragDropHelper.CanAccept(data))
                e.Effects = System.Windows.DragDropEffects.Copy;
            else
                e.Effects = System.Windows.DragDropEffects.None;

            e.Handled = true;
        }
        catch
        {
            e.Effects = System.Windows.DragDropEffects.None;
            e.Handled = true;
        }
    }

    private async void PlaylistListBox_OnDrop(object sender, System.Windows.DragEventArgs e)
    {
        try
        {
            if (_busyCount > 0)
                return;

            if (e.Data is null)
                return;

            var payload = PlaylistDragDropHelper.ExtractBestEffort(e.Data);
            if (payload.LocalPaths.Count == 0 && payload.Urls.Count == 0)
                return;

            using var cts = new CancellationTokenSource();
            Interlocked.Increment(ref _busyCount);
            SetLoadEnabled(false);
            SetBusy("Adding to playlist...", cts);
            try
            {
                if (payload.LocalPaths.Count > 0)
                    await _handleDroppedLocalPathsAsync(payload.LocalPaths, cts.Token).ConfigureAwait(true);

                if (payload.Urls.Count > 0)
                    await _handleDroppedUrlsAsync(payload.Urls, cts.Token).ConfigureAwait(true);
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
            // silently ignore (drag/drop is best-effort UX)
        }
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

            var wantMode = _lastSortSpec.Mode;
            SortModeComboBox.ItemsSource = PlaylistWindowSorting.BuildSortChoices(isYoutube);
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

    private void SortModeComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSortUiEvents)
            return;
        try { _lastSortSpec = GetSortSpecFromUi(); } catch { /* ignore */ }
    }

    private void SortDescToggleButton_OnChecked(object sender, RoutedEventArgs e)
    {
        try { SortDescToggleButton.Content = "Desc"; } catch { /* ignore */ }
        if (_suppressSortUiEvents)
            return;
        try { _lastSortSpec = GetSortSpecFromUi(); } catch { /* ignore */ }
    }

    private void SortDescToggleButton_OnUnchecked(object sender, RoutedEventArgs e)
    {
        try { SortDescToggleButton.Content = "Asc"; } catch { /* ignore */ }
        if (_suppressSortUiEvents)
            return;
        try { _lastSortSpec = GetSortSpecFromUi(); } catch { /* ignore */ }
    }

    private async void ApplySortButton_OnClick(object sender, RoutedEventArgs e)
    {
        try { await ApplySortFromUiAsync(); }
        catch { /* ignore */ }
    }

    private void ChromeCloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        // Hiding instead of closing keeps the visual tree + bindings warm for large playlists.
        // MainWindow owns persistence on close/hide.
        try { Hide(); } catch { /* ignore */ }
    }

    private void ChromeBar_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
            return;

        try
        {
            // Use OS-driven window dragging so Win32 WM_MOVING-based snapping can engage.
            // (This window previously moved itself by setting Left/Top, which bypassed WM_MOVING.)
            DragMove();
            e.Handled = true;
        }
        catch
        {
            // ignore
        }
    }

    // Legacy manual drag handlers removed (DragMove is used instead).

    public void SetItemsSource(ObservableCollection<QueueItem> queueItems, ObservableCollection<QueueItem> playlistItems)
    {
        // Queue ListBox — direct binding, no filter
        if (_queueViewSource is null || !ReferenceEquals(_queueSource, queueItems))
        {
            _queueSource = queueItems;
            _queueViewSource = new CollectionViewSource { Source = _queueSource };
            //QueuedListBox.ItemsSource = _queueViewSource.View;
        }
        QueuedListBox.ItemsSource = queueItems;

        // Playlist ListBox — direct binding with search filter
        if (_playlistViewSource is null || !ReferenceEquals(_playlistItemsSource, playlistItems))
        {
            _playlistItemsSource = playlistItems;
            _playlistViewSource = new CollectionViewSource { Source = _playlistItemsSource };
            _playlistViewSource.View.Filter = PlaylistFilterPredicate;
            PlaylistListBox.ItemsSource = _playlistViewSource.View;

            // Let the window render before we do any expensive view work (filtering can be O(n)).
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try { _playlistViewSource?.View.Refresh(); } catch { /* ignore */ }
            }), DispatcherPriority.Background);
        }
        else
        {
            // Avoid blocking the UI thread during window open; refresh lazily.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try { _playlistViewSource?.View.Refresh(); } catch { /* ignore */ }
            }), DispatcherPriority.Background);
        }

        // Update queue count display
        try
        {
            var count = _queueSource?.Count ?? 0;
            try { QueuedHeaderTextBlock.Text = count > 0 ? $"Queue ({count})" : "Queue"; } catch { /* ignore */ }
            try { QueuedPanel.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed; } catch { /* ignore */ }
        }
        catch { /* ignore */ }
    }

    private bool PlaylistFilterPredicate(object obj)
    {
        if (obj is not QueueItem qi)
            return false;
        // Queue items are now in a separate collection — no need to exclude them
        var q = (_playlistFilterQuery ?? "").Trim();
        if (q.Length == 0)
            return true;
        return MatchesPlaylistFilterTokens(q, qi);
    }

    private static bool MatchesPlaylistFilterTokens(string query, QueueItem qi)
    {
        return PlaylistWindowFiltering.MatchesPlaylistFilterTokens(query, qi);
    }

    private void ApplyPlaylistFilterFromTextBox()
    {
        try { _playlistFilterQuery = PlaylistFilterTextBox?.Text ?? ""; }
        catch { /* ignore */ }
        try { _playlistViewSource?.View.Refresh(); }
        catch { /* ignore */ }
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
            //_queueViewSource?.View.Refresh();
        } catch {}
        try { _playlistViewSource?.View.Refresh(); }
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

        // try
        // {
        //     _queueViewSource?.View.Refresh();
        // }
        try { _playlistViewSource?.View.Refresh(); }
        catch
        {
            // ignore
        }
    }

    public void SetRefreshEnabled(bool enabled) => RefreshButton.IsEnabled = enabled;
    public void SetSourceText(string source)
    {
        UpdateTitleFromSource(source);
    }

    private void UpdateTitleFromSource(string? source)
    {
        try
        {
            var s = (source ?? "").Trim();
            if (s.Length > 64) s = s.Substring(0, 64) + "\u2026";
            Title = string.IsNullOrWhiteSpace(s) ? "LyllyPlayer — Playlist" : $"LyllyPlayer — Playlist — {s}";
        }
        catch { /* ignore */ }
    }
    public void SetLoadEnabled(bool enabled)
    {
        if (_busyCount > 0)
            enabled = false;
        SavePlaylistButton.IsEnabled = enabled;
        LoadPlaylistButton.IsEnabled = enabled;
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
        if (index < 0) return;
        if (_queueSource is null) return;
        try
        {
            var item = _queueSource.FirstOrDefault(q => !q.IsQueued && q.BaseIndex == index);
            if (item is null) return;
            PlaylistListBox.ScrollIntoView(item);
        }
        catch { /* ignore */ }
    }

    /// <summary>Call before <see cref="Window.Show"/> so the first paint does not flash the top of the list before scroll-to-now-playing.</summary>
    public void BeginSuppressQueueListUntilInitialScroll()
    {
        try
        {
            _suppressQueueListUntilInitialScroll = true;
            PlaylistListBox.Opacity = 0;
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
            PlaylistListBox.Opacity = 1;
            try { _initialScrollMaskFailsafeTimer?.Stop(); } catch { /* ignore */ }
        }
        catch
        {
            // ignore
        }
    }

    public void CenterNowPlaying(PlaylistEntry? entry)
    {
        if (entry is null) return;

        try
        {
            // 1. Search Queue First
            if (_queueSource is not null && _queueSource.Any())
            {
                var queuedItem = _queueSource.FirstOrDefault(q =>
                    q.IsQueued &&
                    q.Entry is not null &&
                    string.Equals(q.Entry.VideoId, entry.VideoId, StringComparison.OrdinalIgnoreCase));

                if (queuedItem is not null)
                {
                    CenterListBoxOnQueueItem(QueuedListBox, queuedItem, Interlocked.Increment(ref _centerRequestId));
                    return;
                }
            }

            // 2. Fallback to Base Playlist
            if (_playlistItemsSource is not null && _playlistItemsSource.Any())
            {
                var baseItem = _playlistItemsSource.FirstOrDefault(q =>
                    !q.IsQueued &&
                    q.Entry is not null &&
                    string.Equals(q.Entry.VideoId, entry.VideoId, StringComparison.OrdinalIgnoreCase));

                if (baseItem is not null)
                {
                //    System.Windows.MessageBox.Show($"CenterNowPlaying → QueuedListBox\n" +
                //         $"VideoId: {baseItem.VideoId}\n" +
                //         $"Entry.VideoId: {baseItem.Entry.VideoId}\n" +
                //         $"IsQueued: {baseItem.IsQueued}");     
                    CenterListBoxOnQueueItem(PlaylistListBox, baseItem, Interlocked.Increment(ref _centerRequestId));
                    return;
                }
            }
        }
        catch { }
    }

    // YouTube is now a tab (no modal).

    private async void SavePlaylistButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_busyCount > 0)
                return;

            var s = "";
            try { s = (_getSource() ?? "").Trim(); } catch { s = ""; }
            var path = PlaylistFileDialogs.PickPlaylistToSave(GetDialogOwnerWindow(), s);
            if (string.IsNullOrWhiteSpace(path))
                return;

            Interlocked.Increment(ref _busyCount);
            SetLoadEnabled(false);
            SetBusy("Saving...");
            try
            {
                // Persist playlist title (not the source URL/path). The MainWindow delegate will use its current
                // playlist title when displayName is empty.
                await _savePlaylistToFileAsync(path, "");
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

    private async void LoadPlaylistButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_busyCount > 0)
                return;

            var path = PlaylistFileDialogs.PickPlaylistToLoad(GetDialogOwnerWindow());
            if (string.IsNullOrWhiteSpace(path))
                return;

            var ext = (Path.GetExtension(path) ?? "").Trim().ToLowerInvariant();
            if (ext is ".m3u" or ".m3u8")
            {
                await LoadM3uFromPathAsync(path);
                return;
            }

            Interlocked.Increment(ref _busyCount);
            SetLoadEnabled(false);
            SetBusy("Loading...");
            try
            {
                await _loadPlaylistFromFileAsync(path);
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

    private async void NewPlaylistButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_busyCount > 0)
                return;

            var res = System.Windows.MessageBox.Show(
                GetDialogOwnerWindow(),
                "This will clear the current playlist.\n\nContinue?",
                "New playlist",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning);
            if (res != MessageBoxResult.OK)
                return;

            Interlocked.Increment(ref _busyCount);
            SetLoadEnabled(false);
            SetBusy("Clearing...");
            try
            {
                await _newPlaylistAsync(CancellationToken.None).ConfigureAwait(true);
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

    private async Task LoadM3uFromPathAsync(string path)
    {
        try
        {
            if (_busyCount > 0)
                return;
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
                    readMetadataOnLoad: readMeta,
                    cts.Token,
                    metadataProgress: readMeta ? CreateMetadataLoadProgress() : null).ConfigureAwait(true);
                _sourceChanged(path);
                UpdateTitleFromSource(path);
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
                            readMetadataOnLoad: false,
                            CancellationToken.None).ConfigureAwait(true);
                        _sourceChanged(path);
                        UpdateTitleFromSource(path);
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

    // Local / YouTube are now tabs (no modal buttons).

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

    private async void RemoveMissingButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_busyCount > 0)
            return;

        using var cts = new CancellationTokenSource();
        Interlocked.Increment(ref _busyCount);
        SetLoadEnabled(false);
        SetBusy("Removing missing items...", cts);
        int removedCount = 0;
        try
        {
            removedCount = await _cleanInvalidItemsAsync(cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // User cancelled — playlist stays intact
        }
        catch
        {
            // Ignore errors — don't corrupt the playlist
        }
        finally
        {
            ClearBusy();
            Interlocked.Decrement(ref _busyCount);
            SetLoadEnabled(true);

            if (removedCount > 0)
            {
                try
                {
                    System.Windows.MessageBox.Show(
                        this,
                        $"Removed missing {removedCount} item{(removedCount == 1 ? "" : "s")} from the playlist.",
                        "LyllyPlayer",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                catch { /* ignore */ }
            }
        }
    }

    private async void RemoveDuplicatesButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_busyCount > 0)
            return;

        using var cts = new CancellationTokenSource();
        Interlocked.Increment(ref _busyCount);
        SetLoadEnabled(false);
        SetBusy("Removing duplicates...", cts);
        var removed = 0;
        try
        {
            removed = await _removeDuplicatesAsync(cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // cancel: ignore
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

            if (removed > 0)
            {
                try
                {
                    System.Windows.MessageBox.Show(
                        this,
                        $"Removed {removed} duplicate item{(removed == 1 ? "" : "s")} from the playlist.",
                        "LyllyPlayer",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                catch { /* ignore */ }
            }
        }
    }

    // (legacy CleanInvalidItemsButton removed; replaced by "Remove Missing")

    // Source is reflected in window title; loading happens via dialog.

    // (local-path detection is centralized in LocalPlaylistLoader and PlaylistOpenTargetHelper)

    private void QueueItemContextMenu_OnOpened(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is not ContextMenu cm)
                return;

            var items = cm.Items.OfType<object>().ToList();
            var openMi = items.OfType<MenuItem>().FirstOrDefault();
            var addMi = items.OfType<MenuItem>().FirstOrDefault(x => string.Equals(x.Name, "AddToQueueMenuItem", StringComparison.Ordinal));
            var removeMi = items.OfType<MenuItem>().FirstOrDefault(x => string.Equals(x.Name, "RemoveFromQueueMenuItem", StringComparison.Ordinal));

            if (openMi is null)
                return;

            var qi = cm.PlacementTarget is FrameworkElement fe ? fe.DataContext as QueueItem : null;
            if (qi is null)
                return;

            openMi.Header = PlaylistOpenTargetHelper.GetOpenMenuHeader(qi);

            // Menu visibility rules:
            // - Base playlist rows: only "Add to queue"
            // - Queued rows: "Queue next/last" + "Remove from queue" (no add; queue interactions never create duplicates)
            var isQueuedRow = qi.IsQueued;

            if (addMi is not null)
            {
                addMi.Visibility = isQueuedRow ? Visibility.Collapsed : Visibility.Visible;
                addMi.IsEnabled = !isQueuedRow && qi.Entry is not null;
            }

            if (removeMi is not null)
            {
                removeMi.Visibility = isQueuedRow ? Visibility.Visible : Visibility.Collapsed;
                removeMi.IsEnabled = isQueuedRow && qi.QueueInstanceId is not null;
            }
        }
        catch
        {
            // ignore
        }
    }

    private async void AddToQueueMenuItem_OnClick(object sender, RoutedEventArgs e)
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
            if (qi.Entry is null)
                return;
            if (qi.IsQueued)
                return;

            await _addToQueueAsync(qi.Entry);
        }
        catch
        {
            // ignore
        }
    }

    private async void RemoveFromQueueMenuItem_OnClick(object sender, RoutedEventArgs e)
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
            if (!qi.IsQueued || qi.QueueInstanceId is not Guid id)
                return;

            await _removeQueuedInstanceAsync(id);
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

            if (!PlaylistOpenTargetHelper.TryGetOpenTarget(qi, out var target))
                return;

            if (target.Kind == PlaylistOpenTargetKind.LocalFile)
            {
                var args = $"/select,\"{target.Value}\"";
                Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
                return;
            }

            if (target.Kind == PlaylistOpenTargetKind.Url)
                Process.Start(new ProcessStartInfo(target.Value) { UseShellExecute = true });
        }
        catch
        {
            // ignore
        }
    }

    private void QueueListBox_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.ListBox lb)
            return;
        var idx = TryGetIndexFromOriginalSource(lb, e.OriginalSource as DependencyObject);
        _lastClickedIndex = idx ?? -1;
        if (_lastClickedIndex < 0 || _lastClickedIndex >= lb.Items.Count)
            return;

        if (lb.Items[_lastClickedIndex] is not QueueItem qi)
            return;

        _selectedVideoIdChanged(qi.VideoId);
    }

    private async void QueueListBox_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Resolve row from the double-click hit (virtualized list + fast clicks can leave _lastClickedIndex stale).
        if (sender is not System.Windows.Controls.ListBox lb)
            return;
        var idx = TryGetIndexFromOriginalSource(lb, e.OriginalSource as DependencyObject);
        if (idx is null || idx.Value < 0 || idx.Value >= lb.Items.Count)
            return;

        if (lb.Items[idx.Value] is not QueueItem qi)
            return;

        if (qi.IsQueued && qi.QueueInstanceId is Guid qid)
        {
            await _doubleClickPlayAsync($"queue:{qid:D}");
            return;
        }

        await _doubleClickPlayAsync(qi.VideoId);
    }

    private int? TryGetIndexFromOriginalSource(System.Windows.Controls.ListBox listBox, DependencyObject? original)
    {
        if (original is null)
            return null;

        var cur = original;
        while (cur is not null && cur is not ListBoxItem)
            cur = VisualTreeHelper.GetParent(cur);

        if (cur is not ListBoxItem lbi)
            return null;

        return listBox.ItemContainerGenerator.IndexFromContainer(lbi);
    }

    // (Moved to OpenMenuItem_OnClick)

    private void CenterListBoxOnQueueItem(System.Windows.Controls.ListBox listBox, QueueItem item, int requestId)
        => ListBoxCentering.CenterOnItem(
            listBox,
            item,
            requestId,
            getLatestRequestId: () => _centerRequestId,
            onFinished: EndSuppressQueueListUntilInitialScroll);

    public void RefreshQueueView()
    {
        try
        {
            var count = _queueSource?.Count ?? 0;
            try { QueuedHeaderTextBlock.Text = count > 0 ? $"Queue ({count})" : "Queue"; } catch { /* ignore */ }
            try { QueuedPanel.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed; } catch { /* ignore */ }
        }
        catch { /* ignore */ }
    }
}
