using System;
using System.IO;
using System.Threading;

namespace LyllyPlayer.Updater;

internal static class UpdateLog
{
    private static readonly object Gate = new();
    private static string? _path;

    public static void Init(string installDir)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LyllyPlayer",
                "updates");
            Directory.CreateDirectory(dir);
            _path = Path.Combine(dir, $"updater-{DateTime.UtcNow:yyyyMMdd-HHmmss}.log");
            Write($"installDir={installDir}");
        }
        catch
        {
            _path = null;
        }
    }

    public static void Write(string message)
    {
        var line = $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC {message}";
        try
        {
            lock (Gate)
            {
                if (_path is not null)
                    File.AppendAllText(_path, line + Environment.NewLine);
            }
        }
        catch { /* ignore */ }

        try { Console.WriteLine(line); } catch { /* ignore */ }
    }

    public static void Exception(Exception ex, string context)
        => Write($"{context}: {ex.GetType().Name}: {ex.Message}");
}
