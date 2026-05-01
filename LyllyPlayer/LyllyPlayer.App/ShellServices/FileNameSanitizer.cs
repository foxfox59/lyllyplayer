using System.IO;

namespace LyllyPlayer.ShellServices;

public static class FileNameSanitizer
{
    public static string MakeSafeFileName(string? input, string fallback = "playlist", int maxLen = 80)
    {
        var s = (input ?? "").Trim();
        if (string.IsNullOrWhiteSpace(s))
            s = fallback;

        foreach (var c in Path.GetInvalidFileNameChars())
            s = s.Replace(c, '_');

        s = s.Replace(':', '_');
        s = s.Replace('/', '_').Replace('\\', '_');
        s = s.Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ');

        while (s.Contains("  "))
            s = s.Replace("  ", " ");

        s = s.Trim();
        if (s.Length > maxLen)
            s = s.Substring(0, maxLen).Trim();

        return string.IsNullOrWhiteSpace(s) ? fallback : s;
    }
}

