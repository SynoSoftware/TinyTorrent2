using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Syno.TinyTorrent.Models;
using Syno.TinyTorrent.Services;

namespace Syno.TinyTorrent.Subtitles;

internal sealed partial class SubDL : Supplier
{
    private static readonly Uri Origin = new("https://api.subdl.com");

    internal override SubtitleSupplier SupplierId => SubtitleSupplier.SubDL;
    internal override Uri Privacy { get; } = new("https://subdl.com/privacy");
    internal override Uri Terms { get; } = new("https://subdl.com/terms");
    protected override IReadOnlyDictionary<string, string> Codes => LanguageCodes;
    protected override void Authenticate(HttpRequestMessage request, Account account) =>
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", account.Secret);

    internal override async Task Check(ProviderHttp http, Account account, CancellationToken cancellation)
    {
        var response = await Get(http, account, new Uri(Origin, "/api/v2/me"), cancellation).ConfigureAwait(false);
        using var document = Parse(response.Bytes);
    }

    protected override async Task<byte[]?> Find(ProviderHttp http, Account account,
        SubtitleLookup lookup, Release release, string language, CancellationToken cancellation)
    {
        var codes = language.Split(',');
        var query = "/api/v2/files/search?engine=local&episode_scope=exact&unpack=1&subs_per_page=30&filename=" +
            Uri.EscapeDataString(lookup.FileName) + "&languages=" + Uri.EscapeDataString(language);
        for (var page = 1; ; page++)
        {
            await lookup.Validate(cancellation).ConfigureAwait(false);
            var response = await Get(http, account, new Uri(Origin, query + "&page=" +
                page.ToString(CultureInfo.InvariantCulture)), cancellation).ConfigureAwait(false);
            using var document = Parse(response.Bytes);
            var root = document.RootElement;
            if (!root.TryGetProperty("match", out var match) || match.ValueKind == JsonValueKind.Null)
                return null;
            if (Text(match, "type") != (release.Season is null ? "movie" : "tv") ||
                release.Season is not null && Number(match, "season") is null ||
                release.Episodes.Count == 1 && Number(match, "episode") is null ||
                !Matches(release, Text(match, "title") ?? string.Empty, Number(match, "year"),
                    release.Season is not null ? Number(match, "season") : null,
                    release.Episodes.Count == 1 ? Number(match, "episode") : null))
                return null;
            if (!root.TryGetProperty("subtitles", out var subtitles) || subtitles.ValueKind != JsonValueKind.Array ||
                Number(root, "currentPage") != page || Number(root, "totalPages") is not { } pages || pages < 0)
                throw new SubtitleException(SubtitleFailure.Format);
            foreach (var subtitle in subtitles.EnumerateArray())
            {
                if (Text(subtitle, "language") is not { } code || !codes.Contains(code, StringComparer.OrdinalIgnoreCase) ||
                    IsForced(subtitle) || !subtitle.TryGetProperty("unpack_files", out var files) || files.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var file in files.EnumerateArray())
                {
                    if (Text(file, "language") is not { } fileCode || !codes.Contains(fileCode, StringComparer.OrdinalIgnoreCase) ||
                        !string.Equals(Text(file, "format"), "srt", StringComparison.OrdinalIgnoreCase) || IsForced(file) ||
                        Text(file, "release_name") is not { } name || !SameRelease(release, name))
                        continue;
                    if (!Uri.TryCreate(Origin, Text(file, "url"), out var download) ||
                        download.Scheme != "https" || !download.Host.Equals(Origin.Host, StringComparison.OrdinalIgnoreCase) ||
                        download.Port != 443 || download.UserInfo.Length != 0 ||
                        !download.AbsolutePath.StartsWith("/subtitle/", StringComparison.Ordinal))
                        throw new SubtitleException(SubtitleFailure.Format);
                    // Download links carry an API key; authentication stays in the request header.
                    download = new UriBuilder(download) { Query = string.Empty, Fragment = string.Empty }.Uri;
                    await lookup.Validate(cancellation).ConfigureAwait(false);
                    response = await Get(http, account, download, cancellation, ProviderHttp.FileLimit).ConfigureAwait(false);
                    return await Decode(response, cancellation).ConfigureAwait(false);
                }
            }
            if (page >= pages)
                return null;
        }
    }

    private static bool IsForced(JsonElement value) =>
        new[] { "release_name", "name", "comment" }.Any(field => ForcedLabel().IsMatch(Text(value, field) ?? string.Empty));

    [GeneratedRegex(@"(?:^|[\W_])(?:forced|foreign[ ._-]*(?:parts?(?:[ ._-]*only)?|only))(?:$|[\W_])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ForcedLabel();
}
