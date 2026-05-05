using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace LyllyPlayer.Utils;

public static class PlaylistDragDropHelper
{
    public sealed record DropPayload(
        IReadOnlyList<string> LocalPaths,
        IReadOnlyList<string> Urls);

    public static bool CanAccept(System.Windows.IDataObject? data)
    {
        try
        {
            if (data is null)
                return false;
            if (data.GetDataPresent(System.Windows.DataFormats.FileDrop))
                return true;
            if (TryGetDroppedText(data, out var t))
                return ParseUrlsFromText(t).Count > 0;
        }
        catch { /* ignore */ }
        return false;
    }

    public static DropPayload ExtractBestEffort(System.Windows.IDataObject? data)
    {
        var local = new List<string>();
        var urls = new List<string>();
        try
        {
            if (data is null)
                return new DropPayload(local, urls);

            if (TryGetDroppedFilePaths(data, out var paths))
                local.AddRange(paths);

            if (TryGetDroppedText(data, out var text))
                urls.AddRange(ParseUrlsFromText(text));
        }
        catch
        {
            // ignore
        }

        return new DropPayload(
            LocalPaths: local.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Urls: urls.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    private static bool TryGetDroppedFilePaths(System.Windows.IDataObject data, out List<string> paths)
    {
        paths = new List<string>();
        try
        {
            if (!data.GetDataPresent(System.Windows.DataFormats.FileDrop))
                return false;
            if (data.GetData(System.Windows.DataFormats.FileDrop) is not string[] raw || raw.Length == 0)
                return false;
            foreach (var p in raw)
            {
                var t = (p ?? "").Trim();
                if (!string.IsNullOrWhiteSpace(t))
                    paths.Add(t);
            }
            return paths.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetDroppedText(System.Windows.IDataObject data, out string text)
    {
        text = "";
        try
        {
            // Browsers vary: some expose plain text, others expose URL formats.
            if (data.GetDataPresent(System.Windows.DataFormats.UnicodeText))
                text = (data.GetData(System.Windows.DataFormats.UnicodeText) as string) ?? "";
            else if (data.GetDataPresent(System.Windows.DataFormats.Text))
                text = (data.GetData(System.Windows.DataFormats.Text) as string) ?? "";
            else if (data.GetDataPresent("UniformResourceLocatorW"))
                text = (data.GetData("UniformResourceLocatorW") as string) ?? "";
            else if (data.GetDataPresent("UniformResourceLocator"))
                text = (data.GetData("UniformResourceLocator") as string) ?? "";

            text = (text ?? "").Trim();
            return !string.IsNullOrWhiteSpace(text);
        }
        catch
        {
            return false;
        }
    }

    private static List<string> ParseUrlsFromText(string raw)
    {
        var urls = new List<string>();
        try
        {
            var t = (raw ?? "").Trim();
            if (string.IsNullOrWhiteSpace(t))
                return urls;

            // Split on whitespace; browsers sometimes provide "Title\nURL".
            var parts = t.Split(new[] { '\r', '\n', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var p in parts)
            {
                var s = p.Trim();
                if (string.IsNullOrWhiteSpace(s))
                    continue;
                if (Uri.TryCreate(s, UriKind.Absolute, out var u) &&
                    (u.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) ||
                     u.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)))
                    urls.Add(u.ToString());
            }
        }
        catch
        {
            // ignore
        }

        return urls;
    }
}

