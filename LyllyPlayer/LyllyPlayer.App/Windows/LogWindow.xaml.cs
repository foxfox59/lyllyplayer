using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using LyllyPlayer.Utils;

namespace LyllyPlayer.Windows;

public partial class LogWindow : Window
{
    private const string PlacementKey = "LogWindow";

    private bool _chromeDragging;
    private System.Windows.Point _chromeDragStartScreen;
    private double _chromeDragStartLeft;
    private double _chromeDragStartTop;

    public LogWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => ModalWindowPlacementStore.Restore(this, PlacementKey);
        Closing += (_, _) => ModalWindowPlacementStore.Persist(this, PlacementKey);
        Closed += (_, _) => AppLog.Changed -= OnAppLogChanged;
        AppLog.Changed += OnAppLogChanged;
        Refresh();
    }

    private void OnAppLogChanged(object? sender, EventArgs e)
        => Dispatcher.Invoke(Refresh);

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
            System.Windows.MessageBox.Show(this, ex.Message, "Failed to open log", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Refresh()
    {
        var text = string.Join(Environment.NewLine, AppLog.Snapshot());
        LogTextBox.Text = text;
        LogTextBox.ScrollToEnd();
    }

    private void ChromeCloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        try { Close(); } catch { /* ignore */ }
    }

    private void ChromeBar_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
            return;

        try
        {
            _chromeDragging = true;
            _chromeDragStartLeft = Left;
            _chromeDragStartTop = Top;
            _chromeDragStartScreen = PointToScreen(e.GetPosition(this));

            CaptureMouse();
            MouseMove -= ChromeDrag_MouseMove;
            MouseLeftButtonUp -= ChromeDrag_MouseLeftButtonUp;
            MouseMove += ChromeDrag_MouseMove;
            MouseLeftButtonUp += ChromeDrag_MouseLeftButtonUp;

            e.Handled = true;
        }
        catch
        {
            _chromeDragging = false;
        }
    }

    private void ChromeDrag_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_chromeDragging)
            return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndChromeDrag();
            return;
        }

        try
        {
            var cur = PointToScreen(e.GetPosition(this));
            var dx = cur.X - _chromeDragStartScreen.X;
            var dy = cur.Y - _chromeDragStartScreen.Y;
            Left = _chromeDragStartLeft + dx;
            Top = _chromeDragStartTop + dy;
        }
        catch
        {
            EndChromeDrag();
        }
    }

    private void ChromeDrag_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => EndChromeDrag();

    private void EndChromeDrag()
    {
        if (!_chromeDragging)
            return;
        _chromeDragging = false;
        try { ReleaseMouseCapture(); } catch { /* ignore */ }
        MouseMove -= ChromeDrag_MouseMove;
        MouseLeftButtonUp -= ChromeDrag_MouseLeftButtonUp;
    }
}
