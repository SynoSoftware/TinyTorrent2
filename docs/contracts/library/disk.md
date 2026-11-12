# Disk discovery

Part of the [Library contract](../library.md). Read its shared ownership,
membership and SQLite rules before the sections here that the change affects.

Disk discovery is Library's second data source. It finds the video, music and
subtitle files on the person's fixed drives, and keeps their locations current.
It learns them from gozler, the person's separate file-search program, and never
by listing folders. Library opens as fast with them as without them. The
feature stays off until the person switches it on.

**Owner ruling (11 November 2026):** the feature "must be flawless from the
user point of view", Library is "not to be slow to start. ever", "we must not
recurse folders", and "gozler is ours. if it doesn't have something we can add
it". Three rules follow, and every part below obeys them:

1. **Saved catalogue first.** Opening Library shows what SQLite holds. Nothing
   between opening Library and its first rows waits for gozler.
2. **No folder is listed.** The only file-system call that discovery makes is
   a check that one known path exists, for a path that gozler stopped
   reporting. The subtitle files beside a disk movie come from the saved
   catalogue, not from a listing of its folder.
3. **Work follows the change.** A refresh that finds nothing changed writes
   nothing, and one that finds five changed files writes five.

## gozler

gozler is the owner's own project (MIT licence, source in `3rdParty/goz`). It
indexes the fixed NTFS and ReFS drives and answers a query in milliseconds.
TinyTorrent does not index files itself, because the owner does not want a
second file index.

- **TinyTorrent talks to gozler only by running `gozler.exe --json`.** It does
  not use gozler's named pipe, because the pipe's protocol version must match
  exactly, so a pipe client breaks with an older or newer gozler.
- **TinyTorrent's installer does not bundle gozler and does not offer it.** The
  in-app prompt below is the only way TinyTorrent installs it. gozler is a
  separate program with its own installer, updates and uninstaller, and the
  feature starts off.
- **TinyTorrent never overwrites or replaces an installed gozler,** and never
  starts or repairs its service, because it is the person's installation.
- **Only one gozler service runs on the machine:** the person's own, or the one
  TinyTorrent installed. gozler keeps its index in memory (381 MB private and
  200 MB working set with 2.37 million entries on the owner's PC, November
  2026), which works against TinyTorrent's promise to be light. For the same
  reason the feature is opt-in.
- **gozler gains what TinyTorrent needs from it.** A fact that only gozler has
  is added to gozler's answer, not guessed in TinyTorrent. With `--file-id`
  (gozler 0.5.10), each result names its file by `volume`, the volume's GUID
  path as `--status` reports it, and `file_id`, 32 hexadecimal digits that
  every hard link of the file shares. So a file renamed or moved on its volume
  stays the same Library entry and keeps the matches the person corrected.

The gozler facts this feature relies on:

| Fact | Consequence |
| --- | --- |
| `--json` output is for scripts; later releases add fields. | Library reads the fields it needs and ignores the rest. |
| The Windows service `gozler` runs `gozlerd.exe`; setup puts `gozler.exe` beside it. | Library finds `gozler.exe` through the service. A stopped service is the person's state. |
| It indexes fixed NTFS and ReFS volumes only. | Files on removable drives, network shares and FAT or exFAT volumes are never found. Settings says "fixed drives". |
| A result's `size` and dates can be missing, which means unknown, not zero. | An unknown size or date keeps the saved value. |
| It has no change notification. | Library asks again [when it needs to know](#when-library-asks). |
| Each hard link is a separate result. | Each location is its own Library entry. |
| A user sees only the entries in folders that user may list, and such an answer still counts as complete. | A file missing from a complete answer may still exist; only [the person's own check](#file-presence) says it is gone. |
| Its Include settings are one setting for the whole PC, changed only by an administrator. | TinyTorrent never changes them; [the query](#the-query) leaves out what Library does not want. |
| A term ends at a space; a path with a space needs quotes inside its term. Without them the query is valid, exits 0 and matches nothing. | The query builder quotes every path. |
| One query holds at most 256 terms, counting each `ext:` value and `-a-d`; an OR group is distributed first. | [Hidden folders](#hidden-folders) have a limit: 211 beside the fixed terms. |
| `-timeout` waits for volumes; it is not a deadline. | Library sets its own deadline. |

## The option

**Discover media for Library** starts **off**. With it off, Library never runs
`gozler.exe`. It is saved in `library_settings`, beside Library's other
options, because the engine has no use for it.

- Settings shows the option, what it needs (gozler, and the memory gozler
  uses), that it covers fixed drives, and the gozler state under it. Below that
  it lists the hidden folders, each with Remove.
- Library's empty state, "No files yet", has a button that switches the option
  on, so a person without torrents finds it where they look for their files. No
  other prompt offers it, because a prompt the person did not ask for needs a
  remembered answer and its own design.
- Switching the option off withdraws the disk contribution, as removing a
  torrent withdraws its contribution. Files that only gozler reported leave
  Library with their stored facts; files that a torrent also lists stay, and
  the torrents alone decide them again. The option's description says so.

## Running gozler.exe

- **Find it** in the folder of the program that the Windows service `gozler`
  runs (the service's `ImagePath`), never through `PATH` or the current folder,
  so a folder that anyone can write to never decides which program runs. This
  also finds gozler right after installation, before a running TinyTorrent sees
  a new `PATH`.
- **Run it directly**, never through a shell, with each term as its own
  argument. Read stdout as UTF-8 JSON and stderr as text.
- **Deadline:** 30 seconds per call, then the process ends. A call normally
  takes well under a second.
- **Never on the UI thread.** A refresh runs in the app's background work and
  shows in the work list.
- **A failure is not retried in a loop.** The next try comes from a trigger the
  person controls: Refresh, Retry, or showing Library again. Exit 7 is the one
  exception: it is tried again after 5 seconds, 3 tries in all, because the
  service's pipe may open soon. Exit 8 is never tried again.
- **One call per refresh.** A second call follows only an answer that needs it:
  `--status` after an incomplete answer, and `--server-version` after a failed
  call, to tell "older than needed" from other failures. A gozler that works is
  never asked its version, and a newer gozler is used as it is.
- **Use what came back.** An incomplete answer (exit 2, or `volumes_incomplete`)
  is used and labelled incomplete, never turned into an error.

## What the person sees

Settings shows the state under the option, and Library names the same cause in
the same words.

| Case | How Library knows | What the person sees | What happens next |
| --- | --- | --- | --- |
| gozler not installed | The Windows service `gozler` does not exist. | "gozler is not installed." Settings shows Install; switching the option on opens the install prompt. | The prompt. |
| `gozler.exe` missing | The service exists, its program's folder has no `gozler.exe`. | gozler's command-line program is missing; repair gozler. | No install offer, which would replace the person's gozler. |
| Service stopped or disabled | Exit 8. | gozler's service is not running; start it in gozler, then Retry. | TinyTorrent does not start or repair it. |
| Service did not answer | Exit 7, three times. | gozler's service did not answer. | Retry. |
| No answer in time | The 30-second deadline. | gozler did not answer in 30 seconds. | Retry. |
| A drive is being indexed | Exit 2, and `--status` shows a volume being built or scanned again. | Library shows the files found so far and names the drive. | Asked again 10 seconds after each such answer while Library is shown, until the volume is ready or Library is hidden. |
| gozler cannot keep a drive current | Exit 2, and `--status` shows a volume whose live updates stopped or whose index failed. | Library's list for that drive may be out of date, and gozler's window says why. | No automatic retry, because the cause does not end by itself. |
| Older than needed | After a failed call, `--server-version` reports less than the needed version, or exits 6. | The installed and the needed version, and that gozler updates itself. | No install offer. |
| Other failure | Exit 1, 3, 4, 5 or 6. | gozler could not answer; stderr goes to the debug log. | Retry. |

The needed version is `Gozler.ExpectedVersion`, the one owner that the install
record also reads. It rises only when TinyTorrent starts to use something
newer.

## Installing gozler

The person switches on the option while gozler is not installed. The dialog is
one `Shared.Dialog` that `MainWindow` opens through `Interact`, and
`ShowEditor` with a draft, so it stays open while the work is pending or has
failed. It says what gozler does for the feature, that it runs as a Windows
service, that it uses memory, and that Windows asks for administrator
permission. Its footer gives the download size and source (GitHub). Its
buttons are Install (default) and Cancel.

| What happens | What the person sees and gets |
| --- | --- |
| Cancel | The dialog closes. The option stays off. Nothing is downloaded. |
| Download fails | The dialog stays open and shows the cause; the button reads Retry. The option stays off. |
| The SHA-256 differs | The file is deleted. The dialog says the download is damaged and offers Retry. |
| The person declines the Windows permission prompt | When Windows reports the refusal to TinyTorrent (error 1223, for a setup that asks for permission as it starts), the dialog says Windows did not give permission and gozler is not installed, with Retry. gozler's Inno Setup can instead ask from its own process; it then exits without installing, and the next row applies. |
| gozler's setup is cancelled or fails | Decided from the result, not the exit code: setup has exited and the service `gozler` does not exist, so gozler is not installed, with Retry. |
| Install succeeds | The dialog closes, the option switches on and the first refresh starts. No restart is needed. The first answers can be incomplete while the index builds. |

Cancel works until gozler's setup starts. gozler's setup runs with its own
wizard, never silently, because a silent setup grants nobody gozler's "see
every file" right, and that choice decides which folders Library can see.
TinyTorrent starts it through the Windows shell, as a double-click does, and
Windows shows its permission prompt, whether the setup asks as it starts or
from its own process.

`installer/Build.ps1` writes one download record into the published folder:
gozler's version, the URL of `gozler-<version>-x64-Setup.exe` on its GitHub
release, its SHA-256 and its size. It downloads the asset and compares its hash
with the `digest` GitHub gives the asset. The app downloads over HTTPS,
compares the SHA-256 with the record and deletes a file that differs; a file
with no record is never run. gozler releases are unsigned, so this check is
weaker than a publisher signature; it is acceptable because the owner publishes
both projects and pins the hash when TinyTorrent is built.

TinyTorrent's uninstaller leaves gozler installed, because it is a separate
program with its own entry in Installed apps, and TinyTorrent does not record
who installed it.

## The query

One call per refresh:

```text
gozler.exe --json -n all -a-d -attributes --file-id
  ext:VIDEO;AUDIO;SUBTITLE  !ext:ts;mts|size:>20mb
  !"<Windows>\*"  !"<Program Files>\*"  !"<Program Files (x86)>\*"
  !"<ProgramData>\*"  !"<profiles folder>\*\AppData\*"  !"*\$Recycle.Bin\*"
  !"<each hidden folder>\*"
```

- **The extension sets come from their owners:** video and audio from
  `FileName`, subtitles from `SubtitleFile`. The file kind of a saved file and
  the query read the same definition.
- **`.ts` and `.mts` count as video only from 20 MB.** They are also
  TypeScript extensions: on the owner's PC, 101,199 such files, none reaching
  20 MB. A transport-stream video below 20 MB is one segment of a film. The
  rule is a clause of its own, because inside an OR group gozler distributes
  the group and counts every extension twice (45 terms instead of 76 for the
  fixed part of the query, measured on gozler 0.5.9).
- **Windows' and programs' own folders are left out,** from Windows' known
  folders, because programs keep their sounds and clips there and a person
  does not keep a collection there. The Recycle Bin is left out, so a recycled
  file is a file that is gone.
- **No text the person types** is in the query. A person's search runs on the
  saved catalogue under the product's word-start rule and never goes to gozler.

A file whose attributes show a cloud placeholder (offline, recall on open or
recall on data access) is listed, but saved as not on the disk, so no automatic
reader opens it and downloads it. The person's Open follows Windows.

## Hidden folders

Games and programs keep media outside the folders the query leaves out: on the
owner's PC, 1,501 of 3,022 audio files are test files of code libraries. So the
person can remove a folder's files from Library with one command.

- **Hide folder from Library** is a Library command on a file that no torrent
  lists, in the Library menu and in the row menu. It offers the file's folder
  and each folder above it, nearest first, so one choice hides a whole game or
  a whole drive.
- Choosing a folder saves it and withdraws the disk files under it in the same
  database job, so their rows leave at once with their stored facts. No
  confirmation is asked, because Settings undoes the choice.
- Every later query leaves the folder out. A hidden folder inside another
  hidden folder is not sent.
- Settings lists the hidden folders, each with Remove. Removing one starts a
  refresh, and its files return as new entries.
- The list holds as many folders as fit in one query beside the fixed terms
  (`Gozler.HiddenLimit`); the command says so when it is full.
- A hidden folder does not hide a file that a torrent lists, because the
  torrent's own claim lists it, so the command is not offered on such a file.

## When Library asks

- When Library is shown, and when the window becomes active while Library is
  shown, because that is the moment after the person moved or deleted a file in
  another program.
- On the person's Refresh (F5), Library's own command for this source.
- Every 5 minutes while Library is shown and its window is active, because
  gozler has no change notification.
- Ten seconds after an answer while gozler is still indexing a drive and
  Library is shown.
- Never by itself while Library is hidden; a refresh that is running then
  finishes. The person's own actions in Settings, such as switching the option
  on, Retry and Remove on a hidden folder, ask gozler while Library is hidden.
- After a failure, only Refresh, Retry and showing Library again start one.
- One refresh runs at a time. A trigger that arrives meanwhile starts one more
  after it, because the running answer may be older than the change.
- Only while the option is on and Library's source is connected, as the
  torrent source's reads do. Disk discovery adds no second readiness state.

## One refresh

1. **Ask gozler.** The [absent locations](#absent-locations) are checked again
   at the same time.
2. **The same bytes as the last accepted answer end the refresh.** Nothing is
   parsed or written.
3. **Read the saved disk files** in one short database read.
4. **Compare, outside the database:**
   - *The same path* is the same entry. A known size or modified time that
     differs is saved, and the entry's saved file facts are discarded, so they
     are read again when it is displayed. A same-size edit that keeps its
     modified time is not detected.
   - *A saved path the answer lacks, whose volume and file ID the answer has
     at one new path,* was renamed or moved on its volume and stays the same
     entry. A file with several hard links pairs only when its ID lost exactly
     one path and gained exactly one.
   - *Any other saved path the answer lacks* goes to the
     [presence check](#file-presence), unless its volume is not ready.
   - *Any other new path* is a new entry.
5. **Pair a move to another volume:** when exactly one file with a name and
   size was found gone and exactly one new file has that name and size, it is
   the same entry at its new path.
6. **Accept through `FileSources`** in jobs of at most 2,000 files, so a typed
   search waits for one job and not for a whole first import. Each job writes
   only changes, and the snapshot rows of exactly the entries it changed
   follow ([database](../database.md)). A job writes nothing once the option
   is off, and the last job confirms the disk contribution.

An incomplete answer adds and updates as a complete one does. One `--status`
call then names the volumes that are not ready, and a saved path on such a
volume is left alone, so one drive that gozler cannot read does not keep
deleted files on the other drives in Library. A refresh that the window's
close, the option or a database replacement ends is discarded: its process
ends and its result is never accepted.

## File presence

gozler says what exists, not what is gone: a file can be missing from a
complete answer because the person may not list its folder, gozler has not
read that folder's permissions, its Include settings leave it out, or its drive
is not connected. So **a location is known absent only when the person's own
check says so**, the [File presence](../../../CONTEXT.md) rule.

- **The volume first.** One check per volume: the path's volume answers and is
  the volume gozler named for the file. Otherwise nothing on it changes, so a
  drive that is not connected, or whose letter another drive took, keeps its
  files with their facts.
- **Then each path.** Not found: absent. Found: present. Any other answer, such
  as access denied: unknown, and nothing changes.
- **A file found present stays,** and is checked again only when gozler
  reports it again or the window opens again. A folder that gozler cannot show
  the person costs one check per file per session.
- The checks run in background work, never inside a database job.

| Situation | Library behavior |
| --- | --- |
| A file that only gozler reported is deleted | Its entry and stored facts are removed. |
| A file that a torrent also lists is deleted | The disk claim is removed and the location is saved as absent, so the row leaves Library although the torrent still lists the file. |
| A file goes to the Recycle Bin | It is gone. Restoring it makes a new entry; a match the person corrected by hand must be made again. |
| One hard link disappears | That location is removed; the others stay. |
| Rename, or move on the same volume | The same entry, by its file ID. |
| Move to another volume | The same entry, when one gone file and one new file share name and size. |
| Drive not connected | Nothing changes. |
| TinyTorrent moves, renames or deletes a torrent's files | The torrent's rows change at once; the disk rows follow at the next refresh. |
| gozler stops showing a file that exists | The row stays with its last saved facts. |

## Absent locations

**A file that the person's own check finds gone leaves Library, also when a
torrent still lists it,** because with discovery on every other deleted file
leaves Library, and a row that stays and only fails to open reads as a defect.
The fact is saved with the location (`locations.absent`), so the row stays out
after the window opens again.

- `current_files` leaves an absent location out. It is the one projection that
  decides what Library lists and what file work may read, so no other reader
  tests the fact. The torrent's stored facts stay, because it still lists the
  file.
- Each refresh checks the absent locations again before it uses gozler's
  answer, and a location whose file exists loses the fact. The check does not
  depend on gozler's answer, so a file that returns in a folder gozler does not
  report, such as a hidden folder, still comes back.
- The fact ends when the location loses its last contributor, and when the
  option is switched off.
- A torrent's file that gozler never reported, such as one on a network share,
  is never checked.

## How disk files join the catalogue

`FileSources` is the data-source seam: the torrent source and the disk source
both write through it, and each file's row is derived by one writer.

- **One contribution holds every disk file,** with `contributions.data_source`
  `disk`. Behavior is selected by the data source, never by the origin text.
- **Each source withdraws only its own contributions.** The torrent list never
  withdraws the disk contribution, and the torrent source's read schedule
  (`file_reads`) holds torrents only, so the engine is never asked for the
  files of a torrent that does not exist.
- **A disk file's number comes from a saved counter (`disk_numbers`) when it
  is first seen,** and is never used again, so stored facts of a withdrawn
  file that are still kept never pass to a new one. The number stays with the
  file through a rename or a move, so identifications and file facts keep
  working unchanged.
- **Saved for each disk file,** in `disk_files`: its volume, its file ID and
  its modified time.
- **File confirmation:** the disk contribution is confirmed when the first
  answer of the window session is accepted (`disk_reads`). Until then its saved
  rows are listed, no file work uses them, and cleanup keeps their facts.
- **A location that a torrent lists follows the torrent.** `current_files`
  takes completion and readability from the torrents when a torrent lists the
  location, and from the disk contribution only when none does, so a torrent's
  unfinished file is never shown as finished.
- **Removing a torrent leaves the disk contribution,** so the file stays.
- **Paths stay unique without regard to case;** two paths that differ only by
  case keep one row.
- **A disk file has no torrent.** Show in Torrents is unavailable for it, and
  the File page shows no Torrent row.
- **A disk file's series siblings are the files in its folder,** found through
  the location index, never by reading every disk file.

## Subtitles

- A subtitle file that gozler reports is a Files entry, even when it belongs to
  no movie.
- **The subtitle files beside a disk movie come from the saved catalogue:** the
  subtitle files in its folder and in its `Subs` and `Subtitles` folders. A
  movie that a torrent lists still has its folder listed, as before.
- A disk movie is checked when it is displayed, and again only after a refresh
  that added, removed or moved a subtitle file, when the disk contribution's
  stamp changes.
- A subtitle that discovery found keeps `created=false`, so TinyTorrent never
  moves or deletes it.
- **Automatic subtitle downloads** start from a torrent that finishes, so a
  disk movie gets none. **Download Subtitle on selected disk movies works.** A
  batch over the whole collection covers torrent movies only, because the
  person who starts it expects the movies TinyTorrent downloaded, not a
  supplier request for every movie on the PC.
- Pending and protected answers for subtitle file work count torrents only, so
  a gozler that does not answer holds no subtitle work for torrents.

## Cost

Measured on the owner's laptop on 12 November 2026, one run each, Debug build,
with saved disk files and their answer in a test database:

| Stage | 2,000 disk files | Per 1,000 files |
| --- | --- | --- |
| First import (parse, compare, one job of 2,000) | 404 ms, 24 MB allocated | about 200 ms, 12 MB |
| A refresh that finds 5 changed files | 64 ms | about 30 ms |
| Opening the database, with the search snapshot | 26 ms | about 13 ms |
| First search: Videos, Music, Files | 12, 16, 27 ms | at most about 14 ms |
| A typed search | 10 ms | about 5 ms |
| `gozler.exe`, the whole query (3,424 files on the owner's PC) | 595 ms | — |

A typed search waits for at most one job, so the longest wait is one job of a
first import. The Library table keeps each row object across searches, so a
search at these sizes adds one lookup per row on the UI thread.
