using System.Net;

namespace Syno.TinyTorrent.Shared.Network;

internal sealed partial class ProviderHttp
{
    // Records the holds that a reply reports and throws for any reply but a
    // success, so Library, Subtitles and the data downloads read one answer
    // the same way. A success can still report a used-up allowance, which
    // holds the next request.
    private static async Task Classify(ProviderCall call, ProviderReply reply)
    {
        RequestBlock? wait = null;
        if (call.Quota?.Invoke(reply) is { } quota)
            wait = await call
                .Policy.Fail(quota.Scope, new(RequestBlockKind.RemoteWait, quota.Until))
                .ConfigureAwait(false);
        if (
            reply.Status == HttpStatusCode.TooManyRequests
            || reply.Header("X-RateLimit-Remaining") == "0"
        )
        {
            var hold = await Hold(call, ReplyFailure.Wait, reply.RetryAt).ConfigureAwait(false);
            wait = wait?.RetryAt > hold?.RetryAt ? wait : hold;
        }
        var status = (int)reply.Status;
        if (status is >= 200 and < 300 || call.CanRedirect && status is >= 300 and < 400)
            return;
        if (wait is not null)
            throw new ReplyException(ReplyFailure.Wait, wait);
        // A request without an authentication scope sends no credentials to
        // refuse, so a refused file link backs off like any other failure.
        if (
            reply.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            && call.Request.Authentication is not null
        )
            throw call.Renewable
                ? new ReplyException(ReplyFailure.Expired, null)
                : await Fail(call, ReplyFailure.Refused).ConfigureAwait(false);
        if (call.NamesResource && reply.Status is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            throw new ReplyException(ReplyFailure.Missing, null);
        throw await Fail(call, ReplyFailure.Unavailable, reply.RetryAt).ConfigureAwait(false);
    }

    /// <summary>
    /// Records the hold that <paramref name="failure"/> calls for and returns
    /// the exception that reports both. A feature calls it for a failure that
    /// it reads in the body of a successful reply, such as JSON that does not
    /// parse or an error that the provider writes there.
    /// </summary>
    /// <param name="until">When the provider asks for the next request, if it says.</param>
    internal static async Task<ReplyException> Fail(
        ProviderCall call,
        ReplyFailure failure,
        DateTimeOffset? until = null,
        Exception? inner = null
    ) => new(failure, await Hold(call, failure, until).ConfigureAwait(false), inner);

    // A refusal waits for the person to fix access, and a remote wait for the
    // provider; both hold every operation of the access. Any other failure
    // backs off only the operation that failed.
    private static async Task<RequestBlock?> Hold(
        ProviderCall call,
        ReplyFailure failure,
        DateTimeOffset? until
    )
    {
        var request = call.Request;
        var every = request.Access with { Operation = string.Empty };
        return failure switch
        {
            ReplyFailure.Wait => await call
                .Policy.Fail(request.Allowance ?? every, new(RequestBlockKind.RemoteWait, until))
                .ConfigureAwait(false),
            ReplyFailure.Refused => await call
                .Policy.Fail(request.Authentication ?? every, new(RequestBlockKind.Attention))
                .ConfigureAwait(false),
            ReplyFailure.Unavailable or ReplyFailure.Invalid => await call
                .Policy.Fail(request.Access, new(RequestBlockKind.Backoff, until))
                .ConfigureAwait(false),
            _ => null,
        };
    }
}

/// <summary>
/// An allowance that a reply reports used up, and when the provider renews
/// it, if the reply says.
/// </summary>
internal sealed record Quota(ProviderAccess Scope, DateTimeOffset? Until);

/// <summary>
/// A provider call that did not succeed, and the hold that the shared path
/// recorded for it. Missing, Expired and Route hold nothing.
/// </summary>
internal sealed class ReplyException(ReplyFailure failure, RequestBlock? hold, Exception? inner = null)
    : Exception(failure.ToString(), inner)
{
    internal ReplyFailure Failure { get; } = failure;
    internal RequestBlock? Hold { get; } = hold;
}
