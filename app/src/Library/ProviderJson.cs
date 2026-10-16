using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Syno.TinyTorrent.Shared.Network;

namespace Syno.TinyTorrent.Library;

/// <summary>
/// Turns a provider's replies into Library's failures and request holds, and
/// reads a provider's JSON answer.
/// </summary>
internal static class ProviderJson
{
    internal static async Task<JsonElement> Get(
        ProviderHttp http,
        ProviderCall call,
        CancellationToken cancellation,
        bool missingResource = false,
        Func<JsonElement, VideoFailure?>? refusal = null
    )
    {
        ProviderReply reply;
        try
        {
            reply = await http.Send(
                    call with
                    {
                        Observe = response => Observe(call, response, missingResource),
                    },
                    ProviderHttp.JsonLimit,
                    cancellation
                )
                .ConfigureAwait(false);
        }
        catch (HttpRouteException error)
        {
            throw new RouteException(error);
        }
        catch (InvalidDataException error)
        {
            var hold = await call
                .Policy.Fail(call.Request.Access, new(RequestBlockKind.Backoff))
                .ConfigureAwait(false);
            throw new ProviderException(VideoFailure.InvalidResult, hold.RetryAt, error);
        }
        catch (Exception error)
            when (error is HttpRequestException or IOException or InvalidOperationException)
        {
            var hold = await call
                .Policy.Read(call.Request.Access, CancellationToken.None)
                .ConfigureAwait(false);
            throw new ProviderException(VideoFailure.Unavailable, hold?.RetryAt, error);
        }
        JsonElement data;
        try
        {
            using var document = JsonDocument.Parse(reply.Bytes);
            data = document.RootElement.Clone();
        }
        catch (JsonException error)
        {
            var hold = await call
                .Policy.Fail(call.Request.Access, new(RequestBlockKind.Backoff))
                .ConfigureAwait(false);
            throw new ProviderException(VideoFailure.InvalidResult, hold.RetryAt, error);
        }
        if (refusal?.Invoke(data) is { } failure)
            throw await Refuse(call, failure, null).ConfigureAwait(false);
        return data;
    }

    /// <summary>
    /// Records the hold that a reply's status calls for, and throws the failure
    /// it means; a successful reply passes.
    /// </summary>
    internal static async Task Observe(
        ProviderCall call,
        ProviderReply reply,
        bool missingResource = false
    )
    {
        if (
            reply.Status == HttpStatusCode.TooManyRequests
            || reply.Header("X-RateLimit-Remaining") == "0"
        )
        {
            var hold = await call
                .Policy.Fail(
                    call.Request.Access with
                    {
                        Operation = string.Empty,
                    },
                    new(RequestBlockKind.RemoteWait, reply.RetryAt ?? Reset(reply))
                )
                .ConfigureAwait(false);
            if (reply.Status != HttpStatusCode.OK)
                throw new RequestBlockedException(hold);
        }
        if (reply.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw await Refuse(call, VideoFailure.Refused, null).ConfigureAwait(false);
        if (missingResource && reply.Status == HttpStatusCode.NotFound)
            throw new MissingVideoException();
        if (reply.Status != HttpStatusCode.OK)
            throw await Refuse(call, VideoFailure.Unavailable, reply.RetryAt).ConfigureAwait(false);
    }

    // A refusal waits for the person to fix access, and a used-up allowance
    // waits for the provider; both hold every request to the provider. Any
    // other failure backs off only the operation that failed.
    private static async Task<ProviderException> Refuse(
        ProviderCall call,
        VideoFailure failure,
        DateTimeOffset? retryAt
    )
    {
        var access = call.Request.Access;
        var hold = failure switch
        {
            VideoFailure.Refused => await call
                .Policy.Fail(
                    access with
                    {
                        Operation = string.Empty,
                    },
                    new(RequestBlockKind.Attention)
                )
                .ConfigureAwait(false),
            VideoFailure.Exhausted => await call
                .Policy.Fail(
                    access with
                    {
                        Operation = string.Empty,
                    },
                    new(RequestBlockKind.RemoteWait, retryAt)
                )
                .ConfigureAwait(false),
            _ => await call
                .Policy.Fail(access, new(RequestBlockKind.Backoff, retryAt))
                .ConfigureAwait(false),
        };
        return new ProviderException(failure, hold.RetryAt);
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
