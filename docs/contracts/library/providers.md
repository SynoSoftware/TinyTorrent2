# Identification and providers

Part of the [Library contract](../library.md). Read its shared ownership,
membership and SQLite rules before the sections here that the change affects.

## Identification and early enrichment

Choose one provider: TMDB, Wikidata, OMDb, MDBList or IMDb.
Default provider selection is exclusive, so changing it cancels automatic
work belonging to the previous provider. Accepted manual work keeps its chosen
provider and continues. Each selected provider uses the same background
identification workflow; TMDB also supports series and episodes, and the others
support movies only.

The providers differ in what they return:

| Provider | Access | Information |
| --- | --- | --- |
| TMDB | Built-in key, or the person's own | Localized titles, synopsis, genres, cast, keywords, TMDB rating, directors, box office, and recommended titles that Fetch metadata offers as choices |
| Wikidata | No key | Localized titles, genres and cast, a one-line description as the synopsis, and the IMDb, Rotten Tomatoes and Metacritic scores that Wikidata editors last recorded |
| OMDb | Built-in key, or the person's own | English titles, full plot, genres, main cast, directors, age rating, box office, and IMDb, Rotten Tomatoes and Metacritic scores |
| MDBList | Built-in key, or the person's own | English titles, synopsis, genres, age rating, box office, and IMDb, TMDB, Rotten Tomatoes, Popcornmeter and Metacritic scores; no cast |
| IMDb | No key; the person first downloads IMDb's movie and TV data, about 290 MB | Movies and series with English titles, original titles, episode names, genres and IMDb rating, read from the [IMDb data](../imdb-datasets.md) on this computer; no synopsis or cast |

No provider reads web pages through an installed browser. The owner removed the
IMDb and Rotten Tomatoes providers that did, because a browser costs too much
memory and time for one score. Do not reopen browser experiments.

**Owner ruling: ratings and title facts belong to the title, not to the
provider that found them.** Library saves each site's score, the directors,
the age rating and the box office once per IMDb ID. A provider that names the
same IMDb ID adds to that record, so an IMDb rating from OMDb and one from
MDBList fill the same IMDb slot. The sites stay separate, because a Rotten
Tomatoes percentage and an IMDb score measure different things. A value the
provider lacks never removes a saved one. A site's rating is replaced unless
the saved rating has more votes: a site's vote count only grows, so this keeps
the newer rating, and an editor's old Wikidata score without a count does not
replace a current one. The larger box office stays, because TMDB and MDBList
report worldwide takings and OMDb only US takings, and a total only grows. A
video without an IMDb ID keeps its provider's own ratings and shows no title
facts; only its IMDb rating can come from the IMDb data, as the next paragraph
describes. Titles stay with each provider, because they differ in language. A
movie whose provider lacks a synopsis, cast or genres keeps the ones saved for
the same IMDb ID, so fetching from the IMDb data after TMDB keeps TMDB's
synopsis and cast.

**Owner ruling: downloaded IMDb data rates every video it can name, whatever
the provider.** A movie or episode whose provider returned its IMDb ID shows
the IMDb rating from that data, which is newer than a provider's saved rating.
TMDB returns no IMDb rating, so its movies get one this way. A video without
an IMDb ID, because no provider matched it, automatic downloads are off, or the
provider gave none, is found in the data by its file name, as
[rating by file name](../imdb-datasets.md#rating-by-file-name) describes. That
lookup gives only the rating; the video stays unidentified, and nothing is
saved. Both the table and Inspector display an available rating, even without
a full provider match. Stored information is read independently of automatic
downloading; changes to the selected row refresh the Inspector as well as its
table bindings.

Table rating cells show only the score. Neutral foreground prominence communicates
the sample size, independently of whether the score is high or low:

| Rating site | Small · Subdued (tertiary) | Moderate · Secondary | Large · Primary |
| --- | --- | --- | --- |
| IMDb, TMDB, Popcornmeter | Under 200 | 200–1,999 | 2,000+ |
| Rotten Tomatoes critics | Under 20 | 20–99 | 100+ |
| Metacritic critics | Under 10 | 10–39 | 40+ |

These are display thresholds, not statistical confidence or provider certification.
Unknown counts use secondary foreground; they are not treated as zero. Tooltips
and accessibility help give the exact count on the first line and Small,
Moderate or Large sample on the second. They distinguish critic reviews from
audience votes, or state that sample size is unavailable. The source owns the
sample thresholds once for the style and tooltip. Native theme brushes keep
the scores readable in Light, Dark and High Contrast without warning/error colors.
Inspector ratings retain their compact counts. The IMDb data, TMDB, OMDb and
MDBList report votes; Wikidata reports them only when an editor recorded the count.

Use names and structured clues such as title, year, season and episode numbers
without reading video contents.
If an online provider finds no candidates for a punctuated title, search once
more with punctuation separated into words, because some catalogues miss that
spelling. Both searches count toward the same per-title limit.

Associate automatically only when the available evidence supports an
unambiguous match with title similarity greater than 80%, consistent sequel
numbers and no conflicting known year. More than one qualifying candidate
remains ambiguous. A first search result is not sufficient evidence by itself.
Unmatched and ambiguous files remain usable under their original filenames,
with the type and year their names give
([identification](../../../CONTEXT.md)): a name with a season is a TV episode, and
every other video is a movie.
Provide Fetch metadata and Erase metadata actions without prompting
for every file. Erasing an incorrect association preserves the user's decision
instead of immediately recreating the same automatic match.

Manual decisions survive restart and take precedence over automatic work.
Fetch metadata and Erase metadata apply to all current source entries at the selected
location in one SQLite transaction, so cross-seeded files do not show competing
identifications. When locations meet, the most recently saved explicit choice,
including Clear, applies to all contributors. It remains with each if their
locations later separate. Resolve that effective choice once for search, rows
and details; removing one contributor must not undo a surviving entry's choice.
A late reply cannot overwrite a newer correction or recreate an entry removed
from its data source. A file's video information stays only while a current
entry uses it. Everything else a provider returned, its raw pages and every
movie its pages listed, stays saved, so a later lookup of the same movie needs
no request ([keep everything a provider returns](#saved-provider-data)).

With multiple movies selected, Fetch metadata asks only about the chosen
provider. Below the provider's name and the number of selected videos, it
groups them by that provider's last answer, as checkboxes with their counts in
one aligned column: Not checked yet, Not found last time and Already checked.
Every label and note takes one line. A request that did not complete counts as not checked. Only Not
checked yet starts checked, and it respects local protection; Not found last
time asks again, and Already checked refreshes the provider's answer. An empty
group is disabled. Another provider's match never moves a video between groups.
A confident answer for a video that another provider identified adds this
provider's ratings and title facts when both name the same IMDb ID, and keeps
the identification; another title is no answer. The dialog says that
information from other sources is kept, and counts the selected videos the
provider cannot check: extras and other unsearchable names, videos whose
information the person cleared, and videos whose match has no IMDb ID to compare.
The [search protection contract](../search-protection.md) defines recovery and reset.
It works in the background: the status bar shows "Metadata · 3 of 7 processed"
until it finishes, on every page, with an X that stops it and cancels the
request in progress. Fetching more files while it runs adds them to the same
count, and a file already waiting is not added twice. Automatic lookups wait
until the batch ends. Timed provider holds release the execution gate while
accepted work waits. Other failures are reported like other Library commands.
Retries preserve the accepted scope and freshness cutoff: an All request still
refreshes existing information after a wait. Permission to override local
protection ends when its attempt fails; retaining the requested work does not
grant that permission again. Replacing the selected provider clears the old
provider's displayed failure, while its saved search protection stays with it.
Library retains completed remote query answers across sessions, so equivalent
files reuse an answer without another request.
Library retains the complete selection across result refreshes and remembers
it separately for each configuration.

The Subtitles column reads the [subtitle inventory](../subtitles/files.md#subtitle-inventory-and-lookup-counts)
from SQLite: one language is shown by name and multiple languages by code.
Browsing Library never inspects folders.

Share movie and series information across associated files rather than fetching
and storing a separate copy per file. Bound background work, honor provider
throttling, and prioritize explicit identification over routine enrichment.
Try a displayed, unidentified video once per session, as the
[no-scan ruling](../library.md#purpose-and-experience) requires, with the persistent per-provider
search allowance limiting requests across restarts. **Owner ruling: a
provider's no-match answer is remembered.** Background lookups never ask
the same remote question again until the inputs change or the person requests
Force fetch or resets search protection. An individual Fetch shows its local
block before offering Force fetch. Remote waits cannot be forced.
The table shows an em dash in that file's empty provider cells, so a file the
provider does not have differs from one not looked up yet. A lookup that the
remembered answer holds back, because the provider had no match or the named
title was missing, shows the dash too, so the table and Fetch metadata agree
on what the provider does not have. Provider failure must not
block local search, torrent operations or shutdown.

### Replaceable movie providers

Optional providers are removable, and a new provider uses the same Library
workflow. Providers are composed at compile time in the existing app, because
no runtime plugin installation or discovery is required. Runtime loaders, new
projects, pluggable databases and provider-specific schedulers add no required
behavior.

A provider owns only what differs between providers: how it builds its requests
and reads their answers. It knows neither current torrent membership nor
whether an answer may be saved, and it receives no `Store`, search protection
or network route.

```csharp
internal interface IProvider
{
    string Prepare(string query) => query.Trim();
    Task<VideoSearch> Search(VideoQuery query, CancellationToken cancellation);
    Task<VideoInformation> Describe(
        VideoChoice choice, VideoRequest request, CancellationToken cancellation);
}
```

Each provider folder declares its own `ProviderOption`: the provider ID, which
also names its catalogue and its text section, the name, the policy links,
`Access`, `BuiltInKey`, `Kinds`, `IsLocalized` and, for a provider that answers
from downloaded data, `Dataset`. The provider ID persists the selection and
qualifies stored information, such as `imdb` or `tmdb`. It is an
implementation-owned stable string, because removing a provider must not
require changing central enum ordinals. A saved provider ID that no option
matches durably turns downloading off with no selected provider, rather than
refusing to start; silently falling back to another provider would change the
person's disclosed network behavior.

Library owns what every provider shares. It chooses the key, the person's own
or else the built-in one, and passes it to the option's `Create`. It decides
whether a provider is available. It sends a request through `ProviderGet`,
which applies the saved network route and the search protection of the key and
of the operation Library runs; a 404 for a detail means the video is missing. A
provider reports a failure as a `VideoFailure`, and Library writes its text
with the provider's name. `VideoInformation` carries `Related`, the other
choices its page listed, and Library keeps them. The provider's text section
holds its disclosure. Settings binds to the provider options, and MainWindow
imports no provider implementation. Providers have no settings of their own: a
provider that answers from downloaded data, such as IMDb, declares it on its
option, and Settings offers the download, update and deletion.

`VideoKind` names a movie, a series or an episode, and Library stores the
existing words for them. `VideoInformation` carries facts: genres, cast and
keywords are lists, and an episode carries its series title and each episode's
number, name and synopsis. Library composes the stored title, such as
`Show · S01E01 Pilot`, and the joined text its search and filters read.

Feature construction is the only registration location. Adding an independent
provider adds its folder, its text section and its option to that list.
Removing an online provider deletes its folder, list entry, resources and
dedicated checks; shared lookup, storage, settings, details and dialog code
need no edits. IMDb is the exception: its data also gives every provider's
movies their IMDb rating, so `Store` and the Library view know `Imdb.Dataset`,
and removing IMDb changes them too.

#### Identification order

Library owns automatic identification, and SQLite answers before the provider.
It first reads the saved choices of the provider's catalogue whose normalized
title or original title equals the release's title, through an index on each.
One saved choice of the release's kind and year is described without a search.
Otherwise it searches the provider for the release's title, keeps choices of
the release's kind whose title or original title and year agree, and describes
the one remaining choice. When the search keeps none, one saved choice still
answers before the lookup reports no match. A saved choice decides alone only
with the year, because without it the provider's search could list a second
movie of the same title that no page has shown yet. The description must agree
with that choice, because a search result can lack its year or carry a looser
title. A provider whose `Kinds` lack the release's kind is not asked. The
series hint comes from Library's current, accepted sibling decisions, and
Library describes it directly. Library describes a series choice only for a
file whose name gives a season, and refuses any other as `EpisodeRequired`. No
unique match ends the attempt as a miss, so restarting does not repeat the
lookup. `Describe` returns information or a failure; it does not return null.

An automatic search runs once a session, and the files of one movie reuse its
result. Fetch metadata's dialog always searches again, because the person
repeats a search after an empty or failed answer. It lists the saved choices of
the typed title with the search results, and only those when the provider
fails.

#### Lookup execution and failures

VideoLookup has one guarded execution and commit path for background work, the
batch, Fetch, Refresh and explicit identification. It captures the provider,
catalogue, language and current entry evidence, serializes requests, reuses
matching normalized information unless refreshing, and commits only through
Library's current-membership and latest-decision guards. A provider change,
Disable, a connection change, a language change and a network route change
cancel that context; a route change also clears failures without a retry time.

VideoLookup also owns Fetch metadata's batch: one queue that identifies its
files in order on a worker thread. The background sweep finishes the file it is
reading and then waits until the batch ends, so no page read is wasted and
automatic lookups do not start and cancel between batch files. A batch file
follows the automatic policy, except that it retries an earlier miss: its own
failure is a miss and its match is automatic. The Library view only adds files,
stops the batch and shows its progress.

VideoLookup reports a stored match or miss separately from state changes such
as the provider, its failure or the batch. Saved data can answer many files a
second, so the view refreshes its rows at once and then at most once a second
while such results arrive. Visibility and dwell remain with Library Browser,
and providers do not subscribe to the window.

Expected provider failures carry a `VideoFailure`. A failure of one video, such
as an unreadable page or record, uses `VideoException`; it ends an automatic or
batch lookup as a miss and an explicit one with its error. A provider that
cannot serve any request uses `ProviderException` with an optional retry time,
so one bad page cannot suspend unrelated enrichment. Cancellation stays
`OperationCanceledException`, and storage errors stay with the existing failure
owner.

The background sweep takes each video file on screen that has no saved
decision, no attempt in this session and no remembered no-match answer from
the provider under the file's current name. A file that scrolls away before
its turn waits until it shows again. Opening the database returns earlier failures,
search-limit stops and unusable pages to the sweep; only the provider's own
no-match answer is permanent. Automatic and batch lookups search the provider
only within the shared [search limit](../architecture.md#third-party-provider-access),
which [search protection](../search-protection.md) counts. The sweep starts only
after the delay that observing files creates for it and for the subtitle
worker.

A miss permits background work on the next eligible file. A provider failure
with a retry time suspends background work until that time; without one, it
suspends automatic work until explicit Retry or a relevant configuration
change. This prevents a blocked provider receiving the same request for every
file. An on-demand failure ends the attempt without scheduling another request.
A later explicit request before a retry deadline reports the remaining wait
without contacting the provider. Library owns these decisions and cancellable
waits; adapters own no retry loop. The shared HTTP path reads each reply and
records its hold once, as
[search protection](../search-protection.md#answers-holds-and-recovery) lists,
and a failure's retry time is that hold's. The full retry deadline is kept and
each cancellable worker wait is bounded, because a valid long provider delay
can exceed the platform timer's supported duration.

VideoLookup cancels and drains background and interactive requests before the
application disposes storage or shared HTTP access. Library Browser cancels its
dwell and selection requests. Each adapter observes the supplied cancellation.
The adapters need no separate provider lifetime interface, because resources
are shared or scoped to a call.

#### Saved provider data

Normalized videos, identification associations and attempts carry catalogue
identity. Raw records use catalogue, record key and language; providers
interpret their content. Library keeps a raw record as soon as the provider
returns it, also when the lookup rejects the video, and saves its references
atomically with the accepted `VideoInformation` response.

**Owner ruling, 2026-10-11: keep everything a provider returns.** Raw records
and saved choices stay after their files leave the Library, because reading a
page again costs seconds and risks the website refusing the person's address.
Library saves every search result, every related choice and every described
video as a saved choice, with title, original title, year and kind; a later
listing of the same choice keeps a known year that the new one lacks. Library
supplies the language: it saves information from a provider that is not
`IsLocalized`, such as OMDb, as valid for every language. Cached normalized
facts remain readable after a provider is removed. Episode information records
its reusable series choice explicitly, so the live shared Series query does not
decode TMDB's `tv/...` identifier. That query retains its contribution,
directory, evidence and conflict checks and restricts the hint to the captured
catalogue.

### TMDB early and background enrichment

With TMDB selected for automatic downloads, a video is identified when the
person displays it, as the [no-scan ruling](../library.md#purpose-and-experience)
requires; video completion is not a prerequisite. Process only recognizable
video candidates for movie/TV identification; other files retain ordinary
filename search without provider requests. Reuse common series identification
for a season pack and fetch episode-specific information only for represented
episodes, rather than importing an entire series catalogue. The identification
request also returns the searchable cast and synopsis, so an actor or subject
search finds every file identified earlier, not only the one whose details pane
is open. Do not retrieve artwork, trailers or unrelated credits as part of it.
Recommendations
arrive in the same request through `append_to_response`, so they add no
request; Fetch metadata offers them as choices.

Automatic downloads run in C# only while the product window is open and the
setting is on, within the [per-movie limit](../architecture.md#third-party-provider-access).
Closing the window cancels optional work; the next window tries the files
without a match again. There is no hidden managed process or native fallback. Start with one provider request in flight; concurrency increases only if measured
enrichment delay warrants it without harming interactive work. A provider
request must not occupy the worker that serves interactive Library queries.
Save completed provider information once per coherent result, not as one durable
write per cast member or field. Background work waits when idle; it does not
periodically revisit the whole collection looking for something to refresh.

### Subtitle independence

Movie identification and [subtitle matching](../subtitles.md) are distinct C#
responsibilities. Identifying a title does not establish that a subtitle matches
a release. Share applicable identifiers through their C# owners without a native
provider API. Subtitle work never enables or waits for Library enrichment, and
a movie match never establishes timing compatibility.

## Privacy and provider release requirements

Normal browsing and typing stay local. Automatic video information downloads
start Off. The Download automatically switch starts them; choosing a provider or
editing its credentials leaves that switch unchanged. There is no confirmation
dialog and no per-file consent, because the choice is already the person's
decision, as with Windows' own online settings. Choosing another provider in
Fetch metadata is the person's explicit request for that one file and needs no
further consent. Choosing Off stops further
automatic provider work and cancels its pending requests where possible. Manual
Fetch and Refresh remain available, and accepted manual work continues. Cached information
remains usable only as permitted by the applicable terms; source entries and
filename search remain usable regardless.

Settings presents Provider and Download automatically as separate rows in the
Video information group, like supplier access and automatic subtitles. Provider
selection, credentials and dataset management remain available in both switch
states; Off stops only automatic work and shows no disabled-feature notice.
The provider's information button opens its disclosure, the
inputs it receives, and links to its privacy policy and terms, in the same
flyout as the subtitle supplier's. For a provider that takes an API key, a key
button beside it opens the [account dialog](../architecture.md#third-party-provider-access),
where the person enters their own key or, when the release has one, keeps the
built-in key. Saving credentials preserves automatic download intent. Turning
automatic downloads on with no usable key opens that dialog: Save stores the key
and starts automatic downloads; Cancel leaves them off. Turning them on for IMDb
before its data is installed asks to download it. Download installs the data and
starts automatic downloads when it finishes, unless the person chose another
setting meanwhile; Cancel leaves them off. Downloading or updating the data from
its own row leaves the switch unchanged. The IMDb data row shows the data's size and date and
offers Download or Update and Delete data, whichever provider is chosen,
because every provider's movies use its ratings. While the data downloads, the
row shows its progress and a button that stops it. Providers can be selected and
configured while unavailable. Requests still need the provider's data or key,
while saved information and other providers remain usable. Library's Settings
button opens the provider row; Settings search
has separate destinations for Provider and Download automatically.

Send only the matching inputs a request needs. Do not upload a Library
inventory, absolute paths, torrent info hashes, trackers, peers or movie bytes.
Use HTTPS and keep credentials and search contents out of routine logs. State
the actual network route before starting automatic downloads. The shared
[network-route rule](../architecture.md#network-route) governs every provider;
disclosure never permits a direct fallback. A provider that cannot honor the
configured route remains unavailable.

Follow [third-party provider access](../architecture.md#third-party-provider-access).
TMDB, OMDb and MDBList use the free application keys supplied with the
build; the person does not need an account or key. A person who wants their own
allowance enters their own key, which replaces the built-in one for that
provider. No paid access is part of this feature. Without a key, local Library
search and saved information remain usable, but no requests to that provider
run. Provider limits use the existing wait and retry behavior rather than a
paid fallback; a refused key stops that provider's lookups until the person
changes the key. Wikidata needs no key. IMDb needs no key; it is available
while its downloaded data is on this computer, and its lookups send no
requests.

Verify applicable API/content permission, attribution, credential distribution,
quotas, retention and cache-use requirements. Do not substitute an arbitrary
180-day expiry for verified terms. TMDB's [published FAQ](https://developer.themoviedb.org/docs/faq)
describes attribution and commercial/noncommercial access; the applicable
agreement must cover the shipped integration. The attribution goes under
Credits on the About page. It adds no text or movie artwork to the Library
page.

The repository's MPL 2.0 code license is distinct from permission to use provider
content. No copyright-ownership checkbox, per-file legal confirmation or
unrelated content restriction is added without a concrete applicable
requirement. Provider release work does not prevent implementing or using the
local torrent-backed search. This contract is not a certification of legality.
