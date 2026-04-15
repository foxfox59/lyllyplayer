using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using LyllyPlayer.Utils;

namespace LyllyPlayer.Windows;

public partial class SearchYoutubeDialog : Window
{
    private const string PlacementKey = "SearchYoutubeDialog";

    public SearchYoutubeDialog(string initialQuery, int initialCount, int initialMinLengthSeconds)
    {
        InitializeComponent();
        QueryTextBox.Text = (initialQuery ?? "").Trim();
        try
        {
            var c = initialCount;
            if (c <= 0) c = 50;
            CountTextBox.Text = c.ToString(CultureInfo.InvariantCulture);
        }
        catch { /* ignore */ }

        try
        {
            var s = initialMinLengthSeconds;
            var idx = s switch
            {
                >= 120 => 3,
                >= 60 => 2,
                >= 30 => 1,
                _ => 0
            };
            MinLengthComboBox.SelectedIndex = idx;
        }
        catch { /* ignore */ }

        Loaded += (_, _) =>
        {
            ModalWindowPlacementStore.Restore(this, PlacementKey);
            try
            {
                QueryTextBox.Focus();
                QueryTextBox.SelectAll();
            }
            catch { /* ignore */ }
        };
        Closing += (_, _) => ModalWindowPlacementStore.Persist(this, PlacementKey);
        Closed += OnClosedCleanup;
    }

    /// <summary>
    /// Modal <see cref="Window.ShowDialog"/> disables the owner; in edge cases (Escape / chrome / double-close)
    /// the owner can stay disabled or keep capture, making the app ignore clicks until restart.
    /// </summary>
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

    public string QueryText => (QueryTextBox.Text ?? "").Trim();

    public int ResultCount
    {
        get
        {
            var s = (CountTextBox.Text ?? "").Trim();
            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                return n;
            return 50;
        }
    }

    public int MinLengthSeconds
    {
        get
        {
            try
            {
                // Index matches XAML items: Any, 30 sec, 1 min, 2 min
                return (MinLengthComboBox.SelectedIndex) switch
                {
                    1 => 30,
                    2 => 60,
                    3 => 120,
                    _ => 0
                };
            }
            catch
            {
                return 0;
            }
        }
    }

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

    private void QueryTextBox_OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
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

