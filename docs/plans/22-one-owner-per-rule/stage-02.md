# Stage 2 gate record: make dependencies point one way

Gate done on 12 November 2026, by reading. Nothing was built or run. This
record takes the place of the stage's text in
[the plan](../22-one-owner-per-rule.md#2-make-dependencies-point-one-way) for
the session that implements it.

Paths without a prefix are under `app/src`. Line numbers are those of the
working tree at commit a4312a7. Stage 1 edits some of these files first, so
each reference also names its member; find the member when the line moved.

## Where the stage stands

Every decision is made. The implementer needs no further design. The stage
changes no text a person reads and no behavior a person meets; the two small
differences are under [What changes for a person](#what-changes-for-a-person).

The decisions, each with its reason below:

1. A failure states its own text through one member, `IFailure.Describe`, and
   `Strings.Error` asks for it. Every failure type of the app follows this
   way, `CommandException` included.
2. A failed data download is a `DownloadException` in `Shared`. Library's
   `VideoFailure` loses its six download members.
3. `Setting` and `SettingStore` move to `Shared`. The plan's default is
   confirmed.
4. The four number parsers move from `Settings.Editor` to `Strings`.
5. `DialogPreference` takes the save operation, not the `Editor`.
6. `TorrentSource` moves to `Torrents` and becomes `LibrarySource`.
7. `Coverage` moves to `Shared/Storage`, beside `Statistics`.
8. `RemovalDraft` asks its `MainViewModel` for the subtitle deletion.
9. Six unused `using` lines go, not four.
10. The direction rule goes into the architecture with this stage.

## Gate step 1: the measurement, read again

Stage 2 marks no finding "to re-read". The gate read the measurement of
[Direction between the app areas](../22-one-owner-per-rule.md#direction-between-the-app-areas)
again, because every decision rests on it. Three of its statements are wrong.
The rest holds: a search of the four feature folders and of `Shared` and
`Controls` for every `using` line, alias and qualified name finds the same
files and no others.

| Plan says | The code says |
| --- | --- |
| `Settings/ConnectionSetup.cs` and `Settings/Editor.cs` name `Torrents.SpeedLimit` | Neither file names any type of `Torrents`. Each calls `Text.SpeedLimit(...)`, which is the method `Strings.SpeedLimit` (`Shared/Strings.cs` 259). `ConnectionSetup.cs` 183 to 186, 686, 692, 731, 732 and `Editor.cs` 353, 354 are the only matches. Their `using Syno.TinyTorrent.Torrents;` lines (`ConnectionSetup.cs` 8, `Editor.cs` 9) are unused. |
| `Shared/TorrentSource.cs` takes `Torrent` and `Pieces` | It takes one static member, `Torrent.ReadState` (line 359). `file.Pieces` (421 to 425) is the property `DetailFile.Pieces` of type `Shared.Engine.PieceRange` (`Shared/Engine/PipeClient/Files.cs` 125, 141). The [database contract](../../contracts/database.md) records that the source keeps its own verification projection and does not use the `Pieces` display model. |
| Four files have an unused `using` line | Six have: the four of the plan and the two `Settings` files above. |

So nine files name a type of an area they must not depend on, and six files
carry an unused `using` line for one.

## Gate step 2: the open questions

### Which way a failure carries its text

**Decision: the failure implements `Shared.IFailure`, whose one member
`Describe(Strings text)` returns the text. `Strings.Error(Exception)` calls
it.** The full design is under [Failure text](#failure-text).

The plan offered two ways. Neither fits every failure, so the gate designed
the member four ways and compared them:

| Candidate | What it is | Result |
| --- | --- | --- |
| A. `Strings` in the constructor, `Message` overridden | The way of `CommandException` today (`Shared/Engine/PipeClient.cs` 877 to 888) | Rejected. The code that throws must hold a `Strings`. About 100 throw sites are in the provider and supplier plugins (`Library/Tmdb/Provider.cs` has 7, `Subtitles/Suppliers/OpenSubtitles.cs` has 7), and a plugin receives no host internals ([code style](../../../AGENTS.md#code-style), rule 4). |
| B. A stated group, key and arguments | The failure exposes data; `Strings` formats it | Rejected. Three failures do not fit the shape: `CommandException` needs `Strings.Error(code, detail)` with its fallback and its detail line (`Strings.cs` 198 to 206), `RequestBlockedException` needs `Strings.Protection`, which formats a time (247 to 253), and `VideoException` chooses its key from the provider's access. They would stay as special cases in `Strings`, which is a second way. It also needs a new record type beside the interface. |
| C. One method that receives `Strings` | `string Describe(Strings text)` | **Chosen.** One member covers all seven failure types. The reader supplies `Strings`, and every reader already holds one (30 calls of `Strings.Error`). The text is built when it is read, so it follows a language change, as [localisation](../../contracts/localisation.md) asks: "Localize at the surface displaying the message". |
| D. `Strings` owns the text of each failure enum | `Strings.Get(SubtitleFailure)`, like `Strings.Get(WorkDescription)` | Rejected. `Strings` still has to get from the exception to its enum, and the text of a `VideoException` also needs the provider's name and access. The plan's table of owners gives the text to the failure. |

`CommandException` changes to the chosen way: it drops its `Strings`
parameter and its `Message` override. Keeping them would leave one failure
type with two ways to give its text.

An exception that is not a failure type of the app keeps the existing
fallback: `Strings.Error` shows its `Message`. The app builds such exceptions
from platform types with text it takes from `Strings` where it builds them,
for example `new IOException(_strings.Get("connection", "unavailable"))`
(`PipeClient.cs` 101, 123, 394; `Setting.Submit`). They stay as they are.

### Whether `Setting` moves

**Decision: it moves to `Shared`, with `SettingStore`. The default is
confirmed.**

Everything `Setting` takes from `Settings` is four static parsers of
`Editor`, in five calls (`Settings/Setting.cs`): `Editor.RateInput` (162),
`Editor.TryNumber` (226, 477), `Editor.TryRate` (465) and `Editor.TryInteger`
(473). Its other inputs are already below it: `Strings` is in `Shared`, and
`SettingKind`, `SettingsCategory` and `RateScale` are in the root `Enums.cs`
(851, 245, 168). `SettingStore` is declared in the same file (512). The
parsers move to the base in either case, so after that move `Setting` takes
nothing from `Settings`.

The other remedy keeps `Setting` in `Settings` and splits `SettingsRow`. It
changes the 67 `Setting="{…}"` rows of `Settings/SettingsPage.xaml`, makes two
controls of one, and still leaves `Shared/DialogPreference.cs`,
`Shell/Updates.cs` and the two drafts in `Torrents` dependent on `Settings`.
It costs more and repairs less.

### What `DialogPreference` takes from `Editor`

One call: `settings.Save(values)` (`Shared/DialogPreference.cs` 36), which is
`Editor.Save(object changes)` (`Settings/Editor.cs` 1040). It also names the
type `Setting` (28), which moves to the base.

**Decision: the constructor takes the save operation,
`Func<IReadOnlyDictionary<string, bool>, Task> save`.** The plan expected one
creator in `Shell`. There are four: `Shell/MainWindow.cs` 777,
`Shell/MainWindow/Dialogs.cs` 269, `Torrents/AddDraft.cs` 267 and
`Torrents/RemovalDraft.cs` 110. Each passes the method group, which converts
to the delegate without a lambda: `new DialogPreference(Model.Settings.Save)`
in `Shell`, `Confirmation = new(owner.Settings.Save)` in the drafts. A draft
reaches the editor through its `MainViewModel` and names no `Settings` type,
which the direction rule allows.

`Save` keeps one command for all the given settings.
`app/tests/AddSourceChecks.ps1` 144 to 152 requires that.

### Who else uses `Shared/TorrentSource.cs`

Only `Shell/Features.cs`: the field (40), the creation (186) and the local in
`Detach` (683). No feature, no control, no capture journey and no test script
names it.

**Decision: it moves to `Torrents` as `Torrents/LibrarySource.cs`, type
`LibrarySource`.** Three placements were compared:

| Candidate | Result |
| --- | --- |
| Stay in `Shared`; move the engine's state words (`Torrent.Words`, `States`, `ReadState`, `Torrents/Torrent.cs` 155 to 179) to the base | Rejected. It moves torrent vocabulary into the base to keep one file in place, and it needs a new static type to hold three members, which is a helper. Comparing the two words in the source itself would be a second copy of the engine's words, the pair that stage 1 removes. |
| Move to `Shell`, its only user | Rejected. The file holds a rule, "Library lists a file when it is wanted and every piece from its first to its last is verified" (`IsVerified`, 419 to 426). `Shell` composes; feature rules stay with their owners ([naming](../../agents/naming.md#app-folders)). |
| Move to `Torrents` | **Chosen.** The file reads the torrent rows of a snapshot and the engine's file detail, which is knowledge of torrents. No feature needs it, so the base does not need it. `Torrents` may name `Shared`, where `FileSources`, `Contribution` and `SourceFile` stay. The [Library contract](../../contracts/library.md#ownership-and-data-sources) already calls it "the torrent adapter". |

The name changes because the scope changes. `Torrents.TorrentSource` repeats
its namespace, which [naming](../../agents/naming.md#carry-only-the-necessary-distinctions)
makes invalid. `Source` alone is the product's word for a torrent file or
magnet ([CONTEXT.md](../../../CONTEXT.md), "Source"). The product term for
this concept is "Library data source", so the type is `LibrarySource`: in
`Torrents`, it reads as the torrents' Library data source.

### Why `Settings` names `Torrents.SpeedLimit`

It does not, as [gate step 1](#gate-step-1-the-measurement-read-again) shows.
The `SpeedLimit` draft stays in `Torrents`. The two unused `using` lines go.

### Whether `Coverage` reaches `Settings/Editor/Plugins.cs` through the model

Yes. `Editor.ReadCoverage` reads it with `_owner.Library.ReadCoverage()`
(`Settings/Editor/Plugins.cs` 133), and
[the architecture](../../contracts/architecture.md) records that the editor
owns the plugin coverage reads (line 325). The file names the type only to
hold the result: `private Coverage? _coverage;` (12). `Coverage` is
`internal sealed record Coverage(int Total, int Informed)`
(`Library/Store.cs` 864).

**Decision: the record moves to `Shared/Storage/Database.cs`, beside
`Statistics` (322).** Library produces it and Settings holds it, so it is a
type that two features need. `Statistics` is the same kind of fact, counts
read from the shared database, and `Plugins.cs` already holds one
(`_statistics`, 13) under `using Syno.TinyTorrent.Shared.Storage;`. The SQL
stays in `Library.Store.ReadCoverage`.

## Gate step 3: the design

### Failure text

Owner: each failure type. Entry point for every reader: `Strings.Error`.

In `Shared/Strings.cs`, after `Strings`:

```csharp
/// <summary>A failure that states the text a person reads for it.</summary>
internal interface IFailure
{
    string Describe(Strings text);
}
```

The interface is allowed by [naming](../../agents/naming.md#interfaces):
`Strings.Error` selects between seven real implementations.

`Strings.Error(Exception)` becomes:

```csharp
public string Error(Exception error) =>
    error is IFailure failure ? failure.Describe(this)
    // A title or the surrounding control already says that the
    // operation failed, so only the reason follows it.
    : string.IsNullOrWhiteSpace(error.Message) ? Get("errors", "unknown")
    : error.Message;
```

`Strings.Failure(Library.VideoException)` (235 to 245) is deleted.
`Strings.Error(string code, string? detail)` and `Strings.Protection` stay:
other callers use them (`Shell/MainViewModel/Notifications.cs` 210, 218;
`Subtitles/ChoiceDialog.cs` 54, 109; `Library/MetadataDialog.cs` 73, 240).

Each failure type and its `Describe`:

| Type | File | `Describe(Strings text)` returns |
| --- | --- | --- |
| `CommandException` | `Shared/Engine/PipeClient.cs` | `text.Error(Code, Detail)` |
| `DatabaseVersionException` | `Shared/Storage/Database.cs` | `text.Get("errors", "database_incompatible")` |
| `RequestBlockedException` | `Shared/Network/RequestPolicy.cs` | `text.Protection(Block)` |
| `DownloadException` (new) | `Shared/DataDownload.cs` | See [Data downloads](#data-downloads) |
| `VideoException` | `Library/IProvider.cs` | Virtual. The body of today's `Strings.Failure`: the key is `refused_request` when `Failure` is `Refused` and `Provider.Access` is not `Access.Key`, otherwise the failure's name in lower case; `text.Format("library", "failure_" + key, Provider?.Name ?? string.Empty)`. The comment "A provider without an API key refused the request, not a key." moves with it. |
| `ProviderException` | `Library/IProvider.cs` | Override: with a `Block`, `text.Get("request_protection", block.Kind.ToString().ToLowerInvariant())`; otherwise `base.Describe(text)`. |
| `UpdateException` | `Shell/Updates.cs` | `text.Get("about", "update_" + Reason.ToString().ToLowerInvariant())` |
| `SubtitleException` | `Subtitles/Acquisition.cs` | `Describe(text, Reason)`, the static member below |

`RouteException` and `MissingVideoException` inherit from their base.

**`CommandException`** becomes:

```csharp
internal sealed class CommandException(string code, string? detail, string? command = null)
    : Exception(detail is null ? code : code + ": " + detail), IFailure
```

It keeps `Code`, `Detail` and `Command`, and whatever stage 1 added, such as
the enum of refusals. The base message is the engine's word, so the crash
log (`Shared/Diagnostics/ExceptionLog.cs` 19, 38) states it in one language.
The compiler finds every construction site, because the third argument was a
`Strings` and is now a `string?`.

`PipeClient.ReadOutcome` (605) loses its `strings` parameter, and so does
`PipeClient.Observation`'s constructor (785 to 800), whose only use of it is
that call. Their callers: `PipeClient.cs` 437, 487, 511, 663, 795.

**The text of a `SubtitleFailure`** has four copies of one key rule. Three
of them hold a reason and no exception, so the owner offers both forms:

```csharp
// On SubtitleException.
internal static string Describe(Strings text, SubtitleFailure reason) =>
    text.Get("subtitles", "failure_" + reason.ToString().ToLowerInvariant());

public string Describe(Strings text) => Describe(text, Reason);
```

A static member that takes `Strings` first is the existing pattern
(`Pieces.Name(Strings text, PieceKind kind)`, `Torrents/Pieces.cs` 110).

**Pairs that stay.** A blocked `ProviderException` shows the protection's
reason without its time, and `Library.Browser.Error` adds the time
(`Library/Browser.cs` 150 to 156). A `RequestBlockedException` shows
`Strings.Protection`, which includes the time. These are two rules, so they
are not merged.

### Data downloads

Owner: `Shared/DataDownload.cs`. Stage 9 builds on this type and then
collapses the catch blocks that create it.

In `Enums.cs`:

```csharp
internal enum DownloadFailure
{
    Failed,
    Damaged,
    DiskFull,
    InUse,
    UnknownFormat,
    Interrupted,
    Route,
}
```

In `Shared/DataDownload.cs`:

```csharp
/// <summary>A data download, or the replacement or removal of its installed data, failed.</summary>
internal sealed class DownloadException(DownloadFailure failure, Exception? inner = null)
    : Exception(failure.ToString(), inner), IFailure
{
    internal DownloadFailure Failure { get; } = failure;

    /// <summary>The name of the data, which its owner sets so the text can name it.</summary>
    internal string? Subject { get; set; }

    public string Describe(Strings text) =>
        text.Format(
            "work",
            "download_" + Failure.ToString().ToLowerInvariant(),
            Subject ?? string.Empty
        );
}
```

`VideoFailure` (`Enums.cs` 684) loses `DownloadFailed`, `DownloadDamaged`,
`DiskFull`, `InUse`, `UnknownFormat` and `Interrupted`, with the comment
above them. One list holds each fact, so the IMDb data fails the same way as
the city data. No code casts a `VideoFailure` to or from a number or parses
its name, so removing members changes no saved data.
`VideoException.Subject` (`Library/IProvider.cs` 293) is deleted; the city
data was its only user.

Each site, with what it creates after the change:

| Site | Today | After |
| --- | --- | --- |
| `DataDownload.Failure`, 45 | `new Library.RouteException(error)` | `new DownloadException(DownloadFailure.Route, error)` |
| `DataDownload.Failure`, 49 | `VideoFailure.DownloadFailed` | `DownloadFailure.Failed` |
| `DataDownload.Run`, 84 | `VideoFailure.Interrupted` | `DownloadFailure.Interrupted` |
| `Geolocation.Build`, 278, 282, 286 | `DownloadDamaged`, `DiskFull`, `DownloadFailed` | `Damaged`, `DiskFull`, `Failed` |
| `Geolocation.Replace`, 388 | `InUse` | `InUse` |
| `Geolocation.Report`, 437 | `if (error is VideoException failure) failure.Subject ??= Name;` | The same line for `DownloadException` |
| `Library/Imdb/Dataset.cs` `Build`, 152, 164, 169 | `DownloadDamaged`, `DiskFull`, `DownloadFailed` | `Damaged`, `DiskFull`, `Failed` |
| `Dataset.Build`, 154 | `catch (VideoException error) when (error.Failure == VideoFailure.UnknownFormat)` | `catch (DownloadException error) when (error.Failure == DownloadFailure.UnknownFormat)` |
| `Dataset.Install` 186, `Dataset.Delete` 232 | `InUse` | `InUse` |
| `Dataset` import, 606, 704, 1021 | `UnknownFormat` | `UnknownFormat` |
| `Library/Browser/Information.cs` 319, 398 | `if (error is VideoException failure) failure.Provider ??= option;` | That line stays, and one line is added after it: `if (error is DownloadException download) download.Subject ??= option.Name;` |

`Dataset.cs` 415 and 419 (`Damaged`, `Unreadable`) are failures of a lookup
that reads the data. They stay `ProviderException`. The `<exception>`
comments at `Dataset.cs` 105, 174 and 219 name the new type.

The text moves with the type. In each of the six catalogues
(`Resources/en.json`, `es.json`, `nl.json`, `zh.json`, `pt.json`, `fr.json`)
the message moves from group `library` to group `work` unchanged, placeholder
included:

| From `library` | To `work` |
| --- | --- |
| `failure_downloadfailed` | `download_failed` |
| `failure_downloaddamaged` | `download_damaged` |
| `failure_diskfull` | `download_diskfull` |
| `failure_inuse` | `download_inuse` |
| `failure_unknownformat` | `download_unknownformat` |
| `failure_interrupted` | `download_interrupted` |
| `failure_route` (copied; it stays in `library` for lookups) | `download_route` |

Reason: the member names of `DownloadFailure` must not repeat "Download", so
the old keys no longer follow from the names, and the key rule stays "prefix
plus the name in lower case" as for every other failure. `work` is the
surface that shows these messages (`Shell/Notifications.cs` 450). A
translation that lacks a message falls back to English, so add the English
keys first.

`DataDownload`'s constructor does not change, so
`app/tests/SourceChecks.ps1` 996 still constructs it.

### `Setting` and the number parsers

- `Settings/Setting.cs` moves to `Shared/Setting.cs`, with `git mv`. Its
  namespace becomes `Syno.TinyTorrent.Shared`. `Setting` and `SettingStore`
  keep their names, members and constructor. Its `#if CAPTURE` block (424 to
  426) moves with it.
- `TryInteger`, `TryNumber`, `TryRate` and `RateInput`
  (`Settings/Editor.cs` 1164 to 1199) move to `Strings` as `internal static`
  members with the same names, signatures, bodies and comments. `Strings`
  already owns the other half of the rule, a number as the person reads it
  (`RateUnit`, `SpeedLimit`, `Bytes`), in the same culture, and stage 3 makes
  it the owner of number formats. No new type is added for four functions.
- `SettingsRow` stays one control. Its `using` line changes from `Settings`
  to `Shared`.

### `DialogPreference`

```csharp
public sealed class DialogPreference(Func<IReadOnlyDictionary<string, bool>, Task> save)
    : INotifyPropertyChanged
```

`Save(params Setting[] choices)` keeps its body and ends with
`return values.Count > 0 ? save(values) : Task.CompletedTask;`. The file's
`using Syno.TinyTorrent.Settings;` goes.

### `LibrarySource`

`Shared/TorrentSource.cs` moves to `Torrents/LibrarySource.cs`, with
`git mv`. The namespace becomes `Syno.TinyTorrent.Torrents`, the type
`LibrarySource`. `using Syno.TinyTorrent.Torrents;` goes and
`using Syno.TinyTorrent.Shared;` is added. No member changes.

### `Coverage`

The line `internal sealed record Coverage(int Total, int Informed);` moves
from `Library/Store.cs` 864 to `Shared/Storage/Database.cs`, after
`Statistics`. `Library/Store.cs` and `Library/Browser.cs` already import
`Shared.Storage`. `Settings/Editor/Plugins.cs` loses
`using Syno.TinyTorrent.Library;`.

### Subtitles of a removed torrent

Three interfaces were compared:

| Candidate | Result |
| --- | --- |
| The constructor of `RemovalDraft` takes a delegate | Rejected. The draft already takes its `MainViewModel`, and the [ownership decisions](../../contracts/architecture.md#ownership-decisions) record why a second object beside that reference is not added. |
| `RemovalDraft` keeps both calls and types them with `var` | Rejected. It names no `Subtitles` type, but `Torrents` stays the place that knows how subtitle deletion is ordered. The plan settles that `Shell` holds the operation. |
| A member of `MainViewModel` | **Chosen.** `Shell` composes the features, and the draft calls one member. |

In `Shell/MainViewModel/Actions.cs`, beside the `RemovalDraft` property:

```csharp
/// <summary>
/// Reads which subtitles belong to the torrents, and returns the step that
/// deletes them once the engine accepts the removal; null while Subtitles is
/// not available. The read joins the database queue now, ahead of the
/// Library work the removal starts, so it still finds the subtitles.
/// </summary>
internal Func<Task>? CaptureSubtitles(string[] torrentIds, DeletionMode mode)
{
    if (Subtitles is not { } subtitles)
        return null;
    var capture = subtitles.CaptureDeletion(torrentIds);
    return async () => subtitles.Delete(await capture, mode);
}
```

In `RemovalDraft.Send` (126 to 157): before `Client.Send`,
`var deleteSubtitles = deletesDownloaded ? Owner.CaptureSubtitles(TorrentIds, _deletion) : null;`.
After `AnnounceAccepted`,
`if (deleteSubtitles is not null) _ = deleteSubtitles();`. The static
`DeleteSubtitles` (159 to 163) and the local `subtitles` are deleted. The
comment about the database queue moves to the member above.

## The eleven files, and the six unused lines

| File | Today | After stage 2 |
| --- | --- | --- |
| `Shared/Strings.cs` | Names `Library.ProviderException`, `Library.VideoException`, `Shell.UpdateException`, `Subtitles.SubtitleException` | Names `IFailure` only |
| `Shared/DataDownload.cs` | `Library.RouteException`, `Library.VideoException` | `DownloadException`, declared in the file |
| `Shared/Geolocation.cs` | `using Library`, `VideoException` | `DownloadException`; the `using` line goes |
| `Shared/TorrentSource.cs` | `using Torrents`, `Torrent.ReadState` | Is `Torrents/LibrarySource.cs` |
| `Shared/DialogPreference.cs` | `using Settings`, `Editor`, `Setting` | Takes a delegate; `Setting` is in `Shared` |
| `Controls/SettingsRow.cs` | `using Settings`, `Setting` | `using Shared` |
| `Torrents/SpeedLimit.cs` | `using Settings`, `Editor.RateInput` (155), `Editor.TryRate` (172) | `Strings.RateInput`, `Strings.TryRate`; the `using` line goes |
| `Settings/ConnectionSetup.cs` | `using Torrents`, unused | The line goes. Line 770 calls `Strings.TryNumber`. |
| `Settings/Editor.cs` | `using Torrents`, unused | The line goes. The four parsers leave the file. |
| `Settings/Editor/Plugins.cs` | `using Library`, `Coverage` | `Coverage` is in `Shared.Storage`; the `using` line goes |
| `Torrents/RemovalDraft.cs` | `Subtitles.Acquisition`, `Subtitles.Deletion` | `Owner.CaptureSubtitles` |

Unused `using` lines that go: `Controls/Pane.cs` 6 (`Shell`; its
`Navigation` is its own property), `Shared/Engine/PipeClient.cs` 7 (`Shell`;
`Toast` is its own event and `Inspector` a member of its `Consumer`),
`Torrents/AddDraft.cs` 7 (`Settings`), `Library/Browser/Information.cs` 3
(`Torrents`), and the two `Settings` files in the table. Each file was
searched for every type name of the area it imports; none is named.

After the move of `Setting`, two more lines become unused and go:
`using Syno.TinyTorrent.Settings;` in `Shell/MainViewModel/Finding.cs` 3 and
in `Shell/Updates.cs` 9. `Shell` may name `Settings`, so these two are
tidiness, not the rule.

## Gate step 4: every consumer of every name

Searched: all of `app/src`, which includes the 25 capture journeys and the
`#if CAPTURE` blocks, all XAML, and `app/tests/*.ps1`.

### `Setting`, `SettingStore`

| Consumer | Edit |
| --- | --- |
| `Controls/SettingsRow.cs` 6 | `using Settings` becomes `using Shared` |
| `Settings/SpeedLimits.xaml.cs` | Add `using Syno.TinyTorrent.Shared;` (names `Setting` at 61, 76; has no such line) |
| `Settings/Editor.cs`, `ConnectionSetup.cs`, `SettingsPage.xaml.cs` | None; each has `using Shared` |
| `Settings/SettingsPage.xaml` | None. Its 67 rows set the property `Setting`; XAML names no type. |
| `Shared/DialogPreference.cs` | Same namespace now |
| `Shell/MainWindow/Workspace.cs` 631 | None; has both lines |
| `Shell/MainViewModel/Finding.cs` 215, `Shell/Updates.cs` 26, 38 | Remove the unused `using Settings` |
| Capture: `Shell/MainWindow/Capture.cs` 532 | None; has `using Shared` |
| Capture: `Shell/MainWindow/CaptureEdits.cs` 60, 678, 694 | Add `using Syno.TinyTorrent.Shared;` (has only `Shared.Engine`) |
| Capture: `CaptureSearch.cs` 289, `CaptureSettings.cs` 402 to 464 | None; they read members and name no type |
| `app/tests/SettingChecks.ps1` 10, 11 | `Syno.TinyTorrent.Shared.Setting`, `Syno.TinyTorrent.Shared.SettingStore` |
| `app/tests/AddSourceChecks.ps1` 106 | `Syno.TinyTorrent.Shared.Setting` |
| `app/tests/UpdateChecks.ps1` 73 | `Syno.TinyTorrent.Shared.Setting` |

### The parsers

| Consumer | Edit |
| --- | --- |
| `Shared/Setting.cs` 162, 226, 465, 473, 477 | `Editor.` becomes `Strings.` |
| `Torrents/SpeedLimit.cs` 155, 172 | The same |
| `Settings/ConnectionSetup.cs` 770 | `Strings.TryNumber` |
| `Settings/Proxy.cs` 288 | `Strings.TryInteger` |

No capture journey and no test script names them.

### `DialogPreference`

| Consumer | Edit |
| --- | --- |
| `Shell/MainWindow.cs` 777, `Shell/MainWindow/Dialogs.cs` 269 | `new DialogPreference(Model.Settings.Save)` |
| `Torrents/AddDraft.cs` 267, `Torrents/RemovalDraft.cs` 110 | `Confirmation = new(owner.Settings.Save);` |
| `Shared/Dialog.cs` 188, 197; `Shared/DialogFooter.cs` 13; `AddDraft.cs` 148, 814; `RemovalDraft.cs` 33, 128 | None |
| `app/tests/AddSourceChecks.ps1` 138 | The constructor argument becomes a delegate bound to the uninitialised editor: `[Delegate]::CreateDelegate(<Func of IReadOnlyDictionary[string,bool] and Task>, $settings, $settingsType.GetMethod('Save', $flags))` |

### `TorrentSource`

| Consumer | Edit |
| --- | --- |
| `Shell/Features.cs` 40, 186, 683 | `LibrarySource`. The file imports no `Torrents` and reaches each feature type through an alias (11 to 18), so it adds `using LibrarySource = Syno.TinyTorrent.Torrents.LibrarySource;` |

### `Coverage`

| Consumer | Edit |
| --- | --- |
| `Library/Store.cs` 34, 43, 864 | The declaration leaves the file |
| `Library/Browser.cs` 75 | None |
| `Settings/Editor/Plugins.cs` 4, 12 | The `using` line goes |

### `CommandException`

Construction sites, each losing its `Strings` argument: `PipeClient.cs` 610;
`Settings/Proxy.cs` 309, 325; `Shell/Inspector.cs` 1223;
`Shell/Notifications.cs` 198; `Torrents/AddDraft.cs` 322, 427, 1268; capture
`Shell/MainWindow/Capture.cs` 740 and `CaptureEdits.cs` 249.

Readers of its `Message`: `Strings.Error` (211), which the new body
replaces, and the capture journey `Capture.cs` 3198, which writes
`Model.AddDraft.Failure.Message` into its report and changes to
`Model.Text.Error(Model.AddDraft.Failure)`. `PipeClient.cs` 283 reads the
message of a connection failure, which is never a `CommandException`.
`Torrents/PeerMap.xaml.cs` 309 already calls `Strings.Error` first.

The sites that compare `Code` are stage 1's and do not change here. No test
script names the type.

### `VideoException`, `ProviderException`, `RouteException`, `VideoFailure`

Names stay. The throw and catch sites that change are in the table under
[Data downloads](#data-downloads). `Library/Browser/Information.cs` 38, 520;
`Library/VideoLookup.cs`; `Library/VideoLookup/Identification.cs`;
`Library/MetadataDialog.cs` 182 and `Library/Browser.cs` 153, 452 handle
lookups and do not change.

`app/tests/SourceChecks.ps1`: the list at 1006 gains `'DownloadException'`,
and the `City` column of `$replyCases` (1012 to 1015) changes
`'DownloadFailed'` to `'Failed'` in two rows. `'Route'` and `'RemoteWait'`
stay; the property the script reads is still `Failure`. Lines 1099 to 1119
name `ProviderException` for lookups and stay.

### `SubtitleException`, `SubtitleFailure`

Names stay. The four copies of the key become calls:

| Copy | After |
| --- | --- |
| `Shared/Strings.cs` 223 to 226 | Gone with the switch |
| `Shell/Notifications.cs` 513 to 516 | `Subtitles.SubtitleException.Describe(_text, latest.Reason)`, qualified as the file already writes `Subtitles.FileFailure` (333) |
| `Shell/MainViewModel.cs` 303 | `SubtitleException.Describe(Text, failure)` |
| `Subtitles/Batch.cs` 253 | `SubtitleException.Describe(_text, failure)` |

`Shell/MainViewModel.cs` reaches the type through an alias beside the two it
has (12, 13), `using SubtitleException = Syno.TinyTorrent.Subtitles.SubtitleException;`,
because `Subtitles` is also a property of the model. `app/tests/SourceChecks.ps1` 706, 905 to 951,
1006 and 1029 read the type's name and `Reason`, which stay.

### `UpdateException`

Name and constructor stay. `Shell/Updates.cs` 199, 310 and the capture
journey `CaptureUpdates.cs` 86 do not change.

### `RemovalDraft`

`Shell/MainViewModel.cs` 385 and the capture journey `CaptureDialogs.cs` 99
do not change; the constructor keeps its parameters.

## Files the implementer may edit

Product code:

- `Enums.cs`
- `Shared/Strings.cs`, `Shared/Setting.cs` (moved), `Shared/DialogPreference.cs`,
  `Shared/DataDownload.cs`, `Shared/Geolocation.cs`,
  `Shared/Engine/PipeClient.cs`, `Shared/Storage/Database.cs`,
  `Shared/Network/RequestPolicy.cs`
- `Controls/SettingsRow.cs`, `Controls/Pane.cs`
- `Settings/Editor.cs`, `Settings/Editor/Plugins.cs`,
  `Settings/ConnectionSetup.cs`, `Settings/Proxy.cs`,
  `Settings/SpeedLimits.xaml.cs`
- `Torrents/LibrarySource.cs` (moved), `Torrents/SpeedLimit.cs`,
  `Torrents/AddDraft.cs`, `Torrents/RemovalDraft.cs`
- `Library/IProvider.cs`, `Library/Store.cs`, `Library/Imdb/Dataset.cs`,
  `Library/Browser/Information.cs`
- `Subtitles/Acquisition.cs`, `Subtitles/Batch.cs`
- `Shell/Updates.cs`, `Shell/Features.cs`, `Shell/Notifications.cs`,
  `Shell/Inspector.cs`, `Shell/MainViewModel.cs`,
  `Shell/MainViewModel/Actions.cs`, `Shell/MainViewModel/Finding.cs`,
  `Shell/MainWindow.cs`, `Shell/MainWindow/Dialogs.cs`
- `Resources/en.json`, `es.json`, `nl.json`, `zh.json`, `pt.json`, `fr.json`

Capture journeys: `Shell/MainWindow/Capture.cs`,
`Shell/MainWindow/CaptureEdits.cs`.

Test scripts: `app/tests/SettingChecks.ps1`, `AddSourceChecks.ps1`,
`UpdateChecks.ps1`, `SourceChecks.ps1`.

Documents: `docs/contracts/architecture.md`, `localisation.md`,
`database.md`, `background-work.md`, `library/providers.md`, and
`docs/agents/naming.md`.

A file that is not in this list is edited only when the compiler names it,
and the batch report says which.

## Contract changes, as proposed text

**`docs/contracts/architecture.md`, "Projects and source layout"**, a new
paragraph after the one that starts "The app is one WinUI project". This is
the text the owner decided:

> `Shared` and `Controls` are the base: they name each other and no other
> area. `Torrents`, `Settings`, `Library` and `Subtitles` name the base and
> `Shell`, but never each other and never a `Shell` type that is a window,
> a page or a control, which today are `MainWindow`, `InspectorPane`,
> `AboutPage` and `CreditsPage`; a type that two features need lives in the
> base. The Settings page is the one exception: it shows the settings of
> every feature, so `Settings/SettingsPage.xaml.cs` and the files of
> `Settings/SettingsPage` name `Library` and `Subtitles`. `Shell` composes
> the features and names every area. A file names an area by a `using`
> line, a `using` alias or a qualified type name, so a reviewer checks
> this rule in the diff. The rule limits names: a feature still reads
> another feature's state through its `MainViewModel`. It keeps a feature
> from becoming the authority for a rule that another feature also needs.

**`docs/contracts/architecture.md`, "Ownership decisions"**, three new items:

> - `Setting` and `SettingStore` live in `Shared`, because `SettingsRow`,
>   `DialogPreference`, `Shell` and two drafts in `Torrents` use a setting,
>   and `Settings.Editor` registers them. Splitting `SettingsRow` so that
>   `Setting` could stay in `Settings` would change every row of the
>   Settings page and leave those users dependent on `Settings`.
> - A failure type states the text a person reads through
>   `IFailure.Describe`, and `Strings.Error` asks for it; any other
>   exception shows its message. A failure does not hold `Strings`, because
>   providers and suppliers throw failures and receive no host object. A
>   failure does not expose a group, key and arguments either, because the
>   text of an engine refusal, of a request block and of a provider refusal
>   each needs more than one lookup.
> - The Library data source for managed torrents, `LibrarySource`, lives in
>   `Torrents`, because it reads torrent rows and no feature names it.
>   `FileSources`, which accepts every data source's contributions, stays in
>   `Shared`.

**`docs/contracts/localisation.md`**, after the paragraph that starts "Send
stable error/status codes":

> A failure type of the app implements `IFailure` and builds its text from
> `Strings` when a surface asks for it through `Strings.Error`, so the text
> follows a language change. A failure never stores translated text.

**`docs/contracts/library/providers.md`**, after the paragraph that starts
"Expected provider failures carry a `VideoFailure`":

> A failed download, replacement or removal of a provider's data is a
> `DownloadException` with a `DownloadFailure`, the same failure the city
> data uses.

**`docs/contracts/database.md`** line 43 and
**`docs/contracts/background-work.md`** line 284: `TorrentSource` becomes
`LibrarySource`, and the link becomes
`../../app/src/Torrents/LibrarySource.cs`.

**`docs/agents/naming.md`, "App folders"**: the `Torrents/` item gains "and
the Library data source that reports the files of managed torrents"; the
`Shared/` item gains "settings fields" after "drafts".

`CONTEXT.md` needs no change. The stage adds no product term.

## What changes for a person

Nothing a person reads or does changes. Two differences exist, and the batch
report names them:

- A `CommandException`'s `Message` is now the engine's word, with its detail,
  in place of translated text. A person never reads `Message`; every surface
  calls `Strings.Error`. The crash log and debug output show the word.
- The subtitles of a removed torrent are deleted in the mode the removal
  command was sent with. Today the mode is read after the engine's reply
  (`RemovalDraft.cs` 147), so a second Remove dialog opened in between could
  change it. The command itself always used the earlier value (140).

## Done when

- A search of `Shared` and `Controls` for `TinyTorrent.Torrents`,
  `TinyTorrent.Settings`, `TinyTorrent.Library`, `TinyTorrent.Subtitles` and
  `TinyTorrent.Shell`, and for each of those area names before a type name,
  finds nothing.
- The same search of each feature folder for the other three features finds
  only `Settings/SettingsPage.xaml.cs` and the files of
  `Settings/SettingsPage`. `Library/EmptyState.xaml.cs` matches `Torrents.`
  as the name of its button, which is no type.
- A search for `"failure_" +` finds `VideoException.Describe` and
  `SubtitleException.Describe` only.
- A search for `new Library.`, `Editor.Try`, `Editor.RateInput`,
  `TorrentSource` and `VideoFailure.Download` in `app` finds nothing.
- The direction rule is in the architecture.

## Cost

- **Agent work:** 4 to 5 hours for the implementing session: about 45 files,
  most with one or two lines, plus the six catalogues. One hour for the
  independent review of the diff.
- **Builds:** none of its own. The stage joins batch A's one app build with
  `-Capture`. The capture build is required, because two journeys construct
  `CommandException` and two name `Setting`.
- **Checks on that build:** the four scripts that name a moved type:
  `SettingChecks.ps1`, `AddSourceChecks.ps1`, `UpdateChecks.ps1` and
  `SourceChecks.ps1`. No capture review and no app launch: the stage changes
  no layout and no text.

## What reading could not settle

- That the six `using` lines are unused. Each file was searched for every
  type name of the imported area. An unused `using` line is no build error,
  so the build confirms the opposite case only: a needed line that was
  removed fails the build.
- That the method group `Model.Settings.Save` converts to
  `Func<IReadOnlyDictionary<string, bool>, Task>`. C# allows it: the
  parameter converts to `object` and `Task<long>` converts to `Task`. If the
  compiler refuses, the four creators pass
  `values => Model.Settings.Save(values)`.
- Two other plans cite the type that moves:
  `docs/plans/13-using-gozler.md` 173, 515, 577 and
  `docs/plans/21-missing-torrent-files.md` 630 name `TorrentSource` and
  `app/src/Shared/TorrentSource.cs`. Other sessions own those files, so this
  stage does not edit them. The lead tells their owners.

## Implementation notes

Implemented on 12 November 2026 on top of stage 1, by reading; nothing was
built or run. Where the code differs from the record:

- **The two moves use a plain file move, not `git mv`.** The session may
  not stage. Git shows `Settings/Setting.cs` and `Shared/TorrentSource.cs`
  as deleted and `Shared/Setting.cs` and `Torrents/LibrarySource.cs` as
  untracked until the commit stages them; the content is the same file.
- **`Shared/Strings.cs` also loses `using Shared.Engine` and
  `using Shared.Storage`.** The file named only `CommandException` and
  `DatabaseVersionException` from them.
- **New control bodies have braces**, as the owner's code style asks:
  `MainViewModel.CaptureSubtitles`, the subtitle step in
  `RemovalDraft.Send` and the two `Subject` lines in
  `Library/Browser/Information.cs`. The record wrote them without.
- **`ProviderException.Describe` has a comment** that says why it leaves
  out the time: `Library.Browser.Error` adds it. Without it, the next
  reader would unify it with `Strings.Protection` and show the time twice.
- **`DownloadFailure` sits after `CityPhase`** in `Enums.cs`, beside the
  other data download enums. The catalogue keys sit at the end of `work`.
- **The search for `new Library.`** finds `Shell/MainWindow/Library.cs`
  313, `new Library.BatchDialog`. `Shell` may name every area, so it stays.
- **A fifth copy of the subtitle failure key**, which the record does not
  list, was the capture journey's paused status (`Capture.cs` 659, the
  literal `subtitles.failure_authentication`). It now calls
  `SubtitleException.Describe` too.

## Review fixes

The review of the stage found three low defects. The stage 3 session fixed
them in the same tree, by reading; nothing was built or run. No text a person
reads changes.

1. **A request block's reason had two owners.** `Strings.Protection` and
   `ProviderException.Describe` (`Library/IProvider.cs`) each read the
   `request_protection` text of the block's kind. `Strings` now has
   `Protection(RequestBlockKind kind)`, the reason alone, which
   `Protection(RequestBlock)` and `ProviderException.Describe` call. A copy of
   the block without its time (`block with { RetryAt = null }`) was the other
   remedy; it makes a record only to drop it.
2. **`CaptureSubtitles` lost a live reason.** Its summary
   (`Shell/MainViewModel/Actions.cs`) again says why it returns a step
   instead of waiting for the read: the removal command must not wait behind
   the database queue. It also says that the caller runs the step only after
   the engine accepts the removal.
3. **`VersionException` stored translated text.** It is now an `IFailure` that
   holds the engine's executable path, read while the pipe is open, and its
   `Describe` calls `EngineProcess.VersionMessage`, which takes that path
   instead of the pipe. `EngineProcess.Executable` became internal for the
   throw site, and `CheckVersion` lost its unused `Strings` parameter. The
   connect loop (`PipeClient.cs`, the disconnected message) reads the text
   through `Strings.Error`. [Localisation](../../contracts/localisation.md)
   now scopes the rule to the failure types the app declares: a platform
   exception that the app throws carries its text from `Strings` as its
   message.
