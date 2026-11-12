# One owner for each rule

**Proposed on 12 November 2026. Not reviewed, not approved, and nothing is
implemented.** It comes from an audit of the whole source tree on the same
day: twelve readers read the product code in full and a scanner compared
every file for copied text. The audit found about 190 rules that are
implemented more than once. In about 40 of them the copies already behave
differently. Line numbers are those of commit 7e0ee84.

Paths without a project prefix are under `app/src`. Engine paths start with
`engine/`, library paths with `lib/`.

## Goal

**Each rule of the product has one owner and one implementation, and each
owner sits where the rest of the system expects it.**

- A person never gets two answers to one question. Today the Settings home
  page and the Seeding filter count seeding torrents differently, and one
  page shows a limit of `1000` where another shows `1,000`.
- An engineer who changes a rule changes one place.
- An engineer who needs a rule finds its owner by its name, so the next
  feature uses the owner and does not write a ninth copy.

Removing each copy where it stands does not reach this goal. A pair removed
in place leaves the cause, and the next change makes a new pair. This plan
therefore works by owner: each stage completes or creates one owner, moves
every caller to it and deletes every copy.

## What the audit shows

The 190 findings have five causes. Each cause has its own remedy.

1. **A fact has no name.** No member states the fact, so each caller spells
   it out. "Is this page a workspace" is written as `Torrents or Library`
   in nine places. The app compares engine error codes as text in 15 places
   in 8 files, although the [code style](../../AGENTS.md#code-style) says
   that errors which cross a boundary have named types. Remedy: an enum, a
   member on the model or a named constant at the owner.
2. **An owner exists and callers go around it.** Its interface misses the
   caller's case, or it is private. `Navigation.IsWorkspace` is private.
   `FrameCallback` owns "run once at the next frame" and two places write
   it by hand. `FieldColumns` owns two columns that stack when narrow, but
   does not follow the Windows text size, so four pages write their own.
   Remedy: complete the owner's interface from the needs of all its
   callers, then move them.
3. **A workflow that several features share has no owner.** Six Library and
   subtitle plugins each read their built-in key. The IMDb data and the
   city data each implement download, stop, delete and replace. Eight
   places hand-write "run once after this turn". Remedy: one deep module,
   designed once.
4. **Dependencies point the wrong way.** `Shared` is the lowest area and
   names feature types: [DataDownload.cs](../../app/src/Shared/DataDownload.cs)
   and [Geolocation.cs](../../app/src/Shared/Geolocation.cs) report failures
   as `Library.VideoException`, so a failed city-data download is a "video"
   failure; [Strings.cs](../../app/src/Shared/Strings.cs) lines 214 to 242
   name the exception types of Library, Shell and Subtitles to choose a
   text. `DialogPreference` uses Settings, `TorrentSource` uses Torrents,
   `Controls/Pane` uses `Shell.Navigation` and `Controls/SettingsRow` uses
   `Settings.Setting`. Features use each other: Torrents and Settings in
   both directions (`AddDraft`, `SpeedLimit`, `ConnectionSetup`, `Editor`),
   Library uses Torrents (`Browser/Information`) and Settings uses Library
   (`Editor/Plugins`). The architecture names owners but states no
   direction between the seven app areas, so nothing told a writer or a
   reviewer which way was wrong. Remedy: a stated direction, and each
   shared type moved to the area below its users.
5. **A boundary forces two copies and nothing ties them together.** The
   table library cannot use app code, so five helpers exist on both sides.
   The engine is C++ and the app is C#, so ranges, units and words exist on
   both sides, and the readers reported four pairs that disagree. Remedy: one home that both
   sides can reach, or the side that knows the fact sends it.

## Rules for this work

These apply the existing standard to this plan. The standard itself stays
in [AGENTS.md](../../AGENTS.md), [naming](../agents/naming.md),
[comments](../agents/comments.md) and
[compilation](../agents/compilation.md).

- **Work by owner, and finish each owner.** A stage moves every caller and
  deletes every copy in the same change, because a half-moved rule is two
  mechanisms.
- **Design the owner from all its callers.** Before code, list what each
  caller needs and write the smallest interface that carries all of it. An
  owner fitted to its first caller is the owner the next caller goes
  around, which is cause 2.
- **Settle each difference before code.** Where copies disagree, the stage
  states which behavior wins, under "Settled as". An unstated choice
  changes what a person sees without anyone deciding it.
- **Prefer a type to a helper.** A fact that can be an enum, a member on
  its model or a constant at its owner does not become a static helper. No
  stage adds a `Utils`, `Helpers` or `Common` file.
- **A pair that only looks alike stays.** A pair is collapsed only when one
  sentence states the rule that both copies implement. Merging two
  different rules makes one function with a flag for each caller.
- **No owner for one caller.** An interface, base class or new type needs
  two real users today.
- **Verification answers a question.** Each stage names its open question.
  Moves that the compiler checks end on source review. No stage adds a test
  by habit; [testing](../agents/testing.md#what-earns-a-test) decides.
- **A TableView change starts from the table's history**, as
  [its instructions](../../lib/TableView/AGENTS.md) rule.

## Direction between the app areas

Proposed text for the architecture's
[source layout](../contracts/architecture.md#projects-and-source-layout),
if the owner accepts decision 3:

> `Shared` uses no other app area. `Controls` uses `Shared`. `Torrents`,
> `Settings`, `Library` and `Subtitles` use `Shared` and `Controls`, and
> not each other; a type that two features need lives in `Shared` or
> `Controls`. `Shell` composes the features and may use every area. This
> lets a reviewer refuse a dependency by its direction, and keeps a feature
> from becoming the authority for a rule that another feature also needs.

Two recorded decisions stay as they are: child owners take their
`MainViewModel`, and `PipeClient.Files` holds Inspector arbitration
([ownership decisions](../contracts/architecture.md#ownership-decisions)).

The app is one project, so the compiler cannot refuse a direction. Review
enforces it, in the
[ownership pass](../agents/compilation.md#focused-review-passes).

## The owners

This table is the design. Each row is one concept of the system, its one
owner, and the stage that completes it.

| Concept | Owner | Today | Stage |
| --- | --- | --- | --- |
| Engine errors in the app | `CommandException`, with an enum in `Enums.cs` | Text compared in 15 places | 1 |
| Torrent, peer, file and limit facts | The model types that hold the data | Spelled out by each caller | 1 |
| Which pages are workspaces | `Navigation` | Private; 8 copies | 1 |
| Failure text | Each failure carries its text key; `Strings.Error` reads it | `Strings` names feature types | 2 |
| Direction between areas | The architecture | Not stated | 2 |
| Display text and number formats | `Shared/Strings` | Features format by hand | 3 |
| Folders and paths in the app | One type in `Shared` | 5 versions | 3 |
| Work on the UI thread, once per turn or frame | `Shared/FrameCallback` and one sibling | 11 hand-written | 4 |
| Dialog, menu and command shells | The dialog owners in `MainWindow/Dialogs.cs`, `MenuItems` | Repeated per feature | 4 |
| Settings | `Setting`, `Editor`, `Schedule`, `PeriodSpan`, `Overview` | Page and search restate them | 5 |
| Custom-drawn controls | `Controls`, and the map's own owners | Each control has its own | 6 |
| SQLite access and small saved files | `Shared/Storage` | IMDb data has a second copy | 7 |
| Provider access: keys, replies, JSON | `Shared/Network`, beside `ProviderHttp` | 6 plugins and 2 hosts | 8 |
| What every plugin shares | The hosts: `VideoLookup` with `ProviderOption`, and `Supplier` | Plugins repeat host work | 8 |
| Downloaded data files | `Shared/DataDownload` with `IDataset` | IMDb and city data each | 9 |
| Subtitle operations | `Subtitles.Acquisition` | One shell per operation | 10 |
| Mechanics the table and the app both need | A small library beside `lib/Glyphs` (decision 1) | A copy on each side | 11 |
| Tokens, styles and shared controls | `App.xaml` and `Controls` | Pages write literals | 12 |
| Engine paths, text, errors and Windows resources | `Engine.h`, `Owned.h`, `Drives` | File-local copies | 13 |
| Engine command, save and storage shells | `Commands.cpp`, `Changes`, `Files.cpp`, `Storage.cpp`, `Policy.cpp` | Repeated per command | 14 |
| Handler registration | One table in `Registration.cpp` | 4 lists | 14 |
| Engine windows | `Acrylic`, `Popup`, `Toasts`, as [engine instructions](../../engine/AGENTS.md) say | Copies in each window | 15 |
| Facts both processes state | The engine sends them (decision 2) | Both state them; 4 disagree | 16 |
| Test and diagnostic harness | `ProofHarness`, `CaptureMatrix`, one probe base | Copies per file | 17 |

## Decisions for the owner

1. **Where do mechanics live that the table and the app both need?**
   Recommended: a small library beside `lib/Glyphs`, which is how the
   repository already shares code between the two. The other choice is to
   make them public in TableView; that puts non-table API into the table's
   public contract, and code that does not use the table cannot reach it.
   Its name goes through the naming review.
2. **How do the two processes agree on a setting's range?** Recommended:
   the engine sends each setting's range in the settings snapshot it
   already sends, and the app stops stating ranges, because the owner that
   knows a fact passes it. This changes the pipe contract and the protocol
   version. The other choice keeps both lists and records the pairs in the
   pipe contract, which removes no copy.
3. **Does the direction rule above become contract text?** Recommended:
   yes. Without it stage 2 has no standard to review against.
4. **Are the six probe pages in `lib/TableView/sample/Probe` still in
   use?** They hold 8,841 lines and last changed on 8 October 2026. If
   their questions are answered, deleting them is the tidy result. If they
   stay, stage 17 gives them one base page.

Every other choice in this plan has a default, stated in its stage.

## Stages

Each stage lists what it absorbs. "To re-read" marks findings that one
reader read on both sides and the lead has not checked; the stage re-reads
them first and drops any that is not one rule.

### 1. Name the facts

Owner per fact; no new file. The compiler checks every move.

- **Engine errors.** `CommandException.Code` becomes an enum that the pipe
  client parses once. A word the app does not know becomes `Unknown` and
  the exception keeps the engine's text, so the person still sees it. One
  member answers "the folder was refused". Callers: `Shell/Inspector.cs`
  90, 1021; `Shell/MainViewModel.cs` 1209, 1220;
  `Shell/MainViewModel/Actions.cs` 242; `Shell/Notifications.cs` 490, 491,
  809; `Torrents/AddDraft.cs` 224, 889; `Torrents/MoveDraft.cs` 135;
  `Torrents/Torrent.cs` 193, 196, 203; `Shared/TorrentSource.cs` 229.
- **Workspace pages.** `Navigation.IsWorkspace` becomes internal. Callers:
  `Shell/MainWindow/Workspace.cs` 112, 119, 287, 318; `Actions.cs` 611;
  `Connections.cs` 38; `Chrome.cs` 357; `Shell/Inspector/Selection.cs` 25.
- **Torrent state groups.** `Settings/Overview.cs` 87 calls
  `MainViewModel.Matches` (`Shell/MainViewModel/Finding.cs` 161).
- **Peer facts.** "Connected" and "active" become members of the peer
  record. Callers: `Torrents/Swarm.cs` 172; `Torrents/MapModel.cs` 993.
- **File priorities.** One ordered list of value, text key and glyph in
  `Controls/FileSelection.cs`; the menu (`FileBrowser.xaml.cs` 174) and the
  combo (`FileSelection.cs` 785, 967) read it.
- **Limit mode text.** `Shell/MainViewModel/Limits.cs` 105 owns it.
  Callers: `Settings/LimitSelector.cs` 83; `ConnectionSetup.cs` 350;
  `Schedule.cs` 464. To re-read.
- **One enum each**, to re-read: the incoming-connection state
  (`Shell/MainViewModel/Actions.cs` 39 to 61, six properties); the theme
  (`Settings/ThemePicker.xaml.cs` 41, `Overview.cs` 99,
  `Shell/MainWindow.cs` 491).
- **Movie or series.** `Release.Kind` in `Shared/FileName.cs`. Callers:
  `Subtitles/Supplier.cs` 441; `Suppliers/OpenSubtitles.cs` 245;
  `Library/VideoLookup/Identification.cs` 159, 421, 427. To re-read.
- **Subtitle file extensions.** `FileName` lists them once;
  `Subtitles/SubtitleFile.cs` 15 and `FileName.cs` 66 read the list.
- **A subtitle job is still active.** One member on `Acquisition` and one
  SQL constant. Callers: `Acquisition/Acquire.cs` 692; `Storage.cs` 465,
  798; `Work.cs` 20; `Jobs.cs` 84.
- **Constants at their owners:** 125,000 bytes per Mbit/s
  (`Settings/ConnectionSetup.cs` 15, 642, 800; `Shell/Inspector/Peers.cs`
  107); the port range (`Settings/Proxy.cs` 288, `Editor.cs` 774,
  `Shared/Network/ProviderHttp.cs` 359); the repository address
  (`Shell/AboutPage.xaml.cs` 38, `CreditsPage.xaml.cs` 353, `App.cs` 24,
  `Shell/Updates.cs` 22); the search pause (`Controls/FileSelection.cs` 18,
  `Library/Browser.cs` 899); the minimum window size
  (`Shell/MainWindow/Chrome.cs` 606, `Placement.cs` 162); the dataset age
  (`Shell/MainWindow/Datasets.cs` 64, `Shared/DatasetRow.cs` 167). To
  re-read: the plus and minus key codes (`MainWindow/Actions.cs` 60,
  `Torrents/SpeedGraph.xaml.cs` 42, `PeerMap/Navigation.cs` 18); the split
  height (`Workspace.cs` 26, `Placement.cs` 357); the guess count
  (`Subtitles/Batch.cs` 211, `Acquisition.cs` 17); file kinds written as
  numbers in SQL (`Library/Store.cs` 677).

Settled as: the home page counts seeding torrents as the filter does. A
peer is active only while connected. `.ssa` and `.vtt` are subtitles
everywhere. A restored window is never narrower than the title bar needs.

### 2. Make dependencies point one way

- **Failures carry their text.** One failure base in `Shared` states its
  catalogue group, key and arguments. `Library.VideoException`,
  `Shell.UpdateException` and `Subtitles.SubtitleException` use it, and
  `Strings.Error` no longer names a feature. This also removes the four
  copies of the subtitle failure key (`Shared/Strings.cs` 225,
  `Shell/Notifications.cs` 515, `Shell/MainViewModel.cs` 303,
  `Subtitles/Batch.cs` 253).
- **A data download fails as a download.** `DataDownload` and
  `Geolocation` stop using Library's video failure. Stage 9 builds on this.
- **Trace, then move down or move home:** `Shared/DialogPreference.cs`,
  `Shared/TorrentSource.cs`, `Controls/Pane.cs`, `Controls/SettingsRow.cs`,
  and the feature pairs `Torrents/AddDraft.cs`, `Torrents/SpeedLimit.cs`,
  `Settings/ConnectionSetup.cs`, `Settings/Editor.cs`,
  `Library/Browser/Information.cs`, `Settings/Editor/Plugins.cs`. For each
  one, the type that two areas need moves to `Shared` or `Controls`, or the
  file moves to the feature that alone uses it.
- **The direction rule** goes into the architecture (decision 3).

Not traced yet: what each of those ten files takes from the other area.
The stage starts with that trace.

### 3. Text and paths

- **`Strings` formats; features do not.** Absorbs: a count where 0 means
  unlimited (`Settings/Editor.cs` 325, `ConnectionSetup.cs` 717); "reason;
  N more problems" (`Shell/Notifications.cs` 217, 531;
  `Shell/MainViewModel/Notifications.cs` 253). To re-read: a period's
  length (`Settings/Schedule.cs` 518 against `Strings.Duration`); a rate
  (`Torrents/SpeedGraph.xaml.cs` 242, 330); "time 0 means none"
  (`Torrents/Torrent.cs` 507, `Tracker.cs` 45, `Shell/Inspector.cs` 171);
  the "—" text for no value, 85 times in 22 files; the label of an enum
  value (`Shell/MainViewModel/Finding.cs` 119, 151, 414, 469, 495;
  `Settings/Overview.cs` 37; `Shell/MainWindow/Library.cs` 260;
  `Library/Browser.cs` 164); shortcut text (`MainWindow/Actions.cs` 176,
  500; `Chrome.cs` 672; `PeerMap/Navigation.cs` 64); the day and time label
  (`Settings/Scheduler.cs` 66, `Schedule.cs` 411).
- **Folders.** One type in `Shared` answers "same folder" and "inside this
  folder". Callers: `Torrents/AddDraft.cs` 93, 190; `MoveDraft.cs` 39;
  `RemovalDraft.cs` 183; `Subtitles/Acquisition/Discovery.cs` 588;
  `Shell/MainViewModel/Notifications.cs` 186.
- **Clipboard.** `Torrents/PiecesMap.xaml.cs` 568 asks the model, as
  `Shell/MainWindow.cs` 157 does.

Settled as: a count shows digit grouping (`1,000`). A full-day period reads
"1 day". Two folders are the same after the full path is taken and the
trailing separator is removed, ignoring case. A failed clipboard copy is
reported.

### 4. Scheduling, dialogs, menus and commands

- **Once per turn.** One sibling of `FrameCallback` replaces
  `Shell/MainWindow/Actions.cs` 246; `Chrome.cs` 141, 523;
  `Shell/InspectorPane/Peers.cs` 431, 483; `Shell/InspectorPane.xaml.cs`
  307, 490; `Settings/SettingsPage/Home.cs` 152.
- **Dialogs.** One `Confirm` beside `ShowDialog` for
  `MainWindow/Dialogs.cs` 236; `Datasets.cs` 16, 87; `Actions.cs` 522;
  `Shell/MainWindow.cs` 775, 805. The Remove dialog takes its primary
  button from `ShowEditor` (`Dialogs.cs` 143; `Actions.cs` 685). Library
  key saving moves from `MainWindow/Library.cs` 272 into `Library.Browser`.
  To re-read.
- **Menus and commands.** One Library row menu beside `OriginActions`
  (`MainWindow/Library.cs` 226; `Shell/InspectorPane/Library.cs` 216).
  `Library/Details.cs` 66 calls `OriginActions.Command`. To re-read: one
  copy-magnet and copy-hash rule (`Shell/MainViewModel/Actions.cs` 459;
  `Shell/Inspector.cs` 128, 996); the Move and Download predicate
  (`MainViewModel/Actions.cs` 189, 198).
- **Shell rules.** `Shell/Notifications.cs` 66 and 86 become one. The
  "features may continue" guard in `Shell/Features.cs` 319, 336, 346, 382,
  406 becomes one predicate. To re-read: Library recovery (`Features.cs`
  434, 496); the Peers and Trackers placeholder (`InspectorPane.xaml.cs`
  450, 463); peer scope (`Shell/Inspector/Peers.cs` 355, 370).
- **Small owners in `Controls`.** The guard for Enter and Escape during
  IME composition (`Settings/ConnectionPage.xaml.cs` 31,
  `SettingsPage.xaml.cs` 883, `Shell/InspectorPane.xaml.cs` 213) goes to
  `TextEditor`. To re-read: "is this element inside that container"
  (`MainWindow/Notifications.cs` 122, `InspectorPane.xaml.cs` 270,
  `SettingsPage.xaml.cs` 917); name equals tooltip (`Chrome.cs` 650 is
  private; `InspectorPane/Peers.cs` 263); open a path (`Opening.cs` 12,
  `MainWindow/Library.cs` 68); popup theme (`Chrome.cs` 730, `Work.cs`
  173).

Settled as: the Library menu offers metadata commands only for a row that
supports metadata. A dialog's primary button is disabled while the window
closes. Copy magnet does nothing for a torrent without a magnet. The guard
checks reset and a missing subtitle worker in every copy.

Open question: `Shell/MainWindow/Placement.cs` 91 waits inside the
rendering event, and `FrameCallback` runs after it. Trace whether startup
needs the earlier moment before it moves.

### 5. Settings

- `Setting` says that it has no row; search reads that
  (`Shell/MainViewModel/Finding.cs` 418).
- `Overview` owns which model changes refresh the summaries
  (`Settings/Overview.cs` 17; `SettingsPage.xaml.cs` 216).
- `FieldColumns` follows the Windows text size, and the pages use it:
  `Settings/SpeedLimits.xaml.cs` 118; `SettingsPage/Home.cs` 176;
  `Shell/AboutPage.xaml.cs` 73; `Settings/ConnectionPage.xaml.cs` 91.
- To re-read: `Editor.SelectTheme` (490) against `Setting.Select` (294); a
  saved number as input text (`Setting.cs` 158, 228; `Editor.cs` 333); the
  category tables (`SettingsPage.xaml.cs` 135, 478, 503, 667); "which
  period is at this day and minute" (`Week.cs` 475, 682, 723); the
  15-minute step (`PeriodSpan.cs` 7; `Week.cs` 133, 739).

### 6. Custom-drawn controls

The peer map, pieces map, speed graph, speedometer and week share drawing
facts.

- One brush-to-colour read, mix and alpha (`Torrents/PiecesMap.xaml.cs`
  504, 514; `PeerMap.xaml.cs` 590; `MapRenderer.cs` 971, 974;
  `Settings/Speedometer.xaml.cs` 150). To re-read.
- One answer to "Windows lets this animate" (`Controls/Motion.cs` 12;
  `SettingsExpander.cs` 75; `Torrents/MapRenderer.cs` 132;
  `Speedometer.xaml.cs` 55). To re-read.
- One read-only value automation peer (`PiecesMap.xaml.cs` 729;
  `SpeedGraph.xaml.cs` 781).
- Inside the map: the visible span (`MapMotion.cs` 1095 uses
  `MapViewport.cs` 160); the 33 ms frame (`MapMotion.cs` 29,
  `MapRenderer.cs` 129). To re-read: the meridian (`MapModel.cs` 27, 183,
  312); point-to-segment distance (`MapRenderer.cs` 842, 1876); the
  10-second hold (`MapDots.cs` 128, 173, 215); the zoom step
  (`PeerMap/Navigation.cs` 98, 108, 260).
- In the drafts, to re-read: preview and apply (`AddDraft.cs` 429, 615);
  check then set a folder (`MoveDraft.cs` 123;
  `Shell/MainViewModel/Actions.cs` 227).

Settled as: the Windows animation setting decides for every control; the
map and the speedometer keep High Contrast as their own added condition,
stated once each. A brush's opacity counts, and channel values round.

### 7. Storage

- `Shared/Storage/Database.cs` owns the connection options, the cancel
  hook and the interrupt mapping; `Library/Imdb/Dataset.cs` 391 to 427 and
  508 to 530 use them.
- "Which identification applies to a file" has one query source.
  `Library/Store/Identification.cs` 11, 161, 299, 318, 465 and
  `Shared/Storage/Database/Files.cs` 50 read it, as the view in `Schema.cs`
  205 does. A temporary view needs no schema revision; a stored view is a
  migration under the [database contract](../contracts/database.md).
- To re-read: one small JSON state file loader and saver (`Shell/Updates.cs`
  116, 420; `Shell/MainWindow/Placement.cs` 141, 319); the Library filter
  vocabulary as one table (`Library/Store.cs` 237, 698, 760;
  `Browser.cs` 834); in Subtitles, the video row reader
  (`Acquisition/Discovery.cs` 314; `Storage.cs` 160), the output-source
  upsert (`Storage.cs` 385, 569; `Discovery.cs` 537) and the reset of
  pending jobs (`Acquisition.cs` 180, 228).

Open question: line 14 of `Store/Identification.cs` also requires a video,
and the other copies do not. Trace which one the Library contract means
before they become one.

### 8. Provider access and the two plugin hosts

This stage has the largest design. It starts with the
[design step](#the-design-step-of-stages-8-9-and-10).

- **In `Shared/Network`:** the built-in key (`Subtitles/Suppliers`
  `OpenSubtitles.cs` 26, `SubDL.cs` 23, `SubSource.cs` 43; `Library`
  `Tmdb/Provider.cs` 26, `Omdb/Provider.cs` 26, `MdbList/Provider.cs` 28)
  and which key is sent (`Library/VideoLookup.cs` 230;
  `Subtitles/Supplier.cs` 99). To re-read: reading a JSON text property
  (`Supplier.cs` 517; `Omdb` 235; `Tmdb` 323; `MdbList` 201; `Wikidata`
  307); the common part of a reply failure (`Library/ProviderJson.cs` 63;
  `Supplier.cs` 361; `Shared/DataDownload.cs` 37); address safety, which
  `ProviderHttp.cs` 222 already checks (`Supplier.cs` 293;
  `OpenSubtitles.cs` 301; `SubDL.cs` 137).
- **Library host.** `VideoLookup.Available` (235) is the one answer to
  "is this provider usable" (`Browser/Information.cs` 160). To re-read:
  the host maps a parse error once (`MdbList` 66, `Omdb` 76, `Tmdb` 60,
  `Wikidata` 108), applies the title limit and the kind check once, and
  builds a rating once (`MdbList` 148, `Omdb` 156, `Tmdb` 238, `Wikidata`
  249, `Imdb/Dataset.cs` 439); query words come from `FileName.Words`
  (`Omdb` 32, `Wikidata` 35); one title key (`Imdb/Dataset.cs` 455 against
  `FileName.cs` 85); the retry time of a hold (`Browser.cs` 153, 452;
  `VideoLookup.cs` 438, 928, 1294).
- **Subtitles host.** The host owns the page loop (`OpenSubtitles.cs` 182,
  `SubDL.cs` 163, `SubSource.cs` 154) and one "forced" rule
  (`SubtitleFile.cs` 474; `SubDL.cs` 185). To re-read: the check for a
  sample or an untitled file (`Acquisition/Acquire.cs` 568;
  `Supplier.cs` 154).

Settled as: one "forced" pattern, the wider one. A missing JSON text is
no value, and text is trimmed; a provider's own words such as "N/A" stay
with that provider. An apostrophe never separates title words.

Open question: a score of 0 is no rating in `MdbList` and a rating in
`Omdb` and `Wikidata`. Decide from the Library contract.

### 9. Downloaded data files

This stage starts with the
[design step](#the-design-step-of-stages-8-9-and-10).

`DataDownload` with `IDataset` owns start, stop, interrupt, delete as
maintenance work, fetch to a `.part` file, replace under a write lock,
discard and the failure mapping. The IMDb data and the city data keep
their source, their build and their reader. Absorbs `Shared/Geolocation.cs`
149 to 240, 272 to 337 and 367 to 446; `Library/Browser/Information.cs`
281 to 348 and 373 to 431; `Library/Imdb/Dataset.cs` 106 to 238 and 483 to
503.

Open question: replacing a file while its reader is open. The city data
closes and reopens its reader inside the lock; the shared owner must keep
that.

### 10. Subtitle and Library operations

This stage starts with the
[design step](#the-design-step-of-stages-8-9-and-10). Its findings are to
re-read, except where marked.

- One shell for an operation the person starts
  (`Subtitles/Acquisition/Choose.cs` 11, 85, checked; `Acquisition.cs` 561;
  `Discovery.cs` 131) and one lookup factory (`Choose.cs` 35, 70, 109;
  `Acquire.cs` 275, 370; `Storage.cs` 713).
- One settings write (`Acquisition.cs` 192, 275, 329, 383, 468).
- One scan run (`Discovery.cs` 129, 243, checked). `Invalidate`
  (`Acquisition.cs` 641) calls `ReportWorkWaiting` (`Acquire.cs` 29,
  checked). One rerun loop (`Discovery.cs` 33; `Storage.cs` 203).
- One change subscription and one action runner for the three subtitle
  views (`Subtitles/Batch.cs` 85, 258, 286; `Settings.cs` 304, 569, 592,
  628; `Automatic.cs` 40, 77).
- In Library, one on-screen work loop (`VideoLookup.cs` 751, 1199, 1343;
  `FileFacts.cs` 118, 267).

Settled as: a view refreshes once for a burst of changes, and reports a
failure only while it is attached to its model.

### 11. Mechanics the table and the app both need

Depends on decision 1. Absorbs:

- the text catalogue loader (`lib/TableView/src/Strings.cs` 114 to 199;
  `Shared/Strings.cs` 62 to 135);
- the tooltip that shows the full text only while it is trimmed
  (`lib/TableView/src/Header/Cell.cs` 171; `Controls/Trimming.cs`);
- the click-versus-drag distance (`lib/TableView/src/DragDistance.cs`;
  `Settings/Week.cs` 602; `Torrents/PeerMap/Navigation.cs` 137);
- "is this key down" (`lib/TableView/src/Table/Input.cs` 1437; nine sites
  in `Shell/MainWindow/Actions.cs`, `Shell/MainWindow.cs`,
  `Torrents/PeerMap/Navigation.cs`, `PiecesMap.xaml.cs` and
  `SpeedGraph.xaml.cs`).

Inside the table: `Body/Row.FindOwner` serves `CellsPanel.cs` 97; one
`KeyOf` on `ItemIdentity` replaces `Table/View.cs` 653, `Body/Slots.cs`
492 and `Body/Projection.cs` 194. To re-read: `Header/Strip.cs` 759
against `Table/Input.cs` 261; `Input.cs` 521 against 560;
`Header/Strip.cs` 205 against `Header/Menu.cs` 111; `CellsPanel.cs` 209
against `EffectiveLayout.cs` 110; `CellsPanel.cs` 136 against 289.

Settled as: the Windows drag distance applies everywhere, so a person who
raised it gets the same steadier clicks in the schedule and the map. A
placeholder without a closing brace is an error in both catalogues.

The header menu's check items and `Controls/MenuItems` are the area that
the Switch.cs work settled; this stage confirms that one mechanism
remains.

### 12. Tokens, styles and shared controls

`App.xaml` owns tokens and styles and `Controls` owns shared controls;
pages stop writing literals.

- One dictionary for the button brush aliases (`App.xaml` 599, 649, 699;
  `Shell/MainWindow.xaml` 82), as `Controls/ButtonResources.xaml` does.
- One folder row for `Settings/SettingsPage.xaml` 956, 995, 1335, 1359.
- One failure icon; `Controls/CheckStatus.xaml` 41 and `SettingsPage.xaml`
  1537 use another glyph than `Shell/MainWindow.xaml` 954,
  `Torrents/AddDialog.xaml` 186 and `PiecesMap.xaml` 138.
- The rate display uses the Field styles at `App.xaml` 1684
  (`MainWindow.xaml` 181, 1228; `Shell/InspectorPane.xaml` 277).
- One secondary text style (`App.xaml` 816; `Library/BatchDialog.xaml` 9).
- One template each for: the connection preview row
  (`Settings/ConnectionPage.xaml` 10, 48); the progress cell
  (`Controls/FileBrowser.xaml` 152; `MainWindow.xaml` 607); the Name and
  Title cells and the column definitions (`Library/LibraryTable.xaml` 9,
  41, 193, 286, 337); the Inspector's field with a copy button
  (`InspectorPane.xaml` 452, 477).
- To re-read: the Standard and Alternative limit controls
  (`ConnectionPage.xaml` 496, 526, 558, 599); section headers in
  `Torrents/AddDialog.xaml` 101, 220, 314 against `controls:Section`; the
  link header (`Settings/SpeedLimits.xaml` 69 against
  `Controls/SettingsLink.cs` 62); the toggle row, 35 times in
  `SettingsPage.xaml`; the legend colours (`Settings/Scheduler.xaml` 63
  against `Week.xaml` 33); secondary text set by hand at about 26 sites;
  dividers; the icon font named in three spellings at 130 sites.

Settled as: every failure shows the same glyph; the UI contract's icon
rules choose which. Every folder row has help text, a tooltip and an
automation identifier.

Open question: whether a moved style changes clipping, wrapping or
spacing. One capture review answers it, in the smallest modes that show
the changed surfaces.

Not verified: whether the three identical theme dictionaries can become
one.

### 13. Engine paths, text, errors and Windows resources

- A scoped COM apartment in `engine/inc/Owned.h` replaces the hand-written
  ones (`engine/src/Main.cpp` 254; `Registration.cpp` 165, 644;
  `Engine/Removal.cpp` 470; `Desktop/Window.cpp` 73).
- One predicate for a short file lock, and one retry
  (`engine/src/Store.cpp` 13; `Payload.cpp` 290; `Engine/Storage.cpp` 41).
- `Checks` from `engine/src/Torrent.cpp` 25 serves `Engine/Alerts.cpp`
  270, `Checkpoints.cpp` 126 and `Turns.cpp` 174.
- Two names for the two `SameFolder` rules (`engine/src/Engine.cpp` 46
  asks the disk; `Engine/Files.cpp` 217 compares text).
- One of each: `FinalPath` (`Main.cpp` 70; `Drives.cpp` 20); "exists,
  without following links" (`Engine/Move.cpp` 374; `Tasks.cpp` 862;
  `Files.cpp` 370, 381); sort and remove duplicate paths (`Files.cpp`
  1059, 1076, 1086, 1216, 1218; `Move.cpp` 162; `Removal.cpp` 148).
- To re-read: the long-path prefix (`Payload.cpp` 49, 99;
  `Engine/Locations.cpp` 52, 243; `Drives.cpp` 131); name comparison
  (`Registration.cpp` 53; `Drives.cpp` 105; `Engine/Turns.cpp` 391); a
  Windows error as text (`Store.cpp` 31; `Payload.cpp` 59;
  `Desktop/Window.cpp` 212); a whole number from JSON
  (`engine/inc/Engine.h` 251; `Engine/Session.cpp` 477; `Commands.cpp`
  719; `Edits.cpp` 43, 77, 107); the nearest existing folder
  (`Drives.cpp` 119; `Engine/Storage.cpp` 130); the volume of a folder
  (`Drives.cpp` 95; `Payload.cpp` 97); free space (`Payload.cpp` 177;
  `Engine/Storage.cpp` 149); `ChecksFiles` (`Torrent.cpp` 706;
  `Engine/Session.cpp` 972); the priority choices (`Engine/Additions.cpp`
  55; `Torrent.cpp` 58); a file's pieces (`Torrent.cpp` 288, 1612); the
  path-length literal (`Engine.cpp` 143, 155, 318; `Main.cpp` 72).

Settled as: `ERROR_LOCK_VIOLATION` is a short lock in every retry. Names
compare as Windows compares them (`CompareStringOrdinal`).

### 14. Engine command, save and storage shells, and registration

- One outcome-to-reply conversion (`engine/src/Engine/Commands.cpp` 109 is
  the helper; 183, 189, 327, 463, 607 repeat it).
- `Changes` takes a document, replacing 13 identical lambdas in
  `Additions.cpp`, `Move.cpp`, `Queue.cpp`, `Removal.cpp`, `Session.cpp`,
  `Settings.cpp`, `Shutdown.cpp`, `Tasks.cpp` and `Watching.cpp`.
- One name-choice worker (`Additions.cpp` 211; `Files.cpp` 306).
- One table of registry entries that write, check, test and remove all
  read (`engine/src/Registration.cpp` 365, 379, 834, 865).
- To re-read: reload from resume data (`Storage.cpp` 762; `Move.cpp` 754);
  the files a deletion removes (`Removal.cpp` 118; `Tasks.cpp` 324);
  source and target pairs (`Move.cpp` 141, 351, 581, 692; `Files.cpp`
  1210); the seed-limit predicates (`Policy.cpp` 484, 494, 561, 576);
  the guard for a new torrent (`Additions.cpp` 9; `Session.cpp` 219); the
  result of a conflicting add (`Additions.cpp` 67, 538); the content-path
  loop (`Torrent.cpp` 309, 524; `Previews.cpp` 40; `Files.cpp` 1053;
  `Tree.cpp` 165); the limit of 64 queries (`Alerts.cpp` 471;
  `Policy.cpp` 555).

Open question: the completeness check omits `ApplicationDescription` and
the toast records that registration writes. Trace whether Windows changes
those values itself before the check includes them.

Checks: the cases of [Checks.ps1](../../engine/tests/Checks.ps1) whose
subject is a changed command, and `Transitions.exe` if the storage control
changes. No transfer check.

### 15. Engine windows

The owners are those of the [engine instructions](../../engine/AGENTS.md).

- `Popup` owns the message loop (`engine/src/Main.cpp` 206;
  `Desktop/Application.cpp` 505; `Instance.cpp` 168).
- `Toasts` owns the Exit button (`Application.cpp` 501; `Instance.cpp`
  208), Open folder with a folder found at the click (`Toasts.cpp` 438),
  and one Retry (`Toasts.cpp` 433, 457).
- `Acrylic` owns the window messages (`Splash.cpp` 229; `Popup.cpp` 1691,
  2032), the class registration (`Splash.cpp` 30; `Popup.cpp` 631), `Scale`
  (`Popup.cpp` 92; `Splash.cpp` 22), the message font (`Splash.cpp` 167;
  `Popup.cpp` 912) and one blend (`Acrylic.cpp` 77, 102).
- In `Popup`: the menu row rectangle (1900, 1919). In `Window.cpp`: the
  foreground grant (137, 278, 392) and the start of waiting (167, 176).
- To re-read: the content-change steps (`Popup.cpp` 693, 781, 848); the
  caption line height (977, 1110); the icon paint (`Splash.cpp` 193;
  `Popup.cpp` 1075).

Settled as: a prompt loop posts a quit message on again. Blended colours
round.

Open question: the look of the popup, its menu and the splash after the
shared code moves. One look at each, in light and dark, answers it.

### 16. Facts both processes state

Depends on decision 2. The pairs that disagree today:

- history intervals (`Settings/Editor.cs` 875 states ranges;
  `engine/src/Engine/Settings.cpp` 199 accepts fixed choices);
- the proxy port (`Settings/Proxy.cs` 288 refuses 0; `Settings.cpp` 394
  accepts it);
- speed units (`engine/src/Desktop/Tray.cpp` 316 stops at GiB;
  `Shared/Strings.cs` 277 goes to TiB);
- language fallback (`engine/src/Strings.cpp` 34 uses the closest
  catalogue; the C# loader applies every parent language).

All four are to re-read. The [pipe API contract](../contracts/pipe-api.md)
lists the pairs that must stay on both sides, so a change to one names the
other.

### 17. Test and diagnostic harness

- Table tests use `ProofHarness`: `Descendants` (`lib/TableView/tests`
  `ColumnFitTests.cs` 835, `ColumnMenuTests.cs` 430, `ColumnMoveTests.cs`
  450, `RealColumnDragTests.cs` 259, `RealHeaderMenuTests.cs` 87); `Shot`
  (`ColumnMoveTests.cs` 517; `SortingTests.cs` 859); the key-injection
  setup (`RealHeaderMenuTests.cs` 110; `RealKeyboardTests.cs` 145).
- Capture journeys call `CaptureMatrix`
  (`Shell/MainWindow/CaptureLibrary.cs` 481) in place of the loop at
  `Capture.cs` 1398, 1549, 1753, 2285, 2469, 2847; `CaptureFiles.cs` 93;
  `CaptureSearch.cs` 65; `CaptureTraffic.cs` 62.
- The probe pages follow decision 4.

## The design step of stages 8, 9 and 10

Each of these stages creates or reshapes an owner whose interface depends
on product decisions: what every plugin shares, what the lifecycle of a
data file is, and what a subtitle operation is. An interface chosen without
those decisions is fitted to today's callers and is reshaped at the next
feature.

The owner starts each of these stages with the
`/improve-codebase-architecture` skill and gives it the stage's area as its
direction, for example "the Library providers and the subtitle suppliers,
plan 22 stage 8". The skill reads that area, shows its candidates, and then
asks the owner one question at a time until the shape of the module is
decided. It designs alternative interfaces side by side for the owner to
compare. Only the owner can start the skill.

The direction is required. Without one the skill chooses where to read from
recent commits, and reads code that this plan already covers.

The direction also states three things that the skill cannot know:

- The settled decisions are the architecture's
  [ownership decisions](../contracts/architecture.md#ownership-decisions)
  and the Owner rulings. The skill looks for them in `docs/adr/`, which
  this repository does not have, so without this line it can propose what
  is recorded as rejected.
- A candidate is judged by whether a rule gets one place to change. Easier
  tests are not a benefit by themselves, as
  [testing](../agents/testing.md#what-earns-a-test) rules.
- The stage's findings and its "Settled as" choices are the starting
  point, so the skill does not search for them again.

Each decision goes into the stage's text in this plan when it is made. A
new domain term goes into [CONTEXT.md](../../CONTEXT.md). A candidate that
the owner rejects for a lasting reason goes into the ownership decisions,
so a later review does not propose it again. The stage is then implemented
against the decided interface.

## Order, tracks and cost

Three tracks touch different files, so they can run side by side:

- **App:** 1, 2, 3, 4, 5, 7, 8, 9, 10, 12, in that order. Stage 6 joins
  when no session is working on the map.
- **Engine:** 13, 14, 15.
- **Library and tests:** 17, then 11 after stage 4.

Stage 16 comes last, because it touches both processes.

The order inside the app track follows dependence: names first, because
later stages use them; then direction, because it decides where each later
owner lives; then the owners that many features call; then the large
designs; then XAML, which needs the one capture review.

For each stage, a session that may judge design writes the owner's
interface and the settled choices, a cheaper session moves the callers
against that text, and the lead reads the diff in the
[focused review passes](../agents/compilation.md#focused-review-passes). A
stage starts only when no other session has uncommitted edits in its
files.

Builds are batched under [compilation](../agents/compilation.md): stages
that are ready join one build.

| Batch | Stages | Build | Checks beyond source review |
| --- | --- | --- | --- |
| A | 1, 2, 3 | App | None expected |
| B | 4, 5, 7 | App | The check scripts whose subject changed |
| C | 8, 9, 10 | App | Provider and IMDb checks for the changed paths, from saved pages |
| D | 6, 11 | App and table tests | Table tests for the changed header and text paths |
| E | 12 | Capture | One capture review |
| F | 13, 14 | Engine | Filtered engine checks |
| G | 15 | Engine | One look at each engine window |
| H | 16 | Both | The existing pipe sample |
| I | 17 | Table tests and capture | None; the next table test run and capture use them |

Estimate: 17 stages, 9 builds, one capture review and three design sessions
with the owner; 25 to 40 hours of agent work, about one week with three
tracks. No stage is designed in
detail yet, so the estimate is rough. Each stage states its own cost
before it starts.

## What stays as it is

- Pairs the audit rejected: members that an interface or a base class
  forces, such as the overrides of `Item` and `Toggle` in
  `Controls/MenuItems/Activation.cs` and dependency property declarations;
  the content that is a plugin's own, such as its address, field names and
  language codes.
- The two title-matching strengths in `Subtitles/Supplier.cs` and
  `Library/VideoLookup/Identification.cs`. They are different rules.
- The piece swatches drawn twice in `Torrents/PiecesMap.xaml`. A comment
  at line 303 says the repeat is deliberate.
- The fast JSON text writer at `engine/src/Torrent.cpp` 1036. Its comment
  gives speed as the reason.
- The peer map's own frame queue (`Torrents/PeerMap.xaml.cs` 337). It runs
  inside the rendering event on purpose, and motion is judged live.
- No new test guards a collapsed pair. The compiler and the direction rule
  guard them.

## Done when

- A second audit of the changed areas finds none of the pairs listed here.
- The table of owners and the direction rule are in the architecture, and
  each settled choice is in the contract that owns its behavior.
- This plan is deleted.

## Not verified

- About half of the findings rest on one reader. They are marked "to
  re-read".
- `engine/src/Engine/Detail.cpp` was searched and not read.
  `engine/inc/Engine/State.h` was searched for constants only.
- Tests, the table sample and the capture journeys were compared for
  copied text only. The PowerShell scripts and the JSON catalogues were
  not audited.
- What the ten files in stage 2 take from the other area.
- Whether the direction rule has further exceptions that only reading
  each `using` shows.
