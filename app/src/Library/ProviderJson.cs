using System.Text.Json;
using Syno.TinyTorrent.Shared.Network;

namespace Syno.TinyTorrent.Library;

/// <summary>
/// Reads a provider's JSON answer, and turns a failed reply into Library's
/// failure.
/// </summary>
internal static class ProviderJson
{
    internal static async Task<JsonElement> Get(
        ProviderHttp http,
        ProviderCall call,
        CancellationToken cancellation,
        Func<JsonElement, VideoFailure?>? refusal = null
    )
    {
        ProviderReply reply;
        try
        {
            reply = await http.Send(call, ProviderHttp.JsonLimit, cancellation)
                .ConfigureAwait(false);
        }
        catch (ReplyException error)
        {
            throw Failure(error);
        }
        JsonElement data;
        try
        {
            using var document = JsonDocument.Parse(reply.Bytes);
            data = document.RootElement.Clone();
        }
        catch (JsonException error)
        {
            throw Failure(
                await ProviderHttp
                    .Fail(call, ReplyFailure.Invalid, inner: error)
                    .ConfigureAwait(false)
            );
        }
        // A refusal in the body keeps its own failure, such as OMDb's used-up
        // allowance, and records its hold through the shared path.
        if (refusal?.Invoke(data) is { } failure)
        {
            var refused = await ProviderHttp
                .Fail(
                    call,
                    failure switch
                    {
                        VideoFailure.Refused => ReplyFailure.Refused,
                        VideoFailure.Exhausted => ReplyFailure.Wait,
                        _ => ReplyFailure.Unavailable,
                    }
                )
                .ConfigureAwait(false);
            throw new ProviderException(failure, refused.Hold.RetryAt, refused);
        }
        return data;
    }

    // The failure's retry time is the hold's, so the person sees the time
    // that the shared request policy keeps. A remote wait stays a request
    // block, which the person reads as a wait with its time.
    private static Exception Failure(ReplyException error) =>
        error switch
        {
            ReplyException.Held { Failure: ReplyFailure.Wait } wait => new RequestBlockedException(
                wait.Hold
            ),
            ReplyException.Held held => new ProviderException(
                held.Failure switch
                {
                    ReplyFailure.Refused => VideoFailure.Refused,
                    ReplyFailure.Unavailable => VideoFailure.Unavailable,
                    ReplyFailure.Invalid => VideoFailure.InvalidResult,
                    _ => throw new ArgumentOutOfRangeException(nameof(error)),
                },
                held.Hold.RetryAt,
                error
            ),
            ReplyException.Expired => new ProviderException(VideoFailure.Refused, null, error),
            ReplyException.Missing => new MissingVideoException(),
            ReplyException.Route => new RouteException(error),
            _ => throw new ArgumentOutOfRangeException(nameof(error)),
        };
}
