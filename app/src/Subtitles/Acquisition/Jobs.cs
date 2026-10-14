using Microsoft.Data.Sqlite;
using Syno.TinyTorrent.Models;
using Syno.TinyTorrent.Services;

namespace Syno.TinyTorrent.Subtitles;

internal sealed partial class Acquisition
{
    private static void AssociateJob(SqliteConnection connection, Target target)
    {
        foreach (var video in target.Videos)
            Execute(
                connection,
                """
                INSERT OR IGNORE INTO subtitle_job_sources(path,language,origin,file_index)
                SELECT $path,$language,f.origin,f.file_index FROM collected_files f
                JOIN locations l USING(entry_id)
                WHERE f.origin=$origin AND f.file_index=$index AND f.entry_id=$entry
                    AND l.path=$movie COLLATE PATH;
                """,
                ("$path", target.Path),
                ("$language", target.Language),
                ("$origin", video.Origin),
                ("$index", video.Index),
                ("$movie", video.FinalPath),
                ("$entry", video.EntryId)
            );
    }

    private async Task ReconcileJobs(TargetIndex targets, CancellationToken cancellation)
    {
        var transfers = await Run(
                connection =>
                {
                    using var command = connection.CreateCommand();
                    command.CommandText =
                        "SELECT path,language,origin,file_index FROM subtitle_job_sources;";
                    var sources = new List<(string Path, string Language, string Origin, int Index)>();
                    using (var reader = command.ExecuteReader())
                        while (reader.Read())
                            sources.Add(
                                (
                                    reader.GetString(0),
                                    reader.GetString(1),
                                    reader.GetString(2),
                                    reader.GetInt32(3)
                                )
                            );
                    var moved = new List<(string Previous, string Path, string Language, long EntryId)>();
                    foreach (var source in sources)
                    {
                        var target = targets.Find(source.Language,
                            SubtitleFile.Rank(source.Path, source.Language), source.Origin, source.Index);
                        if (target is null || target.IsAmbiguous || !Current(connection, target.Videos))
                            continue;
                        if (StringComparer.OrdinalIgnoreCase.Equals(source.Path, target.Path))
                            continue;
                        Execute(
                            connection,
                            """
                            UPDATE subtitle_jobs AS destination
                            SET match=CASE WHEN previous.match=$exact THEN $exact
                                ELSE COALESCE(destination.match,previous.match) END
                            FROM subtitle_jobs AS previous
                            WHERE destination.path=$new AND destination.language=$language
                                AND previous.path=$old AND previous.language=$language
                                AND destination.reference IS NULL;
                            INSERT INTO subtitle_jobs(path,language,stage,outcome,retry_at,attempts,manual,guesses,match,reference,fresh)
                            SELECT $new,language,stage,outcome,retry_at,attempts,manual,guesses,match,reference,fresh
                            FROM subtitle_jobs WHERE path=$old AND language=$language
                            ON CONFLICT(path,language) DO UPDATE SET stage=excluded.stage,outcome=excluded.outcome,
                                retry_at=excluded.retry_at,attempts=excluded.attempts,manual=excluded.manual,
                                guesses=excluded.guesses,reference=excluded.reference,fresh=excluded.fresh,
                                match=CASE WHEN excluded.reference IS NOT NULL THEN excluded.match ELSE subtitle_jobs.match END
                            WHERE excluded.manual=1 AND excluded.outcome IN ($pending,$unavailable,$blocked)
                                AND (subtitle_jobs.manual=0 OR subtitle_jobs.outcome NOT IN ($pending,$unavailable,$blocked));
                            """,
                            ("$new", target.Path),
                            ("$old", source.Path),
                            ("$language", source.Language),
                            ("$pending", (int)SubtitleOutcome.Pending),
                            ("$unavailable", (int)SubtitleOutcome.Unavailable),
                            ("$blocked", (int)SubtitleOutcome.Blocked),
                            ("$exact", (int)SubtitleMatch.Exact)
                        );
                        AssociateJob(connection, target);
                        Execute(
                            connection,
                            """
                            DELETE FROM subtitle_job_sources WHERE path=$path AND language=$language
                                AND origin=$origin AND file_index=$index;
                            """,
                            ("$path", source.Path),
                            ("$language", source.Language),
                            ("$origin", source.Origin),
                            ("$index", source.Index)
                        );
                        moved.Add((source.Path, target.Path, source.Language, target.Videos[0].EntryId));
                    }
                    if (moved.Count > 0)
                        Execute(connection, Database.Cleanup);
                    return moved;
                },
                cancellation
            )
            .ConfigureAwait(false);
        lock (_requestSync)
            foreach (var run in new[] { _manualWork, _automaticWork })
            {
                if (run is null || run.Ended)
                    continue;
                foreach (var job in run.Jobs.ToArray())
                {
                    var destinations = transfers.Where(transfer => transfer.Language == job.Language
                        && StringComparer.OrdinalIgnoreCase.Equals(transfer.Previous, job.Path)).ToArray();
                    if (destinations.Length == 0)
                        continue;
                    foreach (var destination in destinations)
                        run.Jobs.Add(job with { Path = destination.Path });
                    if (targets.Find(job.Language, job.Path) is null)
                        run.Jobs.Remove(job);
                }
            }
        foreach (var transfer in transfers)
            if (_batch.ContainsKey(transfer.Previous))
                _batch[transfer.Path] = transfer.EntryId;
        foreach (var previous in transfers.Select(transfer => transfer.Previous).Distinct())
            if (!targets.Contains(previous))
                _batch.Remove(previous);
    }
}
