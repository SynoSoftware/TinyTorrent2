using System.Text.Json;
using Microsoft.Data.Sqlite;
using Syno.TinyTorrent.Models;

namespace Syno.TinyTorrent.Library;

internal sealed partial class Store
{
    /// <summary>
    /// Saves choices a provider returned. A choice saved before keeps the year
    /// and listing facts that a later listing lacks.
    /// </summary>
    private static void SaveChoices(
        SqliteConnection connection,
        IReadOnlyCollection<VideoChoice> choices
    )
    {
        using var command = connection.CreateCommand();
        // The name columns hold normalized titles, so Choices finds a title
        // through an index.
        command.CommandText = """
            INSERT INTO video_choices(catalog_id,video_id,type,title,original,year,content,name,original_name)
            VALUES ($catalog,$video,$type,$title,$original,$year,$content,$name,$original_name)
            ON CONFLICT(catalog_id,video_id) DO UPDATE SET type=excluded.type,title=excluded.title,name=excluded.name,
                original=COALESCE(excluded.original,original),original_name=COALESCE(excluded.original_name,original_name),
                year=COALESCE(excluded.year,year),
                content=CASE WHEN content IS NULL THEN excluded.content WHEN excluded.content IS NULL THEN content
                    ELSE json_patch(content,excluded.content) END;
            """;
        foreach (
            var name in new[]
            {
                "catalog",
                "video",
                "type",
                "title",
                "original",
                "content",
                "name",
                "original_name",
            }
        )
            command.Parameters.Add("$" + name, SqliteType.Text);
        command.Parameters.Add("$year", SqliteType.Integer);
        foreach (var choice in choices)
        {
            command.Parameters["$catalog"].Value = choice.CatalogId;
            command.Parameters["$video"].Value = choice.VideoId;
            command.Parameters["$type"].Value = Stored(choice.Kind);
            command.Parameters["$title"].Value = choice.Title;
            command.Parameters["$name"].Value = FileName.Normalize(choice.Title);
            command.Parameters["$original"].Value =
                (object?)choice.Original ?? DBNull.Value;
            command.Parameters["$original_name"].Value = choice.Original is { } original
                ? FileName.Normalize(original)
                : DBNull.Value;
            command.Parameters["$year"].Value = (object?)choice.Year ?? DBNull.Value;
            command.Parameters["$content"].Value =
                (object?)choice.Content?.GetRawText() ?? DBNull.Value;
            command.ExecuteNonQuery();
        }
    }

    /// <summary>The saved choices of one catalogue whose title or original title is <paramref name="title"/>.</summary>
    internal Task<IReadOnlyList<VideoChoice>> Choices(
        string catalogId,
        string title,
        CancellationToken cancellation
    ) =>
        database.Run<IReadOnlyList<VideoChoice>>(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT video_id,type,title,original,year,content FROM video_choices WHERE catalog_id=$catalog AND name=$name
                    UNION ALL
                    SELECT video_id,type,title,original,year,content FROM video_choices
                    WHERE catalog_id=$catalog AND original_name=$name AND name<>$name;
                    """;
                command.Parameters.AddWithValue("$catalog", catalogId);
                command.Parameters.AddWithValue("$name", FileName.Normalize(title));
                var choices = new List<VideoChoice>();
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    cancellation.ThrowIfCancellationRequested();
                    JsonElement? content = null;
                    if (!reader.IsDBNull(5))
                    {
                        using var document = JsonDocument.Parse(reader.GetString(5));
                        content = document.RootElement.Clone();
                    }
                    choices.Add(
                        new VideoChoice(
                            catalogId,
                            reader.GetString(0),
                            Kind(reader.GetString(1)),
                            reader.GetString(2)
                        )
                        {
                            Original = reader.IsDBNull(3) ? null : reader.GetString(3),
                            Year = reader.IsDBNull(4) ? null : reader.GetInt32(4),
                            Content = content,
                        }
                    );
                }
                return choices;
            },
            cancellation
        );

    internal Task<VideoRecord?> Record(
        string catalogId,
        string path,
        string? language,
        CancellationToken cancellation
    ) =>
        database.Run(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText =
                    "SELECT content,retrieved FROM video_records "
                    + "WHERE catalog_id=$catalog AND path=$path AND language=$language;";
                command.Parameters.AddWithValue("$catalog", catalogId);
                command.Parameters.AddWithValue("$path", path);
                command.Parameters.AddWithValue("$language", Stored(language));
                using var reader = command.ExecuteReader();
                if (!reader.Read())
                    return null;
                using var document = JsonDocument.Parse(reader.GetString(0));
                return new VideoRecord(path, document.RootElement.Clone(), reader.GetInt64(1));
            },
            cancellation
        );

    internal Task<VideoInformation?> Saved(
        VideoTarget target,
        string language,
        CancellationToken cancellation
    ) =>
        database.Run(
            connection =>
            {
                var release = FileName.Interpret(target.Entry.Name);
                if (release.IsExtra || release.Season is not null || release.Year is null)
                    return null;
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT v.video_id,v.title,v.type,v.year,v.genres,v.cast,v.synopsis,v.keywords,
                        v.imdb_rating,v.imdb_kind,v.tmdb_rating,v.tmdb_kind
                    FROM video_information v WHERE catalog_id=$catalog AND type='movie' AND year=$year
                        AND (language=$language OR language='');
                    """;
                command.Parameters.AddWithValue("$catalog", target.CatalogId);
                command.Parameters.AddWithValue("$year", release.Year.Value);
                command.Parameters.AddWithValue("$language", language);
                var title = FileName.Normalize(release.Title);
                VideoInformation? saved = null;
                using (var reader = command.ExecuteReader())
                    while (reader.Read())
                    {
                        cancellation.ThrowIfCancellationRequested();
                        if (FileName.Normalize(reader.GetString(1)) != title)
                            continue;
                        if (saved is not null)
                            return null;
                        saved = new VideoInformation(
                            target.CatalogId,
                            reader.GetString(0),
                            reader.GetString(1),
                            VideoKind.Movie
                        )
                        {
                            Year = reader.GetInt32(3),
                            Genres = Split(reader.GetString(4)),
                            Cast = Split(reader.GetString(5)),
                            Synopsis = reader.GetString(6),
                            Keywords = Split(reader.GetString(7)),
                            ImdbRating = ReadRating(reader, "imdb_rating", "imdb_kind"),
                            TmdbRating = ReadRating(reader, "tmdb_rating", "tmdb_kind"),
                        };
                    }
                if (saved is null)
                    return null;
                command.CommandText = """
                    SELECT r.path,r.content,r.retrieved FROM video_references f
                    JOIN video_records r USING(catalog_id,path,language)
                    WHERE f.catalog_id=$catalog AND f.video_id=$video;
                    """;
                command.Parameters.AddWithValue("$video", saved.VideoId);
                var records = new List<VideoRecord>();
                using (var reader = command.ExecuteReader())
                    while (reader.Read())
                    {
                        cancellation.ThrowIfCancellationRequested();
                        using var document = JsonDocument.Parse(reader.GetString(1));
                        records.Add(
                            new VideoRecord(
                                reader.GetString(0),
                                document.RootElement.Clone(),
                                reader.GetInt64(2)
                            )
                        );
                    }
                return saved with { Records = records };
            },
            cancellation
        );

    private static Rating? ReadRating(SqliteDataReader reader, string value, string kind)
    {
        var ordinal = reader.GetOrdinal(value);
        return reader.IsDBNull(ordinal)
            ? null
            : new Rating(
                reader.GetDouble(ordinal),
                Kind(reader.GetString(reader.GetOrdinal(kind)))
            );
    }

    // Library rows and filters read these words from the database.
    private static string Stored(VideoKind kind) =>
        kind switch
        {
            VideoKind.Series => "tv",
            VideoKind.Episode => "episode",
            _ => "movie",
        };

    private static VideoKind Kind(string stored) =>
        stored switch
        {
            "tv" => VideoKind.Series,
            "episode" => VideoKind.Episode,
            _ => VideoKind.Movie,
        };

    // Information that is the same in every language is stored with an empty
    // language, which Saved accepts for any requested language.
    private static string Stored(string? language) => language ?? string.Empty;

    private static string Joined(IEnumerable<string> values) => string.Join(Separator, values);

    private static IReadOnlyList<string> Split(string stored) =>
        stored.Split(Separator, StringSplitOptions.RemoveEmptyEntries);

    // Keep the response and its choices even when matching rejects the video,
    // so a later lookup can reuse everything the provider returned.
    internal Task SaveResponse(
        VideoInformation video,
        string? language,
        CancellationToken cancellation
    ) =>
        database.Run(
            connection =>
            {
                KeepRecords(connection, video, language);
                IReadOnlyCollection<VideoChoice> choices =
                    video.Kind == VideoKind.Episode
                        ? video.Related
                        :
                        [
                            .. video.Related,
                            new VideoChoice(video.CatalogId, video.VideoId, video.Kind, video.Title)
                            {
                                Year = video.Year,
                            },
                        ];
                SaveChoices(connection, choices);
                return true;
            },
            cancellation
        );

    private static void KeepRecords(
        SqliteConnection connection,
        VideoInformation video,
        string? language
    )
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO video_records VALUES ($catalog,$path,$language,$content,$retrieved)
            ON CONFLICT(catalog_id,path,language) DO UPDATE SET content=excluded.content,retrieved=excluded.retrieved;
            """;
        command.Parameters.AddWithValue("$catalog", video.CatalogId);
        foreach (var name in new[] { "path", "language", "content" })
            command.Parameters.Add("$" + name, SqliteType.Text);
        command.Parameters.Add("$retrieved", SqliteType.Integer);
        command.Parameters["$language"].Value = Stored(language);
        foreach (var record in video.Records)
        {
            command.Parameters["$path"].Value = record.Path;
            command.Parameters["$content"].Value = record.Content.GetRawText();
            command.Parameters["$retrieved"].Value = record.Retrieved;
            command.ExecuteNonQuery();
        }
    }

    private static void SaveRecords(
        SqliteConnection connection,
        VideoInformation video,
        string? language
    )
    {
        KeepRecords(connection, video, language);
        using var command = connection.CreateCommand();
        command.CommandText =
            "DELETE FROM video_references WHERE catalog_id=$catalog AND video_id=$video;";
        command.Parameters.AddWithValue("$catalog", video.CatalogId);
        command.Parameters.AddWithValue("$video", video.VideoId);
        command.ExecuteNonQuery();
        command.CommandText =
            "INSERT INTO video_references VALUES ($catalog,$video,$path,$language);";
        command.Parameters.AddWithValue("$language", Stored(language));
        command.Parameters.Add("$path", SqliteType.Text);
        foreach (var record in video.Records)
        {
            command.Parameters["$path"].Value = record.Path;
            command.ExecuteNonQuery();
        }
    }
}
