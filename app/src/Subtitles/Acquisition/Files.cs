using Microsoft.Data.Sqlite;
using Syno.TinyTorrent.Helpers;
using Syno.TinyTorrent.Models;

namespace Syno.TinyTorrent.Subtitles;

internal sealed partial class Acquisition
{
    private readonly List<(string Path, string Language, string[] Origins, DeletionMode Mode)> _deletions = [];

    private async Task ReconcileFiles(Target[] targets, CancellationToken cancellation)
    {
        var associations = await database.Run(connection =>
        {
            var rows = new List<Association>();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT s.path,s.language,s.origin,s.file_index,o.created
                FROM subtitle_sources s LEFT JOIN subtitle_outputs o USING(path,language);
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
                rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3),
                    !reader.IsDBNull(4) && reader.GetBoolean(4)));
            return rows;
        }, cancellation).ConfigureAwait(false);
        foreach (var group in associations.GroupBy(value => value.Path, StringComparer.OrdinalIgnoreCase))
        {
            var destinations = targets.Where(target => group.Any(source => source.Language == target.Language &&
                target.Videos.Any(video => video.Origin == source.Origin && video.Index == source.Index))).ToArray();
            var moved = destinations.Where(target => !StringComparer.OrdinalIgnoreCase.Equals(target.Path, group.Key)).ToArray();
            if (moved.Length == 0)
                continue;
            var retained = targets.Any(target => StringComparer.OrdinalIgnoreCase.Equals(target.Path, group.Key));
            var destination = moved.Length == 1 && !retained && !moved[0].IsAmbiguous ? moved[0] : null;
            var created = group.Any(source => source.Created);
            var recorded = false;
            if (destination is not null && created)
            {
                var sourceAccess = await PathAccess(group.Key, cancellation).ConfigureAwait(false);
                var destinationAccess = await PathAccess(destination.Path, cancellation).ConfigureAwait(false);
                if (sourceAccess == SubtitlePathAccess.Pending || destinationAccess == SubtitlePathAccess.Pending)
                    continue;
                try
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (sourceAccess == SubtitlePathAccess.Allowed && destinationAccess == SubtitlePathAccess.Allowed &&
                        await database.Run(connection => _connected && Current(connection, destination.Videos), cancellation).ConfigureAwait(false) &&
                        SubtitleFile.Exists(group.Key))
                    {
                        File.Move(group.Key, destination.Path, overwrite: false);
                        recorded = true;
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    await ReportFile(group.Key, SubtitleFailure.Save).ConfigureAwait(false);
                }
            }
            var transferred = await database.Run(connection =>
            {
                var paths = new List<string>();
                foreach (var target in targets.Where(target => StringComparer.OrdinalIgnoreCase.Equals(target.Path, group.Key)))
                    if (Current(connection, target.Videos))
                        Associate(connection, target);
                foreach (var target in moved)
                {
                    if (!Current(connection, target.Videos) && !(destination == target && recorded))
                        continue;
                    Associate(connection, target);
                    if (destination == target && recorded)
                    {
                        Execute(connection, "INSERT OR IGNORE INTO subtitle_outputs(path,language,created) VALUES($path,$language,1);",
                            ("$path", target.Path), ("$language", target.Language));
                        foreach (var source in group.Where(source => source.Language == target.Language))
                            Execute(connection, """
                                INSERT OR IGNORE INTO subtitle_sources(path,language,origin,file_index)
                                SELECT $new,language,origin,file_index FROM subtitle_sources
                                WHERE path=$old AND language=$language AND origin=$origin AND file_index=$index;
                                """, ("$new", target.Path), ("$old", group.Key), ("$language", source.Language),
                                ("$origin", source.Origin), ("$index", source.Index));
                        Execute(connection, "DELETE FROM subtitle_failures WHERE path=$old OR path=$new;",
                            ("$old", group.Key), ("$new", target.Path));
                    }
                    Execute(connection, """
                        INSERT OR IGNORE INTO subtitle_jobs(path,language,stage,outcome,retry_at,attempts)
                        SELECT $new,language,stage,outcome,retry_at,attempts FROM subtitle_jobs
                        WHERE path=$old AND language=$language;
                        """, ("$new", target.Path), ("$old", group.Key), ("$language", target.Language));
                    paths.Add(target.Path);
                    foreach (var video in target.Videos)
                        Execute(connection, """
                            DELETE FROM subtitle_sources WHERE path=$old AND language=$language
                                AND origin=$origin AND file_index=$index;
                            """, ("$old", group.Key), ("$language", target.Language),
                            ("$origin", video.Origin), ("$index", video.Index));
                }
                Execute(connection, Cleanup);
                return paths;
            }, recorded ? CancellationToken.None : cancellation).ConfigureAwait(false);
            if (_finding.Contains(group.Key))
            {
                foreach (var path in transferred)
                    if (_finding.Add(path))
                        FindTotal++;
                if (!retained && transferred.Count > 0 && _finding.Remove(group.Key))
                    FindTotal--;
            }
        }
    }

    private Task<SubtitlePathAccess> PathAccess(string path, CancellationToken cancellation) =>
        database.Run(connection =>
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
        }, cancellation);

    internal async Task<Deletion> CaptureDeletion(string[] origins)
    {
        try
        {
            var paths = await database.Run(connection =>
            {
                var result = new List<(string Path, string Language)>();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT s.path,s.origin,s.language FROM subtitle_sources s
                    JOIN subtitle_outputs o USING(path,language) WHERE o.created=1;
                    """;
                using var reader = command.ExecuteReader();
                var rows = new List<(string Path, string Origin, string Language)>();
                while (reader.Read())
                    rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
                foreach (var group in rows.GroupBy(row => row.Path, StringComparer.OrdinalIgnoreCase))
                    if (group.All(row => origins.Contains(row.Origin)))
                        result.Add((group.Key, group.First().Language));
                return result.ToArray();
            }, _lifetime.Token).ConfigureAwait(false);
            return new Deletion(origins, paths);
        }
        catch (Exception error) when (error is SqliteException or OperationCanceledException)
        {
            Failure = SubtitleFailure.Database;
            Notify();
            return new Deletion(origins, []);
        }
    }

    internal async Task Delete(Deletion deletion, DeletionMode mode)
    {
        await _operation.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var path in deletion.Paths)
                _deletions.Add((path.Path, path.Language, deletion.Origins, mode));
            await DeleteAccepted().ConfigureAwait(false);
            await RefreshCounts().ConfigureAwait(false);
        }
        catch (Exception error) when (error is SqliteException or OperationCanceledException)
        {
            if (error is SqliteException)
                Failure = SubtitleFailure.Database;
            Notify();
        }
        finally
        {
            _operation.Release();
        }
    }

    private async Task DeleteAccepted()
    {
        CancellationToken cancellation;
        lock (_requestSync)
            cancellation = _requests.Token;
        if (_deletions.Count == 0)
            return;
        var targets = await Targets(cancellation).ConfigureAwait(false);
        foreach (var deletion in _deletions.ToArray())
        {
            try
            {
                var path = deletion.Path;
                var access = await PathAccess(path, cancellation).ConfigureAwait(false);
                if (access == SubtitlePathAccess.Pending)
                    return;
                var removing = await database.Run(connection =>
                {
                    using var command = connection.CreateCommand();
                    command.CommandText = "SELECT origin FROM contributions;";
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                        if (deletion.Origins.Contains(reader.GetString(0)))
                            return true;
                    return false;
                }, cancellation).ConfigureAwait(false);
                if (removing)
                    continue;
                if (access == SubtitlePathAccess.Protected || targets.Any(target =>
                    StringComparer.OrdinalIgnoreCase.Equals(target.Path, path)))
                {
                    _deletions.Remove(deletion);
                    continue;
                }
                if (SubtitleFile.Exists(path))
                {
                    if (deletion.Mode == DeletionMode.Permanent)
                        await Task.Run(() => File.Delete(path), cancellation).ConfigureAwait(false);
                    else
                        await RecycleBin.Delete(path, cancellation).ConfigureAwait(false);
                }
                _deletions.Remove(deletion);
                await database.Run(connection =>
                {
                    Execute(connection, "DELETE FROM subtitle_failures WHERE path=$path;", ("$path", path));
                    return true;
                }, CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                _deletions.Remove(deletion);
                await ReportFile(deletion.Path, SubtitleFailure.Save).ConfigureAwait(false);
            }
        }
    }

    private sealed record Association(string Path, string Language, string Origin, int Index, bool Created);
}

internal sealed record Deletion(string[] Origins, (string Path, string Language)[] Paths);
