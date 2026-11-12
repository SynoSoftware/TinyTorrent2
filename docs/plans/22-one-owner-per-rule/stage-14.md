# Stage 14 gate record: engine command, save and storage shells, and registration

The gate of
[stage 14](../22-one-owner-per-rule.md#14-engine-command-save-and-storage-shells-and-registration).
Line numbers are those of commit c2548f2, the head when this gate ran.
`Move.cpp`, `Storage.cpp`, `Torrent.h` and `State.h` changed after 7e0ee84, so
their numbers differ from the plan's. Stage 13 edits several of the same files
before this stage starts, so the implementing session finds each place by the
member this record names. This record replaces the stage's text for the
session that implements it.

## Owners and interfaces

Each member below is the whole interface its callers use. Each was designed
with the `/codebase-design` skill, and the rejected designs are under
[Decisions](#decisions).

### Outcome to reply: `Response` in `engine/inc/Engine.h`

```cpp
// The reply to an operation's outcome: a failure with the outcome's error
// code and detail, or a success that carries `data`.
Json Response(Outcome const& outcome, Json data = Json::object());
```

Defined in `engine/src/Engine.cpp` beside `Success` and `Failure`.
`Acknowledge` in `Commands.cpp` stays, because it turns a reply into the
completion of a command without data; its body calls `Response(outcome)`.

### Saved document: `Changes` in `engine/inc/Changes.h`

```cpp
// Commits `document`, which the storage worker encodes, so the engine
// thread never serializes it. A change calls this at most once.
template <typename Document>
void Commit(Document document, std::function<void(Outcome)> completion)
{
    Write([document = std::move(document)] { return document.ToJson(); }, std::move(completion));
}
private:
void Write(std::function<Json()> encode, std::function<void(Outcome)> completion);
```

`Write` is today's `Commit`, renamed and private, with its body unchanged in
`Changes.cpp`.

### Name choice: `Engine::State::ChooseNames`

Declared in `State.h` beside `NameChoice`, defined in `Files.cpp` beside
`PrepareNames`:

```cpp
// On the worker of the torrent's folder: releases its files, reads the names
// libtorrent holds, and chooses the names `params` asks for, with the renames
// that give them. Blocks on the drive and on libtorrent.
NameChoice ChooseNames(lt::torrent_handle const& handle, lt::add_torrent_params params,
    bool appendsSuffix, Layout layout);
```

The body is the six lines that both workers hold today: `ReleaseFiles`,
`get_renamed_files`, `export_filenames`, `PrepareNames`, `Renames`. `Renames`
keeps its declaration; `ChooseNames` becomes its only caller.

### Reload: `Engine::State::PrepareReload`

Declared in `State.h` beside `Reload`, defined in `Storage.cpp` beside
`ReloadStorage`:

```cpp
// On the torrent's worker: the parameters to add the torrent again from its
// resume data, paused and outside libtorrent's queue, read before its files
// are released. The paused torrent issues no disk job after the release.
// Throws once Exit has begun.
lt::add_torrent_params PrepareReload(lt::torrent_handle const& handle, std::stop_token stop);
```

The body: `get_resume_data(save_info_dict)`, clear `seed_mode` and
`auto_managed`, set `paused`, `ReleaseFiles({handle})`, and the
`shutting_down` throw when `stop` is requested. Each caller then changes the
parameters for its own work.

### Deleted files: `Scope::Deleted`

Declared in `Scope` in `State.h`, defined in `Files.cpp` beside `FileScope`:

```cpp
// The selection's files that a deletion removes: those no outside torrent
// uses.
std::vector<std::filesystem::path> Deleted() const;
```

The body is `files` without `kept`. Both are sorted by `PathBefore` and hold
each path once, so `std::set_difference` with `PathBefore` gives the result of
today's binary search per file.

### An addition of listed content: `Engine::State::EndDuplicate`

Declared in `State.h` beside `Abandon`, defined in `Additions.cpp`:

```cpp
// Ends an addition of content the list holds, through `end`: refused with
// torrent_removing while that torrent is being removed, or as that torrent's
// duplicate. Returns false, and calls nothing, for content the list does not
// hold.
bool EndDuplicate(lt::info_hash_t const& hashes, std::function<void(Outcome, Added)> const& end);
```

### Content paths: `VisitContent` in `engine/inc/Torrent.h`

A template beside `ContentPath`, under the comment "Rules about torrent content
that previews share with torrents":

```cpp
// Calls `visit` with the index and the path under `layout` of each file of
// the content, which leaves out padding files, in file order.
template <typename Visit>
void VisitContent(lt::file_storage const& files, Layout layout, Visit const& visit)
{
    for (auto index : files.file_range())
    {
        if (!files.pad_file_at(index))
            visit(index, ContentPath(files, index, layout));
    }
}
```

### Seeding limits: members of `Settings` and `Torrent`

In `Engine::State::Settings` (`State.h`), inline as `Proxy::IsComplete` is:

```cpp
// A seeding limit is set: a ratio, a seeding time or an inactivity time.
bool LimitsSeeding() const { return ratio != 0 || seedingMinutes != 0 || inactiveMinutes != 0; }
// A seeding limit that time reaches is set: a seeding time or an inactivity
// time. An idle torrent sends no status update while these grow.
bool LimitsSeedingTime() const { return seedingMinutes != 0 || inactiveMinutes != 0; }
```

In `Torrent` (`Torrent.h`, defined in `Torrent.cpp`):

```cpp
// The seeding limits can stop it: it has finished, it is not stopped, and the
// person did not start it again after it finished.
bool SeedLimited() const;
```

The body is `status.is_finished && facts.intent != Intent::Paused &&
!facts.ignoresSeedLimits`. `LimitsSeedingTime` has three words because the
third separates the time limits from the ratio.

### Status query bound: `Engine::State::queryLimit` in `State.h`

Beside `collectionLimit`:

```cpp
// Each status query posts its own alert, and libtorrent's alert queue drops
// alerts past its size, so completion recovery and the seeding limits each
// keep at most this many queries outstanding.
static constexpr std::size_t queryLimit = 64;
```

### Handler registration: one table in `engine/src/Registration.cpp`

`KeyOwner` goes into `engine/inc/Enums.h`, because every engine enum lives
there:

```cpp
// The owner of the registry key that holds a value of the handler
// registration, which decides how removal and the start's checks treat it.
enum class KeyOwner
{
    // TinyTorrent's own key: removal deletes the key with everything in it.
    TinyTorrent,
    // A key that other applications use too: removal deletes only the value.
    Shared,
    // A key where Windows writes the same records itself: removal deletes
    // only the value, and the value alone is not a registration.
    Windows
};
```

In the anonymous namespace of `Registration.cpp`:

```cpp
// A value of TinyTorrent's handler registration. An empty name is the key's
// default value, as the registry functions read it.
struct Entry
{
    KeyOwner owner;
    std::wstring key;
    std::wstring name;
    // REG_SZ with `text`, REG_NONE with no data, or REG_DWORD written as 0.
    DWORD type;
    std::wstring text;
};

// TinyTorrent's handler registration for the copy at `path`, in the order
// registration writes it. The RegisteredApplications value, which records the
// request, is not in it: registration writes that value last and removal
// deletes it first.
std::vector<Entry> Handlers(std::wstring const& path = Executable());
```

The table holds what `RegisterHandlers` writes today, in the same order, built
from the existing constants (`classes`, `kinds`, `capabilities`,
`fileAssociations`, `urlAssociations`, `openWith`, `toasts`):

| Owner | Key under `HKEY_CURRENT_USER\Software` | Value | Type and data |
| --- | --- | --- | --- |
| TinyTorrent | `Classes\TinyTorrent.Torrent` | default | REG_SZ `TinyTorrent` |
| TinyTorrent | `Classes\TinyTorrent.Torrent\DefaultIcon` | default | REG_SZ `Icon(path)` |
| TinyTorrent | `Classes\TinyTorrent.Torrent\shell\open\command` | default | REG_SZ `OpenCommand(path)` |
| TinyTorrent | The same three for `TinyTorrent.Magnet` | | |
| TinyTorrent | `Classes\TinyTorrent.Magnet` | `URL Protocol` | REG_SZ, empty |
| Shared | `Classes\.torrent\OpenWithProgids` | `TinyTorrent.Torrent` | REG_NONE |
| TinyTorrent | `TinyTorrent\Capabilities` | `ApplicationName` | REG_SZ `TinyTorrent` |
| TinyTorrent | `TinyTorrent\Capabilities` | `ApplicationIcon` | REG_SZ `Icon(path)` |
| TinyTorrent | `TinyTorrent\Capabilities` | `ApplicationDescription` | REG_SZ `TinyTorrent` |
| TinyTorrent | `TinyTorrent\Capabilities\FileAssociations` | `.torrent` | REG_SZ `TinyTorrent.Torrent` |
| TinyTorrent | `TinyTorrent\Capabilities\URLAssociations` | `magnet` | REG_SZ `TinyTorrent.Magnet` |
| Windows | `Microsoft\Windows\CurrentVersion\ApplicationAssociationToasts` | `TinyTorrent.Torrent_.torrent`, `TinyTorrent.Magnet_magnet` | REG_DWORD 0 |

The four readers:

- **Present** (`HandlersPresent`): true when a TinyTorrent entry's key exists
  (`Exists`) or a Shared entry's value exists with its type (`Has`). Windows
  records do not count.
- **Complete** (`HandlersComplete`): the handler command starts this copy, as
  today, and every entry of `Handlers(Target(command))` exists with its type
  (`Has`); a REG_SZ entry also reads its `text` (`Read`). A REG_DWORD record's
  data is not compared. The test of the RegisteredApplications value goes,
  because `Repair` returns before `HandlersComplete` when that value is not
  `capabilities`.
- **Write** (`RegisterHandlers`): each entry by its type, then the
  RegisteredApplications value.
- **Remove** (`UnregisterHandlers`): the RegisteredApplications value first;
  then `RegDeleteTreeW` on each TinyTorrent entry's key and `Remove` on each
  other entry's value; then `SHChangeNotify`, as today. Each owned root comes
  before its subkeys in the table, so a subkey's own call finds it gone, and
  `Check` accepts `ERROR_FILE_NOT_FOUND`.

`Execute`'s direct associations (the `.torrent` default and the `magnet`
command) are not handler entries: they claim the default only on the person's
request, and removal leaves them, as
[lifetime](../../contracts/engine/lifetime.md#windows-registration) says. They
stay in `Execute`.

## Callers and the member each calls

| Caller | Today | Calls |
| --- | --- | --- |
| `Commands.cpp` 109 `Acknowledge` | Ternary by hand | `Response(outcome)` |
| `Commands.cpp` 183 `Settings`, 189 `ResetSettings` | Ternary with `settings.ToJson()` | `Response(outcome, settings.ToJson())` |
| `Commands.cpp` 327 `Add` | Ternary with the added torrent | `Response(outcome, {{"torrent_id", ...}, {"duplicate", ...}})` |
| `Commands.cpp` 463 `Edit` | Ternary with the edit's data | `Response(outcome, std::move(data))` |
| `Commands.cpp` 607 `Remove` and `DeleteFiles` | Ternary with `kept_files` | `Response(outcome, {{"kept_files", ...}})` |
| `Commands.cpp` 284 `Preview`, 626 `FileScope` | Failure by hand, success after | `Response(outcome)` in the failure branch; the success branch stays |
| `Commands.cpp` 778, 786, 804, 811 `AnswerTree` | Failure by hand from `expected` errors | `Response(listing.error())`, `Response(tree.error())` |
| `Application.cpp` 373, 384, the host's `Registration` case | Ternary, then `data`; failure by hand | `Response(outcome)`, then `data`; `Response(response.error().ToOutcome(...))` |
| `Additions.cpp` 303 `RecordWatch`, 362 `CommitAddition`; `Move.cpp` 932 `SaveMove`, 1031 `FailMove`; `Queue.cpp` 102 `Queue`; `Removal.cpp` 75 `Remove`; `Session.cpp` 373 `SaveTransfers`, 528 `CommitFacts`; `Settings.cpp` 722 `SaveSettings`; `Shutdown.cpp` 77 `SaveDocument`; `Tasks.cpp` 353 `RecordTasks`, 525 `Answer`; `Watching.cpp` 79 `WatchFolder` | `Commit` with an encoding lambda | `changes.Commit(std::move(document), ...)` or `changes.Commit(Saved(), ...)` |
| `Additions.cpp` 211 `PrepareAddition` | Name worker by hand on `AdditionWorker` | `ChooseNames` inside its `AdditionWorker` job |
| `Files.cpp` 306 `ContinueNames` | Name worker by hand on `VolumeWorker` | `ChooseNames` inside its `VolumeWorker` job |
| `Storage.cpp` 776 `Reload` | Reads, flags, releases | `PrepareReload`, then `DropPieces` and clears `no_verify_files` and `upload_mode` |
| `Move.cpp` 774 `ReloadMove` | Reads, flags, releases | `PrepareReload`, then its pieces, folder, names and its mode's flags |
| `Removal.cpp` 118 `PrepareDeletion` | Kept files removed by hand | `scope.Deleted()` |
| `Tasks.cpp` 324 `DeferredDeletion` | Kept files removed by hand | `scope.Deleted()`, then its own test of folders whose names are unknown |
| `Additions.cpp` 67 `Add` | Removing, then duplicate | `if (EndDuplicate(preview.InfoHashes(), completion)) return;` |
| `Additions.cpp` 538 `On(add_torrent_alert)` | Removing, then duplicate, then `add_failed` | `EndDuplicate` with an `end` that calls `Abandon`; `Abandon` with `add_failed` when it returns false |
| `Torrent.cpp` 308 `VisitPaths`, logical branch | Loop by hand | `VisitContent(layout, facts.layout, ...)` that passes each path to `visit` |
| `Torrent.cpp` 524 `Folder` | Loop by hand that returns early | `VisitContent`; see decision 10 |
| `Previews.cpp` 40 `SharedFiles` | Loop by hand for `Tops` | `VisitContent(layout, settings.layout, ...)` |
| `Files.cpp` 1053 `FilePaths(lt::torrent_info const&, ...)` | Loop by hand | `VisitContent` that adds each full path |
| `Files.cpp` 361 `PrepareNames` | Loop by hand | `VisitContent`; its one `continue` becomes `return` |
| `Tree.cpp` 165 `Tree::Tree` | Loop by hand | `VisitContent` with the loop's body |
| `Policy.cpp` 484 `LimitSeeds`, 576 `ReachedSeedLimit` | Three zero tests | `!settings.LimitsSeeding()` |
| `Policy.cpp` 488 `LimitSeeds`, 548 `QuerySeeds` | Two time-limit tests | `settings.LimitsSeedingTime()` |
| `Policy.cpp` 494, 561, 577 | The torrent's three tests | `torrent.SeedLimited()`; 494 keeps `!torrent.waiting`, because it queries the torrent's handle |
| `Alerts.cpp` 471 `QueryCompletions`, `Policy.cpp` 555 `QuerySeeds` | Literal 64, local `pendingLimit` | `queryLimit`. `QueryCompletions` counts with `std::count_if`, which is signed, so it compares through a `std::size_t` count |
| `Registration.cpp` 365, 379, 834, 865 | Four lists | The four readers of `Handlers` |

In `Tasks.cpp` 353 the completion captures `document.tasks` beside the
document. The order in which C++ evaluates the two arguments is not fixed, so
`RecordTasks` copies `document.tasks` into a local before the call and moves
the document into it.

## Findings to re-read

- **Reload from resume data** (`Storage.cpp` `Reload`; `Move.cpp`
  `ReloadMove`): **kept**. The rule: a reload reads the old handle's resume
  data as the parameters to add the torrent again, paused and outside
  libtorrent's queue, and releases the old files before the engine thread
  swaps the handle. Both workers write it with the same comment ("The paused
  torrent issues no disk job after this release"), and the
  [engine contract](../../contracts/engine.md#persistence-and-file-safety)
  makes one owner serve every reload and move. `PrepareReload` owns it.
- **The files a deletion removes** (`Removal.cpp` `PrepareDeletion`;
  `Tasks.cpp` `DeferredDeletion`): **kept**. The rule: a deletion removes the
  selected torrents' files that no outside torrent uses. `Scope::Deleted` owns
  it. `DeferredDeletion` also keeps every file under the folders of a torrent
  whose names are unknown
  ([files](../../contracts/engine/files.md#unfinished-tasks), "While another
  torrent's names are not read"). That second rule has only this one copy:
  `PrepareDeletion` runs only when `NamesReadyFor` holds, and then no torrent
  with unknown names has a folder that overlaps a deleted file.
- **Source and target pairs** (`Move.cpp` `StartMove` 141, `PrepareMove` 351,
  `ContinueMove` 581, `Relocated` 700; `Files.cpp` `FileScope` 1210):
  **dropped**. The copies write one expression, `FullPath(root / path)`, with
  the save folder and the destination as roots, beside different work.
  `StartMove`'s conflict maps need every file, also a member's file already at
  the destination, because that member still claims its paths. `PrepareMove`
  pairs physical and logical names with each file's size. `ContinueMove` marks
  the files already placed. `Relocated` keeps only the files whose path
  changes, for its five callers. A member that returned every pair would leave
  each caller its own loop and add a filter, so it would only rename the
  expression. `FileScope` pairs one file's physical and logical names in the
  same folder, which is another rule.
- **The seeding-limit predicates** (`Policy.cpp` 484, 494, 561, 576): **kept**.
  The rules: a seeding limit is set when a ratio, a seeding time or an
  inactivity time is set; the time limits need a status query; and a torrent
  meets the limits only when it has finished, is not stopped, and was not
  started again after it finished. The members on `Settings` and `Torrent` own
  them.
- **The guard for a new torrent** (`Additions.cpp` `Guard`; `Session.cpp`
  `Start` 216): **dropped**. `Guard` prepares content that the engine never
  held: no seed or share mode from its source, and no file wanted until its
  choices apply, as the
  [engine contract](../../contracts/engine.md#state-and-work) says ("A new
  addition is added with no file wanted"). The restore adds a torrent that the
  engine saved: with metadata it keeps the saved file choices, and it keeps the
  saved seed mode unless the files must be checked again (`Session.cpp` 191
  to 197). The flags they share serve two different rules, so `Guard` cannot
  serve the restore.
- **The result of a conflicting add** (`Additions.cpp` 67, 538): **kept**. The
  rule: an addition of content the list holds ends with `torrent_removing`
  while that torrent is being removed, and otherwise as its duplicate, whether
  the engine finds it before libtorrent's add or libtorrent refuses the add.
  `EndDuplicate` owns it.
- **The content-path loop** (`Torrent.cpp` 309, 524; `Previews.cpp` 40;
  `Files.cpp` 1053; `Tree.cpp` 165): **kept**, with a sixth copy that the audit
  missed in `Files.cpp` `PrepareNames` 361. The rule: a torrent's content is its
  files without padding, each at its `ContentPath` under the layout.
  `VisitContent` owns it.
- **The limit of 64 queries** (`Alerts.cpp` 471; `Policy.cpp` 555): **kept**.
  The rule: a query that posts one alert per torrent keeps at most 64
  outstanding, so a large list cannot fill libtorrent's alert queue. The
  [engine contract](../../contracts/engine.md#state-and-work) and
  [settings](../../contracts/engine/settings.md) state the bound for each.
  `queryLimit` owns it.

## Open question: does Windows change these values itself?

**`ApplicationDescription`: no.** It lives in TinyTorrent's own key,
`Software\TinyTorrent\Capabilities`. Microsoft's
[Default Programs](https://learn.microsoft.com/en-us/windows/win32/shell/default-programs)
page calls it required, and says that an application without it does not
appear in the lists of possible default programs. The page describes Windows
reading the Capabilities key, never writing it. On the owner's machine the
value holds `TinyTorrent`, which registration wrote. So the check includes it,
with its text. The check also includes each class's name (the class key's
default value), which registration writes and the check omitted too: it lives
in TinyTorrent's own class key.

**The toast records: Windows writes the same records itself.** Microsoft does
not document the `ApplicationAssociationToasts` key. The Default Programs page
documents what the records remember: Windows shows its notification about a
new handler only while it has not shown that handler in a notification, an
Open with flyout or the defaults page. On the owner's machine, read without a
change on 12 November 2026, the key holds 425 records, all REG_DWORD 0, and 314
of them name classes of packaged apps, which Windows registers from their
manifests. TinyTorrent's two records are there with the same type and data.
No document says whether Windows changes or deletes a record later. So:

- The check counts a record by its name and type, not its data, so it never
  writes over a record Windows keeps.
- The presence test ignores the records, because a record alone does not show
  a registration; counting it could make every start unregister.
- A missing record makes the next start's repair write the handler entries
  again. Without the record, Windows asks which app to use at the next open
  ([lifetime](../../contracts/engine/lifetime.md#windows-registration)), so the
  repair fixes a failure the person would see.

## Decisions

The plan leaves these open; each is decided by the engine contract, the Owner
rulings and usability.

1. **`Response` lives in `Engine.h`, not in `Commands.cpp`.** The plan's table
   of owners names `Commands.cpp`. The host's registration case in
   `Application.cpp` also turns an outcome into a reply, and `Success` and
   `Failure` already live in `Engine.h`. Rejected: an overload
   `Failure(Outcome)`, which leaves the six ternaries as copies; a member of
   `Outcome`, which would put the pipe's reply shape into an operation result
   that the tray also uses.
2. **Two callers keep their failure branch.** `Preview` dereferences a preview
   that a failure leaves null, and `FileScope` reads torrents that a failure
   can have removed, so neither builds its data before the outcome is known.
3. **`Changes::Commit` is a template.** `State.h` includes `Changes.h` for its
   `changes` member, and `Document` is a type inside `Engine::State`, so
   `Changes.h` cannot name it. Rejected: a member `Engine::State::Commit` that
   forwards to `Changes`, which is a second member for one rule; moving
   `Document` out of `State`, which holds the nested `Settings`,
   `TransferTotals` and `Task`.
4. **`ChooseNames` takes the handle and two facts, not the torrent.** The
   worker reads no engine state, as the lambdas capture today. It does not
   schedule the job, because the two callers use different workers and
   different completions.
5. **`PrepareReload` releases before the caller's changes.** Today each worker
   changes the parameters and then releases. The changes edit only the
   parameters in memory, and `ReloadMove`'s `exists` test reads only the
   destination, so the order does not matter. A throw on Exit now skips those
   changes, and both completions drop a result after Exit began
   (`IsShuttingDown`), as they do today.
6. **`EndDuplicate` calls the end it receives.** Both callers end an addition
   through a `void(Outcome, Added)` function: `Add` through its completion,
   the alert through `Abandon`. Rejected: a named result type, which is a new
   type for the two values that completion already carries, and whose natural
   name, conflict, already means an alias conflict in `State`
   (`torrent.conflict`, `MarkConflict`).
7. **`VisitContent` is a template with an index and a path.** `Tree` and
   `PrepareNames` need each file's index, and `Tree` runs for every file of a
   torrent with many thousands of files. Rejected: a `std::function` visitor,
   which adds an indirect call per file; a range adapter over
   `file_range()`, because libtorrent's `index_iter` is not a standard
   iterator, so it needs a custom iterator class.
8. **The seeding facts are members of their models.** The plan's table names
   `Policy.cpp` as the owner; the policy stays there (`LimitSeeds`,
   `QuerySeeds`, `ReachedSeedLimit`), and the facts it reads move to
   `Settings` and `Torrent`, as the plan's rule "Prefer a type to a helper"
   asks.
9. **`SeedLimited` leaves out `!waiting`.** `QuerySeeds` reads only torrents
   that libtorrent holds, through `Find(handle)`. `ReachedSeedLimit` today
   accepts a torrent that waits to be added again with a finished status, and
   it keeps doing so, so no behavior changes. `LimitSeeds` keeps
   `!torrent.waiting`, because it queries the torrent's handle.
10. **`Torrent::Folder` joins `VisitContent`.** Its result does not change: after
    the first file outside the first file's top folder, it stops converting
    paths and returns the save folder. It no longer stops the loop there, so it
    builds the remaining content paths. In the common case, files in one top
    folder, it reads every file today too.
11. **`queryLimit` covers the two alert-query bounds only.** `Files.cpp`
    `QueryFiles` 469 stops at 64 torrents for one worker visit that posts no
    alert, a separate rule in the
    [engine contract](../../contracts/engine.md#state-and-work) ("batches of
    at most 64 torrents").
12. **The request record stays outside the table.** Registration writes it last
    and removal deletes it first, and the presence test ignores it, so it has a
    rule of its own that `Repair` already reads.
13. **Behavior change: the start's check reads every value registration
    writes.** A start on the default store whose registered handlers miss a
    class name, `ApplicationDescription` or a toast record now writes the
    handler entries again, with `SHChangeNotify`. Before, it left them. See the
    open question.
14. **Removal deletes TinyTorrent's keys as today,** whole, through the key of
    each TinyTorrent entry. The toast records go after Capabilities instead of
    before it; no person sees the order.
15. **An empty value name means the default value.** The registry functions
    define that convention (`RegSetValueExW`, `RegGetValueW`,
    `RegDeleteValueW`), and the toast records' names are built strings, which a
    `wchar_t const*` field could not hold.
16. **`Application.cpp` waits for stage 15.** It holds stage 15's uncommitted
    edits in `ConfirmExit`. Its two lines in the `Registration` case change
    once those edits are committed, or in the same batch if the lead joins
    batches G and H. Until then, stage 14 is not finished.

## Seen and left

- `Previews.cpp` `Inspect` 264 refuses a preview of content that is being
  removed, and allows a preview of a listed torrent, which shows the
  duplicate. That is the preview's rule, not the end of an addition.
- `Additions.cpp` `StartAddition` 161 prepares names before libtorrent holds
  the torrent, with no handle to release or names to read, so it calls
  `PrepareNames` alone.
- `Files.cpp` `Completed` 538 lists finished files, not the content. Padding
  never carries the unfinished suffix, so it never matches.
- `Move.cpp` `PrepareMove` 341 and `ReloadMove` 788 step through the layout
  beside `Paths()`, by position. They read `Paths()`, the torrent's owner of
  its paths.
- `Commands.cpp` 150 and `Detail.cpp` 136 reply with a refusal code, not an
  outcome.

## Consumers of each changed name

Searched in `engine/`, `app/src` with the capture journeys, `app/tests`,
`engine/tests`, `installer/` and `lib/*/tests`:

- `Response`, `ChooseNames`, `PrepareReload`, `Scope::Deleted`,
  `EndDuplicate`, `VisitContent`, `LimitsSeeding`, `LimitsSeedingTime`,
  `SeedLimited`, `queryLimit`, `KeyOwner`, `Entry` and `Handlers` are new. No
  file names them today. Their callers are the table above.
- `Changes::Commit` changes its parameter. Its consumers are the 13 sites
  above; `Changes.cpp` holds `Write`. No test names `Changes`.
- `HandlersPresent`, `HandlersComplete`, `RegisterHandlers` and
  `UnregisterHandlers` keep their names. Their consumers are
  `Registration::Repair` and `Registration::Execute`.
- `QuerySeeds` loses its local `pendingLimit`.
- `engine/tests/Transitions.cpp` includes `Torrent.h`. It uses neither new
  member, but the template compiles there.
- The registry values are named only in `Registration.cpp` and in lifetime.
  The installer registers through the engine's `--registration` option.
- The pipe replies keep their shape: `ok`, then `data` or
  `error:{code,detail}` ([pipe API](../../contracts/pipe-api.md)). So the app,
  its capture journeys and its check scripts consume no change.

## Files the change may edit

`engine/inc/Engine.h`, `engine/src/Engine.cpp`; `engine/inc/Changes.h`,
`engine/src/Changes.cpp`; `engine/inc/Engine/State.h`; `engine/inc/Torrent.h`,
`engine/src/Torrent.cpp`; `engine/inc/Enums.h`;
`engine/src/Engine/Additions.cpp`, `Alerts.cpp`, `Commands.cpp`, `Files.cpp`,
`Move.cpp`, `Policy.cpp`, `Previews.cpp`, `Queue.cpp`, `Removal.cpp`,
`Session.cpp`, `Settings.cpp`, `Shutdown.cpp`, `Storage.cpp`, `Tasks.cpp`,
`Watching.cpp`; `engine/src/Tree.cpp`; `engine/src/Registration.cpp`;
`engine/src/Desktop/Application.cpp`, its two lines only, as decision 16 says;
`docs/contracts/engine/lifetime.md`.

Stage 13 edits `Engine.h`, `Engine.cpp`, `Torrent.cpp`, `Commands.cpp`,
`Additions.cpp`, `Alerts.cpp`, `Files.cpp`, `Move.cpp`, `Removal.cpp`,
`Session.cpp`, `Storage.cpp`, `Tasks.cpp` and `Registration.cpp` first. The
places overlap in two functions: stage 13 replaces the sort and the duplicate
removal at the end of `FilePaths`, and the `symlink_status` tests inside
`PrepareNames`; stage 14 changes only their loop. Stage 14 builds on stage 13's
names, such as its two names for `SameFolder`, wherever it meets them.

## Proposed contract text

For [lifetime](../../contracts/engine/lifetime.md#windows-registration), after
the sentence that ends "even while TinyTorrent is the default.":

> Windows writes the same records for the handlers it announces, so the
> start's check counts a record by its presence, and a record alone is not a
> registration.

`Response`, `Changes` and the other owners change no contract.

## Checks

The build is batch G's one engine build (`Build.ps1 -Engine`), shared with
stage 13. Each case below runs because the owner asked for proof that nothing
regressed, and its subject changes:

| Case | What changes under it |
| --- | --- |
| `FailedCommit` | A failed commit through `Changes::Commit`, and its `storage_failed` replies through `Response`, for `add` and `settings` |
| `Restart` | Successful commits of `add`, `piece_order`, `speed_limit`, `pause`, `session_pause` and `settings`, their replies, and the document a restart reads |
| `FileNames` | `ChooseNames` from both callers, and `PrepareNames` through `VisitContent` |
| `FileWindow` | `Tree` through `VisitContent`, with its timings, and the `tree` and `edit` replies |
| `FilesSafety` | `Scope::Deleted` for Remove and Delete files, the deferred deletion, the removal's commit and the `kept_files` reply |
| `StorageStop` | `PrepareReload` through `Reload`: `Control::Continue` reloads a check that a read error stopped (`Control.cpp` 436 to 440) |
| `MoveExisting` | `PrepareReload` through `ReloadMove` in files mode, after a reconciliation |
| `MoveRecovery` | `PrepareReload` through `ReloadMove` in download mode, and the save of the failed moves (`FailMove`, `Move.cpp` 1031) |
| `RemoveDuringMove`, with `-OptIn` and `-Folder` on another volume | `EndDuplicate`: an add during a removal answers `torrent_removing` |

Not run, with the reason:

- The other commit sites (`Queue.cpp`, `Session.cpp` 373, `Shutdown.cpp`,
  `Tasks.cpp`, `Watching.cpp`): each changes only the argument of the same
  `Commit`, which `FailedCommit` and `Restart` prove in both outcomes; the
  compiler and the code settle the rest.
- The seeding limits and `queryLimit`: the members return the same expressions,
  which reading settles, and no check sets a seeding limit.
- Registration: no check can register without changing the owner's own file
  handlers, so source review is the evidence. What stays unverified at run
  time is only the new repair after a missing value (decision 13).
- `Transitions.exe`: the storage control does not change.
- Transfer checks, captures and app checks: no transfer, window or app code
  changes.

## Cost

No build in this session, and no build of its own: stage 14 joins batch G's one
engine build with stage 13. About four hours of agent work for about 55 call
sites in 24 code files, and about one hour for the independent review passes. The
nine checks take an estimated 40 minutes of the laptop; their durations are not
measured, and `StorageStop` alone waits up to three minutes by design.

## Implementation notes

Stage 14 is implemented on top of stage 13's uncommitted work, as its
[gate record](stage-13.md) describes it, and with stage 15's edits in
`Application.cpp` kept. Each difference from the record above:

- **Stage 13 names.** `PrepareNames` now calls stage 13's `Present` inside the
  `VisitContent` visitor, and `FilePaths` ends with stage 13's `SortUnique`;
  stage 14 changed only their loops, as planned. `IsChoice` moved from
  `Engine::State` to `Torrent.h` in stage 13; stage 14 does not use it.
  `Registration.cpp`'s `Same` became `SameName` and its COM set-up `Apartment`;
  the table does not use either. No stage 14 design depended on a stage 13 name
  that differs from the plan's text.
- **Line numbers.** Stage 13's edits moved most lines by a few. The
  `Commands.cpp` replies are now at 108, 182, 188, 283, 326, 462, 606, 624,
  776, 784, 802 and 809.
- **The registry writer.** `Registration.cpp` gains one general
  `Write(key, name, type, data, size)`, which creates the key and sets the
  value; the existing string `Write` calls it. `RegisterHandlers` writes each
  entry through it by type. The toast records are therefore written with
  `RegCreateKeyExW` and `RegSetValueExW`, as the Open with value already was,
  instead of `RegSetKeyValueW`; the key and value are the same.
- **The association rows come from `kinds`.** The table builds the
  `FileAssociations` and `URLAssociations` rows from `kinds`, as it builds the
  toast rows, in the same order as before. The constants `fileAssociations` and
  `urlAssociations` had no other reader and are gone.
- **`SaveDocument`** passes `Saved()` straight to `Commit`, without its local.
- **`RecordTasks`** names its copy `saved`, because the function already has a
  local `tasks`.
- **`QueryCompletions`** counts into a `std::size_t`, so the comparison with
  `queryLimit` has no signed and unsigned mix.
- **`Torrent.h`** includes `<libtorrent/file_storage.hpp>`, because the
  `VisitContent` template calls `file_storage` members. Every engine file
  already received it through the precompiled header.
- **Glossary.** `CONTEXT.md` gains **Padding file** and **Seeding limit**, the
  terms that `VisitContent` and the seeding members rely on.
- **Contract.** The proposed sentence is in
  [lifetime](../../contracts/engine/lifetime.md#windows-registration).
- **Behavior changes:** only decision 13. Every other change keeps what the
  engine does and replies.

## Review fixes

The review of the stage 14 diff (scratchpad record `review-stage14.md`)
found two items; the lead fixed both.

- Medium: `Torrent::Folder` no longer stopped early, so a torrent saved
  with `Layout::Strip` whose files share no top folder built one path per
  file on each General read and dropped it. `VisitContent` now ends the walk
  when a visitor that returns bool returns false, and `Folder`'s visitor
  returns false once the files share no top folder. The other visitors
  return nothing and walk every file, as before.
- Low: the reason that an idle torrent sends no status update was written
  in `State.h` and in `Policy.cpp`. It stays in `Policy.cpp`, beside the
  loop it explains.
