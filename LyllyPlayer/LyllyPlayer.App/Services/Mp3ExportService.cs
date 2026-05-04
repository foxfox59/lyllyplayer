using System.IO;
using LyllyPlayer.Utils;
using NAudio.Lame;
using NAudio.Wave;
using System;

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

            // NAudio.Lame's built-in tag writer can result in lossy tags for non-Latin text (and/or leave an ID3v1 tag
            // that some players prioritize, showing "????"). Rewrite tags with TagLib# as Unicode ID3v2 and remove ID3v1.
            TryRewriteTagsUnicodeBestEffort(destPath, id3.Title, id3.Artist);
        }
        finally
        {
            LameEncoderLocator.ApplyLoadDirectory(null);
        }
    }

    private static void TryRewriteTagsUnicodeBestEffort(string destPath, string? title, string? artist)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(destPath) || !File.Exists(destPath))
                return;

            using var f = TagLib.File.Create(destPath);

            // Ensure an ID3v2 tag exists (Unicode-capable) and drop ID3v1 (limited charset).
            try { _ = f.GetTag(TagLib.TagTypes.Id3v2, create: true); } catch { /* ignore */ }
            try { f.RemoveTags(TagLib.TagTypes.Id3v1); } catch { /* ignore */ }

            // Prefer writing to the generic Tag surface; TagLib# will serialize appropriately for the container.
            try
            {
                var t = (title ?? "").Trim();
                f.Tag.Title = string.IsNullOrWhiteSpace(t) ? null : t;
            }
            catch { /* ignore */ }

            try
            {
                var a = (artist ?? "").Trim();
                if (string.IsNullOrWhiteSpace(a))
                {
                    f.Tag.Performers = Array.Empty<string>();
                    f.Tag.AlbumArtists = Array.Empty<string>();
                }
                else
                {
                    f.Tag.Performers = new[] { a };
                    f.Tag.AlbumArtists = new[] { a };
                }
            }
            catch { /* ignore */ }

            // Best effort: if this TagLib build exposes ID3v2 defaults, force Unicode-friendly settings.
            // Use reflection so we don't couple to a specific TagLib# surface.
            try
            {
                var tagType = Type.GetType("TagLib.Id3v2.Tag, taglib-sharp", throwOnError: false);
                if (tagType is not null)
                {
                    static void TrySetStatic(Type t, string prop, object? value)
                    {
                        try
                        {
                            var p = t.GetProperty(prop, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                            if (p is not null && p.CanWrite)
                                p.SetValue(null, value);
                        }
                        catch { /* ignore */ }
                    }

                    // Prefer ID3v2.4 + UTF-8/UTF-16; either is fine for JP/RU. (Property names vary by TagLib version.)
                    TrySetStatic(tagType, "DefaultVersion", (byte)4);
                    TrySetStatic(tagType, "ForceDefaultVersion", true);
                    TrySetStatic(tagType, "DefaultEncoding", Enum.Parse(Type.GetType("TagLib.StringType, taglib-sharp") ?? typeof(object), "UTF16", ignoreCase: true));
                    TrySetStatic(tagType, "ForceDefaultEncoding", true);
                }
            }
            catch { /* ignore */ }

            try { f.Save(); } catch { /* ignore */ }
        }
        catch { /* ignore */ }
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
