using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace LyllyPlayer.Utils;

public sealed class ExplorerLogicalStringComparer : IComparer<string?>
{
    public static ExplorerLogicalStringComparer Instance { get; } = new();

    private ExplorerLogicalStringComparer() { }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int StrCmpLogicalW(string? psz1, string? psz2);

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;
        try { return StrCmpLogicalW(x, y); }
        catch { return string.Compare(x, y, StringComparison.OrdinalIgnoreCase); }
    }
}

