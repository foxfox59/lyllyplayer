using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace LyllyPlayer.ShellServices;

public static class ListBoxCentering
{
    public static void CenterOnItem<T>(
        System.Windows.Controls.ListBox listBox,
        T item,
        int requestId,
        Func<int> getLatestRequestId,
        Action? onFinished = null,
        int attempt = 0)
        where T : class
    {
        void Work()
        {
            if (requestId != getLatestRequestId())
            {
                try { onFinished?.Invoke(); } catch { /* ignore */ }
                return;
            }

            listBox.ApplyTemplate();
            var scrollViewer = FindScrollViewer(listBox);
            if (scrollViewer is null || scrollViewer.ViewportHeight <= 0)
            {
                if (attempt < 3)
                {
                    try { onFinished?.Invoke(); } catch { /* ignore */ }
                    listBox.Dispatcher.BeginInvoke(
                        new Action(() => CenterOnItem(listBox, item, requestId, getLatestRequestId, onFinished, attempt + 1)),
                        DispatcherPriority.Render);
                }
                return;
            }

            // Force container generation.
            listBox.ScrollIntoView(item);
            listBox.UpdateLayout();

            var sv = scrollViewer;
            listBox.Dispatcher.BeginInvoke(
                new Action(() => PerformCentering(listBox, item, requestId, getLatestRequestId, sv, onFinished)),
                DispatcherPriority.Render);
        }

        if (!listBox.Dispatcher.CheckAccess())
            listBox.Dispatcher.BeginInvoke(Work, DispatcherPriority.ContextIdle);
        else
            Work();
    }

    public static void ScrollIndexNearTop(
        System.Windows.Controls.ListBox listBox,
        int index,
        double topPaddingPx = 18,
        int attempt = 0)
    {
        void Work()
        {
            try
            {
                if (index < 0 || index >= listBox.Items.Count)
                    return;

                listBox.ApplyTemplate();
                var scrollViewer = FindScrollViewer(listBox);
                if (scrollViewer is null || scrollViewer.ViewportHeight <= 0)
                {
                    if (attempt < 3)
                    {
                        listBox.Dispatcher.BeginInvoke(
                            new Action(() => ScrollIndexNearTop(listBox, index, topPaddingPx, attempt + 1)),
                            DispatcherPriority.Render);
                    }
                    return;
                }

                // Force container generation.
                var item = listBox.Items[index];
                listBox.ScrollIntoView(item);
                listBox.UpdateLayout();

                var sv = scrollViewer;
                listBox.Dispatcher.BeginInvoke(
                    new Action(() =>
                    {
                        try
                        {
                            // Index-based resolution is robust even when ItemsSource has duplicate values (lyrics lines).
                            if (listBox.ItemContainerGenerator.ContainerFromIndex(index) is not FrameworkElement container)
                                return;

                            const double minPadding = 0;
                            var pad = Math.Max(minPadding, topPaddingPx);

                            var pt = container.TransformToAncestor(sv).Transform(new System.Windows.Point(0, 0));
                            var itemTopYInViewport = pt.Y;
                            var currentOffset = sv.VerticalOffset;
                            var itemTopYInContent = currentOffset + itemTopYInViewport;
                            var desiredOffset = itemTopYInContent - pad;

                            desiredOffset = Math.Max(0, Math.Min(desiredOffset, sv.ScrollableHeight));
                            sv.ScrollToVerticalOffset(desiredOffset);
                        }
                        catch { /* ignore */ }
                    }),
                    DispatcherPriority.Render);
            }
            catch { /* ignore */ }
        }

        if (!listBox.Dispatcher.CheckAccess())
            listBox.Dispatcher.BeginInvoke(Work, DispatcherPriority.ContextIdle);
        else
            Work();
    }

    private static void PerformCentering<T>(
        System.Windows.Controls.ListBox listBox,
        T item,
        int requestId,
        Func<int> getLatestRequestId,
        ScrollViewer scrollViewer,
        Action? onFinished)
        where T : class
    {
        try
        {
            if (requestId != getLatestRequestId())
            {
                try { onFinished?.Invoke(); } catch { /* ignore */ }
                return;
            }

            listBox.ApplyTemplate();

            // Virtualization-safe centering: use index + extent math rather than visual transforms.
            // (TransformToAncestor can fail / misbehave when containers are virtualized/recycled.)
            var index = listBox.Items.IndexOf(item);
            if (index < 0)
            {
                try { onFinished?.Invoke(); } catch { /* ignore */ }
                return;
            }

            var totalItems = (double)listBox.Items.Count;
            if (totalItems <= 0)
            {
                try { onFinished?.Invoke(); } catch { /* ignore */ }
                return;
            }

            var extentH = scrollViewer.ExtentHeight;
            var viewportH = scrollViewer.ViewportHeight;
            if (extentH <= 0 || viewportH <= 0)
            {
                try { onFinished?.Invoke(); } catch { /* ignore */ }
                return;
            }

            var avgItemH = extentH / totalItems;
            var target = (index * avgItemH) + (avgItemH / 2.0) - (viewportH / 2.0);
            var maxOffset = Math.Max(0, extentH - viewportH);
            target = Math.Max(0, Math.Min(target, maxOffset));

            scrollViewer.ScrollToVerticalOffset(target);
        }
        catch { /* ignore */ }
        finally
        {
            try { onFinished?.Invoke(); } catch { /* ignore */ }
        }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        try
        {
            if (root is ScrollViewer sv)
                return sv;

            var n = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < n; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                var found = FindScrollViewer(child);
                if (found is not null)
                    return found;
            }
        }
        catch { /* ignore */ }
        return null;
    }
}

