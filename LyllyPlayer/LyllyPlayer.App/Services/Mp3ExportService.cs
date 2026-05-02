using System.IO;
using LyllyPlayer.Utils;
using NAudio.Lame;
using NAudio.Wave;

namespace LyllyPlayer.Services;

public enum Mp3ExportEncodingMode
{
    Vbr,
    Cbr,
}

public readonly record struct Mp3ExportRequest(
    Mp3ExportEncodingMode Mode,
    int CbrSliderIndex,
    int VbrSliderIndex);

/// <summary>Transcode a cached media file to MP3 via LAME (NAudio.Lame).</summary>
public static class Mp3ExportService
{
    public static void ExportFileToMp3(
        string sourcePath,
        string destPath,
        string title,
        string? artist,
        Mp3ExportRequest request,
        string? lameUserOverridePath)
    {
        if (!LameEncoderLocator.TryResolve(lameUserOverridePath, out var lameDir))
            throw new InvalidOperationException("LAME (libmp3lame) was not found. Install the bundled DLL next to the app or set a path under Options → Export.");

        LameEncoderLocator.ApplyLoadDirectory(lameDir);
        try
        {
            var id3 = new ID3TagData
            {
                Title = string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(destPath) : title.Trim(),
                Artist = string.IsNullOrWhiteSpace(artist) ? null : artist.Trim(),
            };

            LameConfig config = request.Mode switch
            {
                Mp3ExportEncodingMode.Cbr => new LameConfig
                {
                    BitRate = Mp3QualityMaps.CbrKbps[Mp3QualityMaps.ClampSlider(request.CbrSliderIndex)],
                    ID3 = id3,
                },
                _ => BuildVbrConfig(Mp3QualityMaps.ClampSlider(request.VbrSliderIndex), id3),
            };

            using var reader = new AudioFileReader(sourcePath);
            using var writer = new LameMP3FileWriter(destPath, reader.WaveFormat, config);
            var buffer = new byte[reader.WaveFormat.AverageBytesPerSecond];
            int read;
            while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
                writer.Write(buffer, 0, read);
            writer.Flush();
        }
        finally
        {
            LameEncoderLocator.ApplyLoadDirectory(null);
        }
    }

    private static LameConfig BuildVbrConfig(int sliderIndex, ID3TagData id3)
    {
        var v = Mp3QualityMaps.SliderToNegatedV(sliderIndex);
        if (!Enum.TryParse<LAMEPreset>("V" + v, ignoreCase: true, out var preset))
            preset = LAMEPreset.STANDARD;

        return new LameConfig
        {
            Preset = preset,
            ID3 = id3,
        };
    }
}
