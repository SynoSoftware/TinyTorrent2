using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Syno.TinyTorrent.Services;

namespace Syno.TinyTorrent.Library;

/// <summary>Reads a provider's JSON answer and turns a failed request into Library's failures.</summary>
internal static class ProviderJson
{
    /// <remarks>
    /// <paramref name="section"/> names the provider's text section, which
    /// holds route_unavailable, unavailable, refused and invalid_result.
    /// </remarks>
    internal static async Task<JsonElement> Get(
        ProviderHttp http,
        HttpRequestMessage request,
        string section,
        CancellationToken cancellation
    )
    {
        ProviderReply reply;
        try
        {
            reply = await http.Send(request, ProviderHttp.JsonLimit, cancellation)
                .ConfigureAwait(false);
        }
        catch (HttpRouteException error)
        {
            throw new RouteException(section, error);
        }
        catch (InvalidDataException error)
        {
            throw new ProviderException(section, "invalid_result", inner: error);
        }
        catch (Exception error)
            when (error is HttpRequestException or IOException or InvalidOperationException)
        {
            throw new ProviderException(section, "unavailable", inner: error);
        }
        DateTimeOffset? retryAt = null;
        if (reply.Status is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
            retryAt = reply.RetryAt ?? Reset(reply) ?? DateTimeOffset.UtcNow.AddMinutes(1);
        // A refused key or client fails every later request too, so lookups
        // stop until the person changes the key or requests a lookup again.
        if (reply.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new ProviderException(section, "refused");
        // A missing item concerns one video, such as an episode the provider
        // does not list, so it must not stop lookups of other videos.
        if (reply.Status == HttpStatusCode.NotFound)
            throw new VideoException(section, "invalid_result");
        if (reply.Status != HttpStatusCode.OK)
            throw new ProviderException(section, "unavailable", retryAt);
        try
        {
            using var document = JsonDocument.Parse(reply.Bytes);
            return document.RootElement.Clone();
        }
        catch (JsonException error)
        {
            throw new ProviderException(section, "invalid_result", inner: error);
        }
    }

    // MDBList names the end of its daily allowance in Unix seconds instead of
    // sending Retry-After, so lookups wait for it rather than retry each minute.
    private static DateTimeOffset? Reset(ProviderReply reply) =>
        long.TryParse(
            reply.Header("X-RateLimit-Reset"),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var seconds
        )
        && seconds > DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        && seconds <= DateTimeOffset.MaxValue.ToUnixTimeSeconds()
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;
}
