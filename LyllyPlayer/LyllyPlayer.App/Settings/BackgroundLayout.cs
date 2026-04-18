namespace LyllyPlayer.Settings;

public readonly record struct RectN(double X, double Y, double W, double H)
{
    public static RectN Full => new(0, 0, 1, 1);
}

