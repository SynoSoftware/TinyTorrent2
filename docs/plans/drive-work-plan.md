# One disk job per drive

**Feature: nice to have.** Probably won't do if it proves too complicated.
Nothing here is built. The [engine contract](../contracts/engine.md#removal-and-moves)
owns how moves and checks work now: one move at a time, and as many checks at
a time as Advanced's Maximum simultaneous checks allows, whatever the drives.

The first part of this document is the specification: what the person sees.
The second part is the implementation plan.

## Specification

### What the person gets

Disk work on different physical drives runs at the same time. Work on one
drive takes turns, so two jobs never slow each other down on one disk.

Example on the owner's laptop. C: and V: are one drive, disk 0, because V: is
the virtual disk `C:\dev.vhdx`. D: and E: are USB drives. Y: is a network share
on the server `dw`.

| Work | Today | With this feature |
|---|---|---|
| Verify a torrent on C: and one on Y: | One waits for the other | Both run |
| Verify a torrent on C: and one on V: | One waits for the other | One waits for the other: same drive |
| Move from C: to Y: while a torrent on C: is checked | Both run and slow each other down | The move waits for the check |
| Move from C: to Y:, then a move from D: to E: | The second waits | Both run |
| Move from C: to Y:, then a move from D: to C: | The second waits | The second waits: C: is busy |

On a machine with one disk the person sees no change.

### The rules

A job is a check of a torrent's files or a move. A check uses the drive of its
torrent's folder. A move uses the drive of its destination and the drive of
each torrent that it moves.

1. **A drive does one job at a time.** Nobody takes a drive away from a running
   job. A job keeps its drives until it finishes, fails or stalls. A check also
   ends when the person stops its torrent. A move has no Cancel command yet.
2. **All drives or none.** A job starts when all its drives are free. A waiting
   job holds no drive, so two moves can never each hold one drive and wait
   for the other's.
3. **A free drive goes to the first waiting job whose drives are all free.**
   Waiting jobs go in turn order, forced checks first. A waiting job reserves
   nothing, so a later job may use a drive that an earlier waiting job also
   needs, and no job waits for a job that cannot start yet. The accepted cost:
   a move that needs two drives can be overtaken by checks on one of them, and
   it starts when both are free.
4. **A drive is free when no running job uses it.** A waiting job, a stopped
   torrent and a finished job hold nothing. Pause all stops every check, so
   their drives are free while it lasts.
5. **A stalled job gives up.** A running job whose progress does not change for
   2 minutes is stopped. A stalled check stops its torrent with the error "The
   drive stopped responding". A stalled move fails with the same reason, and
   its copies are deleted as for any failed move. Nothing restarts either one;
   the person does.
6. **A forced torrent's check goes before every waiting job that is not
   forced.** It still waits for the running jobs on its drives, because nobody
   takes a drive away. Forced checks keep the order in which they were forced,
   so a later force never goes before an earlier one.
7. The person never retries. A waiting job starts by itself.

### What the person sees

- A running job shows Checking or Moving files with its progress, as now.
- A waiting job shows **Waiting to check** or **Waiting to move**, with the
  same icon as Checking or Moving files. A state is named by what ends it: this
  one ends by itself when the drives are free, so the person has nothing to do.
  Checking and Moving files would claim that work runs, and Queued already
  means the download queue.
- The status tooltip of a waiting job says what it waits for, such as "Waiting
  for C:, used by Ubuntu 24.04". The drive is named by the letter or server of
  the waiting job's own folder, because the person does not know disk numbers.
- The Queued filter includes waiting jobs, because it shows torrents that wait
  for their turn.
- The status bar's verify and move rates count only running jobs, as now.
- No queue position is shown. Each drive has its own order, so one number
  would be wrong for a move that waits for two drives. The tooltip says why the
  job waits.

A drive is freed only when its job has really stopped. A copy or a read that is
stuck inside a Windows call keeps its drive until Windows returns, because a
thread that may still write must not share its folder with a new job. The
engine cannot force a thread out of a Windows call.

### Drive identity

The engine finds the drives of a folder once per folder and keeps the answer
until the engine exits. It follows links in the folder's path first, so
`c:\temp`, a link to `V:\temp`, has V:'s drive.

- A local volume: the disks that its extents lie on
  (`IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS`). A volume that spans several disks
  uses all of them.
- A virtual disk, such as a VHDX: also the drives of the volume that holds its
  file (`GetStorageDependencyInformation`), because its work reaches that disk.
- A network path: its server, because the engine cannot see the server's disks.
- A folder the engine cannot identify: a drive of its own.

A probe on the owner's laptop on 2026-10-19, without administrator rights,
returned disk 0 for C:, disk 1 for V: with its host volume and `\dev.vhdx`,
and disks 2 and 3 for D: and E:.

### Checking threads

libtorrent reads and hashes a check's pieces on its checking threads. With the
default of one thread, two checks on different drives would share that thread
and still take turns. Checking threads therefore becomes a number per running
check: the engine runs that many threads for each check that runs. The
default stays 1.

### Settings

The drive rule replaces Maximum simultaneous checks. That Advanced setting is
removed, and a saved value is ignored.

### Owner decisions

These were decided during implementation. They override any older text in
this document.

1. **Nothing can hang us.** A drive is held only by a job that is doing I/O
   on it, or that still has I/O outstanding in Windows or libtorrent. Every
   wait has a named cause, shown in the UI, that ends by itself in bounded
   time or by the person's action. Nothing hung on one drive makes work on
   another drive, the UI or Exit wait.
2. **No reservations.** A waiting job never holds or reserves a drive. A free
   drive goes to the first waiting job, forced checks first and then by turn,
   whose drives are all free now.
3. **Shared libtorrent check queue: accepted.** Checks stay parallel per drive.
   While they run together, a slow drive slows a fast drive's check. While a
   stalled check's reads remain, the other checks wait and say why.
4. **Magnets without metadata** block a move only when their folder is the
   move's destination, lies inside it, or contains it.
5. **Other torrents' busy file work** (additions, deletions, renames, name
   preparation) delays a move only when its folders overlap the move's.
6. **Renames and restoring wait for running moves**, as before. Exit cancels
   them instead (decision 8).
7. **Pause all keeps each check's place.** A person's Stop and then Start puts
   the check at the back of the queue.
8. **Exit gets 10 seconds, hard.** Exit cancels the work in flight at once,
   saves, and ends. At 10 seconds after Exit the process is killed no matter
   what. The kill is only the watchdog: a normal Exit ends well before it, and
   the kill firing during a normal Exit is a defect. Every saved file is
   written atomically.
9. **Unfinished tasks at start.** The whole queue of disk work at Exit, the
   kill or a crash counts: running and waiting moves, checks, pending
   Verifies, deletions and other queued file work. A setting with the values
   Ask (default), Continue and Abandon decides what happens to it. With Ask,
   one dialog at start lists the waiting work, with the buttons Continue and
   Abandon and the existing "don't show again" footer of `Dialog`. When the box
   is ticked, the caller saves the pressed choice to the setting. Until the
   person answers, the tasks wait and hold no drive.
   - **Continue:** moves resume from where they stopped, keeping complete
     copies and continuing partial `.!ttcopy` files byte-exactly: from the
     last flushed proof once the 4 MiB before it match the source, otherwise
     by comparing the partial with the source and copying only the blocks
     that differ. Checks run again from the start, because a stopped check
     keeps no position, and deletions finish.
   - **Abandon:** a move deletes what it copied, and its torrent stays where
     it was. A check leaves its torrent stopped, with the check pending. A
     deletion leaves its remaining files on disk.
10. **A move that fails by itself** (stall or error) keeps today's undo and
    shows Move failed.
11. **Cancel move in the UI is not built yet.** When it is built, it cancels
    the move and deletes what was copied.

## Implementation plan

The engine already decides waiting work in its once-a-second maintenance pass:
`MoveWaiting` starts a waiting move, and `ApplyWaiting` starts a waiting
Verify. The drive rule is one more step of that pass.

**Which drives are in use is worked out again on every pass, from the jobs that
run.** It is never stored as a loan that a job must give back. A check or a
move can end in many ways: finished, failed, refused, stopped, removed,
restarted, Exit. A stored loan that one of those paths forgot to return would
block its drive until the engine restarts. A job that no longer runs simply
uses no drive on the next pass. The only stored fact is the order of waiting
jobs.

Checks and moves are built together, as one kind of job under one rule.
Neither kind goes first. In total: about 550 lines of engine code, about 30
lines removed from and 40 added to the app, and one new engine check.

**1. Drive identity.** New `engine/inc/Drives.h` and `engine/src/Drives.cpp`,
added to `Engine.vcxproj`. One function takes a folder and returns its drives as
a named type: a set of drive names such as disk 0 or server `dw`.

- Open the folder with `FILE_FLAG_BACKUP_SEMANTICS` and call
  `GetFinalPathNameByHandleW`. This follows links. `VOLUME_NAME_GUID` gives the
  volume of a local folder, and `VOLUME_NAME_DOS` gives `\\?\UNC\server\...`
  for a network folder, also through a mapped drive letter.
- For a local volume, query the extents, then the storage dependency. For a
  virtual disk, add the drives of its host volume, found by the same function.
- For a folder that does not exist, use its nearest existing parent.
- Any failure gives a drive named by the folder.
- Link `virtdisk.lib`.

The lookup can stall on a network share, so it runs on a new `Store` worker in
`State.h`, as previews use `sources`. The answers are kept in a map from folder
to drives on `Engine::State`. A job whose drives are not known yet waits for
the answer.

**2. Several moves.** About 150 changed lines, almost all in
`engine/src/Engine/Move.cpp`. The move already keeps its state in its own
`Move` record, and each torrent saves its own recovery marker. Only the
engine's single `std::optional<Move> move` changes:

- It becomes a list of moves. The functions in `Move.cpp` receive the move
  they serve instead of reading the one member. libtorrent's storage-moved
  message finds the move that holds its torrent.
- Each move gets its own `Store` worker in place of the shared `copies`, so
  one move's copy never waits behind another's.
- Outside `Move.cpp`: the snapshot's move rate is the sum of all moves, the
  list of torrents that hold files loops over all moves, and `FilesBusy` is
  true while any move runs. Renames, deletions, additions, restoring and Exit
  therefore wait for all moves, as they wait for the one move now.
- Moves interrupted by a crash continue one at a time after restart, as now.

Two moves never share a drive, so they never share a folder or a file. That
rule replaces the conflict rules a parallel copier would otherwise need. The
one gap is a place reached by two names that the engine cannot match, such as
a server by name and by IP address. There, a clash fails the move instead of
losing data, because the placement never replaces a file: copies use
`COPY_FILE_FAIL_IF_EXISTS` and renames use `MoveFileExW` without
`MOVEFILE_REPLACE_EXISTING`.

**3. The waiting order.** One counter on `Engine::State` gives each job a turn
number when it starts to wait.

- A waiting check: a new `Torrent` member holds its turn number, present only
  while the engine holds that check. `Torrent::IsHeld` includes it, so
  `ApplyIntent` pauses the torrent and unsets auto-managed, as for a moving
  torrent, and resumes it when the member is cleared. `Classify` already shows
  Checking for it, and `SampleChecks` already skips it.
- A waiting move: `Torrent::waitingMove` gains the turn number beside its
  destination. `MoveWaiting` today takes the first waiting torrent by ID; it
  takes the lowest turn number instead.

**4. The drive pass.** A new `Engine::State` member function in a new
`engine/src/Engine/Drives.cpp`, called from `Maintain` in place of
`MoveWaiting`, which it absorbs. `Session.cpp` is already large, so the pass
does not go there.

1. In-use drives: the drives of every check that libtorrent runs, and of every
   running move.
2. A check that runs on a drive that another running job also uses is held
   and given a turn number. This happens when libtorrent starts two checks on
   one drive in the second before the pass. The first check found keeps the
   drive.
3. Waiting jobs, checks and moves together, go in turn order, with forced
   torrents' checks first. Forcing a waiting torrent gives it a new turn
   number, so it goes behind the checks that were forced before it. The
   forced group is read from the torrent's intent on each pass. A job whose
   drives are all free starts: a check is released, a move goes to
   `StartMove`. A job that does not start marks nothing.
4. A torrent that has left `checking_files`, or that the person stopped, loses
   its turn number. Each torrent whose hold changed gets `ApplyIntent`.

The next job starts up to one second after a drive becomes free. A check that
libtorrent starts on a busy drive runs for up to one second before it is held.
Neither loses work, because a paused check continues from its piece.

A move the person asks for follows the same rule. `FilesReady` refuses it as
busy when its drives are in use, taken by an earlier waiting job that starts
at the next pass, or not known yet, and the existing path for a busy move makes it a waiting move. A move
whose drives are free starts at once, as now.

**5. The stall rule.** The same pass keeps each running job's progress from
the previous pass and the time it last changed.

- A check unchanged for 2 minutes: the engine stops the torrent and sets a
  check error beside `hashError`, which Start or Verify clears.
- A move whose copied bytes are unchanged for 2 minutes while it copies: the
  engine sets a new `stop` flag in `Payload::Progress`. The copy callback
  already returns `COPYFILE2_PROGRESS_CANCEL` when the placement's own stop
  flag is set, after another file's copy failed; it checks the new flag too.
  The existing failure path then deletes the copies and reports the move
  failed. Other move phases, such as waiting for libtorrent to release the
  files, are not timed. The problem kind is new, with its text in both
  `en.json` files.

**6. libtorrent's limits.** In `Policy.cpp`, set `active_checking` to the
largest `int`, so that libtorrent starts every auto-managed check and the pass
decides. libtorrent pauses every check when the limit is 0 or less. Set
`hashing_threads` to Checking threads multiplied by the number of running
checks, counting at least one. The count becomes an input of the policy, and
the pass refreshes the policy when it changes.

**7. Remove Maximum simultaneous checks.**

- Engine: `activeChecking` in both structs in `engine/inc/Engine/State.h`, the
  `visit` in `engine/src/Engine/Settings.cpp`, and the field in `Policy.cpp`.
  `Settings::Read` visits only known keys, so a saved value is ignored.
- App: `ActiveChecking` in `app/src/Settings/Editor.cs`, its control in
  `SettingsPage.xaml`, and its part of `CheckingSummary`.
- `en.json`: remove `active_checking`, `active_checking_hint`,
  `checking_count_one` and `checking_count_other`. Change
  `hashing_threads_hint` to say that the number is per running check. Other
  catalogues are refreshed at release, under the owner ruling.
- Raise the pipe protocol version from 24 to 25 in `engine/inc/Pipe.h`,
  `app/src/Shared/Engine/PipeClient.cs` and `engine/tests/Engine.ps1`, because
  the settings message loses a field.

**8. The waiting statuses.**

- Engine: `Status` gains a waiting check and a waiting move, sent as
  `waiting_check` and `waiting_move`. `Torrent::Classify` returns them for a
  check that the engine holds for its drive and for a `waitingMove`. A move
  that continues after a restart stays Moving. The snapshot row of a waiting
  job also carries the drive it waits for and the ID of the torrent whose job
  uses that drive; both are empty while the drive lookup has not answered.
- App: `app/src/Torrents/Torrent.cs` gives the two statuses the Checking and
  Moving icons and the tooltip. `Torrent.cs` and `app/src/Shared/TorrentSource.cs`
  test for `checking` and moving today; a waiting check and a waiting move
  count the same there. The Queued filter includes both.
- `en.json`: `status.waiting_check` "Waiting to check", `status.waiting_move`
  "Waiting to move", and the tooltip text.
- The new statuses change the snapshot, under the same protocol version 25.

**9. Contracts.** `docs/contracts/engine.md` gets the rules, several moves
and the checking-threads change. `docs/contracts/pipe-api.md` gets the new
version, the removed key, the statuses and the waiting fields.
`docs/contracts/ui.md` gets the statuses, the tooltip and the Queued filter.

### Verification

A new engine check, `DriveTurns`, in `engine/tests/Checks.ps1`. It reuses
MoveAndVerify's sparse fixture and its `-Folder` and `-Network` parameters, and
V: for the second local folder, as MoveAndVerify does.

| Case | Expected |
|---|---|
| Verify a torrent on C: and one on Y: | Both progress values rise in the same seconds |
| Verify a torrent on C: and one on V: | One stays still while the other rises; both finish |
| Verify one in `c:\temp` (a link to V:) and one on V: | They take turns |
| Start a move from C: to Y: while a torrent on C: is checked | The move waits until the check ends |
| Verify on C:, then move C: to Y:, then Verify another on C: | They run in that order |
| Stop a waiting check | It stays stopped after the other check ends |
| While a check runs on C:, Verify two more on C:, then force the second | The forced one runs next, after the running check ends |
| While a check runs on C:, Verify A, B and C on C:, force C, then force A | C runs next, then A, then B |
| A check waits for C: | Its row reports `waiting_check`, drive C: and the running torrent's ID |
| The settings command with `active_checking` | Refused, as an unknown key |
| Move C: to Y: and D: to E: | Both copy in the same seconds |
| Move C: to Y:, then D: to C: | The second waits for the first |
| Move D: to E: while a torrent on Y: is checked | Both run |

MoveAndVerify and FilesSafety must still pass, because they cover moves, a
Verify during a move, and the rates.

The stall rule has no automated case, because no test can make a drive hang on
demand without a test-only hook in the engine. It is checked by reading the
code, and the move half by the existing path that a failed copy already takes.

### Risks

- A storage pool or hardware RAID reports its virtual disk, not its members, so
  the engine treats it as one drive. Its work then takes turns, as today.
- If the drive lookup cannot open a volume on another machine, that folder gets
  a drive of its own, so its jobs run beside the others.
- A job that is slow but not stalled, such as a check of a large piece over a
  slow share, must still change its progress within 2 minutes. A piece of
  16 MiB at 1 MB/s takes 16 seconds.
- Exit saves every torrent a final time. A large library on a slow disk can
  take most of the 10 seconds for that save; when the end at 10 seconds cuts
  it, the next start recovers as after a crash, from the tasks and the last
  saved state.
