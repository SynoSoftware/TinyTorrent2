using System.Globalization;
using System.IO.Compression;
using System.Net.Http;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SQLitePCL;
using Syno.TinyTorrent.Models;
using Syno.TinyTorrent.Services;

namespace Syno.TinyTorrent.Library.Imdb;

/// <summary>
/// IMDb's non-commercial datasets, downloaded when the person asks and
/// imported into a SQLite file beside <c>library.db</c>. The file is a
/// replaceable copy: each download rebuilds it completely.
/// </summary>
internal sealed class Dataset(ProviderHttp http, string directory, RequestPolicy policy)
{
    // The schema version is part of the name, so a file that an earlier
    // version of the app built counts as missing.
    private const string FileName = "imdb-1.db";

    // IMDb marks an empty value with \N.
    private const string Empty = "\\N";
    private const string BasicsHeader =
        "tconst\ttitleType\tprimaryTitle\toriginalTitle\tisAdult\tstartYear\tendYear\truntimeMinutes\tgenres";
    private const string RatingsHeader = "tconst\taverageRating\tnumVotes";
    private const string EpisodesHeader = "tconst\tparentTconst\tseasonNumber\tepisodeNumber";

    // Windows reports a full disk as ERROR_DISK_FULL or ERROR_HANDLE_DISK_FULL.
    private const int DiskFull = unchecked((int)0x80070070);
    private const int HandleDiskFull = unchecked((int)0x80070027);
    private const int ThreadModeBackgroundBegin = 0x10000;
    private const int ThreadModeBackgroundEnd = 0x20000;
    private static readonly Uri Basics = new("https://datasets.imdbws.com/title.basics.tsv.gz");
    private static readonly Uri RatingsSource = new("https://datasets.imdbws.com/title.ratings.tsv.gz");
    private static readonly Uri Episodes = new("https://datasets.imdbws.com/title.episode.tsv.gz");
    private static readonly Uri[] Sources = [Basics, RatingsSource, Episodes];
    private static readonly ProviderAccess Access =
        new(Provider.CatalogId, RequestKey.Digest(string.Empty), "dataset");
    private readonly string _path = Path.Combine(directory, FileName);

    // Reads share the file; Install and Delete replace it alone, because
    // Windows refuses to replace an open file.
    private readonly ReaderWriterLockSlim _file = new();
    private volatile bool _installed = File.Exists(Path.Combine(directory, FileName));

    // Counting reads the whole titles table, so the count is kept until
    // Install or Delete replaces the file.
    private volatile Task<long>? _titles;

    private string Building => _path + ".tmp";

    /// <summary>Whether the data is downloaded; reading it opens no file.</summary>
    internal bool IsInstalled => _installed;

    /// <summary>
    /// Whether the installed data is less than a day old. IMDb updates its
    /// files once a day, so downloading again would most likely transfer the
    /// same files.
    /// </summary>
    internal bool IsCurrent => IsRecent(_path);

    /// <summary>The installed data's file, or null when it is missing.</summary>
    internal FileInfo? Inspect()
    {
        var file = new FileInfo(_path);
        return file.Exists ? file : null;
    }

    /// <summary>The number of movies and series in the installed data.</summary>
    /// <exception cref="ProviderException">The data is damaged or cannot be read.</exception>
    internal Task<long> CountTitles() =>
        _titles ??= Read(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT count(*) FROM titles;";
                return (long)command.ExecuteScalar()!;
            },
            CancellationToken.None
        );

    /// <param name="Received">The bytes downloaded so far.</param>
    /// <param name="Percent">Approximate progress through the current compressed source, when known.</param>
    internal sealed record Progress(long Received, ImdbPhase Phase, int? Percent = null);

    /// <summary>
    /// Downloads the datasets and builds the data beside the installed file,
    /// which stays in use until <see cref="Install"/>; <see cref="Discard"/>
    /// deletes a build that is not installed. Archives that a failed import
    /// left complete less than a day ago are imported again without a
    /// download, because IMDb updates the files once a day. A cancel can end
    /// it with the error that the interruption caused instead of a
    /// cancellation.
    /// </summary>
    /// <exception cref="VideoException">The download or the import failed.</exception>
    internal async Task Build(Action<Progress> report, CancellationToken cancellation)
    {
        // Each download is the person's own request, so it tries once past a
        // refusal or a backoff that an earlier download left.
        var fresh = new FreshRequest(
            new RetryCommand(DateTimeOffset.UtcNow.UtcTicks) { Recheck = true },
            RequestKey.Create(Provider.CatalogId, "dataset", [])
        );
        try
        {
            var received = 0L;
            foreach (var source in Sources)
            {
                var archive = Archive(source);
                if (!IsRecent(archive))
                    received += await Fetch(source, archive, fresh, count => report(new(received + count, ImdbPhase.Downloading)), cancellation)
                        .ConfigureAwait(false);
            }
            await Task.Factory.StartNew(() =>
            {
                var thread = GetCurrentThread();
                if (!SetThreadPriority(thread, ThreadModeBackgroundBegin))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                try
                {
                    cancellation.ThrowIfCancellationRequested();
                    Import((phase, percent) => report(new(received, phase, percent)), cancellation);
                }
                finally
                {
                    if (!SetThreadPriority(thread, ThreadModeBackgroundEnd))
                        System.Diagnostics.Debug.WriteLine(new Win32Exception(Marshal.GetLastWin32Error()));
                }
            }, cancellation, TaskCreationOptions.LongRunning, TaskScheduler.Default)
                .ConfigureAwait(false);
        }
        catch (HttpRouteException error)
        {
            throw new RouteException(error);
        }
        // A damaged or changed archive would fail again, so it is not kept.
        catch (InvalidDataException error)
        {
            Remove(Archives);
            throw new VideoException(VideoFailure.DownloadDamaged, error);
        }
        catch (VideoException error) when (error.Failure == VideoFailure.UnknownFormat)
        {
            Remove(Archives);
            throw;
        }
        catch (Exception error)
            when (error is SqliteException { SqliteErrorCode: raw.SQLITE_FULL }
                or IOException { HResult: DiskFull or HandleDiskFull })
        {
            throw new VideoException(VideoFailure.DiskFull, error);
        }
        catch (Exception error)
            when (error is HttpRequestException or IOException or UnauthorizedAccessException or SqliteException)
        {
            throw new VideoException(VideoFailure.DownloadFailed, error);
        }
    }

    /// <summary>Replaces the installed data with the data <see cref="Build"/> made.</summary>
    /// <exception cref="VideoException">Another program holds the data.</exception>
    internal void Install()
    {
        _file.EnterWriteLock();
        try
        {
            File.Move(Building, _path, overwrite: true);
            _installed = true;
            _titles = null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new VideoException(VideoFailure.InUse, error);
        }
        finally
        {
            _file.ExitWriteLock();
        }
        // The data is installed, so an archive that another program holds is
        // only replaced by the next download. Data that an earlier version of
        // the app built is never read again.
        try
        {
            Remove(Archives);
            Remove([.. Directory.GetFiles(directory, "imdb*.db").Where(path => !string.Equals(path, _path, StringComparison.OrdinalIgnoreCase))]);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Deletes a build that was not installed.</summary>
    internal void Discard()
    {
        // A file that another program holds is deleted by the next build.
        try
        {
            File.Delete(Building);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Deletes the installed data and the archives kept for a new import.</summary>
    /// <exception cref="VideoException">Another program holds the data.</exception>
    internal void Delete()
    {
        _file.EnterWriteLock();
        try
        {
            Remove(Archives);
            File.Delete(_path);
            _installed = false;
            _titles = null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new VideoException(VideoFailure.InUse, error);
        }
        finally
        {
            _file.ExitWriteLock();
        }
    }

    /// <summary>
    /// Reads the ratings of these IMDb titles. A title without votes has none,
    /// and data that is missing or unreadable gives none, so the providers' own
    /// ratings stay in use.
    /// </summary>
    internal async Task<IReadOnlyDictionary<long, Rating>> Ratings(
        IReadOnlyCollection<long> titles,
        CancellationToken cancellation
    )
    {
        if (titles.Count == 0 || !IsInstalled)
            return new Dictionary<long, Rating>();
        try
        {
            return await Read<IReadOnlyDictionary<long, Rating>>(
                    connection =>
                    {
                        using var command = connection.CreateCommand();
                        command.CommandText = """
                            SELECT title_id,kind,rating,votes FROM titles
                            WHERE title_id IN (SELECT value FROM json_each($titles));
                            """;
                        command.Parameters.AddWithValue("$titles", JsonSerializer.Serialize(titles));
                        var ratings = new Dictionary<long, Rating>();
                        using var reader = command.ExecuteReader();
                        while (reader.Read())
                            if (ReadRating(reader) is { } rating)
                                ratings[reader.GetInt64(0)] = rating;
                        return ratings;
                    },
                    cancellation
                )
                .ConfigureAwait(false);
        }
        catch (ProviderException)
        {
            return new Dictionary<long, Rating>();
        }
    }

    /// <summary>
    /// Reads the ratings of videos that no provider gave an IMDb ID, by the
    /// names of their files, in the order of <paramref name="releases"/>. A
    /// movie is found by its exact title and year. An episode gets its series'
    /// rating, as an IMDb match gives it: the series has the exact title and
    /// lists the episode, and the year decides only between several such
    /// series. A name that still fits several titles, or none, gives no
    /// rating, because it could belong to another title.
    /// </summary>
    internal async Task<IReadOnlyList<Rating?>> RatingsByName(
        IReadOnlyList<Release> releases,
        CancellationToken cancellation
    )
    {
        var ratings = new Rating?[releases.Count];
        var names = releases
            .Select((release, position) => new
            {
                Position = position,
                Name = Key(release.Title),
                release.Year,
                release.Season,
                Episode = release.Episodes.Count > 0 ? release.Episodes[0] : (int?)null,
                release.IsExtra,
            })
            .Where(name => !name.IsExtra && name.Name.Length > 0 && (name.Season is not null || name.Year is not null))
            .ToArray();
        if (names.Length == 0 || !IsInstalled)
        {
            return ratings;
        }
        try
        {
            return await Read<IReadOnlyList<Rating?>>(
                    connection =>
                    {
                        using var command = connection.CreateCommand();
                        // CROSS JOIN keeps the names as the outer loop, so each
                        // name is one index lookup instead of a scan of the titles.
                        command.CommandText = """
                            WITH wanted AS (
                                SELECT json_extract(value,'$.Position') AS position,json_extract(value,'$.Name') AS name,
                                    json_extract(value,'$.Year') AS released,json_extract(value,'$.Season') AS season,
                                    json_extract(value,'$.Episode') AS number
                                FROM json_each($names)
                            ),
                            named AS (
                                SELECT w.*,t.title_id,t.year,t.kind,t.rating,t.votes
                                FROM wanted w CROSS JOIN titles t ON t.title_key=w.name
                                UNION
                                SELECT w.*,t.title_id,t.year,t.kind,t.rating,t.votes
                                FROM wanted w CROSS JOIN titles t ON t.original_key=w.name
                            )
                            SELECT position,title_id,year,kind,rating,votes FROM named n
                            WHERE CASE WHEN season IS NULL THEN kind='movie' AND year=released
                                ELSE kind='series' AND EXISTS(SELECT 1 FROM episodes e
                                    WHERE e.series_id=n.title_id AND e.season=n.season
                                        AND (n.number IS NULL OR e.number=n.number)) END;
                            """;
                        command.Parameters.AddWithValue("$names", JsonSerializer.Serialize(names));
                        var found = new List<(int Position, int Year, Rating? Rating)>();
                        using (var reader = command.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                found.Add((reader.GetInt32(0), reader.GetInt32(2), ReadRating(reader)));
                            }
                        }
                        foreach (var titles in found.GroupBy(title => title.Position))
                        {
                            var release = releases[titles.Key];
                            var fits = titles.ToArray();
                            if (fits.Length > 1 && release.Season is not null && release.Year is { } year)
                            {
                                fits = [.. fits.Where(title => title.Year == year)];
                            }
                            if (fits.Length == 1)
                            {
                                ratings[titles.Key] = fits[0].Rating;
                            }
                        }
                        return ratings;
                    },
                    cancellation
                )
                .ConfigureAwait(false);
        }
        catch (ProviderException)
        {
            return ratings;
        }
    }

    /// <summary>Runs <paramref name="read"/> on the installed data, off the calling thread.</summary>
    /// <exception cref="ProviderException">The data is damaged or cannot be read.</exception>
    internal Task<T> Read<T>(Func<SqliteConnection, T> read, CancellationToken cancellation) =>
        Task.Run(
            () =>
            {
                // Each read opens and closes the file, so Install and Delete
                // can replace it between reads.
                _file.EnterReadLock();
                try
                {
                    using var connection = Connect(_path, SqliteOpenMode.ReadOnly, cancellation);
                    return read(connection);
                }
                catch (SqliteException error)
                    when (error.SqliteErrorCode == raw.SQLITE_INTERRUPT && cancellation.IsCancellationRequested)
                {
                    throw new OperationCanceledException(null, error, cancellation);
                }
                // A damaged file fails every lookup until the person downloads
                // the data again.
                catch (SqliteException error)
                    when (error.SqliteErrorCode is raw.SQLITE_CORRUPT or raw.SQLITE_NOTADB)
                {
                    throw new ProviderException(VideoFailure.Damaged, inner: error);
                }
                catch (SqliteException error)
                {
                    throw new ProviderException(VideoFailure.Unreadable, inner: error);
                }
                finally
                {
                    _file.ExitReadLock();
                }
            },
            cancellation
        );

    /// <summary>The kind of a title row, read from its <c>kind</c> column.</summary>
    internal static VideoKind ReadKind(SqliteDataReader reader) =>
        reader.GetString(reader.GetOrdinal("kind")) == "series" ? VideoKind.Series : VideoKind.Movie;

    /// <summary>
    /// The rating of a title row, read from its <c>kind</c>, <c>rating</c> and
    /// <c>votes</c> columns; a title without votes has none.
    /// </summary>
    internal static Rating? ReadRating(SqliteDataReader reader)
    {
        var votes = reader.GetOrdinal("votes");
        return reader.IsDBNull(votes) || reader.GetInt64(votes) == 0
            ? null
            : new Rating(reader.GetDouble(reader.GetOrdinal("rating")), ReadKind(reader))
            {
                Votes = reader.GetInt64(votes),
            };
    }

    /// <summary>
    /// A title in lower case without accents, apostrophes or other
    /// punctuation, because a file name lacks them: "Ocean's Eleven" and
    /// Oceans.Eleven both give "oceans eleven".
    /// </summary>
    internal static string Key(string title)
    {
        var key = new StringBuilder(title.Length);
        foreach (var character in Models.FileName.Normalize(title))
        {
            if (char.IsLetterOrDigit(character))
            {
                key.Append(character);
            }
            else if (character is not ('\'' or '’') && key.Length > 0 && key[^1] != ' ')
            {
                key.Append(' ');
            }
        }
        return key.ToString().TrimEnd();
    }

    private string[] Archives => [.. Sources.Select(Archive)];

    private string Archive(Uri source) => Path.Combine(directory, Path.GetFileName(source.LocalPath));

    private static bool IsRecent(string path) =>
        new FileInfo(path) is { Exists: true } file && file.LastWriteTimeUtc > DateTime.UtcNow.AddDays(-1);

    // Downloads beside the archive and renames the file when it is complete,
    // so an archive that exists holds a whole file.
    private async Task<long> Fetch(
        Uri source,
        string archive,
        FreshRequest fresh,
        Action<long> received,
        CancellationToken cancellation
    )
    {
        var part = archive + ".part";
        using var request = new HttpRequestMessage(HttpMethod.Get, source);
        var call = new ProviderCall(request, policy, new(Access)) { Fresh = fresh };
        try
        {
            var length = await http
                .Download(call with { Observe = reply => ProviderJson.Observe(call, reply) }, part, received, cancellation)
                .ConfigureAwait(false);
            File.Move(part, archive, overwrite: true);
            return length;
        }
        finally
        {
            File.Delete(part);
        }
    }

    // Without pooling, closing the connection closes the file, so the file can
    // be replaced or deleted afterwards. A cancel interrupts a long statement,
    // such as the index rebuild.
    private static SqliteConnection Connect(
        string path,
        SqliteOpenMode mode,
        CancellationToken cancellation
    )
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = mode,
                Pooling = false,
            }.ToString()
        );
        connection.Open();
        raw.sqlite3_progress_handler(
            connection.Handle!,
            1000,
            _ => cancellation.IsCancellationRequested ? 1 : 0,
            null
        );
        return connection;
    }

    // Writes the file that Install moves into place.
    private void Import(Action<ImdbPhase, int?> report, CancellationToken cancellation)
    {
        // A crash during an earlier build can leave the file behind.
        File.Delete(Building);
        using (var connection = Connect(Building, SqliteOpenMode.ReadWriteCreate, cancellation))
        {
            try
            {
                Execute(
                    connection,
                    // The file is temporary until Install, so a crash only
                    // loses a file that the next build deletes. The title
                    // keys hold the titles as Key gives them, so a search can
                    // rank exact titles first and RatingsByName can find a
                    // title from a file name. The episode numbers are only
                    // needed during the import, so they stay out of the file.
                    """
                    PRAGMA journal_mode=OFF;
                    PRAGMA synchronous=OFF;
                    CREATE TABLE titles (
                        title_id INTEGER PRIMARY KEY,
                        kind TEXT NOT NULL CHECK(kind IN ('movie','series')),
                        title TEXT NOT NULL,
                        original TEXT,
                        title_key TEXT NOT NULL,
                        original_key TEXT,
                        year INTEGER NOT NULL,
                        genres TEXT,
                        rating REAL,
                        votes INTEGER
                    );
                    CREATE TABLE episodes (
                        series_id INTEGER NOT NULL,
                        season INTEGER NOT NULL,
                        number INTEGER NOT NULL,
                        title TEXT NOT NULL,
                        year INTEGER,
                        PRIMARY KEY(series_id,season,number)
                    ) WITHOUT ROWID;
                    CREATE VIRTUAL TABLE title_search USING fts5(
                        title, original, content='titles', content_rowid='title_id',
                        tokenize='unicode61 remove_diacritics 2'
                    );
                    CREATE TEMP TABLE episode_numbers (
                        episode_id INTEGER PRIMARY KEY,
                        series_id INTEGER NOT NULL,
                        season INTEGER NOT NULL,
                        number INTEGER NOT NULL
                    );
                    BEGIN;
                    """
                );
                // Data that IMDb changed so that nothing reads would replace
                // the installed copy with an empty one.
                report(ImdbPhase.Episodes, 0);
                if (ImportEpisodes(connection, Archive(Episodes), percent => report(ImdbPhase.Episodes, percent), cancellation) == 0)
                    throw new VideoException(VideoFailure.UnknownFormat);
                report(ImdbPhase.Titles, 0);
                if (ImportTitles(connection, Archive(Basics), percent => report(ImdbPhase.Titles, percent), cancellation) == 0)
                    throw new VideoException(VideoFailure.UnknownFormat);
                report(ImdbPhase.Ratings, 0);
                if (ImportRatings(connection, Archive(RatingsSource), percent => report(ImdbPhase.Ratings, percent), cancellation) == 0)
                    throw new VideoException(VideoFailure.UnknownFormat);
                report(ImdbPhase.Committing, null);
                Execute(connection, "COMMIT;");
                report(ImdbPhase.Indexing, null);
                Execute(
                    connection,
                    """
                    CREATE INDEX titles_by_key ON titles(title_key);
                    CREATE INDEX titles_by_original ON titles(original_key) WHERE original_key IS NOT NULL;
                    INSERT INTO title_search(title_search) VALUES('rebuild');
                    """
                );
                report(ImdbPhase.Optimizing, null);
                Execute(connection, "INSERT INTO title_search(title_search) VALUES('optimize');");
            }
            finally
            {
                raw.sqlite3_progress_handler(connection.Handle!, 0, null, null);
            }
        }
        cancellation.ThrowIfCancellationRequested();
        report(ImdbPhase.Flushing, null);
        // A power cut after the move must not leave a file whose data
        // never reached the disk.
        using var file = new FileStream(Building, FileMode.Open, FileAccess.ReadWrite);
        file.Flush(flushToDisk: true);
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadPriority(IntPtr thread, int priority);

    // Reads each episode's series and number into the temporary table, so
    // ImportTitles can store the episode under them. An episode without a
    // season or number cannot match a file.
    private static int ImportEpisodes(
        SqliteConnection connection,
        string path,
        Action<int> report,
        CancellationToken cancellation
    )
    {
        using var insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO episode_numbers VALUES($id,$series,$season,$number);";
        var id = insert.Parameters.Add("$id", SqliteType.Integer);
        var series = insert.Parameters.Add("$series", SqliteType.Integer);
        var season = insert.Parameters.Add("$season", SqliteType.Integer);
        var number = insert.Parameters.Add("$number", SqliteType.Integer);
        var count = 0;
        foreach (var fields in Rows(path, EpisodesHeader, report, cancellation))
        {
            // tconst, parentTconst, seasonNumber, episodeNumber
            if (
                fields.Length < 4
                || VideoInformation.ParseImdbId(fields[0]) is not { } identity
                || VideoInformation.ParseImdbId(fields[1]) is not { } parent
                || !int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var seasonNumber)
                || !int.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out var episodeNumber)
            )
                continue;
            id.Value = identity;
            series.Value = parent;
            season.Value = seasonNumber;
            number.Value = episodeNumber;
            insert.ExecuteNonQuery();
            count++;
        }
        return count;
    }

    // Keeps the titles a video file can be: films, TV films and TV specials
    // as movies, series and miniseries, and their episodes. Direct-to-video
    // titles stay out, because IMDb files music videos under the same type.
    // Movies and series need a start year to distinguish same-name releases;
    // that year does not establish whether the title has been released.
    private static int ImportTitles(
        SqliteConnection connection,
        string path,
        Action<int> report,
        CancellationToken cancellation
    )
    {
        using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO titles(title_id,kind,title,original,title_key,original_key,year,genres)
            VALUES($id,$kind,$title,$original,$title_key,$original_key,$year,$genres);
            """;
        var id = insert.Parameters.Add("$id", SqliteType.Integer);
        var kind = insert.Parameters.Add("$kind", SqliteType.Text);
        var title = insert.Parameters.Add("$title", SqliteType.Text);
        var original = insert.Parameters.Add("$original", SqliteType.Text);
        var titleKey = insert.Parameters.Add("$title_key", SqliteType.Text);
        var originalKey = insert.Parameters.Add("$original_key", SqliteType.Text);
        var year = insert.Parameters.Add("$year", SqliteType.Integer);
        var genres = insert.Parameters.Add("$genres", SqliteType.Text);
        // A file names an episode by its number, so a second episode under
        // the same number is ignored.
        using var episode = connection.CreateCommand();
        episode.CommandText = """
            INSERT OR IGNORE INTO episodes(series_id,season,number,title,year)
            SELECT series_id,season,number,$title,$year FROM episode_numbers WHERE episode_id=$id;
            """;
        var episodeId = episode.Parameters.Add("$id", SqliteType.Integer);
        var episodeTitle = episode.Parameters.Add("$title", SqliteType.Text);
        var episodeYear = episode.Parameters.Add("$year", SqliteType.Integer);
        var count = 0;
        foreach (var fields in Rows(path, BasicsHeader, report, cancellation))
        {
            // tconst, titleType, primaryTitle, originalTitle, isAdult,
            // startYear, endYear, runtimeMinutes, genres
            if (fields.Length < 9 || VideoInformation.ParseImdbId(fields[0]) is not { } identity)
                continue;
            int? released = int.TryParse(fields[5], NumberStyles.None, CultureInfo.InvariantCulture, out var value)
                ? value
                : null;
            if (fields[1] == "tvEpisode")
            {
                episodeId.Value = identity;
                episodeTitle.Value = fields[2];
                episodeYear.Value = (object?)released ?? DBNull.Value;
                episode.ExecuteNonQuery();
                continue;
            }
            var titleKind = fields[1] switch
            {
                "movie" or "tvMovie" or "tvSpecial" => "movie",
                "tvSeries" or "tvMiniSeries" => "series",
                _ => null,
            };
            if (titleKind is null || released is null)
                continue;
            var hasOriginal = fields[3] != fields[2] && fields[3] != Empty;
            id.Value = identity;
            kind.Value = titleKind;
            title.Value = fields[2];
            original.Value = hasOriginal ? fields[3] : DBNull.Value;
            titleKey.Value = Key(fields[2]);
            originalKey.Value = hasOriginal ? Key(fields[3]) : DBNull.Value;
            year.Value = released;
            genres.Value = fields[8] == Empty ? DBNull.Value : fields[8];
            insert.ExecuteNonQuery();
            count++;
        }
        return count;
    }

    private static int ImportRatings(
        SqliteConnection connection,
        string path,
        Action<int> report,
        CancellationToken cancellation
    )
    {
        using var update = connection.CreateCommand();
        update.CommandText = "UPDATE titles SET rating=$rating,votes=$votes WHERE title_id=$id;";
        var id = update.Parameters.Add("$id", SqliteType.Integer);
        var rating = update.Parameters.Add("$rating", SqliteType.Real);
        var votes = update.Parameters.Add("$votes", SqliteType.Integer);
        var count = 0;
        foreach (var fields in Rows(path, RatingsHeader, report, cancellation))
        {
            // tconst, averageRating, numVotes
            if (
                fields.Length < 3
                || VideoInformation.ParseImdbId(fields[0]) is not { } identity
                || !double.TryParse(fields[1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
                || !long.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            )
                continue;
            id.Value = identity;
            rating.Value = value;
            votes.Value = number;
            count += update.ExecuteNonQuery();
        }
        return count;
    }

    // The rows of a tab-separated IMDb file after its header, which must name
    // the columns the import reads. Titles contain literal quotes and the
    // files use no quoting, so a line splits on tabs only.
    private static IEnumerable<string[]> Rows(
        string path,
        string header,
        Action<int> report,
        CancellationToken cancellation
    )
    {
        using var file = File.OpenRead(path);
        // The archive is gzip, unless the network unpacked it on the way.
        var packed = file.ReadByte() == 0x1f && file.ReadByte() == 0x8b;
        file.Position = 0;
        using Stream content = packed ? new GZipStream(file, CompressionMode.Decompress) : file;
        using var reader = new StreamReader(content, Encoding.UTF8, bufferSize: 64 * 1024);
        if (reader.ReadLine() != header)
            throw new VideoException(VideoFailure.UnknownFormat);
        var count = 0;
        while (reader.ReadLine() is { } line)
        {
            if (++count % 100_000 == 0)
            {
                cancellation.ThrowIfCancellationRequested();
                report((int)Math.Min(99, file.Position * 100 / file.Length));
            }
            yield return line.Split('\t');
        }
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void Remove(params string[] paths)
    {
        foreach (var path in paths)
            File.Delete(path);
    }
}
