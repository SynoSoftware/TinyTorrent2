# Stage 7 gate record: storage

Gate done on 12 November 2026, by reading, on the working tree that holds
stages 1, 2, 3 and 16b. Nothing was built or run. This record takes the
place of [the stage's text](../22-one-owner-per-rule.md#7-storage) for the
session that implements it. Paths without a prefix are under `app/src`. Line
numbers are those of that working tree; each reference also names its
member, because stages 4 and 5 edit some of these files first.

## Where the stage stands

Every item has an owner, an interface and its callers. The stage adds one
file, `Shared/Storage/StateFile.cs`, and one temporary view,
`entry_identifications`. It adds no text, so no catalogue changes, and no
stored schema change, so no revision. A person sees a difference only in the
edge cases listed under [What changes for a person](#what-changes-for-a-person).

The decisions, each with its reason below:

1. `Database` owns three static members that every SQLite connection of the
   app uses: `Connect`, `Execute` and `IsDamaged`.
2. One temporary view, `entry_identifications`, answers "which
   identification decides this Library entry". Seven Library queries read
   it. Source reconciliation does not.
3. The view does not require a video. This answers the open question.
4. `StateFile<T>` in `Shared/Storage` owns the small JSON state files
   `window.json` and `updates.json`.
5. `Store.Buckets` is the one table of the filter sections whose values are
   fixed groups. `Strings.Get(FileKind)` and `Browser.TypeText` name a
   file kind and a video type once.
6. In Subtitles, `Video.Read` with its column list, `Associate` and
   `RetryNow` replace the copies. `AssociateOutput` keeps its own SQL.

## Gate step 1: the findings, read again

| Finding | Rule | Result |
| --- | --- | --- |
| Connection options, cancel hook, interrupt mapping | Every SQLite connection of the app is unpooled, so closing it closes the file; a cancellation interrupts a running statement through SQLite's progress handler; an interrupted statement whose token was cancelled ends as a cancellation. | Kept. `Database.Open` 181 to 189 and `Dataset.Connect` 508 to 530 (options); `Database.Transact` 256 to 268 and `Dataset.Connect` 523 to 528, `Dataset.Import` 612 to 615 (hook); `Database.Run` 99 to 105 and `Dataset.Read` 403 to 409 (mapping). The gate found a fourth copy in the same lines: SQLite reports a damaged or foreign file as `SQLITE_CORRUPT` or `SQLITE_NOTADB` (`Database.Run` 106, 107; `Dataset.Read` 412, 413). |
| Which identification applies | An entry is decided by the latest explicit choice of its files, including Clear; without one, by the latest provider match whose file still has the name it was matched under. | Kept. Copies: the `library_entries` view (`Database/Schema.cs` 205 to 209); `Store.Fetchable` 161 to 166; `Store.Begin` 299 to 303 and 318 to 325; `Store.Undecided` 11 to 15; `Store.Series` 465 to 473. The gate found one more: `Store.Begin` 284 to 287 asks whether the person cleared the file and reads any Clear among the location's files, where `Fetchable` reads the deciding one. `Database/Files.cs` 50 (`Reconcile`) is another rule, see decision 2. |
| JSON state file loader and saver | A small JSON state file reads as no saved state when it is missing or unreadable, and a save writes a temporary file and renames it over the old one; a failure is written to the debug output only. | Kept. `Updates.CheckRelease` 114 to 128 and `Updates.SaveCheck` 419 to 433; `MainWindow.RestorePlacement` 143 to 147 and 204 to 210, `WritePlacement` 320 to 330, `ForgetPlacement` 338 to 347. No third product file reads or writes such a file; the capture journeys write their own evidence files. |
| Library filter vocabulary | A filter section whose values are fixed groups of files (Date modified, Size, Resolution, Subtitles, Metadata) lists its values in one order, counts each by its own condition and names each by its `library` message. | Kept. The five sections are listed in `Store.Search` 239, `Store.Match` 702 and `Browser.FilterText` 835; their values in `Store.Search` 246 to 256, `Store.Dates` and `Store.Sizes` 742 to 762, and again in `Store.Filter` 783 (resolution). |
| File kind text (left by stage 3) | A file kind is named by the `library` message whose key is the kind's name in lower case. | Kept. `Browser.FilterText` 833; `LibraryRow.Kind` (`Browser.cs` 1440). |
| Video type text (found by the gate) | A video's type is named by the `library` message of that type, and a video without one reads Not identified. | Kept. `Browser.FilterText` 834; `LibraryRow.Type` (`Browser.cs` 1389). |
| Subtitles video row reader | A subtitle target's video is read from its collected file, its location, its readiness and its torrent's completion, in one column order. | Kept. `Acquisition.Targets` (`Acquisition/Storage.cs` 159 to 185) and `Acquisition.Movies` (`Acquisition/Discovery.cs` 316 to 345) read the same nine columns into the same `Video` fields. |
| Output-source upsert | Recording that a subtitle serves a video records the movie location it served, and recording it again moves it there. | Kept for the two identical statements: `Acquisition.Record` (`Storage.cs` 581 to 590) and `Acquisition.Scan` (`Discovery.cs` 540 to 548). `AssociateOutput` (`Storage.cs` 380 to 402) stays: it records only the videos that are still collected at the movie's location, because `Record` calls it for a subtitle the app did not create. The created file must be recorded even when its movie moved meanwhile, so the two rules differ. |
| Reset of pending jobs | A route change and a reset of search protection let every pending job search again at once. | Kept. `Acquisition.ChangeRoute` (`Acquisition.cs` 180 to 184) and `Acquisition.ResetProtection` (228 to 234). |

These stay, each with its reason:

- `Database/Files.cs` 47 to 63 (`Reconcile`) ranks identifications by the
  same order but over confirmed files only and copies explicit choices only.
  The [database contract](../../contracts/database.md#reconstructed-state)
  requires that scope: "Choices and ownership shared across a location join
  only confirmed files, because a cached file can still name a path its
  torrent has left." Its own comment (47 to 49) says the same. A view over
  confirmed files would be a second view for one reader.
- `Store.Begin` 313 to 317 and `Store.End` 416, 417 read the latest
  `decided` of the location. That is the guard against a late reply, not
  the rule of which identification applies.
- The filter sections each configuration offers (`Store.Search` 205 to 232),
  the group heading and the glyph of each section
  (`Shell/MainWindow/Library.cs` 427 to 433, 453 to 467) and the column
  sections of `Store.Match` 704. Each has one owner already, and each is a
  different rule: which sections a configuration shows, how the filter pane
  presents a section, which section names may become SQL column names.
- `DatabaseVersionException` and `ProviderException(VideoFailure.Damaged)`
  stay two consequences of a damaged file: `library.db` waits for the
  person to delete it, the IMDb data for a new download. Only the
  classification becomes one.

## Gate step 2: the open question

**Question: `Store.Undecided` (`Library/Store/Identification.cs` 14) also
requires a video, and the other copies do not. Which one does the Library
contract mean?**

**Answer: the copies without the video requirement. An automatic
identification always has a video, so the requirement selects nothing on
any database the app can open. The view does not test it, and
`Undecided`'s condition goes with no change in behavior.**

The evidence:

- The contract. [Identification and providers](../../contracts/library/providers.md#identification-and-early-enrichment)
  97 to 103: manual decisions take precedence, "the most recently saved
  explicit choice, including Clear, applies to all contributors", and
  "Resolve that effective choice once for search, rows and details".
  [Library](../../contracts/library.md) 283, 284 stores "the decision to
  clear an incorrect identification". The
  [database contract](../../contracts/database.md#durable-state) 75:
  `identifications` holds "Accepted provider matches and explicit user
  decisions, including an explicit clear … Ordinary unsuccessful attempts
  are not decisions." So an explicit row may lack a video, and an automatic
  row is an accepted match.
- Every writer. `Store.SaveIdentification` (`Store.cs` 516 to 541) is called
  by `Store.End` (`Identification.cs` 439 to 447) only when `video is not
  null`, and by `Store.EraseMetadata` (`Store.cs` 507) without a video and
  with `isExplicit: true`. `Database.Reconcile` (`Files.cs` 56 to 59) copies
  explicit rows only. `Store.PrepareTiming` (`Store.cs` 72 to 74, capture
  build only) writes explicit rows with a video. No writer creates an
  automatic row without a video.
- Saved databases. The condition dates from commit 4392f96 (9 October
  2026), whose `Store.End` saved an automatic no-match as an identification
  without a video (`Identification.cs` 115, 116 of that commit). Commit
  77a3317 (12 October) stopped it and added the contract sentence above.
  Schema revision 9 dates from 3adf807 (15 October), and `Database.Open`
  refuses every other revision (`Database.cs` 211). So no database the app
  opens holds such a row.

The schema allows an automatic row without a video, which no writer creates.
A `CHECK` would be a migration; the stage does not need it.

## Gate step 3: owners, interfaces, callers

### `Database`: connection options, cancellation, damaged file

Candidates, compared by whether each rule gets one place:

| Candidate | Result |
| --- | --- |
| A. `Connect(path, mode)`, `Execute(connection, action, cancellation)` and `IsDamaged(error)` | **Chosen.** `Execute` holds the hook, its removal and the mapping, which belong together: the hook's only effect is the interruption that the mapping translates. |
| B. `Connect(path, mode, cancellation)` registers the hook for the connection's life, with a separate interrupt filter | Rejected. `library.db` has one connection that serves operations with different tokens, so its hook must follow each transaction; the hook would be written twice again. |
| C. `Database.Read<T>(path, mode, action, cancellation)` that opens, runs and closes | Rejected. The long-lived `library.db` connection cannot use it, so the hook and the mapping would stay in two places. |

The interface, in `Shared/Storage/Database.cs`:

```csharp
/// <summary>
/// Opens the SQLite file at <paramref name="path"/> as every connection of
/// the app is opened. Without pooling, closing the connection closes the
/// file, so the file can be replaced or deleted afterwards. A lock that
/// another connection holds is waited for at most five seconds.
/// </summary>
internal static SqliteConnection Connect(string path, SqliteOpenMode mode);

/// <summary>
/// Runs <paramref name="action"/> on <paramref name="connection"/> so that
/// <paramref name="cancellation"/> interrupts a long statement, such as an
/// index rebuild. An interrupted statement ends as an
/// <see cref="OperationCanceledException"/>.
/// </summary>
internal static T Execute<T>(
    SqliteConnection connection,
    Func<SqliteConnection, T> action,
    CancellationToken cancellation
);

/// <summary>Whether SQLite reports the file as damaged or as no database.</summary>
internal static bool IsDamaged(SqliteException error);
```

What each holds:

- `Connect`: today's builder from `Database.Open` 181 to 189 (`DataSource`,
  `Mode`, `Pooling = false`, `DefaultTimeout = 5`), then `Open()`. It
  disposes the connection when `Open` throws. The comment of
  `Dataset.Connect` (505 to 507) moves to it.
- `Execute`: when `cancellation.CanBeCanceled`, it registers today's handler
  (`Transact` 257 to 262), runs the action, maps `SQLITE_INTERRUPT` with a
  cancelled token to `OperationCanceledException(null, error,
  cancellation)` and removes the handler in `finally`. Otherwise it runs the
  action alone.
- `IsDamaged`: `error.SqliteErrorCode is raw.SQLITE_CORRUPT or
  raw.SQLITE_NOTADB`.

| Caller | Today | After |
| --- | --- | --- |
| `Database.Open` 181 to 192 | Its own builder and `Open` | `var connection = Connect(_path, SqliteOpenMode.ReadWriteCreate);` then the collation, function, pragmas and `Transact` in the existing `try`, which disposes on failure |
| `Database.Transact` 254 to 269 | Registers and removes the handler | `result = Execute(connection, action, cancellation);` |
| `Database.Run` 99 to 111 | Interrupt filter; damaged filter | Interrupt filter deleted; `when (IsDamaged(error))` |
| `Dataset.Read` 397 to 424 | `Connect` with its own options; interrupt and damaged filters | `using var connection = Database.Connect(_path, SqliteOpenMode.ReadOnly); return Database.Execute(connection, read, cancellation);` then `when (Database.IsDamaged(error))` → `Damaged`, other `SqliteException` → `Unreadable`. The read lock and its comment stay. |
| `Dataset.Import` 537 to 616 | `Connect(Building, …, cancellation)`; removes the handler in `finally` | `using (var connection = Database.Connect(Building, SqliteOpenMode.ReadWriteCreate))` around `Database.Execute(connection, connection => { …today's body…; return true; }, cancellation)`. The `finally` at 612 to 615 goes. |
| `Dataset.Connect` 505 to 530 | | Deleted |

`Execute` follows the name of the platform's own commands
(`ExecuteNonQuery`); the token parameter carries the cancellation and the
connection parameter says whose connection runs it. Inside `Dataset`, the
private `Execute(connection, sql)` is the same concept for SQL text, and
`Database.Execute` is always qualified there.

Stage 9 then replaces `Building` with the build path in `Import` and moves
`Install`, `Delete` and `Discard` to `DataDownload`, as
[its record](stage-09.md#what-each-dataset-keeps) states. Stage 7 touches
none of those members.

### `entry_identifications`: which identification decides an entry

| Candidate | Result |
| --- | --- |
| A. A temporary view with one row for each Library entry | **Chosen.** One SQL definition owns both parts of the rule, which identifications apply and which one wins. Every reader joins it by `entry_id`. |
| B. A SQL text constant that names an outer alias, as `Undecided` does | Rejected. The alias is a hidden contract with every reader, and the snapshot view in `Schema.cs` would have to interpolate C# text. |
| C. A view of every identification that applies, and each reader keeps `ORDER BY explicit DESC, decided DESC LIMIT 1` | Rejected. Which one wins would stay in four readers. |
| D. A view with `row_number()` over each entry | Rejected. SQLite does not flatten a view that uses a window function, so the cost of each reader would depend on whether SQLite pushes the reader's condition into the view, which reading cannot settle. |
| E. A stored view | Rejected. It is a migration under the [database contract](../../contracts/database.md#ownership-and-initialization) with no benefit; the projection is recreated for every connection. |

The view, in `Projection` (`Shared/Storage/Database/Schema.cs`), after
`current_files` and before `library_entries`:

```sql
CREATE TEMP VIEW entry_identifications AS
SELECT l.entry_id, i.catalog_id, i.video_id, i.explicit
FROM locations l JOIN identifications i ON (i.origin,i.file_index) = (
    SELECT d.origin,d.file_index FROM identifications d
    JOIN source_files s ON s.origin=d.origin AND s.file_index=d.file_index
    WHERE s.entry_id=l.entry_id AND (d.explicit=1 OR d.evidence=s.name)
    ORDER BY d.explicit DESC,d.decided DESC LIMIT 1);
```

Its correlated subquery is today's `library_entries` subquery. The view has
no aggregate, `LIMIT`, `ORDER BY` or window function of its own, so SQLite's
[query flattener](https://www.sqlite.org/optoverview.html#flattening) merges
it into each reader below. No reader places it as the right side of a
`LEFT JOIN` of its own, which is the one flattening limit that applies to a
view that joins two tables. Each reader then looks up `locations` by its
integer key and runs today's subquery once: today's cost and one key lookup.

A temporary view needs no schema revision: `Projection` runs on every open
and changes nothing stored.

| Reader | After |
| --- | --- |
| `library_entries` (`Schema.cs` 205 to 210) | `LEFT JOIN video_information v ON (v.catalog_id,v.video_id) = (SELECT i.catalog_id,i.video_id FROM entry_identifications i WHERE i.entry_id=f.entry_id)`. The join of `identifications` goes; the view selected nothing else from it. Its columns do not change, so `library_rows` (222) keeps its shape. |
| `Store.Undecided` (`Identification.cs` 11 to 15) | Deleted. `Eligible` starts with `NOT EXISTS(SELECT 1 FROM entry_identifications i WHERE i.entry_id=f.entry_id)`. |
| `Store.Fetchable` 161 to 166 | `SELECT i.catalog_id, i.video_id IS NULL, v.imdb_id IS NOT NULL FROM entry_identifications i LEFT JOIN video_information v ON v.catalog_id=i.catalog_id AND v.video_id=i.video_id WHERE i.entry_id=$entry;` |
| `Store.Begin` 284 to 287 | `" AND NOT EXISTS(SELECT 1 FROM entry_identifications i WHERE i.entry_id=f.entry_id AND i.explicit=1 AND i.video_id IS NULL)"` |
| `Store.Begin` 299 to 303 | `FROM entry_identifications i JOIN video_information v … WHERE i.entry_id=$entry` |
| `Store.Begin` 318 to 325 | The same, with `AND i.catalog_id=$catalog`; column 4 is `i.explicit` |
| `Store.Series` 465 to 473 | `JOIN entry_identifications i ON i.entry_id=f.entry_id`, keeping `i.explicit=0` and the other conditions |
| `Database.Reconcile` (`Files.cs` 50 to 63) | No change, as step 1 states |

### `StateFile<T>`: small saved state

| Candidate | Result |
| --- | --- |
| A. A type that holds its path, with `Read`, `Save` and `Delete` | **Chosen.** The path is the file's identity, so a caller holds one object instead of a path and the rule. |
| B. Static `Read<T>(path)` and `Save<T>(path, value)` | Rejected. The plan prefers a type to a helper, and each caller would keep its path and its null test. |
| C. Keep the two copies | Rejected. The approved table of owners places small saved files in `Shared/Storage`, and a third state file would copy one of them, with its temporary file and its failure handling. |

The interface, in the new file `Shared/Storage/StateFile.cs`:

```csharp
/// <summary>
/// A small JSON file in the data folder that keeps state between runs, such
/// as the window layout. A missing, unreadable or damaged file reads as no
/// saved state, so the defaults apply. A save writes a temporary file and
/// renames it over the old one, so a crash never leaves half a file; two
/// saves of one file must not overlap, because they share that temporary
/// file. A failure is written to the debug output only, because the product
/// works without the state.
/// </summary>
internal sealed class StateFile<T>(string path) where T : class
{
    /// <summary>The saved state, or null when the file is missing or unreadable.</summary>
    internal Task<T?> Read();

    /// <summary>Replaces the file with <paramref name="value"/>; returns whether it did.</summary>
    internal Task<bool> Save(T value);

    /// <summary>Deletes the file.</summary>
    internal void Delete();
}
```

The bodies are today's: `File.ReadAllTextAsync` and
`JsonSerializer.Deserialize`, with `FileNotFoundException` and
`DirectoryNotFoundException` read as no state, and `IOException`,
`UnauthorizedAccessException` and `JsonException` logged; `path + ".tmp"`,
`File.WriteAllTextAsync`, `File.Move(…, overwrite: true)`; `File.Delete`.
The debug line names the file. `Save` serializes before its first `await`,
so a caller on the window thread captures its values there, as today.

| Caller | Member | After |
| --- | --- | --- |
| `Shell/MainWindow/Placement.cs` | field `_placementPath` | `private StateFile<Placement>? _placementFile;` |
| | `StartPlacement` 110 | `_placementFile is null` |
| | `RestorePlacement` 141 to 211 | `_placementFile = new(Path.Combine(directory, "window.json")); var placement = await _placementFile.Read();` The `try` and its three `catch` blocks go. |
| | `WritePlacement` 279, 320 to 330 | `_placementFile is null`; `if (await _placementFile.Save(placement)) _placement = placement;` The comment at 275 stays: writes still share one temporary file. |
| | `ForgetPlacement` 338 to 347 | `_placementFile?.Delete();` after the two awaits |
| `Shell/MainWindow/CapturePreferences.cs` | 16, 36, 41, 53 | Saves and restores `_placementFile`; reads with `Read()`, writes the zero-geometry copy with `Save(…)` |
| `Shell/Updates.cs` | field `_cachePath` | `private StateFile<ReleaseCheck>? _cache;` |
| | `CheckRelease` 114 to 130 | `_cache = new(Path.Combine(directory, "updates.json")); _releaseCheck = await _cache.Read(); Raise();` |
| | `SaveCheck` 419 to 433 | `SaveCheck(ReleaseCheck check)` saves through `_cache` when it exists; its two callers (144, 182) pass `_releaseCheck` after they set it |

Placement members that stage 7 changes: the field `_placementPath`,
`StartPlacement`, `RestorePlacement`, `WritePlacement` and
`ForgetPlacement`. It does not change `Rendered`, `ShowWhenReady`, `Reveal`,
`SetCloak`, `OnWindowChanged`, `RememberBounds`, `RestoreView`,
`SavePlacement` or the `Placement` record. Stage 4 changes `Rendered` and
its use in `ShowWhenReady`; the two stages share no member. The `using`
lines for `System.Diagnostics` and `System.Text.Json` in `Placement.cs`, and
for `System.Text.Json` in `CapturePreferences.cs`, go when nothing else uses
them.

### Library filter vocabulary

In `Library/Store.cs`, in place of `Dates` and `Sizes` (742 to 762):

```csharp
// The filter sections whose values are fixed groups of files, each with its
// values in the order the filter lists them. Filter gives each value's condition.
internal static readonly IReadOnlyDictionary<string, string[]> Buckets =
    new Dictionary<string, string[]>
    {
        ["modified"] = ["today", "yesterday", "week", "last_week", "month", "last_month", "year", "older"],
        ["size"] = ["empty", "tiny", "small", "medium", "large", "huge", "gigantic"],
        ["resolution"] = ["uhd", "qhd", "fhd", "hd", "sd", "unknown_resolution"],
        ["subtitles"] = ["with_subtitles", "without_subtitles"],
        ["metadata"] = ["identified", "unidentified"],
    };
```

| Caller | After |
| --- | --- |
| `Store.Search` 238 to 257 | `if (Buckets.TryGetValue(section, out var values))`; the inline lists go |
| `Store.Match` 702 | `else if (Buckets.ContainsKey(section))` |
| `Store.Filter` 780 to 792 | `Buckets[section].Contains(value) ? $"(CASE … END)='{value}'" : "0=1"`. The test must stay: it is what keeps a saved value out of the SQL text, because the value comes from `window.json`. |
| `Browser.FilterText` 835 to 843 | `_ when Store.Buckets.ContainsKey(section) => Text.Get("library", …)` after the specific arms; the two key exceptions (`empty`, `year`) stay there, because they are text keys |

The section and value strings do not change. They are saved in
`window.json` (`Placement.LibraryFilters`), they are text keys and they are
automation identifiers (`LibraryFilter-<section>`).

Labels, beside the stage 3 overloads in `Shared/Strings.cs` 174 to 187:

```csharp
internal string Get(FileKind kind) => Get("library", kind.ToString().ToLowerInvariant());
```

Callers: `Browser.FilterText` 833 and `LibraryRow.Kind` (`Browser.cs` 1440).

In `Library/Browser.cs`, beside `FilterText`:

```csharp
internal string TypeText(string type) => Text.Get("library", type.Length == 0 ? "unidentified" : type);
```

Callers: `Browser.FilterText` 834 and `LibraryRow.Type` (1389), as
`library.TypeText(Entry.Type)`. A video type is a stored word, not an enum,
so it gets no `Strings` overload.

### Subtitles

On the private record `Video` (`Acquisition/Storage.cs` 908):

```csharp
// The columns Read takes, in its order, from Tables.
internal const string Columns =
    $"f.origin,f.file_index,f.path,l.path,f.size,f.complete,c.readable,f.entry_id,f.wanted,f.origin IN ({Finished})";
internal const string Tables =
    "collected_files f JOIN locations l USING(entry_id) JOIN current_files c USING(entry_id)";
internal static Video Read(SqliteDataReader reader);
```

`Read` is today's reader of `Targets` 170 to 183, as `Store.Read` reads an
`Entry` (`Store.cs` 825).

| Caller | After |
| --- | --- |
| `Acquisition.Targets` 159 to 185 | `SELECT {Video.Columns} FROM {Video.Tables} WHERE f.kind=$kind;` and `files.Add(Video.Read(reader))` |
| `Acquisition.Movies` 316 to 345 | `SELECT {Video.Columns},r.save_path,r.stamp,EXISTS(SELECT 1 FROM subtitle_scans a WHERE a.origin=f.origin AND a.file_index=f.file_index AND a.path=l.path COLLATE PATH AND a.stamp=r.stamp) FROM {Video.Tables} JOIN file_reads r USING(origin) WHERE f.kind=$kind AND ($entries IS NULL OR f.entry_id IN (…))`. `Video.Read(reader)`, then save path 10, stamp 11, scanned 12. |

Beside `AssociateOutput` in `Storage.cs`:

```csharp
// Unlike AssociateOutput, records the association without checking that the
// video is still at that location.
private static void Associate(SqliteConnection connection, string path, string language, Video video);
```

It runs today's statement of `Record` 581 to 589. Callers: `Acquisition.Record`
576 to 590 (`Associate(connection, target.Path, target.Language, video)`) and
`Acquisition.Scan` (`Discovery.cs` 540 to 548, with `record.Path`,
`record.Language`). The comment is the one fact the name cannot carry: why
two methods record associations.

Beside `Unblock` and `Resume` in `Storage.cs`:

```csharp
private static void RetryNow(SqliteConnection connection);
```

It runs `UPDATE subtitle_jobs SET retry_at=0 WHERE outcome=$pending;`.
Callers: `Acquisition.ChangeRoute` 180 to 184 and
`Acquisition.ResetProtection` 228 to 234 (`Acquisition.cs`).

### Settled as

Each item is a difference between copies, or a choice the plan does not
make.

1. **The IMDb data's connections wait at most five seconds for a lock**, as
   `library.db`'s do; before, the default 30. The option bounds only lock
   retries, not a statement's run time, and no other connection writes the
   installed IMDb file, so no person meets it. One set of options keeps one
   rule.
2. **An interrupted IMDb import ends as a cancellation** inside
   `Dataset.Build`, where it was a `SqliteException` that became
   `DownloadException(Failed)`. `DataDownload.Run` reports a stopped build
   as cancelled or interrupted whatever its error (`DataDownload.cs` 77 to
   94), so the person sees the same result.
3. **When an interrupted `library.db` transaction cannot roll back**, the
   stored reopen failure is the rollback's own error (`Transact` 289), where
   it was the interruption's. The connection is unusable in both cases.
4. **A batch skips a file whose deciding identification is the person's
   Clear**, as Fetch metadata's dialog already counts it
   (`Store.Fetchable`). Before, any Clear among the location's files made
   the batch skip it.
5. **A lookup reuses a provider's own choice only when that provider's
   identification decides the entry** (`Store.Begin` 318), as Refresh
   metadata uses "the provider saved with that file's metadata"
   ([CONTEXT.md](../../../CONTEXT.md)).
6. **The series hint reads each sibling location's deciding identification
   when it is automatic**; before, each sibling file's own automatic one.
   The [providers contract](../../contracts/library/providers.md#identification-order)
   names "Library's current, accepted sibling decisions".

Items 4 to 6 differ from today only when the files of one location hold
different identifications. `Database.Reconcile` gives confirmed files the
same explicit choice, so this needs a cached file that is not confirmed, or
a renamed contributor.

## Gate step 4: every consumer of every changed name

Searched: all of `app/src`, which includes the 25 capture journeys and the
`#if CAPTURE` blocks, all XAML, `app/tests/*.ps1`, `docs`, and the owner's
timing scripts in `artifacts/imdb-performance`.

| Name | Consumers |
| --- | --- |
| `Database.Connect`, `Execute`, `IsDamaged` (new) | `Database.cs`, `Library/Imdb/Dataset.cs` |
| `Dataset.Connect` (private, deleted) | `Dataset.Read` 400, `Dataset.Import` 537. `ImdbChecks.ps1` 97 and the timing scripts open their own connections. |
| `entry_identifications` (new) | The readers in the table above |
| `library_entries` (definition changes, columns do not) | `Schema.cs` 222; `Store.ReadCoverage` 39; `Store.Search` 156; `Store.Detail` 430. `docs/contracts/database.md` 109. |
| `Store.Undecided` (private, deleted) | `Store.Eligible` only |
| `StateFile<T>` (new) | `Placement.cs`, `CapturePreferences.cs`, `Updates.cs` |
| `MainWindow._placementPath` | `Placement.cs` 110, 143, 146, 279, 320, 324, 338, 342; `CapturePreferences.cs` 16, 36, 41, 53. No test names it. |
| `Updates._cachePath` | `Updates.cs` only. `UpdateChecks.ps1` names `_releaseCheck` and `_check`, which stay, and the files `updates.json` and `updates.json.tmp` (93, 137, 138), whose names stay. |
| `Store.Dates`, `Store.Sizes` (private, deleted); `Store.Buckets` (new) | `Store.cs`; `Browser.FilterText` |
| `Strings.Get(FileKind)`, `Browser.TypeText` (new) | `Browser.cs` 833, 834, 1389, 1440. `LibraryRow.Kind` and `Type` keep their names, which `LibraryTable.xaml` 66, 188 and `LibraryTable.xaml.cs` 201, 236 bind. |
| `Video.Columns`, `Tables`, `Read`; `Associate`; `RetryNow` (new, private) | `Storage.cs`, `Discovery.cs`, `Acquisition.cs`. `SourceChecks.ps1` calls `Targets`, `Record`, `ResetProtection` and `Reconcile` by name (523, 555, 574, 594, 596, 843, 1218, 1383, 1387, 1391) and sets `Video.Complete` by reflection (852, 857); all keep their names. |

No text key changes, so no catalogue changes. No XAML changes. No test
script changes.

## Gate step 5: files, contracts, checks, cost

### Files the implementer may edit

Product code:

- `Shared/Storage/Database.cs`, `Shared/Storage/Database/Schema.cs`,
  `Shared/Storage/StateFile.cs` (new), `Shared/Strings.cs`
- `Library/Imdb/Dataset.cs`, `Library/Store.cs`,
  `Library/Store/Identification.cs`, `Library/Browser.cs`
- `Subtitles/Acquisition.cs`, `Subtitles/Acquisition/Storage.cs`,
  `Subtitles/Acquisition/Discovery.cs`
- `Shell/MainWindow/Placement.cs` (the members named above),
  `Shell/Updates.cs`
- Capture journey: `Shell/MainWindow/CapturePreferences.cs`

Documents: `docs/contracts/database.md`, `docs/contracts/imdb-datasets.md`,
`docs/agents/naming.md`.

A file that is not in this list is edited only when the compiler names it,
and the batch report says which.

### Contract changes, as proposed text

**`docs/contracts/database.md`**, the paragraph that ends "bounds retries
for external locks to five seconds." gets one more sentence:

> `Database.Connect` opens every SQLite connection of the app, `library.db`
> and the IMDb data alike, with these options, and `Database.Execute` lets a
> cancellation interrupt a running statement and ends that statement as a
> cancellation.

In "Reconstructed state", a row before `library_entries`:

> | `entry_identifications` | The identification that decides each Library entry: the latest explicit choice of its files, including Clear, else the latest provider match whose file still has the name it was matched under. Rows, details, Fetch metadata and video lookups read it, so they agree. Source reconciliation shares explicit choices among confirmed files only and does not read it. |

**`docs/contracts/imdb-datasets.md`**: in "Provider", "This connection and
the import's connection use `Pooling=false` for the same reason" becomes
"This connection and the import's open through `Database.Connect`, which
sets `Pooling=false` for every connection of the app, for the same reason".
In the cancellation text (199), "One connection-scoped SQLite progress
handler" becomes "One SQLite progress handler, which `Database.Execute`
registers for the import's statements,".

**`docs/agents/naming.md`**, "App folders": the `Storage/` folder groups the
"database and small saved state files", where it says "database".

[CONTEXT.md](../../../CONTEXT.md) needs no change: the stage adds no product
term. `StateFile`, `Buckets` and `entry_identifications` are code names.

### What changes for a person

In normal use, nothing. The differences are "Settled as" items 4 to 6, which
need one location whose files hold different identifications, and item 1,
which no person meets.

### Done when

- A search of `app/src` for `sqlite3_progress_handler`, `SQLITE_INTERRUPT`,
  `SqliteConnectionStringBuilder` and `SQLITE_NOTADB` finds
  `Shared/Storage/Database.cs` only.
- A search for `explicit DESC` finds `Schema.cs` (the view) and `Files.cs`
  (`Reconcile`) only.
- A search of `Placement.cs` and `Updates.cs` for `JsonSerializer.Serialize`
  and `".tmp"` finds nothing.
- A search for `"modified" or "size"` and for `Kind.ToString().ToLowerInvariant()`
  in `Library` finds nothing.
- `retry_at=0 WHERE outcome=$pending` appears once, in `Storage.cs`.
- `JustFinished = reader` appears once, in `Video.Read`.
- The three documents hold the text above.

### Checks on batch B's build

The stage joins batch B (stages 4, 5, 7): one app build with `-Capture`,
which compiles the changed capture journey. The checks whose subject
changes, each with its open question:

- **`app/tests/SourceChecks.ps1`.** Do rows, details, the sweep, the
  explicit and late-reply guards, the rename cases and the subtitle targets,
  records, discovery and reset still behave as before? Its Library cases
  (`Search`, `Begin`, `Complete`, `Pending`, `Detail`, `EraseMetadata`) read
  the view, and its subtitle cases run the reader, the upsert and
  `ResetProtection`. A failure means a reader of the view or a subtitle
  query changed behavior.
- **`app/tests/ImdbChecks.ps1`.** Does an import through `Database.Connect`
  and `Database.Execute` still build the same file and still stop when
  cancelled during indexing (129 to 135)?
- **`app/tests/UpdateChecks.ps1`.** Does `StateFile` still read a fresh
  cache, so no request leaves (93 to 99), and save after each check?

No capture mode runs. The `PeerPreferences` journey changes and compiles in
the build. Its run would test `StateFile`'s read and save, which
`UpdateChecks.ps1` runs, and Placement's calls to it, which reading settles.
No layout or text changes, so no capture review.

### Cost

- **Agent work:** about 3 hours for the implementing session: 13 product
  files and one capture journey, of which `Database.cs`, `Dataset.cs`,
  `Identification.cs` and the new `StateFile.cs` change most; three
  documents. About 1 hour for the independent review.
- **Builds:** none of its own; batch B's one app build with `-Capture`.
- **Checks:** the three scripts above, on that build.

## What reading could not settle

- **The time of the Library snapshot rebuild.** No check measures it, before
  or after: `LibraryTiming.ps1` discards the first search, which is the one
  that rebuilds `library_rows`. The view's plan follows from SQLite's
  documented flattening rules, which give each reader today's subquery and
  one more key lookup for each entry.
- **Fetch metadata's groups and the series hint** have no existing check.
  Their queries change only by reading the view; their edge-case
  differences are "Settled as" items 4 to 6.

## Implementation notes

Implemented on 12 November 2026 on top of commit 5f8d2f6, by reading.
Nothing was built or run. The code still matched this record. Where the
result differs from the record:

- **`Database.IsDamaged` has no summary.** Its one-line body says what it
  tests, so the comment policy leaves nothing for a summary to add.
- **`CapturePreferences` fails when its rewrite of the layout fails.** Before,
  `File.WriteAllTextAsync` threw out of the journey. `StateFile.Save`
  returns false instead, so the journey checks that result and throws. If it
  went on, the next restore would read the old layout and move the
  diagnostic window onto the screen. It reads `_placementFile` into a local
  after `RestorePlacement`, so no second `!` is needed.
- **`Dataset.Import` passes its body to `Database.Execute` as a lambda whose
  parameter shadows the `using` variable**, as C# 8 and later allow. It is
  the same connection. The body moved one level in, so its comments were
  rewrapped; their text did not change.
- **`Store.Filter` has one new comment** on the resolution test: the value is
  written into the SQL, so only a listed one may reach it. The record
  required the test to stay; the comment keeps a later edit from removing it.
- **`Browser.TypeText` has no summary,** for the reason given for
  `IsDamaged`.

Files changed: `app/src/Shared/Storage/Database.cs`,
`Database/Schema.cs`, `StateFile.cs` (new); `app/src/Shared/Strings.cs`;
`app/src/Library/Imdb/Dataset.cs`, `Store.cs`, `Store/Identification.cs`,
`Browser.cs`; `app/src/Subtitles/Acquisition.cs`, `Acquisition/Storage.cs`,
`Acquisition/Discovery.cs`; `app/src/Shell/MainWindow/Placement.cs`,
`Shell/Updates.cs`; the capture journey
`app/src/Shell/MainWindow/CapturePreferences.cs`;
`docs/contracts/database.md`, `docs/contracts/imdb-datasets.md`,
`docs/agents/naming.md`. No file outside the record was needed, and no item
was blocked.

The "Done when" searches hold: `sqlite3_progress_handler`,
`SQLITE_INTERRUPT`, `SqliteConnectionStringBuilder` and `SQLITE_NOTADB`
appear only in `Database.cs`; `explicit DESC` only in `Schema.cs` and
`Files.cs`; `retry_at=0 WHERE outcome=$pending` and `JustFinished = reader`
once each in `Storage.cs`; nothing remains of `_placementPath`,
`_cachePath`, `Undecided`, `Dates`, `Sizes`, `"modified" or "size"` or
`Kind.ToString().ToLowerInvariant()` in `Library`. `Placement.cs` and
`Updates.cs` no longer write JSON or a temporary file themselves.
[CONTEXT.md](../../../CONTEXT.md) did not change: `StateFile`, `Buckets` and
`entry_identifications` are code names, and the product terms they rest on,
Library entry and Identification, are already there.

## Review fixes

The review of stage 7 (scratchpad record `review-stage07.md`) found one
medium item, which the lead fixed: `Updates.CheckRelease` replaced the
check held in memory with nothing when `updates.json` was missing or
unreadable, so a manual check that could not send a request no longer held
back the day's automatic check. It now keeps the check in memory when the
file gives none (`_cache.Read() ?? _releaseCheck`), as before the stage.
