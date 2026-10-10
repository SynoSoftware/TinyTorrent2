using Microsoft.Data.Sqlite;
using Syno.TinyTorrent.Models;

namespace Syno.TinyTorrent.Services;

internal sealed class FileSources(Database database, string cleanup, string reconcile, string invalidate)
{
    internal const string Schema = """
        CREATE TEMP TABLE contributions (
            origin TEXT PRIMARY KEY, name TEXT NOT NULL, complete INTEGER NOT NULL,
            settled INTEGER NOT NULL, collected INTEGER NOT NULL DEFAULT 0,
            save_path TEXT NOT NULL, stamp TEXT NOT NULL, checked INTEGER NOT NULL DEFAULT 0
        );
        CREATE TEMP TABLE locations (
            entry_id INTEGER PRIMARY KEY AUTOINCREMENT, path TEXT COLLATE PATH NOT NULL UNIQUE
        );
        CREATE TEMP TABLE source_files (
            origin TEXT NOT NULL, file_index INTEGER NOT NULL, entry_id INTEGER NOT NULL,
            name TEXT NOT NULL, path TEXT NOT NULL, size INTEGER NOT NULL,
            complete INTEGER NOT NULL, wanted INTEGER NOT NULL, kind INTEGER NOT NULL,
            title TEXT NOT NULL, search TEXT NOT NULL,
            PRIMARY KEY (origin, file_index)
        );
        CREATE INDEX source_locations ON source_files(entry_id);
        CREATE TEMP VIEW collected_files AS
        SELECT f.* FROM source_files f JOIN contributions c USING (origin)
        WHERE c.collected=1;
        CREATE TEMP VIEW pending_origins AS
        SELECT origin FROM contributions WHERE collected=0;
        CREATE TEMP VIEW current_files AS
        SELECT l.entry_id, l.path, f.name, f.size, f.kind, f.title, f.search,
            MAX(f.complete AND c.collected) AS complete,
            MAX(f.complete AND c.complete AND c.settled AND c.collected) AS readable
        FROM locations l JOIN source_files f USING (entry_id)
        JOIN contributions c USING (origin)
        GROUP BY l.entry_id;
        """;

    internal Task Invalidate(CancellationToken cancellation) => database.Run(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE contributions SET collected=0, checked=0;" + invalidate;
        return command.ExecuteNonQuery();
    }, cancellation);

    internal Task<Contribution?> Next(CancellationToken cancellation) => database.Run(connection =>
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
        return reader.Read() ? new Contribution(reader.GetString(0), reader.GetString(1),
            reader.GetBoolean(2), reader.GetBoolean(3))
        {
            SavePath = reader.GetString(4),
            Stamp = reader.GetString(5),
        } : null;
    }, cancellation);

    internal Task<bool> IsReady(CancellationToken cancellation) => database.Run(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT NOT EXISTS(SELECT 1 FROM contributions WHERE checked=0);";
        return Convert.ToInt64(command.ExecuteScalar()) != 0;
    }, cancellation);

    internal Task MarkChecked(Contribution contribution, CancellationToken cancellation) => database.Run(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE contributions SET checked=$checked WHERE origin=$origin AND stamp=$stamp;";
        command.Parameters.AddWithValue("$checked", Environment.TickCount64);
        command.Parameters.AddWithValue("$origin", contribution.Origin);
        command.Parameters.AddWithValue("$stamp", contribution.Stamp);
        return command.ExecuteNonQuery();
    }, cancellation);

    internal Task<bool> Reconcile(IReadOnlyList<Contribution> contributions, CancellationToken cancellation) =>
        database.Run(connection =>
        {
            using var current = connection.CreateCommand();
            current.CommandText = "CREATE TEMP TABLE IF NOT EXISTS present (origin TEXT PRIMARY KEY); DELETE FROM present;";
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
            current.CommandText = """
                DELETE FROM source_files WHERE origin NOT IN (SELECT origin FROM present);
                DELETE FROM contributions WHERE origin NOT IN (SELECT origin FROM present);
                DELETE FROM locations WHERE entry_id NOT IN (SELECT entry_id FROM source_files);
                """;
            changed |= current.ExecuteNonQuery() != 0;
            current.CommandText = cleanup;
            current.ExecuteNonQuery();
            if (changed)
            {
                current.CommandText = invalidate;
                current.ExecuteNonQuery();
            }
            return changed;
        }, cancellation);

    internal Task<bool> Replace(Contribution contribution, IReadOnlyList<SourceFile> files, CancellationToken cancellation) =>
        database.Run(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM contributions WHERE origin=$origin AND stamp=$stamp;";
            command.Parameters.AddWithValue("$origin", contribution.Origin);
            command.Parameters.AddWithValue("$stamp", contribution.Stamp);
            if (Convert.ToInt64(command.ExecuteScalar()) == 0)
                return false;

            command.CommandText = "CREATE TEMP TABLE IF NOT EXISTS received (file_index INTEGER PRIMARY KEY); DELETE FROM received;";
            command.ExecuteNonQuery();
            using var received = connection.CreateCommand();
            received.CommandText = "INSERT INTO received VALUES ($index);";
            received.Parameters.Add("$index", SqliteType.Integer);
            using var write = connection.CreateCommand();
            write.CommandText = """
                INSERT INTO source_files
                    (origin, file_index, entry_id, name, path, size, complete, wanted, kind, title, search)
                VALUES ($origin, $index, $entry, $name, $path, $size, $complete, $wanted, $kind, $title, $search)
                ON CONFLICT(origin, file_index) DO UPDATE SET
                    entry_id=excluded.entry_id, name=excluded.name, path=excluded.path,
                    size=excluded.size, complete=excluded.complete, wanted=excluded.wanted,
                    kind=excluded.kind, title=excluded.title, search=excluded.search
                WHERE (entry_id,name,path,size,complete,wanted) <>
                    (excluded.entry_id,excluded.name,excluded.path,excluded.size,excluded.complete,excluded.wanted);
                """;
            foreach (var name in new[] { "origin", "name", "path", "title", "search" })
                write.Parameters.Add("$" + name, SqliteType.Text);
            foreach (var name in new[] { "index", "entry", "size", "complete", "wanted", "kind" })
                write.Parameters.Add("$" + name, SqliteType.Integer);
            command.CommandText = "SELECT collected=0 FROM contributions WHERE origin=$origin;";
            var changed = Convert.ToInt64(command.ExecuteScalar()) != 0;
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
                write.Parameters["$complete"].Value = file.Complete &&
                    StringComparer.OrdinalIgnoreCase.Equals(file.Path, path);
                write.Parameters["$wanted"].Value = file.Wanted;
                write.Parameters["$kind"].Value = (int)FileName.Kind(path);
                write.Parameters["$title"].Value = release.Title;
                write.Parameters["$search"].Value = FileName.Normalize(path + " " + release.Title);
                changed |= write.ExecuteNonQuery() != 0;
            }
            command.CommandText = """
                DELETE FROM source_files WHERE origin=$origin AND file_index NOT IN (SELECT file_index FROM received);
                DELETE FROM locations WHERE entry_id NOT IN (SELECT entry_id FROM source_files);
                """;
            changed |= command.ExecuteNonQuery() != 0;
            command.CommandText = "UPDATE contributions SET collected=1, checked=$checked WHERE origin=$origin;";
            command.Parameters.AddWithValue("$checked", Environment.TickCount64);
            command.ExecuteNonQuery();
            command.CommandText = reconcile + cleanup;
            command.ExecuteNonQuery();
            if (changed)
            {
                command.CommandText = invalidate;
                command.ExecuteNonQuery();
            }
            return changed;
        }, cancellation);

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
