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

    public LyricsWindow(
        Func<bool> lyricsEnabled,
        Func<bool> hasLyrics,
        Func<string?> lyricsTitle,
        Func<IReadOnlyList<string>> lyricsLines)
    {
        _getLyricsEnabled = lyricsEnabled;
        _getHasLyrics = hasLyrics;
        _getLyricsTitle = lyricsTitle;
        _getLyricsLines = lyricsLines;

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

            var title = _getLyricsTitle();
            var lines = _getLyricsLines();

            if (string.IsNullOrWhiteSpace(title))
                title = "(No title)";

            LyricsTitleLabel.Content = title;
            LyricsListBox.ItemsSource = lines;
        }
        catch { /* ignore */ }
    }

    private void ChromeBar_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        try { if (e.ClickCount == 2) return; DragMove(); } catch { /* ignore */ }
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        try { Close(); } catch { /* ignore */ }
    }
}
