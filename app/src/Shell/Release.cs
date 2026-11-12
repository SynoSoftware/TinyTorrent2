using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using Syno.TinyTorrent.Shared;
using Syno.TinyTorrent.Shared.Network;

namespace Syno.TinyTorrent.Shell;

/// <summary>
/// Downloads a file that a GitHub release publishes, TinyTorrent's own setup
/// or gozler's, and checks a downloaded file against its SHA-256.
/// </summary>
internal static class Release
{
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
