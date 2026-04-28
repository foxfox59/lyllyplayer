using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using LyllyPlayer.Utils;

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

            if (!enabled)
            {
                LyricsTitleLabel.Content = "Lyrics not enabled";
                LyricsListBox.ItemsSource = null;
                return;
            }

            if (!_getHasLyrics())
            {
                LyricsTitleLabel.Content = "No lyrics available";
                LyricsListBox.ItemsSource = null;
                return;
            }

            var isPlain = _getIsPlainLyrics();
            var title = _getLyricsTitle();
            var lines = _getLyricsLines();

            if (isPlain)
            {
                LyricsTitleLabel.Content = "No synced lyrics available";
                LyricsListBox.ItemsSource = lines;
                return;
            }

            if (string.IsNullOrWhiteSpace(title))
                title = "(No title)";

            LyricsTitleLabel.Content = title;
            LyricsListBox.ItemsSource = lines;
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
            if (_getIsPlainLyrics())
                return;

            var index = _getCurrentLineIndex();
            if (index < 0 || index >= LyricsListBox.Items.Count)
            {
                LyricsListBox.SelectedIndex = -1;
                _lastScrolledIndex = -1;
                return;
            }

            // Only scroll when the line actually changes — prevents jittery fighting with user scroll
            if (index == _lastScrolledIndex)
            {
                LyricsListBox.SelectedIndex = index;
                return;
            }

            LyricsListBox.SelectedIndex = index;
            _lastScrolledIndex = index;

            // Ensure the selected item is scrolled into view
            var selectedItem = LyricsListBox.SelectedItem;
            if (selectedItem is not null)
                LyricsListBox.ScrollIntoView(selectedItem);
        }
        catch { /* ignore */ }
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
}
