using System.Text.Json;
using Syno.TinyTorrent.Models;

namespace Syno.TinyTorrent.Library.Imdb;

/// <summary>Movies and series from the downloaded IMDb datasets; lookups send no request.</summary>
internal sealed class Provider(Dataset dataset) : IProvider
{
    internal const string CatalogId = "imdb";

    internal static ProviderOption Option(Dataset dataset) =>
        new(
            CatalogId,
            "IMDb",
            new("https://www.imdb.com/privacy"),
            new("https://data.imdb.com/non-commercial-datasets/"),
            _ => new Provider(dataset)
        )
        {
            Dataset = dataset,
            Kinds = new HashSet<VideoKind> { VideoKind.Movie, VideoKind.Series },
        };

    public Task<VideoSearch> Search(VideoQuery query, CancellationToken cancellation) =>
        dataset.Read<VideoSearch>(
            connection =>
            {
                // Each word is quoted, because full-text syntax reads a hyphen,
                // a colon or an upper-case AND in a release name as an operator.
                var words = query
                    .Text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Where(word => word.Any(char.IsLetterOrDigit))
                    .Select(word =>
                        "\"" + word.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
                    )
                    .ToArray();
                if (words.Length == 0)
                    return new([], false);
                using var command = connection.CreateCommand();
                // Exact titles come first, compared by Dataset.Key, so an
                // obscure film with a common title is not cut by the limit.
                // Without the vote order, the index
                // returns its matches by ascending IMDb ID, and a common word
                // would give the oldest.
                command.CommandText = """
                    SELECT t.title_id,t.title,t.original,t.year,t.kind FROM title_search s
                    JOIN titles t ON t.title_id=s.rowid
                    WHERE title_search MATCH $query AND ($year IS NULL OR t.year=$year)
                    ORDER BY CASE WHEN t.title_key=$title OR t.original_key=$title THEN 1 ELSE 0 END DESC,
                        t.votes DESC LIMIT 50;
                    """;
                command.Parameters.AddWithValue("$query", string.Join(' ', words));
                command.Parameters.AddWithValue("$title", Dataset.Key(query.Text));
                command.Parameters.AddWithValue("$year", (object?)query.Year ?? DBNull.Value);
                var choices = new List<VideoChoice>();
                using (var reader = command.ExecuteReader())
                    while (reader.Read())
                        choices.Add(
                            new VideoChoice(
                                CatalogId,
                                VideoInformation.FormatImdbId(reader.GetInt64(0)),
                                Dataset.ReadKind(reader),
                                reader.GetString(1)
                            )
                            {
                                Year = reader.GetInt32(3),
                                Original = reader.IsDBNull(2) ? null : reader.GetString(2),
                            }
                        );
                return new(choices, choices.Count == 0);
            },
            cancellation
        );

    public Task<VideoInformation> Describe(
        VideoChoice choice,
        VideoRequest request,
        CancellationToken cancellation
    )
    {
        if (
            choice.Kind is not (VideoKind.Movie or VideoKind.Series)
            || VideoInformation.ParseImdbId(choice.VideoId) is not { } identity
        )
            throw new VideoException(VideoFailure.InvalidResult);
        return dataset.Read(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText =
                    "SELECT title,year,genres,kind,rating,votes FROM titles WHERE title_id=$id;";
                command.Parameters.AddWithValue("$id", identity);
                VideoInformation video;
                using (var reader = command.ExecuteReader())
                {
                    // An update can remove a title that a saved choice names.
                    if (!reader.Read())
                        throw new VideoException(VideoFailure.InvalidResult);
                    video = new VideoInformation(
                        CatalogId,
                        choice.VideoId,
                        reader.GetString(0),
                        VideoKind.Movie
                    )
                    {
                        Year = reader.GetInt32(1),
                        Genres = reader.IsDBNull(2) ? [] : reader.GetString(2).Split(','),
                        ImdbId = identity,
                        Ratings = new() { Imdb = Dataset.ReadRating(reader) },
                    };
                }
                if (choice.Kind == VideoKind.Movie)
                    return video;
                var release = request.Release;
                var season = release.Season!.Value;
                command.CommandText = """
                    SELECT number,title,year FROM episodes
                    WHERE series_id=$id AND season=$season AND number IN (SELECT value FROM json_each($numbers))
                    ORDER BY number;
                    """;
                command.Parameters.AddWithValue("$season", season);
                command.Parameters.AddWithValue(
                    "$numbers",
                    JsonSerializer.Serialize(release.Episodes)
                );
                var episodes = new List<Episode>();
                int? aired = null;
                using (var reader = command.ExecuteReader())
                    while (reader.Read())
                    {
                        episodes.Add(
                            new Episode(
                                season,
                                reader.GetInt32(0),
                                reader.GetString(1),
                                string.Empty
                            )
                        );
                        aired ??= reader.IsDBNull(2) ? null : (int?)reader.GetInt32(2);
                    }
                // The IMDb data does not list every episode of every series.
                if (episodes.Count != release.Episodes.Count)
                    throw new MissingVideoException();
                // The episodes keep the series' title, genres and rating.
                return video with
                {
                    VideoId = $"{choice.VideoId}/{season}/{string.Join(',', release.Episodes)}",
                    Kind = VideoKind.Episode,
                    Series = choice,
                    Episodes = episodes,
                    Year = aired,
                };
            },
            cancellation
        );
    }
}
