using System;
using System.IO;
using System.Windows;

namespace LyllyPlayer.Windows;

public enum PlaylistOpenMode
{
    Cancel = 0,
    Append = 1,
    Replace = 2,
}

public partial class OpenPlaylistFileDialog : Window
{
    public PlaylistOpenMode Mode { get; private set; } = PlaylistOpenMode.Cancel;

    public OpenPlaylistFileDialog(string path)
    {
        InitializeComponent();
        try
        {
            var name = "";
            try { name = Path.GetFileName(path) ?? ""; } catch { name = ""; }
            PathTextBlock.Text = string.IsNullOrWhiteSpace(name) ? path : $"{name}\n{path}";
        }
        catch { /* ignore */ }
    }

    private void AppendButton_OnClick(object sender, RoutedEventArgs e)
    {
        Mode = PlaylistOpenMode.Append;
        try { DialogResult = true; } catch { /* ignore */ }
        try { Close(); } catch { /* ignore */ }
    }

    private void ReplaceButton_OnClick(object sender, RoutedEventArgs e)
    {
        Mode = PlaylistOpenMode.Replace;
        try { DialogResult = true; } catch { /* ignore */ }
        try { Close(); } catch { /* ignore */ }
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        Mode = PlaylistOpenMode.Cancel;
        try { DialogResult = false; } catch { /* ignore */ }
        try { Close(); } catch { /* ignore */ }
    }
}

