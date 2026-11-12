# Stage 9 gate record: downloaded data files

Gate done on 12 November 2026, by reading. Nothing was built or run, and no
request was sent. This record takes the place of the stage's text in
[the plan](../22-one-owner-per-rule.md#9-downloaded-data-files) for the
session that implements it.

Paths without a prefix are under `app/src`. Line numbers are those of the
working tree at commit fac0595, before any stage edited the app. Stages 2, 7
and 8 edit some of these files first, so each reference names its member;
find the member when the line moved. This record assumes the result of
[stage 2](stage-02.md#data-downloads): a failed data download is a
`Shared.DownloadException` with a `DownloadFailure`.

## Where the stage stands

Every decision is made. The implementer needs no further design.

The decisions, each with its reason below:

1. `DataDownload` becomes the one owner of a dataset's copy on this computer.
   One instance lives as long as its dataset. It is the object that Settings,
   the dialogs and the Inspector use.
2. `IDataset` changes its role. Today it is what Settings sees, and Library
   and `Geolocation` each implement all of it. After the stage it holds only
   what differs between the two datasets, and `Imdb.Dataset` and
   `Geolocation` implement it.
3. The read lock and the write lock stay in each dataset, in one member,
   `IDataset.Replace`. `DataDownload` decides when a replacement runs and
   what it does. This answers the open question.
4. Library runs its replacement inside `VideoLookup.Reload` through one
   delegate that `Browser` gives to its `DataDownload`.
5. Every replacement runs off the window thread. Today the city data installs
   on the window thread.
6. The shared failure mapping is in `DataDownload`. A dataset translates only
   the exceptions of its own library.
7. `DataDownload` keeps its name, as the plan's table of owners states it.

## Gate step 1: the findings, read again

Stage 9 marks no finding "to re-read". The gate read the three ranges that
the plan says the owner absorbs, because the design rests on them. The rule
of each pair, in one sentence:

| Rule | City data, `Shared/Geolocation.cs` | IMDb data |
| --- | --- | --- |
| Start: one download at a time; register the work; show it; clear it at the end | `Download` (149) | `Browser.Download`, `Library/Browser/Information.cs` (281) |
| Stop as the person's cancel, or as an interruption that is reported | `CancelDownload`, `InterruptDownload`, `StopDownload` (180 to 237) | The same three names, `Information.cs` (333 to 348) |
| Delete is maintenance work that cannot be cancelled and reports no success | `Delete` (188) | `Browser.DeleteData`, `Information.cs` (373) |
| A file that exists under its final name is whole: fetch to `.part`, then rename | `Fetch` (297) | `Dataset.Fetch`, `Library/Imdb/Dataset.cs` (483) |
| The installed file is replaced by one rename of the built `.tmp` file while no read runs; another program holding it is `InUse` | `Install`, `Replace` (367 to 397) | `Dataset.Install`, `Dataset.Delete` (175, 220) |
| A build that was not installed is deleted; a held file is left for the next build | `Discard` (408) | `Dataset.Discard` (208) |
| A failed build becomes a `DownloadException` by one mapping | `Build`, catch blocks (272 to 287) | `Dataset.Build`, catch blocks (144 to 170) |
| A failure names its data before it is reported | `Report` (435) | The `report` lambda, `Information.cs` (317) |

All eight are kept. Parts of the plan's ranges are not copies and stay with
their dataset:

- `Geolocation.Dispose` (214), `NewVersion` (400) and `Open` (422). They are
  the city reader.
- The local function `Get` inside `Geolocation.Fetch` (324) and the fallback
  to the previous month. They are the city data's source.
- The body of each `Build` before its catch blocks. It is the build.
- The Library half of `Browser.ReplaceData` (`Information.cs` 407):
  `VideoLookup.Reload`, `Refresh` and `ChangedProviders`.

## Gate step 2: the open question

**Question: how does a shared owner replace a file whose reader is open?**

**Answer: the dataset closes and reopens its reader, under its own write
lock, in one member, `IDataset.Replace(Action change)`. `DataDownload` calls
that member with the file change. The city data keeps its close and reopen
inside the lock, in the same method as today.**

### Every reader of both datasets

| Reader | What it holds | Who calls it |
| --- | --- | --- |
| `Geolocation.Find` (119) | The read lock for one lookup. The reader `_cities` stays open between lookups; it maps the file into memory. | `Shell/Inspector/Peers.cs`: `Locate` (415) inside `Task.Run` in `LocateOrigin` (436), and the reply preparation inside `Task.Run` (612, 616). Capture: `Shell/MainWindow/CapturePeers.cs` 1127, 2045, on the window thread. |
| `Geolocation.Installed` (59), `IsCurrent` (66) | Nothing. Each makes a `FileInfo`. | `Peers.HasCities` (349), `DatasetRow`, `MainWindow/Datasets.cs` |
| `Geolocation.Version` (50) | Nothing | `Shell/Inspector.cs` 321, `Peers.cs` 22, 407, 432 to 443, 607 |
| `Dataset.Read` (391) | The read lock for one query. It opens a connection without pooling and closes it, so no handle stays open between reads. | `Imdb.Provider.Search` and `Describe` (`Library/Imdb/Provider.cs` 25, 86), `Dataset.Ratings` (245) and `RatingsByName` (292), which `Store.Rate` calls (`Library/Store.cs` 107, 115), and `Dataset.CountTitles` (83) |
| `Dataset.IsInstalled` (65) | Nothing. It is a flag. | `VideoLookup.Available` (236), `Store.Rate` (105), `Browser.ChooseAutomaticDownload` (`Information.cs` 162), `Ratings`, `RatingsByName` |
| `Dataset.Inspect` (75), `IsCurrent` (72) | Nothing | `Information.cs` 258, 259 only |

So the two datasets differ in one fact. The IMDb data has no open handle
between reads, and the write lock alone frees the file. The city data keeps a
reader open, so a replacement must close it, change the file and open the
file that remains, all inside the write lock (`Geolocation.Replace`, 377 to
397). It must also clear the kept places and raise `Version`
(`NewVersion`, 400).

### Every wait, and the event that ends it

| Wait | Thread today | Ends when |
| --- | --- | --- |
| A city lookup enters the read lock (`Find`) | Thread pool; the window thread in capture journeys only | A running replacement ends: the reader is closed, one rename or delete returns, and the reader is open again. Opening maps the file and reads none of it. |
| A city replacement enters the write lock (`Replace`) | **Install: the window thread.** `Download` (149) is called on the window thread, `DataDownload.Run` awaits the build without `ConfigureAwait(false)`, and the install lambda (160) calls `Install` directly. Delete: the thread pool (`Task.Run`, 201). | Each lookup in progress returns. A lookup is one walk of a tree in mapped memory. |
| An IMDb read enters the read lock (`Read`) | Thread pool | A running replacement ends: one rename, or the deletes. |
| An IMDb replacement enters the write lock (`Install`, `Delete`) | Thread pool, inside `VideoLookup.Reload` (`Library/VideoLookup.cs` 195, `Task.Run` at 205) | Each read in progress returns. Lookups are already stopped by the change. A rating read of `Store.Rate` or the title count can still run; each is one query. |
| An IMDb replacement waits for Library's change (`VideoLookup.Change`, 289) | Thread pool | An earlier provider change or reset ends, the running batch or sweep sees its cancelled context, and the lookup in progress ends; or Library closes. The download still shows its last build phase and can still be cancelled: the step that starts Finishing checks the token first (`DataDownload.Install`, 141). |
| A stop is awaited (`CancelDownload`, `InterruptDownload`): by `Geolocation.Detach` (101), `Browser.Detach` (`Library/Browser.cs` 1324), `Browser.SuspendProtection` (`Information.cs` 359) and `Features.ResetProtection` (`Shell/Features.cs` 596) | Window thread, asynchronously | The work ends (`CancellableWork.Completion`): the build sees the token, the unfinished build is discarded and the operation is disposed. The network read, the unpack loop, the import's rows and SQLite's progress handler all check the token. Three steps cannot be cancelled and end by themselves: the IMDb flush to disk, the check that the unpacked city file opens, and a replacement that already shows Finishing. |
| A reset drains the IMDb download before it takes Library's change (`SuspendProtection`) | Window thread, asynchronously | The stop above. The order is required by [local data](../../contracts/search-protection/local.md): otherwise the install waits for the change that the reset holds, and the reset waits for the download. |
| The deletion dialog awaits `Delete` (`Shell/MainWindow/Datasets.cs` 125) | Window thread, asynchronously | The replacement ends. The one-dialog slot of `Interact` is held until then. |

One wait is on the window thread: the city install. The rule that it breaks
is already written at the IMDb copy: "Replacing a file can wait for a read in
progress, so it never runs on the calling window thread"
(`VideoLookup.Reload`, 203). The shared owner applies that rule to both
datasets. After the stage no replacement waits on the window thread.

### Why the lock stays in the dataset

The gate compared two places for the lock:

| Candidate | Result |
| --- | --- |
| `DataDownload` owns the lock and offers `Read`. | Rejected. Every reader is in the dataset, so the dataset would need its `DataDownload` before that object exists, because `DataDownload` takes the dataset. The read lock and the reads would be in different files. `DataDownload` would also have to call the dataset twice inside the lock, once to close and once to reopen, and the pairing would rest on call order. |
| The dataset owns the lock and one member, `Replace(Action change)`, that runs a file change under it. | **Chosen.** The lock, the reads, the close and the reopen are in one type. The city data's `Replace` keeps its body. `DataDownload` cannot forget half of the pair, because the pair is one call. |

## Gate step 3: the design

### The candidates

The plan asks for more than one design. Four shapes were compared by one
test: does each rule of [step 1](#gate-step-1-the-findings-read-again) get
one place to change?

| Candidate | What it is | Result |
| --- | --- | --- |
| A. One `DataDownload` for each dataset, and an `IDataset` that the dataset implements | The host owns the workflow; the dataset states what differs | **Chosen.** Each of the eight rules has one place. It is the host and plugin shape of the [code style](../../../AGENTS.md#code-style), rule 4. |
| B. `DataDownload` stays one running download and gains static helpers | Each dataset keeps its `Download`, `Delete` and stop methods and calls more helpers | Rejected. Start, stop and delete stay written twice. Only fetch and the mapping become one. |
| C. An abstract base class that both datasets inherit | The workflow is in the base; a dataset overrides the steps | Rejected. `Geolocation` also serves the country data and the lookups, so it is more than a dataset. The IMDb replacement must run inside `VideoLookup.Reload`, which `Imdb.Dataset` cannot reach, because `Features.Observe` creates it before the `VideoLookup`. |
| D. `IDataset` stays what Settings sees; `Browser` and `Geolocation` keep implementing it and pass each member to a shared object | The view does not change | Rejected. Eight members that only pass a call on, twice. |

Three smaller choices:

- **What a build receives.** `Build(string path, Action<WorkDescription,
  WorkProgress> report, CancellationToken cancellation)` takes three plain
  values. A handle type that also offers the fetch was rejected: it is a new
  type, and `app/tests/ImdbChecks.ps1` calls `Build` without a
  `DataDownload`.
- **How the dataset takes part in a replacement.** One member,
  `Replace(Action change)`. Two members, `Install` and `Delete`, were
  rejected: the rename and the delete would stay written in each dataset.
- **The name of the owner.** `DataDownload` stays, because the approved
  table of owners names it and "the download" is also the word for
  downloaded data. `DataFile` was considered and rejected for that reason
  alone. The members follow from the name: `Start` and `CanStart`, not
  `Download` and `CanDownload`, because a member does not repeat its type
  ([naming](../../agents/naming.md#carry-only-the-necessary-distinctions)).

### `IDataset`, in `Shared/IDataset.cs`

```csharp
/// <summary>
/// Published data that the person downloads and keeps on this computer as a
/// replaceable copy: the IMDb data and the city data. A dataset states what
/// is its own: its names, its file, its build and how its reads give way to
/// a replacement. <see cref="DataDownload"/> owns everything else.
/// </summary>
internal interface IDataset
{
    /// <summary>The text section whose keys name the data, its row and its dialogs.</summary>
    string Section { get; }

    /// <summary>The publisher's name, which a failure's text names.</summary>
    string Name { get; }

    /// <summary>The installed file's full path, or null while the dataset serves no folder.</summary>
    string? FilePath { get; }

    /// <summary>The description a download starts with.</summary>
    WorkDescription Downloading { get; }

    /// <summary>The description of the replacement, which cannot be cancelled.</summary>
    WorkDescription Installing { get; }

    /// <summary>The number of records the row names, or null when the data has no count a person reads.</summary>
    Task<long>? Count { get; }

    /// <summary>
    /// Files beside the data that only a build or an earlier version of the
    /// app used. They are deleted after an installation and with the data.
    /// </summary>
    IReadOnlyList<string> Leftovers { get; }

    /// <summary>Whether a file written at <paramref name="written"/> is most likely the one a new download would transfer.</summary>
    bool IsCurrent(DateTime written);

    /// <summary>
    /// Fetches the data and writes the complete file to <paramref name="path"/>,
    /// which is not the installed file. A cancel can end it with the error
    /// that the interruption caused instead of a cancellation.
    /// </summary>
    Task Build(string path, Action<WorkDescription, WorkProgress> report, CancellationToken cancellation);

    /// <summary>
    /// Runs <paramref name="change"/>, which replaces or deletes the installed
    /// file, while no read runs and the dataset holds the file closed. The
    /// dataset then serves the file that remains, also when the change failed.
    /// </summary>
    void Replace(Action change);
}
```

`FilePath` has two words because `Path` and `File` are the `System.IO` types
that both implementations use on most lines; a property with either name
would hide the type inside the class.

The interface is allowed by [naming](../../agents/naming.md#interfaces):
`DataDownload` selects between two real implementations.

### `DataDownload`, in `Shared/DataDownload.cs`

```csharp
/// <summary>
/// The copy of one dataset on this computer and the work that changes it:
/// the download, with its cancel and its interruption, the replacement of
/// the installed file and its deletion. Settings and the status bar observe
/// it. Its members are called on the window thread, and it raises its events
/// there.
/// </summary>
internal sealed class DataDownload : INotifyPropertyChanged
{
    /// <param name="replace">
    /// Runs a replacement off the window thread when the owner must stop
    /// other work around it; by default <see cref="Task.Run(Action)"/>.
    /// </param>
    internal DataDownload(IDataset dataset, BackgroundWork work, Func<Action, Task>? replace = null);

    /// <summary>Raised for <see cref="Installed"/>, <see cref="Work"/> and <see cref="CanStart"/>.</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>A failed download or deletion, with the data's name set.</summary>
    internal event EventHandler<Exception>? Failed;

    internal string Section { get; }            // dataset.Section
    internal FileInfo? Installed { get; }       // the file at dataset.FilePath, or null when it is missing
    internal bool IsCurrent { get; }            // Installed and dataset.IsCurrent(its write time)
    internal Task<long>? Count { get; }         // dataset.Count
    internal BackgroundWork.Operation? Work { get; }   // the running download, or null
    internal bool CanStart { get; }             // no download runs, none is suspended, and FilePath is not null

    /// <summary>Downloads and installs the data; returns whether the new data is installed.</summary>
    internal Task<bool> Start();

    /// <summary>Stops the running download at the person's request, and completes when it has ended.</summary>
    internal Task Cancel();

    /// <summary>Stops the running download as one the person did not ask for, which reports it, and completes when it has ended.</summary>
    internal Task Interrupt();

    /// <summary>Interrupts the running download and starts no other until <paramref name="apply"/> has ended.</summary>
    internal Task Suspend(Func<Task> apply);

    /// <summary>Deletes the installed data and its leftovers. A failure is reported through <see cref="Failed"/>.</summary>
    internal Task Delete();

    /// <summary>Raises a change of every property, after the dataset's <see cref="IDataset.FilePath"/> changed.</summary>
    internal void Refresh();

    /// <summary>
    /// Downloads the reply of <paramref name="call"/> to a file beside
    /// <paramref name="path"/> and renames it when it is complete, so a file
    /// at <paramref name="path"/> is whole.
    /// </summary>
    internal static Task Fetch(
        ProviderHttp http,
        ProviderCall call,
        string path,
        Action<WorkProgress> progress,
        CancellationToken cancellation
    );
}
```

`Fetch` takes five values. Four are the list of `ProviderHttp.Download`
(`Shared/Network/ProviderHttp.cs` 112), which it calls; the fifth is the HTTP
owner. No type owns the five, and a type made for one call would be an
options bag, which [naming](../../agents/naming.md#inputs-and-results)
rejects.

What the implementation holds, so that the behavior of today stays:

- **One run.** The state of one download is today's `DataDownload` fields:
  the `CancellableWork`, `_interrupted`, `_installed` and their gate (13 to
  31). They become one private nested type, created by `Start`, so a second
  download starts clean. `Run`'s three catch filters (79 to 99), its discard
  in `finally` (100 to 117), `Report` (121), the Finishing step of `Install`
  (141 to 149) and `Stop` (158 to 171) keep their bodies and comments.
- **`Start`.** It reads `dataset.FilePath` once. The build file is that path
  plus `".tmp"`. It then does, in order: `dataset.Build` inside the failure
  mapping below; the token check; the replacement; `Outcome = Completed`.
  The replacement is
  `replace(() => { finishing step; Change(() => File.Move(building, path, overwrite: true)); installed = true; delete the leftovers; })`.
  The leftovers are deleted in one `try` that ignores `IOException` and
  `UnauthorizedAccessException`, as `Dataset.Install` does (195 to 204).
- **`Change(Action change)`**, private: calls `dataset.Replace(change)` and
  turns an `IOException` or `UnauthorizedAccessException` into
  `DownloadException(DownloadFailure.InUse, error)`.
- **`Delete`.** It returns when a download runs or `FilePath` is null. It
  registers `MaintenanceWork.DeleteDataset` as Finishing, runs
  `replace(() => Change(() => { delete each leftover; File.Delete(path); }))`,
  sets `Outcome = Completed` and `ReportsSuccess = false`, and on any
  exception sets `operation.Error` and reports it. The comment "The Settings
  row shows that the data is gone." moves with it.
- **Discard.** `File.Delete(building)` on the thread pool, ignoring a held
  file. The comment "A build that another program holds is deleted by the
  next build." moves with it.
- **The failure mapping.** Around `dataset.Build`:
  `ReplyException` becomes `Failure(error)`, which is today's static method
  (42) and becomes private; `InvalidDataException` becomes `Damaged`; an
  `IOException` for which `IsDiskFull` holds becomes `DiskFull`; any other
  `IOException` or `UnauthorizedAccessException` becomes `Failed`. A
  `DownloadException` or a `RequestBlockedException` passes unchanged.
  `IsDiskFull` (34) becomes private.
- **The report.** One private method sets
  `Subject ??= dataset.Name` on a `DownloadException` and raises `Failed`.
- **Changes.** `Work` and `CanStart` change when a download starts and when
  it ends, and after a stop request, as `ShowDownload` is called today.
  `Installed` changes after each replacement, whether it succeeded, and at
  the end of `Delete`. `Suspend` raises `CanStart` at its start and its end.
- **Threads.** `DataDownload` awaits without `ConfigureAwait(false)`, so its
  continuations, the `replace` delegate and both events run on the window
  thread when the caller was on it. The file change runs inside `replace`,
  off the window thread.

### What each dataset keeps

**`Geolocation`** (`Shared/Geolocation.cs`) implements `IDataset` and owns
one `DataDownload` for its whole life:

- `internal DataDownload Download { get; }`, created in an explicit
  constructor `Geolocation(ProviderHttp http, BackgroundWork work)`, because
  a primary constructor cannot pass `this`. The constructor keeps its two
  parameters, which `app/tests/SourceChecks.ps1` 979 uses.
- `Section` stays. `Name` is today's constant (22) as a property.
  `FilePath` is `_directory` joined with `CityFileName`, or null. `Count` is
  null. `Leftovers` is empty. `Downloading` and `Installing` are
  `WorkDescription.City` with `CityPhase.Downloading` and `Installing`.
  `IsCurrent(written)` is today's month rule (66).
- `Build(path, report, cancellation)` keeps the body of today's `Build`
  (241) without its first four catch blocks. The archive name comes from
  `_directory`, and `_policy` is read at the start. `finally` still deletes
  the archive. `Unpack` (341) wraps its last line:
  `InvalidDatabaseException` becomes `DownloadException(Damaged)`, because
  the exception belongs to the MaxMind library.
- `Fetch` (297) keeps the two months and its comment, and each `Get` calls
  `DataDownload.Fetch(http, call, archive, progress, cancellation)`.
- `Replace(Action change)` is today's `Replace` (377) without its catch
  block, made public for the interface.
- `Attach` and `Detach` keep their bodies. Each ends with
  `Download.Refresh()` in place of `Changed(string.Empty)`. `Detach` starts
  with `await Download.Cancel()`.
- Deleted: `Download`, `CancelDownload`, `InterruptDownload`, `StopDownload`,
  `Delete`, `Install`, `Discard`, `Building`, `Report`, `ShowDownload`,
  `Changed`, `Installed`, the property `IsCurrent`, `DataWork`,
  `CanDownload`, `PropertyChanged`, `Failed`, the field `_download`, and
  `INotifyPropertyChanged` from the type.

**`Imdb.Dataset`** (`Library/Imdb/Dataset.cs`) implements `IDataset`:

- `Section` is `Provider.CatalogId`. `Name` is `"IMDb"`, and
  `Provider.Option` (`Library/Imdb/Provider.cs` 14) passes `dataset.Name`,
  so the name is written once. `FilePath` is `_path`. `Count` is today's
  `CountTitles` (83) as a property. `Downloading` and `Installing` are
  `WorkDescription.Imdb` with `ImdbPhase.Downloading` and `Installing`.
  `IsCurrent(written)` is the one-day rule, and `IsRecent` (477) calls it.
- `Leftovers` is the archives (`Archives`, 472) and each `imdb*.db` file in
  the folder other than `_path`, which is the expression of `Install` (198
  to 202).
- `Build(path, report, cancellation)` keeps today's body. `Import` takes
  `path` in place of `Building` (62), which is deleted. A local function
  turns an `ImdbPhase` into `new WorkDescription.Imdb(phase)` for `report`.
  Its catch blocks become: `InvalidDataException`, remove the archives and
  rethrow; `DownloadException` with `UnknownFormat`, remove the archives and
  rethrow; `SqliteException` with `SQLITE_FULL`, throw
  `DownloadException(DiskFull)`; any other `SqliteException`, throw
  `DownloadException(Failed)`. The comment "A damaged or changed archive
  would fail again, so it is not kept." stays.
- `Fetch` (483) builds the request and the call, and calls
  `DataDownload.Fetch`.
- `Replace(Action change)`: enter the write lock; run `change`; in
  `finally`, set `_installed = File.Exists(_path)` and `_titles = null`, and
  leave the lock.
- `IsInstalled` and `Read` stay. Stage 7 changes the inside of `Read`.
- Deleted: `Install`, `Discard`, `Delete`, `Inspect`, the property
  `IsCurrent`, `Building`.

**Library** (`Library/Browser/Information.cs`, `Library/Browser.cs`):

- `internal DataDownload? Download { get; private set; }`, null before
  Library attaches. `Browser.Attach` (`Browser.cs` 472) creates it after it
  sets `_videos`, when `DataProvider` has a dataset:
  `new DataDownload(dataset, _work, change => ReplaceData(videos, option, change))`.
  It subscribes `Failed` with the two lines of today's report
  (`Information.cs` 319 to 321): set `Provider ??= option` on a
  `VideoException`, then `ReportCommand(error)`.
- `ReplaceData(VideoLookup videos, ProviderOption option, Action change)`:
  `CancelInformation()`; then
  `await videos.Reload(option, change, () => _automaticOnInstall)`; in
  `finally`, `Refresh()` and `ChangedProviders()`. Its comment stays.
- `PrepareAutomaticDownload()` replaces `Download(bool)`, whose only caller
  passes `true` (`Shell/MainViewModel/Actions.cs` 548). It returns false
  unless `Download` can start; sets `_automaticOnInstall = true`; returns
  `await download.Start()`; and sets the flag to false in `finally`. The
  flag is then true only while that download runs, so a deletion never sees
  it. The summary of `Download(bool)` (271 to 280) moves to it.
- `SuspendProtection(apply, includeDataset)` keeps its guard and its flag.
  Its body becomes: with `includeDataset` and a `Download`,
  `await download.Suspend(() => videos.Suspend(apply))`; otherwise
  `await videos.Suspend(apply)`.
- `ChooseAutomaticDownload` (170) reads `Download is not { CanStart: true }`.
- `Stop` (`Browser.cs` 1295) calls `_ = Download?.Cancel()`. `Detach`
  (1311) takes `Download` into a local and sets it to null where it sets
  `_videos` to null, and awaits the local's `Cancel()` where it awaits
  `CancelDownload()` today.
- Deleted: the eight `IDataset` members (257 to 265) and `IDataset` from the
  type (`Browser.cs` 16), `DataWork`, `HasDataWork`, `CanDownload`,
  `Download(bool)`, `CancelDownload`, `InterruptDownload`, `StopDownload`,
  `DeleteData`, `ShowDownload`, the field `_download`. No file outside
  `Information.cs` names `HasDataWork`.

### Every caller, and the member it calls

| Caller | Today | After |
| --- | --- | --- |
| `Shared/DatasetRow.cs` | `IDataset` and its `Installed`, `Count`, `DataWork`, `CanDownload`, `Section` | `DataDownload` and its `Installed`, `Count`, `Work`, `CanStart`, `Section` |
| `Settings/SettingsPage.xaml.cs` 53, 54, 541 to 543 | `EventHandler<IDataset>`; `Main.DownloadData(dataset)` | `EventHandler<DataDownload>`; `Main.DownloadData(download)` |
| `SettingsPage.xaml.cs` `ShowImdb` (251) | `Main.Library.DataProvider is null ? null : Main.Library` | `Main.Library.Download` |
| `SettingsPage.xaml.cs` 545 | `CityRow.Show(Main.Geolocation, …)` | `CityRow.Show(Main.Geolocation.Download, …)` |
| `Shell/MainWindow/Datasets.cs` `ConfirmDownload`, `ConfirmDataDeletion` | `IDataset`; `CanDownload`; `Delete()` | `DataDownload`; `CanStart`; `Delete()` |
| `Shell/MainWindow/Workspace.cs` 231 | `ConfirmDownload(Model.Library, Model.PrepareAutomaticDownload)` | The same call with `Model.Library.Download`, when it is not null |
| `Workspace.cs` 233, 235 | Lambdas over an `IDataset` | The same lambdas over a `DataDownload` |
| `Shell/MainViewModel/Actions.cs` `DownloadData`, `AnnounceData` (541, 550) | `dataset.Download()`, `dataset.Section` | `download.Start()`, `download.Section` |
| `Actions.cs` `PrepareAutomaticDownload` (547) | `Library.Download(enableAutomaticDownload: true)` | `Library.PrepareAutomaticDownload()`, announced with `Library.Download`; nothing when that is null |
| `Shell/MainViewModel.cs` 388 | `Geolocation.Failed += …` | `Geolocation.Download.Failed += …` |
| `Shell/Inspector.cs` `DownloadCities` (249 to 252) | `DownloadData(Geolocation)`, `Geolocation.CanDownload` | `DownloadData(Geolocation.Download)`, `Geolocation.Download.CanStart` |
| `Inspector.cs` 316 | `owner.Geolocation.PropertyChanged += …` | `owner.Geolocation.Download.PropertyChanged += …`; the handler does not change |
| `Shell/Inspector/Peers.cs` `HasCities` (349) | `Geolocation.Installed is not null` | `Geolocation.Download.Installed is not null` |
| `Shell/Features.cs` `ConfigureRoute` (252, 253) | `Library.InterruptDownload()`, `Geolocation.InterruptDownload()` | `Library.Download?.Interrupt()`, `Geolocation.Download.Interrupt()` |
| `Features.cs` `ResetProtection` (596) | `await Geolocation.InterruptDownload()` | `await Geolocation.Download.Interrupt()` |
| `Features.cs` 262, 597 `Library.SuspendProtection`, 608 `Library.DataProvider`, 172 `Geolocation.Attach`, 697 `Geolocation.Detach` | | No change |
| `Library/VideoLookup.cs` `Reload` (195), `Available` (236); `Library/Store.cs` 105 to 115; `Library/IProvider.cs` `ProviderOption.Dataset` (78) | | No change |

A local or a parameter that holds a `DataDownload` is named `download`, not
`dataset`, in every file of this table. The control keeps the name
`DatasetRow`, and the two rows keep `ImdbRow` and `CityRow`, so the
automation identifiers `ImdbRowDownload` and `CityRowDelete` do not change.

The Inspector follows the city data's `Version` through the `DataDownload`
events: `Attach` and `Detach` end with `Refresh`, and a replacement raises
`Installed` on the window thread after `Replace` has raised `Version`.

### Settled as

Each of these is a difference between the copies, or a choice the plan does
not make.

1. **Every replacement runs off the window thread.** The city install moves
   there. Reason: [code style](../../../AGENTS.md#code-style), rule 1, and
   the rule already written at `VideoLookup.Reload`.
2. **A reset holds new IMDb downloads only when it includes the IMDb data.**
   Today `SuspendProtection` also disables Download while another provider
   alone is reset, and lets a running download continue. One rule is
   simpler: `Suspend` interrupts and holds, and it runs only when the reset
   includes the dataset. This agrees with
   [local data](../../contracts/search-protection/local.md): "Reset for IMDb
   or all providers first prevents another dataset command from starting".
3. **Delete also removes IMDb data files that an earlier version of the app
   built.** Today only an installation removes them. Reason: `Leftovers` is
   one list, and a person who deletes the data expects the space back.
4. **The city data is interrupted, not held, by a reset and a route change.**
   This stays as it is. `Features` owns that order
   (`ConfigureRoute`, `ResetProtection`), and holding the city data needs a
   new shape for both methods, which the stage does not otherwise touch.
5. **`Fetch` stays in `DataDownload`,** as the plan states, not in
   `ProviderHttp`. Stage 8 owns `ProviderHttp` in this batch.
6. **The phase lists stay two enums.** `ImdbPhase` and `CityPhase` share two
   member names and have different text keys, so they only look alike.

## Gate step 4: every consumer of every changed name

Searched: all of `app/src`, which includes the 25 capture journeys and the
`#if CAPTURE` blocks, all XAML, `app/tests/*.ps1`, and `docs`.

| Name | Consumers |
| --- | --- |
| `IDataset` | `Shared/IDataset.cs`, `DatasetRow.cs`, `Geolocation.cs`, `Library/Browser.cs` 16, `Browser/Information.cs`, `Settings/SettingsPage.xaml.cs`, `Shell/MainViewModel/Actions.cs`, `Shell/MainWindow/Datasets.cs`. Documents: `docs/contracts/peer-location.md` 39. No capture journey, no XAML, no test script. |
| `DataDownload` | `Shared/DataDownload.cs`, `Geolocation.cs`, `Library/Imdb/Dataset.cs` (`Failure`, `IsDiskFull`), `Browser/Information.cs`. Test: `app/tests/SourceChecks.ps1` 996 to 998 constructs it, passes it to `Geolocation.Build` and disposes its `Operation`. |
| `Geolocation.Build` | `SourceChecks.ps1` 997, by reflection |
| `Geolocation.Installed`, `CanDownload`, `PropertyChanged`, `Failed`, `InterruptDownload` | The rows of the caller table. Capture journeys name only `Find` and `Version` (`CapturePeers.cs` 1127, 1249, 2045), which do not change. `Peers.HasCities` has a `#if CAPTURE` line beside the edited line. |
| `Dataset.Build`, `Install`, `Discard` | `Browser/Information.cs`. Test: `app/tests/ImdbChecks.ps1` 87, 94, 133, by reflection. Outside the repository: `artifacts/imdb-performance/Measure.ps1` 58 and `replay/Profile.ps1` 62 call `Build` with the old two arguments. |
| `Dataset.Inspect`, `CountTitles`, `IsCurrent`, `Delete` | `Browser/Information.cs` only |
| `Dataset` constructor | Unchanged. `SourceChecks.ps1` 76, `ImdbChecks.ps1` 12, `Shell/Features.cs` 152. |
| `Browser.Download(bool)`, `DeleteData`, `CancelDownload`, `InterruptDownload`, `CanDownload`, `DataWork`, `HasDataWork` | `Browser.cs` 1295, 1324; `Information.cs`; `Shell/Features.cs` 252; `Shell/MainViewModel/Actions.cs` 548. `docs/contracts/background-work.md` 246 names `CancelDownload`, and 296 lists `HasDataWork` as a counter the footer never reads, which stays true. |
| `MaintenanceWork.DeleteDataset`, `WorkDescription.City`, `WorkDescription.Imdb`, `CityPhase`, `ImdbPhase` | Unchanged names. `CaptureUpdates.cs` 87 builds an IMDb observation and does not change. |

No text key changes, so no catalogue changes. No XAML changes.

### The two check scripts

**`app/tests/SourceChecks.ps1`**, the City entry of `$replyFeatures` (995 to
999). Its subject is "the city data reports the same failure and records the
same hold as Library and Subtitles". The mapping is now in `DataDownload`,
so the entry runs a download:

- After line 979, call `Attach` with `$directory` and `$policy`, and read
  `$geolocation`'s `Download`.
- Subscribe to `Failed` with a compiled handler, as `ImdbChecks.ps1` does
  for progress (62 to 79): a small `Add-Type` class with a field and a method
  `(object, Exception)`, bound with `[Delegate]::CreateDelegate` to
  `EventHandler[Exception]` and added through the event's add method. A
  script block is not used, because the event can arrive on a thread that
  has no PowerShell runspace.
- `Send` clears the field, invokes `Start`, and throws the field's exception
  when it is set.
- Line 980 (`$cityPhase`) and the `Operation` disposal go.

The expected values in `$replyCases` are those that stage 2 leaves.

**`app/tests/ImdbChecks.ps1`**. Its subject is the import, and it keeps
calling `Build` directly:

- `Build` takes three arguments: the path `imdb-2.db.tmp` in `$directory`,
  the report and the token.
- The report delegate is `Action<WorkDescription, WorkProgress>`.
  `ImdbProgress.Report` reads the description's `Phase` property by
  reflection in place of `phase.ToString()`.
- Line 94 becomes `[IO.File]::Move` of the `.tmp` file to `$installed`.
- Lines 133 and 134, the `Discard` call and its check, go. `Discard` is now
  one `File.Delete` in `DataDownload`, which reading settles. The check that
  a cancelled build left the installed file unchanged (135) stays.

## Gate step 5: files, contracts, cost

### Files the implementer may edit

Product code:

- `Shared/DataDownload.cs`, `Shared/IDataset.cs`, `Shared/Geolocation.cs`,
  `Shared/DatasetRow.cs`
- `Library/Imdb/Dataset.cs`, `Library/Imdb/Provider.cs`,
  `Library/Browser.cs`, `Library/Browser/Information.cs`
- `Settings/SettingsPage.xaml.cs`
- `Shell/Features.cs`, `Shell/Inspector.cs`, `Shell/Inspector/Peers.cs`,
  `Shell/MainViewModel.cs`, `Shell/MainViewModel/Actions.cs`,
  `Shell/MainWindow/Datasets.cs`, `Shell/MainWindow/Workspace.cs`

Test scripts: `app/tests/SourceChecks.ps1`, `app/tests/ImdbChecks.ps1`.

Documents: `docs/contracts/peer-location.md`, `background-work.md`,
`imdb-datasets.md`, `architecture.md`.

A file that is not in this list is edited only when the compiler names it,
and the batch report says which.

### Contract changes, as proposed text

**`docs/contracts/peer-location.md`, "City data in Settings"**, the first two
sentences become:

> The City data row sits under IMDb data in Plugins and is the same control,
> `DatasetRow`. Each row shows one `DataDownload`, the owner of a dataset's
> copy on this computer: it runs the download with its cancel and its
> interruption, replaces the installed file, deletes it and reports a
> failure. `Imdb.Dataset` and `Geolocation` each implement `IDataset`, which
> states what differs: the names, the file, the build, and how the dataset's
> reads give way to a replacement. One pair of dialogs, worded from the
> dataset's text section, asks before an update or a deletion.

In the same paragraph, "moves it over the installed copy under the lookup
lock" becomes "moves it over the installed copy under the lookup lock, off
the window thread".

**`docs/contracts/background-work.md`, "App-wide operations"**, the first
row becomes:

> | IMDb and city data download and install — [DataDownload](../../app/src/Shared/DataDownload.cs), with [Imdb.Dataset](../../app/src/Library/Imdb/Dataset.cs) and [Geolocation](../../app/src/Shared/Geolocation.cs) | One operation from acceptance through final cleanup. `DataDownload.Cancel` and the work list's Cancel stop it; the IMDb phases and their cancellation are in [import execution](imdb-datasets.md#import-execution-and-sql-design). Settings and footer consume the same operation. |

In the table below it, the row "Database recovery, reset protection and
dataset deletion" links `DataDownload` in place of `Browser.Information`.

**`docs/contracts/imdb-datasets.md`, "Provider"**, the item that starts
"`Dataset` owns the download" becomes:

> - `Dataset` owns the sources, the import and the reads. `DataDownload`
>   owns the download's lifetime, the replacement and the deletion, for the
>   IMDb data and the city data alike. `Dataset.Replace` runs the file change
>   under its write lock, and Library runs it inside `VideoLookup`'s change,
>   so no lookup has the file open at that moment.

In "Download and import", step 3, "**Delete data** deletes kept archives
too" becomes "**Delete data** deletes kept archives too, and data files that
an earlier version of the app built".

**`docs/contracts/architecture.md`, "Ownership decisions"**, one new item:

> - `DataDownload` owns the copy of a dataset on this computer for the IMDb
>   data and the city data, and Settings shows it. `IDataset` states what
>   differs. The lock between reads and a replacement stays in the dataset,
>   in `IDataset.Replace`, because every read is there and the city data
>   must close and reopen its reader inside that lock; a lock in
>   `DataDownload` would put the reads and their lock in different files.
>   Library gives its `DataDownload` the step that runs a replacement inside
>   `VideoLookup.Reload`, because `Imdb.Dataset` is created before the
>   lookup and cannot reach it. A base class for datasets was rejected:
>   `Geolocation` is more than a dataset.

The plan's table of owners keeps its row for stage 9.
[CONTEXT.md](../../../CONTEXT.md) needs no change: the stage adds no product
term.

### What changes for a person

Three differences exist, and the batch report names them:

- The city data's installation no longer runs its file change on the window
  thread. A person sees no difference unless a lookup or the file system was
  slow at that moment; then the window no longer waits.
- While request protection is reset for one provider that is not IMDb, the
  IMDb row's Download or Update button stays enabled. Today it is disabled
  until that reset ends.
- Delete data for IMDb also deletes an `imdb*.db` file that an earlier
  version of the app built.

### Done when

- A search of `app` for `CancelDownload`, `InterruptDownload`,
  `StopDownload`, `HasDataWork` and `DeleteData` finds nothing.
- A search for `MaintenanceWork.DeleteDataset`, `".part"`,
  `DownloadFailure.InUse` and `DownloadFailure.Interrupted` in `app/src`
  finds `Shared/DataDownload.cs` only.
- A search for `IDataset` finds its file, `DataDownload.cs` and the two
  implementations.
- A search for `EnterWriteLock` in `Geolocation.cs` and `Dataset.cs` finds
  `Attach`, `Detach`, `Dispose` and one `Replace` in each file.
- The four contracts hold the text above.

### Cost

- **Agent work:** 3 to 4 hours for the implementing session: 16 product
  files, of which four are rewritten in part (`DataDownload.cs`,
  `Geolocation.cs`, `Dataset.cs`, `Information.cs`) and twelve change a few
  lines; two scripts; four documents. One hour for the independent review.
- **Builds:** none of its own. The stage joins batch C's one app build with
  `-Capture`.
- **Checks on that build:** `SourceChecks.ps1` and `ImdbChecks.ps1`, the two
  scripts whose subject changed. No capture review: no layout and no text
  changes.

## What reading could not settle

- **The whole path in the running app.** No existing check starts a
  download, cancels it, installs it or deletes it through `DataDownload`.
  `SourceChecks.ps1` covers a failed city download, and `ImdbChecks.ps1`
  covers the import. The IMDb path can be run without a request: with the
  three archives less than a day old in the data folder, `Build` imports
  them and sends nothing (`Dataset.Build`, 114). The city path cannot be run
  without a request to DB-IP.
- **The two scripts outside the repository.**
  `artifacts/imdb-performance/Measure.ps1` and `replay/Profile.ps1` call
  `Dataset.Build` with two arguments and will fail after the stage. They are
  in build output, which the implementer does not edit. The lead decides
  who updates them.
- **What stages 7 and 8 leave.** Stage 7 changes `Dataset.Read` and
  `Connect`. Stage 8 names `DataDownload.Failure` (37) as a copy of the
  reply mapping and can move it. This record needs only that one call maps a
  `ReplyException` for a data download; the implementer uses the member that
  stage 8 leaves.

## Lead decision on the owner's timing scripts, 12 November 2026

`artifacts\imdb-performance\Measure.ps1` (58) and `replay\Profile.ps1` (62)
call `Dataset.Build` with its old arguments. They are the owner's tools for
timing the IMDb import, not copies of product source, so the implementer
updates those two call lines to the new signature and names them in the
report. Nothing else under `artifacts` changes.

## Implementation notes

Written on 12 November 2026 by the implementing session, by reading the tree
with stage 8 uncommitted. Nothing was built or run, and no request was sent.

### Where the record's references are today

Every member the record names was found by its name. Stage 8 left
`DataDownload.Failure` in place (its decision 4), so it is the one mapping of a
`ReplyException` for a data download, now private. Stage 8 already changed
`Browser.ChooseAutomaticDownload` to read `VideoLookup.Requirement`; this stage
changed only its `!CanDownload` to `Download is not { CanStart: true }`.

### Differences from the record

1. **`Start` publishes the run, and a private `Execute(Run run, string path)`
   holds the workflow.** The nested type is `Run`. `Execute` holds today's
   `Run` body: the three catch filters, the discard in `finally` and the end
   of the work. `Start` sets `_run` and clears it in its own `finally`, so it
   is cleared even when ending the work throws, as the owners' `finally`
   blocks did today. The steps call the dataset directly; no delegates pass
   between `Start` and `Run`.
2. **`DatasetRow` renames its Download button field from `_download` to
   `_start`.** The record names every `DataDownload` local and parameter
   `download`; with the button also called `_download`, one file would use the
   word for two things. The field that holds the `DataDownload` is
   `_download`, and the handler `OnDataset` is `OnDownload`. The automation
   identifier stays `Name + "Download"`.
3. **`Geolocation.Build` throws `InvalidOperationException` when the city data
   serves no folder.** It reads `_directory` and `_policy` at its start, as the
   record says, and both are nullable. `DataDownload` calls `Build` only while
   `FilePath` is set, so the throw does not happen in the app.
4. **`DataDownload.Fetch` keeps `ConfigureAwait(false)`.** The record's rule
   that `DataDownload` awaits on the window thread concerns `Start`, `Delete`
   and the stops. `Fetch` is part of a build and ran with
   `ConfigureAwait(false)` in both copies.
5. **The timing scripts changed in three places each, not only the call
   line.** `Build`'s report delegate is now `Action<WorkDescription,
   WorkProgress>`, so the call fails with the old `Action<ImdbPhase,
   WorkProgress>` delegate. Each script's delegate type line now names
   `Shared.WorkDescription`, and its `ImportClock.Report` reads the
   description's `Phase` by reflection. With `value.ToString()` a phase would
   print as `Imdb { Phase = Titles }`, which renames the JSON phase keys and
   breaks `Measure.ps1 -CancelPhase`. The scripts were edited at
   `V:\artifacts\TinyTorrent2\imdb-performance\`, not through the
   `artifacts` junction.
6. **`Imdb.Dataset.FilePath` is `string`.** It implements `string? FilePath`,
   because the IMDb data always has a path.

Smaller choices, with the same behavior as the record:

- `Installed` is raised in a `finally` after the replacement, so it changes
  whether the replacement succeeded.
- `Imdb.Dataset.Build`'s local function is `ReportPhase`, because a local
  `Report` beside the parameter `report` reads as one name.
- `Browser.PrepareAutomaticDownload`'s summary keeps the automatic-download
  sentence. The failure sentences of `Download(bool)` are in
  `DataDownload.Start`'s summary, where `Run`'s summary had them.
- Comments that named `Install`, `Delete` or `Building` now name the
  replacement or `Replace`. The comment on `Dataset.Install`'s leftover
  deletion moved to `DataDownload.Execute` and to `Dataset.Leftovers`.

### What changes for a person

The three differences under [What changes for a person](#what-changes-for-a-person).
No other behavior changed by reading.

### What reading could not settle

The items under [What reading could not settle](#what-reading-could-not-settle)
stay. The compiler has not seen the change; the batch C build settles it.
