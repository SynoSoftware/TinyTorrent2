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
- **TinyTorrent's installer does not bundle gozler.** Its plugin tree can turn
  media discovery on, or ask for gozler alone, which the app does at its next
  start, so the in-app prompt below is the only way TinyTorrent installs
  gozler. Installing gozler alone leaves discovery off.
  gozler is a separate program with its own installer, updates and
  uninstaller, and the feature starts off.
- **TinyTorrent never overwrites or replaces an installed gozler,** and never
  starts or repairs its service, because it is the person's installation.
- **Only one gozler service runs on the machine:** the person's own, or the one
  TinyTorrent installed. gozler keeps its index in memory (381 MB private and
  200 MB working set with 2.37 million entries on the owner's PC, November
  2026), which works against TinyTorrent's promise to be light. For the same
  reason the feature is opt-in.
- **gozler gains what TinyTorrent needs from it.** A fact that only gozler has
  is added to gozler's answer, not guessed in TinyTorrent. With `--file-id`,
  each result names its file by `volume`, the volume's GUID
  path as `--status` reports it, and `file_id`, 32 hexadecimal digits that
  every hard link of the file shares. So a file renamed or moved on its volume
  stays the same Library entry and keeps the matches the person corrected.

The gozler facts this feature relies on:

| Fact | Consequence |
| --- | --- |
| `--json` output is for scripts; later releases add fields. | Library reads the fields it needs and ignores the rest. |
| An option it does not know ends the call with exit 6. | A gozler without `--file-id`, `--leave-out` or the [own-access option](#the-query) is [older than needed](#what-the-person-sees); no version is compared. |
| The Windows service `gozler` runs `gozlerd.exe`; setup puts `gozler.exe` beside it. | Library finds `gozler.exe` through the service. A stopped service is the person's state. |
| It indexes fixed NTFS and ReFS volumes only. | Files on removable drives, network shares and FAT or exFAT volumes are never found. Settings says "fixed drives". |
| A result's `size` and dates can be missing, which means unknown, not zero. | An unknown size or date keeps the saved value. |
| It has no change notification. | Library asks again [when it needs to know](#when-library-asks). |
| Each hard link is a separate result. | Each location is its own Library entry. |
| A user sees only the entries in folders that user may list, and such an answer still counts as complete. | A file missing from a complete answer may still exist; only [the person's own check](#file-presence) says it is gone. |
| Its Include settings are one setting for the whole PC, changed only by an administrator. | TinyTorrent never changes them; [the query](#the-query) leaves out what Library does not want. |
| A term ends at a space; a path with a space needs quotes inside its term. Without them the query is valid, exits 0 and matches nothing. | The query builder quotes the path in each term. A [folder choice](#library-folders) is an option's own argument and needs no quotes. |
| `--leave-out FOLDER` and `--leave-in FOLDER` take a folder as one option, and gozler tests it on a file's real path: the nearest of the folders at or above the file decides, and a link does not change that. A path wildcard follows links instead: `!"C:\ProgramData\*"` also left out the 9 of 14 files in `C:\Users\Public` that ProgramData's junctions lead to, measured on the owner's PC. | The query gives every [folder choice](#library-folders) as one of these options, so a link never leaves out files that are not in the folder. |
| One query holds at most 256 terms, counting each `ext:` value and `-a-d`. | The fixed query is far below it, and a folder choice is an option, not a term, so the number of choices never reaches it. Windows limits a command line to 32,767 characters, about 550 choices; a person who sets folders with check boxes does not reach that, so Library does not guard it. |
| `-timeout` waits for volumes; it is not a deadline. | Library sets its own deadline. |

## The option

**Show media from this PC in Library** starts **off**. With it off, Library never runs
`gozler.exe`; only [Library folders](#library-folders), when the person opens
it, asks gozler for its drives and folders, so the person can choose folders
before switching the option on. It is saved in `library_settings`, beside Library's other
options, because the engine has no use for it.

- Settings shows the option, what it needs (gozler, and the memory gozler
  uses: about 400 MB for 2.4 million indexed files on the owner's PC), that it
  covers fixed drives, and the gozler state under it. The card's last row
  opens [Library folders](#library-folders).
- Library's empty state, "No files yet", has a button that switches the option
  on, so a person without torrents finds it where they look for their files. No
  other prompt offers it, because a prompt the person did not ask for needs a
  remembered answer and its own design.
- Switching the option off withdraws the disk contribution. Files that only
  gozler reported leave Library; files that a torrent also lists stay, and the
  torrents alone decide them again. The option's description says so. Unlike
  a removed torrent, each withdrawn file keeps its matches
  ([withdrawn files](#withdrawn-files)), because a person who switches the
  option off to free gozler's memory must not lose the matches they corrected.

## Running gozler.exe

- **Find it** in the folder of the program that the Windows service `gozler`
  runs (the service's `ImagePath`), never through `PATH` or the current folder,
  so a folder that anyone can write to never decides which program runs. This
  also finds gozler right after installation, before a running TinyTorrent sees
  a new `PATH`.
- **Run it directly**, never through a shell, with each term as its own
  argument. Read stdout as UTF-8 JSON, or as tab-separated lines for the
  [tree's](#library-folders) folder listing, and stderr as text.
- **Deadline:** 30 seconds per call, then the process ends. A call normally
  takes well under a second.
- **Never on the UI thread.** A refresh runs in the app's background work and
  shows in the work list.
- **A failure is not retried in a loop.** The next try comes from a trigger the
  person controls: Refresh, Retry, or showing Library again. Exit 7 is the one
  exception: it is tried again after 5 seconds, 3 tries in all, because the
  service's pipe may open soon. Exit 8 is never tried again.
- **One call per refresh.** A second call follows only an incomplete answer:
  `--status`, which names the volumes that are not ready.
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
| Older than needed | Exit 6: this gozler does not know an option that Library passes, such as `--file-id` or `--leave-out`. Settings asks before the first search: when it would show Installed, it runs gozler with the options of the query and `--version`, which ends right after the options and never asks the service. The answer is kept until `gozler.exe` changes, so Settings starts gozler once per installed version, not at each change of Library's state. | This version of gozler is too old for TinyTorrent. Settings and Library's message show Update. | Update opens the install prompt in the words of an update ([Installing gozler](#installing-gozler)). No Retry, because asking again changes nothing. |
| Other failure | Exit 1, 3, 4 or 5, or an answer in a shape this version does not read. | gozler could not answer; stderr goes to the debug log. | Retry. |

**Owner ruling (15 November 2026):** TinyTorrent asks for the gozler options
it uses, never for a release number, because the owner numbers gozler's
releases. The one release number is the gozler release that the install prompt
downloads, which the owner writes by hand (20 November 2026); see
[Installing gozler](#installing-gozler).

## Installing gozler

The person switches on the option while gozler is not installed. The dialog is
one `Shared.Dialog` that `MainWindow` opens through `Interact`, and
`ShowEditor` with a draft, so it stays open while the work is pending or has
failed. It says what gozler does for the feature, that it runs as a Windows
service, that it uses memory, and that Windows asks for administrator
permission. Its buttons are Install (default) and Cancel. Nothing reaches
the network before Install.

The same dialog updates a gozler that is [too old](#what-the-person-sees),
which Update in Settings and in Library's message opens. Its title is "Update
Gozler?", it says that Library needs a newer gozler than the one on this PC,
and its default button is Update. It downloads the release that
`Gozler.SetupRelease` names, as Install does. The service exists already, so a
cancelled setup cannot be told from a finished one: the refresh that follows
shows whether the update worked, and a gozler that is still too old shows
Update again.

| What happens | What the person sees and gets |
| --- | --- |
| Cancel | The dialog closes. The option stays off. Nothing is downloaded. |
| GitHub has no release of the gozler version TinyTorrent names, or the release has no uploaded setup yet | The dialog says that Library needs that gozler version, which is not released yet, and names gozler's releases page. Its only button is Close, because asking again fails the same way until that release is published; the person asks again with Install or Update. Nothing is downloaded. |
| Download fails | The dialog stays open and shows the cause; the button reads Retry. The option stays off. |
| The SHA-256 differs | The file is deleted. The dialog says the download is damaged and offers Retry. |
| The person declines the Windows permission prompt | When Windows reports the refusal to TinyTorrent (error 1223, for a setup that asks for permission as it starts), the dialog says Windows did not give permission and the setup did not run, with Retry. gozler's Inno Setup can instead ask from its own process; it then exits without installing, and the next row applies. |
| gozler's setup is cancelled or fails | Decided from the result, not the exit code: setup has exited and the service `gozler` does not exist, so gozler is not installed, with Retry. An update that is cancelled or fails leaves the older gozler, which still does not know Library's options, so the dialog says this version of gozler is too old, with Retry. |
| Install succeeds | The dialog closes, the option switches on and the first refresh starts. No restart is needed. The first answers can be incomplete while the index builds. |

Cancel works until gozler's setup starts. gozler's setup runs with its own
wizard, never silently, because a silent setup grants nobody gozler's "see
every file" right, and that choice decides which folders Library can see.
TinyTorrent starts it through the Windows shell, as a double-click does, and
Windows shows its permission prompt, whether the setup asks as it starts or
from its own process.

`Gozler.SetupRelease` names the gozler release that Install downloads,
0.6.2, the first with the `--leave-out` and `--leave-in` options and the
own-access option. The owner
writes it by hand and moves it only to a release that Library has been tried
with, because a script that takes gozler's latest release would offer a gozler
nobody tried with this TinyTorrent. Library judges an installed gozler by the
options it uses, such as `--file-id`, `--leave-out` and the own-access option, never by this number. Install
asks GitHub for that release, downloads
`gozler-<version>-x64-Setup.exe` from it over HTTPS, compares its
SHA-256 with the `digest` GitHub gives the asset and deletes a file that
differs. gozler releases are unsigned, so this check is weaker than a
publisher signature; it is acceptable because one GitHub account publishes
both projects, so whoever could replace gozler's release could also replace
TinyTorrent's own update.

TinyTorrent's uninstaller leaves gozler installed, because it is a separate
program with its own entry in Installed apps, and TinyTorrent does not record
who installed it.

## The query

One call per refresh:

```text
gozler.exe <own-access option> --json -n all -a-d -attributes --file-id
  --leave-out <each profile's AppData>
  --leave-out <each left-out folder>  --leave-in <each shown folder>
  ext:VIDEO;AUDIO;SUBTITLE  !ext:ts;mts|size:>20mb  !"*\$Recycle.Bin\*"
```

- **The own-access option limits the answer to the folders the person's own
  account may list,** even when an administrator put that account on
  gozlerd's list of accounts that see every file, because Library and its
  folder tree offer only files the person can open. `Gozler` keeps its name
  in one constant, `OwnAccessOption`, and passes it on every call that
  returns files or folders, and not on `--status`.
- **The extension sets come from their owners:** video and audio from
  `FileName`, subtitles from `SubtitleFile`. The file kind of a saved file and
  the query read the same definition.
- **`.ts` and `.mts` count as video only from 20 MB.** They are also
  TypeScript extensions: on the owner's PC, 101,199 such files, none reaching
  20 MB. A transport-stream video below 20 MB is one segment of a film. The
  rule is a clause of its own, because inside an OR group gozler distributes
  the group and counts every extension twice (45 terms instead of 76 for the
  fixed part of the query, measured on gozler 0.5.9).
- **Every profile's AppData and the Recycle Bin are left out,** because
  programs keep their sounds and clips in AppData, and a recycled file is a
  file that is gone. They are not folders a person chooses, so they are not
  [Library folders](#library-folders). Each profile's AppData is a
  `--leave-out` option, listed from the profiles folder at each refresh, so a
  junction inside AppData does not hide what it leads to. It comes before the
  person's choices, so a folder the person shows inside it is shown. The
  Recycle Bin stays a pattern, because every drive has one.
- **Each folder choice is an option:** `--leave-out FOLDER` for a left-out
  folder and `--leave-in FOLDER` for a shown one. gozler applies the nearest
  choice at or above a file's real path, as `FolderChoices` does for the
  catalogue, so a link into a left-out folder does not bring its files back
  and a link inside one does not leave out what it leads to. A gozler that
  does not know the options ends the call with exit 6, and Library shows it as
  [older than needed](#what-the-person-sees).
- **No text the person types** is in the query. A person's search runs on the
  saved catalogue under the product's word-start rule and never goes to gozler.

A file whose attributes show a cloud placeholder (offline, recall on open or
recall on data access) is listed, but saved as not on the disk, so no automatic
reader opens it and downloads it. The person's Open follows Windows.

## Library folders

Games and programs keep media outside the folders the query leaves out: on the
owner's PC, 1,501 of 3,022 audio files are test files of code libraries. So the
person chooses which folders' media Library shows, the way Windows' Indexing
Options chooses what Windows indexes. The choice changes only Library: gozler
still indexes these folders, for every program, by its own Include settings.

- **The rule, owned by `FolderChoices`:** each choice is shown or left out, and
  the nearest choice at or above a file decides. A file with none above it is
  shown. Each saved choice differs from the nearest choice above it, so every
  choice changes something. A file that a torrent lists stays in Library
  whatever the choice, by the torrent's own claim.
- **Defaults:** Windows, Program Files, Program Files (x86) and ProgramData,
  from Windows' known folders, are left out, because programs keep their
  sounds and clips there and a person does not keep a collection there. They
  are ordinary choices, so the tree shows them and the person can show them.
- **Library folders** is a Settings page under Plugins, opened from the last
  row of the Media discovery card. It shows a tree of the drives gozler
  indexes and their folders, as gozler indexes them, each with a check box,
  and a summary of the places Library shows. It saves each click at once, as
  Windows' settings do, so it has no Apply or Cancel. A click on a check box
  sets the folder and everything inside it, as Windows' folder trees do: a
  partial or cleared box becomes checked, a checked one cleared, and the
  choices inside the folder give way.
- **The tree shows gozler's view, not the file system's,** because Library
  finds media only through gozler, so a folder gozler does not index can
  never give Library a file. The drives are the mounts that `gozler --status`
  lists. **A folder that opens reads one level ahead,** off the UI thread, in
  one call: its subfolders when they are not read yet, and the subfolders of
  each of them, so every row it shows already knows whether it has
  subfolders and opens at once, as gozler's own tree does:
  `gozler.exe <own-access option> -path FOLDER /ad depth:FROM..TO -attributes -size -n all -tsv`.
  gozler's depth counts the folders above an entry, the drive's root
  included, so the subfolders of `C:\` are 1 deep. Only a folder the person
  opened shows a progress ring; one read ahead shows none. Tab-separated
  output, because the 25,662 folders of `C:\Windows\WinSxS` take 252 ms that
  way and 1,057 ms as JSON. Released gozler walks every entry below FOLDER
  for this query; [gozler#166](https://github.com/SynoSoftware/gozler/issues/166)
  stops the walk at the depth asked for, with the same command and output.
- **Name, Size, Files and Folders sort the tree,** as a click on their
  headers does in every table. Size is the total of the files below a
  folder, and Files and Folders count the files and folders below it, all
  from the read's `-size`, `--files` and `--folders` columns, which gozler
  keeps in memory. **Owner ruling: these values come only from that one read
  per opened folder, never from a gozler call per folder,** because each
  `-get-folder-size` walks every entry below its folder in a process of its
  own, and scrolling would start one for each row. A value gozler does not
  give leaves its cell empty, as File Explorer leaves a folder's Size, with
  no error, and **an empty value sorts last in both directions** (owner
  ruling), as gozler sorts an unknown size. `--files` and `--folders` arrive
  with [gozler#165](https://github.com/SynoSoftware/gozler/issues/165); a
  gozler without them answers with exit 6, which reads as an older gozler
  and offers Update.
- **The tree** opens each drive one level deep and along the path to each
  choice, so every choice is in view. Every folder opens, however many
  subfolders it has: once the folders of `C:\Windows\WinSxS` are listed, the
  rows in view show at once and the table learns of the rest between the
  person's input. Folders with the hidden attribute are not listed, as File
  Explorer hides them by default, except one at or above a choice. A junction
  or a symbolic link (the reparse-point attribute) is listed but does not
  open, because gozler finds files at their own path.
- **A choice keeps its row** while gozler does not show its folder, so it can
  be cleared: a drive gozler does not index reads "Not connected", and a
  folder gozler does not list reads "Not in Gozler's index". A folder removed
  after its parent was listed, which gozler answers with exit 4, reads the
  same and shows no subfolders; it does not mark the page as failed, because
  every other folder's answer still holds.
- **An incomplete answer** (exit 2) shows the folders that came back, and the
  page's header names the drive gozler is still indexing. When gozler cannot
  answer, the tree shows no drives and the header shows gozler's state in the
  words Settings and Library use, such as "Gozler is not installed." No
  file-system listing takes its place.
- **Leave folder out of Library** is a Library command on a file that no
  torrent lists, in the Library menu and in the row menu. It offers the file's
  folder and each folder above it, nearest first, and leaves the chosen folder
  out as its check box would. A toast names the folder and offers Undo,
  because one click can leave out a whole drive and Settings is far from
  Library; Undo puts back the choices at and inside the folder that the
  command replaced.
- **Saving:** every change, from the page, the command or Undo, is one
  database job that applies the change to the saved choices and withdraws the
  disk files it leaves out, so their rows leave at once and two changes never
  overwrite each other. Each file keeps its matches
  ([withdrawn files](#withdrawn-files)). When a change shows a folder that was
  left out, a refresh starts, and its files return with their matches.
- **Reset all settings** returns the choices to the defaults. It switches
  discovery off first, so no file stays in Library under a left-out folder.

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
  on, Retry and showing a left-out folder, ask gozler while Library is hidden.
- After a failure, only Refresh, Retry and showing Library again start one.
- One refresh runs at a time. A trigger that arrives meanwhile starts one more
  after it, because the running answer may be older than the change.
- Only while the option is on and Library's source is connected, as the
  torrent source's reads do. Disk discovery adds no second readiness state.

## One refresh

1. **Ask gozler.**
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
| A file that a torrent also lists is deleted | The disk claim is removed and the engine is asked to look ([torrent files found gone](#torrent-files-found-gone)). |
| A file goes to the Recycle Bin | It is gone. Restoring it makes a new entry; a match the person corrected by hand must be made again. |
| One hard link disappears | That location is removed; the others stay. |
| Rename, or move on the same volume | The same entry, by its file ID. |
| Move to another volume | The same entry, when one gone file and one new file share name and size. |
| Drive not connected | Nothing changes. |
| TinyTorrent moves, renames or deletes a torrent's files | The torrent's rows change at once; the disk rows follow at the next refresh. |
| gozler stops showing a file that exists | The row stays with its last saved facts. |

## Torrent files found gone

**A file that the person's own check finds gone leaves Library, also when a
torrent still lists it,** because with discovery on every other deleted file
leaves Library, and a row that stays and only fails to open reads as a defect.

- The engine owns a torrent's files, so Library does not decide alone. When
  the check finds gone a file that a torrent lists, or the old path of a disk
  file that moved while a torrent still lists it there, Library sends the
  engine `look` for those torrents.
- The engine follows a payload that moved, or holds the lost files in the
  torrent's [lost-files question](../engine.md#lost-files). The reply makes
  Library read those torrents' files again, and a file whose `on_disk` is
  false is not finished, so its row leaves. The engine's answer is the one
  fact, so Torrents and Library never disagree about the file.
- Until the reply, the row stays with the torrent's claim.
- A torrent's file that gozler never reported, such as one on a network share,
  is never checked.

## Withdrawn files

Leaving a folder out and switching the option off withdraw disk files, but the person did not
delete them, so they keep what the person decided about them.

- Each withdrawn file's volume, file ID and number are saved in
  `withdrawn_disk_files`, and cleanup keeps its identifications.
- When gozler reports a file with that volume and file ID again, it takes back
  its number, so its matches apply again. A file with several hard links gives
  the number to the first link only.
- Its file facts and work in progress are not kept; facts are read again when
  the file is displayed, because the file may have changed meanwhile.
- A withdrawn file deleted before it returns keeps its row. Such rows are a few
  dozen bytes each and grow only with files the person withdrew, so no
  mechanism removes them.

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
  file that are still kept never pass to a new one. Only the same file, by its
  volume and file ID, takes a withdrawn number back. The number stays with the
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
