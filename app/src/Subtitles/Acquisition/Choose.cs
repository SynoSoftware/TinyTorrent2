using Syno.TinyTorrent.Models;
using Syno.TinyTorrent.Services;

namespace Syno.TinyTorrent.Subtitles;

internal sealed partial class Acquisition
{
    // Lists the supplier's subtitles for one video, so the person can choose
    // one when no release matches.
    internal async Task<IReadOnlyList<SubtitleCandidate>> Candidates(
        long entryId,
        string language,
        CancellationToken cancellation
    )
    {
        CancellationToken requests;
        lock (_requestSync)
            requests = _requests.Token;
        using var pending = CancellationTokenSource.CreateLinkedTokenSource(
            requests,
            _lifetime.Token,
            cancellation
        );
        await _operation.WaitAsync(pending.Token).ConfigureAwait(false);
        try
        {
            if (
                await Chosen(entryId, language, pending.Token).ConfigureAwait(false)
                is not { } chosen
            )
                throw new OperationCanceledException(pending.Token);
            var target = chosen.Target with { Manual = true };
            var lookup = new SubtitleLookup(
                Path.GetFileName(chosen.Video.FinalPath),
                language,
                token => Validate(target, token)
            );
            return await Lookup(lookup, pending.Token, choices: true).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await SaveAllowance(RequireSupplier(SupplierId).Allowance(_account))
                    .ConfigureAwait(false);
            }
            finally
            {
                _operation.Release();
                Notify();
            }
        }
    }

    // Downloads the chosen subtitle beside the video, after any subtitles
    // already there, and records the movie as searched so the default Find
    // scope leaves it alone.
    internal async Task Choose(
        long entryId,
        SubtitleCandidate candidate,
        CancellationToken cancellation
    )
    {
        CancellationToken requests;
        lock (_requestSync)
            requests = _requests.Token;
        using var pending = CancellationTokenSource.CreateLinkedTokenSource(
            requests,
            _lifetime.Token,
            cancellation
        );
        await _operation.WaitAsync(pending.Token).ConfigureAwait(false);
        try
        {
            if (
                await Chosen(entryId, candidate.Language, pending.Token).ConfigureAwait(false)
                is not { } chosen
            )
                throw new OperationCanceledException(pending.Token);
            var target = chosen.Target with { Manual = true };
            var lookup = new SubtitleLookup(
                Path.GetFileName(chosen.Video.FinalPath),
                candidate.Language,
                token => Validate(target, token)
            );
            if (
                !await Download(Vacant(target), candidate, lookup, pending.Token)
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
        finally
        {
            try
            {
                await SaveAllowance(RequireSupplier(SupplierId).Allowance(_account))
                    .ConfigureAwait(false);
            }
            finally
            {
                _operation.Release();
                Notify();
                Wake();
            }
        }
    }

    // The video's subtitle destination for the language; null once the video
    // left the torrents or shares its subtitle name with another video.
    private async Task<(Target Target, Video Video)?> Chosen(
        long entryId,
        string language,
        CancellationToken cancellation
    )
    {
        if (!Ready)
            throw new SubtitleException(SubtitleFailure.Unconfigured);
        var targets = await Targets(cancellation).ConfigureAwait(false);
        foreach (
            var target in targets.Where(target => target.Language == language && Eligible(target))
        )
            if (
                target.Videos.FirstOrDefault(video => video.EntryId == entryId && video.Eligible) is
                { } video
            )
                return (target, video);
        return null;
    }
}
