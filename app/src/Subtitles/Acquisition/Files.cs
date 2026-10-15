using Microsoft.Data.Sqlite;
using Syno.TinyTorrent.Helpers;
using Syno.TinyTorrent.Models;
using Syno.TinyTorrent.Services;

namespace Syno.TinyTorrent.Subtitles;

internal sealed partial class Acquisition
{
    private readonly List<(
        string Path,
        string Language,
        IReadOnlySet<string> Origins,
        DeletionMode Mode
    )> _deletions = [];

    private async Task ReconcileFiles(TargetIndex targets, CancellationToken cancellation)
    {
        var associations = await Run(
                connection =>
                {
                    var rows = new List<Association>();
                    using var command = connection.CreateCommand();
                    command.CommandText = """
                        SELECT s.path,s.language,s.origin,s.file_index,o.created,s.movie_path
                        FROM subtitle_output_sources s JOIN subtitle_outputs o USING(path,language);
                        """;
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                        rows.Add(
                            new(
                                reader.GetString(0),
                                reader.GetString(1),
                                reader.GetString(2),
                                reader.GetInt32(3),
                                reader.GetBoolean(4),
                                reader.GetString(5)
                            )
                        );
                    return rows;
                },
                cancellation
            )
            .ConfigureAwait(false);
        var changed = false;
        foreach (
            var group in associations.GroupBy(value => value.Path, StringComparer.OrdinalIgnoreCase)
        )
        {
            var destinations = targets.Order(group.Select(source => targets.Find(source.Language,
                SubtitleFile.Rank(source.Path, source.Language), source.Origin, source.Index)).OfType<Target>());
            if (!group.Any(source => source.Created))
            {
                changed |= await Run(
                        connection =>
                        {
                            var removed = false;
                            foreach (var source in group)
                            {
                                var video = destinations
                                    .SelectMany(target => target.Videos)
                                    .FirstOrDefault(video =>
                                        video.Origin == source.Origin && video.Index == source.Index
                                    );
                                if (
                                    video is null
                                    || !Current(connection, [video])
                                    || StringComparer.OrdinalIgnoreCase.Equals(source.Movie, video.FinalPath)
                                )
                                    continue;
                                Execute(
                                    connection,
                                    """
                                    DELETE FROM subtitle_output_sources WHERE path=$path AND language=$language
                                        AND origin=$origin AND file_index=$index;
                                    """,
                                    ("$path", source.Path),
                                    ("$language", source.Language),
                                    ("$origin", source.Origin),
                                    ("$index", source.Index)
                                );
                                removed = true;
                            }
                            if (removed)
                                Execute(connection, Database.Cleanup + Database.Invalidate);
                            return removed;
                        },
                        cancellation
                    )
                    .ConfigureAwait(false);
                continue;
            }
            var moved = destinations
                .Where(target => !StringComparer.OrdinalIgnoreCase.Equals(target.Path, group.Key))
                .ToArray();
            if (moved.Length == 0)
            {
                var renamed = destinations
                    .Where(target =>
                        target.Videos.Any(video =>
                            group.Any(source =>
                                source.Origin == video.Origin
                                && source.Index == video.Index
                                && !StringComparer.OrdinalIgnoreCase.Equals(source.Movie, video.FinalPath)
                            )
                        )
                    )
                    .ToArray();
                if (renamed.Length == 0)
                    continue;
                changed |= await Run(
                        connection =>
                        {
                            var updated = false;
                            foreach (var target in renamed)
                                if (Current(connection, target.Videos))
                                {
                                    AssociateOutput(connection, target);
                                    updated = true;
                                }
                            if (updated)
                                Execute(connection, Database.Invalidate);
                            return updated;
                        },
                        cancellation
                    )
                    .ConfigureAwait(false);
                continue;
            }
            if (
                moved.Length != 1
                || moved[0].IsAmbiguous
                || targets.Contains(group.Key)
            )
                continue;
            var destination = moved[0];
            if (
                await PathAccess(group.Key, cancellation).ConfigureAwait(false) != SubtitlePathAccess.Allowed
                || await PathAccess(destination.Path, cancellation).ConfigureAwait(false) != SubtitlePathAccess.Allowed
            )
                continue;
            try
            {
                cancellation.ThrowIfCancellationRequested();
                if (
                    !await Run(
                            connection => _connected && Current(connection, destination.Videos),
                            cancellation
                        )
                        .ConfigureAwait(false)
                )
                    continue;
                if (!SubtitleFile.Exists(group.Key))
                {
                    changed |= await Run(
                            connection =>
                            {
                                Execute(
                                    connection,
                                    "DELETE FROM subtitle_outputs WHERE path=$path;",
                                    ("$path", group.Key)
                                );
                                Execute(connection, Database.Invalidate);
                                return true;
                            },
                            cancellation
                        )
                        .ConfigureAwait(false);
                    continue;
                }
                File.Move(group.Key, destination.Path, overwrite: false);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                ReportFile(group.Key, SubtitleFailure.Save);
                continue;
            }
            changed |= await Run(
                    connection =>
                    {
                        Execute(
                            connection,
                            """
                            INSERT OR IGNORE INTO subtitle_outputs(path,language,created,reference)
                            SELECT $new,language,created,reference FROM subtitle_outputs WHERE path=$old;
                            """,
                            ("$new", destination.Path),
                            ("$old", group.Key)
                        );
                        foreach (var source in group)
                            Execute(
                                connection,
                                """
                                INSERT OR IGNORE INTO subtitle_output_sources(path,language,origin,file_index,movie_path)
                                SELECT $path,$language,$origin,$index,$movie FROM subtitle_outputs
                                WHERE path=$path AND language=$language;
                                """,
                                ("$path", destination.Path),
                                ("$language", source.Language),
                                ("$origin", source.Origin),
                                ("$index", source.Index),
                                ("$movie", destination.Videos[0].FinalPath)
                            );
                        AssociateOutput(connection, destination);
                        Execute(
                            connection,
                            "DELETE FROM subtitle_outputs WHERE path=$path;",
                            ("$path", group.Key)
                        );
                        Execute(connection, Database.Cleanup + Database.Invalidate);
                        return true;
                    },
                    CancellationToken.None
                )
                .ConfigureAwait(false);
        }
        if (changed)
            LanguagesChanged?.Invoke(this, EventArgs.Empty);
    }

    private Task<SubtitlePathAccess> PathAccess(string path, CancellationToken cancellation) =>
        Run(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM pending_origins;";
                if (Convert.ToInt64(command.ExecuteScalar()) > 0 || !_connected)
                    return SubtitlePathAccess.Pending;
                command.CommandText = """
                    SELECT f.origin FROM source_files f JOIN locations l USING(entry_id)
                    WHERE f.path=$path COLLATE PATH OR l.path=$path COLLATE PATH;
                    """;
                command.Parameters.AddWithValue("$path", path);
                using (var reader = command.ExecuteReader())
                    if (reader.Read())
                        return SubtitlePathAccess.Protected;
                return SubtitlePathAccess.Allowed;
            },
            cancellation
        );

    internal async Task<Deletion> CaptureDeletion(string[] origins)
    {
        var membership = origins.ToHashSet(StringComparer.Ordinal);
        try
        {
            var paths = await Run(
                    connection =>
                    {
                        var result = new List<(string Path, string Language)>();
                        using var command = connection.CreateCommand();
                        command.CommandText = """
                            SELECT s.path,s.origin,s.language FROM subtitle_output_sources s
                            JOIN subtitle_outputs o USING(path,language) WHERE o.created=1;
                            """;
                        using var reader = command.ExecuteReader();
                        var rows = new List<(string Path, string Origin, string Language)>();
                        while (reader.Read())
                            rows.Add(
                                (reader.GetString(0), reader.GetString(1), reader.GetString(2))
                            );
                        foreach (
                            var group in rows.GroupBy(
                                row => row.Path,
                                StringComparer.OrdinalIgnoreCase
                            )
                        )
                            if (group.All(row => membership.Contains(row.Origin)))
                                result.Add((group.Key, group.First().Language));
                        return result.ToArray();
                    },
                    _lifetime.Token
                )
                .ConfigureAwait(false);
            return new Deletion(membership, paths);
        }
        catch (Exception error) when (error is SqliteException or OperationCanceledException)
        {
            if (error is OperationCanceledException)
                System.Diagnostics.Debug.WriteLine("Subtitle deletion preparation cancelled.");
            Notify();
            return new Deletion(membership, []);
        }
    }

    // Queues the accepted deletion for the worker, so Delete files never waits
    // for subtitle work in progress.
    internal void Delete(Deletion deletion, DeletionMode mode)
    {
        lock (_deletions)
            foreach (var path in deletion.Paths)
                _deletions.Add((path.Path, path.Language, deletion.Origins, mode));
        Wake();
    }

    private bool HasDeletions
    {
        get
        {
            lock (_deletions)
                return _deletions.Count > 0;
        }
    }

    private async Task DeleteAccepted()
    {
        CancellationToken cancellation;
        lock (_requestSync)
            cancellation = _requests.Token;
        (string Path, string Language, IReadOnlySet<string> Origins, DeletionMode Mode)[] deletions;
        lock (_deletions)
            deletions = _deletions.ToArray();
        if (deletions.Length == 0)
            return;
        foreach (var deletion in deletions)
        {
            try
            {
                var path = deletion.Path;
                var access = await PathAccess(path, cancellation).ConfigureAwait(false);
                if (access == SubtitlePathAccess.Pending)
                    return;
                var removing = await Run(
                        connection =>
                        {
                            using var command = connection.CreateCommand();
                            command.CommandText = "SELECT origin FROM contributions;";
                            using var reader = command.ExecuteReader();
                            while (reader.Read())
                                if (deletion.Origins.Contains(reader.GetString(0)))
                                    return true;
                            return false;
                        },
                        cancellation
                    )
                    .ConfigureAwait(false);
                if (removing)
                    continue;
                // Read targets after membership, so a withdrawn origin cannot
                // protect its old caption and consume the accepted deletion.
                var targets = await Targets(cancellation).ConfigureAwait(false);
                if (
                    access != SubtitlePathAccess.Protected
                    && !targets.Any(target =>
                        StringComparer.OrdinalIgnoreCase.Equals(target.Path, path)
                    )
                    && SubtitleFile.Exists(path)
                )
                {
                    if (deletion.Mode == DeletionMode.Permanent)
                        await Task.Run(() => File.Delete(path), cancellation).ConfigureAwait(false);
                    else
                        await RecycleBin.Delete(path, cancellation).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                System.Diagnostics.Debug.WriteLine($"Subtitle file deletion cancelled ({deletion.Language}).");
                return;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                ReportFile(deletion.Path, SubtitleFailure.Delete);
            }
            lock (_deletions)
                _deletions.Remove(deletion);
        }
    }

    private sealed record Association(
        string Path,
        string Language,
        string Origin,
        int Index,
        bool Created,
        string Movie
    );
}

internal sealed record Deletion(IReadOnlySet<string> Origins, (string Path, string Language)[] Paths);
