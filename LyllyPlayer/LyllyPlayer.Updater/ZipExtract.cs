using System;
using System.IO;
using System.IO.Compression;

namespace LyllyPlayer.Updater;

internal static class ZipExtract
{
    /// <summary>
    /// Extract all zip entries with normalized path separators.
    /// PowerShell <c>Compress-Archive</c> often stores backslashes in entry names; naive
    /// <see cref="ZipFile.ExtractToDirectory"/> can flatten or drop nested folders on .NET Framework.
    /// </summary>
    public static int ExtractToDirectory(string zipPath, string destDir)
    {
        Directory.CreateDirectory(destDir);
        var destRoot = Path.GetFullPath(AppendDirectorySeparator(destDir));
        var count = 0;

        using (var archive = ZipFile.OpenRead(zipPath))
        {
            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.FullName))
                    continue;

                var normalized = entry.FullName.Replace('\\', '/').TrimStart('/');
                if (string.IsNullOrEmpty(normalized))
                    continue;

                if (normalized.EndsWith("/", StringComparison.Ordinal))
                {
                    var dirOnly = normalized.TrimEnd('/');
                    if (dirOnly.Length == 0)
                        continue;
                    Directory.CreateDirectory(CombineUnderRoot(destRoot, dirOnly));
                    continue;
                }

                var destPath = CombineUnderRoot(destRoot, normalized);
                var parent = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrEmpty(parent))
                    Directory.CreateDirectory(parent);

                entry.ExtractToFile(destPath, overwrite: true);
                count++;
            }
        }

        return count;
    }

    private static string CombineUnderRoot(string destRoot, string relativePosixPath)
    {
        var parts = relativePosixPath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
        var destPath = destRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        foreach (var part in parts)
        {
            if (part == "." || part == "..")
                throw new InvalidDataException($"Unsafe zip entry path: {relativePosixPath}");
            destPath = Path.Combine(destPath, part);
        }

        destPath = Path.GetFullPath(destPath);
        if (!destPath.StartsWith(destRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Zip entry escapes destination: {relativePosixPath}");

        return destPath;
    }

    private static string AppendDirectorySeparator(string path)
    {
        if (!path.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
            && !path.EndsWith(Path.AltDirectorySeparatorChar.ToString(), StringComparison.Ordinal))
            return path + Path.DirectorySeparatorChar;
        return path;
    }
}
