using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace Syno.TinyTorrent.Shared.Storage;

internal sealed partial class Database : IAsyncDisposable
{
    internal const string FileName = "library.db";

    private readonly SemaphoreSlim _access = new(1);
    private readonly string _path;
    private SqliteConnection? _connection;
    private bool _disposed;
    private volatile bool _incompatible;
    private volatile SqliteException? _reopenFailure;
    internal bool IsIncompatible => _incompatible;
    internal bool RequiresReopen => _reopenFailure is not null;

    internal Database(string directory)
    {
        _path = Path.Combine(directory, FileName);
    }

    internal Task<long> ReadSize() =>
        Run(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT page_count*page_size FROM pragma_page_count(),pragma_page_size();";
            return Convert.ToInt64(command.ExecuteScalar());
        });

    // Returns each settings row to the defaults Schema gives a new database and
    // forgets the person's own API keys; Library information stays.
    internal Task ResetSettings() =>
        Run(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                DELETE FROM provider_keys;
                DELETE FROM request_settings;
                INSERT INTO request_settings(singleton) VALUES(1);
                DELETE FROM library_settings;
                INSERT INTO library_settings(singleton) VALUES(1);
                DELETE FROM subtitle_settings;
                INSERT INTO subtitle_settings(singleton) VALUES(1);
                """;
            return command.ExecuteNonQuery();
        });

    internal Task<Statistics> ReadStatistics() =>
        Run(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT movies,series,episodes,
                    (SELECT COUNT(*) FROM current_files WHERE complete=1),
                    subtitles,languages
                FROM (SELECT COUNT(CASE WHEN type='movie' THEN 1 END) AS movies,
                        COUNT(CASE WHEN type='tv' THEN 1 END) AS series,
                        COUNT(CASE WHEN type='episode' THEN 1 END) AS episodes FROM video_information),
                    (SELECT COUNT(*) AS subtitles,
                        COUNT(DISTINCT language COLLATE NOCASE) AS languages FROM subtitle_outputs);
                """;
            using var reader = command.ExecuteReader();
            reader.Read();
            return new Statistics
            {
                Movies = reader.GetInt64(0),
                Series = reader.GetInt64(1),
                Episodes = reader.GetInt64(2),
                Files = reader.GetInt64(3),
                Subtitles = reader.GetInt64(4),
                Languages = reader.GetInt64(5),
            };
        });

    internal async Task<T> Run<T>(
        Func<SqliteConnection, T> action,
        CancellationToken cancellation = default
    )
    {
        await _access.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await Task.Run(
                    () =>
                    {
                        cancellation.ThrowIfCancellationRequested();
                        if (_incompatible)
                            throw new DatabaseVersionException();
                        if (_reopenFailure is { } failure)
                            throw failure;
                        try
                        {
                            return Transact(Open(), action, cancellation);
                        }
                        catch (SqliteException error)
                            when (error.SqliteErrorCode == raw.SQLITE_INTERRUPT
                                && cancellation.IsCancellationRequested
                            )
                        {
                            throw new OperationCanceledException(null, error, cancellation);
                        }
                        catch (SqliteException error)
                            when (error.SqliteErrorCode is raw.SQLITE_CORRUPT or raw.SQLITE_NOTADB)
                        {
                            _incompatible = true;
                            throw new DatabaseVersionException();
                        }
                        catch (SqliteException error)
                            when (error.SqliteErrorCode == raw.SQLITE_READONLY)
                        {
                            // Reopening loses every TEMP table, so recovery must
                            // first stop the workers and rebuild their file view.
                            _reopenFailure = error;
                            throw;
                        }
                    },
                    cancellation
                )
                .ConfigureAwait(false);
        }
        finally
        {
            _access.Release();
        }
    }

    // The Read-only attribute is the one write protection TinyTorrent can
    // remove without administrator rights.
    internal bool IsReadOnlyFile
    {
        get
        {
            try
            {
                return File.GetAttributes(_path).HasFlag(FileAttributes.ReadOnly);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    internal void ClearReadOnly() =>
        File.SetAttributes(_path, File.GetAttributes(_path) & ~FileAttributes.ReadOnly);

    internal async Task DeleteIncompatible()
    {
        await _access.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_incompatible)
                return;
            await Task.Run(() =>
                {
                    _connection?.Dispose();
                    _connection = null;
                    File.Delete(_path);
                    // SQLite would roll a leftover journal back into the new file.
                    File.Delete(_path + "-journal");
                    _incompatible = false;
                    _disposed = true;
                })
                .ConfigureAwait(false);
        }
        finally
        {
            _access.Release();
        }
    }

    private SqliteConnection Open()
    {
        if (_connection is not null)
            return _connection;
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = _path,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
                DefaultTimeout = 5,
            }.ToString()
        );
        try
        {
            connection.Open();
            connection.CreateCollation(
                "PATH",
                (left, right) => StringComparer.OrdinalIgnoreCase.Compare(left, right)
            );
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA temp_store = MEMORY;";
            command.ExecuteNonQuery();
            Transact(
                connection,
                _ =>
                {
                    command.CommandText = "PRAGMA user_version;";
                    var version = Convert.ToInt32(command.ExecuteScalar());
                    _incompatible = version is not (0 or Version);
                    if (_incompatible)
                        throw new DatabaseVersionException();
                    if (version == 0)
                    {
                        command.CommandText =
                            "SELECT EXISTS(SELECT 1 FROM sqlite_schema WHERE name NOT GLOB 'sqlite_*');";
                        _incompatible = Convert.ToInt64(command.ExecuteScalar()) != 0;
                        if (_incompatible)
                            throw new DatabaseVersionException();
                        command.CommandText = Schema + $"PRAGMA user_version={Version};";
                        command.ExecuteNonQuery();
                    }
                    command.CommandText = Projection;
                    command.ExecuteNonQuery();
                    return true;
                },
                CancellationToken.None
            );
            _connection = connection;
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private T Transact<T>(
        SqliteConnection connection,
        Func<SqliteConnection, T> action,
        CancellationToken cancellation
    )
    {
        // A SQL transaction includes every command on this connection without
        // requiring each feature command to carry an ADO.NET transaction object.
        using var command = connection.CreateCommand();
        command.CommandText = "BEGIN;";
        command.ExecuteNonQuery();
        try
        {
            T result;
            try
            {
                if (cancellation.CanBeCanceled)
                    raw.sqlite3_progress_handler(
                        connection.Handle!,
                        1000,
                        _ => cancellation.IsCancellationRequested ? 1 : 0,
                        null
                    );
                result = action(connection);
            }
            finally
            {
                if (cancellation.CanBeCanceled)
                    raw.sqlite3_progress_handler(connection.Handle!, 0, null, null);
            }
            cancellation.ThrowIfCancellationRequested();
            command.CommandText = "COMMIT;";
            command.ExecuteNonQuery();
            return result;
        }
        catch (Exception error)
        {
            // SQLite ends the transaction itself after some failures, such as
            // a full disk; a ROLLBACK then fails and hides the original error.
            if (raw.sqlite3_get_autocommit(connection.Handle!) == 0)
            {
                try
                {
                    command.CommandText = "ROLLBACK;";
                    command.ExecuteNonQuery();
                }
                catch (SqliteException rollback)
                {
                    // An unfinished transaction cannot serve another operation.
                    _reopenFailure = error as SqliteException ?? rollback;
                }
            }
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _access.WaitAsync().ConfigureAwait(false);
        try
        {
            _disposed = true;
            if (_connection is { } connection)
                await Task.Run(connection.Dispose).ConfigureAwait(false);
            _connection = null;
        }
        finally
        {
            _access.Release();
        }
    }
}

// The file is from another schema version or is damaged. Both have one
// recovery: the person deletes the file. Until then every database call
// fails, and each feature handles it as any other SQLite failure.
internal sealed class DatabaseVersionException : SqliteException
{
    internal DatabaseVersionException()
        : base(null, raw.SQLITE_ERROR) { }
}

internal sealed record Statistics
{
    internal required long Movies { get; init; }
    internal required long Series { get; init; }
    internal required long Episodes { get; init; }
    internal required long Files { get; init; }
    internal required long Subtitles { get; init; }
    internal required long Languages { get; init; }
}
