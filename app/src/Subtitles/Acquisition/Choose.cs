using Syno.TinyTorrent.Shared;
using Syno.TinyTorrent.Shared.Network;
using Syno.TinyTorrent.Shared.Storage;

namespace Syno.TinyTorrent.Subtitles;

internal sealed partial class Acquisition
{
    // Lists the supplier's subtitles for one video, so the person can choose
    // one when no release matches.
    internal Task<IReadOnlyList<SubtitleCandidate>> Candidates(
        long entryId,
        string language,
        CancellationToken cancellation,
        RetryCommand? command = null
    ) =>
        Ask(
            cancellation,
            async pending =>
            {
                var chosen = await Chosen(entryId, language, pending).ConfigureAwait(false);
                var target = chosen with { Manual = true };
                var lookup = LookupFor(target) with { Fresh = Fresh(target, command) };
                return await Lookup(lookup, pending, choices: true).ConfigureAwait(false);
            }
        );

    internal async Task<RequestBlock?> Protection(
        long entryId,
        string language,
        CancellationToken cancellation
    )
    {
        var lookup = LookupFor(await Chosen(entryId, language, cancellation).ConfigureAwait(false));
        return await Run(connection => Protection(connection, lookup), cancellation)
            .ConfigureAwait(false);
    }

    // Downloads the chosen subtitle beside the video, after any subtitles
    // already there, and records the movie as searched so the default Find
    // scope leaves it alone.
    internal Task Choose(
        long entryId,
        SubtitleCandidate candidate,
        CancellationToken cancellation,
        RetryCommand? command = null
    ) =>
        Ask(
            cancellation,
            async pending =>
            {
                var chosen = await Chosen(entryId, candidate.Language, pending)
                    .ConfigureAwait(false);
                var target = chosen with { Manual = true };
                var lookup = LookupFor(target) with { Fresh = Fresh(target, command) };
                if (
                    !await Download(Vacant(target), candidate, lookup, pending)
                        .ConfigureAwait(false)
                )
                    throw new SubtitleException(SubtitleFailure.Exists);
                await Run(
                        connection =>
                        {
                            Execute(
                                connection,
                                """
                                INSERT INTO subtitle_jobs(path,language,stage,outcome,manual,match)
                                VALUES($path,$language,$stage,$complete,1,$likely)
                                ON CONFLICT(path,language) DO UPDATE SET outcome=excluded.outcome,reference=NULL,
                                    match=CASE WHEN match=$exact THEN match ELSE excluded.match END;
                                """,
                                ("$path", target.Path),
                                ("$language", target.Language),
                                ("$stage", (int)Stage(target)),
                                ("$complete", (int)SubtitleOutcome.Complete),
                                ("$likely", (int)SubtitleMatch.Likely),
                                ("$exact", (int)SubtitleMatch.Exact)
                            );
                            AssociateJob(connection, target);
                            Execute(connection, Database.Cleanup);
                            return true;
                        },
                        CancellationToken.None
                    )
                    .ConfigureAwait(false);
                await RefreshCounts().ConfigureAwait(false);
            }
        );

    // The video's subtitle destination for the language. A video that left
    // the torrents, or shares its subtitle name with another video, has
    // Changed.
    private async Task<Target> Chosen(long entryId, string language, CancellationToken cancellation)
    {
        if (!Ready)
        {
            throw new SubtitleException(SubtitleFailure.Unconfigured);
        }
        var targets = await Targets(cancellation, new HashSet<long> { entryId })
            .ConfigureAwait(false);
        return targets.FirstOrDefault(target =>
                target.Language == language
                && Eligible(target)
                && target.Videos.Any(video => video.EntryId == entryId && video.Eligible)
            ) ?? throw new SubtitleException(SubtitleFailure.Changed);
    }
}
