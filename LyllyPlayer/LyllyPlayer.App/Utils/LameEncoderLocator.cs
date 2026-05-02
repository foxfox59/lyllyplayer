using System.IO;
using System.Runtime.InteropServices;

namespace LyllyPlayer.Utils;

/// <summary>Resolve libmp3lame (bundled next to the app or explicit path). NAudio.Lame loads by bitness.</summary>
public static class LameEncoderLocator
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectory(string? lpPathName);

    /// <summary>Returns true when LAME can load: bundled DLL or valid user path.</summary>
    public static bool TryResolve(string? userOverridePath, out string? directoryForLoad)
    {
        directoryForLoad = null;

        if (!string.IsNullOrWhiteSpace(userOverridePath))
        {
            try
            {
                var full = Path.GetFullPath(userOverridePath.Trim());
                if (File.Exists(full))
                {
                    directoryForLoad = Path.GetDirectoryName(full);
                    return !string.IsNullOrEmpty(directoryForLoad);
                }
            }
            catch
            {
                return false;
            }

            return false;
        }

        try
        {
            var baseDir = AppContext.BaseDirectory;
            var name = Environment.Is64BitProcess ? "libmp3lame.64.dll" : "libmp3lame.32.dll";
            var bundled = Path.Combine(baseDir, name);
            if (File.Exists(bundled))
            {
                directoryForLoad = baseDir;
                return true;
            }
        }
        catch
        {
            // ignore
        }

        return false;
    }

    public static void ApplyLoadDirectory(string? directory)
    {
        try
        {
            SetDllDirectory(string.IsNullOrWhiteSpace(directory) ? null : directory);
        }
        catch
        {
            // ignore
        }
    }
}
