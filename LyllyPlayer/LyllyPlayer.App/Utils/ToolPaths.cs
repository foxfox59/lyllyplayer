using System;
using System.IO;

namespace LyllyPlayer.Utils;

public static class ToolPaths
{
    public static string GetManagedYtDlpPath()
    {
        try
        {
            var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(baseDir, "LyllyPlayer", "tools", "yt-dlp", "yt-dlp.exe");
        }
        catch
        {
            return "yt-dlp.exe";
        }
    }
}

