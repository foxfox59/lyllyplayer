using System;
using System.Windows;
using System.Windows.Interop;

namespace LyllyPlayer.Utils;

public sealed class TopmostDialogOwner : IDisposable
{
    private readonly Window _window;

    public TopmostDialogOwner(Window? preferredOwner = null)
    {
        _window = new Window
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
            var owner = preferredOwner ?? DialogOwnerHelper.GetBestOwnerWindow();
            if (owner is not null)
                _window.Owner = owner;
        }
        catch { /* ignore */ }

        try { _window.ShowActivated = false; } catch { /* ignore */ }
        try { _window.Show(); } catch { /* ignore */ }
    }

    public Window OwnerWindow => _window;

    public IntPtr OwnerHwnd
    {
        get
        {
            try { return new WindowInteropHelper(_window).Handle; }
            catch { return IntPtr.Zero; }
        }
    }

    public void Dispose()
    {
        try { _window.Close(); } catch { /* ignore */ }
    }
}

