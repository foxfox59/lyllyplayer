namespace LyllyPlayer.Settings;

/// <summary>
/// Normalized rectangle in image-space.
/// X/Y/W/H are fractions of the source bitmap width/height.
/// </summary>
public readonly record struct RectN(double X, double Y, double W, double H)
{
    public static RectN Full => new(0, 0, 1, 1);
}

