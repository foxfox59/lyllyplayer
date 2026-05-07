using System;
using System.Windows;

namespace LyllyPlayer.Utils;

public static class TopmostMessageBox
{
    public static MessageBoxResult Show(string messageBoxText, string caption, MessageBoxButton button, MessageBoxImage icon)
        => Show(owner: null, messageBoxText, caption, button, icon);

    public static MessageBoxResult Show(Window? owner, string messageBoxText, string caption, MessageBoxButton button, MessageBoxImage icon)
    {
        try
        {
            // Create a hidden topmost owner so the dialog can't appear behind other apps.
            var w = new Window
            {
                Width = 1,
                Height = 1,
                Left = -10000,
                Top = -10000,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                ResizeMode = ResizeMode.NoResize,
                AllowsTransparency = true,
                Background = System.Windows.Media.Brushes.Transparent,
                Opacity = 0,
                Topmost = true,
            };

            try
            {
                // Prefer passed owner for centering, but keep topmost behavior via our hidden window.
                if (owner is not null)
                {
                    try { w.Owner = owner; } catch { /* ignore */ }
                }
                else
                {
                    try
                    {
                        var best = DialogOwnerHelper.GetBestOwnerWindow();
                        if (best is not null)
                            w.Owner = best;
                    }
                    catch { /* ignore */ }
                }
            }
            catch { /* ignore */ }

            try { w.ShowActivated = false; } catch { /* ignore */ }
            try { w.Show(); } catch { /* ignore */ }

            try
            {
                return System.Windows.MessageBox.Show(w, messageBoxText, caption, button, icon);
            }
            finally
            {
                try { w.Close(); } catch { /* ignore */ }
            }
        }
        catch
        {
            try
            {
                return System.Windows.MessageBox.Show(owner, messageBoxText, caption, button, icon);
            }
            catch
            {
                return MessageBoxResult.None;
            }
        }
    }
}

