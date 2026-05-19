namespace LyllyPlayer.Updates;

public static class AppUpdateConstants
{
    public const string GitHubOwner = "foxfox59";
    public const string GitHubRepo = "lyllyplayer";
    public const string UpdaterExeName = "LyllyPlayer.Updater.exe";
    public const string PortableZipPrefix = "LyllyPlayer-portable-";

    public static string LatestReleaseApiUrl =>
        $"https://api.github.com/repos/{GitHubOwner}/{GitHubRepo}/releases/latest";
}
