# Stage 13 gate record: engine paths, text, errors and Windows resources

The gate of
[stage 13](../22-one-owner-per-rule.md#13-engine-paths-text-errors-and-windows-resources).
Line numbers are those of the working tree when this gate started: commit
c2548f2 with the uncommitted stage 15 work. Since 7e0ee84, `Move.cpp` and
`Storage.cpp` moved by a few lines; the plan's other engine line numbers still
hold. This record replaces the stage's text for the session that implements
it.

## Owners and interfaces

Each member below is the whole interface its callers use.

**`Apartment`** (`engine/inc/Owned.h`), beside the other owners of Windows
resources:

| Member | Rule |
| --- | --- |
| `Apartment()`, `~Apartment()` | A single-threaded COM apartment on this thread while the object lives; it ends only an apartment it began. |
| `HRESULT Result() const` | `CoInitializeEx`'s result. Only `Recycle` reads it: it fails its deletion when the apartment did not begin. |

**Torrent rules** (`engine/inc/Torrent.h`, implemented in `Torrent.cpp`):

| Member | Rule |
| --- | --- |
| `bool Checks(lt::torrent_status::state_t state)` | libtorrent checks the torrent's files in this state: `checking_files` or `checking_resume_data`. |
| `bool IsChoice(lt::download_priority_t)` | The priorities a person can choose: skip, low, normal and high. Moves from `Engine::State`. |
| `lt::download_priority_t NearestChoice(std::uint8_t saved)` | The choice nearest to a saved priority. Both read one list of the four choices. |
| `Pieces` (existing) | The pieces a file covers. `Torrent::PrioritizePieces` uses it. |

**Engine-wide helpers** (`engine/inc/Engine.h`, implemented in `Engine.cpp`),
beside `Utf8`, `Executable` and `ProcessImage`:

| Member | Rule |
| --- | --- |
| `std::weak_ordering CompareNames(std::wstring_view, std::wstring_view)` | Names compare as Windows compares them: ordinally, without case, by the file system's case table (`CompareStringOrdinal`). |
| `bool SameName(std::wstring_view, std::wstring_view)` | Inline: `CompareNames(...) == 0`. |
| `std::wstring NameKey(std::wstring_view name)` | The name in upper case by the same table, so names that compare equal have one key in a map or set. |
| `bool Equivalent(path, path)` | Renamed from `tt::SameFolder`: whether the disk says two paths name one folder. |
| `std::optional<std::filesystem::path> FinalPath(void* handle, unsigned long flags)` | The path Windows gives an open handle, in the form `flags` asks for; nothing when Windows does not give it. |
| `std::optional<std::filesystem::path> Win32Path(std::wstring path)` | Moves from `Locations.cpp`: a path with the long-path or NT prefix in its Win32 form; a volume is its drive letter. |
| `std::optional<std::wstring> Reason(unsigned long error, wchar_t const* file = nullptr)` | Windows' own words for a Windows error, in the person's language. `file` fills a message that names one file. |
| `std::string Describe(path, unsigned long error)` | A failed file's detail: its path and `Reason`, or the error's number when Windows has no words. Moves from `Payload.cpp`. |
| `template <typename Value> std::optional<Value> Whole(Json const&)` | A JSON value that is a whole number from 0 to the largest `Value`. |

A file-local `pathLimit` in `Engine.cpp` names the 32,768 characters of the
longest Windows path with its terminating NUL.

**`Engine::State`** (`engine/inc/Engine/State.h`), beside `FullPath`,
`PathBefore` and `SamePath`:

| Member | Rule |
| --- | --- |
| `static bool Present(path)` | Whether anything has the path's name, also a link whose target is gone: `symlink_status`, not following links. |
| `static void SortUnique(std::vector<path>&)` | Sorts paths by `PathBefore` and keeps one of each path that `SamePath` finds equal. |

**`Strings`** (`engine/src/Strings.cpp`) walks the language's parent tags once,
in a file-local `Shipped(tag)`: the shipped catalogues of the tag and its
parents, nearest first.

## Callers and the member each calls

| Caller | Today | Calls |
| --- | --- | --- |
| `Main.cpp` 248 | `struct Apartment` | `Apartment` |
| `Registration.cpp` 165 `Default`, 644 `RepairShortcuts` | `CoInitializeEx` and `CoUninitialize` | `Apartment` |
| `Removal.cpp` 470 `Recycle` | Same, and fails on a failed start | `Apartment`, `Result` |
| `Desktop/Window.cpp` 83 `SetRelaunch` | Same | `Apartment` |
| `Alerts.cpp` 270, `Checkpoints.cpp` 126, `Turns.cpp` 174 | The two states spelled out | `Checks` |
| `Main.cpp` 302, `Pipe.cpp` 488 | `tt::SameFolder` | `Equivalent` |
| The 15 callers of `Engine::State::SameFolder` | Text comparison | Unchanged name |
| `Main.cpp` 70, `Drives.cpp` 20 | Own `FinalPath` | `FinalPath` |
| `Move.cpp` 372, `Tasks.cpp` 860 (lambdas), `Files.cpp` 370, 372, 381 | `symlink_status(...).type()` against `not_found` | `Present` |
| `Files.cpp` 1058, 1075, 1085, 1215, 1217; `Move.cpp` 161; `Removal.cpp` 147 | `std::sort` then `std::unique` | `SortUnique` |
| `Payload.cpp` 99 `Plain`, `Locations.cpp` 243 `NoticeRoot`, `Drives.cpp` 131 | Strip or test `\\?\` and `\\?\UNC\` | `Win32Path` |
| `Locations.cpp` 97, 158, 328 | File-local `Win32Path` | `Win32Path` |
| `Registration.cpp` 53 `Same` (14 calls) | `CompareStringOrdinal` | `SameName` |
| `Files.cpp` 195 `PathBefore`, 200 `SamePath` | `CompareStringOrdinal` | `CompareNames` |
| `Main.cpp` 109, 110 | `_wcsicmp`, `_wcsnicmp` | `SameName` |
| `Payload.cpp` 859 | `_wcsicmp` | `SameName` |
| `Desktop/Instance.cpp` 55, 67 | `_wcsicmp` | `SameName` |
| `Watching.cpp` 41, `Torrent.cpp` 219 | `CompareStringOrdinal` | `SameName` |
| `Drives.cpp` 105, 113, 135 | `towlower` keys | `NameKey` |
| `Turns.cpp` 391 `RootKey` | `LCMapStringEx` | `NameKey` |
| `Store.cpp` 31 `WriteFailure` | `FormatMessageW` | `Describe` |
| `Payload.cpp` 59, 64 `Describe` | `error_code::message()` | `Describe` |
| `Desktop/Window.cpp` 215 | `FormatMessageW` with an insert | `Reason` |
| `Engine.h` 251 `ReadSaved` | Integer range check | `Whole` |
| `Session.cpp` 477, `Commands.cpp` 719, `Edits.cpp` 43, 77, 107, `Torrent.cpp` 51 `IsPriority`, `Settings.cpp` 85 `Limit::Nearest`, `Policy.cpp` 125, 141 | Integer range check | `Whole`, then the caller's own bound |
| `Additions.cpp` 46, `Edits.cpp` 44, `Torrent.cpp` 446 | `Engine::State::IsChoice`, file-local `NearestChoice` | `IsChoice`, `NearestChoice` |
| `Torrent.cpp` 1612 `PrioritizePieces` | Its own piece arithmetic | `Pieces` |
| `Engine.cpp` 143, 155, 318 | `32'768` | `pathLimit` |
| `Strings.cpp` 38 `Match`, 79 the constructor | One catalogue laid over English | `Shipped`: every shipped catalogue on the way |

## Findings to re-read

Kept, each with the one sentence of its rule:

- **The long-path prefix** (`Payload.cpp` 49, 99; `Locations.cpp` 52, 243;
  `Drives.cpp` 131). Rule: a path with the long-path or NT prefix names the
  same file as its Win32 form, `\\?\UNC\nas\a` as `\\nas\a` and `\\?\C:\a` as
  `C:\a`. `Win32Path` owns removing the prefix. `Extended` in `Payload.cpp`
  stays the one place that adds it, because only the payload work needs paths
  longer than `MAX_PATH`.
- **Name comparison** (`Registration.cpp` 53; `Drives.cpp` 105;
  `Turns.cpp` 391). Rule: two names that Windows takes for one name are one
  name to the engine. The search for copies also found `Main.cpp` 109,
  `Payload.cpp` 859, `Instance.cpp` 55 and 67, `Watching.cpp` 41,
  `Torrent.cpp` 219 and `Files.cpp` 197; all move to the owner.
- **A Windows error as text** (`Store.cpp` 31; `Payload.cpp` 59;
  `Desktop/Window.cpp` 212). Rule: a Windows error shows as Windows' own words
  for it, in the person's language.
- **A whole number from JSON** (`Engine.h` 251; `Session.cpp` 477;
  `Commands.cpp` 719; `Edits.cpp` 43, 77, 107). Rule: a count, index, limit,
  tier or priority read from JSON is an integer from 0 to the largest value
  its type holds. The search also found `Torrent.cpp` 51, `Settings.cpp` 85
  and `Policy.cpp` 125, 141, which apply the same check before a bound of
  their own.
- **The priority choices** (`Additions.cpp` 55; `Torrent.cpp` 58). Rule: a
  person chooses skip, low, normal or high for a file. `NearestChoice`'s
  thresholds encode the same four values; nearest by distance gives the same
  result for every saved value from 0 to 255, with no ties.
- **A file's pieces** (`Torrent.cpp` 288, 1612). Rule: a file covers the
  pieces from the one that holds its first byte to the one that holds its last.
  For a file that is not empty both give the same first and last piece.
- **The path-length literal** (`Engine.cpp` 143, 155, 318; `Main.cpp` 72).
  Rule: a Windows path is at most 32,767 characters. `Main.cpp` 72 goes with
  its `FinalPath` copy, because the owner grows its buffer to what Windows asks.
- **The engine's language fallback** (`Strings.cpp` 34). Rule, from
  [localisation](../../contracts/localisation.md#language-rules-and-fallback):
  a missing message comes from a shipped parent language, then English. The
  engine laid only the nearest shipped catalogue over English, so a message
  that a regional catalogue lacks would skip its parent language. No shipped
  language has a regional catalogue today, so the correction changes no text
  the person sees now.

Dropped, each with its reason:

- **The nearest existing folder** (`Drives.cpp` 119; `Storage.cpp` 130). Two
  rules. `Usable` judges whether a location answers: it stops at access
  denied, which answers, and at any error other than a missing folder, which
  does not answer, and it stops at the share root. `Drives::Of` only needs a
  folder on the same drive, so it climbs on every error. One function would
  need a flag for each caller.
- **The volume of a folder** (`Drives.cpp` 95; `Payload.cpp` 97). Two rules.
  `Drives::Of` finds the physical disks or the server that disk work reaches,
  through every link, to schedule jobs. `Inspect` finds the volume that holds
  a folder and what it supports, to choose a rename or a copy and the copy's
  flags.
- **Free space** (`Payload.cpp` 177; `Storage.cpp` 149). Each is one
  `GetDiskFreeSpaceExW` call compared with a byte count, the platform's own
  rule written where it is used, as the plan keeps the port range. A shared
  function would only forward the call, which
  [naming](../../agents/naming.md#files) rules out; and the callers need
  different results: the move needs the volume and Windows' reason, the minute
  check only yes or no.
- **`ChecksFiles`** (`Torrent.cpp` 706; `Session.cpp` 972). Two rules.
  `SampleChecks` measures what libtorrent read, from the status alone, until
  the status shows the pause. `ChecksFiles` also leaves out a check that the
  engine holds for path work or a drive before the status shows it, because
  the drive pass and the stall clock follow the engine's decision. Routing
  the sample through `ChecksFiles` would drop the bytes of a check's last
  second before its pause from the check rate.

## Decisions

The plan leaves these open; each is decided by the engine contract, the Owner
rulings and usability.

1. **Settled as: names compare as Windows compares them.** `CompareNames` is
   `CompareStringOrdinal` without case, and `NameKey` is `LCMapStringEx` upper
   case with the file system's casing, the table `CompareStringOrdinal` uses.
   `_wcsicmp` folded only A to Z in the C locale, so a path, an executable or a
   volume name with another letter in another case now compares equal. The
   drive keys of `Drives::Of` change from lower to upper case; they are only
   compared, never shown or logged. The engine contract states the rule.
2. **Names that are not Windows names stay.** The magnet scheme
   (`Engine.h` 62) is an ASCII URI scheme, the network adapter
   (`Policy.cpp` 47, 93) a GUID that libtorrent matches exactly, and the
   file search (`Tree.cpp` 139) follows the search Owner ruling, which ignores
   accents too.
3. **`Win32Path` replaces `Plain` and `NoticeRoot`'s prefix removal.** For
   every path the engine meets they give the same result. Two differ only for a
   path written as `\\?\Volume{...}\`: `Win32Path` names its drive letter.
   `Inspect` then queries the same volume through its letter, with the same
   result. A save folder typed that way gets its drive's location notice, where
   `NoticeRoot` made it a relative path with no notice. No folder picker gives
   such a path; the change is recorded because it is one.
4. **`Drives::Of` reads a UNC path's server in one place.** The folder's own
   UNC path and the Win32 form of its final path go through one lambda, so the
   `\\?\UNC\` literal leaves `Drives.cpp`.
5. **`FinalPath` grows its buffer.** The `Drives.cpp` copy asks Windows for the
   size it needs; `Main.cpp`'s used a fixed 32,768. Both give the same path for
   every path Windows can return.
6. **Windows' words come as UTF-16 from `FormatMessageW`.** `Payload`'s
   `Describe` used `std::error_code::message()`, which MSVC writes in the ANSI
   code page, as the comment at `Main.cpp` 154 records; the move and deletion
   details are UTF-8 everywhere else. English Windows gives the same words;
   another language now gives correct UTF-8. The error code that `create_directories`
   and `remove` report is a Windows error, because MSVC's `std::filesystem`
   builds every such `error_code` with `system_category` (`_Make_ec`), so
   `Describe` reads it as one. `Window.cpp`'s trailing space after Windows'
   words is trimmed, as `Store.cpp` already did.
7. **`Whole` returns the number.** A caller with its own bound, such as the
   seven days of a schedule period, checks it on the returned value. The
   settings' `Range`, `Limit::Accepts` and `Choice` stay: they accept a range
   from a minimum that each setting names, a different rule.
8. **`Present` and `SortUnique` join `Engine::State`'s path helpers.** Every
   caller is a `State` member, and `PathBefore` and `SamePath`, which
   `SortUnique` uses, live there.
9. **`tt::SameFolder` becomes `Equivalent`**, the standard library's word for
   two paths that resolve to one file. `Engine::State::SameFolder` keeps its
   name, because 15 callers use it for the text rule.
10. **The engine contract states the name rule** in its shared state and work
    section, because paths, volume names and registry classes all follow it.
    The language fallback needs no engine text: localisation owns it.

## Seen and left

- `Settings.cpp` 55, 77, 100: the settings' own range types, decision 7.
- `Pipe.cpp` 355 and `Commands.cpp` 380: a request identity and a context are
  any integer, not a whole number from 0.
- `Torrent.cpp` 475: a saved tracker tier above 255 loads as 255, the nearest
  value, a different rule from refusing it.
- libtorrent's and Boost's error texts (`alert.error.message()` and others)
  are libtorrent's words for its own errors, which the plan does not list.

## Consumers of each changed name

Searched in `engine/`, `engine/tests`, `app/src` with the capture journeys,
`app/tests` and `installer/`. The app reaches no engine C++ name, and no
protocol word, saved key or message key changes.

- `tt::SameFolder` → `Equivalent`: `Main.cpp`, `Pipe.cpp`.
- `FinalPath`, `Win32Path`, `Reason`, `Describe`, `CompareNames`,
  `SameName`, `NameKey`, `Whole`, `Apartment`, `Present`, `SortUnique`,
  `Checks`, `IsChoice`, `NearestChoice`: the callers in the table above.
- `Engine::State::IsChoice`: `Additions.cpp`, `Edits.cpp`.
- Removed: `Main.cpp`'s `FinalPath` and `Apartment`, `Drives.cpp`'s
  `FinalPath`, `Locations.cpp`'s `Win32Path`, `Payload.cpp`'s `Plain` and
  `Describe`, `Store.cpp`'s `WriteFailure`, `Registration.cpp`'s `Same`,
  `Torrent.cpp`'s `IsPriority`, `Strings.cpp`'s `Match`.
- `engine/tests/Transitions.cpp` compiles `Control.cpp` and includes
  `Torrent.h`; it uses none of these names.

## Files the change may edit

`engine/inc/Owned.h`, `Engine.h`, `Torrent.h`, `Engine/State.h`;
`engine/src/Engine.cpp`, `Main.cpp`, `Registration.cpp`, `Drives.cpp`,
`Payload.cpp`, `Store.cpp`, `Strings.cpp`, `Torrent.cpp`, `Pipe.cpp`;
`engine/src/Engine/Removal.cpp`, `Alerts.cpp`, `Checkpoints.cpp`, `Turns.cpp`,
`Files.cpp`, `Move.cpp`, `Tasks.cpp`, `Locations.cpp`, `Session.cpp`,
`Commands.cpp`, `Edits.cpp`, `Additions.cpp`, `Policy.cpp`, `Settings.cpp`,
`Watching.cpp`; `engine/src/Desktop/Window.cpp`, `Instance.cpp`;
`docs/contracts/engine.md`.

## Cost

No build in this session. Batch G: one engine build with stages 14 and 15, then
the engine checks whose subject changed. About three hours of agent work.

## Review fixes

The review of the stage 13 diff found two defects and recorded two accepted
changes. The stage 14 session applied the fixes, because it held the files.

1. **Fixed: a registry name ends at its first NUL.** `Registration.cpp`
   `Read` dropped one trailing NUL. The old `Same` compared up to the first
   NUL; `SameName` compares the full length, so a class name that another
   program stored with more than one NUL no longer matched, and `Remove`
   (of a broken class) and `References` went wrong. `Read` now cuts the text
   at its first NUL (`std::wcslen`), because registry data from other programs
   enters there.
2. **Fixed: the contract's name rule is narrowed.** The engine contract said a
   folder written in another case is never a second folder, also with letters
   outside A to Z. `RootOf` keys roots by libtorrent's `storage_root_key`,
   which folds case only from A to Z. The sentence now covers the comparisons
   and keys the engine makes through `CompareNames` and `NameKey`, and states
   that a storage root's key is libtorrent's own. The storage root key does not
   change.
3. **Accepted: a typed save folder with a prefix that `Win32Path` cannot
   convert.** `NoticeRoot` keeps a `\\?\` prefix that `Win32Path` cannot
   convert, such as a volume without a drive letter or `\\?\GLOBALROOT\...`,
   so such a save folder gets a probed notice root, where before it had none.
   A typed `\??\C:\x` gets C:'s notice. Only a save folder the person types
   reaches these forms.
4. **Accepted: Payload's failure text is in the person's Windows language.**
   MSVC's old text preferred English. An error with no system message now
   shows its number instead of "unknown error". Localisation asks for the
   person's language, and a number is a partial answer where "unknown error"
   was none.
