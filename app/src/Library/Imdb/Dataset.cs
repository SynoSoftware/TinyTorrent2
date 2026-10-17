using System.Collections;
using System.Globalization;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SQLitePCL;
using Syno.TinyTorrent.Shared;
using Syno.TinyTorrent.Shared.Network;

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
    private const string FileName = "imdb-2.db";

    // IMDb marks an empty value with \N.
    private const string Empty = "\\N";
    private const string BasicsHeader =
        "tconst\ttitleType\tprimaryTitle\toriginalTitle\tisAdult\tstartYear\tendYear\truntimeMinutes\tgenres";
    private const string RatingsHeader = "tconst\taverageRating\tnumVotes";
    private const string EpisodesHeader = "tconst\tparentTconst\tseasonNumber\tepisodeNumber";

    private static readonly Uri Basics = new("https://datasets.imdbws.com/title.basics.tsv.gz");
    private static readonly Uri RatingsSource = new(
        "https://datasets.imdbws.com/title.ratings.tsv.gz"
    );
    private static readonly Uri Episodes = new("https://datasets.imdbws.com/title.episode.tsv.gz");
    private static readonly Uri[] Sources = [Basics, RatingsSource, Episodes];
    private static readonly ProviderAccess Access = new(
        Provider.CatalogId,
        RequestKey.Digest(string.Empty),
        "dataset"
    );
    private static readonly Comparer<EpisodeNumber> BySeries = Comparer<EpisodeNumber>.Create(
        (a, b) =>
            a.SeriesId != b.SeriesId ? a.SeriesId.CompareTo(b.SeriesId)
            : a.Season != b.Season ? a.Season.CompareTo(b.Season)
            : a.Number != b.Number ? a.Number.CompareTo(b.Number)
            : a.EpisodeId.CompareTo(b.EpisodeId)
    );
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

    /// <summary>
    /// Downloads the datasets and builds the data beside the installed file,
    /// which stays in use until <see cref="Install"/>; <see cref="Discard"/>
    /// deletes a build that is not installed. Archives that a failed import
    /// left complete less than a day ago are imported again without a
    /// download, because IMDb updates the files once a day. A cancel can end
    /// it with the error that the interruption caused instead of a
    /// cancellation. Each phase reports its own progress: a download its
    /// bytes, a source read its approximate position, and the episode and
    /// search-index phases their rows; optimizing and flushing are unknown.
    /// </summary>
    /// <exception cref="VideoException">The download or the import failed.</exception>
    internal async Task Build(Action<ImdbPhase, WorkProgress> report, CancellationToken cancellation)
    {
        var fresh = FreshRequest.Once(Access);
        try
        {
            foreach (var source in Sources)
            {
                var archive = Archive(source);
                if (!IsRecent(archive))
                    await Fetch(
                            source,
                            archive,
                            fresh,
                            progress => report(ImdbPhase.Downloading, progress),
                            cancellation
                        )
                        .ConfigureAwait(false);
            }
            await Task
                .Factory.StartNew(
                    () =>
                    {
                        // The import yields the processor to the person's
                        // other work and keeps normal disk and memory
                        // priority: Windows background mode lowers those too,
                        // and left the import waiting far longer than it
                        // computed whenever other programs were busy. The
                        // thread ends with the task, so nothing restores it.
                        Thread.CurrentThread.Priority = ThreadPriority.Lowest;
                        cancellation.ThrowIfCancellationRequested();
                        Import(report, cancellation);
                    },
                    cancellation,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default
                )
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
                || error is IOException io && DataDownload.IsDiskFull(io)
            )
        {
            throw new VideoException(VideoFailure.DiskFull, error);
        }
        catch (Exception error)
            when (error
                    is HttpRequestException
                        or IOException
                        or UnauthorizedAccessException
                        or SqliteException
            )
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
            Remove([
                .. Directory
                    .GetFiles(directory, "imdb*.db")
                    .Where(path => !string.Equals(path, _path, StringComparison.OrdinalIgnoreCase)),
            ]);
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
                        command.Parameters.AddWithValue(
                            "$titles",
                            JsonSerializer.Serialize(titles)
                        );
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
            .Select(
                (release, position) =>
                    new
                    {
                        Position = position,
                        Name = Key(release.Title),
                        release.Year,
                        release.Season,
                        Episode = release.Episodes.Count > 0 ? release.Episodes[0] : (int?)null,
                        release.IsExtra,
                    }
            )
            .Where(name =>
                !name.IsExtra
                && name.Name.Length > 0
                && (name.Season is not null || name.Year is not null)
            )
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
                                found.Add(
                                    (reader.GetInt32(0), reader.GetInt32(2), ReadRating(reader))
                                );
                            }
                        }
                        foreach (var titles in found.GroupBy(title => title.Position))
                        {
                            var release = releases[titles.Key];
                            var fits = titles.ToArray();
                            if (
                                fits.Length > 1
                                && release.Season is not null
                                && release.Year is { } year
                            )
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
                    when (error.SqliteErrorCode == raw.SQLITE_INTERRUPT
                        && cancellation.IsCancellationRequested
                    )
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
        reader.GetString(reader.GetOrdinal("kind")) == "series"
            ? VideoKind.Series
            : VideoKind.Movie;

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
        foreach (var character in Syno.TinyTorrent.Shared.FileName.Normalize(title))
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

    private string Archive(Uri source) =>
        Path.Combine(directory, Path.GetFileName(source.LocalPath));

    private static bool IsRecent(string path) =>
        new FileInfo(path) is { Exists: true } file
        && file.LastWriteTimeUtc > DateTime.UtcNow.AddDays(-1);

    // Downloads beside the archive and renames the file when it is complete,
    // so an archive that exists holds a whole file.
    private async Task Fetch(
        Uri source,
        string archive,
        FreshRequest fresh,
        Action<WorkProgress> progress,
        CancellationToken cancellation
    )
    {
        var part = archive + ".part";
        using var request = new HttpRequestMessage(HttpMethod.Get, source);
        var call = new ProviderCall(request, policy, new(Access)) { Fresh = fresh };
        try
        {
            await http.Download(
                    call with
                    {
                        Observe = reply => ProviderJson.Observe(call, reply),
                    },
                    part,
                    progress,
                    cancellation
                )
                .ConfigureAwait(false);
            File.Move(part, archive, overwrite: true);
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
    private void Import(Action<ImdbPhase, WorkProgress> report, CancellationToken cancellation)
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
                    // title from a file name. An episode's numbers and its
                    // title come from different files, so they are two
                    // tables, each written in its own key order.
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
                        episode_id INTEGER NOT NULL,
                        PRIMARY KEY(series_id,season,number)
                    ) WITHOUT ROWID;
                    CREATE TABLE episode_titles (
                        episode_id INTEGER PRIMARY KEY,
                        title TEXT NOT NULL,
                        year INTEGER
                    );
                    CREATE VIRTUAL TABLE title_search USING fts5(
                        title, original, content='titles', content_rowid='title_id',
                        tokenize='unicode61 remove_diacritics 2', columnsize=0
                    );
                    BEGIN;
                    """
                );
                // Data that IMDb changed so that nothing reads would replace
                // the installed copy with an empty one.
                var numbered = LinkEpisodes(
                    connection,
                    ReadEpisodes(Archive(Episodes), Reading(ImdbPhase.Episodes), cancellation),
                    Counting(ImdbPhase.Linking),
                    cancellation
                );
                var ratings = ReadRatings(
                    Archive(RatingsSource),
                    Reading(ImdbPhase.Ratings),
                    cancellation
                );
                var (titles, rated) = ImportTitles(
                    connection,
                    Archive(Basics),
                    numbered,
                    CollectionsMarshal.AsSpan(ratings),
                    Reading(ImdbPhase.Titles),
                    cancellation
                );
                if (titles == 0 || rated == 0)
                    throw new VideoException(VideoFailure.UnknownFormat);
                IndexTitles(connection, titles, Counting(ImdbPhase.Indexing), cancellation);
                Execute(connection, "COMMIT;");
                report(ImdbPhase.Optimizing, new WorkProgress.Unknown());
                Execute(connection, "INSERT INTO title_search(title_search) VALUES('optimize');");
            }
            finally
            {
                raw.sqlite3_progress_handler(connection.Handle!, 0, null, null);
            }
        }
        cancellation.ThrowIfCancellationRequested();
        report(ImdbPhase.Flushing, new WorkProgress.Unknown());
        // A power cut after the move must not leave a file whose data
        // never reached the disk.
        using var file = new FileStream(Building, FileMode.Open, FileAccess.ReadWrite);
        file.Flush(flushToDisk: true);

        // A source's position in its compressed file is approximate progress
        // through it; the phases that write rows count them exactly.
        Action<int> Reading(ImdbPhase phase) =>
            percent =>
                report(
                    phase,
                    new WorkProgress.Fraction(percent, 100, WorkUnit.Percent, approximate: true)
                );

        Action<int, int> Counting(ImdbPhase phase) =>
            (done, total) =>
                report(phase, new WorkProgress.Fraction(done, total, WorkUnit.Percent));
    }

    // An episode's place in its series, from title.episode. The import holds
    // every episode's in memory, so each field is an int, which IMDb's
    // eight-digit identifiers fit.
    private readonly record struct EpisodeNumber(
        int EpisodeId,
        int SeriesId,
        int Season,
        int Number
    );

    // Reads each episode's series, season and number. An episode without a
    // season or number cannot match a file.
    private static List<EpisodeNumber> ReadEpisodes(
        string path,
        Action<int> report,
        CancellationToken cancellation
    )
    {
        var numbers = new List<EpisodeNumber>();
        Span<Range> fields = stackalloc Range[4];
        foreach (var line in Rows(path, EpisodesHeader, report, cancellation))
        {
            // tconst, parentTconst, seasonNumber, episodeNumber
            var row = line.AsSpan();
            if (
                row.Split(fields, '\t') < 4
                || Identity(row[fields[0]]) is not { } episode
                || Identity(row[fields[1]]) is not { } series
                || !int.TryParse(
                    row[fields[2]],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var season
                )
                || !int.TryParse(
                    row[fields[3]],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var number
                )
            )
                continue;
            numbers.Add(new(episode, series, season, number));
        }
        return numbers;
    }

    // The number of an IMDb ID as EpisodeNumber holds it; one too large for
    // it is treated as malformed.
    private static int? Identity(ReadOnlySpan<char> id) =>
        VideoInformation.ParseImdbId(id) is { } number && number <= int.MaxValue
            ? (int)number
            : null;

    // Stores each episode under its series, season and number in that order,
    // so the table grows at its end instead of at a random page for every row.
    // A file names an episode by its number, so duplicates keep the lower ID.
    private static BitArray LinkEpisodes(
        SqliteConnection connection,
        List<EpisodeNumber> episodes,
        Action<int, int> report,
        CancellationToken cancellation
    )
    {
        var numbers = CollectionsMarshal.AsSpan(episodes);
        if (numbers.Length == 0)
            throw new VideoException(VideoFailure.UnknownFormat);
        numbers.Sort(BySeries);
        var maximum = 0;
        foreach (var episode in numbers)
            maximum = Math.Max(maximum, episode.EpisodeId);
        // Positive IDs start at bit zero so int.MaxValue also fits; only
        // membership survives this phase.
        var numbered = new BitArray(maximum);
        using var insert = new Insert(connection, "INSERT OR IGNORE INTO episodes", 4);
        for (var done = 0; done < numbers.Length; done++)
        {
            if (done % 100_000 == 0)
            {
                cancellation.ThrowIfCancellationRequested();
                report(done, numbers.Length);
            }
            var episode = numbers[done];
            numbered[episode.EpisodeId - 1] = true;
            insert.Bind(1, episode.SeriesId);
            insert.Bind(2, episode.Season);
            insert.Bind(3, episode.Number);
            insert.Bind(4, episode.EpisodeId);
            insert.AddRow();
        }
        insert.Flush();
        return numbered;
    }

    // Keeps the titles a video file can be: films, TV films and TV specials
    // as movies, series and miniseries, and their episodes. Direct-to-video
    // titles stay out, because IMDb files music videos under the same type.
    // Movies and series need a start year to distinguish same-name releases;
    // that year does not establish whether the title has been released. A
    // title's rating goes in with its row, because updating the rated titles
    // afterwards reads a random page for each of them.
    private static (int Titles, int Rated) ImportTitles(
        SqliteConnection connection,
        string path,
        BitArray numbered,
        ReadOnlySpan<TitleRating> ratings,
        Action<int> report,
        CancellationToken cancellation
    )
    {
        using var insert = new Insert(
            connection,
            """
            INSERT INTO titles(title_id,kind,title,original,title_key,original_key,year,genres,rating,votes)
            """,
            10
        );
        using var episode = new Insert(connection, "INSERT INTO episode_titles", 3);
        var count = 0;
        var rated = 0;
        Span<Range> fields = stackalloc Range[9];
        foreach (var line in Rows(path, BasicsHeader, report, cancellation))
        {
            // tconst, titleType, primaryTitle, originalTitle, isAdult,
            // startYear, endYear, runtimeMinutes, genres
            var row = line.AsSpan();
            if (
                row.Split(fields, '\t') < 9
                || VideoInformation.ParseImdbId(row[fields[0]]) is not { } identity
            )
                continue;
            int? released = int.TryParse(
                row[fields[5]],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var value
            )
                ? value
                : null;
            if (row[fields[1]] is "tvEpisode")
            {
                if (identity > numbered.Length || !numbered[(int)identity - 1])
                    continue;
                episode.Bind(1, identity);
                episode.Bind(2, line[fields[2]]);
                episode.Bind(3, released);
                episode.AddRow();
                continue;
            }
            var titleKind = row[fields[1]] switch
            {
                "movie" or "tvMovie" or "tvSpecial" => "movie",
                "tvSeries" or "tvMiniSeries" => "series",
                _ => null,
            };
            if (titleKind is null || released is null)
                continue;
            var name = line[fields[2]];
            var originalName =
                row[fields[3]] is Empty || row[fields[3]].SequenceEqual(row[fields[2]])
                    ? null
                    : line[fields[3]];
            insert.Bind(1, identity);
            insert.Bind(2, titleKind);
            insert.Bind(3, name);
            insert.Bind(4, originalName);
            insert.Bind(5, Key(name));
            insert.Bind(6, originalName is null ? null : Key(originalName));
            insert.Bind(7, released);
            insert.Bind(8, row[fields[8]] is Empty ? null : line[fields[8]]);
            var position = ratings.BinarySearch(new TitleRating(identity, 0, 0));
            insert.Bind(9, position < 0 ? null : ratings[position].Value);
            insert.Bind(10, position < 0 ? null : ratings[position].Votes);
            if (position >= 0)
                rated++;
            insert.AddRow();
            count++;
        }
        insert.Flush();
        episode.Flush();
        return (count, rated);
    }

    // A title's rating from title.ratings; ordering by title lets ImportTitles
    // find one by binary search.
    private readonly record struct TitleRating(long TitleId, double Value, long Votes)
        : IComparable<TitleRating>
    {
        public int CompareTo(TitleRating other) => TitleId.CompareTo(other.TitleId);
    }

    private static List<TitleRating> ReadRatings(
        string path,
        Action<int> report,
        CancellationToken cancellation
    )
    {
        var ratings = new List<TitleRating>();
        Span<Range> fields = stackalloc Range[3];
        foreach (var line in Rows(path, RatingsHeader, report, cancellation))
        {
            // tconst, averageRating, numVotes
            var row = line.AsSpan();
            if (
                row.Split(fields, '\t') < 3
                || VideoInformation.ParseImdbId(row[fields[0]]) is not { } identity
                || !double.TryParse(
                    row[fields[1]],
                    NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture,
                    out var value
                )
                || !long.TryParse(
                    row[fields[2]],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var number
                )
            )
                continue;
            ratings.Add(new(identity, value, number));
        }
        CollectionsMarshal.AsSpan(ratings).Sort();
        return ratings;
    }

    // SQL copies each batch's text directly into FTS without a managed
    // round trip per title. Counting the keys keeps progress exact; the key
    // indexes report nothing, so the count stays at zero while they build.
    private static void IndexTitles(
        SqliteConnection connection,
        int total,
        Action<int, int> report,
        CancellationToken cancellation
    )
    {
        report(0, total);
        Execute(
            connection,
            """
            CREATE INDEX titles_by_key ON titles(title_key);
            CREATE INDEX titles_by_original ON titles(original_key) WHERE original_key IS NOT NULL;
            """
        );
        using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO title_search(rowid,title,original)
            SELECT title_id,title,original FROM titles
            WHERE title_id>$after AND title_id<=$through ORDER BY title_id;
            """;
        var after = insert.Parameters.AddWithValue("$after", 0L);
        var through = insert.Parameters.Add("$through", SqliteType.Integer);
        using var select = connection.CreateCommand();
        select.CommandText = "SELECT title_id FROM titles ORDER BY title_id;";
        using var reader = select.ExecuteReader();
        var done = 0;
        while (reader.Read())
        {
            if (++done % 10_000 != 0 && done != total)
                continue;
            cancellation.ThrowIfCancellationRequested();
            through.Value = reader.GetInt64(0);
            insert.ExecuteNonQuery();
            after.Value = through.Value;
            report(done, total);
        }
    }

    // 100 rows a statement remove most of the per-execution cost, which
    // dominates the narrow episode rows; the widest insert binds 1,000 of
    // SQLite's 32,766 parameters in about 2 KB of SQL. Bound values keep title
    // length out of the SQL. Buffering lets the final batch use a VALUES
    // clause with exactly its remaining rows.
    private sealed class Insert(SqliteConnection connection, string sql, int columns) : IDisposable
    {
        private const int Capacity = 100;
        private readonly (long? Number, double? Real, string? Text)[] _values =
            new (long?, double?, string?)[Capacity * columns];
        private Statement? _statement;
        private int _preparedRows;
        private int _rows;

        internal void Bind(int index, long? value) =>
            _values[_rows * columns + index - 1] = (value, null, null);

        internal void Bind(int index, double? value) =>
            _values[_rows * columns + index - 1] = (null, value, null);

        internal void Bind(int index, string? value) =>
            _values[_rows * columns + index - 1] = (null, null, value);

        internal void AddRow()
        {
            if (++_rows == Capacity)
                Flush();
        }

        internal void Flush()
        {
            if (_rows == 0)
                return;
            if (_preparedRows != _rows)
            {
                _statement?.Dispose();
                var row = "(" + string.Join(",", Enumerable.Repeat("?", columns)) + ")";
                _statement = new Statement(
                    connection,
                    sql + " VALUES " + string.Join(",", Enumerable.Repeat(row, _rows))
                );
                _preparedRows = _rows;
            }
            for (var index = 0; index < _rows * columns; index++)
            {
                var value = _values[index];
                if (value.Text is { } text)
                    _statement!.Bind(index + 1, text);
                else if (value.Real is { } real)
                    _statement!.Bind(index + 1, real);
                else
                    _statement!.Bind(index + 1, value.Number);
            }
            _statement!.Execute();
            _rows = 0;
        }

        public void Dispose() => _statement?.Dispose();
    }

    // Millions of writes avoid SqliteCommand's per-execution parameter lookup
    // and reader allocation. Every row binds every parameter, including nulls,
    // because resetting a native statement retains its bindings.
    private sealed class Statement : IDisposable
    {
        private readonly sqlite3 _connection;
        private readonly sqlite3_stmt _statement;

        internal Statement(SqliteConnection connection, string sql)
        {
            _connection = connection.Handle!;
            Check(raw.sqlite3_prepare_v2(_connection, sql, out _statement));
        }

        internal void Bind(int index, long? value) =>
            Check(
                value is { } number
                    ? raw.sqlite3_bind_int64(_statement, index, number)
                    : raw.sqlite3_bind_null(_statement, index)
            );

        internal void Bind(int index, double value) =>
            Check(raw.sqlite3_bind_double(_statement, index, value));

        internal void Bind(int index, string value) =>
            Check(raw.sqlite3_bind_text(_statement, index, value));

        internal void Execute()
        {
            Check(raw.sqlite3_step(_statement));
            Check(raw.sqlite3_reset(_statement));
        }

        private void Check(int code) => SqliteException.ThrowExceptionForRC(code, _connection);

        public void Dispose() => _statement.Dispose();
    }

    // The rows of a tab-separated IMDb file after its header, which must name
    // the columns the import reads. Titles contain literal quotes and the
    // files use no quoting, so a row's fields are the text between its tabs.
    private static IEnumerable<string> Rows(
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
        report(0);
        while (reader.ReadLine() is { } line)
        {
            if (++count % 100_000 == 0)
            {
                cancellation.ThrowIfCancellationRequested();
                report((int)Math.Min(99, file.Position * 100 / file.Length));
            }
            yield return line;
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
