namespace LyllyPlayer.Updates;

public sealed record AppReleaseOffer(
    string Version,
    string TagName,
    string DownloadUrl,
    string ReleasePageUrl,
    string RuntimeId,
    string AssetName);
