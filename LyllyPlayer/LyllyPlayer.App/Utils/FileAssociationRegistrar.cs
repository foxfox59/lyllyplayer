using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace LyllyPlayer.Utils;

public static class FileAssociationRegistrar
{
    // shell32 SHChangeNotify to refresh Explorer icons/associations
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

    private const uint SHCNE_ASSOCCHANGED = 0x08000000;
    private const uint SHCNF_IDLIST = 0x0000;

    public static bool IsAssociatedWithThisAppPerUser(string extension, string progId)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{extension}", writable: false);
            var cur = (key?.GetValue(null) as string ?? "").Trim();
            return string.Equals(cur, progId, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public static void RegisterPerUser(string extension, string progId, string description)
    {
        if (string.IsNullOrWhiteSpace(extension) || !extension.StartsWith("."))
            throw new ArgumentException("Extension must start with '.'", nameof(extension));
        if (string.IsNullOrWhiteSpace(progId))
            throw new ArgumentException("ProgID is required.", nameof(progId));

        var exe = GetCurrentExePathBestEffort();
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
            throw new InvalidOperationException("Could not resolve app executable path.");

        // Extension -> ProgID
        using (var extKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{extension}"))
        {
            extKey?.SetValue(null, progId);
        }

        // ProgID metadata
        using (var pidKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{progId}"))
        {
            pidKey?.SetValue(null, description);
        }

        // ProgID icon
        using (var iconKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{progId}\DefaultIcon"))
        {
            iconKey?.SetValue(null, $"\"{exe}\",0");
        }

        // ProgID open command
        using (var cmdKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{progId}\shell\open\command"))
        {
            cmdKey?.SetValue(null, $"\"{exe}\" \"%1\"");
        }

        RefreshShellAssociations();
    }

    public static void UnregisterPerUser(string extension, string progId)
    {
        try
        {
            // Delete ProgID subtree first (safe even if missing)
            try { Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{progId}", throwOnMissingSubKey: false); }
            catch { /* ignore */ }

            // Remove extension mapping if it points to our progId
            try
            {
                using var extKey = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{extension}", writable: true);
                if (extKey is not null)
                {
                    var cur = (extKey.GetValue(null) as string ?? "").Trim();
                    if (string.Equals(cur, progId, StringComparison.OrdinalIgnoreCase))
                        extKey.DeleteValue("", throwOnMissingValue: false);
                }
            }
            catch { /* ignore */ }

            // If extension key is now empty, delete it
            try
            {
                using var extKey2 = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{extension}", writable: false);
                if (extKey2 is not null && extKey2.ValueCount == 0 && extKey2.SubKeyCount == 0)
                    Registry.CurrentUser.DeleteSubKey($@"Software\Classes\{extension}", throwOnMissingSubKey: false);
            }
            catch { /* ignore */ }
        }
        finally
        {
            RefreshShellAssociations();
        }
    }

    private static string GetCurrentExePathBestEffort()
    {
        try
        {
            var p = Process.GetCurrentProcess();
            var s = p.MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(s))
                return s;
        }
        catch { /* ignore */ }

        try
        {
            var s = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(s))
                return s;
        }
        catch { /* ignore */ }

        try
        {
            var s = AppContext.BaseDirectory;
            if (!string.IsNullOrWhiteSpace(s))
                return Path.Combine(s, "LyllyPlayer.exe");
        }
        catch { /* ignore */ }

        return "";
    }

    private static void RefreshShellAssociations()
    {
        try { SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero); } catch { /* ignore */ }
    }
}

