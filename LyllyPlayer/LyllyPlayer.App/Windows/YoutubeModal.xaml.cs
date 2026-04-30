using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using LyllyPlayer.Models;

namespace LyllyPlayer.Windows;

public partial class YoutubeModal : Window
{
    private readonly Func<string, int, int, bool, bool, CancellationToken, Task> _searchVideosAsync;
    private readonly Func<string, int, CancellationToken, Task<IReadOnlyList<YoutubePlaylistHit>>> _searchPlaylistsAsync;
    private readonly Func<int, CancellationToken, Task<IReadOnlyList<YoutubePlaylistHit>>> _listAccountPlaylistsAsync;
    private readonly Func<string, bool, bool, CancellationToken, Task> _importPlaylistAsync;
    private readonly Func<string, CancellationToken, Task<int?>> _tryGetPlaylistItemCountAsync;
    private readonly Action<bool> _setImportAppendDefault;
    private readonly Func<string> _getLastUrl;
    private readonly Action<string> _setLastUrl;
    private readonly Func<string, CancellationToken, Task> _openUrlAsync;

    public YoutubeModal(
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

        InitializeComponent();

        try { OpenUrlTextBox.Text = _getLastUrl() ?? ""; } catch { /* ignore */ }

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
            ReplaceRadioButton.Checked += (_, _) => { try { _setImportAppendDefault(false); } catch { /* ignore */ } };
            AppendRadioButton.Checked += (_, _) => { try { _setImportAppendDefault(true); } catch { /* ignore */ } };
        }
        catch { /* ignore */ }
    }

    private void ChromeBar_OnMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton != System.Windows.Input.MouseButton.Left)
            return;
        try
        {
            DragMove();
            e.Handled = true;
        }
        catch { /* ignore */ }
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        try { Close(); } catch { /* ignore */ }
    }

    private async void SearchVideosButton_OnClick(object sender, RoutedEventArgs e)
    {
        SearchVideosStatusTextBlock.Text = "";
        var q = (SearchVideosQueryTextBox.Text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(q))
        {
            SearchVideosStatusTextBlock.Text = "Enter a query.";
            return;
        }

        _ = int.TryParse(SearchVideosCountTextBox.Text, out var count);
        _ = int.TryParse(SearchVideosMinLenTextBox.Text, out var minLen);
        count = count <= 0 ? 50 : count;
        minLen = minLen < 0 ? 0 : minLen;

        try
        {
            SearchVideosButton.IsEnabled = false;
            SearchVideosStatusTextBlock.Text = "Searching…";
            var append = AppendRadioButton.IsChecked ?? false;
            var dedupe = GlobalDedupeCheckBox.IsChecked ?? true;
            await _searchVideosAsync(q, count, minLen, append, dedupe, CancellationToken.None);
            SearchVideosStatusTextBlock.Text = "Done.";
        }
        catch (OperationCanceledException)
        {
            SearchVideosStatusTextBlock.Text = "Cancelled.";
        }
        catch (Exception ex)
        {
            SearchVideosStatusTextBlock.Text = ex.Message;
        }
        finally
        {
            SearchVideosButton.IsEnabled = true;
        }
    }

    private async void OpenUrlButton_OnClick(object sender, RoutedEventArgs e)
    {
        OpenUrlStatusTextBlock.Text = "";
        var url = (OpenUrlTextBox.Text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            OpenUrlStatusTextBlock.Text = "Enter a URL.";
            return;
        }

        try { _setLastUrl(url); } catch { /* ignore */ }

        try
        {
            OpenUrlButton.IsEnabled = false;
            OpenUrlStatusTextBlock.Text = "Opening…";
            await _openUrlAsync(url, CancellationToken.None);
            OpenUrlStatusTextBlock.Text = "Done.";
        }
        catch (OperationCanceledException)
        {
            OpenUrlStatusTextBlock.Text = "Cancelled.";
        }
        catch (Exception ex)
        {
            OpenUrlStatusTextBlock.Text = ex.Message;
        }
        finally
        {
            OpenUrlButton.IsEnabled = true;
        }
    }

    private async void SearchPlaylistsButton_OnClick(object sender, RoutedEventArgs e)
    {
        SearchPlaylistsStatusTextBlock.Text = "";
        SearchPlaylistsResultsListBox.ItemsSource = null;

        var q = (SearchPlaylistsQueryTextBox.Text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(q))
        {
            SearchPlaylistsStatusTextBlock.Text = "Enter a query.";
            return;
        }
        _ = int.TryParse(SearchPlaylistsCountTextBox.Text, out var count);
        count = count <= 0 ? 20 : count;

        try
        {
            SearchPlaylistsButton.IsEnabled = false;
            SearchPlaylistsStatusTextBlock.Text = "Searching…";
            var hits0 = await _searchPlaylistsAsync(q, count, CancellationToken.None);

            // Best-effort: enrich playlist counts for results that don't include it.
            var hits = hits0.ToList();
            var needCount = hits.Count(h => h.ItemCount is null);
            if (needCount > 0)
            {
                var maxProbe = Math.Min(hits.Count, 20);
                var probed = 0;
                for (var i = 0; i < maxProbe; i++)
                {
                    if (hits[i].ItemCount is not null)
                        continue;
                    probed++;
                    SearchPlaylistsStatusTextBlock.Text = $"Found {hits.Count}. Getting counts… ({probed}/{needCount})";
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
            SearchPlaylistsStatusTextBlock.Text = ex.Message;
        }
        finally
        {
            SearchPlaylistsButton.IsEnabled = true;
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
            await _importPlaylistAsync(hit.UrlOrId, append, dedupe, CancellationToken.None);
            SearchPlaylistsStatusTextBlock.Text = "Imported.";
        }
        catch (Exception ex)
        {
            SearchPlaylistsStatusTextBlock.Text = ex.Message;
        }
        finally
        {
            ImportSelectedPlaylistButton.IsEnabled = true;
        }
    }

    private async void ImportSelectedPlaylistButton_OnClick(object sender, RoutedEventArgs e)
    {
        var hit = GetSelectedSearchPlaylist();
        if (hit is null)
        {
            SearchPlaylistsStatusTextBlock.Text = "Select a playlist first.";
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
        ImportPlaylistStatusTextBlock.Text = "";
        var url = (ImportPlaylistUrlTextBox.Text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            ImportPlaylistStatusTextBlock.Text = "Enter a playlist URL or ID.";
            return;
        }

        var append = AppendRadioButton.IsChecked ?? false;
        var dedupe = GlobalDedupeCheckBox.IsChecked ?? true;
        try
        {
            ImportPlaylistButton.IsEnabled = false;
            ImportPlaylistStatusTextBlock.Text = "Importing…";
            await _importPlaylistAsync(url, append, dedupe, CancellationToken.None);
            ImportPlaylistStatusTextBlock.Text = "Imported.";
        }
        catch (Exception ex)
        {
            ImportPlaylistStatusTextBlock.Text = ex.Message;
        }
        finally
        {
            ImportPlaylistButton.IsEnabled = true;
        }
    }

    private async void LoadAccountPlaylistsButton_OnClick(object sender, RoutedEventArgs e)
    {
        AccountPlaylistsStatusTextBlock.Text = "";
        AccountPlaylistsListBox.ItemsSource = null;
        try
        {
            LoadAccountPlaylistsButton.IsEnabled = false;
            AccountPlaylistsStatusTextBlock.Text = "Loading…";
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
            AccountPlaylistsStatusTextBlock.Text = $"{ex.Message} (See app.log)";
        }
        finally
        {
            LoadAccountPlaylistsButton.IsEnabled = true;
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
            await _importPlaylistAsync(hit.UrlOrId, append, dedupe, CancellationToken.None);
            AccountPlaylistsStatusTextBlock.Text = "Imported.";
        }
        catch (Exception ex)
        {
            AccountPlaylistsStatusTextBlock.Text = ex.Message;
        }
        finally
        {
            ImportSelectedAccountPlaylistButton.IsEnabled = true;
        }
    }

    private async void ImportSelectedAccountPlaylistButton_OnClick(object sender, RoutedEventArgs e)
    {
        var hit = GetSelectedAccountPlaylist();
        if (hit is null)
        {
            AccountPlaylistsStatusTextBlock.Text = "Select a playlist first.";
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

