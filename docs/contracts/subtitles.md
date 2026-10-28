# Automatic subtitles

This is the contract for automatic subtitles. The Plugins settings page keeps
the Library card unchanged, groups the subtitle settings, shows SQLite
coverage, and keeps batch scope and matching separate from automatic
downloads; the [Settings handoff](../../app/prototypes/settings-options-prototype.md#subtitles)
holds the presentation. Existing ownership and recovery rules come from
[architecture](architecture.md), [engine](engine.md), and
[pipe API](pipe-api.md). Subtitle acquisition has no native part: the engine
has no acquisition, early-piece or companion-file code.

## Read for the change

Read the outcome, ownership and recovery rules here for subtitle changes.
Then follow the affected branches below. These references are parts of this
contract with the same authority.

| Change | Reference |
| --- | --- |
| Settings, languages, automatic intent, batch actions or access checks | [Configuration](subtitles/configuration.md) |
| Lookup timing, hash readiness, ranking, extra matches or the choice dialog | [Matching](subtitles/matching.md) |
| Subtitle records, inventory, local scans, saving, publication, moves, renames or removal | [Files](subtitles/files.md) |
| Supplier endpoints, credentials, quota facts, disclosures or privacy | [Suppliers](subtitles/suppliers.md), together with the matching and file rules for those calls |

## Outcome

**Owner ruling: subtitle matching, downloading, settings and SQLite live entirely
in C#, alongside Library in the product window process.** No supplier operation,
credential, subtitle job or database command crosses into C++. Closing the window
pauses this work and releases its workers; reopening reconciles current torrent
facts before resuming saved work. There is no hidden managed process or native
fallback. C# creates, migrates, populates and queries SQLite, downloads and
publishes subtitles, and manages the subtitle files it created. The engine gains
no feature code, piece requests, sidecar rules or new feature API. This replaces
the earlier closed-window acquisition and native companion-file requirements,
keeping the resident engine focused on torrent work.

**Owner ruling: no Library or subtitle feature is implemented in C++.** C#
creates and migrates SQLite, constructs the current projection, performs every
query and write, downloads TMDB information and subtitles, and manages created
subtitle files. The existing engine pipe supplies ordinary torrent/file facts
only. There is no exception for a small native helper, generic byte interface,
subtitle-aware payload operation or feature-specific invalidation cache. The
pipe carries no subtitle-settings, supplier Check, Find, Recheck, credential,
provider-search, job-status or database messages: views call their C# owners
directly, and SQLite is not linked into the engine. Lookups use filename and
release evidence and a movie hash computed in C#; no engine piece demand or
early-hash interface exists.

**Owner ruling (2026-10-28): subtitles download automatically only when a
torrent finishes downloading, and only for that torrent.** Every other
subtitle search or download happens because the person asked for it. An
automatic search in any other way floods the suppliers' servers with requests
and delays the app in a way the person did not ask for. So no automatic
request is sent while a torrent downloads, opening the window starts no search
of its own, a torrent whose completion search was already decided is never
searched again automatically, and TinyTorrent never walks the library's folders
on its own. It checks for subtitle files only in the folders of a torrent that
just finished and of the movies Library displays, as the
[no-scan ruling](library.md#purpose-and-experience) allows. When a subtitle
does not exist, the person selects the movies in Library and chooses Download
subtitles, or uses Find in Settings.

When TinyTorrent sees a torrent finish, it looks for bundled subtitles in that
torrent's folders. When the switch Download when a torrent finishes is On, it
then searches once for each wanted video in each wanted language that still
has no subtitle. A torrent that finished while the window was closed, or whose
search the window closed before deciding, gets this search when the window
next connects, because subtitle work runs only in the window. The search is decided
once per torrent, whatever its videos and languages: a torrent that finished
while the switch was Off, or a language added or a supplier fixed later, starts
no search for it. [Configuration](subtitles/configuration.md#configuration)
owns the details.

Normal operation has no dialogs, notifications, torrent columns or inspector
statuses. Running subtitle work shows in the shared
[background work](background-work.md) status, and only a requested run reports
its success. A manual choice opens only on request, as described under
[choosing a subtitle](subtitles/matching.md#choosing-a-subtitle). Problems use the window's existing
[feedback placement](ui/feedback.md#feedback-placement), because one place for
every problem lets the person find it without opening Settings. A subtitle failure never changes torrent
completion, seeding, or the availability of the movie.
Shared source and database failures use the existing persistent workspace
feedback and Retry, because they affect Library and subtitles together.

**Owner ruling: every wanted video file in a managed torrent is treated alike.**
A movie, one of several movies in a torrent, and an episode in a season pack
all get the same release-name and hash matching, because neither method needs
to know which kind of video it is. Detecting episodes only to skip them would
add logic and misjudge some movies. Series identification, archives, disc
images, translation, and subtitle generation are outside this design because
they require identification or processing beyond file matching. "Movie" in
this document means any such video file.

## Ownership and quiet recovery

The [product code rules](../../AGENTS.md#code-style) govern reuse and ownership.
The table below identifies the existing owners for subtitle work; supplier
implementations add only the requests and the reading of answers that differ.

Supplier's operation carries the account, HTTP access and acquisition validity
through each supplier call. The shared send path validates it before reserving
allowance, including sign-in, retries and redirects, so suppliers cannot
accidentally spend requests for obsolete work. Account Check carries no movie
membership; its cancellation and network route still apply.

The subtitle module keeps scheduling, matching policy, cancellation and storage
for every supplier, and the Settings and dialog owners keep drafts, focus and
presentation. The host [Supplier](../../app/src/Subtitles/Supplier.cs) sends
every request a supplier builds, adds the account's key, accounts for the
sign-in request apart from the operation it serves, follows the redirects the
supplier allows and decodes subtitle files. It also decides what fits the
file. For a supplier that lists subtitles by title, the host asks for the
catalogue's titles, chooses the one the file names and asks for that title's
subtitles. For a supplier that lists subtitles for the file itself, each
subtitle carries the title the supplier reports, and the host drops it unless
that title is the file's. The supplier yields its answer page by page, and the
host requests no page after one whose subtitles all name another title, so a
wrong title costs one request. A hash search needs no title, because the hash
names the file. The host also drops subtitles that are forced or report another
season, episode or year. The host records each operation's outcome: success,
and a backoff when the supplier cannot read an answer, for a search as for
Check and Download. A search also backs off once when its pages together take
more than 60 seconds; a single request that times out holds through the shared
HTTP path instead. An [ISupplier](../../app/src/Subtitles/ISupplier.cs) keeps only what
differs: request shapes, language mappings, how it signs in, and reading its
answers into facts, such as a listing's title, whether a subtitle is forced,
or that its downloads are used up, with their return time when it says.
Three real alternatives sit behind the supplier interface,
so it earns its place. The release name is interpreted, and empty titles and
extras are rejected, once before a request reaches a supplier. The shared HTTP
owner holds platform HTTP, proxy and TLS configuration, request limits, route
enforcement and cancellation, and makes no title or subtitle decisions.

| Responsibility | Existing owner to reuse or extend |
| --- | --- |
| Dialog shell, buttons, settings layout | [Dialog](../../app/src/Shared/Dialog.cs), [ActionButton](../../app/src/Controls/ActionButton.cs), [SettingsRow](../../app/src/Controls/SettingsRow.cs), [SettingsSection](../../app/src/Controls/SettingsSection.cs), and their shared App.xaml styles. Equal action sizing belongs in that shared styling, not a subtitle-only dialog template. |
| Compound account edits and checks | [Proxy editor](../../app/src/Settings/Proxy.cs) and [ProxyDialog](../../app/src/Settings/ProxyDialog.xaml.cs) already handle drafts and obsolete check results. Reuse their common behavior while keeping supplier validation and credentials separate. |
| Ordinary settings, search and text | [Settings](../../app/src/Settings/Editor.cs), [SettingsPage](../../app/src/Settings/SettingsPage.xaml.cs), [Strings](../../app/src/Shared/Strings.cs), and existing English/Spanish catalogues. Extend their inventory, navigation and save path. |
| Commands and background completion | Direct C# asynchronous operations at the subtitle owner; existing PipeClient supplies torrent facts only. No supplier/job pipe protocol. |
| Saved state and protected secrets | The C# SQLite owner shared with Library; one schema and transaction path, Windows user-scoped DPAPI for secrets. |
| Subtitle records and pending work | The same C# database owner, with subtitle decisions at the subtitle module. Library enablement never gates subtitle work. |
| Torrent facts and file operations | The shared C# torrent adapter seeds and synchronizes SQLite from existing file facts. Subtitles queries SQLite. C# owns subtitle publication and file handling; engine payload operations remain unchanged. |
| Networking | One C# HTTP owner used by TMDB and subtitle adapters. Use platform HTTP, proxy, TLS and ZIP capabilities; add only route behavior the platform does not supply. Engine networking stays with its existing callers. |

One C# subtitle module queries current managed-file facts and its confirmed
settings from SQLite, following the [shared data flow](library.md#sqlite-and-video-information).
Shared application settings, such as the effective proxy, adapter and interface
language, still come from their existing owner; they are not copied into another
settings store to satisfy the file-data rule. Enabling subtitles creates no
Library page, runs no Library query and starts no TMDB work.
It owns supplier requests, matching, retry decisions and direct Settings
operations. Network and file reads run
outside the UI dispatcher and database worker. The engine owns torrent state
and operations; it neither schedules subtitle jobs nor publishes their status.
Before issuing each request and before publishing its result, query SQLite to
confirm that the output still serves at least one captured, current wanted
source file, the feature is enabled, and the supplier/account and requested
language still apply. The movie can move during a request: when the check
before publishing finds the video gone from its captured path, the attempt is
cancelled and the job returns to pending, so reconciliation can give it the
new location. A settings
change or observed removal cancels obsolete work; an uncancellable late result
cannot replace newer state. The [concurrent file-operation limitation](subtitles/files.md#saving-and-following-the-movie)
applies to publication. An already transmitted request cannot be
recalled. Window closure cancels outstanding work without waiting indefinitely
for a supplier, preserving accepted unfinished manual jobs for the next window.

Persist the pending work and subtitle records in SQLite so work resumes after a
restart. The records, and the check before each lookup, keep
reconnecting or restarting from consuming the supplier's allowance again. If
the database cannot be used, subtitle work pauses and the shared workspace
feedback offers Retry, because without records TinyTorrent cannot avoid repeat downloads.

Retry temporary network failures with bounded backoff and respect the supplier's
retry or quota-reset time. Authentication failures wait for corrected credentials.
Apply rate and quota limits across the supplier account's work, not independently
per movie. A large movie collection must not multiply the permitted request rate.
While Automatic subtitles is On or requested manual work waits, a lasting
supplier problem, such as refused access, an incomplete configuration, an
unavailable supplier or an unusable proxy route, shows in the
[message bar](ui/feedback.md#message-bar) with Settings. Subtitle files that
cannot be saved show there with Retry, because the folder is fixed outside the
app. A waiting batch's status names the same problem instead of
Searching. A subtitle that could
not be downloaded or saved for one movie is an event: one error toast names the
latest subtitle file and counts the others, and stays until the person closes
it. Settings shows only state: whether access was checked and when a used-up
allowance returns. This lets the person understand missing subtitles without
reading logs; routine success and transient retries remain invisible.
Diagnostics retain technical detail without credentials.
A completion search uses the C# movie hash at once when the supplier supports
it, and then the release name. A requested search for a finished video in an
unfinished torrent has no hash yet, so when it finds no match it is tried once
more with the hash after confirmed torrent completion.
Do not repeat a lookup already made with that evidence. Further polling would
spend requests without new local evidence. Unresolved outcomes survive restart
so reopening TinyTorrent does not reset those limits.

## Delivery scope

The feature is implemented in C# alongside Library's shared source and database
owners.

**Owner ruling, 2026-10-09:** benchmarks, agent interaction runs, exhaustive
supplier/route/failure matrices and comprehensive test checklists are
**cancelled for this delivery**. Reuse useful work and valid evidence. Keep the
behavioral rules in this design, including completion-only automatic searches,
languages, quiet recovery, source validation, no-replace publication and file
ownership.

## Verification and contract integration

Follow [testing](../agents/testing.md). The former comprehensive verification checklist
is **cancelled**. Existing successful
storage, filesystem, supplier and UI evidence remains useful; do not rerun it
for unchanged behavior or probe suppliers to fill every unverified combination.

Keep focused review of concrete correctness findings, particularly unwanted
network requests, stale publication, overwriting an existing file, or moving
and deleting files the feature does not own. These are behavior requirements,
not a mandate to create a test for each scenario.

The architecture, engine, pipe API and UI contracts own the rules for
their concerns. This contract links to them instead of restating them, so
implemented behavior has one authority. The [Settings handoff](../../app/prototypes/settings-options-prototype.md#subtitles)
remains the presentation reference.
