using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace LyllyPlayer.Updates;

public sealed class GitHubAppReleaseClient
{
    private readonly HttpClient _http;

    public GitHubAppReleaseClient(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
            _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("LyllyPlayer", "1.0"));
        if (_http.DefaultRequestHeaders.Accept.Count == 0)
            _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public static string GetPortableRuntimeId()
    {
        return RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "win-x64",
            Architecture.X86 => "win-x86",
            Architecture.Arm64 => "win-arm64",
            _ => "win-x64",
        };
    }

    public static string GetPortableZipAssetName(string version, string runtimeId)
        => $"{AppUpdateConstants.PortableZipPrefix}{version}-{runtimeId}.zip";

    public async Task<AppReleaseOffer?> TryGetLatestPortableOfferAsync(
        string? runtimeId = null,
        CancellationToken cancellationToken = default)
    {
        runtimeId ??= GetPortableRuntimeId();
        using var resp = await _http
            .GetAsync(AppUpdateConstants.LatestReleaseApiUrl, cancellationToken)
            .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = doc.RootElement;

        var tag = root.TryGetProperty("tag_name", out var tagEl) ? (tagEl.GetString() ?? "") : "";
        if (!AppVersionComparer.TryParseVersion(tag, out var version))
            return null;

        var versionText = version.ToString(3);
        var assetName = GetPortableZipAssetName(versionText, runtimeId);
        string? downloadUrl = null;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var nameEl) ? (nameEl.GetString() ?? "") : "";
                if (!string.Equals(name, assetName, StringComparison.OrdinalIgnoreCase))
                    continue;
                downloadUrl = asset.TryGetProperty("browser_download_url", out var urlEl)
                    ? urlEl.GetString()
                    : null;
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(downloadUrl))
            return null;

        var pageUrl = root.TryGetProperty("html_url", out var htmlEl) ? (htmlEl.GetString() ?? "") : "";
        return new AppReleaseOffer(
            Version: versionText,
            TagName: tag,
            DownloadUrl: downloadUrl,
            ReleasePageUrl: pageUrl,
            RuntimeId: runtimeId,
            AssetName: assetName);
    }
}
