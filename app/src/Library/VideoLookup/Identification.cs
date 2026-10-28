using Syno.TinyTorrent.Shared;
using Syno.TinyTorrent.Shared.Network;

namespace Syno.TinyTorrent.Library;

internal sealed partial class VideoLookup
{
    private async Task<(IdentificationOutcome Outcome, RequestBlock? Block)> Collect(
        Context context,
        VideoTarget target,
        VideoChoice? choice,
        bool refresh
    )
    {
        var language = context.Option.IsLocalized ? context.Language : null;
        var attempt = await store.Begin(target, language, context.Token).ConfigureAwait(false);
        if (attempt is null)
            return Superseded(target);
        VideoInformation? video = null;
        var unanswered = false;
        RequestBlockedException? blocked = null;
        IdentificationOutcome outcome;
        bool accepted;
        try
        {
            refresh =
                refresh
                || target.Kind == LookupKind.Explicit
                    && choice is null
                    && attempt.Choice is not null;
            var release = FileName.Interpret(target.Entry.Name);
            var request = new VideoRequest(
                release,
                context.Language,
                (path, download, token) => Read(context, path, download, refresh, token),
                Get(context, "detail", context.Fresh(SearchRequest(context, release.Title).Key))
            );
            try
            {
                if (context.Option.IsOnline && choice is null && attempt.Choice is null && !refresh)
                    video = await store
                        .Saved(target, context.Language, context.Token)
                        .ConfigureAwait(false);
                if (video is null)
                {
                    choice ??= attempt.Choice;
                    if (choice is not null)
                    {
                        if (choice.Kind == VideoKind.Series && request.Release.Episodes.Count == 0)
                            throw new VideoException(VideoFailure.EpisodeRequired);
                        video = await Describe(context, choice, request).ConfigureAwait(false);
                    }
                    else
                    {
                        var series = request.Release.Season is not null
                            ? await store
                                .Series(target, request.Release, context.Token)
                                .ConfigureAwait(false)
                            : null;
                        video = await Match(context, request, series).ConfigureAwait(false);
                    }
                }
            }
            catch (RequestBlockedException error)
                when (target.Kind != LookupKind.Explicit
                    && error.Block.Kind
                        is RequestBlockKind.Missing
                            or RequestBlockKind.Unusable
                            or RequestBlockKind.Limit
                            or RequestBlockKind.NoMatch
                )
            {
                blocked = error;
            }
            // An individual unusable record must not stop unrelated lookups.
            catch (VideoException error)
                when (error is not ProviderException && target.Kind != LookupKind.Explicit)
            {
                unanswered = true;
            }
            // Another title is no answer for the title the file already has.
            if (attempt.AddsTo is { } title && video?.ImdbId != title)
                video = null;
            outcome =
                video is not null ? IdentificationOutcome.Matched
                : blocked is { Block.Kind: not RequestBlockKind.Missing }
                    ? IdentificationOutcome.Blocked
                : unanswered ? IdentificationOutcome.Unanswered
                : IdentificationOutcome.NoMatch;
            var answered = attempt with { BlockedRequest = blocked?.Request };
            accepted = await (
                video is not null
                    ? store.Complete(answered, video, context.Token)
                    : store.Complete(
                        answered,
                        outcome switch
                        {
                            IdentificationOutcome.Blocked => AttemptOutcome.Blocked,
                            IdentificationOutcome.Unanswered => AttemptOutcome.Unanswered,
                            _ => AttemptOutcome.NoMatch,
                        },
                        context.Token
                    )
            ).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            // A provider that cannot serve any request has not answered for
            // this file, so the attempt leaves it eligible for a later lookup.
            var failure =
                context.Token.IsCancellationRequested
                || error
                    is OperationCanceledException
                        or ProviderException
                        or RequestBlockedException
                    ? AttemptOutcome.Cancelled
                    : AttemptOutcome.Failed;
            await store
                .Complete(
                    attempt with
                    {
                        BlockedRequest = (blocked ?? (error as RequestBlockedException))?.Request,
                    },
                    failure,
                    CancellationToken.None
                )
                .ConfigureAwait(false);
            context.Token.ThrowIfCancellationRequested();
            throw;
        }
        if (!accepted)
            return Superseded(target);
        Stored?.Invoke(this, EventArgs.Empty);
        return (outcome, blocked?.Block);
    }

    // A refused explicit request says so, because its caller would otherwise
    // report that no match exists.
    private static (IdentificationOutcome Outcome, RequestBlock? Block) Superseded(
        VideoTarget target
    )
    {
        if (target.Kind == LookupKind.Explicit)
            throw new VideoException(VideoFailure.FileChanged);
        return (IdentificationOutcome.Superseded, null);
    }

    /// <summary>
    /// Finds the provider's unambiguous match for a release, or null. A search
    /// result alone is weak evidence, so its description must agree with it.
    /// </summary>
    private async Task<VideoInformation?> Match(
        Context context,
        VideoRequest request,
        VideoChoice? series
    )
    {
        var release = request.Release;
        var kind = release.Season is null ? VideoKind.Movie : VideoKind.Series;
        if (!CanMatch(context.Option, release))
            return null;
        if (series is not null)
            return await Describe(context, series, request).ConfigureAwait(false);
        var matches = Matches(
            release,
            (await Search(context, release.Title).ConfigureAwait(false)).Choices
        );
        if (matches.Length != 1)
        {
            if (context.Option.IsOnline)
                await store
                    .RejectSearch(SearchRequest(context, release.Title).Key)
                    .ConfigureAwait(false);
            return null;
        }
        var match = matches[0];
        var video = await Describe(context, match, request).ConfigureAwait(false);
        var year = kind == VideoKind.Movie ? video.Year ?? match.Year : match.Year ?? video.Year;
        if (
            FileName.Normalize(video.Title) == FileName.Normalize(match.Title)
            && (release.Year is null || year == release.Year)
        )
            return video;
        if (context.Option.IsOnline)
            await store
                .RejectSearch(SearchRequest(context, release.Title).Key)
                .ConfigureAwait(false);
        return null;
    }

    private async Task<VideoSearch> Search(
        Context context,
        string query,
        int? year = null,
        bool automatic = true
    )
    {
        var queries = Queries(context, query, year, automatic).ToArray();
        for (var index = 0; index < queries.Length; index++)
        {
            context.Token.ThrowIfCancellationRequested();
            var answer = await SearchOne(context, queries[index].Text, queries[index].Year)
                .ConfigureAwait(false);
            if (!answer.Empty || index == queries.Length - 1)
                return answer with { Broadened = year is not null && queries[index].Year is null };
        }
        return new([], false);
    }

    private static IEnumerable<(string Text, int? Year)> Queries(
        Context context,
        string query,
        int? year = null,
        bool automatic = true
    )
    {
        query = context.Provider.Prepare(query);
        if (query.Length == 0)
            yield break;
        yield return (query, year);
        if (!automatic)
            yield break;
        if (year is not null)
            yield return (query, null);
        if (context.Access is null)
            yield break;
        var plain = context.Provider.Prepare(string.Join(' ', FileName.Words(query)));
        if (plain.Length > 0 && plain != query)
            yield return (plain, null);
    }

    private async Task<VideoSearch> SearchOne(Context context, string query, int? year = null)
    {
        if (context.Access is not { } access)
            return await context
                .Provider.Search(
                    new(query, context.Language, Offline) { Year = year },
                    context.Token
                )
                .ConfigureAwait(false);
        var request = SearchRequest(context, query, year);
        var since = context.Scope == FetchScope.New ? 0 : context.Accepted ?? 0;
        if (
            await store.Search(request.Key, context.Token, since).ConfigureAwait(false) is { } saved
        )
            return new(
                saved,
                saved.Count == 0
                    && (await policy.Read(request, context.Token).ConfigureAwait(false))?.Kind
                        == RequestBlockKind.Missing
            );
        if (context.Scope == FetchScope.New || context.Command is null)
        {
            if (await policy.Read(request, context.Token).ConfigureAwait(false) is { } block)
                throw new RequestBlockedException(block);
        }
        VideoSearch answer;
        try
        {
            answer = await context
                .Provider.Search(
                    new(query, context.Language, Get(context, "search", context.Fresh(request.Key)))
                    {
                        Year = year,
                    },
                    context.Token
                )
                .ConfigureAwait(false);
        }
        catch (VideoException error) when (error is not ProviderException)
        {
            var hold = await policy
                .Fail(access with { Operation = "search" }, new(RequestBlockKind.Backoff))
                .ConfigureAwait(false);
            throw new ProviderException(VideoFailure.InvalidResult, hold.RetryAt, error);
        }
        await store.SaveSearch(request, answer, CancellationToken.None).ConfigureAwait(false);
        await policy.Succeed(access with { Operation = "search" }).ConfigureAwait(false);
        await Recovered(context, access).ConfigureAwait(false);
        context.Token.ThrowIfCancellationRequested();
        return answer;
    }

    private static LookupRequest SearchRequest(Context context, string query, int? year = null)
    {
        var option = context.Option;
        query = context.Provider.Prepare(query);
        var parts = new List<string>
        {
            query,
            option.IsLocalized ? context.Language : string.Empty,
        };
        if (year is { } requested)
            parts.Add(requested.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return new(
            RequestKey.Create(option.ProviderId, "search", parts),
            RequestKey.Create(
                option.ProviderId,
                "search-count",
                [FileName.Normalize(string.Join(' ', FileName.Words(query)))]
            )
        );
    }

    // Sends an online provider's request with the protection of its key and
    // the operation Library runs; a 404 for a detail means the video is gone.
    // A refusal holds every request that uses the key.
    private ProviderGet Get(Context context, string operation, FreshRequest? fresh) =>
        context.Access is not { } access
            ? Offline
            : (message, cancellation, refusal) =>
                ProviderJson.Get(
                    http,
                    new ProviderCall(
                        message,
                        policy,
                        new(access with { Operation = operation }) { Authentication = access }
                    )
                    {
                        Fresh = fresh,
                        NamesResource = operation == "detail",
                    },
                    cancellation,
                    refusal
                );

    // A provider that answers from downloaded data sends no request.
    private static readonly ProviderGet Offline = (_, _, _) =>
        throw new InvalidOperationException();

    private async Task<VideoRecord> Read(
        Context context,
        string path,
        Func<CancellationToken, Task<VideoRecord>> download,
        bool refresh,
        CancellationToken cancellation
    )
    {
        var catalog = context.Option.ProviderId;
        var language = context.Option.IsLocalized ? context.Language : null;
        var request = new LookupRequest(
            RequestKey.Create(catalog, "detail", [path, language ?? string.Empty])
        );
        var since = context.Scope == FetchScope.New ? 0 : context.Command?.Accepted ?? 0;
        if (
            await store
                .Record(
                    catalog,
                    path,
                    language,
                    cancellation,
                    refresh ? context.Accepted ?? long.MaxValue : 0
                )
                .ConfigureAwait(false) is
            { } saved
        )
            return saved;
        if (await policy.Read(request, cancellation, since).ConfigureAwait(false) is { } block)
            throw new RequestBlockedException(block) { Request = request.Key };
        VideoRecord record;
        try
        {
            record = await download(cancellation).ConfigureAwait(false);
        }
        catch (VideoException error) when (error is not ProviderException)
        {
            var outcome =
                error is MissingVideoException ? RequestOutcome.Missing : RequestOutcome.Unusable;
            await policy.Record(request, outcome).ConfigureAwait(false);
            throw new RequestBlockedException(
                new(
                    outcome == RequestOutcome.Missing
                        ? RequestBlockKind.Missing
                        : RequestBlockKind.Unusable
                )
            )
            {
                Request = request.Key,
            };
        }
        await store.SaveRecord(request, record, language).ConfigureAwait(false);
        if (context.Access is { } access)
        {
            await policy.Succeed(access with { Operation = "detail" }).ConfigureAwait(false);
            await Recovered(context, access).ConfigureAwait(false);
        }
        return record;
    }

    private async Task Recovered(Context context, ProviderAccess access)
    {
        if (context.Command?.Recheck == true)
            await policy.Succeed(access).ConfigureAwait(false);
        lock (_gate)
            if (ReferenceEquals(Provider, context.Provider))
            {
                _failure = null;
                _recheck = null;
            }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task<VideoInformation> Describe(
        Context context,
        VideoChoice choice,
        VideoRequest request
    )
    {
        var video = await context
            .Provider.Describe(choice, request, context.Token)
            .ConfigureAwait(false);
        await store.SaveChoices(video, CancellationToken.None).ConfigureAwait(false);
        context.Token.ThrowIfCancellationRequested();
        return video;
    }

    // A season without an episode number names no episode a provider can describe.
    private static bool CanMatch(ProviderOption option, Release release) =>
        !release.IsExtra
        && release.Title.Length > 0
        && option.Kinds.Contains(release.Season is null ? VideoKind.Movie : VideoKind.Series)
        && (release.Season is null || release.Episodes.Count > 0);

    private static VideoChoice[] Matches(Release release, IEnumerable<VideoChoice> choices) =>
        choices
            .Where(choice =>
                choice.Kind == (release.Season is null ? VideoKind.Movie : VideoKind.Series)
                && Confidence(release, choice) > 0.8
            )
            .ToArray();

    private static double Confidence(Release release, VideoChoice choice)
    {
        if (release.Year is { } year && choice.Year is { } candidate && year != candidate)
            return 0;
        var title = FileName.Words(FileName.Normalize(release.Title)).ToHashSet();
        var numbers = title.Where(word => word.All(char.IsDigit)).ToHashSet();
        double Similarity(string value)
        {
            var words = FileName.Words(FileName.Normalize(value)).ToHashSet();
            if (!numbers.SetEquals(words.Where(word => word.All(char.IsDigit))))
                return 0;
            return title.Count + words.Count == 0
                ? 0
                : 2.0 * title.Intersect(words).Count() / (title.Count + words.Count);
        }
        return Math.Max(
            Similarity(choice.Title),
            choice.Original is { } original ? Similarity(original) : 0
        );
    }
}
