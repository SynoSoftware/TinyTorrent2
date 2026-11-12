# Using gozler to find files for Library

**Reviewed on 11 November 2026. Steps 2 and 3 are built (12 November 2026);
the [database contract](../contracts/database.md) now owns their rules.** The
other steps can start in the order at the end. No decision is open. The
[Library](../contracts/library.md),
[database](../contracts/database.md) and [UI](../contracts/ui.md) contracts own
current behavior; update them alongside implementation.

The engine's part of the earlier draft, torrent files that were deleted or
moved outside TinyTorrent, is [plan 21](21-missing-torrent-files.md). It is not
ready, and this plan does not depend on it.

## North star

**Library shows the video, music and subtitle files that are on the person's
drives, and keeps their locations current. It learns them from gozler, a
separate program, and never by listing folders. Library opens as fast with
them as without them. The feature stays off until the person switches it on.**

The owner's requirement of 11 November 2026, in the owner's words: the feature
"must be flawless from the user point of view", Library is "not to be slow to
start. ever", "we must not recurse folders", and "gozler is ours. if it doesn't
have something we can add it".

Three rules follow. Every part of this plan obeys them.

1. **Saved catalogue first.** Opening Library shows what SQLite holds. Nothing
   between opening Library and its first rows waits for gozler.
2. **No folder is listed.** The only file-system call that discovery makes is
   a check that one known path exists, for a path that gozler stopped
   reporting. The subtitle files beside a disk movie come from the saved
   catalogue, not from a listing of its folder.
3. **Work follows the change.** A refresh that finds nothing changed writes
   nothing, and one that finds five changed files writes five.

gozler is the owner's own project (MIT licence, source in `3rdParty/goz`,
version 0.5.9). It already indexes the fixed drives and answers a query in
milliseconds. TinyTorrent does not index files itself.

## Decisions of 11 November 2026

1. **TinyTorrent uses gozler** for Library's media discovery, and later for
   finding torrent files that were moved outside TinyTorrent
   ([plan 21](21-missing-torrent-files.md)). Reason: the owner does not want
   to build a file index a second time.
2. **TinyTorrent's installer does not bundle gozler, and does not offer it.**
   The only way TinyTorrent installs gozler is the in-app prompt in decision 6.
   Reason: gozler is a separate program with its own installer, updates and
   uninstaller. The feature starts off, so an installer offer helps nobody who
   never switches it on, and a second entry point would need a second
   implementation of the download and its check.
3. **TinyTorrent never overwrites or replaces a gozler that is already
   installed.** TinyTorrent uses the person's gozler as it is. Reason: it is
   the person's installation.
4. **Only one gozler service runs on the machine:** the person's own, or the
   one TinyTorrent installed. Reason: gozler keeps its index in memory (381 MB
   private and 200 MB working set on the owner's PC, with 2.37 million
   entries), which works against TinyTorrent's promise to be light. For the same reason the feature is
   opt-in.
5. **TinyTorrent talks to gozler only by running `gozler.exe --json`.** It does
   not use gozler's named pipe. Reason: the pipe's protocol version must match
   exactly, so a pipe client breaks with an older or newer gozler, and the pipe
   client exists only in Rust.
6. **When the person switches the feature on and gozler is not installed,
   TinyTorrent asks whether to install it.** Yes downloads gozler's installer,
   checks it and runs it. Reason: the person should not have to leave
   TinyTorrent to install what the feature needs. See
   [Installing gozler](#installing-gozler).
7. **gozler gains what TinyTorrent needs from it.** A fact that only gozler
   has is added to gozler's answer, not guessed in TinyTorrent. Reason: gozler
   is the owner's project. [What we add to gozler](#what-we-add-to-gozler)
   holds the one addition this plan needs.
8. **A file that the person's own check finds gone leaves Library, also when a
   torrent still lists it.** Chosen on 11 November 2026 from the review's two
   answers. Reason: with discovery on, every other deleted file leaves
   Library, so a row that stays and only fails to open reads as a defect. This
   changes the Library contract's ruling that torrents alone establish
   membership. See [Absent locations](#absent-locations).
9. **The person can hide a folder from Library.** Chosen on 11 November 2026.
   Reason: games and programs keep media outside the folders that the query
   leaves out. A game on `D:\Games` puts thousands of sounds into Music, and
   on the owner's PC 1,501 of the 3,022 audio files are test files of code
   libraries. See [Hidden folders](#hidden-folders).

## What gozler gives us

Checked against gozler's source on 11 November 2026. Paths are under
`3rdParty/goz`. These are the only gozler facts this plan relies on.

| Fact | Consequence in this plan | Source |
| --- | --- | --- |
| `--json` output is for scripts. A consumer ignores the fields it does not need, and later releases add fields. | Read the fields we need; ignore the rest. | `docs/JSON.md:92` |
| The Windows service is named `gozler`. Its program is `gozlerd.exe`, which runs as LocalSystem and starts automatically. Setup puts `gozler.exe` in the same folder. | Find `gozler.exe` through the service. A stopped service is the person's state, not ours. | `crates/gozler-core/src/proto/types.rs:36`, `crates/gozler-daemon/src/service.rs:63-73`, `installer/Setup.iss:81` |
| It indexes fixed NTFS and ReFS volumes only. | Files on removable drives, network shares and FAT or exFAT volumes are never found. Settings says "fixed drives". | `docs/DESIGN.md:19` |
| A result has `path`, `is_dir`, `size`, `mtime_ft`, and `attributes` when asked with `-attributes`. `size` and the date can be missing, which means unknown, not zero. It has no file ID. | An unknown size or date keeps the saved value. The file ID is [added](#what-we-add-to-gozler). | `docs/JSON.md:74-90`, `crates/gozler-core/src/proto/types.rs:317` |
| It has no change notification. | We ask again [when we need to know](#when). | `docs/DESIGN.md:583` |
| Each hard link is a separate entry. | Each location is its own Library entry, as today. | `docs/DESIGN.md:178` |
| A user sees only the entries in folders that user may list, unless the user is on gozler's "see every file" list; an administrator is filtered too. Such an answer counts as complete: exit 0 and no warning. A folder whose permissions gozlerd has not read yet, or could not read, is hidden the same way. | A file that is missing from a complete answer may still exist. Only [the person's own check](#presence) says that a file is gone. | `docs/SECURITY.md:8-9`, `docs/DESIGN.md:189-194` |
| The Include settings (drives, folders, name patterns) are one setting for the whole PC, and only an administrator changes them. | TinyTorrent cannot use them to keep unwanted files out of Library. [The query](#the-query) leaves those files out. | `docs/DESIGN.md:51-53` |
| Exit codes. 0: complete. 2: matches may be missing because a volume is not ready; the answer also has `volumes_incomplete`. 7: the service runs but did not answer: its pipe is not open yet, its reply was late or refused, or it is older than `gozler.exe`. 8: the service is stopped, disabled or not installed. 4: bad query. 6: unknown option. 1, 3 and 5: other failures. | See [What the person sees](#what-the-person-sees). | `crates/gozler-core/src/escompat/exit.rs:11-41` |
| `--json --status` returns each indexed volume with its phase. Two phases end by themselves: the index is being built, or scanned again. Two do not: live updates stopped, or the index failed. | After an incomplete answer, one status call names the volumes that are not ready. | `docs/JSON.md:117-120`, `crates/gozler-core/src/types.rs:153` |
| `--server-version` prints the running service's version. | TinyTorrent runs it only after a call has failed, to name the cause. | `docs/JSON.md:122` |
| `-timeout` waits for volumes to be ready; it is not an execution deadline. | TinyTorrent sets its own deadline and does not pass `-timeout`. | `docs/JSON.md:128` |
| `-n all` builds the whole answer in memory and writes it as one JSON object. | One call per refresh. [Measured](#measured-on-11-november-2026) below. | `docs/JSON.md:23`, `crates/gozler-core/src/output/results.rs:88` |
| Search terms match in any order, as substrings. | A person's typed search never goes to gozler. | `docs/JSON.md:24` |
| A term ends at a space. A path with a space needs quotes inside its term: `!"C:\Program Files\*"`. Without them the query is still valid, exits 0, and matches nothing. | The query builder quotes every path. | Run on 11 November 2026: 0 results unquoted, 3,603 quoted |
| One query can hold `ext:` lists, `size:` limits, groups `<a b>\|<c>` and exclusions `!term`, up to 256 terms; each `ext:` value counts as a term. | One call leaves out what Library does not want. | `docs/DESIGN.md:113-117`, `crates/gozler-daemon/src/engine.rs:163` |
| The installer is `gozler-<version>-x64-Setup.exe` on the GitHub release. It needs administrator permission, installs to `Program Files\Gozler`, and starts the service. Releases are not signed and publish no hash files; gozler's own window checks a download against the `digest` that GitHub gives the asset. | Pin the version and its SHA-256 when TinyTorrent is built. | `installer/Setup.iss:15,41,53`, `.github/workflows/release.yml`, `docs/DESIGN.md:913,948` |
| A silent gozler setup grants nobody the "see every file" right. | Run gozler's setup with its own wizard. | `installer/Setup.iss:342-348` |

## What we add to gozler

**Each `--json` result names its file: the identity of its volume and the
file's ID on that volume, when the call asks for them.** gozlerd already holds
both. Its index is keyed by the file's ID (`Frn`), and it knows each volume by
its GUID path. The ID is text, because a ReFS ID has 128 bits.

Reason: a path is the only identity a result has today, so a renamed file
looks like one file gone and another file new. Without the ID, a person who
renames a movie, or the folder that holds 400 of them, finds that Library
forgot every match they corrected by hand. With the ID, a file that changes
its name or its folder on one volume stays the same Library entry. The
volume's identity also tells "the drive that held this file is not connected"
from "the file is gone" when another drive has taken its letter.

The change is made in `3rdParty/goz` under its own `AGENTS.md`. It adds a field
to the result payload, so it raises `PROTO_VERSION`, and gozler's command-line
and JSON rules decide the option and field names. TinyTorrent's expected gozler
version becomes the release that carries it. A person whose gozler is older
sees "older than TinyTorrent needs" (below). TinyTorrent has one way to
identify a file, not a second one for old versions.

Not added, each for a reason:

- **A change notification, or an answer that holds only the changes.** A full
  answer costs little at the sizes measured below, an unchanged answer ends
  the refresh at once, and a subscription would keep a process open while
  Library is shown.
- **A flag for "some names are hidden from you".** The person's own check
  covers that case, and covers an error in the index too.
- **Removable drives.** gozler's index and its security rules are built on
  fixed volumes. Library does not find files on a removable drive; accept
  this.

## Measured on 11 November 2026

On the owner's PC: gozler 0.5.9, two volumes, 2.37 million indexed entries.

| Query | Files found |
| --- | --- |
| Every video, audio and subtitle extension, as the draft asked | 105,242 |
| The same, with `.ts` and `.mts` only from 20 MB | 4,043 |
| The same, without Windows, Program Files, ProgramData, AppData and the Recycle Bin | 3,340: 177 video, 3,022 audio, 141 subtitle |

101,199 of the 105,242 files were TypeScript source: `.ts` and `.mts` are also
TypeScript extensions, and none of those files reached 20 MB. An answer of
100,226 files was 28 MB and took 0.6 s. The answer for 3,340 files is 0.9 MB.

Library's own SQL, on the product's schema and projection in an in-memory
SQLite database on the same PC:

| Saved files | Copy every file into the search snapshot | Refresh 5 changed files in it |
| --- | --- | --- |
| 2,000 | 15 ms | 1.2 ms |
| 20,000 | 134 ms | 4.2 ms |
| 100,000 | 716 ms | 16 ms |

## Owners

| Owner | Responsibility and reason |
| --- | --- |
| gozler | Indexes the drives, answers queries, names each file. It is not TinyTorrent code. |
| Disk source (new, C#, beside `TorrentSource`) | Library's second data source. It runs `gozler.exe`, builds the query, compares the answer with the saved disk files, checks File presence, and hands the changes to `FileSources`. It runs in the window process, because all Library work lives there and the answer goes straight to SQLite. |
| `FileSources` (existing) | Accepts each contribution's files into SQLite and owns File confirmation and reconciliation. It learns which source a contribution belongs to, so each source withdraws only its own contributions. |
| `Database` (existing) | Runs the schema migration, built as revision 10. Keeps the search snapshot current: each transaction copies only the entries it changed, so a disk refresh needs no snapshot code of its own. |
| `library_settings` (existing) | Holds the option, beside Library's other options. |
| Install action (new, C#) | The one download, check and run of gozler's setup, for the in-app prompt. TinyTorrent's own `Setup.iss` has no part in it. |
| Existing Library, video lookup, file facts and subtitle owners | Unchanged. Each keeps its rule and sees a disk file through the same projection as a torrent file. |

## The option

**Discover media for Library** starts **off**. With it off, Library never
runs `gozler.exe`. The engine's use of gozler in
[plan 21](21-missing-torrent-files.md) has no setting: it asks a gozler that
is installed, and never installs or starts one. The option is saved in
`library_settings`, because the engine has no use for it and Library's other
options are there.

- Settings shows the option, what it needs (gozler, and the memory gozler
  uses), that it covers fixed drives, and the gozler state under it. Below
  that it lists the [hidden folders](#hidden-folders), each with Remove.
- Library's empty state, "No files yet", gains a button that switches the
  option on, so a person without torrents finds it where they look for their
  files. No other prompt offers it: the draft's offer on the first Library
  visit was optional there, and a prompt the person did not ask for needs a
  remembered answer and its own design.
- Switching the option off withdraws the disk contribution, as removing a
  torrent withdraws its contribution. Files that only gozler reported leave
  Library with their stored facts; files that a torrent also lists stay. The
  option's description says that the files found on the PC leave Library.

TinyTorrent has no drive setting. gozler's Include settings decide what gozler
indexes, and TinyTorrent never changes them. What Library leaves out of
gozler's answer is in [the query](#the-query).

## Running gozler.exe

- **Find it** in the folder of the program that the Windows service `gozler`
  runs (the service's `ImagePath`), not through `PATH` and not from the current
  folder. Reason: a folder that anyone can write to must not decide which
  program TinyTorrent runs. This also finds gozler right after installation,
  when a running TinyTorrent does not see the `PATH` entry yet. A program
  started by its full path needs no `PATH`, and `gozler.exe` reaches the
  service through its pipe.
- **Run it directly**, never through a shell. Pass each term as a separate
  argument, with every path quoted inside its term. Read stdout as UTF-8 JSON
  and stderr as text.
- **Deadline:** 30 seconds per call, then end the process. A call normally
  takes well under a second. The cause shown is "gozler did not answer in 30
  seconds".
- **Never on the UI thread.** Calls run in the app's background work and show
  in the existing work presentation. The window never waits for gozler.
- **A timeout or failure is not retried in a loop.** The next try comes from a
  trigger the person controls: Refresh, Retry, or showing Library again.
- **Exit 7:** retry after 5 seconds, at most 3 times, then report it. Reason:
  the pipe may open soon (`docs/DESIGN.md:96`). Exit 8 is never retried.
- **One call per refresh.** A second call follows only an answer that needs
  it: `--status` after an incomplete answer, and `--server-version` after a
  failure. The query's own exit code and `volumes_incomplete` say whether the
  answer is complete.
- **Use what came back.** An answer with exit 2 is used and labelled
  incomplete; it is never turned into an error.

## What the person sees

One default for each case. Settings shows the state under the option. A
Library message names the same cause in the same words.

| Case | How we know | What the person sees | Default action |
| --- | --- | --- | --- |
| gozler not installed | The Windows service `gozler` does not exist. | "gozler is not installed." Switching the option on opens the install prompt. Settings shows an Install button. | The prompt. |
| Installed, `gozler.exe` missing | The service exists and the folder of its program has no `gozler.exe`. | "gozler's command-line program is missing. Repair gozler." | No install offer: that would replace the person's gozler (decision 3). |
| Installed, service stopped or disabled | Exit 8. | "gozler's service is not running. Start it in gozler, then Retry." | TinyTorrent does not start or repair the person's service (decision 3). gozler's window offers its own repair. |
| Service did not answer | Exit 7, still 7 after 3 tries. | "gozler's service did not answer." | Retry button. |
| A drive is still being indexed | Exit 2, and `--status` shows a volume that is being built or scanned again. | Library shows the files found so far and names the drive that is still being indexed. | Ask again 10 seconds after each such answer while Library is shown, because the person is watching a list that says it is incomplete. This ends when the volume is ready or Library is hidden. |
| gozler cannot keep a drive current | Exit 2, and `--status` shows a volume whose live updates stopped or whose index failed. | Library says that its list for that drive may be out of date, and that gozler's window says why. | No automatic retry, because the cause does not end by itself. The next refresh asks again. |
| Older than expected | Only after a call failed (exit 1, 4 or 6): `--server-version` reports less than the expected version, or exits 6. | "gozler 0.5.9 is older than TinyTorrent needs. Update it in gozler.", with both versions. | No install offer (decision 3). gozler has Help > Check for updates. |
| Newer than expected | Not checked: a call that works needs no version check. | Nothing. | Use it. Reason: later releases add fields. |
| gozler refused, exit 1, 3, 4, 5 or 6 | stderr; then one `--server-version` to tell "older" from any other failure. | "gozler could not answer." with stderr in the log. | Retry button. |

The expected version is one number, kept beside the pinned download record
(below). Raise it only when TinyTorrent starts to use something newer.

## Installing gozler

### The prompt

The person switches on the option while gozler is not installed. The dialog
follows `app/AGENTS.md` "Dialogs" and the [button rulings](../contracts/ui.md#buttons):
one `Shared.Dialog` that `MainWindow` opens through `Interact`, and `ShowEditor`
with a draft, so it stays open while the work is pending or has failed.

- Title: "Install gozler?"
- Body: what gozler does for the feature, that it runs as a Windows service,
  that it uses memory, and that Windows asks for administrator permission.
- Footer: the download size and source (GitHub).
- Buttons: Install (default) and Cancel.

| What happens | What the person sees and gets |
| --- | --- |
| Person chooses Cancel | The dialog closes. The option stays **off**. Nothing is downloaded. Switching it on later asks again. |
| Download fails | The dialog stays open and shows the cause. The button reads Retry. The option stays off. |
| Check fails (the SHA-256 differs) | The file is deleted. The dialog says the download is damaged and offers Retry. |
| Person declines the Windows permission prompt | Windows reports error 1223 when the person cancels. The dialog says "Windows did not give permission. gozler is not installed." with Retry. |
| gozler's own setup is cancelled or fails | The dialog decides from the result, not from the exit code: when setup has exited and the Windows service `gozler` does not exist, it says "gozler is not installed." with Retry. It runs no gozler call. |
| Install succeeds | The dialog closes, the option switches **on** and the first refresh starts. No restart is needed: TinyTorrent finds `gozler.exe` through the service, and gozler's setup starts the service (`crates/gozler-daemon/src/service.rs:78`). The first answers can be incomplete while the index builds; Library fills as the table above says. |

Cancel works until gozler's setup starts; it stops the download. After that, the
setup cannot be stopped from TinyTorrent, as with TinyTorrent's own update
(`app/src/Shell/Updates.cs`). Closing the window then leaves the setup running;
the option stays off, and the next switch-on finds gozler installed and needs no
prompt.

gozler's setup runs with its own wizard, not silently. Reason: a silent setup
grants nobody the "see every file" right, and that choice decides which folders
Library can see. The person answers it on gozler's page. TinyTorrent starts the
setup through the Windows shell, as a double-click does, so Windows shows the
permission prompt for gozler's manifest. `Updates.cs` starts TinyTorrent's own
setup without the shell; that start is not reused here.

### Download and check

`Updates.cs` already has the HTTP download and a SHA-256 compare; the prompt
reuses the download and moves the compare into a shared helper.

- `installer/Build.ps1` writes one record: gozler version, the URL of
  `gozler-<version>-x64-Setup.exe` on its GitHub release, its SHA-256 and its
  size. It downloads the asset, hashes it and compares the hash with the
  `digest` GitHub gives the asset. The record goes into the published folder
  beside the executables, where the app reads it. `Setup.iss` does not use it.
- The app downloads over HTTPS, compares the SHA-256 with the record, and
  deletes a file that differs. A file with no record is never run.

The check is weaker than for the Microsoft runtimes, which are verified by
publisher signature; gozler releases are unsigned. It is acceptable because the
owner publishes both projects and pins the hash when TinyTorrent is built.

### Uninstalling TinyTorrent

TinyTorrent's uninstaller leaves gozler installed. Reason: it is a separate
program with its own entry in Installed apps and its own uninstaller, the person
may use it directly, and TinyTorrent's own Setup treats the Microsoft runtimes
the same way. TinyTorrent does not record who installed gozler.

## Library

### The query

One call per refresh. Its terms, each a separate argument:

```text
gozler.exe --json -n all -a-d -attributes <the option that asks for file identity>
  <ext:VIDEO|ext:AUDIO|ext:SUBTITLE|<ext:ts;mts size:>20mb>>
  !"<Windows>\*"  !"<Program Files>\*"  !"<Program Files (x86)>\*"
  !"<ProgramData>\*"  !"<profiles folder>\*\AppData\*"  !"*\$Recycle.Bin\*"
  !"<each hidden folder>\*"
```

- **The extension sets come from their owners:** video and audio from
  `FileName`, subtitles from `SubtitleFile`. Both are pattern expressions
  today (`app/src/Shared/FileName.cs:9`, `app/src/Subtitles/SubtitleFile.cs:15`).
  Each owner exposes its set as data, so its own test and the query read one
  definition. `FileName` gives `.ssa` and `.vtt` the kind Other today; give
  them the kind of the other subtitle files in the same change. The app
  classifies each result by its extension through those owners.
- **`.ts` and `.mts` count as video only from 20 MB.** Reason: they are also
  TypeScript extensions, and on the owner's PC that is 101,199 files, none of
  which reaches 20 MB. A transport-stream video below 20 MB is a streaming
  segment, one of hundreds that make a film, and a person does not open those
  one by one. With this rule the draft's special handling of `.ts` is gone.
- **Windows' and programs' own folders are left out.** Reason: programs keep
  their sounds and clips there, and a person does not keep a collection there.
  The paths come from Windows' known folders, not from typed names. The
  Recycle Bin is left out, so a recycled file is a file that is gone.
- **Each [hidden folder](#hidden-folders) is left out** the same way.
- **`-a-d`** asks for files only.
- **No person-typed text** is in the query.

Automatic readers skip a file whose attributes show a cloud placeholder (recall
or offline). The person's Open follows Windows.

### Hidden folders

The query leaves out Windows' and programs' own folders, but games and programs
also keep media elsewhere. The person removes those files from Library with one
command (decision 9).

- **Hide folder from Library** is a Library command on a file that no torrent
  lists, in the Library menu and in the row menu. It offers the file's folder
  and each folder above it, nearest first, so one choice hides a whole game or
  a whole drive.
- Choosing a folder saves it in Library's settings and withdraws the disk
  files under it in the same database job, so their rows leave at once. Their
  stored facts go with them, as when the option is switched off. No
  confirmation is asked, because Settings undoes the choice.
- Every later query leaves the folder out, as one more exclusion. A hidden
  folder that lies inside another hidden folder is not sent.
- Settings lists the hidden folders under the option, each with Remove.
  Removing one starts a refresh, and its files return as new entries.
- The list has a limit, because one gozler query holds 256 terms. Step 4
  finds how many folders fit beside the fixed terms, and the command says so
  when the list is full.
- A hidden folder does not hide a file that a torrent lists, because the
  torrent's own claim lists it. So the command is not offered on such a file.

### When

- When Library is shown.
- When the window becomes active while Library is shown. Reason: that is the
  moment after the person moved or deleted a file in another program; without
  it, the row stays for minutes and Open fails.
- On the person's Refresh, F5. This is a new Library command: Library has none
  today, because the torrent source needs none.
- Every 5 minutes while Library is shown and its window is active. Reason:
  gozler has no change notification, and a file can arrive from another
  program while the person watches.
- Ten seconds after an incomplete answer, while Library is shown and gozler
  is still indexing a drive ([What the person sees](#what-the-person-sees)).
- Never while Library is hidden. A refresh that is running then finishes.
- After a failure, only Refresh, Retry and showing Library again start one.
- One refresh runs at a time. A trigger that arrives while one runs starts one
  more after it, because the running answer may be older than the change.
- Refreshes run only while the option is on and Library's source is
  connected, as the torrent source's reads do (`app/src/Shell/Features.cs:326`).
  Disk discovery adds no second readiness state.

### One refresh

1. **Ask gozler**, in the background. The [absent locations](#absent-locations)
   are checked again at the same time.
2. **The same bytes as the last accepted answer end the refresh.** Nothing is
   parsed or written.
3. **Read the saved disk files** in one short database read: path, volume,
   file ID, size and modified time.
4. **Compare, outside the database:**
   - *The same path* is the same entry. A known size or modified time that
     differs is saved, and the entry's saved file facts are discarded, so they
     are read again when the entry is displayed. An unknown size or date keeps
     the saved value. A same-size edit that keeps its modified time is not
     detected; accept this.
   - *A saved path that the answer lacks, whose volume and file ID the answer
     has at one new path,* is a file that was renamed or moved on its volume.
     It stays the same entry with the new path. A file with several hard links
     is paired only when its ID lost exactly one path and gained exactly one.
   - *Any other saved path that the answer lacks* goes to the
     [presence check](#presence), unless its volume is not ready (below).
   - *Any other new path* is a new entry.
5. **Check presence** for those paths.
6. **Pair a move to another volume:** when exactly one file with a given name
   and size was found absent and exactly one new file has that name and size,
   it is the same entry at its new path. Any other absent file is removed.
7. **Accept through `FileSources`** in one short database job. The job checks
   that the option is still on and that this refresh is the current one, then
   writes only the changes, updates the snapshot rows of the entries it
   changed, and confirms the disk contribution. A first import has no saved
   files to compare with, so it writes its files 2,000 to a job, and a typed
   search waits for one job, not for the import.

An incomplete answer (exit 2 or `volumes_incomplete`) adds and updates as a
complete one does. One `--status` call then names the volumes that are not
ready, and a saved path on such a volume is left alone: no presence check and
no removal. Reason: one drive that gozler cannot read must not keep deleted
files on the other drives in Library. The status is read after the answer, so
a volume can become ready in between. Its missing paths are then checked and
found present, which costs one check for each and removes nothing.

A refresh that the window's close, the option or a database replacement ends
is discarded; the process is ended and its result is never accepted.

### Presence

gozler says what exists. It does not say what is gone: a file can be missing
from a complete answer because the person may not list its folder, because
gozlerd has not read that folder's permissions yet, because gozler's Include
settings leave it out, or because its drive is not connected.

Rule: **a location is known absent only when the person's own check says so.**
This is the File presence rule of the product glossary.

- **The volume first.** One check for each volume: the path's volume answers,
  and it is the volume gozler named for the file. Otherwise every location on
  it is unknown, and nothing changes. Files on a drive that is not connected
  stay in Library with their facts, and Open reports Unavailable, as today.
- **Then each path.** File or folder not found: absent. Found: present. Any
  other answer, such as access denied: unknown, and nothing changes.
- **A file found present stays**, and is not checked again until gozler
  reports it again or the window opens again. So a folder that gozler cannot
  show the person costs one check for each file in a session, not in each
  refresh.
- The checks run in the app's background file work, never inside a database
  job, and end with the window or the option.

The number of checks follows the change: normally the files the person deleted
since the last refresh. The most a session can make is one for each saved disk
file.

| Situation | Library behavior |
| --- | --- |
| A file that only gozler reported is deleted | Its entry and stored facts are removed. |
| A file that a torrent also lists is deleted | The disk contribution's claim is removed, and the location is saved as absent, so the row leaves Library although the torrent still lists the file. See [Absent locations](#absent-locations). |
| A file goes to the Recycle Bin | It is gone, as above. Restoring it makes a new entry: an automatic match returns from the saved provider answers without a request, and a match the person corrected by hand must be made again. |
| One hard link disappears | That location is removed; the others stay. |
| Rename, or move on the same volume | The same entry, by its file ID. |
| Move to another volume | The same entry, when one absent file and one new file share name and size. |
| Drive not connected | Nothing changes. |
| TinyTorrent moves, renames or deletes a torrent's files | The torrent's rows change at once, as today. The disk rows of those files follow at the next refresh, which starts when Library is shown again; those commands are given on Torrents. |
| gozler stops showing a file that exists | The row stays, with its last saved facts. |
| Unfinished torrent file | TinyTorrent's unfinished suffix is not a media extension, so gozler does not report it. The completion rule below covers a file at its final name. |

### Absent locations

**A location that the presence check finds absent is saved as absent**
(decision 8).

- The fact is saved with the location, so the row stays out after the window
  opens again. Without it the torrent's claim would list the file again, and
  nothing would check it a second time: the presence check looks only at the
  paths that the disk contribution holds.
- `current_files` leaves an absent location out. It is the one projection that
  decides what Library lists and what file work may read, so no other reader
  tests the fact. The torrent's stored facts for the file stay, because the
  torrent still lists it.
- **Each refresh checks the absent locations again**, before it uses gozler's
  answer, and a location whose file exists loses the fact. Reason: the check
  must not depend on gozler's answer, or a file that returns in a folder that
  gozler does not report, such as a hidden folder, would stay out of Library
  for good. The number of checks is the number of files that the person
  deleted while keeping their torrent.
- The fact also ends when the location loses its last contributor, and when
  the option is switched off. With the option off, the torrent's claim decides
  alone, as today.
- A file that is downloaded again returns at the next refresh. Until
  [plan 21](21-missing-torrent-files.md) is built, the engine downloads such a
  file again by itself when it notices the loss.
- When plan 21 is built, Library also asks the engine for a look at that
  torrent. The engine then skips the lost file until the person answers.
  Library's own rule for a file that is not wanted keeps the row out, and this
  saved fact is removed for torrent-listed files, as that plan says.
- A torrent's file that gozler never reported, such as one on a network share,
  is never checked, so it stays listed as today.

### How disk files join the catalogue

The disk source is the second caller of `FileSources`, beside `TorrentSource`.
The Library contract asks for a small data-source interface; `FileSources` is
that seam, and this is the change that gives it a second source.

- **One contribution holds every disk file, and a contribution records its
  source.** The migration adds that fact to the contribution. A reserved
  origin text would be a value that selects a second behavior, which the code
  style forbids.
- **Each source withdraws only its own contributions**, and the torrent
  source's bookkeeping holds torrents only. `FileSources` holds this rule in
  one place.
  [Code that takes every origin for a torrent](#code-that-takes-every-origin-for-a-torrent)
  lists where it matters.
- **A disk file's identity is its contribution and a number given when the
  file is first seen, never used again.** Identifications and file facts are
  keyed by contribution and file number today, so they work unchanged, and
  the number stays with the file through a rename or a move.
- **Saved for each disk file**, besides what a torrent file saves: its
  volume's identity, its file ID and its modified time.
- **File confirmation:** the disk contribution is confirmed when the first
  answer of the window session is accepted. Until then its saved rows are
  listed and no file work uses them, which is the existing rule for a torrent
  whose file list is not read yet. `FileSources` owns this fact, and the
  shared projections `collected_files`, `pending_origins` and `current_files`
  read it there. It is not an entry in the torrent source's read schedule. An
  unconfirmed disk contribution counts as pending for cleanup, so its stored
  facts are kept while gozler does not answer.
- **A location that a current torrent lists follows the torrent.** The
  projection `current_files` takes the highest completion among a location's
  contributors (`app/src/Shared/Storage/Database/Schema.cs:167-173`), and a
  disk file always counts as complete and wanted, so unchanged it would show
  a torrent's unfinished file as finished and readable. `current_files` takes completion
  and readability from the torrents when a torrent lists the location, and
  from the disk contribution only when none does. It is the one place that
  derives both.
- **Removing a torrent leaves the disk contribution**, so the file stays in
  Library. Its choices stay too, by the existing rule that shares them with
  the contributors that remain.
- **Paths stay unique without regard to case.** Two files whose paths differ
  only by case exist only in a folder with Windows' case sensitivity switched
  on; Library keeps one row for them. The draft's change to path uniqueness is
  dropped.
- **A disk file has no torrent.** Show in Torrents is unavailable for it, and
  the File page shows no Torrent row.

### Code that takes every origin for a torrent

Today every contribution is a torrent, and the code below relies on it. Each
place gets the rule beside it. The table comes from a search of `app/src` for
the catalogue's tables and views on 11 November 2026. Repeat that search before
step 5 starts, because a reader that is added meanwhile and missed acts on the
disk contribution as on a torrent.

Every other reader goes through the shared projections `collected_files`,
`pending_origins` and `current_files`, and so follows File confirmation without
a change: identification, file facts, Erase metadata, subtitle targets and
their associations, the sharing of choices between contributors, and cleanup.

| Code today | What it would do to the disk contribution | Rule |
| --- | --- | --- |
| `FileSources.Reconcile` deletes every origin that the torrent list does not name (`app/src/Shared/FileSources.cs:155-162`). | Delete the disk catalogue at the first reconcile after a window opens. | Each source withdraws only its own contributions. |
| The same function fills the lists for the subtitle completion search, `known_torrents` and `new_origins` (lines 142-149). | Start automatic subtitle downloads for disk movies. | Only torrents enter those lists. |
| `FileSources.Next` schedules any contribution's file list for a read, and `TorrentSource` then asks the engine for it (`FileSources.cs:31-47`). | Ask the engine for the files of a torrent that does not exist. | The read schedule holds torrents only. |
| `FileSources.Invalidate` clears the confirmation of every contribution at each new engine session, and `FileSources.IsReady` waits until every contribution is checked (`FileSources.cs:8-13`, `:62-72`). | Lose the disk contribution's confirmation with every engine session, and keep Library from ever becoming ready. | Both cover torrents only. |
| Series inference joins every file of the same origin to find a file's siblings (`app/src/Library/Store/Identification.cs:467-469`). | Join every disk file for each call. | A disk file's siblings are the files in its folder. |
| The subtitle owner's `PathAccess` answers Pending while any contribution is unconfirmed, and Protected for a path that any contribution lists (`app/src/Subtitles/Acquisition/Files.cs:234-250`). | A gozler that does not answer would hold every subtitle move and deletion, for torrents too. A subtitle that TinyTorrent saved would become Protected from TinyTorrent as soon as gozler reports it. | Both answers count torrents only. A found subtitle stays protected by its `created=false`. |
| The subtitle owner takes one common folder for all files of an origin, and lists that folder with every folder below it when the origin's save folder contains it (`app/src/Subtitles/Acquisition/Discovery.cs:289-351`, `:598`). | List a whole drive, folder by folder. | The disk contribution has no save folder and no common folder. See [Subtitles](#subtitles). |
| `Store.ReadOrigins` returns every contribution of an entry as a torrent to show (`app/src/Library/Store.cs:19-31`). | Enable Show in Torrents for a file that has no torrent; the command would then do nothing. | Only torrents are origins to show. |
| The person's subtitle batch over the whole collection selects every complete movie (`app/src/Subtitles/Acquisition/Storage.cs:682-697`). | Send supplier requests for every movie on the PC. | See [Subtitles](#subtitles). |

### Subtitles

- A subtitle file that gozler reports is a Files entry, even when it belongs
  to no movie.
- **The subtitle files beside a disk movie come from the saved catalogue.**
  The subtitle owner asks one function for the subtitle files near a movie.
  For a movie that a torrent lists, that function lists the folder, as today.
  For a disk movie it reads the catalogue: the subtitle files in the movie's
  folder and in its `Subs` and `Subtitles` folders. Reason: gozler already
  reported them, so a listing would repeat that work on the disk, and rule 2
  allows none. Matching and association keep their one owner.
- A disk movie is checked when it is displayed, and again only after a refresh
  that added, removed or moved a subtitle file. An unchanged disk causes no
  second check.
- A subtitle that discovery found keeps `created=false`: discovery grants no
  right to move or delete it.
- **Automatic subtitle downloads** start from a torrent that finishes, so a
  disk movie gets none.
- **Download Subtitle on selected disk movies works.** It is the person's
  command for those files, and its path needs only the file's entry, name,
  size and readability (`app/src/Subtitles/Acquisition/Choose.cs:168-186`).
- **A batch over the whole collection covers torrent movies only.** Reason:
  the person who starts it expects the movies that TinyTorrent downloaded, not
  a supplier request for every movie on the PC.

### What stays as it is

- **Facts are fetched only for displayed entries**, under the Library
  contract's no-scan ruling. That covers provider lookups, the automatic
  video-information setting, per-movie search protection and the file facts
  reader, for disk files as for torrent files. The draft's sentence about
  bounded pending batches is gone, because no owner reads a whole pending set.
- **A person's typed search** runs on the saved catalogue in SQLite under the
  repository's word-start rule. It is never sent to gozler.

### Opening Library stays fast

Four stages of opening and searching grow with the saved catalogue today. The
disk source multiplies the catalogue, so each stage gets its change before the
option can be switched on.

| Stage today | Its size | Change |
| --- | --- | --- |
| Built in step 3. The first search after the window opens, and the first after any change to the catalogue, copied every file into the search snapshot. | 7 ms for each 1,000 saved files: 134 ms at 20,000. | The connection builds the snapshot when it opens, in the background, and every transaction copies only the entries it changed: about 0.05 ms for each changed entry at 20,000 files. |
| One row object is built for every match of the shown configuration, on the thread that started the search (`app/src/Library/Browser.cs:921-948`). | Not measured. Videos, the default, holds a fraction of the files; Music and Files can hold all of them. | Measure at 1,000 and 2,000 disk files (step 7), and change what makes opening or typing wait. |
| A displayed movie that is not checked yet makes the subtitle owner read every file of that movie's origin, and then every collected file (`app/src/Subtitles/Acquisition/Discovery.cs:97`, `:289-293`). | Not measured. For the disk contribution, the origin is every disk file. | Compute the common folder for torrents only, and read the displayed entries only. |
| Every job waits for the one database lock (`app/src/Shared/Storage/Database.cs:83`). | A job sized by the catalogue makes a typed search wait that long. | No job of a refresh is sized by the catalogue: the compare and the presence checks run outside the database, and the first import is split. |

The product's tables are built for 100 to 500 rows normally and for 10,000
rarely (the owner's sizes for TableView, 3 November 2026). A person with more
media files than that still gets every file, and opening and typing then cost
what step 7 measures for each 1,000 files. Record that cost in the Library
contract.

## Implementation order and evidence

Use the [testing](../agents/testing.md), [compilation](../agents/compilation.md)
and [coordination](../agents/coordination.md) policies. Steps 1 to 3 do not
depend on each other. Step 4 needs only the option name that step 1 chooses.

1. **gozler: file identity in results.** In `3rdParty/goz`, under its own
   rules and build. Evidence: a file renamed on a volume keeps its ID in the
   answer, and an older `gozlerd` is refused by the new `gozler.exe` as the
   protocol rule says.
2. **Built: the first migration.** Revision 10 adds a contribution's data
   source, a disk file's volume, file ID and modified time, a location's
   absence, the list of hidden folders and the option, as the
   [database contract](../contracts/database.md) records. Evidence:
   `app/tests/SourceChecks.ps1` opens a revision 9 database with its saved
   match.
3. **Built: opening Library stays fast.** The first row of the table above.
   Evidence: `SourceChecks.ps1` counts the snapshot rows written: none by the
   first search after opening, five for a change to five entries.
4. **The runner.** Finding `gozler.exe`, the exit-code states, the 30-second
   deadline, exit 7's three tries, the query builder, and how many hidden
   folders one query holds. Evidence: a folder
   whose name has a space gives the same count as gozler's own window, because
   an unquoted path gives an empty answer with exit 0.
5. **The disk source.** After steps 1 to 4. Evidence, each for a failure the
   person would see: the torrent reconcile leaves the disk contribution; a
   renamed folder keeps its entries and their matches; a drive that gozler is
   still indexing loses no entry, while a file deleted on another drive leaves
   Library; a drive that is not connected removes nothing; a torrent's
   unfinished file is not shown as finished; an unchanged answer writes
   nothing; a displayed disk movie lists no folder; a gozler that does not
   answer holds no subtitle work for torrents; the torrent source asks the
   engine nothing about the disk contribution; a deleted file that a torrent
   lists leaves Library and stays out after the window opens again; the files
   of a hidden folder leave at once and stay out after the next refresh.
6. **The option, the Settings state with the hidden folders, the Hide folder
   and Refresh commands, and the install prompt**, with their text in every
   shipped language catalogue. Design the two commands and the Settings list
   as `app/AGENTS.md` asks before writing them. Check each outcome row of the prompt
   once, including the declined permission prompt and a damaged file.
7. **Measure** with 1,000 and 2,000 disk files in a test database: opening
   Library in each configuration, typing a search during a refresh, the
   `gozler.exe` call and the app's memory while it runs. Change any stage that
   makes opening or typing wait, then record the cost for each 1,000 files.
8. **Contracts.** Move this plan's rules into them and delete the plan. The
   Library contract's membership ruling, its sentences that Library checks no
   file's existence and has no periodic refresh, and its "Future data sources"
   section change with this feature; so do the database contract's file
   identity and durable tables, the UI contracts for Settings, the empty state
   and Refresh, and localisation.
