using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
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
                text = TryReadStringLikeData(data.GetData("UniformResourceLocatorW")) ?? "";
            else if (data.GetDataPresent("UniformResourceLocator"))
                text = TryReadStringLikeData(data.GetData("UniformResourceLocator")) ?? "";
            else if (data.GetDataPresent(System.Windows.DataFormats.Html) || data.GetDataPresent("HTML Format"))
            {
                var htmlRaw = data.GetData(System.Windows.DataFormats.Html) ?? data.GetData("HTML Format");
                var html = TryReadStringLikeData(htmlRaw) ?? "";
                text = ExtractUrlFromHtmlBestEffort(html) ?? "";
            }

            text = (text ?? "").Trim();
            return !string.IsNullOrWhiteSpace(text);
        }
        catch
        {
            return false;
        }
    }

    private static string? TryReadStringLikeData(object? data)
    {
        try
        {
            if (data is null)
                return null;
            if (data is string s)
                return s;
            if (data is byte[] bytes && bytes.Length > 0)
            {
                // URL drops are commonly UTF-16LE null-terminated (UniformResourceLocatorW) or ANSI/UTF8.
                var u16 = Encoding.Unicode.GetString(bytes);
                var trimmedU16 = u16.Trim('\0', '\r', '\n', ' ', '\t');
                if (!string.IsNullOrWhiteSpace(trimmedU16))
                    return trimmedU16;

                var u8 = Encoding.UTF8.GetString(bytes);
                var trimmedU8 = u8.Trim('\0', '\r', '\n', ' ', '\t');
                if (!string.IsNullOrWhiteSpace(trimmedU8))
                    return trimmedU8;
            }
        }
        catch { /* ignore */ }
        return null;
    }

    private static string? ExtractUrlFromHtmlBestEffort(string html)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(html))
                return null;

            // "HTML Format" payload may include a header; just search for href.
            var m = Regex.Match(html, "href\\s*=\\s*\"(?<u>https?://[^\"]+)\"", RegexOptions.IgnoreCase);
            if (m.Success)
                return m.Groups["u"].Value;

            // Fallback: first http(s) looking token.
            var m2 = Regex.Match(html, "(?<u>https?://\\S+)", RegexOptions.IgnoreCase);
            if (m2.Success)
                return m2.Groups["u"].Value.TrimEnd('"', '\'', '>', ')', ']');
        }
        catch { /* ignore */ }
        return null;
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

