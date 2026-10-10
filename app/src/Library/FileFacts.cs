using Microsoft.Data.Sqlite;
using Syno.TinyTorrent.Models;
using Syno.TinyTorrent.Services;
using Windows.Storage;

namespace Syno.TinyTorrent.Library;

internal sealed class FileFacts(Database database) : IAsyncDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _gate = new();
    private Task? _work;
    private bool _again;
    private bool _connected;
    private CancellationTokenSource _connection = new();
    internal event EventHandler? Changed;
    internal event EventHandler<Exception>? Failed;

    internal const string Schema = """
        CREATE TEMP TABLE fact_attempts (entry_id INTEGER PRIMARY KEY, path TEXT COLLATE PATH NOT NULL);
        """;

    internal const string Reconcile = """
        WITH facts AS (
            SELECT s.entry_id,p.*,
                row_number() OVER (PARTITION BY s.entry_id ORDER BY p.origin,p.file_index) AS rank
            FROM source_files s JOIN file_facts p USING(origin,file_index)
        )
        INSERT INTO file_facts
        SELECT s.origin,s.file_index,p.created,p.modified,p.title,p.artist,p.album,
            p.track,p.year,p.genre,p.duration,p.search
        FROM source_files s JOIN facts p ON p.entry_id=s.entry_id AND p.rank=1
        WHERE true ON CONFLICT(origin,file_index) DO NOTHING;
        """;

    internal void SetConnected(bool connected)
    {
        lock (_gate)
        {
            if (_lifetime.IsCancellationRequested)
                return;
            if (_connected != connected)
            {
                _connection.Cancel();
                _connection.Dispose();
                _connection = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                _connected = connected;
            }
            if (_lifetime.IsCancellationRequested || !connected)
                return;
            _again = true;
            if (_work is null || _work.IsCompleted)
                _work = Task.Run(Run);
        }
    }

    private async Task Run()
    {
        CancellationToken cancellation;
        lock (_gate)
            cancellation = _connection.Token;
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                lock (_gate)
                {
                    if (!_connected)
                    {
                        return;
                    }
                    _again = false;
                }
                var target = await database.Run(connection =>
                {
                    using var command = connection.CreateCommand();
                    command.CommandText = """
                        SELECT f.entry_id,f.path,f.kind,s.origin,s.file_index FROM current_files f
                        JOIN source_files s USING(entry_id)
                        WHERE f.readable=1
                        AND NOT EXISTS(SELECT 1 FROM file_facts p
                            JOIN source_files owner USING(origin,file_index) WHERE owner.entry_id=f.entry_id)
                        AND NOT EXISTS(SELECT 1 FROM fact_attempts a WHERE a.entry_id=f.entry_id AND a.path=f.path COLLATE PATH)
                        LIMIT 1;
                        """;
                    using var reader = command.ExecuteReader();
                    return reader.Read() ? new Target(reader.GetInt64(0), reader.GetString(1),
                        reader.GetString(3), reader.GetInt32(4)) { Kind = (FileKind)reader.GetInt32(2) } : null;
                }, cancellation).ConfigureAwait(false);
                if (target is null)
                {
                    lock (_gate)
                    {
                        if (_again)
                            continue;
                    }
                    return;
                }
                Facts? facts = null;
                try
                {
                    var file = await StorageFile.GetFileFromPathAsync(target.Path).AsTask(cancellation).ConfigureAwait(false);
                    var basic = await file.GetBasicPropertiesAsync().AsTask(cancellation).ConfigureAwait(false);
                    facts = new Facts(file.DateCreated.ToUnixTimeSeconds(), basic.DateModified.ToUnixTimeSeconds());
                    if (target.Kind == FileKind.Audio)
                    {
                        var music = await file.Properties.GetMusicPropertiesAsync().AsTask(cancellation).ConfigureAwait(false);
                        facts = facts with
                        {
                            Title = music.Title, Artist = music.Artist, Album = music.Album,
                            Track = music.TrackNumber == 0 ? null : (int)music.TrackNumber,
                            Year = music.Year == 0 ? null : (int)music.Year,
                            Genre = string.Join(" · ", music.Genre), Duration = (long)music.Duration.TotalSeconds,
                        };
                    }
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    facts = null;
                    System.Diagnostics.Debug.WriteLine("Library file properties: " + error.GetType().Name);
                }
                lock (_gate)
                    if (!_connected)
                        continue;
                await database.Run(connection => Save(connection, target, facts), cancellation).ConfigureAwait(false);
                if (facts is not null)
                    Changed?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error) { Failed?.Invoke(this, error); }
        finally
        {
            lock (_gate)
            {
                _work = null;
                if (_connected && _again && !_lifetime.IsCancellationRequested)
                    _work = Task.Run(Run);
            }
        }
    }

    private static bool Save(SqliteConnection connection, Target target, Facts? facts)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS(SELECT 1 FROM source_files s JOIN current_files f USING(entry_id)
                WHERE s.origin=$origin AND s.file_index=$index AND f.entry_id=$entry
                AND f.path=$path COLLATE PATH AND f.readable=1);
            """;
        command.Parameters.AddWithValue("$origin", target.Origin);
        command.Parameters.AddWithValue("$index", target.Index);
        command.Parameters.AddWithValue("$entry", target.EntryId);
        command.Parameters.AddWithValue("$path", target.Path);
        if (Convert.ToInt64(command.ExecuteScalar()) == 0)
            return false;
        command.CommandText = "INSERT OR REPLACE INTO fact_attempts VALUES ($entry,$path);";
        command.ExecuteNonQuery();
        if (facts is null)
            return false;
        command.CommandText = """
            INSERT INTO file_facts
            SELECT origin,file_index,$created,$modified,$title,$artist,$album,$track,$year,$genre,$duration,$search
            FROM source_files WHERE entry_id=$entry
            ON CONFLICT(origin,file_index) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$created", facts.Created);
        command.Parameters.AddWithValue("$modified", facts.Modified);
        command.Parameters.AddWithValue("$title", facts.Title);
        command.Parameters.AddWithValue("$artist", facts.Artist);
        command.Parameters.AddWithValue("$album", facts.Album);
        command.Parameters.AddWithValue("$track", (object?)facts.Track ?? DBNull.Value);
        command.Parameters.AddWithValue("$year", (object?)facts.Year ?? DBNull.Value);
        command.Parameters.AddWithValue("$genre", facts.Genre);
        command.Parameters.AddWithValue("$duration", (object?)facts.Duration ?? DBNull.Value);
        command.Parameters.AddWithValue("$search", FileName.Normalize(string.Join(' ', facts.Title,
            facts.Artist, facts.Album, facts.Year, facts.Genre)));
        command.ExecuteNonQuery();
        command.CommandText = Store.Invalidate;
        command.ExecuteNonQuery();
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        Task? work;
        lock (_gate)
            work = _work;
        if (work is not null)
            await work.ConfigureAwait(false);
        _lifetime.Dispose();
        _connection.Dispose();
    }

    private sealed record Target(long EntryId, string Path, string Origin, int Index)
    {
        internal FileKind Kind { get; init; }
    }

    private sealed record Facts(long Created, long Modified)
    {
        internal string Title { get; init; } = string.Empty;
        internal string Artist { get; init; } = string.Empty;
        internal string Album { get; init; } = string.Empty;
        internal int? Track { get; init; }
        internal int? Year { get; init; }
        internal string Genre { get; init; } = string.Empty;
        internal long? Duration { get; init; }
    }
}
