# Stage 8 gate record: provider access and the two plugin hosts

Gate done on 12 November 2026, by reading. Nothing was built or run, and no
request went to a provider. This record takes the place of the stage's text in
[the plan](../22-one-owner-per-rule.md#8-provider-access-and-the-two-plugin-hosts)
for the session that implements it.

Paths without a prefix are under `app/src`. Line numbers are those of commit
a4312a7. Stages 1 to 7 edit some of these files first, so each reference also
names its member; find the member when the line moved. Stage 1 already added
`Release.Kind`, and stage 2 adds `IFailure.Describe` to the failure types
([stage 2](stage-02.md)); this record builds on both.

## Where the stage stands

Every decision is made. The implementer needs no further design. The stage
changes no text in the catalogues. It changes six small behaviors, listed under
[What changes for a person](#what-changes-for-a-person).

The decisions, each with its reason below:

1. One reader of a built-in key, `ApiKey.BuiltIn(name)` in `Shared/Network`.
   The plugin still declares the key; the reading has one implementation.
2. One reader of JSON values, `Shared.Network.ProviderJson`, with `Text`,
   `Number` and `Child`. No value is `null`, and text is trimmed.
3. Library's request sender moves from `Library/ProviderJson.cs` into the host,
   as the part file `Library/VideoLookup/Requests.cs`.
4. The three reply-failure switches stay. They are three vocabularies, not
   one rule.
5. One rule for an address that a reply names, `ProviderHttp.CanFollow`.
6. The Library host maps an unreadable answer once, applies the title limit
   once and checks the kind once. `IProvider` keeps its shape.
7. One rating factory, `Rating.From`. A score of 0 that a site states is a
   rating; MDBList's 0 stays MDBList's own mark for "unknown".
8. One answer to "what does this provider still need",
   `VideoLookup.Requirement`.
9. One title rule in `Shared/FileName`: `TitleWords` and `TitleKey`. An
   apostrophe joins its word. `FileName.Words` does not change, because the
   search ruling owns it.
10. One answer to "is this failure still waiting", `ProviderException.IsWaiting`,
    and one read of a hold's retry time inside `VideoLookup`.
11. The Subtitles host owns the page loop. `ISupplier.Search` reads one page.
12. One "forced" rule, `SubtitleFile.IsForced`, with the wider pattern.
13. One fact for "this name can be searched for", `Release.IsSearchable`.
14. `ISupplier.Available` goes. The host derives it from `Access` and the
    built-in key.

## Gate step 1: the findings, read again

Every finding of the stage was read on both sides, also the ones the plan does
not mark "to re-read", because three of them describe the code wrongly.

| Finding | Result | The rule, in one sentence |
| --- | --- | --- |
| Built-in key, 6 copies | Kept | The build writes a key into the assembly under a name, and a blank or missing value is no key. |
| JSON text property, 5 copies | Kept. `Wikidata.Value` keeps its name, because a SPARQL cell is its own shape, and calls the owner. | A property that is missing, is not text, or is blank is no value; text is trimmed. |
| Common part of a reply failure, 3 copies | **Dropped.** See [decision 4](#4-the-reply-failure-switches-stay). | No sentence states one rule: each switch translates the shared classification into its feature's own failure list. |
| Address safety, 4 copies | Kept, as a second rule beside the one in `ProviderHttp.Transfer`. | An address that a reply names is used only when it is HTTPS on the default port without credentials. |
| `VideoLookup.Available` is the one answer | Kept, with a wider result. `Browser.ChooseAutomaticDownload` needs to know *what* is missing, not only whether. | A provider can answer when its data is installed, or when it needs no key or has one. |
| Host maps a parse error once, 8 copies | Kept | An answer whose shape the provider cannot read is an `InvalidResult` of that one search or video. |
| Title limit, 7 copies | Kept. TMDB has no limit and lists search results without a title. | A title has 1 to 300 characters; a choice without one is not listed, and a description without one is unusable. |
| Kind check, 5 copies | Kept. `ProviderOption.Kinds` already states the fact. | A provider describes only the kinds its option lists. |
| Builds a rating once, 6 copies | Kept | A rating is a site's score scaled to ten points; a score outside its scale is no rating, and a vote count of zero is unknown. |
| Query words from `FileName.Words` (`Omdb` 32, `Wikidata` 35) | Kept, with `Queries` in `VideoLookup/Identification.cs` as the third copy | A query for a catalogue that reads punctuation as syntax is the title's words joined by spaces. |
| One title key (`Imdb/Dataset.cs` 455 against `FileName.cs` 85) | Kept, but the pair is another one. `FileName.Normalize` (85) only folds case and accents, and `Dataset.Key` calls it. The real pair is `Dataset.Key` against `Words(Normalize(title))` in `Supplier.TitleWords`, `VideoLookup.Confidence` and `VideoLookup.SearchRequest`. | Two titles are the same when their words agree, in lower case without accents or punctuation, and an apostrophe joins its word. |
| Retry time of a hold, 5 sites | Kept, as two rules | A provider failure waits while its retry time is in the future. The retry time of a failure is the provider failure's or the request block's. |
| Page loop, 3 copies | Kept. The plan's wording is imprecise: the host already reads the pages (`Supplier.Search`, 197), and each supplier also runs a loop that produces them. | A search reads pages 1 to N, where N is the count the supplier reports, and a negative count is an unreadable answer. |
| One "forced" rule, 2 patterns | Kept, settled as the wider pattern | A label marks a forced-only subtitle when it holds the word "forced", or "foreign" with "parts" or "only". |
| Sample or untitled file, 2 copies | Kept. `VideoLookup.CanMatch` (418) is a third copy. | A release can be searched for when it is no sample or trailer and has a title. |

A seventh reader of assembly metadata exists, `Shared/Engine/EngineProcess.cs`
15. It stays. It reads a required build fact and fails when it is absent; a
missing key is a normal state. One function with a "required" flag would serve
two rules.

## Gate step 2: the open question

**A score of 0 in `MdbList` against `Omdb` and `Wikidata`.**

**Decision: a score of 0 that a site states is a rating. A provider's own mark
for a score it lacks is no rating, and stays with that provider. `Omdb` and
`Wikidata` keep 0 as a rating, and `MdbList` keeps reading its 0 as "no
score". No behavior changes.**

The Library contract decides the first half.
[Presentation](../../contracts/library/presentation.md#configurations) says of the
rating columns: "A missing score stays empty; it is not zero." So zero and
missing are two states, and a zero is a value.
[Providers](../../contracts/library/providers.md#identification-and-early-enrichment)
says the same of counts: "Unknown counts use secondary foreground; they are not
treated as zero." A Rotten Tomatoes 0% is a real score that the site shows, so
OMDb's `"0%"` and a Wikidata `"0%"` statement are ratings.

The second half follows the stage's "Settled as": "a provider's own words such
as "N/A" stay with that provider." `MdbList.ReadRating` (148) carries the
comment "a source without votes has no score", and the same file reads
MDBList's `revenue` and `votes` of 0 as unknown. The code treats 0 as MDBList's
word for "unknown" in all three fields. Changing that could show a false 0 on
every movie MDBList has no score for, which breaks the contract sentence above
for many rows. Keeping it can hide a true 0% for a few. The first error is
worse, and the contract forbids it.

Reading could not settle what MDBList writes for an unknown score, because the
repository holds no saved MDBList page. See
[What reading could not settle](#what-reading-could-not-settle).

## Gate step 3: the design

Each owner was designed in more than one way. The rejected candidates are in
the table of each section.

### 1. The built-in key

Owner: `Shared/Network/ApiKey.cs`, new.

```csharp
/// <summary>The API keys that the build writes into the release.</summary>
internal static class ApiKey
{
    /// <summary>
    /// The key the build wrote under <paramref name="name"/>, or null when
    /// this build has none.
    /// </summary>
    internal static string? BuiltIn(string name) =>
        typeof(ApiKey)
            .Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(value => value.Key == name)
            ?.Value
            is { } key
        && !string.IsNullOrWhiteSpace(key)
            ? key
            : null;
}
```

| Candidate | Result |
| --- | --- |
| The plugin declares only the name; the host reads the key | Rejected. `Browser.ChooseAutomaticDownload` and `MainWindow.ShowKey` read `ProviderOption.HasBuiltInKey` from the option alone, and `app/tests/SourceChecks.ps1` 421 gives an option a fixture key through `BuiltInKey`. The option must hold the value. |
| One table of all six keys in one file | Rejected. Adding or removing a plugin would edit a central list, which [providers](../../contracts/library/providers.md#replaceable-movie-providers) rules out: "shared lookup, storage, settings, details and dialog code need no edits". |
| One reader that the plugin's declaration calls | **Chosen.** The rule has one implementation. Each plugin keeps one line, which names what is its own: the name of its key. |

Callers:

| Caller | After |
| --- | --- |
| `Library/Tmdb/Provider.cs` `Option` (25 to 28) | `BuiltInKey = ApiKey.BuiltIn("TmdbToken"),` |
| `Library/Omdb/Provider.cs` `Option` (25 to 28) | `ApiKey.BuiltIn("OmdbKey")` |
| `Library/MdbList/Provider.cs` `Option` (27 to 30) | `ApiKey.BuiltIn("MdbListKey")` |
| `Subtitles/Suppliers/OpenSubtitles.cs` `BuiltInKey` (24 to 28) | `public string? BuiltInKey { get; } = ApiKey.BuiltIn("OpenSubtitlesKey");` |
| `Subtitles/Suppliers/SubDL.cs` `BuiltInKey` (21 to 25) | `ApiKey.BuiltIn("SubDLKey")` |
| `Subtitles/Suppliers/SubSource.cs` `BuiltInKey` (41 to 45) | `ApiKey.BuiltIn("SubSourceKey")` |
| `ProviderOption.HasBuiltInKey` (`Library/IProvider.cs` 63) | `BuiltInKey is not null` |
| `Supplier.HasBuiltInKey` (`Subtitles/Supplier.cs` 29) | `supplier.BuiltInKey is not null` |
| `OpenSubtitles.Available` (22) | Deleted, see [decision 14](#14-isupplieravailable-goes) |

`using System.Reflection;` goes from the six plugin files. Each already
imports `Shared.Network`, except the three Library providers, which add it.

"The person's own key, else the built-in one" (`VideoLookup.Key` 230,
`Supplier.ApiKey` 99) stays two lines, as the plan's
[What stays as it is](../22-one-owner-per-rule.md#what-stays-as-it-is) rules.

### 2. JSON values

Owner: `Shared/Network/ProviderJson.cs`, new. The name is free because
[decision 3](#3-librarys-sender-moves-into-its-host) moves Library's type.

```csharp
/// <summary>
/// Reads values from a provider's JSON answer. A property that is missing or
/// holds another kind of value is no value, so a reader needs no shape checks.
/// </summary>
internal static class ProviderJson
{
    /// <summary>
    /// The property's text without surrounding white space, or null when the
    /// element has no such text or the text is blank.
    /// </summary>
    internal static string? Text(JsonElement element, string property) =>
        Child(element, property) is { ValueKind: JsonValueKind.String } value
        && value.GetString()!.Trim() is { Length: > 0 } text
            ? text
            : null;

    internal static long? Number(JsonElement element, string property) =>
        Child(element, property) is { ValueKind: JsonValueKind.Number } value
        && value.TryGetInt64(out var number)
            ? number
            : null;

    /// <summary>
    /// The property's value, or an undefined element when the element has
    /// none, so nested reads need no checks.
    /// </summary>
    internal static JsonElement Child(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var child)
            ? child
            : default;
}
```

`Number` is `Supplier.Number` (524) unchanged, and `Child` is `MdbList.Child`
(207) unchanged. They move with `Text` because the same callers need all
three, and a supplier would otherwise import its JSON readers from two types.

| Candidate | Result |
| --- | --- |
| No value is the empty string | Rejected. The [code style](../../../AGENTS.md#code-style) gives each state its own value, "so an empty string, a zero or a sentinel you invent never selects a second behavior". The suppliers already use `null`. |
| Extension methods on `JsonElement` | Rejected. [Naming](../../agents/naming.md#files) keeps extension methods free of product rules, and "blank is no value" is one. |
| A type named `Json` | Rejected. In a file with `using System.Text;` the name resolves to the namespace `System.Text.Json` (CS0118). `OpenSubtitles.cs` has that line. |
| A static type the plugins import with `using static` | **Chosen.** It is the pattern the three suppliers use today (`using static Syno.TinyTorrent.Subtitles.Supplier;`), so call sites stay `Text(item, "title")`. |

Callers:

| Caller | After |
| --- | --- |
| `Supplier.Text` (517), `Supplier.Number` (524) | Deleted. `Supplier.Parse` (532) stays: it throws the Subtitles failure. |
| `OpenSubtitles.cs`, `SubDL.cs`, `SubSource.cs` | Add `using static Syno.TinyTorrent.Shared.Network.ProviderJson;` beside the existing `using static`. Call sites do not change. |
| `MdbList.Text` (201), `MdbList.Child` (207) | Deleted; the file adds the `using static` line. |
| `Tmdb.Text` (323) | Deleted; the file adds the `using static` line. |
| `Omdb.Text` (235) | Keeps its name and its comment about "N/A", and becomes `ProviderJson.Text(element, property) is { } text && text != "N/A" ? text : null`, with return type `string?`. A member of the class hides the imported name, so the call is qualified. |
| `Wikidata.Value` (307) | Keeps its name and comment, and becomes `Text(Child(row, column), "value")`, with return type `string?`. |
| `MdbList` reads of `votes` (161) and `revenue` (137); `Tmdb` reads of `revenue` (201) and `vote_count` (239) | `Number(element, "votes") is > 0 and var votes ? votes : null`, and the same form for the others. The reads of `year` and `tmdbid` (`MdbList.ReadYear`, `MdbList.TmdbId`) keep `TryGetInt32` on `Child(...)`, because they need a 32-bit value. |

The return type changes from `string` to `string?` in the four Library
providers. The compiler's nullable warnings list every site. Each site takes
one of three forms:

- A required value: `Text(item, "title") is { } title`.
- A value that `VideoInformation` holds as text: `Synopsis = Text(movie, "description") ?? string.Empty`.
- A value passed to a parser: `VideoInformation.ParseYear(Text(item, "Year") ?? string.Empty)`.
  `ParseImdbId` takes a span, and a null string converts to an empty span.

`Tmdb.Text` today throws when its element is no object, and the catch around
it turns that into `InvalidResult`. With the shared reader the element gives no
value, and the next required read (`Id`, or the title check) fails the same
answer. The outcome is the same.

### 3. Library's sender moves into its host

`Library/ProviderJson.cs` moves with `git mv` to
`Library/VideoLookup/Requests.cs` and becomes a part of `VideoLookup`:

```csharp
internal sealed partial class VideoLookup
{
    // The members Get (308) and Offline (328) move here from Identification.cs.

    private static async Task<JsonElement> Send(
        ProviderHttp http,
        ProviderCall call,
        CancellationToken cancellation,
        Func<JsonElement, VideoFailure?>? refusal = null
    ) { /* the body of ProviderJson.Get, unchanged */ }

    private static Exception Failure(ReplyException error) => /* unchanged */;
}
```

`Get` calls `Send(http, call, cancellation, refusal)`. `Send` stays static with
its `http` parameter, so `app/tests/SourceChecks.ps1` 962 and 974 change only
the type and the method name.

`Offline` changes its exception from `InvalidOperationException` to
`NotSupportedException`, because [decision 6](#6-the-library-host) maps
`InvalidOperationException` from a provider to `InvalidResult`, and a provider
without network access that sends a request is a defect that must stay visible.

Reason for the move: the plan gives "what every plugin shares" to the host, and
sending a request is host work. The static class was the host's sender under a
name that the shared reader needs. Reason for a part file: `VideoLookup.cs` has
1,492 lines and `Identification.cs` 452, and requests are one coherent
responsibility.

### 4. The reply-failure switches stay

`VideoLookup.Failure` (today `ProviderJson.Failure`, 66), `Supplier.Failure`
(363) and `DataDownload.Failure` (42) each translate a `ReplyException` into
their feature's failure:

| `ReplyException` | Library | Subtitles | Data download (after stage 2) |
| --- | --- | --- | --- |
| `Held`, `Wait` | `RequestBlockedException` | `Quota` | `RequestBlockedException` |
| `Held`, `Refused` | `ProviderException(Refused)` | `Authentication` | `Failed` |
| `Held`, `Unavailable` | `ProviderException(Unavailable)` | `Network` | `Failed` |
| `Held`, `Invalid` | `ProviderException(InvalidResult)` | `Network` | `Failed` |
| `Expired` | `ProviderException(Refused)` | `Authentication` | `Failed` |
| `Missing` | `MissingVideoException` | `Format` | `Failed` |
| `Route` | `RouteException` | `Route` | `DownloadException(Route)` |

The common part already has one owner: `ProviderHttp.Classify` and `Hold`
(`Shared/Network/ProviderHttp/Replies.cs`) read the reply and record the hold
once. What remains in each feature is its own list of failures, and the
[code style](../../../AGENTS.md#code-style) asks for exactly that: "A contract
separates the failures its caller handles differently." One mapper would take a
table per feature, which is a function with a flag for each caller.
`SourceChecks.ps1` 1012 to 1016 already guards that the three agree on the
hold.

`ReplyException` keeps its nested types. `Held` carries a hold that is never
null, so the type states "only a held failure holds the provider".

### 5. An address that a reply names

Owner: `ProviderHttp` in `Shared/Network/ProviderHttp.cs`.

```csharp
// In Transfer, in place of the pattern at 222:
if (request.RequestUri is not { } address || !IsProtected(address))
    throw new InvalidOperationException(
        "Provider requests require HTTPS without URL credentials."
    );

private static bool IsProtected(Uri address) =>
    address is { IsAbsoluteUri: true, Scheme: "https", UserInfo: "" };

/// <summary>
/// Whether a request may go to an address that a reply names, in a redirect
/// or in its body: HTTPS on the default port without credentials.
/// </summary>
internal static bool CanFollow(Uri address) => IsProtected(address) && address.IsDefaultPort;
```

`Transfer` does not gain the port condition. `app/tests/ProviderChecks.ps1` 41
sends a request to `https://127.0.0.1:<port>/check` and expects the route to
refuse it, so the transport must accept a port.

| Caller | After |
| --- | --- |
| `Supplier.Send` (293) | `|| !ProviderHttp.CanFollow(target)` in place of the `target is not { ... }` pattern |
| `OpenSubtitles.CanDownload` (301) | `ProviderHttp.CanFollow(uri) && (uri.Host is ...) && uri.AbsolutePath.StartsWith(...)` |
| `SubDL.Search` (137 to 143) | `!Uri.TryCreate(...) || !ProviderHttp.CanFollow(download) || !download.Host.Equals(...) || !download.AbsolutePath.StartsWith(...)` |

The host names and the paths stay with each supplier; they are its own content.
The host does not check a supplier's first request for the supplier, because
SubDL refuses a bad link when it reads the search answer, before the answer is
saved, and the host sees the address only at download.

### 6. The Library host

Owner: `VideoLookup`, in `Library/VideoLookup/Identification.cs`. `IProvider`
keeps `Prepare`, `Search` and `Describe`.

```csharp
// A provider reads its answer with JsonElement, whose accessors throw these
// when the answer has another shape. Such an answer fails the one search or
// video. A disposed owner is not an answer.
private static async Task<T> Ask<T>(Func<Task<T>> provider)
{
    try
    {
        return await provider().ConfigureAwait(false);
    }
    catch (Exception error)
        when (error
                is (InvalidOperationException and not ObjectDisposedException)
                    or KeyNotFoundException
                    or FormatException
        )
    {
        throw new VideoException(VideoFailure.InvalidResult, error);
    }
}

// A title has 1 to 300 characters. A choice without one is not listed, and a
// description without one is unusable.
private static bool HasTitle(string title) => title.Length is > 0 and <= 300;
```

Where the host applies them:

| Member | Change |
| --- | --- |
| `SearchOne` (232), both calls of `context.Provider.Search` | `var answer = await Ask(() => context.Provider.Search(...))`, then `answer = answer with { Choices = [.. answer.Choices.Where(choice => HasTitle(choice.Title))] };` before the answer is saved or returned. `Empty` stays the provider's. |
| `Describe` (403) | Before the call: `if (!context.Option.Kinds.Contains(choice.Kind)) throw new VideoException(VideoFailure.InvalidResult);`. The call becomes `await Ask(() => context.Provider.Describe(choice, request, context.Token))`. After it: `if (!HasTitle(video.Title)) throw new VideoException(VideoFailure.InvalidResult);` and `video = video with { Related = [.. video.Related.Where(related => HasTitle(related.Title))] };`, before `store.SaveChoices`. |
| `Read` (331), the call `download(cancellation)` | `record = await Ask(() => download(cancellation))`, inside the existing `try`, so an unreadable page is still recorded as `Unusable` and not saved. |

What each provider loses:

| Provider | Catch blocks deleted | Title checks deleted | Kind check deleted |
| --- | --- | --- | --- |
| `MdbList` | `Search` (66) | `Search` (55: the `Length` pattern becomes `is { } title`), `Describe` (97, 110) | `Describe` (80); the TMDB-number check stays |
| `Omdb` | `Search` (76) | `Search` (62), `Describe` (105, 119); the `Response` checks stay | `Describe` (89); the IMDb-ID check stays |
| `Tmdb` | `Search` (60, 96), `Information` (214), `ReadEpisode` (231) | `Information` (157: only the `id` check stays). `ReadEpisode`'s name check stays; an episode name is no title. | `Describe` (110); the number check stays |
| `Wikidata` | `Search` (108), `Information` (240) | `Search` (92: `Title.Length` leaves the filter), `Information` (198) | `Describe` (121); the item check stays |
| `Imdb` | None | None | `Describe` (82); the IMDb-ID check stays |

| Candidate | Result |
| --- | --- |
| Split `IProvider` into "build the request" and "read the answer", so the host runs each reader and maps its errors exactly | Rejected. TMDB's dated search sends two requests and merges them, and an episode description reads one record per episode in order. The split needs a multi-step protocol that reshapes every provider to remove eight catch blocks. |
| A shared function that each provider calls around its reading | Rejected. The provider must still remember the call, at eight sites. The plan gives the mapping to the host. |
| The host wraps each call into a provider | **Chosen.** A provider cannot forget it. |

The cost of the chosen way, stated plainly: the wrapper also covers host code
that a provider calls back, `ProviderGet` and `ReadRecord`. `ProviderHttp`
already turns its own `InvalidOperationException` into a route failure
(`IsRoute`, 301), and a disposed owner is excluded. Any other
`InvalidOperationException` from the store or the request policy inside those
callbacks is a defect. After this change it fails the one video as
`InvalidResult`, with the defect as inner exception, where today it ends the
batch with an error. No such exception is known.

One difference in saving: today `Omdb` and `MdbList` check the title inside the
download callback, so a page without a usable title is not saved. After the
change the page is saved and the host then refuses the description. The
[providers contract](../../contracts/library/providers.md#saved-provider-data)
already says: "Library keeps a raw record as soon as the provider returns it,
also when the lookup rejects the video." No further request is sent for that
video in either case.

### 7. Ratings

Owner: `Rating` in `Library/IProvider.cs`.

```csharp
/// <summary>
/// The rating for a site's <paramref name="score"/> on a scale that ends at
/// <paramref name="maximum"/>, or null when the score is outside the scale.
/// A vote count that is not positive is unknown.
/// </summary>
internal static Rating? From(double score, double maximum, VideoKind kind, long? votes = null) =>
    maximum > 0 && double.IsFinite(maximum) && score >= 0 && score <= maximum
        ? new Rating(score / maximum * 10, kind) { Votes = votes > 0 ? votes : null }
        : null;
```

A score that is not a number fails both comparisons, so the factory needs no
separate check for it.

| Caller | After |
| --- | --- |
| `MdbList.ReadRating` (148) | `Child(rating, "score") is { ValueKind: JsonValueKind.Number } score && score.TryGetDouble(out var value) && value > 0 ? Rating.From(value, 100, VideoKind.Movie, Number(rating, "votes")) : null`. The comment becomes: "Each rating's score is its source's rating on a 0 to 100 scale. MDBList writes 0 for a score it does not know." |
| `Omdb.ReadScore` (156) | `double.TryParse(...) ? Rating.From(value, maximum, VideoKind.Movie) : null` |
| `Omdb.ReadRating` (185) | `double.TryParse(...) ? Rating.From(value, 10, VideoKind.Movie, votes) : null`, where `votes` is the parsed `imdbVotes` or null |
| `Tmdb.ReadRating` (238) | `Number(data, "vote_count") is > 0 and var votes && Child(data, "vote_average") is { ValueKind: JsonValueKind.Number } average && average.TryGetDouble(out var value) ? Rating.From(value, 10, kind, votes) : null`. TMDB's rule "no votes, no rating" stays its own. |
| `Wikidata.ReadRating` (249) | Both numbers parsed, then `Rating.From(value, maximum, VideoKind.Movie, votes)` |
| `Imdb.Dataset.ReadRating` (439) | The "title without votes has none" check stays, then `Rating.From(reader.GetDouble(...), 10, ReadKind(reader), reader.GetInt64(votes))` |

After the change a search for `new Rating(` in `app/src` finds only the
factory.

### 8. What a provider still needs

Owner: `VideoLookup` in `Library/VideoLookup.cs`. The private `Available`
(235) becomes:

```csharp
/// <summary>
/// What the person must supply before the provider can answer: its data, an
/// API key, or nothing. A provider that needs a key works only with one, and
/// one that answers from downloaded data only once it is installed.
/// </summary>
internal ProviderRequirement Requirement(ProviderOption option)
{
    lock (_gate)
        return option.Dataset is { } dataset
            ? dataset.IsInstalled ? ProviderRequirement.None : ProviderRequirement.Data
            : option.Access == Access.Key && string.IsNullOrWhiteSpace(Key(option))
                ? ProviderRequirement.Key
                : ProviderRequirement.None;
}
```

The lock is re-entrant, so the three callers that hold `_gate` call the same
member.

| Caller | After |
| --- | --- |
| `VideoLookup.Select` (131) | `Requirement(option) != ProviderRequirement.None` |
| `VideoLookup.Refresh` (369) | The same |
| `VideoLookup.Execute` (1143) | `available = Requirement(context.Option) == ProviderRequirement.None;`, and the `lock` around it goes |
| `Browser.ChooseAutomaticDownload` (`Library/Browser/Information.cs` 160 to 165) | `var missing = enabled && _videos is { } videos ? videos.Requirement(option) : ProviderRequirement.None;` |

`Browser.HasKey` and `ProviderOption.HasBuiltInKey` stay; `MainWindow.ShowKey`
(`Shell/MainWindow/Library.cs` 280) uses both for the account dialog.

### 9. The title rule

Owner: `FileName` in `Shared/FileName.cs`. `Shared` is the base, so `Library`
and `Subtitles` both reach it.

```csharp
/// <summary>
/// The words of a title, as written. An apostrophe joins its word, because a
/// file name lacks it: "Ocean's Eleven" and Oceans.Eleven both give Oceans
/// and Eleven. Search splits at every such character instead; see
/// <see cref="Words"/>.
/// </summary>
internal static string[] TitleWords(string title)
{
    var words = new List<string>();
    var word = new StringBuilder();
    foreach (var character in title)
    {
        if (char.IsLetterOrDigit(character))
            word.Append(character);
        else if (character is not ('\'' or '’') && word.Length > 0)
        {
            words.Add(word.ToString());
            word.Clear();
        }
    }
    if (word.Length > 0)
        words.Add(word.ToString());
    return [.. words];
}

/// <summary>
/// A title in lower case without accents or punctuation, which two spellings
/// of one title share: "Ocean's Eleven" and Oceans.Eleven both give
/// "oceans eleven".
/// </summary>
internal static string TitleKey(string title) => string.Join(' ', TitleWords(Normalize(title)));
```

`TitleKey` gives exactly what `Imdb.Dataset.Key` (455) gives today: the loop
tests each character the same way. That matters because the installed IMDb
data holds these keys in `titles.title_key` and `original_key`
([IMDb data](../../contracts/imdb-datasets.md)), and a key computed another way
would miss them until the next data update.

| Candidate | Result |
| --- | --- |
| Change `FileName.Words` so an apostrophe joins | Rejected. The [search ruling](../../../AGENTS.md#working-rules) fixes it: "A word starts ... after any character that is not a letter or a digit". `SearchWords` and `StartsWord` rest on `Words`. |
| `TitleWords(title) => Words(title without apostrophes)` | Rejected. `Words` counts every Unicode number as part of a word (`\p{N}`), and `Dataset.Key` counts only letters and decimal digits. `Alien³` would get the key `alien³` where the installed data holds `alien`. |
| Keep `Dataset.Key` and call it from `Subtitles` | Not possible. `Subtitles` must not name `Library`. |
| Two members in `FileName`, written as one loop | **Chosen.** |

Callers:

| Caller | After |
| --- | --- |
| `Imdb.Dataset.Key` (455) | Deleted. Its three uses (304, 804, 805) and `Imdb/Provider.cs` 53 call `FileName.TitleKey`; the comment at `Provider.cs` 40 names it. |
| `Supplier.TitleWords` (454) | Deleted. `Supplier.Matches` (434) compares `FileName.TitleKey(title)` with `FileName.TitleKey(release.Title)`, and `Supplier.Within` (457) uses the two keys in its `Contains`. The comment about "U.N.C.L.E." moves to `Matches`. |
| `VideoLookup.Confidence` (436, 440) | `FileName.TitleWords(FileName.Normalize(...)).ToHashSet()` in both places. Its signature stays; `SourceChecks.ps1` 86 calls it. |
| `VideoLookup.SearchRequest` (300), the counter | `[FileName.TitleKey(query)]` |
| `VideoLookup.Queries` (227), the fallback query | `context.Provider.Prepare(string.Join(' ', FileName.TitleWords(query)))` |
| `Omdb.Prepare` (32) | `string.Join(' ', FileName.TitleWords(query))`; the file adds `using Syno.TinyTorrent.Shared;` and loses `using System.Text.RegularExpressions;` |
| `Wikidata.Prepare` (35) | `string.Join(' ', FileName.TitleWords(query.ToLowerInvariant()))`; the comment stays; `using System.Text;` goes |

`IProvider.Prepare` stays on the provider. How a catalogue reads a query is the
provider's own; the words come from the one owner.

Not changed, each for its reason:

- `Supplier.Likeness` (481, 482) and `SameRelease` compare release names, not
  titles. Release tags have no apostrophes.
- `Imdb.Provider.Search` quotes each word for SQLite's full-text syntax. That
  is the catalogue's own query form.
- `VideoLookup.Match` (180) compares two spellings from one provider, the
  search result and its description, so `Normalize` is the right strength.
- `Library/Store/Videos.cs` 50, 53, 77, 164 and 170 find a saved choice by
  `FileName.Normalize(title)`, which the database stores in indexed columns. A
  file named Oceans.Eleven therefore does not find the saved choice "Ocean's
  Eleven" and searches the provider once more. Moving this to `TitleKey` needs
  a migration of saved data under the
  [database contract](../../contracts/database.md), which is a major
  refactoring of an established contract. It is not part of this stage. See
  [Left for separate work](#left-for-separate-work).
- `Subtitles/Acquisition/Storage.cs` 776 builds a saved question key with
  `Normalize`. It is saved data.

### 10. A failure that still waits

Owner of the first rule: `ProviderException` in `Library/IProvider.cs`.

```csharp
/// <summary>Whether the retry time is still ahead, so a request now would only repeat the wait.</summary>
internal bool IsWaiting => RetryAt > DateTimeOffset.UtcNow;
```

| Caller | After |
| --- | --- |
| `Browser.Error` (`Library/Browser.cs` 153) | `error is ProviderException { IsWaiting: true, RetryAt: { } retry }` |
| `Browser`, the command's can-execute (452, 453) | `!(fetch.Error is ProviderException { IsWaiting: true })` |
| The same command (457) | `&& !failure.IsWaiting` |
| `VideoLookup.Resume` (438) | `if (Failure is { IsWaiting: true }) return;` |

Owner of the second rule: `VideoLookup`, private.

```csharp
// When the hold behind a failure ends, if it has a time.
private static DateTimeOffset? RetryAt(Exception error) =>
    error switch
    {
        ProviderException provider => provider.RetryAt,
        RequestBlockedException blocked => blocked.Block.RetryAt,
        _ => null,
    };
```

| Caller | After |
| --- | --- |
| `VideoLookup.ServeBatch` (928 to 936) | `catch (Exception error) when (RetryAt(error) is { } retry)`; the local that computes `retry` goes. The branch on `error is ProviderException` stays. |
| `VideoLookup.ServeSweep` (1294 to 1301) | One block: `catch (Exception error) when (RetryAt(error) is { } retry) { sweep.RetryAt = retry; }` |

`VideoLookup.Refresh` 385, `_failure is { RetryAt: null }`, asks another
question, "has this failure no time at all", and stays.

### 11. The Subtitles host owns the page loop

`ISupplier.Search` (`Subtitles/ISupplier.cs` 87) reads one page:

```csharp
/// <summary>
/// Reads page <paramref name="page"/>, counted from 1, of the full SRT
/// subtitles in the query's language for the title the host chose, or else
/// for the file, by its name or hash, with what the supplier reports about
/// each. The host asks for each page, decides which subtitles fit the file,
/// and reads no further page after one that is listed under another title.
/// </summary>
Task<SubtitlePage> Search(
    SubtitleQuery query,
    int page,
    SupplierClient client,
    CancellationToken cancellation
);
```

In `Subtitles/Supplier.cs`, beside `SubtitleSearch`:

```csharp
/// <summary>One page of a supplier's answer, and how many pages the answer has.</summary>
internal sealed record SubtitlePage(IReadOnlyList<SubtitleCandidate> Candidates, long Pages)
{
    /// <summary>Whether the page lists no subtitle at all.</summary>
    internal bool Empty { get; init; }

    /// <summary>
    /// The title the supplier lists the page's subtitles under, when it
    /// matched the file itself to a title. The host checks that it is the
    /// file's, and that every page reports the same one.
    /// </summary>
    internal Listing? Title { get; init; }
}
```

`SubtitleCandidate.Title` (594) is deleted. Only SubDL sets it, with one value
for the whole page, and only `Supplier.Named` reads it. `SubtitleSearch` stays
as the host's whole answer; it is saved as JSON in `subtitle_searches`, and the
deleted property is internal, so the saved form does not change.

`Supplier.Search` (197) keeps its `Titles` step and replaces its `await
foreach`:

```csharp
var candidates = new List<SubtitleCandidate>();
var empty = true;
Listing? title = null;
for (var number = 1; ; number++)
{
    var page = await supplier.Search(query, number, client, cancellation).ConfigureAwait(false);
    // A supplier that matches the file to a title answers every page for
    // that title, so a page under another title contradicts the first.
    if (page.Pages < 0 || number > 1 && !Same(title, page.Title))
        throw new SubtitleException(SubtitleFailure.Format);
    title = page.Title;
    empty &= page.Empty;
    var named = Named(query, page.Title);
    if (named)
        candidates.AddRange(page.Candidates.Where(candidate => Fits(query, candidate)));
    // A title that is not the file's costs no further page.
    if (page.Candidates.Count > 0 && !named || number >= page.Pages)
        break;
}
return new(candidates, empty);
```

`Named(SubtitleQuery query, Listing? title)` keeps its three conditions and
reads the page's title. `Fits` loses its `Named` clause, because the loop
applies it to the page. `Same` is private and static in `Supplier`: both null,
or equal `Kind`, `Year`, `Season` and `Episode` and the same `Titles` in order.
A record compares its list by reference, so the record's own equality does not
serve.

What each supplier keeps and loses:

| Supplier | After |
| --- | --- |
| `OpenSubtitles.Search` (91) | No `for`, no `yield`, no `[EnumeratorCancellation]`. It builds the filter, adds `page` when it is above 1, reads the candidates and returns `new(candidates, pages) { Empty = subtitles.GetArrayLength() == 0 }`. A missing `total_pages` is still `Format` in the supplier. The `pages < 0` test goes to the host. |
| `SubSource.Search` (101) | The same shape; `pages` from `pagination.pages`. |
| `SubDL.Search` (38) | The local `title` and the comparison at 94 to 104 go to the host as `SubtitlePage.Title` and `Same`. A null `match` returns `new([], 1) { Empty = true }` on page 1 and throws `Format` on a later page. An unreadable title returns `new([], 1)` on page 1 and throws `Format` later. `currentPage` is still compared with `page`. Each candidate keeps `Season` and `Episode`. |

`using System.Runtime.CompilerServices;` goes from the three files.

| Candidate | Result |
| --- | --- |
| Keep `IAsyncEnumerable` and give the suppliers a shared helper that runs the loop | Rejected. The supplier still drives the loop, and the host consumes it with a second one. |
| Pass the first page back into the supplier, so SubDL keeps its comparison | Rejected. It adds a parameter that two of three suppliers ignore. |
| One page per call, the title at page level | **Chosen.** One loop exists. "Every page of one answer is listed under one title" becomes a host rule that holds for any supplier. |

### 12. The "forced" rule

Owner: `SubtitleFile` in `Subtitles/SubtitleFile.cs`.

```csharp
/// <summary>
/// Whether a file name, release name or description marks a forced-only
/// subtitle, which holds only the foreign dialogue.
/// </summary>
internal static bool IsForced(string label) => Forced().IsMatch(label);
```

`Forced()` (474) takes the pattern and options of `SubDL.ForcedLabel` (185):

```
(?:^|[\W_])(?:forced|foreign[ ._-]*(?:parts?(?:[ ._-]*only)?|only))(?:$|[\W_])
```

| Caller | After |
| --- | --- |
| `SubtitleFile.Identify` (74) | `if (names.Any(IsForced))` |
| `SubDL.IsForced(JsonElement)` (180) | `Text(value, field) is { } label && SubtitleFile.IsForced(label)` for each of the three fields. `ForcedLabel` (185 to 189) is deleted; the comment "SubDL has no field for forced subtitles, so their labels say it" stays. |

SubDL still reads the labels into the fact `Forced`, as
[the subtitle contract](../../contracts/subtitles.md#ownership-and-quiet-recovery) gives a supplier:
"reading its answers into facts, such as ... whether a subtitle is forced". A
second candidate, where SubDL reports the labels and the host decides, adds a
member to `SubtitleCandidate` for one supplier and was rejected for that.

### 13. A name that can be searched for

Owner: `Release` in `Shared/FileName.cs`.

```csharp
/// <summary>Whether a catalogue can be asked for the release: it is no sample or trailer and has a title.</summary>
internal bool IsSearchable => !IsExtra && Title.Length > 0;
```

"Unsearchable names" is the contract's own term
([providers](../../contracts/library/providers.md#identification-and-early-enrichment)).

| Caller | After |
| --- | --- |
| `Acquisition`, the lookup in `Subtitles/Acquisition/Acquire.cs` (569) | `if (!release.IsSearchable) return [];` |
| `Supplier.Candidates` (154 to 156) | The lines are deleted, with the local `release`; the query at 164 takes `FileName.Interpret(lookup.FileName)`. |
| `VideoLookup.CanMatch` (418) | `release.IsSearchable && option.Kinds.Contains(release.Kind) && (release.Season is null || release.Episodes.Count > 0)` |

The check in `Supplier.Candidates` goes because
[the subtitle contract](../../contracts/subtitles.md) says the rejection
happens "once before a request reaches a supplier". `Acquisition` is the only
caller of `Supplier.Candidates` and rejects before it reads a saved answer or a
counter, so its check is the one that takes effect.

`Imdb/Dataset.cs` 308 and 312, `Library/Store/Videos.cs` 152 and
`Subtitles/Acquisition/Storage.cs` 910 read `IsExtra` for other rules (rating
by file name needs a year and a movie; a file's eligibility) and stay.

### 14. `ISupplier.Available` goes

`ISupplier.Available` (`Subtitles/ISupplier.cs` 21) has one override,
`OpenSubtitles.Available => !string.IsNullOrWhiteSpace(BuiltInKey)`. It
restates a host rule that `ISupplier.BuiltInKey`'s own comment gives: "a
password supplier always sends it". The host derives it:

```csharp
// A password supplier sends the built-in key with every request, so it
// works only in a release that has one.
internal bool Available => Access != Access.Password || HasBuiltInKey;
```

The member leaves `ISupplier` and `OpenSubtitles`. `Supplier.Configured`,
`Supplier.Accepts`, `Supplier.Candidates` (152) and
`Shell/MainWindow/Subtitles.cs` 93 read `Supplier.Available` and do not change.

### Every listed caller, mapped

| Plan item | Member it calls after the stage |
| --- | --- |
| `OpenSubtitles.cs` 26, `SubDL.cs` 23, `SubSource.cs` 43, `Tmdb` 26, `Omdb` 26, `MdbList` 28 | `ApiKey.BuiltIn` |
| `Supplier.cs` 517, `Omdb` 235, `Tmdb` 323, `MdbList` 201, `Wikidata` 307 | `ProviderJson.Text` |
| `Library/ProviderJson.cs` 63, `Supplier.cs` 361, `Shared/DataDownload.cs` 37 | Stay, [decision 4](#4-the-reply-failure-switches-stay) |
| `Supplier.cs` 293, `OpenSubtitles.cs` 301, `SubDL.cs` 137 | `ProviderHttp.CanFollow` |
| `Browser/Information.cs` 160 | `VideoLookup.Requirement` |
| `MdbList` 66, `Omdb` 76, `Tmdb` 60, `Wikidata` 108 | `VideoLookup.Ask` |
| The title limit and the kind check | `VideoLookup.HasTitle`, `ProviderOption.Kinds` in `VideoLookup.Describe` |
| `MdbList` 148, `Omdb` 156, `Tmdb` 238, `Wikidata` 249, `Imdb/Dataset.cs` 439 | `Rating.From` |
| `Omdb` 32, `Wikidata` 35 | `FileName.TitleWords` |
| `Imdb/Dataset.cs` 455 | `FileName.TitleKey` |
| `Browser.cs` 153, 452; `VideoLookup.cs` 438 | `ProviderException.IsWaiting` |
| `VideoLookup.cs` 928, 1294 | `VideoLookup.RetryAt` |
| `OpenSubtitles.cs` 182, `SubDL.cs` 163, `SubSource.cs` 154 | The loop in `Supplier.Search`, through `ISupplier.Search(query, page, ...)` |
| `SubtitleFile.cs` 474, `SubDL.cs` 185 | `SubtitleFile.IsForced` |
| `Acquisition/Acquire.cs` 568, `Supplier.cs` 154 | `Release.IsSearchable` |

## Gate step 4: every consumer of every name

Searched: all of `app/src`, which includes the 25 capture journeys and the 16
files with `#if CAPTURE` blocks, all XAML, and `app/tests/*.ps1`.

No capture journey and no `#if CAPTURE` block names a type or member this
stage changes. The journeys reach suppliers and providers only through
`ShowSupplier`, `SelectSupplier` and the Settings pages
(`CaptureLibraryFiles.cs` 111, 470; `CaptureSettings.cs` 987 to 996), and
`CaptureUpdates.cs` 28 reads `ProviderHttp._client`, which stays. No XAML file
binds `Available` or `HasBuiltInKey`.

| Name | Consumers | Edit |
| --- | --- | --- |
| `Library.ProviderJson` (type), `Get` | `VideoLookup.Get` (`Identification.cs` 312); `app/tests/SourceChecks.ps1` 962, 974 | The script reads `$assembly.GetType('Syno.TinyTorrent.Library.VideoLookup').GetMethod('Send', $staticFlags)`. The arguments stay `($http, $call, $cancel, $null)`. |
| `ProviderOption.BuiltInKey`, `HasBuiltInKey` | `VideoLookup.Key` 231; `Browser/Information.cs` 163 (goes); `Shell/MainWindow/Library.cs` 280; `SourceChecks.ps1` 422 | None outside the three providers; the script sets a fixture key through the same `init` property. |
| `ISupplier.BuiltInKey` | `Supplier` 29, 100 | 29 only |
| `ISupplier.Available` | `Supplier` 27; `OpenSubtitles` 22 | Both, [decision 14](#14-isupplieravailable-goes) |
| `Supplier.Text`, `Supplier.Number` | The three suppliers, through `using static` | One added `using static` line each |
| `ISupplier.Search` | `Supplier.Search` 212; the three suppliers | [Decision 11](#11-the-subtitles-host-owns-the-page-loop). `SourceChecks.ps1` 1074, 1083 and 926 reach it only through `Supplier.Candidates` and `Acquisition.Candidates`, whose signatures stay. |
| `SubtitleCandidate.Title` | `SubDL` 158; `Supplier.Named` 412 | Both |
| `SubtitleSearch` | `Acquire.cs` 536 to 566; `Storage.cs` (saved answers); `SourceChecks.ps1` 1074 (`.Candidates`) | None |
| `Supplier.Send`, `Supplier+Operation`, `Supplier.Scope`, `Supplier.Candidates` | `SourceChecks.ps1` 932 to 992, 1073 to 1083 | None; names and parameters stay |
| `VideoLookup.Available` (private) | `Select` 131, `Refresh` 369, `Execute` 1143 | [Decision 8](#8-what-a-provider-still-needs) |
| `VideoLookup.Confidence`, `SearchRequest`, `Queries`, `VideoLookup+Context` | `SourceChecks.ps1` 86, 436 to 486 | None; signatures stay. The script's cases still hold: `Spider-Man` and its Wikidata form share the counter `spider man`, and the fallback of "Mission: Impossible - Rogue Nation" is still "Mission Impossible Rogue Nation". |
| `Omdb.Provider.Prepare` | `SourceChecks.ps1` 425 | None; the case gives the same text |
| `Imdb.Dataset.Key` | `Dataset.cs` 304, 804, 805; `Imdb/Provider.cs` 40, 53; `docs/contracts/imdb-datasets.md` 333 | All. `ImdbChecks.ps1` and `SourceChecks.ps1` 76 name `Imdb.Dataset` but not `Key`. |
| `Imdb.Dataset.ReadRating` | `Dataset.cs` 269, 356; `Imdb/Provider.cs` 109 | None; the signature stays |
| `Rating` (constructor) | `SourceChecks.ps1` 120 | None; the constructor stays public |
| `ProviderException.RetryAt` | `Browser.cs` 153, 452, 457; `VideoLookup.cs` 385, 438, 930, 1294; `SourceChecks.ps1` 1043 | [Decision 10](#10-a-failure-that-still-waits); the script reads the property, which stays |
| `FileName.Words`, `Normalize`, `SearchWords`, `Matches` | `SourceChecks.ps1` 160 to 162 and 14 product files | None; the members do not change |
| `SubtitleFile.Forced` (private) | `SubtitleFile.Identify` 74 | [Decision 12](#12-the-forced-rule). `SourceChecks.ps1` 1191 and 1210 check a `.forced.srt` file, which both patterns match. |
| `ProviderHttp.Transfer`'s address test | `ProviderChecks.ps1` 13 to 58, through `Send` | None; the transport rule does not change |

## Files the implementer may edit

Product code:

- `Shared/Network/ApiKey.cs` (new), `Shared/Network/ProviderJson.cs` (new),
  `Shared/Network/ProviderHttp.cs`
- `Shared/FileName.cs`
- `Library/IProvider.cs`, `Library/VideoLookup.cs`,
  `Library/VideoLookup/Identification.cs`,
  `Library/VideoLookup/Requests.cs` (moved from `Library/ProviderJson.cs`),
  `Library/Browser.cs`, `Library/Browser/Information.cs`
- `Library/Tmdb/Provider.cs`, `Library/Omdb/Provider.cs`,
  `Library/MdbList/Provider.cs`, `Library/Wikidata/Provider.cs`,
  `Library/Imdb/Provider.cs`, `Library/Imdb/Dataset.cs`
- `Subtitles/ISupplier.cs`, `Subtitles/Supplier.cs`, `Subtitles/SubtitleFile.cs`,
  `Subtitles/Suppliers/OpenSubtitles.cs`, `Subtitles/Suppliers/SubDL.cs`,
  `Subtitles/Suppliers/SubSource.cs`
- `Subtitles/Acquisition/Acquire.cs`, one line. Stage 10 works in this folder
  after stage 8.

Test scripts: `app/tests/SourceChecks.ps1`, two lines.

Documents: `docs/contracts/library/providers.md`, `docs/contracts/subtitles.md`,
`docs/contracts/subtitles/files.md`, `docs/contracts/imdb-datasets.md`,
`docs/contracts/architecture.md`.

No catalogue under `Resources` changes. A file that is not in this list is
edited only when the compiler names it, and the batch report says which.

Stage 9 also edits `Library/Imdb/Dataset.cs` and `Library/Browser/Information.cs`,
in other members (`Build`, `Install`, `Delete`, `Fetch`; `Download`,
`DeleteData`). Stage 10 also edits `Library/VideoLookup.cs` (`ServeBatch`,
`ServeSweep`). Stage 8 is implemented first, as the plan orders.

## Contract changes, as proposed text

**`docs/contracts/library/providers.md`, "Replaceable movie providers"**, in
place of the paragraph that starts "Library owns what every provider shares":

> Library owns what every provider shares. It chooses the key, the person's own
> or else the built-in one, and passes it to the option's `Create`. It decides
> what a provider still needs before it can answer, its data or a key
> (`VideoLookup.Requirement`). It sends a request through `ProviderGet`, which
> applies the saved network route and the search protection of the key and of
> the operation Library runs; a 404 for a detail means the video is missing.
> A provider reads its answer and lets a wrong shape throw: Library reports an
> answer the provider cannot read as `InvalidResult` for that one search or
> video. Library lists a choice only when its title has 1 to 300 characters,
> refuses a description without such a title, and asks a provider to describe
> only the kinds its option lists. A provider reports any other failure as a
> `VideoFailure`, and Library writes its text with the provider's name.
> `VideoInformation` carries `Related`, the other choices its page listed, and
> Library keeps them. The provider's text section holds its disclosure.
> Settings binds to the provider options, and MainWindow imports no provider
> implementation. Providers have no settings of their own: a provider that
> answers from downloaded data, such as IMDb, declares it on its option, and
> Settings offers the download, update and deletion.
>
> `Shared/Network` owns what the providers and the subtitle suppliers both
> need: `ApiKey.BuiltIn` reads a key the build wrote into the release,
> `ProviderJson` reads a value from an answer, where a missing or blank value
> is no value and text is trimmed, and `ProviderHttp.CanFollow` decides whether
> an address that a reply names may receive a request. A provider's own mark
> for a fact it lacks, such as OMDb's `N/A`, stays with that provider.

**The same file, the ruling "ratings and title facts belong to the title"**,
one sentence after "The sites stay separate, because a Rotten Tomatoes
percentage and an IMDb score measure different things.":

> A provider builds each rating with `Rating.From`, which scales the site's
> score to ten points and gives no rating for a score outside the site's scale.
> A score of 0 that a site states is a rating, such as a Rotten Tomatoes 0%;
> a provider's own mark for a score it lacks, such as MDBList's 0, is none.

**The same file, "Identification and early enrichment"**, after "Both searches
count toward the same per-title limit.":

> Titles compare by their words in lower case without accents or punctuation,
> and an apostrophe joins its word, so a file named Oceans.Eleven matches
> "Ocean's Eleven" (`FileName.TitleKey`). The second search, the per-title
> limit and the IMDb data's title keys use the same words.

**`docs/contracts/subtitles.md`**, in the paragraph that starts "The subtitle
module keeps scheduling", in place of "The supplier yields its answer page by
page, and the host requests no page after one whose subtitles all name another
title, so a wrong title costs one request.":

> The host asks the supplier for each page, up to the count the supplier
> reports, and requests no page after one that is listed under another title,
> so a wrong title costs one request. Every page of one answer is listed under
> one title; a page under another is an answer the supplier cannot read.

In the same paragraph, in place of "The release name is interpreted, and empty
titles and extras are rejected, once before a request reaches a supplier.":

> The release name is interpreted, and a name that cannot be searched for, an
> empty title or an extra, is rejected once, in Acquisition, before a saved
> answer is read or a request reaches a supplier (`Release.IsSearchable`). A
> password supplier is available only in a release with its built-in key; the
> host decides that from the supplier's `Access`.

**`docs/contracts/subtitles/files.md`**, in place of "A `forced` token marks a
forced-only file, which gets no record and does not stop a full subtitle from
being downloaded.":

> A label that says "forced", or "foreign" with "parts" or "only", marks a
> forced-only file, which gets no record and does not stop a full subtitle from
> being downloaded. `SubtitleFile.IsForced` owns the rule, and SubDL's labels
> are read with it.

**`docs/contracts/imdb-datasets.md`** line 333: "`Dataset.Key` writes" becomes
"`FileName.TitleKey` writes". The rest of the paragraph stays.

**`docs/contracts/architecture.md`, "Ownership decisions"**, three new items.
They record the candidates rejected for a lasting reason:

> - Each feature translates a failed provider reply into its own failures:
>   `VideoLookup`, `Supplier` and `DataDownload` each hold one switch over
>   `ReplyException`. `ProviderHttp` reads the reply and records the hold once.
>   One shared mapper would take a table per feature, because the three
>   failure lists share no member.
> - A Library provider keeps `Search` and `Describe` as whole operations.
>   `VideoLookup` wraps each call into a provider and reports a wrong answer
>   shape as `InvalidResult`. Splitting a provider into request builders and
>   answer readers, so the host could run each reader, would need a multi-step
>   protocol for TMDB's two-request search and its per-episode records.
> - A plugin declares its built-in key by one call of `ApiKey.BuiltIn` with
>   the key's name. The hosts do not read keys by name, because
>   `ProviderOption.BuiltInKey` is read where no host object is present, and a
>   central list of keys would change with every plugin.

The existing item for `Supplier` in the same list gains "and the page loop"
after "redirects, subtitle decoding".

`CONTEXT.md` needs no change. The stage adds no product term.

## What changes for a person

The batch report names each of these. Each is a "Settled as" choice of the
plan or a decision of this gate.

1. **Titles with an apostrophe match** (settled: "An apostrophe never
   separates title words"). A file named Oceans.Eleven.2001 is identified
   automatically as "Ocean's Eleven": today the title words `ocean`, `s`,
   `eleven` against `oceans`, `eleven` give a similarity of 0.4, under the 0.8
   the contract requires. The same holds for a subtitle title listing. A query
   that holds an apostrophe reaches OMDb and Wikidata as "Oceans Eleven" in
   place of "Ocean s Eleven". For such a query the saved search answer and the
   per-title count are under a new key, so the title is asked once more.
2. **TMDB search results without a title are not listed**, and no provider's
   result with a title over 300 characters is. TMDB results had no check.
3. **Text from TMDB and from the suppliers is trimmed**, and blank text is no
   value (settled). A subtitle with a blank release name is skipped.
4. **A local subtitle file labelled "foreign parts only" is forced-only**
   (settled: the wider pattern). It gets no record and does not satisfy its
   language.
5. **A provider whose saved key is blank asks for a key** when the person
   turns automatic downloads on. Today the switch saves, and the save fails
   with "provider unavailable".
6. **An unreadable answer is one video's failure wherever the provider reads
   it**, not only inside the eight catch blocks. An IMDb data row without a
   year, for example, fails that one title in place of ending the batch.

Not visible: a page whose title is unusable is saved before the host refuses
it ([decision 6](#6-the-library-host)); a query with a superscript or fraction
digit, such as `Alien³`, reaches OMDb without that character.

Nothing changes for ratings: the three providers treat a score of 0 as before.

## Left for separate work

`Library/Store/Videos.cs` finds a saved choice by `FileName.Normalize(title)`
in the indexed columns of `video_choices`, so "Ocean's Eleven" and
Oceans.Eleven do not meet there and the provider is searched once more. The
target is the stage's one title key, `FileName.TitleKey`, in those columns. It
needs a migration from database revision 9 that rewrites the stored keys. The
session that implements this stage puts this text in its report, so the lead
can record it as an issue.

## Done when

- A search of `app/src` for `GetCustomAttributes<AssemblyMetadataAttribute>`
  finds `Shared/Network/ApiKey.cs` and `Shared/Engine/EngineProcess.cs` only.
- A search of `Library` and `Subtitles` for `ValueKind == JsonValueKind.String`
  finds nothing outside `SubSource.Search`'s read of its `releaseInfo` array.
- A search of the five provider folders for `KeyNotFoundException`, for
  `<= 300`, `> 300` and for `new Rating(` finds nothing.
- A search of `app/src` for `Dataset.Key`, `TitleWords(` outside `FileName.cs`
  and its callers in this record, `IAsyncEnumerable<SubtitleSearch>`,
  `ForcedLabel`, `ProviderJson.Get` and `supplier.Available` finds nothing.
- A search of `Library` for `RetryAt > DateTimeOffset.UtcNow` finds
  `ProviderException.IsWaiting` only.
- A search of `Subtitles` for `IsDefaultPort` and `Port != 443` finds nothing.
- The contract texts above are in their contracts.

## Cost

- **Agent work:** 6 to 8 hours for the implementing session. It edits 21
  product files; the four JSON providers take most of the time, because each
  read changes to a nullable value. 1.5 hours for the independent review of
  the diff.
- **Builds:** none of its own. The stage joins batch C's one app build with
  `-Capture`. No capture journey names a changed member, so the capture build
  only confirms that.
- **Checks on that build:** `app/tests/SourceChecks.ps1`, whose subject
  changes in the supplier search, the reply table, the search counter and the
  forced file; `app/tests/ImdbChecks.ps1`, because the IMDb title key and
  rating read move; `app/tests/ProviderChecks.ps1`, because `Transfer`'s
  address test is rewritten. No capture review and no app launch: the stage
  changes no layout and no text.

## What reading could not settle

- **What MDBList writes for a score it does not know.** The repository holds
  no saved MDBList answer, and this gate sends no request. The decision keeps
  today's reading of 0 as "no score". One saved MDBList answer for a movie
  without a Rotten Tomatoes score settles it: if the score there is `null`,
  the `value > 0` test in `MdbList.ReadRating` can become `>= 0`.
- **Whether OMDb and Wikidata find "Ocean's Eleven" for the query "Oceans
  Eleven".** The plan settles that an apostrophe joins its word, and a file
  name already sends that spelling. No request was sent to confirm it.
- **That no check reads a provider's answer.** The plan's batch C names
  "Provider and IMDb checks for the changed paths, from saved pages".
  `app/tests/ProviderChecks.ps1` checks the proxy route only, and
  `SourceChecks.ps1` gives OMDb one error body. The readers of TMDB, MDBList
  and Wikidata, and OMDb's reader of a movie, have no check, so their edits
  rest on the compiler and on the review of the diff.
- **How many sites the nullable `Text` touches.** About 60 across the four
  JSON providers, counted by reading. The compiler gives the exact list.
- **That `SubtitleFile`'s wider pattern marks no full subtitle as forced.**
  The pattern is the one SubDL's labels pass today. It was not run against a
  list of real file names.

## Implementation notes

Written on 12 November 2026 by the implementing session, by reading the tree
after commit 5f8d2f6 with stages 4 to 7 uncommitted. Nothing was built or run.

### Where the record's references are today

Every member the record names still exists under its name. Only these line
numbers moved; all other references in the record are exact today.

| Record reference | Today |
| --- | --- |
| `FileName.Normalize` (`FileName.cs` 85) | 96 |
| `Imdb.Dataset.Key` (455) and its uses (304, 804, 805) | 448; uses 305, 774, 775 (stage 7) |
| `Imdb.Dataset.ReadRating` (439) | 432 |
| `Browser.ChooseAutomaticDownload` (`Information.cs` 160 to 165) | 166 to 171 |
| `Browser.Error` (`Browser.cs` 153); the command (452, 453, 457) | 154; 451, 452, 456 |
| `SubDL.Search`, the download address test (137 to 143) | 138 to 145 |
| `SubtitleFile.Identify`, the forced test (74); `SubtitleFile.Forced` (474) | 66; 460 to 464 |
| `MainWindow.ShowKey` (`Shell/MainWindow/Library.cs` 280) | 255; not edited |
| `docs/contracts/imdb-datasets.md` 333 | 334 |

Stage 1's `Release.Kind` is in place (`FileName.cs` 221), and `CanMatch`,
`Matches` and `Supplier.Agrees` already read it. Stage 2's `IFailure`,
`DownloadException` and `DataDownload.Failure` are as the record's table of
decision 4 shows.

### Differences from the record

1. **`VideoLookup.Failure(ReplyException)` is renamed `Translate`.**
   `VideoLookup` already has the property `Failure` (`VideoLookup.cs` 40). A
   method of the same name in the part file `Requests.cs` does not compile
   (CS0102). `Translate` is the record's own verb in decision 4. `Supplier.Failure`
   and `DataDownload.Failure` keep their names.
2. **The move of `Library/ProviderJson.cs` is a new file and a delete, not
   `git mv`.** `git mv` stages the rename, and this work stages nothing. The
   session writes `Library/VideoLookup/Requests.cs` and deletes
   `Library/ProviderJson.cs`. Git finds the rename from the content when the
   lead stages both. The delete removes a committed file, so the session asks
   for it in its report before it runs.
3. **`Dataset` calls `Shared.FileName.TitleKey`.** `Dataset` has a private
   constant `FileName` (`Dataset.cs` 25), which hides the type inside the
   class. This is why `Dataset.Key` qualifies `Normalize` today. The three
   calls in `Dataset.cs` are qualified; `Imdb/Provider.cs` writes
   `FileName.TitleKey`.
4. **`imdb-datasets.md` names `Dataset.Key` four times, not once:** the two
   schema comments (307, 308), the paragraph (334) and the rating-by-name
   rule (573). All four name `FileName.TitleKey`, because a contract that
   names a deleted member misleads.
5. **`SubtitlePage` becomes `SubtitleSearch.Reply`, a record nested in
   `SubtitleSearch`.** [CONTEXT.md](../../../CONTEXT.md) defines **Page** as a
   whole view of the product window, and every other `…Page` type in the app
   is one (`SettingsPage`, `ConnectionPage`, `AboutPage`, `CreditsPage`). A
   provider's answer is already called a reply (`ReplyException`,
   `ProviderReply`), so the lead chose `Reply`: one reply of a supplier's
   search. Members, the file and the behavior stay as the record designs them.
   `SubtitleSearch` keeps its saved JSON form, because a nested type adds no
   property.
6. **`Enums.cs` gets one comment edit.** `ProviderRequirement` says "What the
   person must supply before a Library provider downloads automatically". After
   decision 8, `VideoLookup.Execute` refuses every lookup with it, so the
   comment becomes "What the person must supply before a Library provider can
   answer." `Enums.cs` is outside the record's file list.
7. **A TMDB series without a name costs its episode requests.** The record says
   that no further request is sent for a refused description. That holds for
   OMDb, MDBList and Wikidata, which read one record. `Tmdb.Describe` reads the
   series record and then one record for each episode, and only then does the
   host refuse the empty title. Today `Tmdb.Information` refuses it inside the
   first download. TMDB lists every series with a name, so the session accepts
   this and names it in the batch report instead of adding a mechanism.

Smaller wording choices, the same behavior as the record:

- `Execute` drops its local `available` as well as the lock:
  `if (Requirement(context.Option) != ProviderRequirement.None) throw …`.
- The host's page loop gets parentheses around each `&&` inside an `||`.
- `SearchOne` applies the title filter at both calls of
  `context.Provider.Search`, as the record writes; no helper is added.
- The architecture's `Supplier` item reads "redirects, subtitle decoding, the
  page loop and which subtitles fit a file", so the list stays one sentence.
- Comments that the change makes wrong are corrected: the summary of the old
  `ProviderJson` type becomes the comment of `VideoLookup.Send`;
  `SubtitleSearch` loses "or one page of it"; `Supplier.Named` speaks of a
  page's title; `Supplier`'s summary names the page loop; SubDL's comment on
  its first page keeps only what SubDL still decides.

Consequences that need no decision, for the batch report:

- `Rating.From` computes `score / maximum * 10`. MDBList computes `value / 10`
  and TMDB and the IMDb data store `value` today, so a few values differ in the
  last bit (59 / 100 × 10 is 5.8999999999999995). `Browser.Score` rounds to one
  decimal, a whole number or a whole percent, so nothing visible changes.
  OMDb and Wikidata already compute this way.
- A TMDB choice without an original title saves NULL in place of an empty
  text. `Store.Choices` compares `original_name` with a non-empty name, so it
  finds the same choices.
- Blank text is no value also in OpenSubtitles' sign-in: a blank `base_url`
  keeps the default address, where today sign-in fails as `Format`. A blank
  `file_name` shows the release name as the label.

### Order of edits

1. The base: `Shared/Network/ApiKey.cs` and `Shared/Network/ProviderJson.cs`
   (new), `ProviderHttp` (`IsProtected`, `CanFollow`), `FileName`
   (`TitleWords`, `TitleKey`, `Release.IsSearchable`).
2. Library's contract types: `IProvider.cs` (`HasBuiltInKey`, `Rating.From`,
   `ProviderException.IsWaiting`) and the comment in `Enums.cs`.
3. The Library host: `VideoLookup/Requests.cs` from `Library/ProviderJson.cs`;
   `Identification.cs` (`Ask`, `HasTitle`, `SearchOne`, `Describe`, `Read`,
   `Queries`, `SearchRequest`, `CanMatch`, `Confidence`); `VideoLookup.cs`
   (`Requirement`, `RetryAt`, `Select`, `Refresh`, `Execute`, `Resume`,
   `ServeBatch`, `ServeSweep`); `Browser.cs`; `Browser/Information.cs`.
4. The providers: TMDB, OMDb, MDBList, Wikidata, then `Imdb/Provider.cs` and
   `Imdb/Dataset.cs`.
5. The Subtitles host: `SubtitleFile`, `ISupplier`, `Supplier`, the three
   suppliers, then the one line in `Acquire.cs`.
6. `app/tests/SourceChecks.ps1` 962 and 974.
7. The contract texts.
8. The searches under [Done when](#done-when), the
   [focused review passes](../../agents/compilation.md#focused-review-passes),
   and a read of the whole diff of each file that also holds stage 4 to 7
   edits.

### Files the session edits

Product code, each in the record's list unless marked:

- `Shared/Network/ApiKey.cs` (new), `Shared/Network/ProviderJson.cs` (new),
  `Shared/Network/ProviderHttp.cs`, `Shared/FileName.cs`
- `Enums.cs`, one comment (difference 6)
- `Library/IProvider.cs`, `Library/VideoLookup.cs`,
  `Library/VideoLookup/Identification.cs`, `Library/VideoLookup/Requests.cs`
  (new), `Library/ProviderJson.cs` (deleted, difference 2),
  `Library/Browser.cs`, `Library/Browser/Information.cs`
- `Library/Tmdb/Provider.cs`, `Library/Omdb/Provider.cs`,
  `Library/MdbList/Provider.cs`, `Library/Wikidata/Provider.cs`,
  `Library/Imdb/Provider.cs`, `Library/Imdb/Dataset.cs`
- `Subtitles/ISupplier.cs`, `Subtitles/Supplier.cs`,
  `Subtitles/SubtitleFile.cs`, `Subtitles/Suppliers/OpenSubtitles.cs`,
  `Subtitles/Suppliers/SubDL.cs`, `Subtitles/Suppliers/SubSource.cs`,
  `Subtitles/Acquisition/Acquire.cs`

Test scripts: `app/tests/SourceChecks.ps1`, lines 962 and 974.

Documents: `docs/contracts/library/providers.md`, `docs/contracts/subtitles.md`,
`docs/contracts/subtitles/files.md`, `docs/contracts/imdb-datasets.md` (four
places), `docs/contracts/architecture.md`.

Five of these files hold uncommitted edits of stages 4 to 7:
`Library/Browser.cs`, `Library/Browser/Information.cs`, `Library/Imdb/Dataset.cs`,
`docs/contracts/architecture.md` and `docs/contracts/imdb-datasets.md`. Stage 8
changes other lines in each. The new `Ownership decisions` items go after the
stage 6 item about `DragDistance`.

[Stage 9's record](stage-09.md) names `VideoLookup.Available` (236) as a reader of
`Dataset.IsInstalled`. After this stage that reader is
`VideoLookup.Requirement`.

The text under [Left for separate work](#left-for-separate-work) goes into the
report of the session that makes the edits.

### Found while implementing

- `docs/contracts/subtitles.md` also said "each subtitle carries the title the
  supplier reports". After decision 11 the page carries it, so that sentence
  changes with the two the record lists.
- `IProvider`'s remarks gain one sentence: a provider lets a wrong shape throw
  and gives an empty title when the answer has none, and Library handles both.
  Each provider now writes `?? string.Empty` for a title, and this is the one
  place that says why.
- TMDB's undated search names its locals `found` and drops `choices`, because
  without the `try` block they would clash with the dated branch's locals
  (CS0136). OMDb's search reads the IMDb ID once with a pattern, as MDBList's
  search already does, so the nullable ID needs no `!`.
- The "Done when" search for `supplier.Available` finds
  `Shell/MainWindow/Subtitles.cs` 93. That line reads the host's derived
  `Supplier.Available`, which decision 14 keeps.
- `Store/Identification.cs` 480 compares the titles of two episode files in
  one folder with `Normalize`. It stays, for the reason the record gives for
  `VideoLookup.Match`: both spellings come from one source.

### Fixes after the independent review

The lead decided these four after the review of the first implementation.

1. **Elisions.** Joining every apostrophe broke dotted elisions:
   L.Auberge.Espagnole gives l, auberge, espagnole, and "L'Auberge espagnole"
   gave lauberge, espagnole. A title now agrees when its words agree in either
   form, the apostrophe joining its word or separating it.
   `FileName.TitleForms` gives both forms of the normalized words, and
   `FileName.TitleKeys` their keys; one private loop writes both.
   `VideoLookup.Confidence` takes the better similarity of the two forms, each
   form paired with the same form, and `Supplier.Matches` and `Within` accept
   either key. `FileName.TitleKey` keeps its code, because the installed IMDb
   data stores it; the queries, the per-title counter and the IMDb keys keep
   the joining form. The providers contract and the subtitle matching contract
   say both forms.
2. **A forced label in a title.** `SubtitleFile.Identify` tested the whole
   name of a subtitle that does not start with the video's file name, so
   Forced.Vengeance.en.srt read as forced-only. A name that starts with the
   video's file name is still tested after it. Any other name is tested after
   the video's title (`FileName.TrimTitle`: letters and digits without case,
   ending at a word boundary), or whole when it does not start with the title,
   so eng.forced.srt and Title.forced.srt stay forced. Trimming the title only
   from the other names keeps a subtitle of the film "Forced" named after its
   file and ending in .forced forced-only. Language recognition is unchanged.
   SubDL's labels keep their older weakness.
3. **What an unreadable answer fails.** For an online search, `SearchOne`
   backs off the provider's searches, as
   [search protection](../../contracts/search-protection.md#answers-holds-and-recovery)
   requires; a description, or a search of the IMDb data, fails one video. The
   providers contract and the comment on `VideoLookup.Ask` say so, and the
   comment names `SqliteDataReader` beside `JsonElement`.
4. **MDBList's score of 0.** The comment no longer states what MDBList writes
   for a score it lacks, which no saved answer shows. It names the contract's
   rule that the code applies.
