using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Syno.TinyTorrent.Shared.Network;
using static Syno.TinyTorrent.Subtitles.Supplier;

namespace Syno.TinyTorrent.Subtitles.Suppliers;

internal sealed partial class SubDL : ISupplier
{
    private static readonly Uri Origin = new("https://api.subdl.com");

    public SubtitleSupplier SupplierId => SubtitleSupplier.SubDL;
    public Uri Privacy { get; } = new("https://subdl.com/privacy");
    public Uri Terms { get; } = new("https://subdl.com/terms");
    public IReadOnlyDictionary<string, string> Codes => LanguageCodes;
    public string? BuiltInKey { get; } =
        typeof(SubDL)
            .Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(value => value.Key == "SubDLKey")
            ?.Value;

    public void Authenticate(HttpRequestMessage request, string key) =>
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

    public async Task Check(SupplierClient client, CancellationToken cancellation)
    {
        var reply = await client
            .Get(new Uri(Origin, "/api/v2/me"), cancellation)
            .ConfigureAwait(false);
        using var document = Parse(reply.Bytes);
    }

    public async Task<SubtitleSearch> Search(
        SubtitleQuery query,
        SupplierClient client,
        CancellationToken cancellation
    )
    {
        var candidates = new List<SubtitleCandidate>();
        var total = 0;
        var codes = query.Code.Split(',');
        var address =
            "/api/v2/files/search?engine=local&episode_scope=exact&unpack=1&subs_per_page=30&filename="
            + Uri.EscapeDataString(query.FileName)
            + "&languages="
            + Uri.EscapeDataString(query.Code);
        for (var page = 1; ; page++)
        {
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
            if (match.ValueKind == JsonValueKind.Null)
                return page == 1
                    ? new(candidates, true)
                    : throw new SubtitleException(SubtitleFailure.Format);
            // SubDL matches the file name to one title and answers every page
            // for it. When the first page's title is not the file's, nothing
            // is; when a later page's is not, the answer contradicts itself.
            var type = Text(match, "type");
            var series = type == "tv";
            int? season = series ? (int?)Number(match, "season") : null;
            int? episode = series ? (int?)Number(match, "episode") : null;
            if (
                type is not ("movie" or "tv")
                || Text(match, "title") is not { } title
                || series && season is null
                || !Matches(
                    query.Release,
                    new(series ? VideoKind.Series : VideoKind.Movie, [title])
                    {
                        Year = (int?)Number(match, "year"),
                        Season = season,
                        Episode = episode,
                    }
                )
            )
                return page == 1
                    ? new(candidates)
                    : throw new SubtitleException(SubtitleFailure.Format);
            if (
                !root.TryGetProperty("subtitles", out var subtitles)
                || subtitles.ValueKind != JsonValueKind.Array
                || Number(root, "currentPage") != page
                || Number(root, "totalPages") is not { } pages
                || pages < 0
            )
                throw new SubtitleException(SubtitleFailure.Format);
            foreach (var subtitle in subtitles.EnumerateArray())
            {
                total++;
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
                        || download.Scheme != "https"
                        || !download.Host.Equals(Origin.Host, StringComparison.OrdinalIgnoreCase)
                        || download.Port != 443
                        || download.UserInfo.Length != 0
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
            if (page >= pages)
                return new(candidates, total == 0);
        }
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
            ForcedLabel().IsMatch(Text(value, field) ?? string.Empty)
        );

    [GeneratedRegex(
        @"(?:^|[\W_])(?:forced|foreign[ ._-]*(?:parts?(?:[ ._-]*only)?|only))(?:$|[\W_])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex ForcedLabel();
}
