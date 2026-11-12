# Missing torrent files and files moved outside TinyTorrent

**Design of 11 November 2026, rewritten on 12 November 2026 after four review
rounds and the owner's decisions of that day. Nothing is implemented.** Not
reviewed: the corrections from the last report of round 4 (the Continued
row, the end of a returned file's question, a drop rule of the record, and a
controller row removed as wrong), the trust of a payload that plainly moved
to another drive (decision 5), and [The dialog](#the-dialog). The owner
accepted the dialog's design; its rendered layout is not checked. No
decision is open. It is
the engine's part of [disk discovery](../contracts/library/disk.md) and a
benefit of it: disk discovery brings gozler, and with gozler TinyTorrent finds a payload that the person
moved to another drive. The [engine](../contracts/engine.md),
[pipe API](../contracts/pipe-api.md) and [UI](../contracts/ui.md) contracts own
current behavior; update them alongside implementation.

## Goal

**TinyTorrent continues what the person did to a torrent's files in another
program, and downloads only what the person asked for.**

- **Moved:** the torrent uses the files where they are now, without a click.
- **Gone:** the files wait. The person is asked once, for all torrents
  together: download them again, or skip them.
- **Untouched:** the rest of the torrent keeps running.

## Decisions

The owner's, in the owner's words.

1. 11 November 2026: when a file that held downloaded data is gone while its
   drive answers, the engine does not download it again by itself.
2. 11 November 2026, the case that matters most: "I downloaded something, I
   moved it, and TinyTorrent can find it with gozler and use that new location
   like nothing happened." About a check at the new place: "gozler can tell us
   that it is the same file. no need to check it".
3. 12 November 2026: "we should detect that file was deleted externally and we
   should ask the user (if the torrent is not stopped) what does he want to
   do... download again or uncheck them". The dialog "should look in the same
   style like remove torrent", with "checkboxes in front of each torrent with
   the missing files count and size", the answers Redownload and Skip, and no
   Delete files check box.
4. 12 November 2026: "gozler is mandatory for plan 13... plan 21 is a bonus, a
   benefit of plan 13. if you can use ntfs to figure out files by file id,
   fine, but that doesn't mean that you're removing gozler from here."
5. 12 November 2026, about a payload found on another drive: "if we can
   reasonably detect that it has been moved from one drive to other (gozler)
   - we can trust it". The dialog: "design it from remove dialog".
6. 12 November 2026: the owner accepted [The dialog](#the-dialog) as
   designed, with Cancel as its default button, and then removed its row for
   choosing the folder of moved files: "if you didn't locate it with gozler,
   there's no point". The ruling in the
   [UI contract](../contracts/ui.md), "Buttons", gives Cancel to a question
   "about an act the person did not choose" and names one such case; this
   dialog is the second, because the person chose neither answer.

## Steps

Use the [testing](../agents/testing.md), [compilation](../agents/compilation.md)
and [coordination](../agents/coordination.md) policies. Read [Words](#words)
first. Each step names what to read and ends on its condition. Each evidence
case watches a failure that the person would see. Steps 1 to 8 need no gozler.

1. **The question.** Read [The question](#the-question-in-the-engine) and
   the reload's save and the checkpoint rule in
   [The reload and the storage controller](#the-reload-and-the-storage-controller).
   Done when every writer of the saved priorities reaches the question
   through its one function, and these hold: after a file is lost, a started
   torrent requests nothing for it and seeds its other files; a crash between
   the look and the save, and one between the save and the reload, download
   nothing.
2. **"Is this torrent done" and the repair mark.** Read items 1 and 3 of
   [One owner for each question](#one-owner-for-each-question).
   Done when every reader of libtorrent's "finished" in `engine/src` is listed
   in the report with its decision, and these hold: a torrent whose every
   file is lost stays in its folder, and shows Files missing while it is
   started; Skip sends no Download finished toast.
3. **The look at an add.** Read [The look](#the-look).
   Done when every add of a torrent from resume data, and every place that
   clears the pieces before an add, is listed with its look, and these hold: a
   file that was deleted or cut short while the engine was closed is in the
   question after the start, is not downloaded, and the torrent checks
   nothing; files of a seeding torrent that are deleted one after another end
   in the question with no check of every file; an Exit right after that
   start loses nothing; a magnet that is restored without its metadata
   fetches it.
4. **The look before a check.** Read [The look](#the-look).
   Done when every caller of `force_recheck` and of `clear_error`, in the
   engine and in the fork, is listed with its look or with this plan's reason
   for none, and these hold: Verify of a torrent with a deleted file ends
   with the file in the question and nothing downloading; so does Move files
   of such a torrent; so does Retry after the write of a piece that
   libtorrent had already counted failed, which is the failure that sets the
   fork's mark for a check: the deleted file is in the question before that
   check runs.
5. **The controller's event and the look command.** Read
   [The reload and the storage controller](#the-reload-and-the-storage-controller).
   Done when each row of that section's table that the controller decides is
   a case in `engine/tests/Transitions.cpp` and its run shows 0 failures, and
   the report names the lines of the driver that enforce the two rows it
   decides: a reload runs, and the torrent is removed.
6. **The record, the proof and "data on disk".** Read
   [The record and the proof](#the-record-and-the-proof) and item 2 of
   [One owner for each question](#one-owner-for-each-question).
   Done when these hold: a file of the right name and size without a matching
   record is used only after a check; the neighbours of a deleted file stay
   in Library; Set download folder to the payload's new place on its own
   drive reads no file; a torrent that finished before this version has its
   records after one engine start.
7. **The follow on the same drive.** Read [The follow](#the-follow).
   Done when these hold: after the folder above a seeding payload is renamed,
   the torrent seeds from the new path with no check; a file that a program
   changed before the move is checked; files put back from the Recycle Bin are
   used with no check; a payload that was deleted to the Recycle Bin is asked
   about.
8. **The app.** Read [What the person sees](#what-the-person-sees) and
   [Pipe and saved data](#pipe-and-saved-data). Design the bar's text and
   severity, the dialog, the row mark, the Files state and the popup first, as
   [`app/AGENTS.md`](../../app/AGENTS.md) and
   [`engine/AGENTS.md`](../../engine/AGENTS.md) ask. Every new text goes into
   each shipped catalogue of the app and of the engine.
   Done when these hold: one answer in the dialog serves every checked
   torrent; an answer leaves alone a file that became missing after the dialog
   opened; Enter in the dialog cancels; Skip leaves the files unchecked; a
   dialog for one torrent shows no check box; the Files tab shows
   Missing, and checking such a file downloads it; the
   bar counts only started torrents, and the row's command opens the dialog
   for a stopped one; with the window closed the popup shows one notice; Open
   on a payload that moved on its drive opens the new path.
9. **The follow to another drive,** after plan 13's rules for running gozler
   exist. Read [The follow](#the-follow). The tests give the runner's answer
   as a list of paths, so they need no running gozler.
   Done when these hold: a payload copied to another drive with its times
   kept, and deleted at its old place, is followed with no check and no
   click, and one notice names the torrent and the folder; a payload with one
   file of another time is asked about; so is a payload that two folders hold;
   a copy whose original is still in place is checked by its hashes; the bar
   asks when the service is stopped and when the deadline passes; while a
   volume is still built the search repeats, and it ends when the volume is
   ready.
10. **Contracts.** Move this plan's rules into the contracts and delete the
    plan. Done when each of these has its rule: the engine contract (lost
    files are asked about), the files contract (the record, the proof, the
    follow, and the owner's ruling on hash checks with its exception for a
    payload that plainly moved), the pipe API (the fields and the two commands), the feedback
    contract (the bar, its place in the order, the dialog), the UI contract
    (this dialog as the second case of a Cancel default), the notifications contract
    (the popup's notice), the Library contract (the look and "data on disk"),
    and plan 13 or the contracts that replaced it (the changes under
    [What changes in plan 13](#what-changes-in-plan-13)).

## Words

- **Lost file:** a file whose verified data is gone. `LostData` decides it: the
  path is not found, or the file is shorter than its data, while its folder
  answers (`engine/src/Engine/Storage.cpp`).
- **Question:** the lost files of one torrent that the person wanted and has
  not answered for.
- **Look:** one run of `LostData` for a torrent, on its drive's worker.
- **Record:** what the engine remembers of a finished file: its identity on
  its drive and its modification time.
- **Proven:** a file that its record shows to be the same, unchanged file, or
  a file of a payload that plainly moved to another drive.
- **Follow:** the torrent continues at the folder where its payload is now.
- **Done:** libtorrent calls the torrent finished, and it has no question.

## What the person sees

| The person | Result |
| --- | --- |
| Moved the payload on its drive, or moved or renamed a folder above it | The torrent follows at once, with no check, no notice and no click. |
| Put deleted files back from the Recycle Bin | The torrent uses them again, with no check. |
| Moved the payload to another fixed drive, with gozler | The torrent follows with no check. One notice names the torrents and the folder. |
| Moved the payload where the engine cannot find it, such as a removable drive or a share | Asked, as for deleted files. |
| Deleted files | Asked: Redownload, or Skip. The other files keep running. |
| Unplugged the drive, or the share is away | Nothing changes: only a folder that answers can show a lost file. |
| Renamed a file, or the torrent's own top folder | Asked. The engine uses a torrent's file under the name that the torrent gives it. |
| Moved only some of the files | Asked about those files. A torrent has one folder. |

### The question

The owner decided the question and its dialog. This plan adds that the person
opens the dialog. Reason: a dialog that opens by itself takes the focus from
what the person is doing.

- **The message bar asks.** The engine reports one condition for all asked
  torrents, as it does for torrents that wait for the person
  (`engine/src/Engine/Alerts.cpp`, the lasting conditions), and the window's
  one message bar shows it as a warning, as it shows torrents that wait for
  their drive: the torrents wait for the person, and nothing failed. Title:
  Files missing. Message: one torrent is named, several are counted, with the
  reason, such as "3 torrents have files that were deleted or moved outside
  TinyTorrent." Its one action, Review, opens the dialog. In the bar's order
  of problems it follows the torrents that wait for their drive or share. It
  returns after a close as the
  [feedback contract](../contracts/ui/feedback.md) says for every bar.
- **The bar counts started torrents** (`facts.intent` other than `Paused`),
  when the engine's search for the files has ended and the condition has
  lasted the two minutes that every torrent condition waits. Reason: a move
  that still runs ends in a follow. A stopped torrent shows the count on its
  row, and the bar counts it when the person starts it. A seed limit stops a
  torrent with an open question as it stops any other, and it is then a
  stopped torrent.
- **The dialog answers,** as [The dialog](#the-dialog) designs it.
- **The row and the details pane** show the count and offer the command that
  opens the same dialog for the selected torrents, stopped ones included.
  Reason: a closed bar leaves every torrent with its answer, as with Retry.
- **The Files tab** shows such a file as Missing. Checking it there is the
  answer Redownload for that file.
- **With the window closed,** the engine's popup shows the condition as one
  notice, under "Notify about problems". Its button opens the window, where
  the bar asks.
- **The torrent keeps its place in the list.** It shows under its transfer
  status, and the Errors filter stays for failures.

### The dialog

Designed on 12 November 2026 from the Remove torrents dialog, with the
impeccable and WinUI design skills, in the skills' Operate mode: the person
has a task to finish, so the dialog reuses Remove's parts and adds no new
visual element. It is the dialog of Move files and Remove torrents
(`app/src/Torrents/FileDialog.xaml`), with a third draft beside `MoveDraft` and
`RemovalDraft`.

```
┌──────────────────────────────────────────────────────────────┐
│ [Files]  Redownload or skip missing files?                   │
│                                                              │
│ [folder] D:\Downloads                                        │
│     [x] Some.Show.S01.1080p                 3 files · 4.2 GiB│
│     [x] Another.Movie.2024                   1 file · 1.8 GiB│
│ [folder] \\nas\media                                         │
│     [ ] Concert.Recording                   2 files · 650 MiB│
│                                                              │
│ 2 torrents · 4 files · 6.0 GiB  [Redownload] [Skip] [Cancel] │
└──────────────────────────────────────────────────────────────┘
```

**Title: "Redownload or skip missing files?"** with the Files glyph. File
Explorer asks the same kind of question when a copy meets existing files: its
title, Replace or Skip Files, names both answers, because the person must
choose and neither answer is expected. Remove's title glyph names its act;
this title has two acts, so its glyph names the subject, not one answer.

**The list, as Remove's.** The torrents are grouped by folder, with the
folder glyph and the folder's path in semibold, trimmed with its full text in
a tooltip. Each torrent's row keeps Remove's template: the name in the
identity style, trimmed, with name and folder in its tooltip, and at the right
in secondary text the count and size of its missing files. The list scrolls
in Remove's 224 DIPs, so the parts below it stay in view.

- **A check box in front of each torrent,** checked when the dialog opens,
  when the dialog lists two or more. A dialog for one torrent shows no check
  box, as unchecking its only torrent would be Cancel.
- **The count names the files.** Its tooltip lists the missing files' names,
  ten at most, then "and 4 more", within the 456 DIPs that a tooltip fits;
  the check box's accessible help text carries the same list. Reason: the
  person decides by which files are gone, such as three watched episodes or
  the sample file.
- **A torrent whose question ends while the dialog is open keeps its row.**
  Its check box turns off and is disabled, and its count reads Found again.
  Reason: rows never move under the person.

**Footer: the cost of the answer.** The footer's one item is a fact, as the
UI contract allows: the count of checked torrents, of their missing files and
their total size, such as "2 torrents · 4 files · 6.0 GiB". It shows what
Redownload will fetch, and it updates as boxes are checked. A dialog with no
checked torrent hides the fact and disables Redownload and Skip.

**Buttons: Redownload, Skip and Cancel,** one word and one glyph each, by the
owner's ruling on buttons. Redownload has the Download glyph; Skip has the
CircleSlash glyph, which the Files tab shows for a skipped file. Their
tooltips carry what the word leaves out:

- Redownload: "Download the missing files of the checked torrents again, into
  their folders."
- Skip: "Uncheck the missing files of the checked torrents. You can check them
  in Files later."
- Cancel: "Decide later. The message bar keeps the question."

The shared `Dialog` has a second answer button; connect it to the draft as
the question about an unfinished draft does (`AskUnfinished` in
`app/src/Shell/MainWindow/Dialogs.cs`), because `ShowEditor` connects only the
first. Cancel is the default, by owner decision 6, so the accent and
the first focus are on Cancel, and Enter decides nothing. The dialog has no
"Don't show this dialog again": a remembered answer would download or skip
files without asking.

**Keyboard:** Tab goes through the check boxes and the three buttons;
Space checks a box; Escape is Cancel. Each check box's accessible name is the
torrent's name.

**Engine gone:** FileDialog's existing Restart bar shows, and the answers are
disabled through `FileDraft.CanSend`, as for Move and Remove.

### The answers

- **Redownload:** the files get back the priority the person gave them, and
  the engine downloads them into their place. A stopped torrent stays
  stopped.
- **Skip:** the files become ordinary skipped files, and the Files tab shows
  them unchecked.
- **The dialog closes at once.** It lists what the rows say, the engine
  accepts the answer, and the dialog closes.
- **The engine looks before it applies an answer.** Each torrent's answer
  applies when the look on its drive returns, to the files that were in the
  question when the engine accepted the answer and still are. A file that is
  back is used. A torrent whose drive does not answer keeps its question. Reason: Redownload for a file that the person
  has just put back would download it a second time.
- **An answer acts only on what the dialog showed.** A question has a number
  that rises at each change. The answer returns the number that the dialog
  showed, and the engine compares it with the question's number from before
  the answer's own look. An answer with another number leaves that torrent's
  question as it is, and the bar stays for it. Reason: a count is no
  identity. When one file comes back and another is lost, the count is the
  same.

## The question in the engine

**A file with an open question is a skipped file.** The torrent's saved facts
get one new part, the question: its number, and for each file the priority
that the person had given it. In the same save the file's saved priority
becomes "do not download".

Reason: every reader of a file's priority then reads the truth without knowing
the question. Those readers are the priorities that libtorrent gets, the base
of the person's next edit, the file page's `priority`, Library's `Wanted`, the
wanted-pieces map and the totals. A separate "missing" mark that each of them
must merge is the kind of flag that one reader forgets, and that reader
downloads the file.

**One function changes the question and the priorities together.** It is the
only writer of the question, so the question holds only skipped files.

- The person's priority edit gives it the files that the edit names. Reason:
  the edit replaces the whole list of priorities (`CommitEdit`), so the values
  alone do not show that the person confirmed Skip for a file.
- When one turn of the engine finds lost files in many torrents, it saves
  once for all of them.
- `Describe` leaves the question out of what it sends, as it leaves out the
  priorities.

| What happens | Effect on the file |
| --- | --- |
| A look finds the file lost, the person wanted it, and the torrent does not follow | It joins the question and becomes skipped. |
| The person answers Redownload, or checks the file in the Files tab | It leaves the question with its earlier priority, and downloads. |
| The person answers Skip, or an edit names the file with any other priority | It leaves the question. That priority decides. |
| The file is on disk again and is proven, or a check verifies every piece of it, or the torrent follows | It leaves the question with its earlier priority, and its pieces count again. |
| The torrent is removed | The question goes with its facts. |
| Start, Force, Stop, a schedule, an engine restart | The file stays in the question. |

A lost file that the person had skipped stays as it is: nothing would
download it.

## One owner for each question

The first review found most defects of the earlier design at a reader that did
not know about missing files. Each question that such a reader asks gets one
function.

1. **Is this torrent done?** See [Words](#words). Completion with the final
   move, the Download finished toast and the check of a finished download,
   the Completed status and the row's `complete` read this one answer.
   Reason: a torrent whose every file is in the question is "finished" for
   libtorrent, and would move to its final folder with no files. Where a
   torrent with a question would be Completed, its status is Files missing,
   also while it uploads its other files; while it downloads other files it
   is Downloading. The seed limits and the controller's measure of progress
   keep libtorrent's value: they measure seeding and transfer.
2. **Is this file's data on disk?** The engine answers for each file: the
   file is neither lost nor in a question, and every piece of it is verified
   or it has its record. Library reads
   this answer in place of its own count of pieces. Reason: the reload drops
   the pieces that a lost file shares with its neighbours. Without this answer
   the intact neighbours of a deleted file leave Library until those pieces
   download again, which never happens for a stopped torrent.
3. **Did a download finish?** The question's function sets a repair mark when
   files join the question of a torrent that libtorrent calls finished. While
   the mark is set, `Completion` passes over a finish. The mark is saved with
   the torrent's facts. It ends when libtorrent next calls the torrent
   finished, by any path, and when the person makes any file of the torrent
   wanted.
   Reason: after Skip the torrent fetches only the few pieces that the lost
   files shared with their neighbours, and a Download finished toast, or a
   check of every file, for that repair is wrong.

## The look

**One rule: the engine looks before a check of every file can start.** A
check discards the pieces that show which files held data, and `LostData`
judges by those pieces.

**Every start of a check has its look:**

| Start | The look |
| --- | --- |
| `Recheck`, the engine's one caller of `force_recheck`: Verify, the check of a finished download, a check after a restart or after names | `Recheck` owns the wait. It keeps the wanted check and asks for a look. It sends `force_recheck` when a look has run and no reload is wanted. |
| The fork's own check after a write had failed. `clear_error` starts it (`src/torrent.cpp:1278`, `:9981-9987`), and both `ClearDiskError` and every `resume()` reach `clear_error` (`:10574-10607`) | An answer that ends a storage failure waits for one look: `Answer` asks for it, and gives the answer to the controller when the look returns or fails. A torrent without a failure, and the answers Stop, Move and Remove, go on at once. The answer then clears the error as today. After a failed write the fork's resume data lists no pieces, so the fork's check of every file runs: it repairs a counted piece that is not on disk, and by then the lost files are in the question. The clears that the engine makes by itself follow a probe, which has just looked. |
| The fork's own check after Move files of a torrent that has a skipped file: `move_storage` finds a file at the destination and asks for a check (`src/storage_utils.cpp:128-131`, `src/torrent.cpp:9457-9464`) | The move looks at the source, and saves the question, before it places files. Every torrent with a question has a skipped file, so every such move takes this path. |
| An add from resume data: the engine start, a move, the reload | See "The hold at an add". |
| The restore of a torrent that is marked for verification, which clears the pieces first | The restore keeps the pieces that it clears, as an add keeps its own. A restore whose resume file names another folder keeps nothing: those pieces belong to the other folder. |
| A storage failure | The controller's probe and minute check, as today. |
| A request from the app | See "The app asks for a look". |

Two more starts exist in the fork and need no look. `leave_seed_mode` checks a
torrent that was added in seed mode, and the engine adds none that way.
`clear_error` after a conflict of two torrents (`On(torrent_conflict_alert)`)
starts the check only for a torrent whose write had failed before it
conflicted, which falls under "Outside the look".

**The hold at an add.** When the resume data that the engine adds claims
pieces, the engine keeps those pieces and holds the torrent, paused, until
libtorrent has judged that data. An add without pieces, such as a magnet that
still fetches its metadata, has nothing to judge and gets no hold. The torrent's state shows the judgement
(`src/torrent.cpp:2473-2484`, `:9232-9255`), so the engine needs no alert,
which libtorrent can drop under load.

- **Accepted** (`downloading`, `finished` or `seeding`): the hold ends and
  the kept pieces go.
- **Rejected** (`checking_files`): libtorrent has discarded its pieces and
  queued a check, which waits while the torrent is paused. The engine looks,
  judged by the kept pieces. Lost files join the question, and the reload
  adds the torrent again from the kept pieces without theirs. libtorrent
  accepts that data, so the check of every file never runs. When nothing is
  lost, the hold ends and libtorrent's check runs, as today.
- Where the add ends in name preparation (the engine start and a move), that
  preparation is the look: it runs on the drive's worker and reads each file.
  It gets the kept pieces, or the handle's pieces once the add was accepted,
  and applies `LostData`'s whole rule to them, presence and size. Today its worker gets only the metadata, the folder and
  the names. The reload's add has no name preparation, so its look is the
  driver's own job.
- While the engine keeps a torrent's pieces, a checkpoint saves the kept
  pieces. Reason: Exit saves every torrent from libtorrent's own pieces. An
  Exit between a rejection and the look would save a torrent with no pieces,
  and the next start would check every file and download the lost one.

**Name preparation keeps a lost file's name.** Today it gives every file that
is not found the unfinished name, and the name change starts a check
(`PrepareNames`, `ApplyNames`, `FinishNames` in
`engine/src/Engine/Files.cpp`). A file that is not found and has verified
data is lost: it keeps its name and is reported. Reason: otherwise the first
engine start after a deletion checks the whole torrent, and the engine then
knows the lost file under a name ending in `.!tt`, which no moved file has.
After Redownload the file is an ordinary file without data, and gets the
unfinished name at the name preparation that the answer asks for.

**The app asks for a look.** Library asks when its own presence check finds a
torrent's file gone ([disk discovery](../contracts/library/disk.md#file-presence)). The window asks when an
Open or Open folder command gets "path not found" from Windows for a torrent.
The reply says whether the torrent followed, and the window then opens the
new path. The window waits 2 seconds for this reply and then reports the path
as not found, so a drive that does not answer cannot hold the command. Reason:
for a torrent that no peer reads, Open is the person's first contact with a
payload they moved. The open request must carry the torrent, which
`OpenRequested` lacks today
(`app/src/Shell/MainViewModel/Actions.cs`). The engine runs its own look and
uses no path from the app.

**Set download folder is the person's answer.** What is absent at the folder
that the person chose downloads there, as today. Move files moves what the
torrent has, so it looks first, as the table says.

**Outside the look, as today:**

- A seed's file that is deleted or moved while no peer asks for it is found
  at the next read, look from the app, check or engine start. A periodic
  check of healthy torrents would read every torrent's files each minute and
  wake sleeping drives.
- A file that is deleted while a check of its torrent runs downloads again
  after the check. Catching it needs a hold between the end of every check
  and a second look, on each of the 33 callers of `ApplyIntent`, for a
  deletion that must fall inside the check.
- A torrent whose write failed, and that then conflicts with another torrent,
  is checked by the fork with no look: two rare events must meet.

## The reload and the storage controller

**The reload is the one job that makes lost files safe.** It gains a first
step:

1. **Save** the question and the skipped priorities.
2. Build the torrent's parameters, drop the lost files' pieces, release the
   files, add the torrent again (today). The add uses the saved priorities, so
   the torrent requests nothing for a lost file.

A save that fails ends the reload as a failed reload, which the controller
already handles. The reload builds from the kept pieces when the engine keeps
pieces for the torrent, and from the handle's resume data otherwise. Today it
always reads the handle, which has no pieces after a rejection. It also gives
pieces back: it adds the torrent with the pieces that count without a check
([The record and the proof](#the-record-and-the-proof)).

**A file is wanted and without its pieces only when the person was asked.**
The reload saves before it drops pieces. A checkpoint leaves out a lost
file's pieces only when the saved facts skip the file; a checkpoint that
still claims them is safe, because the next add finds the file lost again.
The look comes before every check.

**`Control` gets one event and no new stage: what a look found without a
failure.** The event carries the lost files, and whether files of the
question are back and proven. Either one makes the controller want a reload:
it keeps that wish beside the lost files, so `Failing()`, `Holds()` and `Due`
read one answer for both. The request for a look stays in the controller's
driver, beside the driver's other waiting work. The driver starts a look when
the controller has no job, no reload is wanted, and no file work uses the
files. `Torrent::WorkWaits` counts a wanted look, so a check waits for it.

| Controller state | The event does |
| --- | --- |
| No failure, no job | `Lose`, or the wish for a reload that gives pieces back. The reload starts at the next `Due`; `Failing()` already counts lost files. |
| A reload runs | The driver drops the result and looks again after the reload. Reason: a reload that succeeds forgets every lost file. |
| A failure recovers, is absent or is stopped | `Lose`. The failure's own probe finds the same files. |
| A failure waits for the person | `Lose`. The reload follows the person's answer to the failure. |
| Continued | `Lose`. `Due`'s reload branch also runs in Continued, with its guards. A reload that succeeds there leaves the ladder and the count of continues as they are, and resets the progress as every reload does, because the skipped files lower the measure. One that fails is the failure returning (`Ladder::Return`), as an error in Continued is today. Reason: lost files hold the torrent, a held torrent makes no progress, and Continued ends only by progress; and a reload for lost files is no retry of the failure. |
| The torrent is removed | Refused, as `Steer` refuses every start. |

The hold at an add is one new term that `Torrent::IsHeld` reads. It starts at
an add whose resume data claims pieces, and ends at libtorrent's judgement, or
at the look that follows a rejection.

A move's own reload already settles the lost files it carried, and the
controller forgets those (`Control::Forget`, from `Replaced`). `Replaced`
also ends the wish to give pieces back, which was about the old folder. A
file of the question that the move verified at the destination leaves the
question: `SaveMove` gives those files to the question's function.

An audit of 12 November 2026 read all of `Control.cpp` and
`Engine/Storage.cpp` and found the design sound: each of its rules has one
function that enforces it, and `ApplyIntent` is the only place that resumes a
torrent. The changes above go into those functions.

## The record and the proof

**The engine keeps, for each finished file of a torrent, the file's identity
on its drive and its modification time.** With this record the engine knows
the file again without reading it.

- **What:** the volume's identity in the form plan 13 uses, the file ID as
  128 bits, and the modification time, which Windows gives for an open file
  (`GetFileInformationByHandleEx`). A volume without stable file IDs, such as
  FAT or exFAT, gives a record without an ID.
- **Where:** one small file for each torrent, beside its resume data, written
  when a record changes and deleted where the resume data is deleted. Reason:
  the facts document is written whole at every commit and every 30 seconds
  while bytes move, and `Describe` sends a torrent's facts to the app. A
  record for each of 50,000 files would be rewritten and sent with every
  small change.
- **Taken** on the drive's worker each time a file becomes finished and has
  its final name: after its last piece, after a check, after the engine moved
  the torrent, and once after an engine start for the files that finished
  before the record existed. That one pass opens each finished file once.
- **Kept** while the file has an open question, because it is then the only
  description of the file that left. Kept also when a piece of the file stops
  counting only because another file that the piece covers is absent: the
  file's own bytes did not change.
- **Dropped** when a check fails a piece of the file while every file that the
  piece covers is on disk, when the person answers Redownload or Skip for the
  file, when the engine moves the file, and when the torrent's folder changes
  and the file is not proven at the new folder.

Somebody must remember the file from before it left. gozler knows where each
file is now, and it keeps a file's entry when the file moves on its volume.
Only the engine knows which entry was a torrent's finished file, and a move
to another volume makes a new file with a new ID. So the engine remembers,
and gozler finds.

**A file is proven when it is the same file and it did not change:** it has
the record's file ID, the size that the torrent gives it, and the record's
modification time. The ID alone leaves a gap: a program can change a file in
place, and the file keeps its ID.

**A payload that moved to another drive is proven when the move is plain to
see** (owner decision 5): every file of the torrent that held data is gone
from its old folder, and one other folder holds all of them, each under its
name with its record's size and modification time. Windows gives each file on
the other drive a new ID, so the ID cannot show the move. The old files gone
and one complete, unchanged set elsewhere is what a move leaves behind. When
two folders hold such a set, nothing shows which one the person moved, and
neither is proven.

Any other copy is checked by its hashes, as the files contract's ruling asks
for a file of the expected size at a move's destination.

Name and size prove nothing: another program's download of the same torrent
has the same names and sizes.

**A piece counts without a check when every file that it covers is proven.**
One function gives this answer, for the follow, for files that came back to
their place, and for the person's Set download folder.

**Download mode compares before it checks.** The rule belongs to the move
owner's download mode, so the person's command and the engine's follow behave
alike. When every file that holds data is proven, the torrent continues at
that folder and nothing is read, and the mode leaves the torrent unmarked for
verification. Today every such move marks it, and name preparation then
starts a check (`SaveMove`, `ContinueNames`, `FinishNames`). Otherwise the
mode checks every file, as today.

## The follow

**The engine follows a payload when every file of the torrent that holds data
is lost and is found under one other folder, each under the path that the
torrent gives it.** That is the case where the person moved the whole
payload. The look that finds the files lost tries the follow before it asks.

Two finders work together. gozler finds a payload on another drive. On the
same drive, the file ID of the record leads Windows straight to the file,
which the owner allowed as an addition to gozler.

### On another drive: gozler finds the folder

A move to another drive makes new files, so no ID leads to them. gozler finds
them by name and size.

1. **When.** After a look left files in the question, and at each engine
   start for a torrent that has a question. The service `gozler` must exist,
   and every file of the torrent that holds data must be lost and have a
   record. The engine uses an installed gozler, and leaves installing and
   starting it to plan 13.
2. **Ask, in one call:** at most five of the lost files, the largest first,
   each by its whole final name and its size
   (`<wfn:a size:1>|<wfn:b size:2>`), with a result limit above what the
   answer can hold. The engine runs `gozler.exe` under plan 13's rules for
   running it, on a worker of its own, so a gozler that does not answer
   blocks no drive's work and no save. Exit ends a running `gozler.exe` at
   once, so Exit keeps its 10 seconds.
3. **Use what came back,** as plan 13 does. Each result, less the file's
   path inside the torrent, names a possible folder outside the Recycle Bin.
4. **Validate** on that folder's drive worker: every lost file is there under
   its name, with the record's size and modification time. Exactly one folder
   must pass. When several pass, the person is asked, because the engine
   cannot choose between copies.
5. **Use it** through download mode, asked by the engine. The payload moved
   ([A file is proven](#a-file-is-proven)), so the torrent continues at the
   new folder with no check, as on the same drive, and the files leave the
   question. Every file stays where it is. The mode's own refusals apply,
   such as files that another torrent claims; a refusal leaves the question
   open.
6. **Tell the person** with one notice for all torrents that one search
   followed. It names the torrents and the folder, because the engine chose a
   folder on another drive by itself.

**The search ends** with gozler's answer, with the 30-second deadline, at
once when the service is stopped, and after plan 13's three tries when the
service does not answer. One state keeps it open: nothing was followed, and
`--status` shows a volume that gozler still builds or scans again. The engine
then asks again one minute later, until that volume is ready. gozler builds
its index again at each start, so this is the normal state after Windows
starts. A volume whose index failed ends the search.

A result that arrives after the torrent's question or its folder changed is
discarded.

The engine runs `gozler.exe` itself, where plan 13's Library lets the window
run it. Reason: the engine works while the window is closed, which is the
usual state while torrents seed. If only the window could search, the engine
would report missing files for a payload that the person only moved. Plan
13's section "Running gozler.exe" is the one text of the rules for both.

A person who keeps a second copy of their downloads on another fixed drive,
and deletes a payload, gets a torrent that now uses the copy. The notice says
so, and Set download folder leads back.

### On the same drive: the file ID finds the file

Windows opens a file by its ID from any place on its volume (`OpenFileById`),
and gives the path it has now. The look does this for each lost file that has
a record with an ID.

- It answers in the look itself, also while gozler still builds its index
  after Windows starts.
- It finds a file whatever happened to the folders above it: moved, renamed
  or nested deeper.
- A file under `$Recycle.Bin` counts as deleted.
- When every such file is proven and they share one folder, the torrent
  continues there at once, through download mode, with no check and no
  notice. The files are the same files, which is the owner's case "no need to
  check it".
- The same lookup finds files that came back to their own place, for example
  from the Recycle Bin. They leave the question in the save of the reload
  that gives their pieces back. Until that reload they stay in the question,
  so a move before it treats them as files of the question.

gozler needs no query by file ID for this: Windows gives the answer from the
record.

## Pipe and saved data

Working names; [naming](../agents/naming.md) decides them at implementation.
`redownload` is taken: it is the pipe name of Set download folder.

- **Torrent row:** the count and the size of the files in its question, the
  question's number, and the status Files missing.
- **File entries:** `missing`, and the engine's answer "data on disk".
  `priority` stays the saved priority.
- **The condition** for the bar and the popup: the count of asked torrents
  and of their files.
- **The answer command:** the torrents, each with the number it showed, and
  the answer. The engine accepts it at once, and commits once for all
  torrents whose look has returned. It answers for each listed torrent that
  still exists.
- **The look command:** the torrents. Its reply says, for each, whether the
  torrent followed.
- **Saved facts:** the question, as its number and each file's index and
  earlier priority, and the repair mark.
- **Beside the resume data:** the records.

## Today's code

Facts that cost a trace to find. Line numbers are of 12 November 2026.

| Fact | Source |
| --- | --- |
| The storage controller keeps the lost files and starts a reload at the next `Due`. The reload adds the torrent again without those files' pieces, and the torrent is held until then. The files then download again, because the torrent runs by its intent with unchanged priorities. | `Control::On(Due)`, `Holds()`; `Reload`, `DropPieces` in `Storage.cpp` |
| The reload drops every piece from a lost file's first byte to its last, so the pieces it shares with its neighbours go too. | `DropPieces` |
| At an add, libtorrent checks the resume data against the files. A wanted file with saved pieces that is absent, or a seed's file that is too short, rejects the data: libtorrent discards every piece and queues a check of every file, which starts when the torrent is not paused. | `3rdParty/libtorrent/src/storage_utils.cpp:564-632`; `src/torrent.cpp:2334-2343`, `:2463-2477`, `:10256-10264` |
| libtorrent leaves a skipped file out of that resume check. A check reads every piece of a file that exists, whatever its priority. | `storage_utils.cpp:570-576`, `:606-611`; `src/torrent.cpp:2814-2824` |
| A torrent with every wanted piece skipped is finished for libtorrent. | `include/libtorrent/aux_/piece_picker.hpp:472-487` |
| libtorrent can drop an alert when its queue is full, also the one that reports rejected resume data. | `include/libtorrent/aux_/alert_manager.hpp:55-63` |
| When a file exists at a skipped file's path, libtorrent writes that file's part of a shared piece into it. | `src/pread_storage.cpp:260-292`, `:500-523` |
| Library lists a torrent's file when it is wanted and every piece from its first to its last is verified. | `app/src/Shared/TorrentSource.cs:411-426` |
| `Completion::Lost` is about dropped alerts, so nothing marks a repair today. | `engine/src/Engine/Alerts.cpp:655-683` |

## Decided against

| Alternative | Reason |
| --- | --- |
| A hold on the whole torrent while the question is open | A person who deletes three watched episodes of a season still seeds the other seventeen, and an unfinished download of the other files goes on overnight. |
| A setting for the follow, and a second place in Settings for gozler's state | The engine uses the gozler that plan 13 brings, and the same drive needs no setting. |
| A look after every check, with `stop_when_ready` | See "Outside the look". |
| Following an unfinished payload | An unfinished file has no record, and its modification time changes while it downloads, so nothing proves it. The person is asked and uses Set download folder, which checks. |
| Following a renamed file or top folder | The engine keeps no name of its own for a torrent's file. Windows finds such a file by its ID, but the engine cannot use it under another name. |
| Protecting another file that the person puts at a lost file's path | The repair of a shared piece writes that piece's bytes into it, as libtorrent does today for every skipped file. Protecting it needs a change in the libtorrent fork and the owner's rebuild of it, for a rare case. |

## What changes in plan 13

- Library asks the engine for a look after its presence check finds a
  torrent's file gone, and reads the engine's answer "data on disk" for a
  torrent's file.
- Plan 13 saves that a torrent-listed location is absent, so that its row
  stays out of Library. Once the engine skips the file, Library's own rule
  for a file that is not wanted removes the row, for every torrent file and
  with discovery off too. When both plans are built, plan 13's saved absence
  has no reader for torrent-listed files and is removed.
- Plan 13's option decides whether Library runs `gozler.exe`. The engine asks
  an installed gozler for the follow to another drive, with no setting of its
  own.

## Not verified

- That the fork's check after a failed write starts only through
  `clear_error`, and that a paused torrent without metadata stays in
  `downloading_metadata`: read in the fork's source by the review, not run.
- The order of a look that the app asked for and the storage failure that the
  same missing files raise at the next read of a running torrent.
- How many follows at one engine start pass the move owner at once; the save
  queue holds 64 changes (`engine/src/Changes.cpp`).
- That `OpenFileById` opens a file in a folder that the engine's account may
  not list, the form of the path under `$Recycle.Bin` on a drive other than
  the system drive, and the path it gives for a file with hard links:
  Microsoft's documentation of the call was read; no run.
- When Windows shows the final modification time of a file that libtorrent
  wrote: not traced. A record that is taken too early costs only a check.
- That Windows keeps the modification time when File Explorer moves a file
  to another drive: Robocopy's documentation says it copies time stamps;
  Microsoft's text for File Explorer was not found. A time that is not kept
  costs the follow, and the person is asked.
- How long gozler builds its index after Windows starts: not measured. The
  one-minute repeat depends on it.
- The dialog's list is not virtualized, as for Remove: its cost for hundreds
  of torrents was not measured.
- The texts and the layout of the bar, the dialog, the row mark and the
  popup: nothing was drawn.
- That the linked libtorrent build holds the source that was read: the
  libraries are dated after the newest edited source file; not proven.
