using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using Syno.TinyTorrent.Shared;
using Syno.TinyTorrent.Shared.Network;

namespace Syno.TinyTorrent.Shell;

/// <summary>
/// Reads a GitHub release, downloads a file it publishes, TinyTorrent's own
/// setup or gozler's, and checks a downloaded file against its SHA-256.
/// </summary>
internal static class Release
{
    internal static async Task<JsonDocument> Latest(
        ProviderHttp http,
        string repository,
        CancellationToken cancellation
    ) =>
        await Read(http, repository, "latest", cancellation)
        ?? throw new HttpRequestException("GitHub has no release.", null, HttpStatusCode.NotFound);

    /// <summary>The release with this tag, or null when GitHub has none.</summary>
    internal static Task<JsonDocument?> Tagged(
        ProviderHttp http,
        string repository,
        string tag,
        CancellationToken cancellation
    ) => Read(http, repository, "tags/" + tag, cancellation);

    private static async Task<JsonDocument?> Read(
        ProviderHttp http,
        string repository,
        string release,
        CancellationToken cancellation
    )
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "https://api.github.com/repos/" + repository + "/releases/" + release
        );
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");
        var reply = await http.Send(request, ProviderHttp.JsonLimit, cancellation);
        if (reply.Status == HttpStatusCode.NotFound)
        {
            return null;
        }
        if (reply.Status != HttpStatusCode.OK)
        {
            throw new HttpRequestException("GitHub refused the release lookup.", null, reply.Status);
        }
        return JsonDocument.Parse(reply.Bytes);
    }

    // The release's version as its tag spells it, without the leading v.
    internal static string? Tag(JsonElement release) =>
        release.TryGetProperty("tag_name", out var tag) && tag.ValueKind == JsonValueKind.String
            ? tag.GetString()?.TrimStart('v', 'V')
            : null;

    internal static Uri? Asset(JsonElement release, string name) =>
        Named(release, name) is { } asset
        && asset.TryGetProperty("browser_download_url", out var url)
        && url.ValueKind == JsonValueKind.String
        && Uri.TryCreate(url.GetString(), UriKind.Absolute, out var location)
            ? location
            : null;

    // The SHA-256 that GitHub computed for the asset when it was uploaded.
    internal static string? Digest(JsonElement release, string name) =>
        Named(release, name) is { } asset
        && asset.TryGetProperty("digest", out var digest)
        && digest.ValueKind == JsonValueKind.String
        && digest.GetString() is { Length: 71 } text
        && text.StartsWith("sha256:", StringComparison.Ordinal)
            ? text[7..]
            : null;

    private static JsonElement? Named(JsonElement release, string name)
    {
        if (
            !release.TryGetProperty("assets", out var assets)
            || assets.ValueKind != JsonValueKind.Array
        )
            return null;
        foreach (var asset in assets.EnumerateArray())
            if (
                asset.TryGetProperty("name", out var assetName)
                && assetName.ValueKind == JsonValueKind.String
                && assetName.GetString() == name
            )
                return asset;
        return null;
    }

    internal static async Task Download(
        ProviderHttp http,
        Uri asset,
        string path,
        Action<WorkProgress> progress,
        CancellationToken cancellation
    )
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            await Locate(http, asset, cancellation)
        );
        await http.Download(request, path, progress, cancellation);
    }

    // GitHub answers a release download with a redirect to the file's
    // storage location, which serves the file.
    internal static async Task<Uri> Locate(
        ProviderHttp http,
        Uri asset,
        CancellationToken cancellation
    )
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, asset);
        var reply = await http.Send(request, ProviderHttp.JsonLimit, cancellation);
        if (
            reply.Status
                is HttpStatusCode.Found
                    or HttpStatusCode.MovedPermanently
                    or HttpStatusCode.TemporaryRedirect
                    or HttpStatusCode.PermanentRedirect
            && Uri.TryCreate(reply.Header("Location"), UriKind.Absolute, out var location)
        )
            return location;
        throw new HttpRequestException("The release download did not redirect.", null, reply.Status);
    }

    internal static async Task<bool> Matches(
        string path,
        string sha256,
        CancellationToken cancellation
    )
    {
        await using var file = File.OpenRead(path);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellation));
        return string.Equals(sha256, actual, StringComparison.OrdinalIgnoreCase);
    }
}
