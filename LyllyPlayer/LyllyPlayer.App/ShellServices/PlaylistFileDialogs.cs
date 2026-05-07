using System.Windows;

namespace LyllyPlayer.ShellServices;

public static class PlaylistFileDialogs
{
    public static string? PickPlaylistToLoad(Window? owner)
    {
        using var top = new LyllyPlayer.Utils.TopmostDialogOwner(owner);
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Load playlist",
            Filter = "Playlists (*.lyllylist;*.json;*.m3u;*.m3u8)|*.lyllylist;*.json;*.m3u;*.m3u8|Lylly playlist (*.lyllylist)|*.lyllylist|Playlist JSON (legacy) (*.json)|*.json|M3U playlist (*.m3u;*.m3u8)|*.m3u;*.m3u8|All files (*.*)|*.*",
            CheckFileExists = true,
            CheckPathExists = true,
            Multiselect = false
        };

        return dlg.ShowDialog(top.OwnerWindow) == true ? dlg.FileName : null;
    }

    public static string? PickPlaylistToSave(Window? owner, string? suggestedBaseName)
    {
        var safe = FileNameSanitizer.MakeSafeFileName(suggestedBaseName, fallback: "playlist", maxLen: 80);
        using var top = new LyllyPlayer.Utils.TopmostDialogOwner(owner);
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save playlist",
            Filter = "Lylly playlist (*.lyllylist)|*.lyllylist|M3U playlist (*.m3u;*.m3u8)|*.m3u;*.m3u8|Playlist JSON (legacy) (*.json)|*.json|All files (*.*)|*.*",
            AddExtension = true,
            DefaultExt = ".lyllylist",
            FileName = $"{safe}.lyllylist",
            OverwritePrompt = true
        };

        return dlg.ShowDialog(top.OwnerWindow) == true ? dlg.FileName : null;
    }
}

