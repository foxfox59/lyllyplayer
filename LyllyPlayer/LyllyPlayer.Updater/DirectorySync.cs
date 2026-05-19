namespace LyllyPlayer.Updater;

internal static class DirectorySync
{
    public static void CopyDirectory(string sourceDir, string destDir, IReadOnlySet<string>? skipFileNames = null)
    {
        sourceDir = Path.GetFullPath(sourceDir);
        destDir = Path.GetFullPath(destDir);
        Directory.CreateDirectory(destDir);

        foreach (var srcPath in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(sourceDir, srcPath);
            var fileName = Path.GetFileName(srcPath);
            if (skipFileNames is not null && skipFileNames.Contains(fileName))
                continue;

            var destPath = Path.Combine(destDir, rel);
            var parent = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(parent))
                Directory.CreateDirectory(parent);
            File.Copy(srcPath, destPath, overwrite: true);
        }
    }

    public static void ClearDirectory(string dir, IReadOnlySet<string>? keepFileNames = null)
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
}
