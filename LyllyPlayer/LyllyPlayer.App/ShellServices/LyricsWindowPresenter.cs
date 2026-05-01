using System;
using System.Collections.Generic;

namespace LyllyPlayer.ShellServices;

public readonly record struct LyricsWindowViewState(
    string TitleText,
    IReadOnlyList<string>? Lines,
    bool IsPlainLyrics,
    int? InitialSelectedIndex
);

/// <summary>
/// Presenter for <c>LyricsWindow</c> that computes what the view should show.
/// Keeps the WPF window mostly UI-only.
/// </summary>
public sealed class LyricsWindowPresenter
{
    public LyricsWindowViewState BuildRefreshState(
        bool lyricsEnabled,
        bool hasLyrics,
        bool isPlainLyrics,
        string? title,
        IReadOnlyList<string> lines,
        Func<int> getCurrentLineIndex,
        int itemsCountOrZero)
    {
        if (!lyricsEnabled)
            return new LyricsWindowViewState("Lyrics not enabled", null, IsPlainLyrics: false, InitialSelectedIndex: null);

        if (!hasLyrics)
            return new LyricsWindowViewState("No lyrics available", null, IsPlainLyrics: false, InitialSelectedIndex: null);

        if (isPlainLyrics)
            return new LyricsWindowViewState("No synced lyrics available", lines, IsPlainLyrics: true, InitialSelectedIndex: null);

        var tt = string.IsNullOrWhiteSpace(title) ? "(No title)" : title!;

        var idx = 0;
        try { idx = getCurrentLineIndex(); } catch { idx = 0; }
        if (itemsCountOrZero <= 0)
            idx = 0;
        else if (idx < 0 || idx >= itemsCountOrZero)
            idx = 0;

        return new LyricsWindowViewState(tt, lines, IsPlainLyrics: false, InitialSelectedIndex: idx);
    }

    public bool TryGetHighlightIndex(bool isPlainLyrics, int currentLineIndex, int itemCount, out int selectedIndex)
    {
        selectedIndex = -1;
        if (isPlainLyrics)
            return false;
        if (itemCount <= 0)
            return false;
        if (currentLineIndex < 0 || currentLineIndex >= itemCount)
            return true; // clear selection
        selectedIndex = currentLineIndex;
        return true;
    }
}

