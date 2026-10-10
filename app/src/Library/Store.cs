using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Syno.TinyTorrent.Models;
using Syno.TinyTorrent.Services;

namespace Syno.TinyTorrent.Library;

internal sealed partial class Store(Database database)
{
    internal const string Invalidate = "DROP TABLE IF EXISTS library_rows;";

    // Stored lists such as genres keep this separator, which the Genres filter
    // splits on.
    internal const string Separator = " · ";

    // The entries of the library_rows snapshot. Invalidate drops that table,
    // and the next Search rebuilds both.
    private readonly Dictionary<long, Entry> _cache = [];

#if CAPTURE
    internal Task<int> PrepareTiming() => database.Run(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TEMP TABLE timing_videos AS
            SELECT * FROM current_files WHERE kind=0 ORDER BY entry_id LIMIT 1000;
            INSERT INTO video_information(catalog_id,video_id,language,title,type,year,genres,cast,synopsis,keywords,search,retrieved)
            SELECT 'timing',CAST(entry_id AS TEXT),'en','Coastal mystery ' || entry_id,'movie',2024,
                'Drama · Mystery','Example Actor',
                'An investigator returns to a coastal town after a fishing boat disappears. As a storm approaches, conflicting accounts draw old friends into a search that reveals a secret the community has kept for years.',
                'investigator coastal mystery','investigator coastal mystery example actor',1700000000
            FROM timing_videos;
            WITH samples AS (
                SELECT CAST(entry_id AS TEXT) AS video_id,row_number() OVER (ORDER BY entry_id) AS position
                FROM timing_videos ORDER BY entry_id LIMIT 2
            )
            UPDATE video_information SET title=title || ' (sample ratings)',
                imdb_rating=CASE samples.position WHEN 1 THEN 7.8 ELSE 6.9 END,imdb_kind='movie',
                tmdb_rating=CASE samples.position WHEN 1 THEN 8.1 ELSE 7.2 END,tmdb_kind='movie'
            FROM samples WHERE catalog_id='timing' AND video_information.video_id=samples.video_id;
            INSERT INTO identifications(origin,file_index,catalog_id,video_id,explicit,decided,evidence)
            SELECT f.origin,f.file_index,'timing',CAST(f.entry_id AS TEXT),1,1700000000,f.name
            FROM source_files f JOIN timing_videos v USING(entry_id);
            INSERT OR REPLACE INTO file_facts(origin,file_index,created,modified,title,artist,album,track,year,genre,duration,search)
            SELECT origin,file_index,1700000000,1700000100,
                CASE WHEN kind=1 THEN 'Track ' || file_index ELSE '' END,
                CASE WHEN kind=1 THEN 'Example Artist' ELSE '' END,
                CASE WHEN kind=1 THEN 'Example Album' ELSE '' END,
                CASE WHEN kind=1 THEN file_index%12+1 END,
                CASE WHEN kind=1 THEN 2024 END,
                CASE WHEN kind=1 THEN 'Rock' ELSE '' END,
                CASE WHEN kind=1 THEN 180 END,
                CASE WHEN kind=1 THEN 'example artist album rock' ELSE '' END
            FROM source_files;
            DROP TABLE timing_videos;
            """ + Invalidate;
        command.ExecuteNonQuery();
        command.CommandText = "SELECT COUNT(*) FROM video_information WHERE catalog_id='timing';";
        return Convert.ToInt32(command.ExecuteScalar());
    });
#endif

    private const string Matches = "library_matches m JOIN library_rows r ON r.rowid=m.row_id";

    internal const string Schema = """
        CREATE TABLE video_information (
            catalog_id TEXT NOT NULL, video_id TEXT NOT NULL, language TEXT NOT NULL,
            title TEXT NOT NULL, type TEXT NOT NULL, year INTEGER, genres TEXT NOT NULL,
            cast TEXT NOT NULL, synopsis TEXT NOT NULL, keywords TEXT NOT NULL, search TEXT NOT NULL, retrieved INTEGER NOT NULL,
            series_catalog TEXT, series_id TEXT, series_type TEXT, series_title TEXT, series_year INTEGER,
            PRIMARY KEY(catalog_id,video_id)
        );
        CREATE TABLE identifications (
            origin TEXT NOT NULL, file_index INTEGER NOT NULL, catalog_id TEXT, video_id TEXT,
            explicit INTEGER NOT NULL, decided INTEGER NOT NULL, evidence TEXT NOT NULL,
            PRIMARY KEY(origin,file_index)
        );
        CREATE TABLE video_records (
            catalog_id TEXT NOT NULL, path TEXT NOT NULL, language TEXT NOT NULL,
            content TEXT NOT NULL, retrieved INTEGER NOT NULL,
            PRIMARY KEY(catalog_id,path,language)
        );
        CREATE TABLE video_references (
            catalog_id TEXT NOT NULL, video_id TEXT NOT NULL, path TEXT NOT NULL, language TEXT NOT NULL,
            PRIMARY KEY(catalog_id,video_id,path,language),
            FOREIGN KEY(catalog_id,video_id) REFERENCES video_information(catalog_id,video_id) ON DELETE CASCADE
        );
        CREATE TABLE video_attempts (
            origin TEXT NOT NULL, file_index INTEGER NOT NULL, catalog_id TEXT NOT NULL,
            evidence TEXT NOT NULL, request TEXT NOT NULL, outcome TEXT, video_id TEXT,
            PRIMARY KEY(origin,file_index,catalog_id)
        );
        CREATE TABLE file_facts (
            origin TEXT NOT NULL, file_index INTEGER NOT NULL, created INTEGER, modified INTEGER,
            title TEXT NOT NULL, artist TEXT NOT NULL, album TEXT NOT NULL, track INTEGER,
            year INTEGER, genre TEXT NOT NULL, duration INTEGER, search TEXT NOT NULL,
            PRIMARY KEY(origin,file_index)
        );
        CREATE TABLE library_settings (
            setting TEXT PRIMARY KEY, value TEXT NOT NULL
        );
        CREATE TABLE provider_settings (
            section TEXT NOT NULL, setting TEXT NOT NULL, value TEXT NOT NULL,
            PRIMARY KEY(section,setting)
        );
        """;

    internal const string Information = """
        ALTER TABLE video_information ADD COLUMN imdb_rating REAL CHECK(imdb_rating BETWEEN 0 AND 10);
        ALTER TABLE video_information ADD COLUMN imdb_kind TEXT CHECK(imdb_kind IN ('movie','tv'));
        ALTER TABLE video_information ADD COLUMN tmdb_rating REAL CHECK(tmdb_rating BETWEEN 0 AND 10);
        ALTER TABLE video_information ADD COLUMN tmdb_kind TEXT CHECK(tmdb_kind IN ('movie','tv'));
        ALTER TABLE video_information ADD COLUMN episodes TEXT;
        """;

    internal const string Projection = """
        CREATE TEMP VIEW library_entries AS
        SELECT f.entry_id, f.path, f.name, f.size, f.kind,
            COALESCE(NULLIF(v.title,''), NULLIF(p.title,''), f.title) AS title,
            COALESCE(v.type,'') AS type, COALESCE(v.year,p.year) AS year,
            COALESCE(v.genres,p.genre,'') AS genres,
            CASE WHEN length(v.cast)>160 THEN substr(v.cast,1,160)||'…' ELSE COALESCE(v.cast,'') END AS cast,
            COALESCE(p.artist,'') AS artist, COALESCE(p.album,'') AS album,
            p.track, p.duration, p.created, p.modified,
            f.search AS file_search,
            v.video_id, COALESCE(p.search,'') AS music_search, v.catalog_id,
            v.imdb_rating, v.imdb_kind, v.tmdb_rating, v.tmdb_kind,
            v.series_catalog, v.series_id, v.series_type, v.series_title, v.episodes
        FROM current_files f
        LEFT JOIN identifications i ON (i.origin,i.file_index) = (
            SELECT d.origin,d.file_index FROM identifications d
            JOIN source_files s ON s.origin=d.origin AND s.file_index=d.file_index
            WHERE s.entry_id=f.entry_id AND (d.explicit=1 OR d.evidence=s.name)
            ORDER BY d.explicit DESC,d.decided DESC LIMIT 1)
        LEFT JOIN video_information v ON v.catalog_id=i.catalog_id AND v.video_id=i.video_id
        LEFT JOIN file_facts p ON (p.origin,p.file_index) = (
            SELECT d.origin,d.file_index FROM file_facts d
            JOIN source_files s ON s.origin=d.origin AND s.file_index=d.file_index
            WHERE s.entry_id=f.entry_id LIMIT 1)
        WHERE f.complete=1;
        """;

    internal const string Cleanup = """
        DELETE FROM video_attempts WHERE NOT EXISTS (
            SELECT 1 FROM pending_origins p WHERE p.origin=video_attempts.origin)
        AND NOT EXISTS (SELECT 1 FROM collected_files f
            WHERE f.origin=video_attempts.origin AND f.file_index=video_attempts.file_index);
        DELETE FROM identifications WHERE NOT EXISTS (
            SELECT 1 FROM pending_origins p WHERE p.origin=identifications.origin)
        AND NOT EXISTS (SELECT 1 FROM collected_files f
            WHERE f.origin=identifications.origin AND f.file_index=identifications.file_index);
        DELETE FROM file_facts WHERE NOT EXISTS (
            SELECT 1 FROM pending_origins p WHERE p.origin=file_facts.origin)
        AND NOT EXISTS (SELECT 1 FROM collected_files f
            WHERE f.origin=file_facts.origin AND f.file_index=file_facts.file_index);
        DELETE FROM video_information WHERE NOT EXISTS (
            SELECT 1 FROM video_attempts a WHERE a.catalog_id=video_information.catalog_id
                AND a.video_id=video_information.video_id)
        AND NOT EXISTS (SELECT 1 FROM identifications i WHERE i.catalog_id=video_information.catalog_id
            AND i.video_id=video_information.video_id);
        """ + VideoCleanup;

    internal const string Reconcile = """
        WITH choices AS (
            SELECT f.entry_id,i.*,
                row_number() OVER (PARTITION BY f.entry_id ORDER BY i.explicit DESC,i.decided DESC) AS rank
            FROM source_files f JOIN identifications i USING(origin,file_index)
        )
        INSERT INTO identifications(origin,file_index,catalog_id,video_id,explicit,decided,evidence)
        SELECT f.origin,f.file_index,c.catalog_id,c.video_id,c.explicit,c.decided,f.name
        FROM source_files f JOIN choices c ON c.entry_id=f.entry_id AND c.rank=1
        WHERE c.explicit=1
        ON CONFLICT(origin,file_index) DO UPDATE SET catalog_id=excluded.catalog_id,video_id=excluded.video_id,
            explicit=excluded.explicit,decided=excluded.decided,evidence=excluded.evidence;
        """;

    internal Task<Matches> Search(Query query, CancellationToken cancellation) =>
        database.Run(connection =>
        {
            var terms = FileName.Normalize(query.Text)
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Distinct(StringComparer.Ordinal).ToArray();
            using var snapshot = connection.CreateCommand();
            snapshot.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_temp_master WHERE name='library_rows' AND type='table');";
            if (Convert.ToInt64(snapshot.ExecuteScalar()) == 0)
            {
                _cache.Clear();
                snapshot.CommandText = "CREATE TEMP TABLE library_rows AS SELECT * FROM library_entries;";
                snapshot.ExecuteNonQuery();
                snapshot.CommandText = "SELECT * FROM library_rows;";
                using var reader = snapshot.ExecuteReader();
                while (reader.Read())
                {
                    cancellation.ThrowIfCancellationRequested();
                    var entry = Read(reader);
                    _cache.Add(entry.EntryId, entry);
                }
            }
            var text = new StringBuilder("1=1");
            for (var index = 0; index < terms.Length; index++)
            {
                var parameter = "$term" + index;
                text.Append($"""
                     AND (instr(r.file_search,{parameter})>0 OR instr(r.music_search,{parameter})>0 OR
                        (r.catalog_id,r.video_id) IN (SELECT v.catalog_id,v.video_id FROM video_information v
                            WHERE (v.catalog_id,v.video_id) IN (SELECT catalog_id,video_id FROM library_rows WHERE video_id IS NOT NULL)
                                AND instr(v.search,{parameter})>0))
                    """);
                snapshot.Parameters.AddWithValue(parameter, terms[index]);
            }
            snapshot.CommandText = $"CREATE TEMP TABLE library_matches AS SELECT r.rowid AS row_id FROM library_rows r WHERE {text};";
            snapshot.ExecuteNonQuery();
            var entries = new List<Entry>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = $"SELECT r.entry_id FROM {Matches} WHERE " + Match(command, query);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    cancellation.ThrowIfCancellationRequested();
                    entries.Add(_cache[reader.GetInt64(0)]);
                }
            }
            var facets = new List<Facet>();
            var sections = query.Configuration switch
            {
                LibraryConfiguration.Videos => new[] { "type", "genres", "year" }
                    .Concat(query.Filters.Keys.Where(section => section is "series" or "season")).ToArray(),
                LibraryConfiguration.Music => new[] { "genres", "year" }
                    .Concat(query.Filters.Keys.Where(section => section is "artist" or "album")).ToArray(),
                _ => new[] { "kind", "modified", "size" }
                    .Concat(query.Filters.Keys.Where(section => section is "year" or "genres" or "artist" or "album" or "series" or "season")).ToArray(),
            };
            if (query.Filters.ContainsKey("series"))
                sections = sections.Where(section => section != "season")
                    .SelectMany(section => section == "series" ? new[] { "series", "season" } : new[] { section }).ToArray();
            foreach (var section in sections)
            {
                cancellation.ThrowIfCancellationRequested();
                using var command = connection.CreateCommand();
                var predicate = Match(command, query, section);
                if (section is "modified" or "size")
                {
                    var values = section == "modified" ? Dates : Sizes;
                    command.CommandText = "SELECT " + string.Join(",", values.Select(value =>
                        $"COUNT(CASE WHEN {Filter(section, value)} THEN 1 END)")) +
                        $" FROM {Matches} WHERE {predicate};";
                    using var reader = command.ExecuteReader();
                    reader.Read();
                    for (var index = 0; index < values.Length; index++)
                    {
                        var value = values[index];
                        var count = reader.GetInt32(index);
                        if (count > 0 || query.Filters.GetValueOrDefault(section) == value)
                            facets.Add(new(section, value, count));
                    }
                }
                else if (section == "series")
                {
                    command.CommandText = $"""
                        SELECT r.series_catalog,r.series_id,r.series_type,r.series_title,COUNT(*)
                        FROM {Matches} WHERE {predicate} AND r.series_id IS NOT NULL
                        GROUP BY r.series_catalog,r.series_id,r.series_type;
                        """;
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        var series = new VideoChoice(reader.GetString(0), reader.GetString(1), Kind(reader.GetString(2)), reader.GetString(3));
                        facets.Add(new(section, SeriesKey(series), reader.GetInt32(4)) { Label = series.Title });
                    }
                }
                else if (section == "season")
                {
                    command.CommandText = $"""
                        SELECT json_extract(e.value,'$.Season'),COUNT(DISTINCT r.entry_id)
                        FROM {Matches},json_each(r.episodes) e WHERE {predicate}
                        GROUP BY json_extract(e.value,'$.Season');
                        """;
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                        facets.Add(new(section, reader.GetInt32(0).ToString(CultureInfo.InvariantCulture), reader.GetInt32(1)));
                }
                else if (section == "genres")
                {
                    command.CommandText = $"""
                        SELECT j.value, COUNT(*) FROM {Matches},
                            json_each('[' || replace(json_quote(genres),'{Separator}','","') || ']') j
                        WHERE {predicate} AND j.value<>'' GROUP BY j.value;
                        """;
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                        facets.Add(new(section, reader.GetString(0), reader.GetInt32(1)));
                }
                else
                {
                    var populated = section is "artist" or "album" ? $" AND {section}<>''" : string.Empty;
                    command.CommandText = $"SELECT {section}, COUNT(*) FROM {Matches} WHERE {predicate} AND {section} IS NOT NULL{populated} GROUP BY {section};";
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                        facets.Add(new(section, Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture)!, reader.GetInt32(1)));
                }
                if (query.Filters.TryGetValue(section, out var selected) &&
                    !facets.Any(facet => facet.Section == section && facet.Value == selected))
                {
                    string? label = null;
                    if (section == "series")
                    {
                        command.Parameters.Clear();
                        command.CommandText = "SELECT series_title FROM video_information r WHERE " + SeriesMatch(command, selected) + " LIMIT 1;";
                        label = command.ExecuteScalar() as string;
                    }
                    facets.Add(new(section, selected, 0) { Label = label });
                }
            }
            var counts = new Dictionary<LibraryConfiguration, int>();
            using (var command = connection.CreateCommand())
            {
                var configurations = Enum.GetValues<LibraryConfiguration>();
                var predicates = new List<string>(configurations.Length);
                foreach (var configuration in configurations)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var predicate = Match(command, query with { Configuration = configuration });
                    predicates.Add($"COUNT(CASE WHEN {predicate} THEN 1 END)");
                }
                command.CommandText = $"SELECT {string.Join(',', predicates)} FROM {Matches};";
                using var reader = command.ExecuteReader();
                reader.Read();
                for (var index = 0; index < configurations.Length; index++)
                    counts[configurations[index]] = reader.GetInt32(index);
            }
            snapshot.CommandText = "DROP TABLE library_matches;";
            snapshot.ExecuteNonQuery();
            return new Matches(entries, facets, counts);
        }, cancellation);

    internal Task<Detail?> Detail(long entryId, CancellationToken cancellation) =>
        database.Run(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT r.*,COALESCE(v.cast,'') AS full_cast,COALESCE(v.synopsis,'') AS synopsis
                FROM library_entries r LEFT JOIN video_information v USING(catalog_id,video_id) WHERE r.entry_id=$entry;
                """;
            command.Parameters.AddWithValue("$entry", entryId);
            Entry entry;
            string cast;
            string synopsis;
            VideoChoice? series;
            IReadOnlyList<Episode> episodes;
            using (var reader = command.ExecuteReader())
            {
                if (!reader.Read())
                    return null;
                entry = Read(reader);
                cast = reader.GetString(reader.GetOrdinal("full_cast"));
                synopsis = reader.GetString(reader.GetOrdinal("synopsis"));
                var identity = reader.GetOrdinal("series_id");
                series = reader.IsDBNull(identity) ? null : new VideoChoice(
                    reader.GetString(reader.GetOrdinal("series_catalog")), reader.GetString(identity),
                    Kind(reader.GetString(reader.GetOrdinal("series_type"))), reader.GetString(reader.GetOrdinal("series_title")));
                var ordinal = reader.GetOrdinal("episodes");
                episodes = reader.IsDBNull(ordinal) ? [] : JsonSerializer.Deserialize<Episode[]>(reader.GetString(ordinal)) ?? [];
            }
            command.CommandText = """
                SELECT DISTINCT c.origin,c.name,f.file_index FROM source_files f
                JOIN contributions c USING(origin) WHERE f.entry_id=$entry;
                """;
            var origins = new List<Origin>();
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                    origins.Add(new(reader.GetString(0), reader.GetString(1), reader.GetInt32(2)));
            return new Detail(entry, cast, synopsis, origins) { Series = series, Episodes = episodes };
        }, cancellation);

    internal Task EraseMetadata(Entry entry, CancellationToken cancellation) =>
        database.Run(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM collected_files f " +
                "WHERE entry_id=$entry AND path=$path COLLATE PATH AND f.name=$name);";
            command.Parameters.AddWithValue("$entry", entry.EntryId);
            command.Parameters.AddWithValue("$path", entry.Path);
            command.Parameters.AddWithValue("$name", entry.Name);
            if (Convert.ToInt64(command.ExecuteScalar()) == 0)
                throw new VideoException("library", "file_changed");
            SaveIdentification(connection, entry.EntryId, null, true);
            return true;
        }, cancellation);

    private static void SaveIdentification(SqliteConnection connection, long entryId, VideoInformation? video, bool isExplicit)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO identifications(origin,file_index,catalog_id,video_id,explicit,decided,evidence)
            SELECT origin,file_index,$catalog,$video,$explicit,$time,name FROM source_files WHERE entry_id=$entry
            ON CONFLICT(origin,file_index) DO UPDATE SET catalog_id=excluded.catalog_id,video_id=excluded.video_id,
                explicit=excluded.explicit,decided=excluded.decided,evidence=excluded.evidence;
            """;
        command.Parameters.AddWithValue("$entry", entryId);
        command.Parameters.AddWithValue("$catalog", (object?)video?.CatalogId ?? DBNull.Value);
        command.Parameters.AddWithValue("$video", (object?)video?.VideoId ?? DBNull.Value);
        command.Parameters.AddWithValue("$explicit", isExplicit);
        command.Parameters.AddWithValue("$time", DateTime.UtcNow.Ticks);
        command.ExecuteNonQuery();
        command.CommandText = """
            UPDATE video_attempts SET request='',outcome=COALESCE(outcome,'cancelled')
            WHERE (origin,file_index) IN (SELECT origin,file_index FROM source_files WHERE entry_id=$entry);
            """ + Cleanup + Invalidate;
        command.ExecuteNonQuery();
    }

    private static void SaveVideo(SqliteConnection connection, VideoInformation video, string? language)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO video_information(catalog_id,video_id,language,title,type,year,genres,cast,synopsis,keywords,search,retrieved,
                series_catalog,series_id,series_type,series_title,series_year,imdb_rating,imdb_kind,tmdb_rating,tmdb_kind,episodes) VALUES (
                $catalog,$id,$language,$title,$type,$year,$genres,$cast,$synopsis,$keywords,$search,$retrieved,
                $seriesCatalog,$seriesId,$seriesType,$seriesTitle,$seriesYear,$imdbRating,$imdbKind,$tmdbRating,$tmdbKind,$episodes)
            ON CONFLICT(catalog_id,video_id) DO UPDATE SET language=excluded.language,
                title=excluded.title,type=excluded.type,year=excluded.year,genres=excluded.genres,
                cast=excluded.cast,synopsis=excluded.synopsis,keywords=excluded.keywords,
                search=excluded.search,retrieved=excluded.retrieved,
                series_catalog=excluded.series_catalog,series_id=excluded.series_id,series_type=excluded.series_type,
                series_title=excluded.series_title,series_year=excluded.series_year,
                imdb_rating=excluded.imdb_rating,imdb_kind=excluded.imdb_kind,
                tmdb_rating=excluded.tmdb_rating,tmdb_kind=excluded.tmdb_kind,episodes=excluded.episodes;
            """;
        var title = Joined(video.Episodes.Select(episode =>
            $"S{episode.Season:00}E{episode.Number:00} {episode.Name}").Prepend(video.Title));
        var synopsis = video.Kind == VideoKind.Episode
            ? string.Join("\n\n", video.Episodes.Select(episode => episode.Synopsis)) : video.Synopsis;
        var genres = Joined(video.Genres);
        var cast = Joined(video.Cast);
        var keywords = Joined(video.Keywords);
        command.Parameters.AddWithValue("$catalog", video.CatalogId);
        command.Parameters.AddWithValue("$id", video.VideoId);
        command.Parameters.AddWithValue("$language", Stored(language));
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$type", Stored(video.Kind));
        command.Parameters.AddWithValue("$year", (object?)video.Year ?? DBNull.Value);
        command.Parameters.AddWithValue("$genres", genres);
        command.Parameters.AddWithValue("$cast", cast);
        command.Parameters.AddWithValue("$synopsis", synopsis);
        command.Parameters.AddWithValue("$keywords", keywords);
        command.Parameters.AddWithValue("$search", FileName.Normalize(string.Join(' ', title,
            video.Year, genres, cast, synopsis, keywords)));
        command.Parameters.AddWithValue("$retrieved", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("$seriesCatalog", (object?)video.Series?.CatalogId ?? DBNull.Value);
        command.Parameters.AddWithValue("$seriesId", (object?)video.Series?.VideoId ?? DBNull.Value);
        command.Parameters.AddWithValue("$seriesType", video.Series is { } series ? Stored(series.Kind) : (object)DBNull.Value);
        command.Parameters.AddWithValue("$seriesTitle", (object?)video.Series?.Title ?? DBNull.Value);
        command.Parameters.AddWithValue("$seriesYear", (object?)video.Series?.Year ?? DBNull.Value);
        command.Parameters.AddWithValue("$imdbRating", (object?)video.ImdbRating?.Value ?? DBNull.Value);
        command.Parameters.AddWithValue("$imdbKind", video.ImdbRating is { } imdb ? Stored(imdb.Kind) : (object)DBNull.Value);
        command.Parameters.AddWithValue("$tmdbRating", (object?)video.TmdbRating?.Value ?? DBNull.Value);
        command.Parameters.AddWithValue("$tmdbKind", video.TmdbRating is { } tmdb ? Stored(tmdb.Kind) : (object)DBNull.Value);
        command.Parameters.AddWithValue("$episodes", video.Episodes.Count == 0 ? (object)DBNull.Value : JsonSerializer.Serialize(video.Episodes));
        command.ExecuteNonQuery();
        SaveRecords(connection, video, language);
    }

    private static string Match(SqliteCommand command, Query query, string? except = null)
    {
        var sql = new StringBuilder(query.Configuration switch
        {
            LibraryConfiguration.Videos => "r.kind=0",
            LibraryConfiguration.Music => "r.kind=1",
            _ => "1=1",
        });
        foreach (var (section, value) in query.Filters)
        {
            if (section == except || except == "series" && section == "season")
                continue;
            if (section == "series")
                sql.Append(" AND " + SeriesMatch(command, value));
            else if (section == "season")
            {
                var parameter = "$filter" + command.Parameters.Count;
                sql.Append(query.Filters.ContainsKey("series")
                    ? $" AND EXISTS(SELECT 1 FROM json_each(r.episodes) e WHERE CAST(json_extract(e.value,'$.Season') AS TEXT)={parameter})"
                    : " AND 0=1");
                command.Parameters.AddWithValue(parameter, value);
            }
            else if (section is "size" or "modified")
                sql.Append(" AND " + Filter(section, value));
            else if (section is "type" or "kind" or "year" or "genres" or "artist" or "album")
            {
                var parameter = "$filter" + command.Parameters.Count;
                sql.Append(section == "genres"
                    ? $" AND instr('{Separator}' || r.genres || '{Separator}','{Separator}' || {parameter} || '{Separator}')>0"
                    : $" AND CAST(r.{section} AS TEXT)={parameter}");
                command.Parameters.AddWithValue(parameter, value);
            }
        }
        return sql.ToString();
    }

    internal static string SeriesKey(VideoChoice series) => string.Join('/',
        new[] { series.CatalogId, series.VideoId, Stored(series.Kind) }.Select(Uri.EscapeDataString));

    private static string SeriesMatch(SqliteCommand command, string value)
    {
        var parts = value.Split('/');
        if (parts.Length != 3)
            return "0=1";
        var predicates = new List<string>();
        var columns = new[] { "series_catalog", "series_id", "series_type" };
        for (var index = 0; index < columns.Length; index++)
        {
            var parameter = "$filter" + command.Parameters.Count;
            predicates.Add($"r.{columns[index]}={parameter}");
            command.Parameters.AddWithValue(parameter, Uri.UnescapeDataString(parts[index]));
        }
        return string.Join(" AND ", predicates);
    }

    private static readonly string[] Dates = ["today", "yesterday", "week", "last_week", "month", "last_month", "year", "older"];
    private static readonly string[] Sizes = ["empty", "tiny", "small", "medium", "large", "huge", "gigantic"];

    private static string Filter(string section, string value)
    {
        if (section == "size")
            return value switch
            {
                "empty" => "size=0",
                "tiny" => "size>0 AND size<=16384",
                "small" => "size>16384 AND size<=1048576",
                "medium" => "size>1048576 AND size<=134217728",
                "large" => "size>134217728 AND size<=1073741824",
                "huge" => "size>1073741824 AND size<=4294967296",
                "gigantic" => "size>4294967296",
                _ => "0=1",
            };
        var today = DateTime.Today;
        var firstDay = CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
        var week = today.AddDays(-((7 + (int)today.DayOfWeek - (int)firstDay) % 7));
        var month = new DateTime(today.Year, today.Month, 1);
        var year = new DateTime(today.Year, 1, 1);
        var (start, end) = value switch
        {
            "today" => (today, today.AddDays(1)),
            "yesterday" => (today.AddDays(-1), today),
            "week" => (week, week.AddDays(7)),
            "last_week" => (week.AddDays(-7), week),
            "month" => (month, month.AddMonths(1)),
            "last_month" => (month.AddMonths(-1), month),
            "year" => (year, year.AddYears(1)),
            "older" => (DateTime.UnixEpoch, year),
            _ => (today, today),
        };
        return $"(modified>={new DateTimeOffset(start).ToUnixTimeSeconds()} AND modified<{new DateTimeOffset(end).ToUnixTimeSeconds()})";
    }

    private static Entry Read(SqliteDataReader reader)
    {
        long? Number(int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
        return new Entry(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3))
        {
            Kind = (FileKind)reader.GetInt32(4),
            Title = reader.GetString(5), Type = reader.GetString(6), Year = (int?)Number(7),
            Genres = reader.GetString(8), Cast = reader.GetString(9), Artist = reader.GetString(10), Album = reader.GetString(11),
            Track = (int?)Number(12), Duration = Number(13),
            Created = Number(14), Modified = Number(15),
            ImdbRating = ReadRating(reader, "imdb_rating", "imdb_kind"),
            TmdbRating = ReadRating(reader, "tmdb_rating", "tmdb_kind"),
        };
    }
}
