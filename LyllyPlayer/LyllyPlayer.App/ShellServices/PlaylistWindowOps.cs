using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using LyllyPlayer.Models;

namespace LyllyPlayer.ShellServices;

/// <summary>
/// Bundle of callbacks that <see cref="Windows.PlaylistWindow"/> uses to delegate backend work to the shell.
/// Keeps the window code focused on UI behavior.
/// </summary>
public sealed record PlaylistWindowOps(
    Func<string, Task> LoadUrlAsync,
    Func<(int count, int minLengthSeconds)> GetSearchDefaults,
    Action<int, int> SetSearchDefaults,
    Func<string, int, int, bool, bool, CancellationToken, Task> SearchYoutubeVideosAsync,
    Func<string, int, CancellationToken, Task<IReadOnlyList<YoutubePlaylistHit>>> SearchYoutubePlaylistsAsync,
    Func<int, CancellationToken, Task<IReadOnlyList<YoutubePlaylistHit>>> ListYoutubeAccountPlaylistsAsync,
    Func<string, bool, bool, CancellationToken, Task> ImportYoutubePlaylistAsync,
    Func<string, CancellationToken, Task<int?>> TryGetYoutubePlaylistItemCountAsync,
    Func<string, CancellationToken, Task> OpenUrlAsync,
    Func<string> GetLastYoutubeUrl,
    Action<string> SetLastYoutubeUrl,
    Func<bool> GetYoutubeImportAppendDefault,
    Action<bool> SetYoutubeImportAppendDefault,
    Func<bool> GetLocalImportAppendDefault,
    Action<bool> SetLocalImportAppendDefault,
    Func<bool> GetLocalImportRemoveDuplicatesDefault,
    Action<bool> SetLocalImportRemoveDuplicatesDefault,
    Func<string, bool, bool, bool, CancellationToken, IProgress<(int done, int total)>?, Task> AddLocalFolderAsync,
    Func<IReadOnlyList<string>, bool, bool, CancellationToken, IProgress<(int done, int total)>?, Task> AddLocalFilesAsync,
    Func<PlaylistSortSpec, CancellationToken, Task> ApplySortAsync,
    Func<bool> GetIsYoutubeSource,
    Func<string, string, Task> SavePlaylistToFileAsync,
    Func<string, Task> LoadPlaylistFromFileAsync,
    Func<CancellationToken, Task> NewPlaylistAsync,
    Func<IReadOnlyList<PlaylistEntry>, string?, string, CancellationToken, Task> LoadEntriesAsync,
    Func<CancellationToken, Task> RefreshAsync,
    Func<CancellationToken, Task<int>> CleanInvalidItemsAsync,
    Func<CancellationToken, Task<int>> RemoveDuplicatesAsync,
    Action? CapturePlaylistForCancelRestore,
    Action? CommitPlaylistCancelRestore,
    Action? RollbackPlaylistCancelRestore,
    Action<string> SourceChanged,
    Func<string> GetSource,
    Func<string> GetFfmpegPath,
    Func<bool> GetIncludeSubfoldersOnFolderLoad,
    Func<bool> GetReadMetadataOnLoad,
    Func<bool> GetKeepIncompletePlaylistOnCancel,
    Func<bool> GetRefreshOffersMetadataSkip,
    Func<CancellationToken, Task> RefreshLocalWithoutMetadataAsync,
    Action<string> SelectedVideoIdChanged,
    Func<string, Task> DoubleClickPlayAsync,
    Func<PlaylistEntry, Task> AddToQueueAsync,
    Func<Guid, Task> RemoveQueuedInstanceAsync,
    Func<string, Task> RemoveFromPlaylistAsync,
    /// <summary>Drag/drop: add local files/folders to playlist (append/replace is decided by the shell).</summary>
    Func<IReadOnlyList<string>, CancellationToken, Task> HandleDroppedLocalPathsAsync,
    /// <summary>Drag/drop: add URLs (e.g. browser tabs) to playlist (append/replace is decided by the shell).</summary>
    Func<IReadOnlyList<string>, CancellationToken, Task> HandleDroppedUrlsAsync
);

