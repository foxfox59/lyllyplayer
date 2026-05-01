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
    Func<Window, CancellationToken, Task> OpenYoutubeModalAsync,
    Func<Window, CancellationToken, Task> OpenLocalFilesModalAsync,
    Func<PlaylistSortSpec, CancellationToken, Task> ApplySortAsync,
    Func<bool> GetIsYoutubeSource,
    Func<string, string, Task> SavePlaylistToFileAsync,
    Func<string, Task> LoadPlaylistFromFileAsync,
    Func<IReadOnlyList<PlaylistEntry>, string?, string, CancellationToken, Task> LoadEntriesAsync,
    Func<CancellationToken, Task> RefreshAsync,
    Func<CancellationToken, Task<int>> CleanInvalidItemsAsync,
    Action? CapturePlaylistForCancelRestore,
    Action? CommitPlaylistCancelRestore,
    Action? RollbackPlaylistCancelRestore,
    Action<string> SourceChanged,
    Func<string> GetSource,
    Func<string> GetLastYoutubeUrl,
    Action<string> SetLastYoutubeUrl,
    Func<string> GetFfmpegPath,
    Func<bool> GetIncludeSubfoldersOnFolderLoad,
    Func<bool> GetReadMetadataOnLoad,
    Func<bool> GetKeepIncompletePlaylistOnCancel,
    Func<bool> GetRefreshOffersMetadataSkip,
    Func<CancellationToken, Task> RefreshLocalWithoutMetadataAsync,
    Action<string> SelectedVideoIdChanged,
    Func<string, Task> DoubleClickPlayAsync,
    Func<PlaylistEntry, Task> AddToQueueAsync,
    Func<Guid, Task> RemoveQueuedInstanceAsync
);

