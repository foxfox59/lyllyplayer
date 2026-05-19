namespace LyllyPlayer.Updates;

public static class AppVersionComparer
{
    public static bool TryParseVersion(string? text, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        var t = (text ?? "").Trim();
        if (t.Length == 0)
            return false;
        if (t.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            t = t[1..].Trim();
        var plus = t.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0)
            t = t[..plus].Trim();
        var dash = t.IndexOf('-', StringComparison.Ordinal);
        if (dash >= 0)
            t = t[..dash].Trim();

        var parts = t.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            return false;

        var nums = new int[Math.Min(4, parts.Length)];
        for (var i = 0; i < nums.Length; i++)
        {
            if (!int.TryParse(parts[i], out nums[i]))
                return false;
        }

        version = nums.Length switch
        {
            1 => new Version(nums[0], 0),
            2 => new Version(nums[0], nums[1]),
            3 => new Version(nums[0], nums[1], nums[2]),
            _ => new Version(nums[0], nums[1], nums[2], nums[3]),
        };
        return true;
    }

    public static int Compare(string? installed, string? latest)
    {
        if (!TryParseVersion(installed, out var a))
            a = new Version(0, 0);
        if (!TryParseVersion(latest, out var b))
            b = new Version(0, 0);
        return a.CompareTo(b);
    }

    public static bool IsNewer(string? latest, string? installed)
        => Compare(installed, latest) < 0;
}
