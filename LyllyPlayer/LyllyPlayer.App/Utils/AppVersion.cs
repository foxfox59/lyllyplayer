using System.Reflection;

namespace LyllyPlayer.Utils;

/// <summary>Human-readable app version string (same value persisted as <c>LastSavedByAppVersion</c> in settings.json).</summary>
public static class AppVersion
{
    public static string Current { get; } = Resolve();

    private static string Resolve()
    {
        var asm = Assembly.GetEntryAssembly() ?? typeof(AppVersion).Assembly;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            var plus = info.IndexOf('+', StringComparison.Ordinal);
            return (plus >= 0 ? info[..plus] : info).Trim();
        }

        return asm.GetName().Version?.ToString(3) ?? "unknown";
    }
}
