using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Syno.TinyTorrent.Shared.Network;
using static Syno.TinyTorrent.Shared.Network.ProviderJson;
using static Syno.TinyTorrent.Subtitles.Supplier;

namespace Syno.TinyTorrent.Subtitles.Suppliers;

internal sealed partial class SubDL : ISupplier
{
    private static readonly Uri Origin = new("https://api.subdl.com");

    public SubtitleSupplier SupplierId => SubtitleSupplier.SubDL;
    public Uri Privacy { get; } = new("https://subdl.com/privacy");
    public Uri Terms { get; } = new("https://subdl.com/terms");
    public IReadOnlyDictionary<string, string> Codes => LanguageCodes;
    public string? BuiltInKey { get; } = ApiKey.BuiltIn("SubDLKey");

    public void Authenticate(HttpRequestMessage request, string key) =>
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

    public async Task Check(SupplierClient client, CancellationToken cancellation)
    {
        var reply = await client
            .Get(new Uri(Origin, "/api/v2/me"), cancellation)
            .ConfigureAwait(false);
        using var document = Parse(reply.Bytes);
    }

    public async Task<SubtitleSearch.Reply> Search(
        SubtitleQuery query,
        int page,
        SupplierClient client,
        CancellationToken cancellation
    )
    {
        var codes = query.Code.Split(',');
        var address =
            "/api/v2/files/search?engine=local&episode_scope=exact&unpack=1&subs_per_page=30&filename="
            + Uri.EscapeDataString(query.FileName)
            + "&languages="
            + Uri.EscapeDataString(query.Code);
        var reply = await client
            .Get(
                new Uri(Origin, address + "&page=" + page.ToString(CultureInfo.InvariantCulture)),
                cancellation
            )
            .ConfigureAwait(false);
        using var document = Parse(reply.Bytes);
        var root = document.RootElement;
        if (!root.TryGetProperty("match", out var match))
            throw new SubtitleException(SubtitleFailure.Format);
        // SubDL matches the file name to one title and answers every page for
        // it. A first page without a readable title names none; a later page
        // without one is an answer SubDL cannot read.
        if (match.ValueKind == JsonValueKind.Null)
        {
            if (page > 1)
                throw new SubtitleException(SubtitleFailure.Format);
            return new([], 1) { Empty = true };
        }
        var type = Text(match, "type");
        var series = type == "tv";
        int? season = series ? (int?)Number(match, "season") : null;
        int? episode = series ? (int?)Number(match, "episode") : null;
        if (
            type is not ("movie" or "tv")
            || Text(match, "title") is not { } reported
            || series && season is null
        )
        {
            if (page > 1)
                throw new SubtitleException(SubtitleFailure.Format);
            return new([], 1);
        }
        var title = new Listing(series ? VideoKind.Series : VideoKind.Movie, [reported])
        {
            Year = (int?)Number(match, "year"),
            Season = season,
            Episode = episode,
        };
        if (
            !root.TryGetProperty("subtitles", out var subtitles)
            || subtitles.ValueKind != JsonValueKind.Array
            || Number(root, "currentPage") != page
            || Number(root, "totalPages") is not { } pages
        )
            throw new SubtitleException(SubtitleFailure.Format);
        var candidates = new List<SubtitleCandidate>();
        foreach (var subtitle in subtitles.EnumerateArray())
        {
            if (
                Text(subtitle, "language") is not { } code
                || !codes.Contains(code, StringComparer.OrdinalIgnoreCase)
                || !subtitle.TryGetProperty("unpack_files", out var files)
                || files.ValueKind != JsonValueKind.Array
            )
                continue;
            foreach (var file in files.EnumerateArray())
            {
                if (
                    Text(file, "language") is not { } fileCode
                    || !codes.Contains(fileCode, StringComparer.OrdinalIgnoreCase)
                    || !string.Equals(
                        Text(file, "format"),
                        "srt",
                        StringComparison.OrdinalIgnoreCase
                    )
                    || Text(file, "release_name") is not { } name
                )
                    continue;
                if (
                    !Uri.TryCreate(Origin, Text(file, "url"), out var download)
                    || !ProviderHttp.CanFollow(download)
                    || !download.Host.Equals(Origin.Host, StringComparison.OrdinalIgnoreCase)
                    || !download.AbsolutePath.StartsWith("/subtitle/", StringComparison.Ordinal)
                )
                    throw new SubtitleException(SubtitleFailure.Format);
                // Download links carry an API key; authentication stays in the request header.
                download = new UriBuilder(download)
                {
                    Query = string.Empty,
                    Fragment = string.Empty,
                }.Uri;
                candidates.Add(
                    new(query.Language, name, name, download.AbsoluteUri)
                    {
                        Season = season,
                        Episode = episode,
                        Forced = IsForced(subtitle) || IsForced(file),
                    }
                );
            }
        }
        return new(candidates, pages)
        {
            Empty = subtitles.GetArrayLength() == 0,
            Title = title,
        };
    }

    public async Task<ProviderReply> Download(
        SubtitleCandidate candidate,
        SupplierClient client,
        CancellationToken cancellation
    )
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(candidate.Reference));
        return await client.Send(request, SupplierRequest.File, cancellation).ConfigureAwait(false);
    }

    // SubDL has no field for forced subtitles, so their labels say it.
    private static bool IsForced(JsonElement value) =>
        new[] { "release_name", "name", "comment" }.Any(field =>
            Text(value, field) is { } label && SubtitleFile.IsForced(label)
        );
}
