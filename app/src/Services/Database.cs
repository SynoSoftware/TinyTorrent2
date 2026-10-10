using Microsoft.Data.Sqlite;

namespace Syno.TinyTorrent.Services;

internal sealed class Database : IAsyncDisposable
{
    private readonly SemaphoreSlim _access = new(1);
    private readonly string _path;
    private readonly string _schema;
    private readonly string _projection;
    private SqliteConnection? _connection;
    private bool _disposed;

    internal Database(string directory, string schema, string projection)
    {
        _path = Path.Combine(directory, "library.db");
        _schema = schema;
        _projection = projection;
    }

    internal async Task<T> Run<T>(Func<SqliteConnection, T> action, CancellationToken cancellation = default)
    {
        await _access.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await Task.Run(() =>
            {
                cancellation.ThrowIfCancellationRequested();
                return Transact(Open(), action, cancellation);
            }, cancellation).ConfigureAwait(false);
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
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        try
        {
            connection.Open();
            connection.CreateCollation("PATH", (left, right) =>
                StringComparer.OrdinalIgnoreCase.Compare(left, right));
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA temp_store = MEMORY;";
            command.ExecuteNonQuery();
            Transact(connection, current =>
            {
                command.CommandText = "PRAGMA user_version;";
                var version = Convert.ToInt32(command.ExecuteScalar());
                if (version > 1)
                    throw new DatabaseVersionException();
                if (version == 0)
                {
                    command.CommandText = _schema + "PRAGMA user_version=1;";
                    command.ExecuteNonQuery();
                }
                return true;
            }, CancellationToken.None);
            command.CommandText = _projection;
            command.ExecuteNonQuery();
            _connection = connection;
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static T Transact<T>(SqliteConnection connection, Func<SqliteConnection, T> action,
        CancellationToken cancellation)
    {
        // A SQL transaction includes every command on this connection without
        // requiring each feature command to carry an ADO.NET transaction object.
        using var command = connection.CreateCommand();
        command.CommandText = "BEGIN;";
        command.ExecuteNonQuery();
        try
        {
            var result = action(connection);
            cancellation.ThrowIfCancellationRequested();
            command.CommandText = "COMMIT;";
            command.ExecuteNonQuery();
            return result;
        }
        catch
        {
            command.CommandText = "ROLLBACK;";
            command.ExecuteNonQuery();
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

internal sealed class DatabaseVersionException : Exception
{
}
