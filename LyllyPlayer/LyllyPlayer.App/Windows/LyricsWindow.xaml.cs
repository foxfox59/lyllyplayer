using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LyllyPlayer.Utils;
using LyllyPlayer.ShellServices;

namespace LyllyPlayer.Windows;

public partial class LyricsWindow : Window
{
    private readonly Func<bool> _getLyricsEnabled;
    private readonly Func<bool> _getHasLyrics;
    private readonly Func<string?> _getLyricsTitle;
    private readonly Func<IReadOnlyList<string>> _getLyricsLines;
    private readonly Func<bool> _getIsPlainLyrics;
    private readonly Func<int> _getCurrentLineIndex;
    private int _lastScrolledIndex = -1;
    private readonly LyricsWindowPresenter _presenter = new();

    public LyricsWindow(
        Func<bool> lyricsEnabled,
        Func<bool> hasLyrics,
        Func<string?> lyricsTitle,
        Func<IReadOnlyList<string>> lyricsLines,
        Func<bool> isPlainLyrics,
        Func<int> getCurrentLineIndex)
    {
        _getLyricsEnabled = lyricsEnabled;
        _getHasLyrics = hasLyrics;
        _getLyricsTitle = lyricsTitle;
        _getLyricsLines = lyricsLines;
        _getIsPlainLyrics = isPlainLyrics;
        _getCurrentLineIndex = getCurrentLineIndex;

        InitializeComponent();

        Loaded += (_, _) =>
        {
            ModalWindowPlacementStore.Restore(this, "LyricsWindow");
            Refresh();
        };
        Closing += (_, _) => ModalWindowPlacementStore.Persist(this, "LyricsWindow");
    }

    /// <summary>
    /// Refreshes the display based on current lyrics state.
    /// Call this when the track changes or lyrics state changes.
    /// </summary>
    public void Refresh()
    {
        try
        {
            var enabled = _getLyricsEnabled();
            var hasLyrics = false;
            try { hasLyrics = _getHasLyrics(); } catch { hasLyrics = false; }
            var isPlain = false;
            try { isPlain = _getIsPlainLyrics(); } catch { isPlain = false; }
            var title = _getLyricsTitle();
            var lines = _getLyricsLines();

            // Note: Items.Count isn't updated until after ItemsSource assignment; pass current count as best-effort.
            var state = _presenter.BuildRefreshState(
                lyricsEnabled: enabled,
                hasLyrics: hasLyrics,
                isPlainLyrics: isPlain,
                title: title,
                lines: lines,
                getCurrentLineIndex: _getCurrentLineIndex,
                itemsCountOrZero: LyricsListBox.Items.Count);

            LyricsTitleLabel.Content = state.TitleText;
            LyricsListBox.ItemsSource = state.Lines;

            if (state.Lines is null)
                return;
            if (state.IsPlainLyrics)
                return;

            // Defer initial selection/scroll until after layout settles.
            // On reopen we want to keep the currently highlighted line, not jump to index 0.
            _lastScrolledIndex = -1;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (LyricsListBox.Items.Count > 0)
                    {
                        var idx = state.InitialSelectedIndex ?? 0;
                        if (idx < 0 || idx >= LyricsListBox.Items.Count)
                            idx = 0;

                        LyricsListBox.SelectedIndex = idx;
                        _lastScrolledIndex = idx;

                        Dispatcher.BeginInvoke(
                            new Action(() => ScrollSelectedNearTopBestEffort(selectedIndex: idx)),
                            DispatcherPriority.Background);
                    }
                }
                catch { /* ignore */ }
            }), DispatcherPriority.Loaded);
        }
        catch { /* ignore */ }
    }

    /// <summary>
    /// Highlights the currently playing lyric line in the ListBox.
    /// Scrolls into view only when the highlighted line changes (not on every timer tick).
    /// Only effective for synced (time-aligned) lyrics. No-op for plain lyrics.
    /// </summary>
    public void RefreshCurrentLine()
    {
        try
        {
            // Don't update highlight for plain lyrics (no time alignment)
            var isPlain = false;
            try { isPlain = _getIsPlainLyrics(); } catch { isPlain = false; }
            var index = -1;
            try { index = _getCurrentLineIndex(); } catch { index = -1; }
            if (!_presenter.TryGetHighlightIndex(isPlain, index, LyricsListBox.Items.Count, out var selectedIndex))
                return;
            if (selectedIndex < 0)
            {
                LyricsListBox.SelectedIndex = -1;
                _lastScrolledIndex = -1;
                return;
            }

            // Only scroll when the line actually changes — prevents jittery fighting with user scroll
            if (selectedIndex == _lastScrolledIndex)
            {
                LyricsListBox.SelectedIndex = selectedIndex;
                return;
            }

            LyricsListBox.SelectedIndex = selectedIndex;
            _lastScrolledIndex = selectedIndex;

            // Pin the selected line near the top for better readability.
            // Defer so layout/containers are ready and we don't fight ScrollIntoView.
            Dispatcher.BeginInvoke(
                new Action(() => ScrollSelectedNearTopBestEffort(selectedIndex: selectedIndex)),
                DispatcherPriority.Background);
        }
        catch { /* ignore */ }
    }

    private void ScrollSelectedNearTopBestEffort(int selectedIndex)
    {
        try
        {
            if (selectedIndex < 0 || selectedIndex >= LyricsListBox.Items.Count)
                return;

            // Ensure container exists (non-virtualizing list should have it after ScrollIntoView, but be defensive).
            if (LyricsListBox.ItemContainerGenerator.ContainerFromIndex(selectedIndex) is not ListBoxItem item)
                return;

            var sv = FindScrollViewer(LyricsListBox);
            if (sv is null)
                return;

            // Desired: keep highlighted line near the top with some padding.
            const double topPaddingPx = 18;

            // Compute the item’s Y within the scroll viewer, then convert to an absolute scroll offset target.
            var pt = item.TransformToAncestor(sv).Transform(new System.Windows.Point(0, 0));
            var itemTopYInViewport = pt.Y;
            var currentOffset = sv.VerticalOffset;
            var itemTopYInContent = currentOffset + itemTopYInViewport;
            var desiredOffset = itemTopYInContent - topPaddingPx;

            // Clamp to valid scroll range.
            desiredOffset = Math.Max(0, Math.Min(desiredOffset, sv.ScrollableHeight));
            sv.ScrollToVerticalOffset(desiredOffset);
        }
        catch { /* ignore */ }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        try
        {
            if (root is ScrollViewer sv)
                return sv;

            var n = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < n; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                var found = FindScrollViewer(child);
                if (found is not null)
                    return found;
            }
        }
        catch { /* ignore */ }
        return null;
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

    private void CopyButton_OnClick(object sender, RoutedEventArgs e)
    {
        CopyLyricsToClipboardBestEffort();
    }

    private void CopyCommand_CanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        try
        {
            e.CanExecute = LyricsListBox?.Items is { Count: > 0 };
            e.Handled = true;
        }
        catch
        {
            e.CanExecute = false;
            e.Handled = true;
        }
    }

    private void CopyCommand_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        CopyLyricsToClipboardBestEffort();
        e.Handled = true;
    }

    private void CopyLyricsToClipboardBestEffort()
    {
        try
        {
            var enabled = _getLyricsEnabled();
            if (!enabled || !_getHasLyrics())
            {
                var fallback = (LyricsTitleLabel?.Content?.ToString() ?? "No lyrics available").Trim();
                if (!string.IsNullOrWhiteSpace(fallback))
                    System.Windows.Clipboard.SetText(fallback);
                return;
            }

            var lines = _getLyricsLines();
            if (lines is null || lines.Count == 0)
                return;

            var text = string.Join(Environment.NewLine, lines);
            if (!string.IsNullOrWhiteSpace(text))
                System.Windows.Clipboard.SetText(text);
        }
        catch { /* ignore */ }
    }
}
