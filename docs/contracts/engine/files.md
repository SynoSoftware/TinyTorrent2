# Files and recovery

Part of the [engine contract](../engine.md). Read its shared state, edit and
persistence rules before the sections here that the change affects.

## Shared files

Several torrents can use the same files, for example the same content seeded
from two trackers. People who seed on several trackers depend on it. libtorrent
refuses a second torrent with the same info hash; the engine adds no other
ownership check. A
torrent that finds existing files verifies them before using them and downloads
the pieces that do not match, which overwrites those files. The
[Add dialog](../ui/torrents.md#add) therefore names the torrents that already use files
where the new torrent will save, before the person confirms. That is the
incomplete-download folder while one is in use, and otherwise the chosen
destination.

Delete files and Move must not reach the files of a torrent outside the command.
When either runs, compare its full paths, without case as Windows compares names,
with the files of the torrents in the list that the command does not include, so
a command that includes every torrent using a file can delete or move it. Delete
files keeps a file that a torrent outside the command uses, and reports it. Move
refuses when its scope contains such a file, names those torrents, and offers to
move them together. A move of several torrents places a shared file once for
every member that uses it. It refuses a group in which
different files would take one path, or one file two paths, because a member
would lose that file when another member's move takes it. libtorrent permits
[re-addition before work on an old handle has ended](https://www.libtorrent.org/reference-Session.html#remove-torrent()),
so a deletion or move that has not finished stays in this comparison after its
torrent leaves the list.

## Removal and moves

For delete-data, commit membership removal before deleting payload. If that
commit fails, do not delete files. Removal and deletion have separate outcomes:
a failed deletion is notified after the row is gone. The commit that removes
the torrents also saves their deletion as a task, so a deletion that Exit or a
crash cuts off is offered at the next start as an
[unfinished task](#unfinished-tasks); recovery does not restore the removed
torrent.

Ordinary Remove also retains a transient file hold until libtorrent reports
that its disk work has stopped. It keeps payload and partfiles, and removes the
row as soon as membership commits. A later Delete of shared payload waits for
that hold in the existing deletion owner, without delaying the UI. If the release
alert is lost, the same recovery path waits for handle destruction.

Add records the .torrent file a torrent came from, so Removal can delete it
when the person asks. The file holds none of the torrent's data, so its deletion
waits only for the membership commit and uses the payload's deletion mode. A
recorded file that has moved or gone counts as deleted, because the person
wanted it gone.

Permanent deletion and a move's cleanup share `Payload::Delete`. It runs one
worker per logical processor, as rmbrr and FastDelete do, each taking at most
256 files of one folder, so workers in different folders do not wait for each
other. Each file goes in one call with POSIX semantics, which also ignores the
read-only attribute; on FAT, where Windows uses the classic delete, a read-only
file is made writable first. Antivirus scanners and indexers hold files open
for a moment, so a refused delete is tried again for up to 200 ms, as rimraf
does. The Recycle Bin mode uses `IFileOperation`, because only Explorer can
recycle files.

A move takes only the torrent's own files: other files in its folder, such as
added subtitles, stay where they are, and only folders left empty are removed.
The engine moves the files itself, because libtorrent's
[`move_storage`](https://github.com/arvidn/libtorrent/blob/v2.1.2/include/libtorrent/torrent_handle.hpp)
reports no bytes, so the row could show no percentage. Afterwards
`move_storage` with `reset_save_path_unchecked` makes libtorrent take the new
folder without checking the files there. A torrent with a skipped file uses
`dont_replace` instead: libtorrent keeps a part file for the pieces of skipped
files, and only that mode moves it. That mode also checks every file it finds
at the destination, which after the engine's move is every file. The engine
cannot move the part file itself, because libtorrent keeps the part file's
folder in memory. A move never replaces a file, so a file
that another program creates at the destination during the move fails that
torrent's move.

`Payload::Move` chooses the method for each file:

- On one volume, a rename (`MoveFileExW` without `MOVEFILE_COPY_ALLOWED`),
  which takes milliseconds whatever the file's size.
- Across volumes, `CopyFile2`. It keeps alternate data streams such as
  `Zone.Identifier`, uses SMB server-side copy and ODX by itself, and reports
  each chunk, which gives the percentage.
- A copy that runs alone proves its bytes every gibibyte: it flushes the
  file through the handle that `CopyFile2` passes to its progress callback
  and saves the offset in the torrent's marker, so a crash copies at most a
  gibibyte again. A flush took up to 15 ms on a local disk and 50 ms over SMB,
  for a gibibyte that copies in 2 to 9 seconds. `CopyFile2` deletes the file
  it cancels or stops, and its restartable mode does not resume a local copy,
  so Exit parks the copy instead: the callback flushes and proves the offset,
  then waits until the process ends, and the file keeps its bytes. A parallel
  copy is small, so Exit cancels it and the next move copies it again.
- Files of 32 MiB and more between local volumes skip the system cache
  (`COPY_FILE_NO_BUFFERING`), as AngelCOPY's engine does.
- Files under 32 MiB between two volumes without a seek penalty copy on 16
  workers, each taking at most 256 files of one folder, which AngelCOPY
  measured as fastest on NVMe. Every other copy runs alone, because parallel
  copies make a hard disk seek. A volume whose seek penalty is unknown counts as
  a hard disk.
- A sparse file stays sparse where `CopyFile2` supports it (Windows 11 22H2,
  build 22621) and the destination volume supports sparse files. Elsewhere it
  takes its full size, so before it copies, the move refuses a destination
  volume without the space its copies need. An unfinished download therefore
  cannot fill the destination.

The [ring copy research](../../plans/ring-copy-plan.md) keeps the evidence and
the design of a copy engine of the engine's own, which will not be
implemented.

Each member of a move moves on its own, in this order: its files are placed,
libtorrent takes the new folder, the folder is saved with its marker cleared,
and then the sources that a copy left are deleted, with the folders that leaves
empty up to the old save folder. The member leaves Moving when its folder is
saved, so its transfers, or the verification it needs first, continue while
the rest of the group moves. Each complete copy is flushed to the disk before
it takes its final name, and the rename is written through, so the saved
folder never names a copy whose bytes a power loss could still lose. Sources
are deleted only after that save, so a
crash never leaves a saved folder whose files are partly gone. A source that a
later member of the group also uses stays until that member's sources go. A failed cleanup is
logged; it leaves only files that the destination also holds.

A move that Exit or a crash cuts off is an [unfinished task](#unfinished-tasks)
at the next start. Each saved destination records how far its move got. It is
staged until the preflight passes, and placing once that is saved, before any
file changes. A continued move decides each file from the disk, because the
engine does not know where it stopped: a file only at the destination was
renamed and counts as moved; a file only at the source moves now. A copy has
its file's name with `.!ttcopy` added until it is complete, so a file with that
name is an unfinished copy. A move that continues a placing move to the same
destination continues that copy; every other move deletes it. A file at the
destination's name beside a source that holds data stays as the interrupted
move's complete copy when its size and last write time match the source, which
`CopyFile2` keeps and the paused torrent does not change. Any other such file
can be someone else's, so the move refuses it and never deletes it. Choosing a
checked destination through Move does the same. A FAT volume keeps a time to 2
seconds, in local time, so a copy there matches its source's time within 2
seconds, or one hour after a daylight saving change.

A continued copy ends byte for byte equal to its source, and copies only what
it lacks. It compares the 4 MiB before its saved proof with the source, and
when they match it copies from the proof on, because a crash can leave the
bytes before a flush on disk while later cached writes are lost. Without a
proof, or when those bytes differ, it compares the whole unfinished copy with
the source in 1 MiB blocks and copies only the blocks that differ, which is
cheaper than copying again where reading is faster than writing. It copies
with its own loop of reads and writes, proves its bytes every gibibyte as
`CopyFile2` does, makes the holes of a sparse source holes again, and then
gives the copy the source's size, alternate data streams, last write time and
attributes and flushes it before it takes its final name. Comparing counts as
progress, so a long comparison is not taken for a drive that stopped
responding, as [drive turns](#drive-turns) describe. A
source that is gone while its
unfinished copy exists fails the move, because the copy may hold the only
bytes and cannot be checked; the torrent keeps its marker.

Busy files and busy drives delay a continued move, and the torrent shows
Waiting to move while it waits. Another torrent that still acquires metadata
also delays it, and the torrent shows that reason with the other torrent's
name, as a move to the final folder does. Any other refusal shows on the
torrent and waits for the person.

A failed copy deletes the copies and folders it made and renames files back.
The member reports Move failed, its new marker clears, and the rest of the
group still moves. If a rename back fails, the files are split between the two
folders, so the member keeps its marker and reports Move interrupted. The same
applies to a member that shares a file an earlier member placed, because on
one volume that file has left its old folder, and to a member with files
already at the destination, which stay there. A member that recovers an
interrupted move keeps its marker after any failure. A failed save, or a
libtorrent failure after the files are placed, also keeps the marker. A
waiting member that shares a file with a failed member fails with it and
reports the same problem, because moving it would take that file from the
failed member's folder.

Start, Stop and Verify clear a move problem that left no marker, because the
torrent's files are where they were and the person has answered it. A move
problem with a marker stays, because the files can be in two folders. Other
commands, such as a speed limit or the queue order, keep the problem, because
they do not answer it.

The move preflight checks both physical and final names at source and destination.
When the source is absent or an empty placeholder and the destination exists,
it counts that file as already moved. A source containing data and an existing
destination remain a collision; choosing the current save folder is allowed.
A destination that is the source file under another path, such as through a
link, stops the move with `same_location`, because moving the file onto itself
would delete the only copy as its source.
An empty unfinished placeholder is removed after preflight so it cannot mask a
completed destination file. Files already at the destination are not placed
again.

A moved torrent is verified at its new folder before transfer resumes only
when the move found files of it there, or when the move recovers an
interrupted move, whose files can be in two folders. So a person who moved
files or whose drive letter changed needs only to choose their folder, and
pieces that do not match are downloaded again over those files. The files a
move places itself hold the pieces the torrent already verified, so checking
them again would only make the person wait. A torrent with a skipped file is
still checked, because only that check moves libtorrent's part file.

Moves that share no drive run at the same time, each with its own copy worker,
and [drive turns](#drive-turns) decide when each one starts. Renames of
finished files, restoring and Exit wait until no move runs. A payload
operation's source and destination paths stay held until its disk work has a
known outcome.

**Owner ruling:** Adding a torrent never waits for, and is never refused
because of, another torrent's file work. A person adding a torrent wants it in
the list now; a rename, move or deletion of other files is not their problem.
The addition does its disk work on its own worker. While another torrent's
files are being renamed or removed, the new torrent joins the list without
choosing its file names and stays paused, writing nothing, until that work ends
and its names are chosen, as a magnet's are once its metadata arrives. A move
that places a file where a new torrent writes the same name fails for that
member and keeps both files, which only happens when two torrents share a file
name in one folder.

Another torrent's file work delays a move only where it can reach the move's
files. A pending addition, a deletion, a rename, or names still being read
delay it when that torrent's save folder is the move's destination or a
member's save folder, lies inside one or contains one: a torrent's files lie
under its save folder, and the shared-files refusal reads the names of the
torrents in the members' folders. An outside torrent still acquiring metadata
delays it only when its save folder overlaps the destination, because its
unknown files could only be written there. The moving torrents' own busy files
and the shared-files refusal always apply. The rows of a move that waits for
such work name the torrent whose files it waits for. Other torrents and
settings remain usable.

**Owner ruling:** No command is refused because files are busy, and the person
never has to try again. Waiting for other file work is the engine's job, not a
problem for the person. Each command replies once its choice is saved:

- Pause, Resume, Force start, Reorder, speed limits, download order, trackers
  and Reannounce change no files and apply at once. A torrent whose own files
  are busy stays paused until they are free, then follows its choice.
- Verify, a change of the wanted files, and Remove that keeps the files wait
  for the torrent's own busy files and then apply. A removed torrent leaves the
  list at once, as a deleted one does.
- A Move that cannot start yet waits its turn, and its torrents show Waiting
  to move. Busy files, busy drives and another torrent's missing metadata only
  delay it; the last shows on the torrents. Any other refusal at its start shows on the torrents.
  A waiting move is saved as an [unfinished task](#unfinished-tasks). A later
  Move replaces a waiting one, and a waiting move keeps the move to the final
  folder from starting.
- The Add dialog does not wait for other torrents' file names. Until they are
  read, it compares only their final names, so a file renamed on disk can be
  missed for that moment.

**Owner ruling:** Delete files never waits for other work and never refuses
because files are busy. A person who deletes a torrent wants it gone, not a
problem to solve. A deletion runs beside a move, a rename or another deletion,
and does not wait for torrents without metadata, which have no files a
deletion could reach. A torrent still waiting for its turn in a move leaves
the move, and its files are deleted where they are. A torrent whose own files
libtorrent is moving or renaming leaves the list at once and is removed when
that disk work ends, because libtorrent cannot stop it halfway. While an
addition runs, deleted torrents also leave the list at once and are removed
when it ends: the addition is not in the list yet, so nothing else shows
which of its files a deletion would reach. Files that another torrent uses
are still kept. Such a torrent also leaves the saved list before Delete
replies, so it does not return after a restart; if the engine stops before
removing it, its files stay on disk.

Remove, like Delete files, takes a torrent out of the list, the saved list and
every command at once, even while its own files are busy. The engine gives it
to libtorrent for removal when its own move, rename, name preparation, file
name check or Zone.Identifier write ends. Delete files also waits until
another torrent's file names are read, because only then can it find the files
other torrents use. Until the engine gives it to libtorrent, a preview or Add of
the same content is refused with `torrent_removing`, because libtorrent cannot
hold two copies of one torrent. After that the content can be added again, and
the Add dialog names a deletion that still holds files at the destination. A
waiting removal does not stop other torrents from preparing their names or
finishing their renames: the deletion compares every other torrent's names when
it starts, so a shared file is still kept.

Before moving or renaming shared files, pause every owner and wait for each
storage's disk-release callback on the worker that changes the files. A paused flag or an
untagged flush alert cannot establish that those owners have stopped using the
files. `ReleaseFiles` contains the pinned libtorrent native-interface access:
it posts onto the network thread and retains the torrent until its disk callback
completes, so the engine thread remains free while the worker waits.

For a group with partially overlapping file lists, check the whole destination
before moving anything. A member skips the files that an earlier member of the
group has already placed. A member that needs verification is verified before
its saved intent resumes.
Two distinct source files cannot map to one destination path.
A member removed during preflight can leave a destination conflict in that
check; the person retries the surviving group instead of the engine rechecking it.

Before a move starts, save its destination with the torrent and clear it when
that torrent's own move ends. Reserve its paths before that commit,
so an addition cannot enter while the write is pending. If no disk move began,
a failed destination preflight clears the newly staged markers through the
same writer. Choosing a location through Move also resolves an interrupted move;
a failed preflight retains its recovery marker. Delete
files deletes only in the save folder, never at an interrupted destination,
because a held destination is not proof that existing files there belong to
the torrent.
Until its move continues or the person chooses a folder, that saved
destination keeps the torrent paused with a Move interrupted error, so it does
not download again into the old folder. While the answer about
[unfinished tasks](#unfinished-tasks) waits, the error is `move_unfinished`
instead, which points to that question. Moving it to the folder that holds the
files offers Use the files there, and verification establishes what is there.
The user's running or paused intent does not change.

Lost move alerts leave the operation pending with its paths held. Request a
flush to obtain another acknowledgement after the disk work, then resolve it
through the ordinary move outcome path. Alerts can arrive late, including during
a later move of the same torrent, so that path checks that libtorrent is no
longer moving storage and observes its actual save path. That observation runs
on the payload worker with the current member and paths still held;
a failed observation requests another acknowledgement and keeps those holds.
Files found at the requested location still require verification, including
Use the files there in the same folder; location alone never establishes valid
content. A deletion whose release alert
was lost waits for its removed handle to expire, because libtorrent retains the
torrent until its disk work ends. Both operations continue during shutdown;
missing evidence never releases their files or implies success.

Addition recovery observes live handles and move locations in one network-thread
collection from the payload worker. It resumes only the same addition in the
same phase, so a delayed observation cannot advance an addition already saved
or abandoned. A move still in progress keeps its ordinary completion path.

A completed move requires verification after restart until a safe
destination checkpoint has committed. Restoring discards old piece claims and
seed mode when that verification is pending or the checkpoint names an older
path. Clear this requirement only after the saved destination checkpoint claims
no pieces that the current checked torrent lacks, and checking has ended. This
also covers Use the files there in the same folder: matching names and sizes do
not establish that the bytes match the torrent.

## Unfinished tasks

The engine saves its disk work as tasks in `settings.json`, in the order it
runs them, so that the next start finds the work that Exit, the end at 10
seconds or a crash left unfinished. Every saved file is written whole and then
renamed into place, so a cut leaves the old or the new file, never part of
one. The tasks are:

- A running move, and its members that wait, with the proof of its current
  copy in the torrent's marker. A marker that no task holds, such as one
  saved before a crash and before its task, is a running move too. A move
  that failed by itself and kept its marker is no task: it shows Move failed
  until the person acts, because continuing it on its own would repeat the
  failure.
- The old files of a moved torrent whose new folder is saved but whose old
  files are not deleted yet.
- A check that runs or waits for its drive, and a move that waits for its
  turn, in the order of their turns. A move to the final folder that waits for
  its turn is one of these.
- A Verify that waits for the torrent's files.
- The files of removed torrents that a deletion has not deleted yet, from the
  commit that removes the torrents. A removal that waits until libtorrent can
  take its torrents saves its files only once every torrent's names are read,
  because only then does it know which files other torrents keep.

The engine saves the tasks again whenever they change, together with any
other change to `settings.json`, and once more at Exit, before it cancels
anything. Renames to final names are not tasks: they follow from each
torrent's state at every start.

At start, the `unfinished_tasks` setting decides: `ask`, the default,
`continue` or `abandon`. With `ask`, the engine keeps the tasks and reports
how many torrents' moves, checks and deletions wait; the window asks once for
all of them. Until the answer, the work waits and holds no drive: a moved
torrent stays where it was and shows `move_unfinished`, a waiting move shows
Waiting to move, a check stays paused and shows Waiting to check, and a move to
the final folder does not start. Without a window, a notice says that the work
waits and opens the window when selected. With `continue`, work that the
engine continued at start without asking and that a crash cut off again is
asked for instead, because work that crashes the engine would otherwise run
again at every start. The save at Exit clears that record.

Continue runs the work in the saved order:

- A move waits for its turn again, keeps its complete copies and continues an
  unfinished copy byte for byte, as [moves](#removal-and-moves) describe. A
  moved torrent is verified at its new folder, because its files were in two
  folders.
- A check runs again from the start as a Verify, because a check that
  stopped keeps no position. Checks take their turns as they start, after the
  continued moves.
- A deletion deletes the files that remain. The old files of a moved torrent
  are deleted the same way.

Abandon drops the work:

- A move gives back what it placed. Each unfinished copy and each complete
  copy beside its source is deleted, a file the move renamed is renamed back,
  folders it left empty go, and the marker clears, so the torrent stays where
  it was. It leaves the files that its preflight found at the destination,
  which are the person's own, and every file that another torrent uses there,
  such as one that an earlier member of the same move placed. A staged move
  changed no file, so only its marker clears. Giving back takes its drives
  as a move does and gives up as a move does when it stops making progress.
  When the destination does not answer, nothing changes, and the marker and
  the task stay, so the next start asks again. A move whose files cannot be
  given back keeps its marker and shows Move interrupted.
- A move to the final folder that had not started clears the final folder, so
  it does not start again when the download finishes.
- A check leaves its torrent stopped with its check pending: the torrent
  checks its files once the person starts it.
- A deletion leaves its remaining files on disk.
- A move cut after its new folder was saved, while it deleted the old files,
  is complete at its destination. Abandon leaves the old files there, and
  Continue deletes them.

## Drive turns

Disk jobs on different physical drives run at the same time, and jobs on one
drive take turns, so two jobs never slow each other down on one disk. A job is
a check of a torrent's files or a move. A check uses the drive of its
torrent's folder. A move uses the drive of its destination and of each torrent
it moves. Checks and moves follow one rule, and neither kind goes first.

- A drive does one job at a time, and nobody takes it from a running job. A
  job keeps its drives until it finishes, fails or stalls; a check also ends
  when the person stops its torrent or libtorrent stops it with a torrent
  error. A move keeps its source drives until it
  ends, also while it deletes the sources.
- A job starts only when all its drives are free. A waiting job holds and
  reserves no drive, so no job waits for a job that cannot start yet, and two
  moves never each hold one drive and wait for the other's.
- A free drive goes to the first waiting job whose drives are all free. Waiting
  jobs go in turn order: forced torrents' checks first, then the others. A job
  takes its turn when it starts to wait, and a waiting check takes a new one
  when the person forces it, so a later force goes after an earlier one. A
  later job may use a drive that an earlier waiting job also needs, so a move
  that needs two drives can be overtaken by checks on one of them; it starts
  when both are free.
- Pause all stops every check, and their drives are free while it lasts. Each
  check keeps its place: after Pause all, a check that ran continues first, or
  waits in its place for a move that started on its drive meanwhile. A
  person's Stop and then Start puts a check at the back.
- A running job whose progress does not change for 2 minutes gives up with
  `drive_stalled`. A stalled check stops its torrent until Start or Verify.
  Verify of a stalled check whose reads remain lets that check continue,
  where it can stall again, and rechecks once the reads return. A
  stalled move's copy is cancelled, and the move fails as after any failed
  copy, which gives back what it placed and shows Move failed. Nothing
  restarts either one. Only a move's placement is timed, not the wait for
  libtorrent to release the files.

The once-a-second maintenance pass decides. It works out which drives are in
use from the jobs that run, on every pass, and never stores that as a loan a
job must give back, because a job ends in many ways and a forgotten return
would block its drive until the engine restarts. The only stored fact is each
job's turn number. libtorrent starts every check, and the pass holds a check
whose drive is busy by pausing its torrent, so such a check runs for up to one
second first and continues from its piece when it is released. A Move the
person asks for starts at once when its drives are free and otherwise waits
its turn. A move to the final folder waits its turn in the same way. A move
that continues after a restart waits for its drives too, and shows Waiting to
move meanwhile; one destination's group that cannot start does not hold back
another's.

A drive is freed only when its job has really stopped. A copy or a read stuck
inside a Windows call keeps its drive until Windows returns. A check that
stops, by Stop, Pause all or the drive rule, keeps its drive until libtorrent
reports that it has no read left (`torrent_paused_alert`).

libtorrent hashes every check on one shared queue of threads. Checking threads
is multiplied by the number of checks that run or still read, but each check
keeps at least twice as many reads queued as there are threads, so a check on
a slow drive slows a check on a fast drive to its pace. A read that never
returns stops every check, so while a stalled check's reads remain, only that
check gives up with `drive_stalled`, and every other check shows Waiting to
check for the stalled check's drive. Their queued reads touch no drive, so
they claim none, and moves on their drives go on.

`Drives::Of` names the drives of a folder after following the links in its
path, so `C:\temp`, a link to `V:\temp`, has V:'s drives:

- A local volume: the disks its extents lie on, and for a virtual disk such as
  a VHDX also the disks of the volume that holds its file, because its work
  reaches that disk.
- A network folder: its server, because the engine cannot see the server's
  disks.
- A folder that cannot be identified: a drive of its own, so its jobs run
  beside the others.

A folder that does not exist uses its nearest existing parent. The lookup runs
on a worker of its letter or server, because it can stall on a network share,
and its answer is kept until the engine exits. A job whose drives are not known yet waits. A
storage pool or a hardware RAID reports one disk, so its jobs take turns.

## Unfinished files

New torrents use the `.!tt` suffix for incomplete files by default, such as
`movie.mkv.!tt`, so other programs can distinguish unfinished downloads.
Transfers settings can turn it off for new torrents. Each torrent retains its
choice, so changing the default does not rename an existing library.

- When the suffix is enabled, each file that does not exist at the destination
  gets its suffixed name through `add_torrent_params::renamed_files`. A file
  that already exists there keeps its name, so verification finds it instead of
  downloading it again.
- When a file completes, Windows gives it its real name in the same folder,
  without replacing an existing file, then `rename_file` updates libtorrent's
  mapping. Finishing is always a rename and never a copy. If another program
  has the file open, Windows refuses the rename; the file keeps its suffix and
  the engine tries again later, without reporting a download error.
- Before that rename, the engine pauses the torrent and waits for libtorrent
  to write the data it still holds in memory, without holding any files busy.
  Releasing the files for the rename waits for that write, which takes
  minutes on a slow network drive, and every other torrent's file work would
  otherwise wait with it.
- libtorrent saves the current names in the resume data, so they survive a
  restart.
- After a lost rename result, release that storage on the payload worker and
  read its current name mapping. Advance only when that observation proves the
  target name; otherwise retry the mapping change. A failed observation keeps
  the owners paused and retries later, because the physical rename may already
  have succeeded.
- The shared-files comparison uses the torrent's original name and its actual
  disk name, so a file is the same file before and after it finishes. It does
  not strip suffixes from unrelated names that happen to end in `.!tt`.
- Open on a file that is still downloading starts the program registered for
  its real extension, through `ShellExecuteEx` with `SEE_MASK_CLASSNAME`, so
  watching a video during a sequential download still works in players that
  read the content.

An optional incomplete-download folder applies to new torrents. Each torrent
retains its chosen final folder separately from its current save path. After
its wanted files finish and their names settle, the owner of moves
takes them to that final folder before notifying completion. Collisions and
interrupted moves use the same refusal and recovery as a manual move. A refused
move shows its reason on the torrent.
While another torrent whose save folder overlaps the final folder still
acquires metadata, the torrent shows that reason
with the other torrent's name, and the move starts once the metadata arrives,
so one magnet that never gets metadata cannot hold every finished torrent back
without a sign. A successful manual move supersedes the final folder.
