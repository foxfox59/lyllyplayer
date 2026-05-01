using LyllyPlayer.Settings;

namespace LyllyPlayer.ShellServices;

/// <summary>
/// Composition-friendly façade over <see cref="SettingsStore"/> for load/save.
/// Debounced disk writes remain coordinated by the main window persist timer.
/// </summary>
public sealed class SettingsService
{
    public AppSettings LoadStartup(out SettingsStartupLoadInfo info) => SettingsStore.Load(out info);

    public AppSettings LoadLatest() => SettingsStore.Load();

    public void Save(AppSettings settings) => SettingsStore.Save(settings);

    public string GetSettingsPath() => SettingsStore.GetSettingsPath();
}
