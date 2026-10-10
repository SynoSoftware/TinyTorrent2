using System.Text.Json;
using Microsoft.Data.Sqlite;
using Syno.TinyTorrent.Models;

namespace Syno.TinyTorrent.Library;

internal sealed partial class Store
{
    internal const string VideoCleanup = """
        DELETE FROM video_records WHERE NOT EXISTS (
            SELECT 1 FROM video_references r
            WHERE r.catalog_id=video_records.catalog_id AND r.path=video_records.path
                AND r.language=video_records.language);
        """;

    internal Task<VideoRecord?> Record(string catalogId, string path, string? language, CancellationToken cancellation) =>
        database.Run(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT content,retrieved FROM video_records " +
                "WHERE catalog_id=$catalog AND path=$path AND language=$language;";
            command.Parameters.AddWithValue("$catalog", catalogId);
            command.Parameters.AddWithValue("$path", path);
            command.Parameters.AddWithValue("$language", Stored(language));
            using var reader = command.ExecuteReader();
            if (!reader.Read())
                return null;
            using var document = JsonDocument.Parse(reader.GetString(0));
            return new VideoRecord(path, document.RootElement.Clone(), reader.GetInt64(1));
        }, cancellation);

    internal Task<VideoInformation?> Saved(VideoTarget target, string language, CancellationToken cancellation) =>
        database.Run(connection =>
        {
            var release = FileName.Interpret(target.Entry.Name);
            if (release.IsExtra || release.Season is not null || release.Year is null)
                return null;
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT v.video_id,v.title,v.type,v.year,v.genres,v.cast,v.synopsis,v.keywords
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
                    saved = new VideoInformation(target.CatalogId, reader.GetString(0), reader.GetString(1), VideoKind.Movie)
                    {
                        Year = reader.GetInt32(3), Genres = Split(reader.GetString(4)), Cast = Split(reader.GetString(5)),
                        Synopsis = reader.GetString(6), Keywords = Split(reader.GetString(7)),
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
                    records.Add(new VideoRecord(reader.GetString(0), document.RootElement.Clone(), reader.GetInt64(2)));
                }
            return saved with { Records = records };
        }, cancellation);

    // Library rows and filters read these words from the database.
    private static string Stored(VideoKind kind) => kind switch
    {
        VideoKind.Series => "tv",
        VideoKind.Episode => "episode",
        _ => "movie",
    };

    private static VideoKind Kind(string stored) => stored switch
    {
        "tv" => VideoKind.Series,
        "episode" => VideoKind.Episode,
        _ => VideoKind.Movie,
    };

    // Information that is the same in every language is stored with an empty
    // language, which Saved accepts for any requested language.
    private static string Stored(string? language) => language ?? string.Empty;

    // A stored list keeps the separator the Genres filter splits on.
    private static string Joined(IEnumerable<string> values) => string.Join(" · ", values);
    private static IReadOnlyList<string> Split(string stored) => stored.Split(" · ", StringSplitOptions.RemoveEmptyEntries);

    private static void SaveRecords(SqliteConnection connection, VideoInformation video, string? language)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM video_references WHERE catalog_id=$catalog AND video_id=$video;";
        command.Parameters.AddWithValue("$catalog", video.CatalogId);
        command.Parameters.AddWithValue("$video", video.VideoId);
        command.ExecuteNonQuery();
        command.CommandText = """
            INSERT INTO video_records VALUES ($catalog,$path,$language,$content,$retrieved)
            ON CONFLICT(catalog_id,path,language) DO UPDATE SET content=excluded.content,retrieved=excluded.retrieved;
            INSERT INTO video_references VALUES ($catalog,$video,$path,$language);
            """;
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
}
