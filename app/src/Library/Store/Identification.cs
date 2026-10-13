using Microsoft.Data.Sqlite;
using Syno.TinyTorrent.Models;
using Syno.TinyTorrent.Services;
using System.Text.Json;

namespace Syno.TinyTorrent.Library;

internal sealed partial class Store
{
    private const string Undecided = """
        NOT EXISTS(SELECT 1 FROM source_files m JOIN identifications i USING(origin,file_index)
            WHERE m.entry_id=f.entry_id AND (i.explicit=1 OR
                (i.video_id IS NOT NULL AND i.evidence=m.name)))
        """;

    // The sweep skips a file that will never be downloaded, because it never
    // shows in Library.
    private const string Eligible =
        Undecided
        + """
            AND (f.wanted=1 OR f.complete=1)
            AND NOT EXISTS(SELECT 1 FROM source_files m JOIN video_attempts a USING(origin,file_index)
                WHERE m.entry_id=f.entry_id AND a.catalog_id=$catalog AND a.evidence=m.name
                    AND (a.outcome IS NULL OR a.outcome<>'cancelled'))
            """;

    internal Task<IReadOnlyList<VideoChoice>?> Search(RequestKey key, CancellationToken cancellation, long since = 0) =>
        database.Run<IReadOnlyList<VideoChoice>?>(
            connection =>
            {
                using var command = SearchCommand(connection, key);
                command.CommandText = """
                    SELECT v.video_id,v.type,v.title,v.original,v.year FROM video_searches s
                        LEFT JOIN json_each(s.choices) c ON 1
                        LEFT JOIN video_choices v ON v.catalog_id=s.catalog_id AND v.video_id=c.value
                    WHERE s.catalog_id=$catalog AND s.question_id=$question AND s.checked>=$since ORDER BY c.key;
                    """;
                command.Parameters.AddWithValue("$since", since);
                using var reader = command.ExecuteReader();
                var choices = new List<VideoChoice>();
                var found = false;
                while (reader.Read())
                {
                    found = true;
                    if (!reader.IsDBNull(0))
                        choices.Add(ReadChoice(reader, key.ProviderId));
                }
                return found ? choices : null;
            },
            cancellation
        );

    internal Task SaveSearch(
        LookupRequest request,
        VideoSearch answer,
        CancellationToken cancellation
    ) =>
        database.Run(
            connection =>
            {
                var choices = answer.Choices;
                using var command = SearchCommand(connection, request.Key);
                command.CommandText = """
                        INSERT INTO video_searches(catalog_id,question_id,choices,checked) VALUES($catalog,$question,$choices,$checked)
                        ON CONFLICT(catalog_id,question_id) DO UPDATE SET choices=excluded.choices,rejected=0,checked=excluded.checked;
                        """;
                command.Parameters.AddWithValue("$checked", DateTimeOffset.UtcNow.UtcTicks);
                command.Parameters.AddWithValue("$choices", JsonSerializer.Serialize(choices.Select(choice => choice.VideoId)));
                command.ExecuteNonQuery();
                SaveChoices(connection, choices.ToArray());
                RequestPolicy.Record(connection, request, answer.Empty ? RequestOutcome.Missing
                    : choices.Count == 0 ? RequestOutcome.NoMatch : RequestOutcome.Answered);
                return true;
            },
            cancellation
        );

    private static SqliteCommand SearchCommand(
        SqliteConnection connection,
        RequestKey key
    )
    {
        var command = connection.CreateCommand();
        command.Parameters.AddWithValue("$catalog", key.ProviderId);
        command.Parameters.AddWithValue("$question", key.QuestionId);
        return command;
    }

    internal Task RejectSearch(RequestKey key) => database.Run(connection =>
    {
        using var command = SearchCommand(connection, key);
        command.CommandText = "UPDATE video_searches SET rejected=1 WHERE catalog_id=$catalog AND question_id=$question;";
        return command.ExecuteNonQuery();
    });

    internal Task<IReadOnlyList<VideoFetch>> Fetchable(IReadOnlyCollection<Entry> entries, string catalog, CancellationToken cancellation) =>
        database.Run<IReadOnlyList<VideoFetch>>(connection =>
        {
            var result = new List<VideoFetch>();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT EXISTS(SELECT 1 FROM source_files f JOIN identifications i USING(origin,file_index)
                    WHERE f.entry_id=$entry AND (i.explicit=1 OR i.evidence=f.name)
                        AND (i.explicit=1 AND i.video_id IS NULL OR i.video_id IS NOT NULL AND i.catalog_id<>$catalog)),
                    EXISTS(SELECT 1 FROM source_files f JOIN identifications i USING(origin,file_index)
                        WHERE f.entry_id=$entry AND i.catalog_id=$catalog AND i.video_id IS NOT NULL
                            AND (i.explicit=1 OR i.evidence=f.name)),
                    (SELECT b.reason FROM video_attempts a JOIN collected_files f USING(origin,file_index)
                        JOIN request_blocks b ON b.provider_id=a.catalog_id AND b.question_id=a.question_id
                        WHERE f.entry_id=$entry AND a.catalog_id=$catalog AND a.evidence=f.name LIMIT 1);
                """;
            command.Parameters.AddWithValue("$catalog", catalog);
            command.Parameters.Add("$entry", SqliteType.Integer);
            foreach (var entry in entries.Where(entry => entry.Kind == FileKind.Video).DistinctBy(entry => entry.EntryId))
            {
                command.Parameters["$entry"].Value = entry.EntryId;
                using var reader = command.ExecuteReader();
                if (reader.Read() && !reader.GetBoolean(0))
                    result.Add(new(entry with { HasMetadata = reader.GetBoolean(1) },
                        reader.IsDBNull(2) ? null : new((RequestBlockKind)reader.GetInt32(2)), null));
            }
            return result;
        }, cancellation);

    internal static void ResetProtection(SqliteConnection connection, string? providerId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM video_searches WHERE ($provider IS NULL OR catalog_id=$provider)
                AND (rejected=1 OR json_array_length(choices)=0);
            DELETE FROM video_attempts WHERE $provider IS NULL OR catalog_id=$provider;
            """ + Database.Invalidate;
        command.Parameters.AddWithValue("$provider", (object?)providerId ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    internal Task ClearAttempts(string catalogId, CancellationToken cancellation) =>
        database.Run(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM video_attempts WHERE catalog_id=$catalog;" + Database.Invalidate;
            command.Parameters.AddWithValue("$catalog", catalogId);
            return command.ExecuteNonQuery();
        }, cancellation);

    internal Task<VideoTarget?> Next(string catalogId, CancellationToken cancellation) =>
        database.Run(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = $"""
                    SELECT f.entry_id,f.path,f.name,f.size FROM collected_files f
                    WHERE f.kind=0 AND {Eligible} LIMIT 1;
                    """;
                command.Parameters.AddWithValue("$catalog", catalogId);
                using var reader = command.ExecuteReader();
                return reader.Read()
                    ? new VideoTarget(
                        new Entry(
                            reader.GetInt64(0),
                            reader.GetString(1),
                            reader.GetString(2),
                            reader.GetInt64(3)
                        )
                        {
                            Kind = FileKind.Video,
                        },
                        catalogId,
                        LookupKind.Automatic
                    )
                    : null;
            },
            cancellation
        );

    internal Task<VideoAttempt?> Begin(
        VideoTarget target,
        string? language,
        CancellationToken cancellation
    ) =>
        database.Run(
            connection =>
            {
                using var command = TargetCommand(connection, target);
                // A batch file can wait while the person saves a decision for it,
                // which the batch must not replace.
                command.CommandText =
                    "SELECT EXISTS(SELECT 1 FROM collected_files f "
                    + "WHERE f.entry_id=$entry AND f.path=$path COLLATE PATH AND f.name=$name"
                    + (
                        target.Kind switch
                        {
                            LookupKind.Automatic => " AND " + Eligible,
                            LookupKind.Batch => " AND " + Undecided,
                            LookupKind.Refresh => " AND NOT EXISTS(SELECT 1 FROM source_files m JOIN identifications i USING(origin,file_index)"
                                + " WHERE m.entry_id=f.entry_id AND (i.explicit=1 AND i.video_id IS NULL"
                                + " OR i.video_id IS NOT NULL AND i.catalog_id<>$catalog))",
                            _ => string.Empty,
                        }
                    )
                    + ");";
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
                        CASE WHEN v.series_id IS NULL THEN v.year ELSE v.series_year END,i.explicit FROM source_files f
                    JOIN identifications i USING(origin,file_index)
                    JOIN video_information v ON v.catalog_id=i.catalog_id AND v.video_id=i.video_id
                    WHERE f.entry_id=$entry AND i.catalog_id=$catalog AND (i.explicit=1 OR i.evidence=f.name)
                    ORDER BY i.explicit DESC,i.decided DESC LIMIT 1;
                    """;
                VideoChoice? choice = null;
                var explicitChoice = false;
                using (var reader = command.ExecuteReader())
                    if (reader.Read())
                    {
                        explicitChoice = reader.GetBoolean(4);
                        choice = new VideoChoice(
                            target.CatalogId,
                            reader.GetString(0),
                            Kind(reader.GetString(1)),
                            reader.GetString(2)
                        )
                        {
                            Year = reader.IsDBNull(3) ? null : reader.GetInt32(3),
                        };
                    }
                var requestId = Guid.NewGuid().ToString("N");
                command.CommandText = """
                    INSERT INTO video_attempts(origin,file_index,catalog_id,evidence,request,outcome)
                    SELECT f.origin,f.file_index,$catalog,f.name,$request,NULL
                    FROM collected_files f WHERE f.entry_id=$entry
                    ON CONFLICT(origin,file_index,catalog_id) DO UPDATE SET evidence=excluded.evidence,
                        request=excluded.request,outcome=NULL,question_id=NULL;
                    """;
                command.Parameters.AddWithValue("$request", requestId);
                command.ExecuteNonQuery();
                return new VideoAttempt(target, requestId, choice, decided, language) { Explicit = explicitChoice };
            },
            cancellation
        );

    internal Task<bool> Complete(
        VideoAttempt attempt,
        VideoInformation? video,
        string outcome,
        CancellationToken cancellation
    ) =>
        database.Run(
            connection =>
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
                if (accepted && video is not null)
                    SaveVideo(connection, video, attempt.Language);
                command.CommandText = """
                    UPDATE video_attempts SET outcome=$outcome,question_id=$question
                    WHERE catalog_id=$catalog AND request=$request AND outcome IS NULL;
                    """;
                command.Parameters.AddWithValue(
                    "$outcome",
                    accepted
                        ? video is null
                            ? outcome
                            : "success"
                        : "cancelled"
                );
                command.Parameters.AddWithValue("$question",
                    accepted && video is null && attempt.BlockedRequest is { } blocked
                        ? blocked.QuestionId : (object)DBNull.Value);
                command.ExecuteNonQuery();
                if (accepted && video is not null)
                {
                    SaveIdentification(
                        connection,
                        attempt.Target.Entry.EntryId,
                        video,
                        attempt.Target.Kind == LookupKind.Explicit || attempt.Explicit
                    );
                }
                return accepted;
            },
            cancellation
        );

    internal Task<VideoChoice?> Series(
        VideoTarget target,
        Release release,
        CancellationToken cancellation
    ) =>
        database.Run(
            connection =>
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
                    if (
                        !StringComparer.OrdinalIgnoreCase.Equals(
                            Path.GetDirectoryName(reader.GetString(1)),
                            Path.GetDirectoryName(reader.GetString(2))
                        )
                    )
                        continue;
                    var previous = FileName.Interpret(reader.GetString(0));
                    if (
                        previous.Season != release.Season
                        || previous.Year != release.Year
                        || FileName.Normalize(previous.Title) != FileName.Normalize(release.Title)
                    )
                        continue;
                    var videoId = reader.GetString(3);
                    if (series is not null && series.VideoId != videoId)
                        return null;
                    series = new VideoChoice(
                        target.CatalogId,
                        videoId,
                        Kind(reader.GetString(4)),
                        reader.GetString(5)
                    )
                    {
                        Year = reader.IsDBNull(6) ? null : reader.GetInt32(6),
                    };
                }
                return series;
            },
            cancellation
        );

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

internal sealed record VideoTarget(Entry Entry, string CatalogId, LookupKind Kind);

// Language is null when the provider's information is the same in every language.
internal sealed record VideoAttempt(
    VideoTarget Target,
    string RequestId,
    VideoChoice? Choice,
    long Decided,
    string? Language
)
{
    internal bool Explicit { get; init; }
    internal RequestKey? BlockedRequest { get; init; }
}
