using Microsoft.Data.Sqlite;
using Syno.TinyTorrent.Models;
using Syno.TinyTorrent.Services;
using Windows.Storage;

namespace Syno.TinyTorrent.Library;

internal sealed class FileFacts(Database database, BackgroundWork work) : IAsyncDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _gate = new();
    private Task? _work;
    private bool _again;
    private bool _connected;
    private CancellationTokenSource _connection = new();
    private readonly HashSet<Target> _cancelled = [];
    internal event EventHandler? Changed;
    internal event EventHandler<Exception>? Failed;

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
        CancellationTokenSource request;
        lock (_gate)
        {
            if (!_connected)
            {
                _work = null;
                return;
            }
            _again = false;
            request = CancellationTokenSource.CreateLinkedTokenSource(_connection.Token);
        }
        var cancellation = request.Token;
        BackgroundWork.Operation? operation = null;
        var signalling = Task.CompletedTask;
        var ended = false;
        var done = 0;
        var unreadable = 0;
        var observation = new WorkObservation(
            new WorkDescription.FileProperties(),
            new WorkProgress.Unknown(),
            new WorkExecution.Running()
        );
        try
        {
            var pending = await database
                .Run(
                    connection =>
                    {
                        using var command = connection.CreateCommand();
                        command.CommandText = """
                            SELECT f.entry_id,f.path,f.kind,f.size FROM current_files f
                            WHERE f.readable=1
                            AND NOT EXISTS(SELECT 1 FROM file_facts p
                                JOIN source_files owner USING(origin,file_index) WHERE owner.entry_id=f.entry_id)
                            AND NOT EXISTS(SELECT 1 FROM fact_attempts a WHERE a.entry_id=f.entry_id AND a.path=f.path COLLATE PATH);
                            """;
                        using var reader = command.ExecuteReader();
                        var targets = new List<Target>();
                        while (reader.Read())
                            targets.Add(
                                new(reader.GetInt64(0), reader.GetString(1))
                                {
                                    Kind = (FileKind)reader.GetInt32(2),
                                    Size = reader.GetInt64(3),
                                }
                            );
                        return targets;
                    },
                    cancellation
                )
                .ConfigureAwait(false);
            Target[] accepted;
            lock (_gate)
                accepted = pending.Where(target => !_cancelled.Contains(target)).ToArray();
            if (accepted.Length == 0)
                return;
            lock (_gate)
            {
                observation = observation with
                {
                    Progress = new WorkProgress.Fraction(0, accepted.Length, WorkUnit.Files),
                };
                operation = work.Begin(
                    observation,
                    () =>
                    {
                        lock (_gate)
                        {
                            if (!ended && !request.IsCancellationRequested)
                            {
                                foreach (var target in accepted.Skip(done))
                                    _cancelled.Add(target);
                                observation = observation with
                                {
                                    Execution = new WorkExecution.Cancelling(),
                                };
                                operation?.Report(observation);
                                signalling = request.CancelAsync();
                            }
                            return signalling;
                        }
                    }
                );
            }
            foreach (var target in accepted)
            {
                cancellation.ThrowIfCancellationRequested();
                Facts? facts = null;
                try
                {
                    var file = await StorageFile
                        .GetFileFromPathAsync(target.Path)
                        .AsTask(cancellation)
                        .ConfigureAwait(false);
                    var basic = await file.GetBasicPropertiesAsync()
                        .AsTask(cancellation)
                        .ConfigureAwait(false);
                    facts = new Facts(
                        file.DateCreated.ToUnixTimeSeconds(),
                        basic.DateModified.ToUnixTimeSeconds()
                    );
                    if (target.Kind == FileKind.Audio)
                    {
                        var music = await file
                            .Properties.GetMusicPropertiesAsync()
                            .AsTask(cancellation)
                            .ConfigureAwait(false);
                        facts = facts with
                        {
                            Title = music.Title,
                            Artist = music.Artist,
                            Album = music.Album,
                            Track = music.TrackNumber == 0 ? null : (int)music.TrackNumber,
                            Year = music.Year == 0 ? null : (int)music.Year,
                            Genre = string.Join(Store.Separator, music.Genre),
                            Duration =
                                music.Duration > TimeSpan.Zero
                                    ? (long)music.Duration.TotalSeconds
                                    : null,
                            Bitrate = music.Bitrate == 0 ? null : music.Bitrate,
                        };
                    }
                    else if (target.Kind == FileKind.Video)
                    {
                        var video = await file
                            .Properties.GetVideoPropertiesAsync()
                            .AsTask(cancellation)
                            .ConfigureAwait(false);
                        facts = facts with
                        {
                            Duration =
                                video.Duration > TimeSpan.Zero
                                    ? (long)video.Duration.TotalSeconds
                                    : null,
                            Bitrate = video.Bitrate == 0 ? null : video.Bitrate,
                            Width = video.Width == 0 ? null : (int)video.Width,
                            Height = video.Height == 0 ? null : (int)video.Height,
                        };
                    }
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    unreadable++;
                    operation.Error ??= error;
                    facts = null;
                    System.Diagnostics.Debug.WriteLine(
                        "Library file properties: " + error.GetType().Name
                    );
                }
                lock (_gate)
                    if (!_connected)
                        continue;
                await database
                    .Run(connection => Save(connection, target, facts), cancellation)
                    .ConfigureAwait(false);
                if (facts is not null)
                    Changed?.Invoke(this, EventArgs.Empty);
                lock (_gate)
                {
                    done++;
                    if (!request.IsCancellationRequested)
                    {
                        observation = observation with
                        {
                            Progress = new WorkProgress.Fraction(
                                done,
                                accepted.Length,
                                WorkUnit.Files
                            ),
                        };
                        operation.Report(observation);
                    }
                }
            }
            operation.Outcome = unreadable > 0 ? WorkOutcome.Failed : WorkOutcome.Completed;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (operation is not null)
                operation.Outcome = WorkOutcome.Cancelled;
            System.Diagnostics.Debug.WriteLine("Library file properties read cancelled.");
        }
        catch (Exception error)
        {
            if (operation is not null)
                operation.Error = error;
            Failed?.Invoke(this, error);
        }
        finally
        {
            lock (_gate)
                ended = true;
            try
            {
                await signalling.ConfigureAwait(false);
            }
            finally
            {
                operation?.Summarize(
                    "properties_result",
                    done,
                    observation.Progress is WorkProgress.Fraction fraction ? fraction.Total : done,
                    unreadable
                );
                operation?.Dispose();
                request.Dispose();
            }
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
            SELECT EXISTS(SELECT 1 FROM current_files f WHERE f.entry_id=$entry
                AND f.path=$path COLLATE PATH AND f.size=$size AND f.readable=1);
            """;
        command.Parameters.AddWithValue("$entry", target.EntryId);
        command.Parameters.AddWithValue("$path", target.Path);
        command.Parameters.AddWithValue("$size", target.Size);
        if (Convert.ToInt64(command.ExecuteScalar()) == 0)
            return false;
        command.CommandText = "INSERT OR REPLACE INTO fact_attempts VALUES ($entry,$path);";
        command.ExecuteNonQuery();
        if (facts is null)
            return false;
        command.CommandText = """
            INSERT OR IGNORE INTO file_facts
            SELECT origin,file_index,$created,$modified,$title,$artist,$album,$track,$year,$genre,$duration,$search,
                $bitrate,$width,$height
            FROM source_files WHERE entry_id=$entry;
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
        command.Parameters.AddWithValue("$bitrate", (object?)facts.Bitrate ?? DBNull.Value);
        command.Parameters.AddWithValue("$width", (object?)facts.Width ?? DBNull.Value);
        command.Parameters.AddWithValue("$height", (object?)facts.Height ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$search",
            FileName.Normalize(
                string.Join(' ', facts.Title, facts.Artist, facts.Album, facts.Year, facts.Genre)
            )
        );
        command.ExecuteNonQuery();
        command.CommandText = Database.Invalidate;
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

    private sealed record Target(long EntryId, string Path)
    {
        internal FileKind Kind { get; init; }
        internal long Size { get; init; }
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
        internal long? Bitrate { get; init; }
        internal int? Width { get; init; }
        internal int? Height { get; init; }
    }
}
