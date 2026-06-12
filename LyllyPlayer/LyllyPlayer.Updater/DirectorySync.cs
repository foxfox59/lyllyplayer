using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace LyllyPlayer.Updater;

internal static class DirectorySync
{
    public static int CopyDirectory(string sourceDir, string destDir, ICollection<string>? skipFileNames = null)
    {
        sourceDir = Path.GetFullPath(sourceDir);
        destDir = Path.GetFullPath(destDir);
        Directory.CreateDirectory(destDir);

        var copied = 0;
        foreach (var srcPath in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var fileName = Path.GetFileName(srcPath);
            if (skipFileNames is not null && skipFileNames.Contains(fileName))
                continue;

            var rel = GetRelativePath(sourceDir, srcPath);
            var destPath = Path.Combine(destDir, rel);
            var parent = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(parent))
                Directory.CreateDirectory(parent);
            File.Copy(srcPath, destPath, overwrite: true);
            copied++;
        }

        return copied;
    }

    public static int CountFiles(string dir)
    {
        if (!Directory.Exists(dir))
            return 0;
        return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Count();
    }

    public static void ClearDirectory(string dir, ICollection<string>? keepFileNames = null)
    {
        dir = Path.GetFullPath(dir);
        if (!Directory.Exists(dir))
            return;

        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(file);
            if (keepFileNames is not null && keepFileNames.Contains(name))
                continue;
            try { File.Delete(file); } catch { /* ignore */ }
        }

        foreach (var sub in Directory.EnumerateDirectories(dir, "*", SearchOption.TopDirectoryOnly))
        {
            try { Directory.Delete(sub, recursive: true); } catch { /* ignore */ }
        }
    }

    public static long GetDirectorySizeBytes(string dir)
    {
        if (!Directory.Exists(dir))
            return 0;
        long total = 0;
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            try { total += new FileInfo(file).Length; } catch { /* ignore */ }
        }
        return total;
    }

    private static string GetRelativePath(string relativeTo, string path)
    {
        var root = Path.GetFullPath(relativeTo.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var full = Path.GetFullPath(path);
        var prefix = root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new IOException($"Path is not under source root: {path}");

        return full.Substring(prefix.Length);
    }
}
