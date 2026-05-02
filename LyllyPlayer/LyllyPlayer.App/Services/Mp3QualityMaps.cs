namespace LyllyPlayer.Services;

/// <summary>Maps unified quality slider (0–9) to CBR bitrates and VBR presets (-V9 … -V0).</summary>
public static class Mp3QualityMaps
{
    /// <summary>CBR kb/s per slider index (left = smaller file).</summary>
    public static readonly int[] CbrKbps =
    {
        64, 80, 96, 112, 128, 160, 192, 224, 256, 320,
    };

    public const int DefaultCbrSliderIndex = 6;
    public const int DefaultVbrSliderIndex = 7;

    public static int ClampSlider(int index) => Math.Clamp(index, 0, CbrKbps.Length - 1);

    /// <summary>LAME -V index (0 = best … 9 = worst).</summary>
    public static int SliderToNegatedV(int sliderIndex)
    {
        var k = ClampSlider(sliderIndex);
        return 9 - k;
    }

    public static string DescribeCbr(int sliderIndex)
    {
        var kbps = CbrKbps[ClampSlider(sliderIndex)];
        return $"{kbps} kb/s constant bitrate";
    }

    public static string DescribeVbr(int sliderIndex)
    {
        var v = SliderToNegatedV(sliderIndex);
        return v switch
        {
            0 => "-V0 (~245 kb/s typical)",
            1 => "-V1 (~225 kb/s typical)",
            2 => "-V2 (~190 kb/s typical)",
            3 => "-V3 (~175 kb/s typical)",
            4 => "-V4 (~165 kb/s typical)",
            5 => "-V5 (~130 kb/s typical)",
            6 => "-V6 (~115 kb/s typical)",
            7 => "-V7 (~100 kb/s typical)",
            8 => "-V8 (~85 kb/s typical)",
            _ => "-V9 (~65 kb/s typical)",
        };
    }
}
