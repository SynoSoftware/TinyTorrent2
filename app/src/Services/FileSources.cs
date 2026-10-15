using Microsoft.Data.Sqlite;
using Syno.TinyTorrent.Models;

namespace Syno.TinyTorrent.Services;

internal sealed class FileSources(Database database)
{
    internal Task Invalidate(CancellationToken cancellation) =>
        database.Run(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText =
                    "UPDATE contributions SET collected=0, checked=0;" + Database.Invalidate;
                return command.ExecuteNonQuery();
            },
            cancellation
        );

    // Collected files stay current until each new read replaces them.
    internal Task Reread(CancellationToken cancellation) =>
        database.Run(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE contributions SET checked=0;";
                return command.ExecuteNonQuery();
            },
            cancellation
        );

    internal Task<Contribution?> Next(CancellationToken cancellation) =>
        database.Run(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT origin, name, complete, settled, save_path, stamp FROM contributions
                    WHERE checked=0 OR (complete=0 AND checked<$active) OR checked<$idle
                    ORDER BY collected, checked LIMIT 1;
                    """;
                command.Parameters.AddWithValue("$active", Environment.TickCount64 - 5000);
                command.Parameters.AddWithValue("$idle", Environment.TickCount64 - 60000);
                using var reader = command.ExecuteReader();
                return reader.Read()
                    ? new Contribution(
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.GetBoolean(2),
                        reader.GetBoolean(3)
                    )
                    {
                        SavePath = reader.GetString(4),
                        Stamp = reader.GetString(5),
                    }
                    : null;
            },
            cancellation
        );

    internal Task<bool> IsReady(CancellationToken cancellation) =>
        database.Run(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText =
                    "SELECT NOT EXISTS(SELECT 1 FROM contributions WHERE checked=0);";
                return Convert.ToInt64(command.ExecuteScalar()) != 0;
            },
            cancellation
        );

    internal Task MarkChecked(Contribution contribution, CancellationToken cancellation) =>
        database.Run(connection => MarkChecked(connection, contribution), cancellation);

    private static int MarkChecked(SqliteConnection connection, Contribution contribution)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE contributions SET checked=$checked WHERE origin=$origin AND stamp=$stamp;";
        command.Parameters.AddWithValue("$checked", Environment.TickCount64);
        command.Parameters.AddWithValue("$origin", contribution.Origin);
        command.Parameters.AddWithValue("$stamp", contribution.Stamp);
        return command.ExecuteNonQuery();
    }

    internal Task<bool> Reconcile(
        IReadOnlyList<Contribution> contributions,
        CancellationToken cancellation
    ) =>
        database.Run(
            connection =>
            {
                using var current = connection.CreateCommand();
                current.CommandText = "DELETE FROM present;";
                current.ExecuteNonQuery();
                using var insert = connection.CreateCommand();
                insert.CommandText = "INSERT INTO present VALUES ($origin);";
                var origin = insert.Parameters.Add("$origin", SqliteType.Text);
                using var update = connection.CreateCommand();
                update.CommandText = """
                    INSERT INTO contributions (origin, name, complete, settled, save_path, stamp)
                    VALUES ($origin, $name, $complete, $settled, $path, $stamp)
                    ON CONFLICT(origin) DO UPDATE SET name=excluded.name,
                        complete=excluded.complete, settled=excluded.settled, save_path=excluded.save_path,
                        checked=CASE WHEN stamp=excluded.stamp THEN checked ELSE 0 END,
                        collected=CASE WHEN stamp=excluded.stamp THEN collected ELSE 0 END,
                        stamp=excluded.stamp
                    WHERE stamp<>excluded.stamp OR name<>excluded.name;
                    """;
                update.Parameters.Add("$origin", SqliteType.Text);
                update.Parameters.Add("$name", SqliteType.Text);
                update.Parameters.Add("$complete", SqliteType.Integer);
                update.Parameters.Add("$settled", SqliteType.Integer);
                update.Parameters.Add("$path", SqliteType.Text);
                update.Parameters.Add("$stamp", SqliteType.Text);
                var changed = false;
                foreach (var contribution in contributions)
                {
                    cancellation.ThrowIfCancellationRequested();
                    origin.Value = contribution.Origin;
                    insert.ExecuteNonQuery();
                    update.Parameters["$origin"].Value = contribution.Origin;
                    update.Parameters["$name"].Value = contribution.Name;
                    update.Parameters["$complete"].Value = contribution.Complete;
                    update.Parameters["$settled"].Value = contribution.Settled;
                    update.Parameters["$path"].Value = contribution.SavePath;
                    update.Parameters["$stamp"].Value = contribution.Stamp;
                    changed |= update.ExecuteNonQuery() != 0;
                }
                // known_torrents lists every torrent the window has seen finished,
                // and only a torrent missing from it gets automatic subtitle
                // searches, so a download that finishes after a restart is still
                // searched. The first list is taken as known, even when empty,
                // so a new database does not search the whole collection. A removed
                // torrent is forgotten, so adding it again is new.
                using var history = connection.CreateCommand();
                history.CommandText = "SELECT initialized FROM source_state WHERE singleton=1;";
                var recorded = Convert.ToInt64(history.ExecuteScalar()) != 0;
                history.CommandText = """
                    INSERT OR IGNORE INTO new_origins SELECT origin FROM present
                    WHERE $recorded AND origin NOT IN (SELECT origin FROM known_torrents);
                    DELETE FROM new_origins WHERE origin NOT IN (SELECT origin FROM present);
                    DELETE FROM known_torrents WHERE origin NOT IN (SELECT origin FROM present);
                    INSERT OR IGNORE INTO known_torrents SELECT origin FROM contributions
                    WHERE origin IN (SELECT origin FROM present) AND (complete=1 OR NOT $recorded);
                    UPDATE source_state SET initialized=1 WHERE singleton=1 AND initialized=0;
                    """;
                history.Parameters.AddWithValue("$recorded", recorded);
                history.ExecuteNonQuery();
                current.CommandText =
                    Database.Reconcile
                    + """
                        DELETE FROM source_files WHERE origin NOT IN (SELECT origin FROM present);
                        DELETE FROM contributions WHERE origin NOT IN (SELECT origin FROM present);
                        DELETE FROM locations WHERE entry_id NOT IN (SELECT entry_id FROM source_files);
                        """;
                changed |= current.ExecuteNonQuery() != 0;
                current.CommandText = Database.Cleanup;
                current.ExecuteNonQuery();
                if (changed)
                {
                    current.CommandText = Database.Invalidate;
                    current.ExecuteNonQuery();
                }
                return changed;
            },
            cancellation
        );

    internal Task<bool> Replace(
        Contribution contribution,
        IReadOnlyList<SourceFile> files,
        CancellationToken cancellation
    ) =>
        database.Run(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText =
                    "SELECT collected FROM contributions WHERE origin=$origin AND stamp=$stamp;";
                command.Parameters.AddWithValue("$origin", contribution.Origin);
                command.Parameters.AddWithValue("$stamp", contribution.Stamp);
                if (command.ExecuteScalar() is not long collected)
                    return false;

                if (
                    collected != 0
                    && Unchanged(connection, contribution.Origin, files, cancellation)
                )
                {
                    MarkChecked(connection, contribution);
                    return false;
                }

                // Share saved choices and subtitle ownership before changed
                // membership separates or removes their current contributors.
                command.CommandText = Database.Reconcile;
                var changed = command.ExecuteNonQuery() != 0;
                command.CommandText = "DELETE FROM received;";
                command.ExecuteNonQuery();
                using var received = connection.CreateCommand();
                received.CommandText = "INSERT INTO received VALUES ($index);";
                received.Parameters.Add("$index", SqliteType.Integer);
                using var write = connection.CreateCommand();
                write.CommandText = """
                    INSERT INTO source_files
                        (origin, file_index, entry_id, name, path, size, complete, wanted, kind, title, type, year, search)
                    VALUES ($origin, $index, $entry, $name, $path, $size, $complete, $wanted, $kind, $title, $type, $year, $search)
                    ON CONFLICT(origin, file_index) DO UPDATE SET
                        entry_id=excluded.entry_id, name=excluded.name, path=excluded.path,
                        size=excluded.size, complete=excluded.complete, wanted=excluded.wanted,
                        kind=excluded.kind, title=excluded.title, type=excluded.type, year=excluded.year,
                        search=excluded.search
                    WHERE (entry_id,name,path,size,complete,wanted) <>
                        (excluded.entry_id,excluded.name,excluded.path,excluded.size,excluded.complete,excluded.wanted);
                    """;
                foreach (var name in new[] { "origin", "name", "path", "title", "type", "search" })
                    write.Parameters.Add("$" + name, SqliteType.Text);
                foreach (
                    var name in new[]
                    {
                        "index",
                        "entry",
                        "size",
                        "complete",
                        "wanted",
                        "kind",
                        "year",
                    }
                )
                    write.Parameters.Add("$" + name, SqliteType.Integer);
                changed |= collected == 0;
                foreach (var file in files)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var path = Path.GetFullPath(file.FinalPath);
                    var entryId = Locate(connection, contribution.Origin, file.Index, path);
                    var name = Path.GetFileName(path);
                    var release = FileName.Interpret(name);
                    received.Parameters["$index"].Value = file.Index;
                    received.ExecuteNonQuery();
                    write.Parameters["$origin"].Value = contribution.Origin;
                    write.Parameters["$index"].Value = file.Index;
                    write.Parameters["$entry"].Value = entryId;
                    write.Parameters["$name"].Value = name;
                    write.Parameters["$path"].Value = file.Path;
                    write.Parameters["$size"].Value = file.Size;
                    write.Parameters["$complete"].Value = IsComplete(file, path);
                    write.Parameters["$wanted"].Value = file.Wanted;
                    var kind = FileName.Kind(path);
                    write.Parameters["$kind"].Value = (int)kind;
                    write.Parameters["$title"].Value = release.Title;
                    // The name identifies a video before any provider does: a
                    // season makes it an episode, and every other video is a movie.
                    write.Parameters["$type"].Value =
                        kind != FileKind.Video ? string.Empty
                        : release.Season is null ? "movie"
                        : "episode";
                    write.Parameters["$year"].Value =
                        kind != FileKind.Video
                            ? DBNull.Value
                            : (object?)release.Year ?? DBNull.Value;
                    write.Parameters["$search"].Value = FileName.Normalize(
                        path + " " + release.Title
                    );
                    changed |= write.ExecuteNonQuery() != 0;
                }
                command.CommandText = """
                    DELETE FROM source_files WHERE origin=$origin AND file_index NOT IN (SELECT file_index FROM received);
                    DELETE FROM locations WHERE entry_id NOT IN (SELECT entry_id FROM source_files);
                    """;
                changed |= command.ExecuteNonQuery() != 0;
                command.CommandText =
                    "UPDATE contributions SET collected=1, checked=$checked WHERE origin=$origin;";
                command.Parameters.AddWithValue("$checked", Environment.TickCount64);
                command.ExecuteNonQuery();
                if (changed)
                {
                    command.CommandText =
                        Database.Reconcile + Database.Cleanup + Database.Invalidate;
                    command.ExecuteNonQuery();
                }
                return changed;
            },
            cancellation
        );

    private static bool Unchanged(
        SqliteConnection connection,
        string origin,
        IReadOnlyList<SourceFile> files,
        CancellationToken cancellation
    )
    {
        var incoming = files.ToDictionary(file => file.Index);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT f.file_index,f.path,f.name,l.path,f.size,f.complete,f.wanted
            FROM source_files f JOIN locations l USING(entry_id) WHERE f.origin=$origin;
            """;
        command.Parameters.AddWithValue("$origin", origin);
        using var reader = command.ExecuteReader();
        var count = 0;
        while (reader.Read())
        {
            cancellation.ThrowIfCancellationRequested();
            if (!incoming.TryGetValue(reader.GetInt32(0), out var file))
                return false;
            var path = Path.GetFullPath(file.FinalPath);
            if (
                reader.GetString(1) != file.Path
                || reader.GetString(2) != Path.GetFileName(path)
                || !StringComparer.OrdinalIgnoreCase.Equals(reader.GetString(3), path)
                || reader.GetInt64(4) != file.Size
                || reader.GetBoolean(5) != IsComplete(file, path)
                || reader.GetBoolean(6) != file.Wanted
            )
                return false;
            count++;
        }
        return count == files.Count;
    }

    private static bool IsComplete(SourceFile file, string path) =>
        file.Complete && StringComparer.OrdinalIgnoreCase.Equals(file.Path, path);

    private static long Locate(SqliteConnection connection, string origin, int index, string path)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT entry_id FROM locations WHERE path=$path COLLATE PATH;";
        command.Parameters.AddWithValue("$path", path);
        if (command.ExecuteScalar() is long existing)
            return existing;

        command.CommandText = """
            SELECT f.entry_id FROM source_files f
            WHERE origin=$origin AND file_index=$index;
            """;
        command.Parameters.AddWithValue("$origin", origin);
        command.Parameters.AddWithValue("$index", index);
        if (command.ExecuteScalar() is long previous)
        {
            command.CommandText = "SELECT path FROM locations WHERE entry_id=$entry;";
            command.Parameters.AddWithValue("$entry", previous);
            var oldPath = (string)command.ExecuteScalar()!;
            command.CommandText = "UPDATE locations SET path=$path WHERE entry_id=$entry;";
            command.ExecuteNonQuery();
            command.CommandText = """
                INSERT INTO locations(path)
                SELECT $old WHERE EXISTS(SELECT 1 FROM source_files
                    WHERE entry_id=$entry AND (origin<>$origin OR file_index<>$index));
                UPDATE source_files SET entry_id=(SELECT entry_id FROM locations WHERE path=$old COLLATE PATH)
                WHERE entry_id=$entry AND (origin<>$origin OR file_index<>$index);
                """;
            command.Parameters.AddWithValue("$old", oldPath);
            command.ExecuteNonQuery();
            return previous;
        }
        command.CommandText = "INSERT INTO locations(path) VALUES ($path) RETURNING entry_id;";
        return (long)command.ExecuteScalar()!;
    }
}

internal sealed record Contribution(string Origin, string Name, bool Complete, bool Settled)
{
    internal string SavePath { get; init; } = string.Empty;
    internal string Stamp { get; init; } = string.Empty;
}

internal sealed record SourceFile(int Index, string Path, string FinalPath, long Size)
{
    internal bool Complete { get; init; }
    internal bool Wanted { get; init; }
}
