using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LyllyPlayer.Utils;

/// <summary>
/// Shared limits for JSON from disk or subprocess output. System.Text.Json does not execute code from JSON
/// (no script hooks); these guards reduce denial-of-service from huge files or extreme nesting.
/// </summary>
public static class SafeJson
{
    /// <summary>Maximum object/array nesting depth for deserialize and <see cref="JsonDocument"/>.</summary>
    public const int MaxDepth = 64;

    /// <summary>User-saved playlist files (<c>*.json</c>).</summary>
    public const int MaxPlaylistFileBytes = 32 * 1024 * 1024;

    /// <summary>Theme import from Options.</summary>
    public const int MaxThemeImportFileBytes = 512 * 1024;

    /// <summary><c>settings.json</c> and similar small app config.</summary>
    public const int MaxAppSettingsFileBytes = 8 * 1024 * 1024;

    /// <summary>Cache index, playlist cache, modal placements, last-playlist snapshot, etc.</summary>
    public const int MaxGeneralAppJsonFileBytes = 64 * 1024 * 1024;

    public static JsonSerializerOptions CreateDeserializerOptions(bool writeIndented = false) =>
        new()
        {
            WriteIndented = writeIndented,
            MaxDepth = MaxDepth,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
        };

    public static JsonDocumentOptions CreateDocumentOptions() =>
        new()
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
            MaxDepth = MaxDepth,
        };

    /// <summary>Reads UTF-8 text after verifying the file is not larger than <paramref name="maxBytes"/>.</summary>
    /// <exception cref="FileNotFoundException">File does not exist.</exception>
    /// <exception cref="InvalidDataException">File exceeds size limit.</exception>
    public static string ReadUtf8TextForJson(string path, int maxBytes)
    {
        var fi = new FileInfo(path);
        if (!fi.Exists)
            throw new FileNotFoundException("JSON file not found.", path);

        if (fi.Length > maxBytes)
        {
            throw new InvalidDataException(
                $"The file is too large to load as JSON (max {maxBytes / (1024 * 1024)} MB).");
        }

        return File.ReadAllText(path, Encoding.UTF8);
    }
}
