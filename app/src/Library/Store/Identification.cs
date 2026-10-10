using Microsoft.Data.Sqlite;
using Syno.TinyTorrent.Models;

namespace Syno.TinyTorrent.Library;

internal sealed partial class Store
{
    private const string Eligible = """
        NOT EXISTS(SELECT 1 FROM source_files m JOIN identifications i USING(origin,file_index)
            WHERE m.entry_id=f.entry_id AND (i.explicit=1 OR
                (i.video_id IS NOT NULL AND i.evidence=m.name)))
        AND NOT EXISTS(SELECT 1 FROM source_files m JOIN video_attempts a USING(origin,file_index)
            WHERE m.entry_id=f.entry_id AND a.catalog_id=$catalog AND a.evidence=m.name
                AND (a.outcome IS NULL OR a.outcome IN ('success','no_match')))
        """;

    internal Task Recover(CancellationToken cancellation) => database.Run(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE video_attempts SET request='',outcome='cancelled' WHERE outcome IS NULL;";
        return command.ExecuteNonQuery();
    }, cancellation);

    internal Task<VideoTarget?> Next(string catalogId, CancellationToken cancellation) => database.Run(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT f.entry_id,f.path,f.name,f.size FROM collected_files f
            WHERE f.kind=0 AND {Eligible} LIMIT 1;
            """;
        command.Parameters.AddWithValue("$catalog", catalogId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? new VideoTarget(new Entry(reader.GetInt64(0), reader.GetString(1),
            reader.GetString(2), reader.GetInt64(3)) { Kind = FileKind.Video }, catalogId, false) : null;
    }, cancellation);

    internal Task<bool> CanLookup(VideoTarget target, CancellationToken cancellation) => database.Run(connection =>
    {
        using var command = TargetCommand(connection, target);
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM collected_files f " +
            "WHERE f.complete=1 AND f.kind=0 AND f.entry_id=$entry " +
            $"AND f.path=$path COLLATE PATH AND f.name=$name AND {Eligible}) " +
            "AND NOT EXISTS(SELECT 1 FROM source_files m JOIN video_attempts a USING(origin,file_index) " +
            "WHERE m.entry_id=$entry AND a.catalog_id=$catalog AND a.evidence=m.name " +
            "AND a.outcome IS NOT NULL AND a.outcome<>'cancelled');";
        return Convert.ToInt64(command.ExecuteScalar()) != 0;
    }, cancellation);

    internal Task<VideoAttempt?> Begin(VideoTarget target, string? language, CancellationToken cancellation) => database.Run(connection =>
    {
        using var command = TargetCommand(connection, target);
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM collected_files f " +
            "WHERE f.entry_id=$entry AND f.path=$path COLLATE PATH AND f.name=$name" +
            (target.Manual ? string.Empty : " AND " + Eligible) + ");";
        if (Convert.ToInt64(command.ExecuteScalar()) == 0)
            return null;
        command.CommandText = """
            SELECT COALESCE(MAX(i.decided),0) FROM source_files f JOIN identifications i USING(origin,file_index)
            WHERE f.entry_id=$entry;
            """;
        var decided = Convert.ToInt64(command.ExecuteScalar());
        command.CommandText = """
            SELECT COALESCE(v.series_id,v.video_id),COALESCE(v.series_type,v.type),
                COALESCE(v.series_title,v.title),
                CASE WHEN v.series_id IS NULL THEN v.year ELSE v.series_year END FROM source_files f
            JOIN identifications i USING(origin,file_index)
            JOIN video_information v ON v.catalog_id=i.catalog_id AND v.video_id=i.video_id
            WHERE f.entry_id=$entry AND i.catalog_id=$catalog AND (i.explicit=1 OR i.evidence=f.name)
            ORDER BY i.explicit DESC,i.decided DESC LIMIT 1;
            """;
        VideoChoice? choice = null;
        using (var reader = command.ExecuteReader())
            if (reader.Read())
                choice = new VideoChoice(target.CatalogId, reader.GetString(0), Kind(reader.GetString(1)), reader.GetString(2))
                    { Year = reader.IsDBNull(3) ? null : reader.GetInt32(3) };
        var requestId = Guid.NewGuid().ToString("N");
        command.CommandText = """
            INSERT INTO video_attempts(origin,file_index,catalog_id,evidence,request,outcome,video_id)
            SELECT f.origin,f.file_index,$catalog,f.name,$request,NULL,NULL
            FROM collected_files f WHERE f.entry_id=$entry
            ON CONFLICT(origin,file_index,catalog_id) DO UPDATE SET evidence=excluded.evidence,
                request=excluded.request,outcome=NULL;
            """;
        command.Parameters.AddWithValue("$request", requestId);
        command.ExecuteNonQuery();
        return new VideoAttempt(target, requestId, choice, decided, language);
    }, cancellation);

    internal Task<bool> Complete(VideoAttempt attempt, VideoInformation? video, string outcome,
        CancellationToken cancellation) => database.Run(connection =>
    {
        if (video is not null && video.CatalogId != attempt.Target.CatalogId)
            return false;
        using var command = TargetCommand(connection, attempt.Target);
        command.CommandText = """
            SELECT EXISTS(SELECT 1 FROM video_attempts a JOIN collected_files f USING(origin,file_index)
                WHERE a.catalog_id=$catalog AND a.request=$request
                    AND a.outcome IS NULL AND a.evidence=f.name
                    AND f.entry_id=$entry AND f.name=$name AND f.path=$path COLLATE PATH)
            AND $decided=(SELECT COALESCE(MAX(i.decided),0) FROM source_files f
                JOIN identifications i USING(origin,file_index) WHERE f.entry_id=$entry);
            """;
        command.Parameters.AddWithValue("$request", attempt.RequestId);
        command.Parameters.AddWithValue("$decided", attempt.Decided);
        var accepted = Convert.ToInt64(command.ExecuteScalar()) != 0;
        command.CommandText = """
            UPDATE video_attempts SET outcome=$outcome,video_id=COALESCE($video,video_id)
            WHERE catalog_id=$catalog AND request=$request AND outcome IS NULL;
            """;
        command.Parameters.AddWithValue("$outcome", accepted ? video is null ? outcome : "success" : "cancelled");
        command.Parameters.AddWithValue("$video", accepted ? (object?)video?.VideoId ?? DBNull.Value : DBNull.Value);
        command.ExecuteNonQuery();
        if (accepted && video is not null)
            SaveVideo(connection, video, attempt.Language);
        if (accepted && (video is not null || outcome == "no_match" && !attempt.Target.Manual))
            SaveIdentification(connection, attempt.Target.Entry.EntryId, video, attempt.Target.Manual);
        return accepted;
    }, cancellation);

    internal Task<VideoChoice?> Series(VideoTarget target, Release release, CancellationToken cancellation) =>
        database.Run(connection =>
        {
            using var command = TargetCommand(connection, target);
            command.CommandText = """
                SELECT f.name,f.path,current.path,v.series_id,v.series_type,v.series_title,v.series_year
                FROM collected_files current
                JOIN source_files f ON f.origin=current.origin
                JOIN identifications i ON i.origin=f.origin AND i.file_index=f.file_index
                JOIN video_information v ON v.catalog_id=i.catalog_id AND v.video_id=i.video_id
                WHERE current.entry_id=$entry AND current.name=$name AND current.path=$path COLLATE PATH
                    AND i.explicit=0 AND i.evidence=f.name
                    AND v.catalog_id=$catalog AND v.series_catalog=$catalog AND v.series_id IS NOT NULL;
                """;
            using var reader = command.ExecuteReader();
            VideoChoice? series = null;
            while (reader.Read())
            {
                if (!StringComparer.OrdinalIgnoreCase.Equals(Path.GetDirectoryName(reader.GetString(1)),
                    Path.GetDirectoryName(reader.GetString(2))))
                    continue;
                var previous = FileName.Interpret(reader.GetString(0));
                if (previous.Season != release.Season || previous.Year != release.Year ||
                    FileName.Normalize(previous.Title) != FileName.Normalize(release.Title))
                    continue;
                var videoId = reader.GetString(3);
                if (series is not null && series.VideoId != videoId)
                    return null;
                series = new VideoChoice(target.CatalogId, videoId, Kind(reader.GetString(4)), reader.GetString(5))
                    { Year = reader.IsDBNull(6) ? null : reader.GetInt32(6) };
            }
            return series;
        }, cancellation);

    private static SqliteCommand TargetCommand(SqliteConnection connection, VideoTarget target)
    {
        var command = connection.CreateCommand();
        command.Parameters.AddWithValue("$entry", target.Entry.EntryId);
        command.Parameters.AddWithValue("$path", target.Entry.Path);
        command.Parameters.AddWithValue("$name", target.Entry.Name);
        command.Parameters.AddWithValue("$catalog", target.CatalogId);
        return command;
    }
}

internal sealed record VideoTarget(Entry Entry, string CatalogId, bool Manual);

// Language is null when the provider's information is the same in every language.
internal sealed record VideoAttempt(VideoTarget Target, string RequestId, VideoChoice? Choice, long Decided, string? Language);
