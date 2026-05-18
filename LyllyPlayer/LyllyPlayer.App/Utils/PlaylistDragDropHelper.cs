using System;
using System.Collections.Generic;
using System.IO;
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
            {
                foreach (var p in paths)
                    TryAddNormalizedLocalPath(local, p);
            }

            if (data.GetDataPresent("UniformResourceLocatorW"))
                TryAddNormalizedLocalPath(local, TryReadStringLikeData(data.GetData("UniformResourceLocatorW")));
            if (data.GetDataPresent("UniformResourceLocator"))
                TryAddNormalizedLocalPath(local, TryReadStringLikeData(data.GetData("UniformResourceLocator")));

            if (TryGetDroppedText(data, out var text))
            {
                TryAddNormalizedLocalPath(local, text);
                urls.AddRange(ParseUrlsFromText(text));
            }
        }
        catch
        {
            // ignore
        }

        return new DropPayload(
            LocalPaths: local.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Urls: urls.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    /// <summary>
    /// When Explorer opens a file, drag/drop often includes both <see cref="DropPayload.LocalPaths"/> and a redundant
    /// file:// (or path-like) URL. Feeding those into YouTube import shows "Added 1 items" and can replace the playlist.
    /// </summary>
    public static List<string> FilterYoutubeHttpUrlsOnly(IReadOnlyList<string> urls)
    {
        var acc = new List<string>();
        if (urls is null || urls.Count == 0)
            return acc;

        foreach (var raw in urls)
        {
            var s = (raw ?? "").Trim();
            if (string.IsNullOrWhiteSpace(s))
                continue;

            if (!Uri.TryCreate(s, UriKind.Absolute, out var uri))
                continue;

            if (!uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                continue;

            var host = uri.Host ?? "";
            if (host.Contains("youtube", StringComparison.OrdinalIgnoreCase) ||
                host.Contains("youtu.be", StringComparison.OrdinalIgnoreCase) ||
                host.Contains("music.youtube", StringComparison.OrdinalIgnoreCase))
                acc.Add(s);
        }

        return acc;
    }

    public static bool LooksLikeLocalFilesystemPath(string? raw)
        => TryNormalizeLocalAudioPath(raw) is not null;

    /// <summary>Resolve Explorer / shell arguments to a full local path when possible.</summary>
    public static string? TryNormalizeLocalAudioPath(string? raw)
    {
        try
        {
            var s = (raw ?? "").Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(s))
                return null;

            if (Uri.TryCreate(s, UriKind.Absolute, out var uri) &&
                (uri.Scheme.Equals(Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase) || uri.IsFile))
            {
                var local = uri.LocalPath;
                if (!string.IsNullOrWhiteSpace(local))
                {
                    try { return Path.GetFullPath(local); } catch { return local; }
                }
            }

            if (File.Exists(s))
            {
                try { return Path.GetFullPath(s); } catch { return s; }
            }

            var ext = Path.GetExtension(s);
            if (LocalPlaylistLoader.IsSupportedAudioExtension(ext))
            {
                try { return Path.GetFullPath(s); } catch { return s; }
            }
        }
        catch { /* ignore */ }

        return null;
    }

    private static void TryAddNormalizedLocalPath(List<string> local, string? raw)
    {
        var p = TryNormalizeLocalAudioPath(raw);
        if (!string.IsNullOrWhiteSpace(p))
            local.Add(p);
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
            // Prefer actual URL payloads over "title text" payloads.
            string? candidate = null;

            if (data.GetDataPresent("UniformResourceLocatorW"))
                candidate = TryReadStringLikeData(data.GetData("UniformResourceLocatorW"));
            if (string.IsNullOrWhiteSpace(candidate) && data.GetDataPresent("UniformResourceLocator"))
                candidate = TryReadStringLikeData(data.GetData("UniformResourceLocator"));
            if (string.IsNullOrWhiteSpace(candidate) &&
                (data.GetDataPresent(System.Windows.DataFormats.Html) || data.GetDataPresent("HTML Format")))
            {
                var htmlRaw = data.GetData(System.Windows.DataFormats.Html) ?? data.GetData("HTML Format");
                var html = TryReadStringLikeData(htmlRaw) ?? "";
                candidate = ExtractUrlFromHtmlBestEffort(html);
            }
            if (string.IsNullOrWhiteSpace(candidate) && data.GetDataPresent(System.Windows.DataFormats.UnicodeText))
                candidate = (data.GetData(System.Windows.DataFormats.UnicodeText) as string);
            if (string.IsNullOrWhiteSpace(candidate) && data.GetDataPresent(System.Windows.DataFormats.Text))
                candidate = (data.GetData(System.Windows.DataFormats.Text) as string);

            text = candidate ?? "";

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

