using System.Windows;

namespace LyllyPlayer.ShellServices;

public static class PlaylistFileDialogs
{
    public static string? PickPlaylistToLoad(Window? owner)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Load playlist",
            Filter = "Playlists (*.json;*.m3u;*.m3u8)|*.json;*.m3u;*.m3u8|Playlist JSON (*.json)|*.json|M3U playlist (*.m3u;*.m3u8)|*.m3u;*.m3u8|All files (*.*)|*.*",
            CheckFileExists = true,
            CheckPathExists = true,
            Multiselect = false
        };

        return dlg.ShowDialog(owner) == true ? dlg.FileName : null;
    }

    public static string? PickPlaylistToSave(Window? owner, string? suggestedBaseName)
    {
        var safe = FileNameSanitizer.MakeSafeFileName(suggestedBaseName, fallback: "playlist", maxLen: 80);
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save playlist",
            Filter = "Playlist JSON (*.json)|*.json|M3U playlist (*.m3u;*.m3u8)|*.m3u;*.m3u8|All files (*.*)|*.*",
            AddExtension = true,
            DefaultExt = ".json",
            FileName = $"{safe}.json",
            OverwritePrompt = true
        };

        return dlg.ShowDialog(owner) == true ? dlg.FileName : null;
    }
}

