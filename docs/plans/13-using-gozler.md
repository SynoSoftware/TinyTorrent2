# Using gozler to find files

**Work in progress: not reviewed.** Review this plan once the other agents'
current work is finished, before anyone implements it. Design plan,
2026-11-11. Nothing is implemented yet. The
[Library](../contracts/library.md), [engine](../contracts/engine.md),
[database](../contracts/database.md) and [pipe API](../contracts/pipe-api.md)
contracts own current behavior; update them alongside implementation.

## North star

**Library shows the video, music and subtitle files that are on the person's
drives, and keeps their locations current. Optionally, the engine finds torrent
files that the person moved outside TinyTorrent and uses them again. Both ask
gozler, a separate program, and both stay off until the person switches them
on. Missing torrent files are never downloaded again without the person's
permission.**

gozler is the owner's own project (MIT licence, source in `3rdParty/goz`,
version 0.5.9). It already indexes the fixed drives and answers a query in
milliseconds. TinyTorrent does not index files itself.

## Decisions of 11 November 2026

1. **TinyTorrent uses gozler for both features:** Library's media discovery,
   and finding torrent files that were moved outside TinyTorrent. Reason: the
   owner does not want to build a file index a second time.
2. **TinyTorrent's installer does not bundle gozler, and does not offer it.**
   The only way TinyTorrent installs gozler is the in-app prompt in decision 6.
   Reason: gozler is a separate program with its own installer, updates and
   uninstaller. Both features start off, so an installer offer helps nobody who
   never switches one on, and a second entry point would need a second
   implementation of the download and its check.
3. **TinyTorrent never overwrites or replaces a gozler that is already
   installed.** The person's gozler may be older or newer than the version
   TinyTorrent expects. TinyTorrent uses it as it is. Reason: it is the
   person's installation.
4. **Only one gozler service runs on the machine:** the person's own, or the
   one TinyTorrent installed. TinyTorrent never runs a second gozler beside an
   existing one. Reason: gozler keeps its index in memory (the owner's figure is
   200-300 MB), which works against TinyTorrent's promise to be light. For the
   same reason both features are opt-in.
5. **TinyTorrent talks to gozler only by running `gozler.exe --json`.** It does
   not use gozler's named pipe. Reason: the pipe's protocol version must match
   exactly, so a pipe client breaks with an older or newer gozler, and the pipe
   client exists only in Rust.
6. **When the person switches on a feature and gozler is not installed,
   TinyTorrent asks whether to install it.** Yes downloads gozler's installer,
   checks it and runs it. Reason: the person should not have to leave
   TinyTorrent to install what the feature needs. See
   [Installing gozler](#installing-gozler).

## What gozler gives us

Paths are under `3rdParty/goz`. These are the only gozler facts this plan relies
on.

| Fact | Consequence in this plan | Source |
| --- | --- | --- |
| `--json` output is for scripts. Fields are only added over time. | Read the fields we need; ignore the rest. | `docs/JSON.md:92` |
| The service `gozlerd` runs as LocalSystem and starts automatically. | A stopped service is the person's state, not ours. | `crates/gozler-daemon/src/service.rs:3,73` |
| It indexes fixed NTFS and ReFS volumes only. Removable drives are out of scope. | Files on removable drives are never found. | `docs/DESIGN.md:19`, `docs/JSON.md:29` |
| A result has `path`, `is_dir`, `size`, `mtime`, `created` (and `_ft` forms), and `attributes` when asked with `-attributes`. It has no file ID. | A location is a path. A moved file is recognised by name and size. | `docs/JSON.md:74-90`, `docs/DESIGN.md:2651` |
| It has no change notification. | We ask again when we need to know. | `docs/DESIGN.md:583,2651` |
| Each hard link is a separate name. | Every matching location counts. | `docs/DESIGN.md:46` |
| It shows each user only the entries in folders that user can list, unless that user is on its "see every file" list. | Files in folders the person cannot list are not found. | `docs/SECURITY.md` (second bullet) |
| A search answer has `volumes_incomplete` and an exit code (2 means matches may be missing). Exit 2: Exit 7: the service runs but its pipe is not open, or the service is older than `gozler.exe`. Exit 8: the service is stopped, disabled or not installed. Exit 6: unknown option. | See [What the person sees](#what-the-person-sees). | `crates/gozler-core/src/escompat/exit.rs:11-36`, `docs/JSON.md` "Read search results", `docs/JSON.md:99-105`, `docs/DESIGN.md:96` |
| `--server-version` (new in 0.5.9) prints the running service's version. `wfn:` (whole-name match) is new in 0.5.9. | Expected version is 0.5.9. TinyTorrent runs `--server-version` only after a call has failed, to name the cause. | `docs/WHATS-NEW.md`, `docs/JSON.md:122` |
| `-timeout` waits for volumes to be ready; it is not an execution deadline. | TinyTorrent sets its own deadline. | `docs/JSON.md:129` |
| `-n all` holds the whole answer in memory before writing it. | Ask once per refresh; the answer is held briefly in `gozler.exe`. | `docs/JSON.md:23` |
| Search terms match in any order, as substrings. | A person's typed search never goes to gozler. | `docs/JSON.md:24` |
| The installer is `gozler-<version>-x64-Setup.exe` on the GitHub release. It needs administrator permission, installs to `Program Files\Gozler`, and starts the service. Releases are not signed and publish no hash files; gozler's own window checks a download against the `digest` that GitHub gives the asset. | Pin the version and its SHA-256 when TinyTorrent is built. | `installer/Setup.iss:15,41,53`, `crates/gozler-daemon/src/service.rs:78`, `.github/workflows/release.yml`, `docs/DESIGN.md:913,948` |
| A silent gozler setup grants nobody the "see every file" right. | Run gozler's setup with its own wizard. | `installer/Setup.iss:342-348` (gozler), `docs/DESIGN.md:198` |

## Owners

| Owner | Responsibility and reason |
| --- | --- |
| gozler | Indexes the drives, answers queries. It is not TinyTorrent code. |
| App gozler runner (C#) | Finds `gozler.exe`, runs it for Library and for the Settings state, applies the [rules for running it](#running-gozlerexe). Library's answer can hold tens of thousands of locations, and the app reads it straight into SQLite staging. Routing it through the engine would need a paged reply, a new DTO and a second transfer of the same data. |
| Engine gozler runner (C++) | The same rules, for locating moved torrent files. It runs when the window is closed. |
| Install action (C#) | The one download, check and run of gozler's setup, for the in-app prompt. `Setup.iss` has no part in it. |
| Existing Library, file-facts and subtitle owners | Identify media, read contents as the person, associate subtitles, search, present. Each rule keeps one owner. |
| Existing database owner | Migrations, locations, transactions, cleanup. |
| Existing engine | Recovery hold, check-only gate, adoption of moved files. Stays unelevated. |

## The two options

Two independent options start **off**: **Discover media for Library** and
**Track moved torrent files**. Settings shows both, with the gozler state
underneath (see below). Each option says what it needs: gozler, and the memory
gozler uses. The first Library visit may offer them once, with both choices
explicit; remember a declined offer. Describe tracking as "automatically
reusing matching files found elsewhere when their old location is missing", so
consent covers reuse, not an unknowable move history. **Moved-file checking**
offers Quick (default) and Full, in the same setup surface.

With both off, TinyTorrent never runs `gozler.exe`.

There is no volume or folder setting in TinyTorrent. gozler's own Settings
decide which volumes and folders it indexes; a second scope in TinyTorrent would
be a second owner of the same choice. TinyTorrent never changes gozler's
settings.

## Running gozler.exe

One contract for both runners:

- **Find it** in the folder of the program that the Windows service `gozler`
  runs, not through `PATH` and not from the current folder. Reason: a folder
  that anyone can write to must not decide which program TinyTorrent runs. This
  also finds gozler right after installation, because a running TinyTorrent
  does not see the `PATH` entry the installer added. The implementer confirms
  that `gozler.exe` sits beside `gozlerd.exe` (both go to one folder in
  gozler's `Setup.iss`, and gozler's README says to keep them together).
- **Run it directly**, never through a shell. Pass names and paths as separate
  arguments. Read stdout as UTF-8 JSON and stderr as text.
- **Deadline:** 30 seconds per call, then end the process. A call normally takes
  milliseconds. The cause shown is "gozler did not answer in 30 seconds".
- **Never on the UI thread, never in pipe dispatch.** Library calls run in the
  app's background work and show in the existing work presentation. The engine's
  calls run in its operation worker. The window never waits for gozler: Library
  shows its saved catalogue at once.
- **A timeout or failure is not retried in a loop.** The next try comes from a
  trigger the person controls (see [Library](#library)) or from Retry.
- **Exit 7:** retry after 5 seconds, at most 3 times, then report it. Reason:
  `docs/DESIGN.md:96` says the pipe may open soon. Exit 8 is never retried.
- **One call per need.** A Library refresh is exactly one call (the media
  query). A locating job is exactly one call. No routine extra call runs before
  or after: no `--status`, and `--server-version` only after a failure. The query's own exit code and
  `volumes_incomplete` say whether the answer is complete.
- **Use what came back.** An answer with exit 2 is used and labelled
  incomplete; it is never turned into an error.

## What the person sees

One default for each case. Settings shows the state under the two options. A
Library or engine message names the same cause in the same words.

| Case | How we know | What the person sees | Default action |
| --- | --- | --- | --- |
| gozler not installed | The Windows service `gozler` does not exist, or the folder of its program has no `gozler.exe`. | "gozler is not installed." Switching an option on opens the install prompt. Settings shows an Install button. | The prompt. |
| Installed, service stopped or disabled | Exit 8. | "gozler's service is not running. Start it in gozler, then Retry." | TinyTorrent does not start or repair the person's service. Reason: decision 3. gozler's window offers its own repair. |
| Service starting | Exit 7, still 7 after 3 tries. | "gozler's service did not answer." | Retry button. |
| Still building its index | Exit 2. | Library shows the files found so far and says the list is incomplete. Torrent tracking does not adopt from an incomplete answer; the row says so and offers Retry. | Library refreshes at its next trigger. |
| Older than expected | Only after a call failed (exit 1, 4 or 6): `--server-version` reports less than 0.5.9, or exits 6. | "gozler 0.4.0 is older than TinyTorrent needs (0.5.9). Update it in gozler." | No install offer: that would replace the person's gozler (decision 3). gozler has Help > Check for updates. |
| Newer than expected | Not checked: a call that works needs no version check. | Nothing. | Use it. Reason: fields are only added (`docs/JSON.md:92`). |
| gozler refused, exit 1, 3, 4, 5 or 6 | stderr; then one `--server-version` to tell "older" from any other failure. | "gozler could not answer." with stderr in the log. | Retry button. |

The expected version is one number, kept beside the pinned download record
(below). Raise it only when TinyTorrent starts to use something newer.

## Installing gozler

### The prompt

The person switches on an option while gozler is not installed. The dialog
follows `app/AGENTS.md` "Dialogs" and the [button rulings](../contracts/ui.md#buttons):
one `Shared.Dialog` that `MainWindow` opens through `Interact`, and `ShowEditor`
with a draft, so it stays open while the work is pending or has failed.

- Title: "Install gozler?"
- Body: what gozler does for the feature, that it runs as a Windows service,
  that it uses memory (the owner's figure), and that Windows asks for
  administrator permission.
- Footer: the download size and source (GitHub).
- Buttons: Install (default) and Cancel.

| What happens | What the person sees and gets |
| --- | --- |
| Person chooses Cancel | The dialog closes. The option stays **off**. Nothing is downloaded. Switching it on later asks again. |
| Download fails | The dialog stays open and shows the cause. The button reads Retry. The option stays off. |
| Check fails (the SHA-256 differs) | The file is deleted. The dialog says the download is damaged and offers Retry. |
| Person declines the Windows permission prompt | Windows reports error 1223 when the person cancels. The dialog says "Windows did not give permission. gozler is not installed." with Retry. |
| gozler's own setup is cancelled or fails | The dialog decides from the result, not from the exit code: when setup has exited and the Windows service `gozler` does not exist, it says "gozler is not installed." with Retry. It runs no gozler call. |
| Install succeeds | The dialog closes, the option switches **on** and starts at once. No restart is needed: TinyTorrent finds `gozler.exe` through the service (above), and gozler's setup starts the service. The first answer can be exit 2 while the index builds; Library shows what it finds and says the list is incomplete. |

Cancel works until gozler's setup starts; it stops the download. After that, the
setup cannot be stopped from TinyTorrent, as with TinyTorrent's own update
(`app/src/Shell/Updates.cs`). Closing the window then leaves the setup running;
the option stays off, and the next switch-on finds gozler installed and needs no
prompt.

gozler's setup runs with its own wizard, not silently. Reason: a silent setup
grants nobody the "see every file" right, and that choice decides which folders
Library can see. The person answers it on gozler's page. TinyTorrent starts the
setup as a double-click does, so Windows shows the permission prompt for
gozler's manifest (gozler's own window does the same; `docs/DESIGN.md:950`).

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
- The app starts the setup with its wizard and decides success from gozler's
  answer.

The check is weaker than for the Microsoft runtimes, which are verified by
publisher signature; gozler releases are unsigned. It is acceptable because the
owner publishes both projects and pins the hash when TinyTorrent is built.

### Uninstalling TinyTorrent

TinyTorrent's uninstaller leaves gozler installed. Reason: it is a separate
program with its own entry in Installed apps and its own uninstaller, the person
may use it directly, and TinyTorrent's own Setup treats the Microsoft runtimes
the same way (it installs them and never removes them). TinyTorrent does not
record who installed gozler.

## Library

### Asking gozler

- **What:** one call per refresh:
  `gozler.exe --json -n all -attributes ext:mkv;mp4;srt;...`, a semicolon list
  of every video, audio and subtitle extension (`docs/DESIGN.md:117`). The
  sets come from `FileName` and `SubtitleFile`; do not add another list. The app
  classifies each result by its extension through those owners. One call, not
  one per extension: about 40 process starts cost more than one answer of a few
  MB that `gozler.exe` holds briefly (`docs/JSON.md:23`).
- **When:** when Library opens, on the person's Refresh, and every 5 minutes
  while Library is on screen and its window is active. Never while Library is
  hidden. Reason: gozler has no change notification, and each query warms its
  memory. After a failure, the timer stops until the person refreshes or Library
  opens again.
- **Saved catalogue first:** Library presents its saved catalogue at once. A
  refresh never replaces it with a preparing page.
- **Persist:** a location is a path with `size` and `mtime_ft`. A changed size
  or modified time invalidates saved file facts. A same-size edit that keeps its
  modified time is not detected; accept this.

### Presence

gozler says what exists. It does not say what is gone: a file can be missing
from an answer because the answer is incomplete, the folder is hidden from the
person, the drive is not indexed, or gozler's Include settings leave it out.

Rule: **a location is known absent only when the person's own check says so.**
After a refresh, for each saved location that gozler's answer does not contain,
whether or not gozler indexes its drive, check the path as the ordinary user.
That check alone decides absent or unknown. Absent means the volume answers and
the file is not there. Anything else (drive offline, access denied, failed check)
is unknown and changes nothing. This is the existing File presence rule, and its
owner keeps one implementation for every source. Recording absence in an
incomplete answer (exit 2 or `volumes_incomplete`) is not allowed; adding and updating is.

Other rules, each from an existing owner:

| Situation | Library behavior |
| --- | --- |
| Confirmed deletion of the only location | Remove it from Files, video and music results and from search, even if a torrent still lists it. Keep the torrent. |
| Windows Recycle Bin | Locations in the volume's `$Recycle.Bin` are ineligible. Restoring makes the outside locations eligible again and keeps valid choices. |
| One hard link disappears | Remove that location; keep the others. |
| File moves | If exactly one location is confirmed absent and exactly one new location has the same name and size in the same cycle, keep the person's choices on the new location. Otherwise it is a new file and a gone file. Reason: gozler gives no file ID. |
| Drive not indexed or not answering | Keep saved data. Exclude its locations while the drive is unavailable. |
| Subtitle disappears | Remove its location; update associations through the existing owner. |
| Cloud placeholder | Automatic readers skip files whose attributes show recall or offline. The person's Open follows Windows. |
| Unfinished torrent file | TinyTorrent's unfinished suffix does not match. A path that a current torrent reports unfinished stays ineligible. A hard link with another name to such a file is not detected. |

Library applies one finished cycle at a time. Stage the answers in SQLite and
accept them in one transaction through the existing reconciliation owner, so a
replacement file at an old path is not mistaken for a late deletion. Interrupted
staging is discarded without deleting choices, locations or subtitle
associations. After each accepted cycle, refresh the active Library query
through its existing owner. Persistence is a preserving migration through the
single `Database` owner; the location owner's case-insensitive path uniqueness
migrates so that case-distinct names survive.

Removing a torrent does not remove gozler's contribution to files that stay on
disk. Torrent and gozler contributions are joined only when they name the same
path.

### Candidates

Whole-drive discovery finds application videos and sounds too. Keep the
automatic video-information setting and per-movie search protection for every
eligible video. Filename search works before enrichment. The provider and
file-facts owners must read bounded pending batches from SQLite instead of the
whole pending set. The suffix `.ts` is shared by video and TypeScript: keep it
searchable by name, and admit it to automatic identification only when the
existing ordinary-user file-facts reader gives positive video evidence.

### Search

A person's typed search runs on the saved catalogue in SQLite under the
repository's word-start rule (root `AGENTS.md`): "50 para" finds "Paracetamol
500mg". It is never sent to gozler, whose matching differs (substring, any
order). The queries TinyTorrent sends to gozler contain no person-typed text.

### Subtitles

Subtitles are Files candidates even when unassociated. For a disk video, the
matching scope is the video's folder and its `Subs`/`Subtitles` folders, never a
whole drive. Found subtitles keep `created=false`: discovery grants no right to
move or delete them. A move or removal updates associations through the existing
owner. No network request, no parsing in gozler, no enrolment of disk videos in
torrent subtitle jobs.

## Missing torrent files

gozler does not decide this section. The engine owns it, and it works with
tracking off.

The engine has a general `Error` status and transient disk errors, but no
durable missing-payload hold. `SetIntent` clears errors and `ApplyIntent` can
resume transfers. Add typed `FilesMissing` and `FilesUnverified` reasons and one
saved hold, independent of Paused, Resumed and Forced intent.

`FilesMissing`: previously present, wanted payload is now definitely absent.
`FilesUnverified`: recovery could not establish earlier payload state, for
example a legacy installation with unreadable resume data. Neither is called
deletion. Both block payload transfers until recovery resolves them. Tracking
authorizes reuse of existing files, never repair downloads. An offline drive,
denied access or failed probe is an availability outcome, never missing-file
evidence; transfers stay blocked while the files cannot be checked.

### Evidence and detection

- Persist compact groups of expected local file indices with the torrent's
  facts: files observed on disk or settled complete, never every entry of a new
  manifest. Bind each group to the volume (volume GUID and serial) the files
  were resolved on. Keep this independent of libtorrent's piece map, so a
  recheck cannot erase the only record that a file existed. Commit expectations
  before advertising completion. Migrate older torrents from readable resume
  data; if evidence for local payload is unavailable, use the unverified
  hold and allow metadata retrieval with zero payload priorities. A legacy field
  without a volume binding cannot infer its volume from today's drive letter.
  An absent legacy field means unknown history; a saved empty list means no
  tracked files have been recorded yet. New additions save the empty list.
- Check known paths on the existing payload worker: at restore, before
  releasing transfers, on native file errors, and in a 60-second round-robin
  sweep while the engine runs, with the window closed and tracking off. Measure
  the cost and the real coverage interval. This checks known torrent paths; it
  enumerates no folder.
- Absence is definite only when the path resolves onto the saved volume. An
  offline volume, reused drive letter or replaced mount stays unavailable,
  never `FilesMissing`, even if another volume now has that letter. An existing
  binding stays even if the replacement uses another file system; only a
  settled location change leaves it. The hold and these checks cover payload on
  any local volume, because a metadata check as the ordinary user and a volume
  GUID with its serial exist on every local file system. Destinations on network
  shares keep their current behavior: a share has no volume identity, and an
  unreachable share cannot be told from a deleted file.
- A file created and deleted before any evidence was kept cannot be told from a
  file never created. Do not promise otherwise.

### The check-only transfer gate

Before startup checking or `Verify`, install a gate: libtorrent can treat
absent files as missing pieces without a disk error, and `force_recheck`
normally allows downloading afterwards. Use the `stop_when_ready` flag in
restore parameters when metadata is available. Without metadata, restore with
zero payload priorities and `default_dont_download`, and install the gate when
metadata arrives, before wanted priorities apply. For a live torrent, hold
`ApplyIntent`, pause, disable auto-management, transition to checking, arm the
flag, then allow checking, in one libtorrent-thread callback. Arming while
transferring consumes the flag at once; leaving the torrent paused prevents
checking. Confirm this order against the pinned libtorrent before coding it.

The engine reads the check result before it releases the gate. Every path that
can start transfers (naming completion, automatic recovery, scheduler, Resume,
Force, Resume all) respects the same hold. On confirmed loss, stop transfers and
persist the hold before acknowledging; a failed save keeps the live hold and
reports the failure. Ordinary presence checks never dismiss a hold.

Detection is asynchronous. The enforceable rule is: **no automatic network
repair once loss is established**, including through recheck and restart. An
already issued write can finish. Do not claim that polling stops every write.

### Engine commands

These extend the existing engine endpoint. DTO properties are snake_case.

| Operation | Command | Input | Successful output |
| --- | --- | --- | --- |
| `ReportMissing(MissingFiles)` | `report_missing_files` | `MissingFiles` | Empty object after a durable hold is accepted. Stale reports are harmless. |
| `FindFiles(Recovery)` | `find_files` | `Recovery` | Empty object after a locating job for the current hold is scheduled or joined. |
| `DownloadFiles(Recovery)` | `download_files` | `Recovery` | Empty object after authorization commits. |

```text
Engine settings additions
  discover_media: bool = false
  track_moves: bool = false
  move_check: quick | full = quick

MissingFiles   { torrent_id: string, files: MissingFile[] }
MissingFile    { index: int32, path: string }
Recovery       { torrent_id: string, hold_id: string }
PayloadHold =
  { kind: missing, hold_id: string, files: int32[] }
  { kind: unverified, hold_id: string }
```

The settings use the existing settings path. `torrent_id` is the durable
torrent addition ID. `MissingFiles.files` has 1-1,024 entries; split larger
reports. `path` is a guard, not permission to inspect a location: the engine
resolves its own mapping and establishes absence on its payload worker. A hold
is also a typed field on the existing row and detail DTOs. Reuse the existing
error envelope: `invalid_request`, `unavailable`/`files_busy`,
`storage_failed`/`overloaded`, and `recovery_required` for a stale choice. Never
replay an uncertain `download_files`. The hold ID is opaque, saved, and changes
for each new episode or larger affected set.

`FindFiles` is the explicit Retry for a held torrent. It needs tracking on,
rejects a stale hold ID, and joins an existing job. It never waits for gozler or
hashing. It changes no download authorization.

### Recovery choices

The row and detail completion fields must stop advertising confirmed missing
payload as complete, even when libtorrent's cached progress says 100 percent.
Show "Download missing files" for a missing hold and "Download files" for an
unverified hold. The command keeps the wanted and skipped selection, checks the
current hold, and commits explicit repair authorization and resumed intent
before payload transfers. It reconciles libtorrent's piece ownership through the
same check-only operation first: clearing the error and resuming a cached
100-percent torrent is not a repair. For a missing hold it clears expectations
only for the files authorized in that episode. If more loss appears, keep the
hold with a new ID and ask again. Individual Resume and Force on a held torrent
return `recovery_required`; Resume all skips held torrents. Pause stays allowed.
Remove stays allowed and requests no download. If the person restores all
selected files and `Verify` succeeds, clearing the hold also commits Stopped
(Paused): verifying starts neither downloading nor seeding. An unverified hold
stays until the whole uncertainty is resolved.

### Following files moved outside TinyTorrent

**Adoption** means: update the torrent's location to files already there. It
never copies, overwrites or moves files. The engine's payload-operation owner
decides; Library never changes torrent paths.

The automatic case is one coherent, complete wanted-payload layout at one new
root with no unresolved partfile dependency. Conflicting roots, scattered or
renamed files and incomplete copies use the existing explicit location
recovery.

When **Track moved torrent files** is off, no search and no adoption run;
ordinary missing-file protection still applies. When on, a new durable missing
episode schedules one locating job. It may also try held torrents once when
tracking is switched on, or after Retry or a location choice. A repeated sweep
over an unchanged hold, a restart, or an unchanged Library report starts no
job. A newer episode cancels stale work; switching tracking off cancels it.

The job, in this order:

1. **Known folder.** Save the content directory's `RootIdentity` (volume and file
   ID) when observed, on a volume whose file system gives stable file IDs (NTFS
   and ReFS). On another file system, skip this step; `OpenFileById` needs
   those IDs. At job start, resolve it with `OpenFileById` as the
   ordinary user and get its current path. This finds a same-volume folder move
   without gozler. A surviving hard link alone does not choose a destination.
2. **Ask gozler, in one call.** Take the largest missing wanted file (name and
   size) and run `gozler.exe --json -n 100 "wfn:<name>" "size:<bytes>"`. If one
   file is not enough to find the root (for example, the largest file may be the
   one the person deleted), put several missing files into the same call with
   gozler's grouping, `<wfn:a size:1>|<wfn:b size:2>` (`docs/JSON.md:36`), up to
   a small fixed number. Never one call per file. Build the arguments as
   separate arguments with the quoting rules in `gozler --help`; test names with
   spaces and with `!`, `;`, `&`, `'` and `%`. A name has no wildcard characters
   on Windows. Use only an answer with exit 0 and `more` false. An incomplete
   answer (exit 2) or too many candidates leaves the hold and says so, with
   Retry. Reason: an incomplete answer cannot show that a root is the only one.
3. **Derive roots.** For each candidate, remove the file's relative path inside
   the torrent to get a possible root. Reject Recycle Bin and recall/reparse
   locations.
4. **Validate** every required known path under a root as the ordinary user, with
   sizes and local-content availability, using known paths without enumerating
   folders. A matching movie alone does not show that the other files moved.
   Several complete roots need the person's choice. Recheck identity, layout and
   current hold and mapping before settling.
5. **Settle** under the checking mode below, or leave the hold and offer Retry or
   the existing explicit location recovery. No scan loop.

If the person moved the files into a folder that they cannot list, or onto a
removable drive, gozler cannot find them; the hold stays and the row offers the
explicit location recovery.

**Moved-file checking** is captured per adoption. Quick checks up to three
distinct complete pieces near the beginning, middle and end of every nonempty
wanted file, deduplicating shared pieces, with the torrent's existing hashes. A
passing sample supports trusting the other piece claims; it does not mean every
byte was verified. Full checks all required payload. Both need a unique
coherent layout and all backing storage, and neither allows missing files,
ambiguous ownership or automatic redownload after a mismatch.

Quick also needs the live torrent's complete wanted-piece ownership to be
intact. A missing-file recheck can already have cleared it, and
`reset_save_path_unchecked` does not restore it. If that is lost, keep the hold
and offer Full. Do not resume with an incomplete piece map, run Full silently,
store piece history, or remove and re-add the torrent in seed mode.

Sample through the existing payload and native-handle seam, with one outstanding
libtorrent `disk_interface::async_hash`. A v1 result hashes the whole piece,
including adjacent files and padding. For v2, combine SHA-256 block hashes with
libtorrent's Merkle primitives and compare with an authenticated piece-layer
hash or the file root, respecting short final pieces and zero-leaf padding. If
authenticated hashes, required bytes or support are missing, Quick is
unresolved and offers Full. Do not call piece-completion callbacks such as
`on_blocks_hashed`: they can change piece ownership. See
[disk hashing](../../3rdParty/libtorrent/include/libtorrent/disk_interface.hpp),
[Merkle primitives](../../3rdParty/libtorrent/include/libtorrent/aux_/merkle.hpp)
and [BEP 52](https://www.bittorrent.org/beps/bep_0052.html).

Reuse the existing move owner with an explicit adoption operation. libtorrent
supplies `reset_save_path` for Full and `reset_save_path_unchecked` for Quick's
gated reset. `SaveMove` always sets `verifyFiles=true`, so its finalization must
honor the adoption's captured requirement. Adoption never enters `dont_replace`,
never moves files and never deletes placeholders. Save the purpose, destination
and captured requirement before acknowledging, so a restart cannot fall through
to a physical move. Run hashing as operation work, never inside a pipe reply's
deadline. Keep the prior location until adoption succeeds; a temporary path
reset for checking is not the commit. If checking fails or is cancelled, restore
the prior mapping under the gate. The existing move finalizer commits the
destination before verification; adoption cannot reuse that order unchanged.

Keep the person's intent: a Stopped torrent stays Stopped. After validation
accounts for all missing wanted payload and confirms complete piece ownership,
settle the new location and resolve only the matching hold in one ordered
commit. An enlarged or replaced hold, unverified history or another blocking
condition is not cleared by a stale adoption. Failure keeps the protection and
grants no repair permission. An explicit `Verify` of restored files settles to
Stopped, as above.

### One deletion path across Library and engine

After Library accepts a cycle with known absence, it reports each affected live
torrent's own path to the engine (`ReportMissing`). The engine's hold is durable
before it acknowledges. Library's publication does not wait for that
acknowledgement, and a failed report never hides unrelated files. Known absence
persists while a torrent contribution exists, so reports are derived again on
reconciliation and on engine reconnect, with the existing bounded retry; replays
are harmless. A hard-link deletion affects only torrents whose own paths
disappeared. App-owned deletion uses the same hold. The engine's own sweep
covers an interruption before delivery, so the UI is not the only protection
against a redownload.

This refines the engine contract's Resume-clears-disk-error rule: ordinary
errors keep their recovery; these two durable reasons need their named choices.

## Open decisions

1. **Removable drives.** Default: accept the limit. gozler indexes fixed drives
   only, and its index and security model are built on that. Files on removable
   drives are not discovered, and a moved torrent file on one is not found. The
   alternative is to extend gozler to index removable drives, which is work in
   the gozler project: its index is rebuilt at every start and its volume set
   follows the machine within a minute (`docs/DESIGN.md:19,23`). Default: accept.

## Implementation order and evidence

1. **The runner contract.** One runner, then the second. Check: exit-code
   mapping, the 30-second deadline, a name with special characters, `PATH`
   not being used, and exactly one call per refresh and per locating job. Open question: does `gozler.exe` run from the service's
   folder when `PATH` does not contain it?
2. **Installing.** Build record and prompt. Check each outcome
   row once, including the declined permission prompt and a damaged file. Open
   question: does the feature work at once after install, without restarting
   TinyTorrent?
3. **Engine hold and gate** (independent of gozler). Known deletion, new and
   uncreated files, skipped and reselected files, missing resume data, stale
   reports and IDs, failed saves, shared files, a replaced volume or reused
   drive letter. Restore, Verify, Force, Resume all, scheduling and naming
   completion must not authorize repair. Check detection with the window closed,
   tracking off, and payload outside the media filter; check restart with a
   saved hold. Validate the libtorrent flag order at the transfer seam.
4. **Adoption.** Same-volume folder move, another-volume move, Quick and Full,
   mismatch, duplicate candidates, recycled files, an incomplete gozler answer,
   partial and partfile-backed payload. Quick cannot release transfers after a
   check cleared complete ownership. Tracking off performs no search; repeated
   absence and restarts never start a search loop.
5. **Library source.** Atomic acceptance, abandoned staging, the presence rule
   (hidden folder, unindexed drive, incomplete answer), the move rule, subtitle
   scope, and unfinished-torrent exclusion. A torrent-only ghost disappears
   after the first complete cycle; saved browsing stays available during a
   refresh. Provider and file-facts batches stay bounded.
6. **Settings, localisation and contracts.** Both options, the state line, the
   prompt text in every shipped language catalogue. Update the architecture,
   Library, engine, database, pipe API and localisation contracts.
7. **Measure** saved browsing separately from refresh-to-current-results, deletion
   to row removal, `gozler.exe` call times and process memory while it runs,
   Library input latency during a refresh, and the engine's known-path sweep.
   Do not accept a refresh that stalls the window.

Use the [testing](../agents/testing.md) and [compilation](../agents/compilation.md)
policies. This design task needs no build and no run of gozler.
