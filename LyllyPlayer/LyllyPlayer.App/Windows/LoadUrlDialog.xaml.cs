using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using LyllyPlayer.Utils;

namespace LyllyPlayer.Windows;

public partial class LoadUrlDialog : Window
{
    private const string PlacementKey = "LoadUrlDialog";

    public LoadUrlDialog(string initialText)
    {
        InitializeComponent();
        UrlTextBox.Text = initialText ?? "";
        Loaded += (_, _) =>
        {
            ModalWindowPlacementStore.Restore(this, PlacementKey);
            try
            {
                UrlTextBox.Focus();
                UrlTextBox.SelectAll();
            }
            catch { /* ignore */ }
        };
        Closing += (_, _) => ModalWindowPlacementStore.Persist(this, PlacementKey);
        Closed += OnClosedCleanup;
    }

    private void OnClosedCleanup(object? sender, EventArgs e)
    {
        try
        {
            if (Mouse.Captured is UIElement cap && IsAncestorOf(cap))
                Mouse.Capture(null);
        }
        catch
        {
            // ignore
        }

        try
        {
            if (Owner is Window ow)
            {
                ow.IsEnabled = true;
                ow.Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        ow.Activate();
                    }
                    catch
                    {
                        /* ignore */
                    }
                }), DispatcherPriority.Background);
            }
        }
        catch
        {
            // ignore
        }
    }

    public string UrlText => UrlTextBox.Text?.Trim() ?? "";

    private void LoadButton_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void UrlTextBox_OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            DialogResult = true;
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            DialogResult = false;
        }
    }

    private void ChromeBar_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
            return;
        try { DragMove(); } catch { /* ignore */ }
    }
}

