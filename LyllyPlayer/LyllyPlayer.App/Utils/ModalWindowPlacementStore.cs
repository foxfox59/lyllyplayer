using System.IO;
using System.Text.Json;
using System.Windows;

namespace LyllyPlayer.Utils;

/// <summary>
/// Persists last screen position for small modal/tool windows (separate from main settings to avoid merge races).
/// </summary>
public static class ModalWindowPlacementStore
{
    private const string FileName = "modal-window-placements.json";

    private sealed class Entry
    {
        public double Left { get; set; }
        public double Top { get; set; }
    }

    private sealed class Root
    {
        public Dictionary<string, Entry> Placements { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        MaxDepth = SafeJson.MaxDepth,
    };

    private static readonly JsonSerializerOptions ReadOptions = SafeJson.CreateDeserializerOptions();

    private static string GetPath()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LyllyPlayer",
            FileName);

    private static Root LoadAll()
    {
        try
        {
            var p = GetPath();
            if (!File.Exists(p))
                return new Root();
            var json = SafeJson.ReadUtf8TextForJson(p, SafeJson.MaxGeneralAppJsonFileBytes);
            return JsonSerializer.Deserialize<Root>(json, ReadOptions) ?? new Root();
        }
        catch
        {
            return new Root();
        }
    }

    private static void SaveAll(Root root)
    {
        try
        {
            var path = GetPath();
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(root, JsonOptions));
        }
        catch
        {
            // ignore
        }
    }

    public static void Restore(Window window, string key)
    {
        if (window is null || string.IsNullOrWhiteSpace(key))
            return;

        var root = LoadAll();
        if (!root.Placements.TryGetValue(key, out var e))
            return;

        try
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            var w = window.Width;
            var h = window.Height;
            if (w <= 0 || double.IsNaN(w))
                w = window.MinWidth > 0 ? window.MinWidth : 400;
            if (h <= 0 || double.IsNaN(h))
                h = window.MinHeight > 0 ? window.MinHeight : 300;

            var vsLeft = SystemParameters.VirtualScreenLeft;
            var vsTop = SystemParameters.VirtualScreenTop;
            var vsRight = vsLeft + SystemParameters.VirtualScreenWidth;
            var vsBottom = vsTop + SystemParameters.VirtualScreenHeight;
            const double minVisible = 32;
            var left = Math.Min(Math.Max(e.Left, vsLeft - w + minVisible), vsRight - minVisible);
            var top = Math.Min(Math.Max(e.Top, vsTop - h + minVisible), vsBottom - minVisible);
            window.Left = left;
            window.Top = top;
        }
        catch
        {
            // ignore
        }
    }

    public static void Persist(Window window, string key)
    {
        if (window is null || string.IsNullOrWhiteSpace(key))
            return;

        try
        {
            if (window.WindowState != WindowState.Normal)
                return;

            var root = LoadAll();
            root.Placements[key] = new Entry { Left = window.Left, Top = window.Top };
            SaveAll(root);
        }
        catch
        {
            // ignore
        }
    }
}
