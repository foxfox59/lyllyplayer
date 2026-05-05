using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using LyllyPlayer.Models;

namespace LyllyPlayer.Windows;

public partial class YoutubeTabView : System.Windows.Controls.UserControl
{
    private Func<string, int, int, bool, bool, CancellationToken, Task>? _searchVideosAsync;
    private Func<string, int, CancellationToken, Task<IReadOnlyList<YoutubePlaylistHit>>>? _searchPlaylistsAsync;
    private Func<int, CancellationToken, Task<IReadOnlyList<YoutubePlaylistHit>>>? _listAccountPlaylistsAsync;
    private Func<string, bool, bool, CancellationToken, Task>? _importPlaylistAsync;
    private Func<string, CancellationToken, Task<int?>>? _tryGetPlaylistItemCountAsync;
    private Action<bool>? _setImportAppendDefault;
    private Func<string>? _getLastUrl;
    private Action<string>? _setLastUrl;
    private Func<string, CancellationToken, Task>? _openUrlAsync;

    public YoutubeTabView()
    {
        InitializeComponent();
    }

    public void Initialize(
        Func<string, int, int, bool, bool, CancellationToken, Task> searchVideosAsync,
        Func<string, int, CancellationToken, Task<IReadOnlyList<YoutubePlaylistHit>>> searchPlaylistsAsync,
        Func<int, CancellationToken, Task<IReadOnlyList<YoutubePlaylistHit>>> listAccountPlaylistsAsync,
        Func<string, bool, bool, CancellationToken, Task> importPlaylistAsync,
        Func<string, CancellationToken, Task<int?>> tryGetPlaylistItemCountAsync,
        Func<string> getLastUrl,
        Action<string> setLastUrl,
        Func<string, CancellationToken, Task> openUrlAsync,
        (int count, int minLenSeconds) searchDefaults,
        bool importAppendDefault,
        Action<bool> setImportAppendDefault)
    {
        _searchVideosAsync = searchVideosAsync;
        _searchPlaylistsAsync = searchPlaylistsAsync;
        _listAccountPlaylistsAsync = listAccountPlaylistsAsync;
        _importPlaylistAsync = importPlaylistAsync;
        _tryGetPlaylistItemCountAsync = tryGetPlaylistItemCountAsync;
        _setImportAppendDefault = setImportAppendDefault;
        _getLastUrl = getLastUrl;
        _setLastUrl = setLastUrl;
        _openUrlAsync = openUrlAsync;

        try { OpenUrlTextBox.Text = _getLastUrl?.Invoke() ?? ""; } catch { /* ignore */ }
        try { SearchVideosCountTextBox.Text = searchDefaults.count.ToString(); } catch { /* ignore */ }
        try { SearchVideosMinLenTextBox.Text = searchDefaults.minLenSeconds.ToString(); } catch { /* ignore */ }

        try
        {
            ReplaceRadioButton.IsChecked = !importAppendDefault;
            AppendRadioButton.IsChecked = importAppendDefault;
        }
        catch { /* ignore */ }

        try
        {
            ReplaceRadioButton.Checked += (_, _) => { try { _setImportAppendDefault?.Invoke(false); } catch { /* ignore */ } };
            AppendRadioButton.Checked += (_, _) => { try { _setImportAppendDefault?.Invoke(true); } catch { /* ignore */ } };
        }
        catch { /* ignore */ }
    }

    private async void SearchVideosButton_OnClick(object sender, RoutedEventArgs e)
    {
        try { SearchVideosStatusTextBlock.Text = ""; } catch { /* ignore */ }
        var q = (SearchVideosQueryTextBox.Text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(q))
        {
            try { SearchVideosStatusTextBlock.Text = "Enter a query."; } catch { /* ignore */ }
            return;
        }

        _ = int.TryParse(SearchVideosCountTextBox.Text, out var count);
        _ = int.TryParse(SearchVideosMinLenTextBox.Text, out var minLen);
        count = count <= 0 ? 50 : count;
        minLen = minLen < 0 ? 0 : minLen;

        try
        {
            SearchVideosButton.IsEnabled = false;
            try { SearchVideosStatusTextBlock.Text = "Searching…"; } catch { /* ignore */ }
            var append = AppendRadioButton.IsChecked ?? false;
            var dedupe = GlobalDedupeCheckBox.IsChecked ?? true;
            if (_searchVideosAsync is null)
                throw new InvalidOperationException("YouTube tab not initialized.");
            await _searchVideosAsync(q, count, minLen, append, dedupe, CancellationToken.None);
            try { SearchVideosStatusTextBlock.Text = "Done."; } catch { /* ignore */ }
        }
        catch (OperationCanceledException)
        {
            try { SearchVideosStatusTextBlock.Text = "Cancelled."; } catch { /* ignore */ }
        }
        catch (Exception ex)
        {
            try { SearchVideosStatusTextBlock.Text = ex.Message; } catch { /* ignore */ }
        }
        finally
        {
            try { SearchVideosButton.IsEnabled = true; } catch { /* ignore */ }
        }
    }

    private async void OpenUrlButton_OnClick(object sender, RoutedEventArgs e)
    {
        try { OpenUrlStatusTextBlock.Text = ""; } catch { /* ignore */ }
        var url = (OpenUrlTextBox.Text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            try { OpenUrlStatusTextBlock.Text = "Enter a URL."; } catch { /* ignore */ }
            return;
        }

        try { _setLastUrl?.Invoke(url); } catch { /* ignore */ }

        try
        {
            OpenUrlButton.IsEnabled = false;
            try { OpenUrlStatusTextBlock.Text = "Opening…"; } catch { /* ignore */ }
            if (_openUrlAsync is null)
                throw new InvalidOperationException("YouTube tab not initialized.");
            await _openUrlAsync(url, CancellationToken.None);
            try { OpenUrlStatusTextBlock.Text = "Done."; } catch { /* ignore */ }
        }
        catch (OperationCanceledException)
        {
            try { OpenUrlStatusTextBlock.Text = "Cancelled."; } catch { /* ignore */ }
        }
        catch (Exception ex)
        {
            try { OpenUrlStatusTextBlock.Text = ex.Message; } catch { /* ignore */ }
        }
        finally
        {
            try { OpenUrlButton.IsEnabled = true; } catch { /* ignore */ }
        }
    }

    private YoutubePlaylistHit? GetSelectedSearchPlaylist()
    {
        try
        {
            if (SearchPlaylistsResultsListBox.Tag is IReadOnlyList<YoutubePlaylistHit> hits &&
                SearchPlaylistsResultsListBox.SelectedIndex is int idx &&
                idx >= 0 && idx < hits.Count)
                return hits[idx];
        }
        catch { /* ignore */ }
        return null;
    }

    private async Task ImportHitAsync(YoutubePlaylistHit hit)
    {
        try
        {
            ImportSelectedPlaylistButton.IsEnabled = false;
            SearchPlaylistsStatusTextBlock.Text = "Importing…";
            var append = AppendRadioButton.IsChecked ?? false;
            var dedupe = GlobalDedupeCheckBox.IsChecked ?? true;

            if (_importPlaylistAsync is null)
                throw new InvalidOperationException("YouTube tab not initialized.");
            await _importPlaylistAsync(hit.UrlOrId, append, dedupe, CancellationToken.None);
            SearchPlaylistsStatusTextBlock.Text = "Imported.";
        }
        catch (Exception ex)
        {
            try { SearchPlaylistsStatusTextBlock.Text = ex.Message; } catch { /* ignore */ }
        }
        finally
        {
            try { ImportSelectedPlaylistButton.IsEnabled = true; } catch { /* ignore */ }
        }
    }

    private async void SearchPlaylistsButton_OnClick(object sender, RoutedEventArgs e)
    {
        try { SearchPlaylistsStatusTextBlock.Text = ""; } catch { /* ignore */ }
        try { SearchPlaylistsResultsListBox.ItemsSource = null; } catch { /* ignore */ }

        var q = (SearchPlaylistsQueryTextBox.Text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(q))
        {
            try { SearchPlaylistsStatusTextBlock.Text = "Enter a query."; } catch { /* ignore */ }
            return;
        }

        _ = int.TryParse(SearchPlaylistsCountTextBox.Text, out var count);
        count = count <= 0 ? 20 : count;

        try
        {
            SearchPlaylistsButton.IsEnabled = false;
            try { SearchPlaylistsStatusTextBlock.Text = "Searching…"; } catch { /* ignore */ }

            if (_searchPlaylistsAsync is null)
                throw new InvalidOperationException("YouTube tab not initialized.");
            var hits0 = await _searchPlaylistsAsync(q, count, CancellationToken.None);

            var hits = hits0.ToList();
            var needCount = hits.Count(h => h.ItemCount is null);
            if (needCount > 0 && _tryGetPlaylistItemCountAsync is not null)
            {
                var maxProbe = Math.Min(hits.Count, 20);
                var probed = 0;
                for (var i = 0; i < maxProbe; i++)
                {
                    if (hits[i].ItemCount is not null)
                        continue;
                    probed++;
                    try { SearchPlaylistsStatusTextBlock.Text = $"Found {hits.Count}. Getting counts… ({probed}/{needCount})"; } catch { /* ignore */ }
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                    var n = await _tryGetPlaylistItemCountAsync(hits[i].UrlOrId, cts.Token);
                    if (n is int ok && ok > 0)
                        hits[i] = hits[i] with { ItemCount = ok };
                }
            }

            SearchPlaylistsResultsListBox.ItemsSource = hits
                .Select(h => $"{h.Title}{(string.IsNullOrWhiteSpace(h.Channel) ? "" : $" — {h.Channel}")}{(h.ItemCount is int n ? $" ({n})" : "")}")
                .ToList();
            SearchPlaylistsResultsListBox.Tag = hits;
            SearchPlaylistsStatusTextBlock.Text = hits.Count == 0 ? "No playlists found." : $"Found {hits.Count}.";
        }
        catch (Exception ex)
        {
            try { SearchPlaylistsStatusTextBlock.Text = ex.Message; } catch { /* ignore */ }
        }
        finally
        {
            try { SearchPlaylistsButton.IsEnabled = true; } catch { /* ignore */ }
        }
    }

    private async void ImportSelectedPlaylistButton_OnClick(object sender, RoutedEventArgs e)
    {
        var hit = GetSelectedSearchPlaylist();
        if (hit is null)
        {
            try { SearchPlaylistsStatusTextBlock.Text = "Select a playlist first."; } catch { /* ignore */ }
            return;
        }
        await ImportHitAsync(hit);
    }

    private async void SearchPlaylistsResultsListBox_OnMouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var hit = GetSelectedSearchPlaylist();
        if (hit is null)
            return;
        await ImportHitAsync(hit);
    }

    private async void ImportPlaylistButton_OnClick(object sender, RoutedEventArgs e)
    {
        try { ImportPlaylistStatusTextBlock.Text = ""; } catch { /* ignore */ }
        var url = (ImportPlaylistUrlTextBox.Text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            try { ImportPlaylistStatusTextBlock.Text = "Enter a playlist URL or ID."; } catch { /* ignore */ }
            return;
        }

        var append = AppendRadioButton.IsChecked ?? false;
        var dedupe = GlobalDedupeCheckBox.IsChecked ?? true;
        try
        {
            ImportPlaylistButton.IsEnabled = false;
            try { ImportPlaylistStatusTextBlock.Text = "Importing…"; } catch { /* ignore */ }

            if (_importPlaylistAsync is null)
                throw new InvalidOperationException("YouTube tab not initialized.");
            await _importPlaylistAsync(url, append, dedupe, CancellationToken.None);
            try { ImportPlaylistStatusTextBlock.Text = "Imported."; } catch { /* ignore */ }
        }
        catch (Exception ex)
        {
            try { ImportPlaylistStatusTextBlock.Text = ex.Message; } catch { /* ignore */ }
        }
        finally
        {
            try { ImportPlaylistButton.IsEnabled = true; } catch { /* ignore */ }
        }
    }

    private YoutubePlaylistHit? GetSelectedAccountPlaylist()
    {
        try
        {
            if (AccountPlaylistsListBox.Tag is IReadOnlyList<YoutubePlaylistHit> hits &&
                AccountPlaylistsListBox.SelectedIndex is int idx &&
                idx >= 0 && idx < hits.Count)
                return hits[idx];
        }
        catch { /* ignore */ }
        return null;
    }

    private async Task ImportAccountHitAsync(YoutubePlaylistHit hit)
    {
        try
        {
            ImportSelectedAccountPlaylistButton.IsEnabled = false;
            AccountPlaylistsStatusTextBlock.Text = "Importing…";
            var append = AppendRadioButton.IsChecked ?? false;
            var dedupe = GlobalDedupeCheckBox.IsChecked ?? true;

            if (_importPlaylistAsync is null)
                throw new InvalidOperationException("YouTube tab not initialized.");
            await _importPlaylistAsync(hit.UrlOrId, append, dedupe, CancellationToken.None);
            AccountPlaylistsStatusTextBlock.Text = "Imported.";
        }
        catch (Exception ex)
        {
            try { AccountPlaylistsStatusTextBlock.Text = ex.Message; } catch { /* ignore */ }
        }
        finally
        {
            try { ImportSelectedAccountPlaylistButton.IsEnabled = true; } catch { /* ignore */ }
        }
    }

    private async void LoadAccountPlaylistsButton_OnClick(object sender, RoutedEventArgs e)
    {
        try { AccountPlaylistsStatusTextBlock.Text = ""; } catch { /* ignore */ }
        try { AccountPlaylistsListBox.ItemsSource = null; } catch { /* ignore */ }

        try
        {
            LoadAccountPlaylistsButton.IsEnabled = false;
            try { AccountPlaylistsStatusTextBlock.Text = "Loading…"; } catch { /* ignore */ }

            if (_listAccountPlaylistsAsync is null)
                throw new InvalidOperationException("YouTube tab not initialized.");
            var hits = await _listAccountPlaylistsAsync(80, CancellationToken.None);
            AccountPlaylistsListBox.ItemsSource = hits
                .Select(h => $"{h.Title}{(string.IsNullOrWhiteSpace(h.Channel) ? "" : $" — {h.Channel}")}{(h.ItemCount is int n ? $" ({n})" : "")}")
                .ToList();
            AccountPlaylistsListBox.Tag = hits;
            AccountPlaylistsStatusTextBlock.Text = hits.Count == 0
                ? "No playlists found. See app.log for details; often a YouTube/yt-dlp limitation. Use Import tab to paste a playlist URL."
                : $"Found {hits.Count}.";
        }
        catch (Exception ex)
        {
            try { AccountPlaylistsStatusTextBlock.Text = $"{ex.Message} (See app.log)"; } catch { /* ignore */ }
        }
        finally
        {
            try { LoadAccountPlaylistsButton.IsEnabled = true; } catch { /* ignore */ }
        }
    }

    private async void ImportSelectedAccountPlaylistButton_OnClick(object sender, RoutedEventArgs e)
    {
        var hit = GetSelectedAccountPlaylist();
        if (hit is null)
        {
            try { AccountPlaylistsStatusTextBlock.Text = "Select a playlist first."; } catch { /* ignore */ }
            return;
        }
        await ImportAccountHitAsync(hit);
    }

    private async void AccountPlaylistsListBox_OnMouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var hit = GetSelectedAccountPlaylist();
        if (hit is null)
            return;
        await ImportAccountHitAsync(hit);
    }
}

