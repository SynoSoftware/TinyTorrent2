using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Syno.TinyTorrent.Models;
using Syno.TinyTorrent.Services;

namespace Syno.TinyTorrent.Library;

internal sealed partial class Store(Database database, Imdb.Dataset imdb)
{
    // Stored lists such as genres keep this separator, which the Genres filter
    // splits on.
    internal const string Separator = " · ";

    internal Task<Coverage> ReadCoverage() =>
        database.Run(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT COUNT(*),COUNT(video_id) FROM library_entries WHERE kind=$video;";
            command.Parameters.AddWithValue("$video", (int)FileKind.Video);
            using var reader = command.ExecuteReader();
            reader.Read();
            return new Coverage(reader.GetInt32(0), reader.GetInt32(1));
        });

#if CAPTURE
    internal Task<int> PrepareTiming() =>
        database.Run(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                    CREATE TEMP TABLE timing_videos AS
                    SELECT * FROM current_files WHERE kind=0 ORDER BY entry_id LIMIT 1000;
                    INSERT INTO video_information(catalog_id,video_id,language,title,type,year,genres,cast,synopsis,keywords,search)
                    SELECT 'timing',CAST(entry_id AS TEXT),'en','Coastal mystery ' || entry_id,'movie',2024,
                        'Drama · Mystery','Alex Morgan · Jamie Rivera · Taylor Chen · Casey Bennett · Jordan Williams · Robin Patel · Avery Thompson · Cameron Ellis · Morgan Brooks · Reese Anderson · Riley Campbell · Quinn Mitchell · Rowan Stewart · Parker Hughes · Skyler Evans · Emerson Reed',
                        'An investigator returns to a coastal town after a fishing boat disappears. As a storm approaches, conflicting accounts draw old friends into a search that reveals a secret the community has kept for years.',
                        'investigator coastal mystery','investigator coastal mystery example actor'
                    FROM timing_videos;
                    WITH samples AS (
                        SELECT CAST(entry_id AS TEXT) AS video_id,row_number() OVER (ORDER BY entry_id) AS position
                        FROM timing_videos ORDER BY entry_id LIMIT 2
                    )
                    UPDATE video_information SET title=title || ' (sample ratings)',imdb_id=999999000+samples.position
                    FROM samples WHERE catalog_id='timing' AND video_information.video_id=samples.video_id;
                    INSERT OR REPLACE INTO title_facts(imdb_id,ratings,directors,certification,box_office) VALUES
                        (999999001,'{"Imdb":{"Value":7.8,"Kind":"Movie","Votes":2712000},"Tmdb":{"Value":8.1,"Kind":"Movie","Votes":31250},"RottenTomatoes":{"Value":9.1,"Kind":"Movie","Votes":412},"Popcornmeter":{"Value":8.7,"Kind":"Movie","Votes":25000},"Metacritic":{"Value":8.2,"Kind":"Movie","Votes":58}}',
                            'Sam Okafor','PG-13',412500000),
                        (999999002,'{"Imdb":{"Value":6.9,"Kind":"Movie","Votes":48},"Tmdb":{"Value":7.2,"Kind":"Movie","Votes":2712},"RottenTomatoes":{"Value":5.4,"Kind":"Movie"}}',
                            'Sam Okafor','PG-13',2300000);
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
                    """ + Database.Invalidate;
            command.ExecuteNonQuery();
            command.CommandText =
                "SELECT COUNT(*) FROM video_information WHERE catalog_id='timing';";
            return Convert.ToInt32(command.ExecuteScalar());
        });
#endif

    internal async Task<Matches> Search(Query query, CancellationToken cancellation)
    {
        var matches = await Find(query, cancellation).ConfigureAwait(false);
        return matches with { Entries = await Rate(matches.Entries, cancellation).ConfigureAwait(false) };
    }

    // An IMDb rating comes from the downloaded IMDb data when that has it,
    // because the data is newer than the provider's saved rating.
    private async Task<IReadOnlyList<Entry>> Rate(
        IReadOnlyList<Entry> entries,
        CancellationToken cancellation
    )
    {
        var ratings = await imdb.Ratings(
                entries.Select(entry => entry.ImdbId).OfType<long>().ToHashSet(),
                cancellation
            )
            .ConfigureAwait(false);
        return ratings.Count == 0
            ? entries
            : entries
                .Select(entry =>
                    entry.ImdbId is { } id && ratings.TryGetValue(id, out var rating)
                        ? entry with { Ratings = entry.Ratings with { Imdb = rating } }
                        : entry
                )
                .ToArray();
    }

    private Task<Matches> Find(Query query, CancellationToken cancellation) =>
        database.Run(
            connection =>
            {
                var terms = FileName
                    .Normalize(query.Text)
                    .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                using var snapshot = connection.CreateCommand();
                snapshot.CommandText = "SELECT valid FROM library_state WHERE singleton=1;";
                if (Convert.ToInt64(snapshot.ExecuteScalar()) == 0)
                {
                    snapshot.CommandText = """
                        DELETE FROM library_rows;
                        INSERT INTO library_rows SELECT * FROM library_entries;
                        UPDATE library_state SET valid=1 WHERE singleton=1;
                        """;
                    snapshot.ExecuteNonQuery();
                }
                var text = new StringBuilder("1=1");
                for (var index = 0; index < terms.Length; index++)
                {
                    var parameter = "$term" + index;
                    text.Append(
                        $"""
                         AND (instr(r.file_search,{parameter})>0 OR instr(r.music_search,{parameter})>0 OR
                            (r.catalog_id,r.video_id) IN (SELECT v.catalog_id,v.video_id FROM video_information v
                                WHERE (v.catalog_id,v.video_id) IN (SELECT catalog_id,video_id FROM library_rows WHERE video_id IS NOT NULL)
                                    AND instr(v.search,{parameter})>0))
                        """
                    );
                    snapshot.Parameters.AddWithValue(parameter, terms[index]);
                }
                var matches = "library_rows r";
                if (terms.Length > 0)
                {
                    cancellation.ThrowIfCancellationRequested();
                    snapshot.CommandText =
                        $"DELETE FROM library_matches; INSERT INTO library_matches SELECT r.rowid FROM library_rows r WHERE {text};";
                    snapshot.ExecuteNonQuery();
                    matches = "library_matches m JOIN library_rows r ON r.rowid=m.row_id";
                }
                var entries = new List<Entry>();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText =
                        $"SELECT r.*,{Unmatched} FROM {matches} WHERE " + Match(command, query);
                    command.Parameters.AddWithValue("$catalog", (object?)query.CatalogId ?? DBNull.Value);
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        cancellation.ThrowIfCancellationRequested();
                        entries.Add(Read(reader));
                    }
                }
                var facets = new List<Facet>();
                var sections = query.Configuration switch
                {
                    LibraryConfiguration.Videos => new[] { "type", "genres", "year" }
                        .Concat(
                            query.Filters.Keys.Where(section => section is "series" or "season")
                        )
                        .ToArray(),
                    LibraryConfiguration.Music => new[] { "genres", "year" }
                        .Concat(query.Filters.Keys.Where(section => section is "artist" or "album"))
                        .ToArray(),
                    _ => new[] { "kind", "modified", "size" }
                        .Concat(
                            query.Filters.Keys.Where(section =>
                                section
                                    is "year"
                                        or "genres"
                                        or "artist"
                                        or "album"
                                        or "series"
                                        or "season"
                            )
                        )
                        .ToArray(),
                };
                if (query.Filters.ContainsKey("series"))
                    sections = sections
                        .Where(section => section != "season")
                        .SelectMany(section =>
                            section == "series" ? new[] { "series", "season" } : new[] { section }
                        )
                        .ToArray();
                foreach (var section in sections)
                {
                    cancellation.ThrowIfCancellationRequested();
                    using var command = connection.CreateCommand();
                    var predicate = Match(command, query, section);
                    if (section is "modified" or "size")
                    {
                        var values = section == "modified" ? Dates : Sizes;
                        command.CommandText =
                            "SELECT "
                            + string.Join(
                                ",",
                                values.Select(value =>
                                    $"COUNT(CASE WHEN {Filter(section, value)} THEN 1 END)"
                                )
                            )
                            + $" FROM {matches} WHERE {predicate};";
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
                            FROM {matches} WHERE {predicate} AND r.series_id IS NOT NULL
                            GROUP BY r.series_catalog,r.series_id,r.series_type;
                            """;
                        using var reader = command.ExecuteReader();
                        while (reader.Read())
                        {
                            var series = new VideoChoice(
                                reader.GetString(0),
                                reader.GetString(1),
                                Kind(reader.GetString(2)),
                                reader.GetString(3)
                            );
                            facets.Add(
                                new(section, SeriesKey(series), reader.GetInt32(4))
                                {
                                    Label = series.Title,
                                }
                            );
                        }
                    }
                    else if (section == "season")
                    {
                        command.CommandText = $"""
                            SELECT json_extract(e.value,'$.Season'),COUNT(DISTINCT r.entry_id)
                            FROM {matches},json_each(r.episodes) e WHERE {predicate}
                            GROUP BY json_extract(e.value,'$.Season');
                            """;
                        using var reader = command.ExecuteReader();
                        while (reader.Read())
                            facets.Add(
                                new(
                                    section,
                                    reader.GetInt32(0).ToString(CultureInfo.InvariantCulture),
                                    reader.GetInt32(1)
                                )
                            );
                    }
                    else if (section == "genres")
                    {
                        command.CommandText = $"""
                            SELECT j.value, COUNT(*) FROM {matches},
                                json_each('[' || replace(json_quote(genres),'{Separator}','","') || ']') j
                            WHERE {predicate} AND j.value<>'' GROUP BY j.value;
                            """;
                        using var reader = command.ExecuteReader();
                        while (reader.Read())
                            facets.Add(new(section, reader.GetString(0), reader.GetInt32(1)));
                    }
                    else
                    {
                        var populated = section is "artist" or "album"
                            ? $" AND {section}<>''"
                            : string.Empty;
                        var order = section switch
                        {
                            "year" => " ORDER BY year DESC",
                            "type" =>
                                " ORDER BY CASE type WHEN 'movie' THEN 0 WHEN 'episode' THEN 1 ELSE 2 END",
                            _ => string.Empty,
                        };
                        command.CommandText =
                            $"SELECT {section}, COUNT(*) FROM {matches} WHERE {predicate} AND {section} IS NOT NULL{populated} GROUP BY {section}{order};";
                        using var reader = command.ExecuteReader();
                        while (reader.Read())
                            facets.Add(
                                new(
                                    section,
                                    Convert.ToString(
                                        reader.GetValue(0),
                                        CultureInfo.InvariantCulture
                                    )!,
                                    reader.GetInt32(1)
                                )
                            );
                    }
                    if (
                        query.Filters.TryGetValue(section, out var selected)
                        && !facets.Any(facet => facet.Section == section && facet.Value == selected)
                    )
                    {
                        string? label = null;
                        if (section == "series")
                        {
                            command.Parameters.Clear();
                            command.CommandText =
                                "SELECT series_title FROM video_information r WHERE "
                                + SeriesMatch(command, selected)
                                + " LIMIT 1;";
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
                        var predicate = Match(
                            command,
                            query with
                            {
                                Configuration = configuration,
                            }
                        );
                        predicates.Add($"COUNT(CASE WHEN {predicate} THEN 1 END)");
                    }
                    command.CommandText = $"SELECT {string.Join(',', predicates)} FROM {matches};";
                    using var reader = command.ExecuteReader();
                    reader.Read();
                    for (var index = 0; index < configurations.Length; index++)
                        counts[configurations[index]] = reader.GetInt32(index);
                }
                if (terms.Length > 0)
                {
                    snapshot.CommandText = "DELETE FROM library_matches;";
                    snapshot.ExecuteNonQuery();
                }
                return new Matches(entries, facets, counts);
            },
            cancellation
        );

    // Whether the selected provider, $catalog, searched for the row's file
    // under its current name and found no match.
    private const string Unmatched = """
        EXISTS(SELECT 1 FROM source_files s JOIN video_attempts x USING(origin,file_index)
            WHERE s.entry_id=r.entry_id AND x.catalog_id=$catalog AND x.evidence=s.name
                AND x.outcome='no_match') AS unmatched
        """;

    internal async Task<Detail?> Detail(long entryId, string? catalogId, CancellationToken cancellation)
    {
        var detail = await ReadDetail(entryId, catalogId, cancellation).ConfigureAwait(false);
        return detail is null
            ? null
            : detail with { Entry = (await Rate([detail.Entry], cancellation).ConfigureAwait(false))[0] };
    }

    private Task<Detail?> ReadDetail(long entryId, string? catalogId, CancellationToken cancellation) =>
        database.Run(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = $"""
                    SELECT r.*,{Unmatched},COALESCE(v.cast,'') AS full_cast,COALESCE(v.synopsis,'') AS synopsis,
                        COALESCE(t.directors,'') AS directors,COALESCE(t.certification,'') AS certification,t.box_office
                    FROM library_entries r LEFT JOIN video_information v USING(catalog_id,video_id)
                    LEFT JOIN title_facts t ON t.imdb_id=r.imdb_id WHERE r.entry_id=$entry;
                    """;
                command.Parameters.AddWithValue("$entry", entryId);
                command.Parameters.AddWithValue("$catalog", (object?)catalogId ?? DBNull.Value);
                Entry entry;
                string cast;
                string synopsis;
                string directors;
                string certification;
                long? boxOffice;
                VideoChoice? series;
                IReadOnlyList<Episode> episodes;
                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read())
                        return null;
                    entry = Read(reader);
                    cast = reader.GetString(reader.GetOrdinal("full_cast"));
                    synopsis = reader.GetString(reader.GetOrdinal("synopsis"));
                    directors = reader.GetString(reader.GetOrdinal("directors"));
                    certification = reader.GetString(reader.GetOrdinal("certification"));
                    var takings = reader.GetOrdinal("box_office");
                    boxOffice = reader.IsDBNull(takings) ? null : reader.GetInt64(takings);
                    var identity = reader.GetOrdinal("series_id");
                    series = reader.IsDBNull(identity)
                        ? null
                        : new VideoChoice(
                            reader.GetString(reader.GetOrdinal("series_catalog")),
                            reader.GetString(identity),
                            Kind(reader.GetString(reader.GetOrdinal("series_type"))),
                            reader.GetString(reader.GetOrdinal("series_title"))
                        );
                    var ordinal = reader.GetOrdinal("episodes");
                    episodes = reader.IsDBNull(ordinal)
                        ? []
                        : JsonSerializer.Deserialize<Episode[]>(reader.GetString(ordinal)) ?? [];
                }
                command.CommandText = """
                    SELECT DISTINCT c.origin,c.name,f.file_index FROM source_files f
                    JOIN contributions c USING(origin) WHERE f.entry_id=$entry;
                    """;
                var origins = new List<Origin>();
                using (var reader = command.ExecuteReader())
                    while (reader.Read())
                        origins.Add(
                            new(reader.GetString(0), reader.GetString(1), reader.GetInt32(2))
                        );
                return new Detail(entry, cast, synopsis, origins)
                {
                    Series = series,
                    Episodes = episodes,
                    Directors = directors,
                    Certification = certification,
                    BoxOffice = boxOffice,
                };
            },
            cancellation
        );

    internal Task EraseMetadata(Entry entry, CancellationToken cancellation) =>
        database.Run(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText =
                    "SELECT EXISTS(SELECT 1 FROM collected_files f "
                    + "WHERE entry_id=$entry AND path=$path COLLATE PATH AND f.name=$name);";
                command.Parameters.AddWithValue("$entry", entry.EntryId);
                command.Parameters.AddWithValue("$path", entry.Path);
                command.Parameters.AddWithValue("$name", entry.Name);
                if (Convert.ToInt64(command.ExecuteScalar()) == 0)
                    throw new VideoException(VideoFailure.FileChanged);
                SaveIdentification(connection, entry.EntryId, null, true);
                return true;
            },
            cancellation
        );

    private static void SaveIdentification(
        SqliteConnection connection,
        long entryId,
        VideoInformation? video,
        bool isExplicit
    )
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
        command.CommandText =
            """
                UPDATE video_attempts SET request='',outcome=COALESCE(outcome,'cancelled')
                WHERE (origin,file_index) IN (SELECT origin,file_index FROM source_files WHERE entry_id=$entry);
                """
            + Database.PruneVideos
            + Database.Invalidate;
        command.ExecuteNonQuery();
    }

    private static void SaveVideo(
        SqliteConnection connection,
        VideoInformation video,
        string? language
    )
    {
        video = Filled(connection, video);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT ratings FROM title_facts WHERE imdb_id=$imdbId;";
        command.Parameters.AddWithValue("$imdbId", (object?)video.ImdbId ?? DBNull.Value);
        var shared = command.ExecuteScalar() is string saved ? ReadRatings(saved).Merge(video.Ratings) : video.Ratings;
        // The larger box office stays: TMDB and MDBList report worldwide
        // takings and OMDb only US takings, and a total only grows.
        command.CommandText = """
            INSERT INTO video_information(catalog_id,video_id,language,title,type,year,genres,cast,synopsis,keywords,search,
                series_catalog,series_id,series_type,series_title,series_year,ratings,episodes,imdb_id) VALUES (
                $catalog,$id,$language,$title,$type,$year,$genres,$cast,$synopsis,$keywords,$search,
                $seriesCatalog,$seriesId,$seriesType,$seriesTitle,$seriesYear,$ratings,$episodes,$imdbId)
            ON CONFLICT(catalog_id,video_id) DO UPDATE SET language=excluded.language,
                title=excluded.title,type=excluded.type,year=excluded.year,genres=excluded.genres,
                cast=excluded.cast,synopsis=excluded.synopsis,keywords=excluded.keywords,
                search=excluded.search,
                series_catalog=excluded.series_catalog,series_id=excluded.series_id,series_type=excluded.series_type,
                series_title=excluded.series_title,series_year=excluded.series_year,
                ratings=excluded.ratings,episodes=excluded.episodes,
                imdb_id=excluded.imdb_id;
            INSERT INTO title_facts(imdb_id,ratings,directors,certification,box_office)
            SELECT $imdbId,$shared,$directors,$certification,$boxOffice WHERE $imdbId IS NOT NULL
            ON CONFLICT(imdb_id) DO UPDATE SET ratings=excluded.ratings,
                directors=CASE WHEN excluded.directors<>'' THEN excluded.directors ELSE directors END,
                certification=CASE WHEN excluded.certification<>'' THEN excluded.certification ELSE certification END,
                box_office=CASE WHEN box_office IS NULL OR excluded.box_office>box_office
                    THEN excluded.box_office ELSE box_office END;
            """;
        var title = Joined(
            video
                .Episodes.Select(episode =>
                    $"S{episode.Season:00}E{episode.Number:00} {episode.Name}"
                )
                .Prepend(video.Title)
        );
        var synopsis =
            video.Kind == VideoKind.Episode
                ? string.Join("\n\n", video.Episodes.Select(episode => episode.Synopsis))
                : video.Synopsis;
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
        command.Parameters.AddWithValue(
            "$search",
            FileName.Normalize(
                string.Join(' ', title, video.Year, genres, cast, synopsis, keywords)
            )
        );
        command.Parameters.AddWithValue(
            "$seriesCatalog",
            (object?)video.Series?.CatalogId ?? DBNull.Value
        );
        command.Parameters.AddWithValue(
            "$seriesId",
            (object?)video.Series?.VideoId ?? DBNull.Value
        );
        command.Parameters.AddWithValue(
            "$seriesType",
            video.Series is { } series ? Stored(series.Kind) : (object)DBNull.Value
        );
        command.Parameters.AddWithValue(
            "$seriesTitle",
            (object?)video.Series?.Title ?? DBNull.Value
        );
        command.Parameters.AddWithValue("$seriesYear", (object?)video.Series?.Year ?? DBNull.Value);
        command.Parameters.AddWithValue("$ratings", WriteRatings(video.Ratings));
        command.Parameters.AddWithValue("$shared", WriteRatings(shared));
        command.Parameters.AddWithValue("$directors", Joined(video.Directors));
        command.Parameters.AddWithValue("$certification", video.Certification);
        command.Parameters.AddWithValue("$boxOffice", (object?)video.BoxOffice ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$episodes",
            video.Episodes.Count == 0
                ? (object)DBNull.Value
                : JsonSerializer.Serialize(video.Episodes)
        );
        command.ExecuteNonQuery();
        SaveReferences(connection, video, language);
    }

    // A movie whose provider lacks its synopsis, cast or genres keeps the ones
    // already saved for it, by this provider or by another that names the same
    // IMDb ID, so a fetch from a sparser provider does not lose them. Episodes
    // are left out, because every episode of a series shares its IMDb ID.
    private static VideoInformation Filled(SqliteConnection connection, VideoInformation video)
    {
        if (video.Kind != VideoKind.Movie || video.Synopsis.Length > 0 && video.Cast.Count > 0 && video.Genres.Count > 0)
            return video;
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT synopsis,"cast",genres FROM video_information
            WHERE type='movie' AND (catalog_id=$catalog AND video_id=$id OR imdb_id=$imdbId)
            ORDER BY catalog_id=$catalog AND video_id=$id DESC;
            """;
        command.Parameters.AddWithValue("$catalog", video.CatalogId);
        command.Parameters.AddWithValue("$id", video.VideoId);
        command.Parameters.AddWithValue("$imdbId", (object?)video.ImdbId ?? DBNull.Value);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (video.Synopsis.Length == 0)
                video = video with { Synopsis = reader.GetString(0) };
            if (video.Cast.Count == 0)
                video = video with { Cast = Split(reader.GetString(1)) };
            if (video.Genres.Count == 0)
                video = video with { Genres = Split(reader.GetString(2)) };
        }
        return video;
    }

    private static string Match(SqliteCommand command, Query query, string? except = null)
    {
        var sql = new StringBuilder(
            query.Configuration switch
            {
                LibraryConfiguration.Videos => "r.kind=0",
                LibraryConfiguration.Music => "r.kind=1",
                _ => "1=1",
            }
        );
        foreach (var (section, value) in query.Filters)
        {
            if (section == except || except == "series" && section == "season")
                continue;
            if (section == "series")
                sql.Append(" AND " + SeriesMatch(command, value));
            else if (section == "season")
            {
                var parameter = "$filter" + command.Parameters.Count;
                sql.Append(
                    query.Filters.ContainsKey("series")
                        ? $" AND EXISTS(SELECT 1 FROM json_each(r.episodes) e WHERE CAST(json_extract(e.value,'$.Season') AS TEXT)={parameter})"
                        : " AND 0=1"
                );
                command.Parameters.AddWithValue(parameter, value);
            }
            else if (section is "size" or "modified")
                sql.Append(" AND " + Filter(section, value));
            else if (section is "type" or "kind" or "year" or "genres" or "artist" or "album")
            {
                var parameter = "$filter" + command.Parameters.Count;
                sql.Append(
                    section == "genres"
                        ? $" AND instr('{Separator}' || r.genres || '{Separator}','{Separator}' || {parameter} || '{Separator}')>0"
                        : $" AND CAST(r.{section} AS TEXT)={parameter}"
                );
                command.Parameters.AddWithValue(parameter, value);
            }
        }
        return sql.ToString();
    }

    internal static string SeriesKey(VideoChoice series) =>
        string.Join(
            '/',
            new[] { series.CatalogId, series.VideoId, Stored(series.Kind) }.Select(
                Uri.EscapeDataString
            )
        );

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

    private static readonly string[] Dates =
    [
        "today",
        "yesterday",
        "week",
        "last_week",
        "month",
        "last_month",
        "year",
        "older",
    ];
    private static readonly string[] Sizes =
    [
        "empty",
        "tiny",
        "small",
        "medium",
        "large",
        "huge",
        "gigantic",
    ];

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
        var ratings = reader.GetOrdinal("ratings");
        return new Entry(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt64(3)
        )
        {
            Kind = (FileKind)reader.GetInt32(4),
            Title = reader.GetString(5),
            Type = reader.GetString(6),
            Year = (int?)Number(7),
            HasMetadata = !reader.IsDBNull(reader.GetOrdinal("video_id")),
            IsUnmatched = reader.GetBoolean(reader.GetOrdinal("unmatched")),
            Genres = reader.GetString(8),
            Cast = reader.GetString(9),
            Artist = reader.GetString(10),
            Album = reader.GetString(11),
            Track = (int?)Number(12),
            Duration = Number(13),
            Created = Number(14),
            Modified = Number(15),
            Bitrate = Number(reader.GetOrdinal("bitrate")),
            Width = (int?)Number(reader.GetOrdinal("width")),
            Height = (int?)Number(reader.GetOrdinal("height")),
            Subtitles = new(
                reader
                    .GetString(reader.GetOrdinal("subtitles"))
                    .Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToArray()
            ),
            Ratings = reader.IsDBNull(ratings) ? new() : ReadRatings(reader.GetString(ratings)),
            ImdbId = Number(reader.GetOrdinal("imdb_id")),
        };
    }
}

internal sealed record Coverage(int Total, int Informed);
