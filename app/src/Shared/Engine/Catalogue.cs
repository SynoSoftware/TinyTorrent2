using System.Text.Json;
using System.Text.Json.Serialization;

namespace Syno.TinyTorrent.Shared.Engine;

public static class Catalogue
{
    public sealed record Text
    {
        public string Value { get; init; } = string.Empty;
        public bool Exclude { get; init; }
    }

    public sealed record Range
    {
        public long? Minimum { get; init; }
        public long? Maximum { get; init; }
    }

    public sealed record Rule
    {
        public string RuleId { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public bool Enabled { get; init; } = true;
        public Text Title { get; init; } = new();
        public Range Size { get; init; } = new();
        public Range Count { get; init; } = new();
        public FileQuantifier Quantifier { get; init; }
        public Text Filename { get; init; } = new();
        public Text Path { get; init; } = new();
        public Text Extension { get; init; } = new();
        public Range FileSize { get; init; } = new();
    }

    public sealed record Observation
    {
        public long? Seeds { get; init; }
        public long? Peers { get; init; }
        public long Observed { get; init; }
        public int Responses { get; init; }
    }

    public sealed record File
    {
        public int Index { get; init; }
        public string Name { get; init; } = string.Empty;
        public string Path { get; init; } = string.Empty;
        public long Size { get; init; }
        public bool Padding { get; init; }
        public bool Symlink { get; init; }
        public string Root { get; init; } = string.Empty;
    }

    public sealed record Source
    {
        public string Hash { get; init; } = string.Empty;
        public string? V1 { get; init; }
        public string? V2 { get; init; }
        public string Name { get; init; } = string.Empty;
        public long Size { get; init; }
        public int Count { get; init; }
        public long Admitted { get; init; }
        public long? Seen { get; init; }
        public Observation Availability { get; init; } = new();
        public File File { get; init; } = new();
    }

    public sealed record Hit
    {
        public Source Matched { get; init; } = new();
        public Source Preferred { get; init; } = new();
        public IReadOnlyList<Source> Sources { get; init; } = [];
        public IReadOnlyList<string> Aliases { get; init; } = [];
        public bool SourcesLimited { get; init; }
    }

    public sealed record Cursor
    {
        public long Entry { get; init; }
        public int File { get; init; } = -1;
        public long Watermark { get; init; }
        public long Generation { get; init; }
        public string Value { get; init; } = string.Empty;
    }

    public sealed record Query
    {
        public CatalogueTarget Target { get; init; }
        public CatalogueSort Sort { get; init; }
        public bool Descending { get; init; }
        public string Text { get; init; } = string.Empty;
        public string Extension { get; init; } = string.Empty;
        public Range Size { get; init; } = new();
        public string Torrent { get; init; } = string.Empty;
        public int Limit { get; init; } = 100;
        public Cursor? Cursor { get; init; }
    }

    public sealed record Page
    {
        public IReadOnlyList<Hit> Hits { get; init; } = [];
        public Cursor? Next { get; init; }
        public bool Limited { get; init; }
        public bool Exhaustive { get; init; }
        public long Visited { get; init; }
        public long Watermark { get; init; }
        public long Generation { get; init; }
    }

    public sealed record Entry
    {
        public Source Torrent { get; init; } = new();
        public string Comment { get; init; } = string.Empty;
        public string Creator { get; init; } = string.Empty;
        public long Created { get; init; }
        public IReadOnlyList<File> Files { get; init; } = [];
        public int Offset { get; init; }
        public int Next { get; init; }
        public int SelectableCount { get; init; }
        public bool More { get; init; }
    }

    public sealed record SourcePage
    {
        public IReadOnlyList<Source> Sources { get; init; } = [];
        public IReadOnlyList<string> Aliases { get; init; } = [];
        public int Offset { get; init; }
        public int Next { get; init; }
        public bool More { get; init; }
        public bool Limited { get; init; }
    }

    public sealed record Status
    {
        public bool Exists { get; init; }
        public bool Enabled { get; init; }
        public Crawler? Crawler { get; init; }
        public string Error { get; init; } = string.Empty;
        public CatalogueCondition ErrorKind { get; init; }
        public int Acquiring { get; init; }
        public int Probing { get; init; }
        public int? AcquiringLimit { get; init; }
        public int? ProbingLimit { get; init; }
        public long Ready { get; init; }
        public long Nodes { get; init; }
        public long Sampled { get; init; }
        public double TorrentsPerSecond { get; init; }
        public long Torrents { get; init; }
        public long Files { get; init; }
        public long Rejected { get; init; }
        public long Pending { get; init; }
        public long Allocated { get; init; }
        public long Reusable { get; init; }
        public bool Rebuilding { get; init; }
        public bool Projecting { get; init; }
        public long Rebuilt { get; init; }
        public long Generation { get; init; }
        public int BudgetMib { get; init; } = 1024;
        public int StaleHours { get; init; } = 4320;
        public string Directory { get; init; } = string.Empty;
    }

    public sealed record PolicyPreview
    {
        public long Suppressed { get; init; }
        public long Retained { get; init; }
        public long Released { get; init; }
    }

    public sealed record Removal
    {
        public string Token { get; init; } = string.Empty;
        public long Torrents { get; init; }
        public long Files { get; init; }
        public long Bytes { get; init; }
        public long Removed { get; init; }
        public bool More { get; init; }
    }

    public sealed record RemovalRow
    {
        public Source Torrent { get; init; } = new();
        public IReadOnlyList<string> Reasons { get; init; } = [];
    }

    public sealed record RemovalPage
    {
        public IReadOnlyList<RemovalRow> Entries { get; init; } = [];
        public long Next { get; init; }
        public bool More { get; init; }
    }

    public sealed record Choice
    {
        public int Index { get; init; }
        public int Priority { get; init; } = 4;
    }

    public sealed record Addition
    {
        public required Source Source { get; init; }
        public Query Scope { get; init; } = new();
        public bool WholeTorrent { get; init; }
        public IReadOnlyList<int>? Files { get; init; }
        public string? Destination { get; init; }
        public int? DefaultPriority { get; init; }
        public IReadOnlyList<Choice>? Choices { get; init; }
    }
}

internal sealed class CatalogueClient(PipeClient client, Strings strings)
{
    private static readonly JsonSerializerOptions Format = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    internal Task<Catalogue.Status> StatusAsync() =>
        Read<Catalogue.Status>(Consumer.CatalogueStatus, "catalogue_status");

    internal Task<Catalogue.Page> QueryAsync(Catalogue.Query query) =>
        Read<Catalogue.Page>(Consumer.Catalogue, "catalogue_query", query);

    internal Task<Catalogue.Entry> DetailAsync(string hash, int offset = 0, int limit = 200, string query = "") =>
        Read<Catalogue.Entry>(Consumer.CatalogueDetail, "catalogue_detail", new { hash, offset, limit, query });

    internal Task<Catalogue.SourcePage> SourcesAsync(
        Catalogue.Source source, Catalogue.Query query, int offset = 0, int limit = 100
    ) => Read<Catalogue.SourcePage>(Consumer.CatalogueSources, "catalogue_sources",
        new
        {
            hash = source.Hash, index = source.File.Index, offset, limit,
            parent = query.Torrent, text = query.Text,
            watermark = query.Cursor?.Watermark ?? 0,
            generation = query.Cursor?.Generation ?? 0,
        });

    internal async Task<IReadOnlyList<Catalogue.Rule>> RulesAsync()
    {
        var reply = await Read<Rules>(Consumer.CatalogueRules, "catalogue_rules", new { action = "get" });
        return reply.Items;
    }

    internal Task<Catalogue.PolicyPreview> PreviewRulesAsync(IReadOnlyList<Catalogue.Rule> rules) =>
        Read<Catalogue.PolicyPreview>(Consumer.CatalogueRules, "catalogue_rules", new { action = "preview", rules });

    internal async Task<IReadOnlyList<Catalogue.Rule>> SaveRulesAsync(IReadOnlyList<Catalogue.Rule> rules)
    {
        var reply = await Send<Rules>("catalogue_rules", new { action = "save", rules });
        return reply.Items;
    }

    internal Task<Catalogue.Removal> PreviewRemovalAsync(bool clear = false) =>
        Read<Catalogue.Removal>(Consumer.CatalogueRules, "catalogue_preview", new { kind = clear ? "clear" : "excluded" });

    internal Task<Catalogue.Removal> RemoveAsync(string token) =>
        Send<Catalogue.Removal>("catalogue_remove", new { token });

    internal Task<Catalogue.RemovalPage> RemovalRowsAsync(string token, long after = 0, int limit = 100) =>
        Read<Catalogue.RemovalPage>(Consumer.CatalogueRules, "catalogue_removal_rows", new { token, after, limit });

    internal Task<Catalogue.Status> RebuildAsync(bool cancel = false) =>
        Send<Catalogue.Status>("catalogue_rebuild", new { cancel });

    internal async Task<JsonElement> PrepareAsync(Catalogue.Addition addition)
    {
        var source = addition.Source;
        var arguments = new
        {
            hash = source.Hash,
            scope = addition.Scope,
            destination = addition.Destination,
            index = addition.WholeTorrent ? (int?)null : source.File.Index,
            root = addition.WholeTorrent ? null : source.File.Root,
            length = addition.WholeTorrent ? (long?)null : source.File.Size,
            default_priority = addition.DefaultPriority ?? (addition.WholeTorrent ? (int?)null : 0),
            choices = addition.Choices ?? (addition.WholeTorrent ? null : (addition.Files ?? [source.File.Index])
                .Distinct().Select(index => new Catalogue.Choice { Index = index }).ToArray()),
        };
        return await client.Send("catalogue_prepare", Encode(arguments), Requester.Person);
    }

    internal void Withdraw()
    {
        client.Withdraw(Consumer.Catalogue);
        client.Withdraw(Consumer.CatalogueDetail);
        client.Withdraw(Consumer.CatalogueStatus);
        client.Withdraw(Consumer.CatalogueSources);
    }

    private async Task<T> Read<T>(Consumer consumer, string command, object? arguments = null) =>
        Decode<T>(await client.Read(consumer, command, arguments is null ? null : Encode(arguments)));

    private async Task<T> Send<T>(string command, object arguments) =>
        Decode<T>(await client.Send(command, Encode(arguments), Requester.Person));

    private static JsonElement Encode(object? value) => JsonSerializer.SerializeToElement(value, Format);

    private T Decode<T>(JsonElement value) => value.Deserialize<T>(Format)
        ?? throw new InvalidDataException(strings.Get("catalogue", "invalid_reply"));

    private sealed record Rules
    {
        [JsonPropertyName("rules")]
        public IReadOnlyList<Catalogue.Rule> Items { get; init; } = [];
    }
}
