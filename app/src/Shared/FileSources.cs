using Microsoft.Data.Sqlite;
using Syno.TinyTorrent.Shared.Storage;

namespace Syno.TinyTorrent.Shared;

internal sealed class FileSources(Database database)
{
    internal Task Invalidate(CancellationToken cancellation) =>
        database.Run(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE file_reads SET collected=0, checked=0;";
                return command.ExecuteNonQuery();
            },
            cancellation
        );

    // Confirmed files stay confirmed until each new read replaces them.
    internal Task Reread(CancellationToken cancellation) =>
        database.Run(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE file_reads SET checked=0;";
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
                    SELECT r.origin, r.name, r.complete, r.settled, r.save_path, r.stamp
                    FROM file_reads r LEFT JOIN contributions c USING(origin)
                    WHERE r.checked=0 OR (r.complete=0 AND r.checked<$active) OR r.checked<$idle
                    ORDER BY r.checked<>0, c.origin IS NOT NULL AND c.stamp=r.stamp,
                        r.collected, r.checked LIMIT 1;
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
                    "SELECT NOT EXISTS(SELECT 1 FROM file_reads WHERE checked=0);";
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
            "UPDATE file_reads SET checked=$checked WHERE origin=$origin AND stamp=$stamp;";
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
                    INSERT INTO file_reads (origin, name, complete, settled, save_path, stamp)
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
                    update.ExecuteNonQuery();
                }
                // known_torrents lists every torrent whose subtitle completion
                // search is decided, and new_origins the present torrents
                // missing from it. Subtitles move a torrent from one to the
                // other when they decide, so a download that finishes after a
                // restart, or before a window close let them decide, is still
                // searched, once. The first list is taken as known, even when
                // empty, so a new database does not search the whole collection.
                // A removed torrent is forgotten, so adding it again is new.
                using var history = connection.CreateCommand();
                history.CommandText = "SELECT initialized FROM source_state WHERE singleton=1;";
                var recorded = Convert.ToInt64(history.ExecuteScalar()) != 0;
                history.CommandText = """
                    INSERT OR IGNORE INTO new_origins SELECT origin FROM present
                    WHERE $recorded AND origin NOT IN (SELECT origin FROM known_torrents);
                    DELETE FROM new_origins WHERE origin NOT IN (SELECT origin FROM present);
                    DELETE FROM known_torrents WHERE origin NOT IN (SELECT origin FROM present);
                    INSERT OR IGNORE INTO known_torrents SELECT origin FROM present
                    WHERE NOT $recorded;
                    UPDATE source_state SET initialized=1 WHERE singleton=1 AND initialized=0;
                    """;
                history.Parameters.AddWithValue("$recorded", recorded);
                history.ExecuteNonQuery();
                current.CommandText = Database.Reconcile;
                current.ExecuteNonQuery();
                // The torrent list withdraws torrents only; the disk
                // contribution answers to disk discovery.
                current.CommandText = """
                    DELETE FROM source_files WHERE origin NOT IN (SELECT origin FROM present)
                        AND origin NOT IN (SELECT origin FROM contributions WHERE data_source='disk');
                    DELETE FROM contributions WHERE data_source='torrent'
                        AND origin NOT IN (SELECT origin FROM present);
                    DELETE FROM locations WHERE entry_id NOT IN (SELECT entry_id FROM source_files);
                    """;
                var changed = current.ExecuteNonQuery() != 0;
                current.CommandText =
                    "DELETE FROM file_reads WHERE origin NOT IN (SELECT origin FROM present);";
                current.ExecuteNonQuery();
                current.CommandText = Database.Cleanup;
                current.ExecuteNonQuery();
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
                command.CommandText = """
                    SELECT c.stamp FROM file_reads r LEFT JOIN contributions c USING(origin)
                    WHERE r.origin=$origin AND r.stamp=$stamp;
                    """;
                command.Parameters.AddWithValue("$origin", contribution.Origin);
                command.Parameters.AddWithValue("$stamp", contribution.Stamp);
                var stamp = command.ExecuteScalar();
                if (stamp is null)
                    return false;

                if (
                    stamp is string
                    && Unchanged(connection, contribution.Origin, files, cancellation)
                )
                {
                    return Confirm(connection, contribution);
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
                using var write = new FileWriter(connection);
                changed |= stamp is not string;
                foreach (var file in files)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var path = Path.GetFullPath(file.FinalPath);
                    received.Parameters["$index"].Value = file.Index;
                    received.ExecuteNonQuery();
                    changed |= write.Save(
                        contribution.Origin,
                        file.Index,
                        file.Path,
                        path,
                        file.Size,
                        IsComplete(file, path),
                        file.Wanted
                    );
                }
                command.CommandText = """
                    DELETE FROM source_files WHERE origin=$origin AND file_index NOT IN (SELECT file_index FROM received);
                    DELETE FROM locations WHERE entry_id NOT IN (SELECT entry_id FROM source_files);
                    """;
                changed |= command.ExecuteNonQuery() != 0;
                changed |= Confirm(connection, contribution);
                if (changed)
                {
                    command.CommandText = Database.Reconcile + Database.Cleanup;
                    command.ExecuteNonQuery();
                }
                return changed;
            },
            cancellation
        );

    private static bool Confirm(SqliteConnection connection, Contribution contribution)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO contributions(origin,name,stamp) VALUES($origin,$name,$stamp)
            ON CONFLICT(origin) DO UPDATE SET name=excluded.name, stamp=excluded.stamp
            WHERE name<>excluded.name OR stamp<>excluded.stamp;
            """;
        command.Parameters.AddWithValue("$origin", contribution.Origin);
        command.Parameters.AddWithValue("$name", contribution.Name);
        command.Parameters.AddWithValue("$stamp", contribution.Stamp);
        var changed = command.ExecuteNonQuery() != 0;
        command.CommandText =
            "UPDATE file_reads SET collected=1, checked=$checked WHERE origin=$origin AND stamp=$stamp;";
        command.Parameters.AddWithValue("$checked", Environment.TickCount64);
        command.ExecuteNonQuery();
        return changed;
    }

    internal Task<bool> HasCache(CancellationToken cancellation) =>
        database.Run(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT EXISTS(SELECT 1 FROM contributions);";
                return Convert.ToInt64(command.ExecuteScalar()) != 0;
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
            SELECT f.file_index,f.path,f.name,l.path,f.size,f.complete,f.wanted,f.kind
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
                || reader.GetInt32(7) != (int)FileName.Kind(path)
            )
                return false;
            count++;
        }
        return count == files.Count;
    }

    private static bool IsComplete(SourceFile file, string path) =>
        file.Complete && StringComparer.OrdinalIgnoreCase.Equals(file.Path, path);

    // The disk contribution is addressed by this origin; its behaviour is
    // selected by its data source.
    private const string Disk = "disk";

    internal Task<bool> ReadDiscovery(CancellationToken cancellation) =>
        database.Run(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT discovery FROM library_settings WHERE singleton=1;";
                return Convert.ToInt64(command.ExecuteScalar()) != 0;
            },
            cancellation
        );

    internal Task EnableDiscovery(CancellationToken cancellation) =>
        database.Run(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE library_settings SET discovery=1 WHERE singleton=1;";
                return command.ExecuteNonQuery();
            },
            cancellation
        );

    // Saves the option off and withdraws the disk contribution in one job,
    // so no refresh can accept files between them. Files that a torrent
    // also lists stay, and the torrents alone decide them again.
    internal Task<bool> DisableDiscovery(CancellationToken cancellation) =>
        database.Run(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE library_settings SET discovery=0 WHERE singleton=1;";
                command.ExecuteNonQuery();
                command.CommandText = Database.Reconcile;
                command.ExecuteNonQuery();
                command.CommandText = """
                    DELETE FROM source_files WHERE origin IN
                        (SELECT origin FROM contributions WHERE data_source='disk');
                    DELETE FROM contributions WHERE data_source='disk';
                    DELETE FROM locations WHERE entry_id NOT IN (SELECT entry_id FROM source_files);
                    UPDATE locations SET absent=0 WHERE absent=1;
                    """;
                var changed = command.ExecuteNonQuery() != 0;
                command.CommandText = "DELETE FROM disk_reads;" + Database.Cleanup;
                command.ExecuteNonQuery();
                return changed;
            },
            cancellation
        );

    internal Task<IReadOnlyList<SavedDiskFile>> ReadDisk(CancellationToken cancellation) =>
        database.Run<IReadOnlyList<SavedDiskFile>>(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT f.file_index,l.path,k.volume,k.file_id,f.size,k.modified,f.complete
                    FROM source_files f JOIN locations l USING(entry_id)
                    JOIN disk_files k USING(origin,file_index) WHERE f.origin=$disk;
                    """;
                command.Parameters.AddWithValue("$disk", Disk);
                using var reader = command.ExecuteReader();
                var files = new List<SavedDiskFile>();
                while (reader.Read())
                {
                    cancellation.ThrowIfCancellationRequested();
                    files.Add(
                        new(
                            reader.GetInt32(0),
                            new DiskFile(reader.GetString(1), reader.GetString(2), reader.GetString(3))
                            {
                                Size = reader.GetInt64(4),
                                Modified = reader.IsDBNull(5) ? null : reader.GetInt64(5),
                                Placeholder = !reader.GetBoolean(6),
                            }
                        )
                    );
                }
                return files;
            },
            cancellation
        );

    // The locations the person's own check found gone; each refresh checks
    // them again.
    internal Task<IReadOnlyList<(long Entry, string Path)>> ReadAbsent(
        CancellationToken cancellation
    ) =>
        database.Run<IReadOnlyList<(long Entry, string Path)>>(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT entry_id,path FROM locations WHERE absent=1;";
                using var reader = command.ExecuteReader();
                var locations = new List<(long, string)>();
                while (reader.Read())
                    locations.Add((reader.GetInt64(0), reader.GetString(1)));
                return locations;
            },
            cancellation
        );

    // Writes one job of a disk refresh. A refresh that the option or a newer
    // refresh ended writes nothing. The job that confirms is the refresh's
    // last; it makes the disk files available to file work.
    internal Task<bool> AcceptDisk(DiskChanges changes, CancellationToken cancellation) =>
        database.Run(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT discovery FROM library_settings WHERE singleton=1;";
                if (Convert.ToInt64(command.ExecuteScalar()) == 0)
                    return false;
                command.Parameters.AddWithValue("$disk", Disk);
                command.CommandText = """
                    INSERT INTO contributions(origin,name,stamp,data_source) VALUES($disk,$disk,'','disk')
                    ON CONFLICT(origin) DO NOTHING;
                    """;
                var changed = command.ExecuteNonQuery() != 0;
                var subtitles = false;
                if (changes.Removed.Count + changes.Updated.Count + changes.Returned.Count > 0)
                {
                    // Share saved choices before a removal or a move separates
                    // the files that held them.
                    command.CommandText = Database.Reconcile;
                    changed |= command.ExecuteNonQuery() != 0;
                }
                using var location = connection.CreateCommand();
                location.CommandText = "UPDATE locations SET absent=0 WHERE entry_id=$entry AND absent=1;";
                var entry = location.Parameters.Add("$entry", SqliteType.Integer);
                foreach (var returned in changes.Returned)
                {
                    entry.Value = returned;
                    changed |= location.ExecuteNonQuery() != 0;
                }
                // A torrent may still list a removed file. Its location then
                // stays, saved as absent, so the row stays out of Library.
                using var removal = connection.CreateCommand();
                removal.CommandText = """
                    UPDATE locations SET absent=1 WHERE entry_id=(SELECT entry_id FROM source_files
                        WHERE origin=$disk AND file_index=$index)
                        AND EXISTS(SELECT 1 FROM source_files o WHERE o.entry_id=locations.entry_id
                            AND o.origin<>$disk);
                    DELETE FROM source_files WHERE origin=$disk AND file_index=$index RETURNING path;
                    """;
                removal.Parameters.AddWithValue("$disk", Disk);
                var removed = removal.Parameters.Add("$index", SqliteType.Integer);
                foreach (var index in changes.Removed)
                {
                    cancellation.ThrowIfCancellationRequested();
                    removed.Value = index;
                    if (removal.ExecuteScalar() is not string path)
                        continue;
                    subtitles |= FileName.IsSubtitle(path);
                    changed = true;
                }
                using var write = new FileWriter(connection);
                using var identity = connection.CreateCommand();
                identity.CommandText = """
                    INSERT INTO disk_files(origin,file_index,volume,file_id,modified)
                    VALUES($disk,$index,$volume,$id,$modified)
                    ON CONFLICT(origin,file_index) DO UPDATE SET volume=excluded.volume,
                        file_id=excluded.file_id, modified=excluded.modified
                    WHERE (volume,file_id,modified) IS NOT (excluded.volume,excluded.file_id,excluded.modified);
                    """;
                identity.Parameters.AddWithValue("$disk", Disk);
                var fileIndex = identity.Parameters.Add("$index", SqliteType.Integer);
                var volume = identity.Parameters.Add("$volume", SqliteType.Text);
                var id = identity.Parameters.Add("$id", SqliteType.Text);
                var modified = identity.Parameters.Add("$modified", SqliteType.Integer);
                using var stale = connection.CreateCommand();
                // A file written since its facts were read has them read again
                // when it is displayed.
                stale.CommandText = """
                    DELETE FROM file_facts WHERE (origin,file_index) IN (
                        SELECT o.origin,o.file_index FROM source_files f
                        JOIN disk_files k USING(origin,file_index)
                        JOIN source_files o ON o.entry_id=f.entry_id
                        WHERE f.origin=$disk AND f.file_index=$index
                            AND (f.size<>$size OR k.modified IS NOT $modified));
                    DELETE FROM fact_attempts WHERE entry_id IN (
                        SELECT f.entry_id FROM source_files f JOIN disk_files k USING(origin,file_index)
                        WHERE f.origin=$disk AND f.file_index=$index
                            AND (f.size<>$size OR k.modified IS NOT $modified));
                    """;
                stale.Parameters.AddWithValue("$disk", Disk);
                var staleIndex = stale.Parameters.Add("$index", SqliteType.Integer);
                var staleSize = stale.Parameters.Add("$size", SqliteType.Integer);
                var staleModified = stale.Parameters.Add("$modified", SqliteType.Integer);
                command.CommandText = """
                    SELECT path FROM source_files WHERE origin=$disk AND file_index=$index;
                    """;
                command.Parameters.Add("$index", SqliteType.Integer);
                foreach (var (index, file) in changes.Updated)
                {
                    cancellation.ThrowIfCancellationRequested();
                    command.Parameters["$index"].Value = index;
                    if (command.ExecuteScalar() is not string before)
                        continue;
                    if (!StringComparer.OrdinalIgnoreCase.Equals(before, file.Path))
                        subtitles |=
                            FileName.IsSubtitle(before) || FileName.IsSubtitle(file.Path);
                    staleIndex.Value = index;
                    staleSize.Value = file.Size ?? 0;
                    staleModified.Value = (object?)file.Modified ?? DBNull.Value;
                    changed |= stale.ExecuteNonQuery() != 0;
                    changed |= Save(index, file);
                }
                // A torrent may still list a moved file at its old path.
                using var vacated = connection.CreateCommand();
                vacated.CommandText = "UPDATE locations SET absent=1 WHERE path=$path AND absent=0;";
                var old = vacated.Parameters.Add("$path", SqliteType.Text);
                foreach (var path in changes.Vacated)
                {
                    old.Value = path;
                    changed |= vacated.ExecuteNonQuery() != 0;
                }
                using var numbers = connection.CreateCommand();
                numbers.CommandText = "UPDATE disk_numbers SET next=next+1 RETURNING next-1;";
                foreach (var file in changes.Added)
                {
                    cancellation.ThrowIfCancellationRequested();
                    subtitles |= FileName.IsSubtitle(file.Path);
                    changed |= Save(Convert.ToInt32(numbers.ExecuteScalar()), file);
                }
                if (changes.Confirms)
                {
                    command.CommandText = "INSERT OR IGNORE INTO disk_reads VALUES($disk);";
                    changed |= command.ExecuteNonQuery() != 0;
                }
                if (subtitles)
                {
                    // Subtitle discovery checks a disk movie again only when
                    // this stamp changes.
                    command.CommandText = "UPDATE contributions SET stamp=$stamp WHERE origin=$disk;";
                    command.Parameters.AddWithValue("$stamp", Guid.NewGuid().ToString("N"));
                    command.ExecuteNonQuery();
                }
                if (changed)
                {
                    // A removed or moved file can leave its old location empty.
                    command.CommandText =
                        "DELETE FROM locations WHERE entry_id NOT IN (SELECT entry_id FROM source_files);"
                        + Database.Reconcile
                        + Database.Cleanup;
                    command.ExecuteNonQuery();
                }
                return changed;

                bool Save(int index, DiskFile file)
                {
                    var saved = write.Save(
                        Disk,
                        index,
                        file.Path,
                        file.Path,
                        file.Size ?? 0,
                        !file.Placeholder,
                        wanted: true
                    );
                    fileIndex.Value = index;
                    volume.Value = file.Volume;
                    id.Value = file.Id;
                    modified.Value = (object?)file.Modified ?? DBNull.Value;
                    return identity.ExecuteNonQuery() != 0 || saved;
                }
            },
            cancellation
        );

    // Saves the folder and withdraws the disk files under it in one job, so
    // their rows leave at once. Files that a torrent lists stay.
    internal Task<bool> Hide(string folder, CancellationToken cancellation) =>
        database.Run(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = "INSERT OR IGNORE INTO hidden_folders VALUES($folder);";
                command.Parameters.AddWithValue("$folder", folder);
                command.ExecuteNonQuery();
                command.CommandText = "SELECT file_index,path FROM source_files WHERE origin=$disk;";
                command.Parameters.AddWithValue("$disk", Disk);
                var hidden = new List<int>();
                using (var reader = command.ExecuteReader())
                    while (reader.Read())
                        if (FolderComparer.Instance.Contains(folder, reader.GetString(1)))
                            hidden.Add(reader.GetInt32(0));
                if (hidden.Count == 0)
                    return false;
                command.CommandText = Database.Reconcile;
                command.ExecuteNonQuery();
                command.CommandText = """
                    DELETE FROM source_files WHERE origin=$disk AND file_index=$index;
                    """;
                var index = command.Parameters.Add("$index", SqliteType.Integer);
                foreach (var file in hidden)
                {
                    index.Value = file;
                    command.ExecuteNonQuery();
                }
                command.CommandText =
                    "DELETE FROM locations WHERE entry_id NOT IN (SELECT entry_id FROM source_files);"
                    + Database.Reconcile
                    + Database.Cleanup;
                command.ExecuteNonQuery();
                return true;
            },
            cancellation
        );

    internal Task Unhide(string folder, CancellationToken cancellation) =>
        database.Run(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM hidden_folders WHERE path=$folder;";
                command.Parameters.AddWithValue("$folder", folder);
                return command.ExecuteNonQuery();
            },
            cancellation
        );

    internal Task<IReadOnlyList<string>> ReadHidden(CancellationToken cancellation) =>
        database.Run<IReadOnlyList<string>>(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT path FROM hidden_folders ORDER BY path;";
                using var reader = command.ExecuteReader();
                var folders = new List<string>();
                while (reader.Read())
                    folders.Add(reader.GetString(0));
                return folders;
            },
            cancellation
        );

    // A folder as the start of the paths under it. A drive root already ends
    // with its separator.
    internal static string Folder(string folder) =>
        Path.EndsInDirectorySeparator(folder) ? folder : folder + Path.DirectorySeparatorChar;

    // The bounds of the paths under a folder, at any depth, in the PATH order
    // of locations.path: each lies after "folder\" and before "folder]",
    // because ']' follows '\'. A query between them reads the index.
    internal static (string Low, string High) PathRange(string folder)
    {
        var low = Folder(folder);
        return (low, low[..^1] + "]");
    }

    // Writes one file of a contribution at its location; both data sources
    // save files through it, so a file's row means the same for each.
    private sealed class FileWriter : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly SqliteCommand _write;

        internal FileWriter(SqliteConnection connection)
        {
            _connection = connection;
            _write = connection.CreateCommand();
            _write.CommandText = """
                INSERT INTO source_files
                    (origin, file_index, entry_id, name, path, size, complete, wanted, kind, title, type, year, search)
                VALUES ($origin, $index, $entry, $name, $path, $size, $complete, $wanted, $kind, $title, $type, $year, $search)
                ON CONFLICT(origin, file_index) DO UPDATE SET
                    entry_id=excluded.entry_id, name=excluded.name, path=excluded.path,
                    size=excluded.size, complete=excluded.complete, wanted=excluded.wanted,
                    kind=excluded.kind, title=excluded.title, type=excluded.type, year=excluded.year,
                    search=excluded.search
                WHERE (entry_id,name,path,size,complete,wanted,kind) <>
                    (excluded.entry_id,excluded.name,excluded.path,excluded.size,excluded.complete,excluded.wanted,excluded.kind);
                """;
            foreach (var name in new[] { "origin", "name", "path", "title", "type", "search" })
                _write.Parameters.Add("$" + name, SqliteType.Text);
            foreach (
                var name in new[] { "index", "entry", "size", "complete", "wanted", "kind", "year" }
            )
                _write.Parameters.Add("$" + name, SqliteType.Integer);
        }

        // Saves the file whose data is at path and which belongs at
        // location, and reports whether its row changed.
        internal bool Save(
            string origin,
            int index,
            string path,
            string location,
            long size,
            bool complete,
            bool wanted
        )
        {
            var entryId = Locate(_connection, origin, index, location);
            var name = Path.GetFileName(location);
            var release = FileName.Interpret(name);
            _write.Parameters["$origin"].Value = origin;
            _write.Parameters["$index"].Value = index;
            _write.Parameters["$entry"].Value = entryId;
            _write.Parameters["$name"].Value = name;
            _write.Parameters["$path"].Value = path;
            _write.Parameters["$size"].Value = size;
            _write.Parameters["$complete"].Value = complete;
            _write.Parameters["$wanted"].Value = wanted;
            var kind = FileName.Kind(location);
            _write.Parameters["$kind"].Value = (int)kind;
            _write.Parameters["$title"].Value = release.Title;
            // The name identifies a video before any provider does: a
            // file of a series is one of its episodes.
            _write.Parameters["$type"].Value =
                kind != FileKind.Video ? string.Empty
                : release.Kind == VideoKind.Movie ? "movie"
                : "episode";
            _write.Parameters["$year"].Value =
                kind != FileKind.Video ? DBNull.Value : (object?)release.Year ?? DBNull.Value;
            _write.Parameters["$search"].Value = FileName.Normalize(location + " " + release.Title);
            return _write.ExecuteNonQuery() != 0;
        }

        public void Dispose() => _write.Dispose();
    }

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

// A file as gozler names it: its path, its volume and its ID on that volume.
// A size or date that gozler does not know is null.
internal sealed record DiskFile(string Path, string Volume, string Id)
{
    internal long? Size { get; init; }
    internal long? Modified { get; init; }

    // A cloud file whose data is not on the disk; reading it downloads it.
    internal bool Placeholder { get; init; }
}

internal sealed record SavedDiskFile(int Index, DiskFile File);

// One job of a disk refresh. An updated file carries the values to save.
internal sealed record DiskChanges
{
    internal IReadOnlyList<DiskFile> Added { get; init; } = [];
    internal IReadOnlyList<(int Index, DiskFile File)> Updated { get; init; } = [];
    internal IReadOnlyList<int> Removed { get; init; } = [];

    // Absent locations whose file the person's check found again.
    internal IReadOnlyList<long> Returned { get; init; } = [];

    // The old paths of moved files that the person's check found gone.
    internal IReadOnlyList<string> Vacated { get; init; } = [];
    internal bool Confirms { get; init; }
}
