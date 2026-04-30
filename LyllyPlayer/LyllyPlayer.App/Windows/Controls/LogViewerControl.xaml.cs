using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LyllyPlayer.Utils;

namespace LyllyPlayer.Windows.Controls;

public partial class LogViewerControl : System.Windows.Controls.UserControl
{
    private readonly DispatcherTimer _timer;
    private bool _paused;
    private bool _suspended;
    private int _lastLen;

    public LogViewerControl()
    {
        InitializeComponent();
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(400),
        };
        _timer.Tick += (_, _) => Tick();
        Loaded += (_, _) =>
        {
            _timer.Start();
            _ = Dispatcher.BeginInvoke(Refresh, DispatcherPriority.Background);
        };
        Unloaded += (_, _) =>
        {
            try { _timer.Stop(); } catch { /* ignore */ }
        };
    }

    public void SetSuspended(bool suspended)
    {
        _suspended = suspended;
        SuspendedOverlay.Visibility = suspended ? Visibility.Visible : Visibility.Collapsed;
        StateTextBlock.Text = suspended ? "Suspended" : "";
        if (!suspended)
            Refresh();
    }

    private void PauseToggleButton_OnClick(object sender, RoutedEventArgs e)
    {
        _paused = PauseToggleButton.IsChecked == true;
        PauseToggleButton.Content = _paused ? "Resume" : "Pause";
        StateTextBlock.Text = _paused ? "Paused" : "";
        if (!_paused)
            Refresh();
    }

    private void RefreshButton_OnClick(object sender, RoutedEventArgs e) => Refresh();

    private void OpenFileButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = AppLog.LogPath,
                UseShellExecute = true,
            };
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                Window.GetWindow(this),
                ex.Message,
                "Failed to open log",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void Tick()
    {
        if (_suspended || _paused)
            return;

        try
        {
            var len = 0;
            try { len = (int)new FileInfo(AppLog.LogPath).Length; } catch { /* ignore */ }
            if (len != _lastLen)
                Refresh();
        }
        catch { /* ignore */ }
    }

    private void Refresh()
    {
        if (_suspended)
            return;

        var text = ReadTailBestEffort(AppLog.LogPath, maxChars: 120_000);
        LogTextBox.Text = text;
        LogTextBox.ScrollToEnd();
        _lastLen = text.Length;
    }

    private static string ReadTailBestEffort(string path, int maxChars)
    {
        try
        {
            if (!File.Exists(path))
                return "";

            // Approx: UTF-8, average ~1 byte per char for logs.
            var maxBytes = Math.Max(4096, maxChars);
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var len = fs.Length;
            var start = Math.Max(0, len - maxBytes);
            fs.Seek(start, SeekOrigin.Begin);

            using var sr = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var s = sr.ReadToEnd();

            // If we started mid-line, drop the first partial line.
            if (start > 0)
            {
                var nl = s.IndexOf('\n');
                if (nl >= 0 && nl + 1 < s.Length)
                    s = s[(nl + 1)..];
            }

            if (s.Length <= maxChars)
                return s;

            return s[^maxChars..];
        }
        catch
        {
            return "";
        }
    }
}

