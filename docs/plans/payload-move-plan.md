# Payload move with progress

Design plan, 2026-10-18. Not started. The [engine](../contracts/engine.md#removal-and-moves)
and [pipe API](../contracts/pipe-api.md) contracts own current behavior; update
them alongside implementation, then delete this plan.

## Goal

**A person who moves torrents sees each torrent's own move percentage in its
row and in the Inspector. Each torrent returns to its normal state as soon as
its own files are moved, without waiting for the rest of the group. Every copy
and every deletion uses the fastest known method for its disks.**

Today the engine calls libtorrent's `move_storage`. libtorrent reports only
that a move runs (`torrent_status::moving_storage`) and that it ended
(`storage_moved_alert`). It reports no bytes, so the row hides its bar and shows
"—", and the Inspector shows an indeterminate bar. The engine also saves the new
folder and clears Moving for the whole group only at the end (`SaveMove`, then
`EndMove`).

## Decision: the engine moves the files itself

The engine moves the payload files with Windows file APIs, and then tells
libtorrent only the new save path with
`move_storage(destination, lt::move_flags_t::reset_save_path_unchecked)`.
That flag changes the save path and keeps libtorrent's piece state, so no check
runs.

The copy is safe without a check:

1. A moving torrent is paused (`Torrent::ApplyIntent`), so libtorrent starts
   no new reads or writes for it.
2. `ReleaseFiles` calls `async_release_files` and blocks until its callback
   runs (`ready.get()`). In `pread_disk_io`, libtorrent's disk I/O on Windows,
   that job is a fence: `disk_job_fence::raise_fence` holds it back until the
   torrent has no outstanding disk jobs. When the callback runs, every earlier
   read and write for the torrent has completed and its files are closed. The
   copy starts only after that.
3. A copy that returns success is complete, because every read and write
   failure is reported.

The existing `verifyFiles` marker stays. After a crash before the next
checkpoint, startup still checks the files at the new folder.

## Move phases and crash recovery

Each member of a move goes through these phases in this order. The order
ensures that a complete copy of the files always exists, either at a path the
saved state names or at the destination that the saved recovery marker names.

| Phase | What happens | State after a crash in this phase | Recovery |
|---|---|---|---|
| 1. Marked | `StartMove` saves the destination as the recovery marker, as today. | Files untouched. | Move interrupted, as today. |
| 2. Copying (another volume) | Files are copied. Sources are not touched. | Sources complete. Some destination files complete, one partial. | Move interrupted. The person chooses the old folder, and verification resumes the torrent there. The copies at the destination stay, as after a crash in a libtorrent move today. |
| 2. Renaming (same volume) | Files are renamed one at a time. A rename is atomic. | Each file is in exactly one of the two folders. | Move interrupted. The Move dialog opens at the saved destination (`FileDraft`), and that move counts the renamed files as already moved and renames the rest (the preflight rule for an absent source and an existing destination). |
| 3. Committed | libtorrent gets the new save path, and the new save path is saved with the torrent. The marker is cleared in the same commit. | The torrent is at the destination. Its sources still exist (copy case). | None needed. The torrent restarts at the destination, and `verifyFiles` checks it. |
| 4. Cleaning (another volume) | The sources and the folders they leave empty are deleted. | The torrent is at the destination. Some sources are left in the old folder. | None. The leftover files are not the torrent's any more. They cost disk space but hold no data that is not at the destination. |

Sources are deleted only after phase 3. A crash can therefore never leave a
saved save path whose files are partly deleted. A failed copy in phase 2 rolls
back: it removes the copies it made, or renames the files back, and the member
returns to its old folder.

## Where the methods come from

Four open-source copiers and two deletion tools were read. Each case below
uses the method of the project that measured or was built for that case. No
code is taken from them: FastCopy v3 and Ultracopier are GPLv3, AngelCOPY and
FastDelete have no licence, and TinyTorrent is MPL-2.0, so only their designs
and measurements are used. BetterCopy and rmbrr are MIT, but they are Rust.

| Copier | What it does | Evidence | What this plan takes |
|---|---|---|---|
| FastCopy v3 | Its own unbuffered, overlapped I/O through one 256 MB ring buffer, 20 reads and 20 writes of 1 MB in flight, files opened ahead by 16 threads. Two physical disks: reader and writer run at the same time. One physical disk: it reads files until the buffer is full, then writes them all. | Its documentation: parallel reads and writes on one hard disk "will be slow because many HDD head seeks will occur". No published numbers. | The hard-disk profile of the ring engine. |
| AngelCOPY (native engine, 1.0.0 and later) | Rename on one volume. Local files of 32 MiB and more: its own unbuffered, overlapped ring, 8 MiB blocks, 8 in flight. Smaller files: `CopyFileExW` with 16 workers, in items of up to 256 files from one folder. Network: `CopyFileExW` only. Deletion: 8 workers. | Its own cold-cache benchmark on one NVMe machine: one 8 GiB file at 3200 MB/s against robocopy's 2430 MB/s. Its benchmark also ran `CopyFileEx` with `COPY_FILE_NO_BUFFERING`, but the result is not published. Small files: 33% faster than robocopy `/MT:64` on a nested tree, equal on one flat folder; 64 workers fell to 130 MB/s against 428 MB/s for 16. Share to share, `CopyFileExW` took 0.2 s against the ring's 6–7 s (server-side copy). Share to local, buffered was about 2× faster. Deleting 10,000 files of 64 KiB: 1358–1656 ms one at a time, 330–380 ms with 8 workers; on a share, 8 workers were 3.5× faster. | The SSD profile of the ring engine, the network rule, small-file workers, and deletion. |
| BetterCopy | `CopyFileExW` per file and 16 workers for files under 1 MiB on SSD, one folder per worker. | One small-file result on NVMe. | It confirms the small-file workers and the split by folder. |
| Ultracopier | Its own buffered, overlapped I/O, 8 buffers of 256 KB. | None. | Nothing that the others do not do better. |
| FastDelete | Deletion only: one thread per logical processor, one folder per work item, `DeleteFile` per file, an extra `SetFileAttributes` call for each read-only file. | 1 million files: 32 s against `rd /s /q`'s 84 s on a 6-core laptop, 39 s against 283 s on a 16-core desktop. Read-only files about 2× slower, because of the extra call. It cannot help when all files are in one folder. | The thread count, and the evidence that the gain grows with cores. |
| rmbrr | Deletion only: one thread per logical processor; each file deleted in one call with POSIX semantics and "ignore read-only"; a folder deleted by the workers as soon as its last child is gone. | 28,434 files in 5,122 folders: 1.78 s, against 6.4 s for `rmdir` and 9.5 s for robocopy. | The deletion call and the parallel folder removal. |

Robocopy is closed source. Its switches (`/j`, `/sparse`, `/nooffload`,
`/noclone`, `/compress`) control the Windows copy engine, so it uses `CopyFile2`
or `CopyFileEx` itself. AngelCOPY measured its ring against robocopy, which is
the measurement of the ring against the Windows copy engine.

## Copy method for each source and destination

The module classifies each pair of volumes once and uses the first row that
applies.

| Case | Method | Source of the method |
|---|---|---|
| Same volume | `MoveFileExW` without `MOVEFILE_COPY_ALLOWED`. Windows returns `ERROR_NOT_SAME_DEVICE` for another volume. | A rename changes only the folder entry, so even 100 GB takes milliseconds. AngelCOPY, BetterCopy and Ultracopier do the same. |
| Source or destination is a network path | `CopyFile2`, buffered | AngelCOPY's measurement: SMB server-side copy (`FSCTL_SRV_COPYCHUNK`) is about 30× faster between shares on one server, and buffered reads from a share are about 2× faster. |
| Both local, and either volume is on a hard disk | The ring engine, hard-disk profile, for every file | FastCopy's design: one physical disk reads until the buffer is full and then writes, so the head seeks once per buffer; two disks run reader and writer at the same time and open files ahead. |
| Both local, neither has a seek penalty, file of 32 MiB or more | The ring engine, SSD profile | AngelCOPY's measurement: 32% faster than robocopy, which uses the Windows copy engine, on one large cold file. This is the weakest evidence in the plan: one machine, and no published number for `CopyFile2` with `COPY_FILE_NO_BUFFERING`. The ring is therefore built last (see [Implementation steps](#implementation-steps)), and until then this case uses `CopyFile2` with `COPY_FILE_NO_BUFFERING`. |
| Both local, neither has a seek penalty, file under 32 MiB | `CopyFile2`, 16 workers, each taking up to 256 files from one folder | AngelCOPY's measurement: 16 workers are best on NVMe, and more workers lose speed to NTFS contention when they create files in the same folder. The 256-file items let one large folder use several workers. |

The ring engine falls back to `CopyFile2` for a file when the unbuffered open
is refused, as AngelCOPY does. Some file system filters refuse
`FILE_FLAG_NO_BUFFERING`.

The `CopyFile2` cases use these flags:

- `COPY_FILE_FAIL_IF_EXISTS`: the preflight already refuses existing files.
  The flag also refuses a file that another program creates during the move.
- `COPY_FILE_COPY_SYMLINK`: a symlink that the torrent declares is copied as a
  link, not as the file it points to.
- `COPY_FILE_ENABLE_SPARSE_COPY` for a sparse file with holes, on Windows 11
  22H2 and later (`NTDDI_WIN10_NI`). Microsoft does not document what an older
  Windows does with the flag, so the module reads the build number with
  `RtlGetVersion` and passes the flag only where it exists.

`CopyFile2` copies alternate data streams by default, so the internet-zone mark
(`Zone.Identifier`) stays on finished files. ODX needs no code, because
`CopyFile2` tries it first by itself. The engine does not set
`COPY_FILE_REQUEST_COMPRESSED_TRAFFIC`, because compression helps only on a slow
link and costs CPU on a fast one.

**How the module classifies a pair.**

1. `GetDriveTypeW` returns `DRIVE_REMOTE` for a network volume. AngelCOPY uses
   the same test.
2. `IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS` gives the physical disk numbers of a
   local volume. FastCopy uses the same query.
3. `IOCTL_STORAGE_QUERY_PROPERTY` with `StorageDeviceSeekPenaltyProperty` tells
   whether a disk has a seek penalty. FastCopy and BetterCopy use it.
4. Two volumes are on one physical disk when they share a disk number.

BetterCopy sends both queries on a volume handle opened with no access rights,
so the engine needs no administrator rights. If a query fails, the pair counts
as a hard disk on separate disks. The hard-disk profile is correct on every
local pair, and it is the choice that loses the least speed when the answer is
unknown.

## The ring engine

One engine with two profiles. Each profile uses the values of the copier that
tuned it, so the plan invents no numbers.

| Setting | Hard-disk profile (FastCopy) | SSD profile (AngelCOPY) |
|---|---|---|
| Block size | 1 MB | 8 MiB |
| Blocks in flight, each side | 20 | 8 |
| Files | All files of the pair | Files of 32 MiB and more |
| Buffer | 256 MB, shared by all files | The blocks in flight |
| One physical disk | Read until the buffer is full, then write all of it | Not used: an SSD has no seeks |
| Two disks | Reader and writer run at the same time | Each block is written as soon as it is read |
| Files opened ahead | 16 threads, up to 256 open files | One file at a time |

Common to both profiles:

- **Unbuffered.** Sources are opened with `FILE_FLAG_NO_BUFFERING`.
  Destinations of 64 KB and more are written with `FILE_FLAG_NO_BUFFERING`
  (FastCopy's limit). Smaller destinations in the hard-disk profile are written
  through the cache, because a small unbuffered write waits for the disk. An
  unbuffered write is a whole number of sectors, so the last block is padded,
  and then the end of file is set to the true size. Each block starts at a
  sector-aligned address.
- **Overlapped I/O** with `FILE_FLAG_OVERLAPPED` on an I/O completion port, for
  files of 512 KB and more. Smaller files use synchronous I/O (FastCopy).
- **Preallocation.** The writer sets each destination to its full size before
  it writes, as FastCopy and AngelCOPY do, so the file system can place it in
  one piece.
- **Sparse files.** For a sparse source with holes, the reader reads only the
  allocated ranges (`FSCTL_QUERY_ALLOCATED_RANGES`). When the destination
  volume reports `FILE_SUPPORTS_SPARSE_FILES`, the writer first marks the
  destination sparse (`FSCTL_SET_SPARSE`), then sets its size, and then writes
  only those ranges. In that order, setting the size allocates nothing, so the
  holes stay holes. On exFAT or FAT32 the holes are written as zeros. This
  works on every Windows version. None of the four copiers keeps sparse files
  sparse in its own engine.
- **Metadata.** A copy keeps what a person can see or depends on: the data,
  every alternate data stream (including `Zone.Identifier`), the extended
  attributes, the file attributes, and the creation, access and write times.
  The streams and extended attributes are copied with
  `BackupRead`/`BackupWrite`, skipping the main data stream that the ring has
  already copied, as FastCopy does. The attributes and times are set with
  `FileBasicInfo`. Security descriptors are not copied, because `CopyFile2`
  does not copy them by default either: the destination takes the permissions
  of its folder. OLE property sets on NTFS are stored as alternate data
  streams, so they are copied with them. AngelCOPY's ring copies only times and
  attributes, and so loses the `Zone.Identifier` mark.
- **Files it passes to `CopyFile2`:** a declared symlink and a file encrypted
  with EFS. These are rare, and `CopyFile2` already copies them correctly.
- The buffer is allocated when a move starts and released when it ends.

## Deletion

The move removes its sources in phase 4, after the commit. A rollback removes
the copies it made. File deletion with the permanent mode removes a torrent's
files. All three call one function, `DeletePayload`.

- **Workers.** One worker per logical processor
  (`std::thread::hardware_concurrency`), as rmbrr and FastDelete use. Their
  results on large trees show the gain growing with the number of cores.
  AngelCOPY's fixed 8 workers were measured on one machine with 10,000 files
  only.
- **Work items.** Up to 256 files from one folder. Workers in different folders
  do not wait for each other, because NTFS works through the changes of one
  folder one at a time. The split into 256-file items, from AngelCOPY, also
  spreads one large folder over several workers, which FastDelete cannot do.
- **Each file.** `std::filesystem::remove`. In the MSVC library it opens the
  file for `DELETE` and deletes it with one
  `SetFileInformationByHandle(FileDispositionInfoEx)` call that sets
  `FILE_DISPOSITION_FLAG_POSIX_SEMANTICS` and
  `FILE_DISPOSITION_FLAG_IGNORE_READONLY_ATTRIBUTE` (from Windows 10 1809).
  This is the same call rmbrr makes. The name is removed at once, and a
  read-only file needs no extra `SetFileAttributes` call, which made FastDelete
  2× slower on read-only files. On a file system without POSIX delete, such as
  FAT32, the library falls back to the classic delete. The classic delete
  refuses a read-only file, so on access denied there the function clears the
  read-only attribute and deletes again, as the `remove_dir_all` crate and
  libuv do. A file that is already gone, or whose delete is already pending,
  counts as deleted, as in Rust's standard library.
- **Short retries.** Antivirus scanners, the search indexer and media players
  often hold a file open for a moment. A delete that fails with a sharing
  violation, a lock violation or access denied is tried again up to 10 times,
  waiting 1.2 times longer each time and at most 200 ms, as rimraf does. Rust's
  standard library and rmx also retry, but for only a few milliseconds.
- **Folders that are not empty yet.** A folder removal that fails with
  `ERROR_DIR_NOT_EMPTY` is retried in the same way, because a scanner's handle
  or a pending delete can keep a child visible for a moment. Rust's standard
  library and rmx retry this case too.
- **Folders.** For a move, each source folder that the move empties is removed
  by the workers as soon as its last file and subfolder are gone, as rmbrr
  does, up to the save path. Folders do not wait for one pass at the end.
  File deletion keeps its current rule.
- **Errors.** It deletes every file it can and throws the first failure, as the
  permanent branch of `Erase` does now. The move writes the failure to the log
  and is still complete, because the files are committed at the destination.

The recycle-bin mode stays in `Erase` with `IFileOperation`. Only Explorer's
engine can send files to the Recycle Bin, so there is no faster method for it.

## The move module

`engine/inc/Payload.h` declares the whole interface:

```cpp
struct PayloadFile
{
    std::filesystem::path source;
    std::filesystem::path destination;
};

struct MoveProgress
{
    std::atomic<std::uint64_t> total{0};
    std::atomic<std::uint64_t> done{0};
};

// Puts each file at its destination by the fastest method for its volumes: a
// rename on the same volume, otherwise a copy that leaves the source in place.
// Returns the sources that were copied, which the caller deletes after it has
// committed the new location. On failure, it removes the copies it made,
// renames files back, and throws an exception that names the file. Blocks the
// calling thread until every file is in place.
std::vector<std::filesystem::path> MovePayload(
    std::vector<PayloadFile> const& files, MoveProgress& progress);

// Deletes the files permanently and throws the first failure after trying
// every file. A file that is already gone counts as deleted. With a root, it
// also removes each folder that the deletion leaves empty, up to that root.
void DeletePayload(std::vector<std::filesystem::path> const& files,
    std::optional<std::filesystem::path> const& root);
```

The module owns the classification, the choice of method, the ring engine,
deletion, rollback and source cleanup. The caller supplies only the paths. It
decides nothing about collisions, shared files or torrents, because the move
preflight in `Move.cpp` already owns those rules.

| File | Holds |
|---|---|
| `engine/inc/Payload.h` | The interface above. |
| `engine/src/Payload.cpp` | `MovePayload` and `DeletePayload`: classification, the choice of method, the rename, the `CopyFile2` cases with their workers, deletion, rollback and cleanup. |
| `engine/inc/Payload/Ring.h`, `engine/src/Payload/Ring.cpp` | The ring engine and its two profiles. It is a separate file because it owns its own threads, completion port and buffer, and only `Payload.cpp` calls it. |

The folder follows `engine/inc/Engine/` and `engine/src/Engine/`, which hold the
parts of `Engine::State`. The name uses the product term payload (see
`CONTEXT.md`). `Files.cpp` keeps the `Engine::State` file operations.

Behavior the interface does not show:

- A missing source is skipped. libtorrent creates no file for a file that is
  not wanted, and the preflight has already accepted absent sources.
- **Progress units.** `total` is the number of payload bytes to move, read
  before the first file moves. `done` counts each byte once, when it is
  written: a renamed file adds its whole size at once, a `CopyFile2` copy adds
  the bytes from each `COPYFILE2_CALLBACK_CHUNK_FINISHED` message, and the ring
  adds each completed write. With one physical hard disk, the bar therefore
  moves in steps of up to 256 MB, one per buffer. A torrent whose files are all
  empty has a `total` of 0; its row shows 0 until the member completes.
- **Long paths.** Every path is passed to Windows with the `\\?\` prefix.
  libtorrent creates paths longer than 260 characters, and the engine has no
  `longPathAware` manifest, so without the prefix such a file could not be
  moved or deleted. `DeletePayload` therefore also lets File deletion delete
  those files.
- Destination folders are created before the copy starts, as AngelCOPY and
  BetterCopy do, so workers do not create the same folder at the same time.
- All workers and engine threads end before a function returns. A failure in
  any of them stops the others, and then the rollback runs.
- Source cleanup removes only the moved files and the folders they leave empty.
  Other files in the folder, such as added subtitles, stay. The engine contract
  requires this.

## What the change reuses

| Existing code | Use in this change |
|---|---|
| `Store` worker (`Store.h`) | A new `Store moves;` member in `Engine::State`, as `sources` and `checks` have, so a 30-minute copy does not block the payload worker. The payload worker runs one job at a time, and it also serves checkpoints and `.!tt` renames. `Store` already turns an exception into a `StorageOutcome` failure. `Tick` drains the new worker with the others. |
| `ReleaseFiles` | Runs at the start of the copy job, for the member and every owner of its shared files, so libtorrent has stopped using them. |
| `FilePaths(torrent, folder, false)` and `FullPath`, `SamePath` | The physical source and destination of each file. A file whose source and destination are the same, such as one with an absolute path, is left out. The list also holds libtorrent's part file, which keeps the pieces of files that are not wanted. `reset_save_path_unchecked` does not move it. libtorrent names it `.<hex of info_hashes().get_best()>.parts` and puts it in `add_torrent_params::part_file_dir`, a folder relative to the save path (`pread_storage.cpp`, `torrent.cpp`). The engine never sets `part_file_dir`, so the part file is in the save path itself. One function in `Move.cpp` builds that path. Its comment names the pinned libtorrent source and states that it relies on `part_file_dir` staying unset. |
| `Erase` in `Files.cpp` | Its permanent branch calls `DeletePayload`, so File deletion and the move share one deletion implementation. Its recycle-bin branch is unchanged. |
| Move preflight (`StartMove`, `PrepareMove`) | Unchanged: collisions, shared files, outside torrents, empty placeholders. |
| `FinishMove`, `RecoverMove` | The `reset_save_path_unchecked` call still ends with a libtorrent alert. The existing path handles that alert, including a lost one. |
| `Problem{ProblemKind::MoveFailed, detail}`, `Notify` | Report a failed copy on its torrent. |
| `changes.Commit` with `Saved()` | Commit each member's new save path, as `SaveMove` does now for the group. |
| Snapshot cadence (1 s by default) | Carries the progress. No new message. |
| Row `progress` field | Holds the move fraction while `status` is `moving`, as libtorrent's progress holds check progress while `status` is `checking`. The app already shows both. |
| App progress bar, `P1` percent format, "Moving" status and glyph | Show the move. No new strings. |

Not reused: libtorrent's internal `aux::copy_file`. It is not public API and
reports no progress.

## Changes by file

**Engine**

- `engine/inc/Payload.h`, `engine/src/Payload.cpp`,
  `engine/inc/Payload/Ring.h`, `engine/src/Payload/Ring.cpp`: new. The move
  module.
- `engine/src/Engine.vcxproj`: add the new files.
- `engine/src/Engine/Files.cpp`: the permanent branch of `Erase` calls
  `DeletePayload` without a root.
- `engine/inc/Enums.h`: add `MovePhase::Copying` and `MovePhase::Cleaning`.
  `Copying` means the worker owns the member's files (phase 2). `Moving` keeps
  its meaning: libtorrent is changing the save path. The deletion ruling and
  `RecoverMove` treat the two differently. `Cleaning` means the member is
  committed and its sources are being deleted (phase 4); the move keeps its
  path holds until it ends, so an addition cannot reach the old files.
- `engine/inc/Engine/State.h`:
  - Add `Store moves;`.
  - `Move` keeps the files that the preflight found at the destination apart
    from the files this move put there. Only the first kind needs a check.
  - `Move` gets `std::shared_ptr<MoveProgress>` for the current member. The
    shared pointer keeps the counter alive while the worker runs, even if the
    member leaves the move.
- `engine/src/Engine/Move.cpp`:
  - `ContinueMove`: run `MovePayload` on `moves` for the files that are not yet
    at the destination. Then call `move_storage` with
    `reset_save_path_unchecked`, or with `reset_save_path` when the member uses
    a file that the preflight found already at the destination. The
    `dont_replace` and `fail_if_exist` choices go away, because libtorrent no
    longer moves files.
  - `SaveMove` takes one member: it commits that torrent's save path, clears its
    marker and Moving, and applies its intent, so the row changes at once.
  - After that commit, a job on `moves` calls `DeletePayload` with the sources
    that `MovePayload` returned and the old save path as root (phase 4). The
    next member starts when it ends.
  - A failed copy leaves no change on disk. It clears that member's marker,
    shows the error on that torrent, and the move continues with the next
    member, so one locked file does not stop the others.
  - `EndMove` runs when no member is left.
- `engine/inc/Torrent.h`, `engine/src/Torrent.cpp`: the torrent holds a
  `std::shared_ptr<MoveProgress const>` while its files are being copied.
  `Row` writes `done / total` to `progress` while the torrent is moving, and 0
  while it waits for its turn.

**App**

- `app/src/Torrents/Torrent.cs`: remove `ShowsProgress` and the "—" in
  `ProgressText`. Both exist only to hide the bar during a move.
- `app/src/Shell/MainWindow.xaml`: remove the bar's `Visibility` binding.
- `app/src/Torrents/Inspector.cs`: `IsIndeterminate` is true only without
  metadata.

**Documents**

- `docs/contracts/engine.md`, "Removal and moves": the engine moves the files;
  the copy method table, the ring engine and deletion; each member completes on
  its own; a failed member does not stop the group. Remove the libtorrent flag
  rules that no longer apply.
- `docs/contracts/pipe-api.md`: `progress` while `moving`; markers clear per
  torrent.
- `docs/README.md`: list this plan, and remove it when the plan is deleted.

## Unchanged rules

- Exit still waits for a running move. The move stays set until the worker
  ends, so the existing wait covers it.
- A deleted torrent whose files are being copied still leaves the list at once
  and is removed when the disk work ends, as the deletion ruling requires.
- After a crash, recovery uses the existing Move interrupted flow, as
  [Move phases and crash recovery](#move-phases-and-crash-recovery) describes
  for each phase.

## Windows 10

README supports Windows 10 1809. Every method above runs on Windows 10, and
there is no code that runs only there. The ring engine, the rename, deletion
and the `CopyFile2` cases are the same on every version.

One difference remains. `COPY_FILE_ENABLE_SPARSE_COPY` exists only from
Windows 11 22H2. On older Windows, a sparse file that `CopyFile2` copies is
written at its full size. These are files under 32 MiB on SSD, and network
copies. The ring engine keeps sparse files sparse on every version.

## Implementation steps

The order puts the move's correctness first. Every step leaves a complete,
working move, and the ring engine comes last because it only makes copies
faster.

1. Change `Move.cpp`, `State.h`, `Enums.h` and `Torrent.cpp` for the phases,
   the worker and per-member completion.
2. Write `MovePayload` with the classification, the rename and the `CopyFile2`
   cases. Until step 4, every local copy uses `CopyFile2`, with
   `COPY_FILE_NO_BUFFERING` for files of 32 MiB and more.
3. Write `DeletePayload` and move the permanent branch of `Erase` to it.
4. Write the ring engine with its two profiles. `CopyFile2` stays as its
   fallback for refused unbuffered opens, symlinks and EFS files.
5. Change the app and the contracts.
6. Verify:
   - Engine check (`engine/tests/Checks.ps1`, beside the existing move case):
     move two torrents in one command, one of them with an unwanted file so
     that it has a part file. Each row reaches its new folder and leaves Moving
     on its own, the files and the part file are at the destination, and the
     files match their hashes. The failure it watches for: a member that waits
     for the group, a lost part file, or a move that loses data.
   - The existing File deletion checks still pass, because `Erase` changed.
   - By the owner, by hand: end the engine during a large move to another
     drive, start it again, and recover through Move interrupted by choosing
     the old folder. Then end it during a move on the same volume, and recover
     by choosing the saved destination. In both cases the torrent resumes with
     its data complete.
   - By hand, on a second drive (USB or second disk): a cross-volume move shows
     a rising percentage, the files are at the destination with their
     `Zone.Identifier` stream and times, the sources and their empty folders
     are gone, and no check runs afterwards.
   - By hand: an unfinished sparse file moved by the ring engine stays sparse
     at the destination (`fsutil sparse queryflag`), and its size on disk stays
     close to its downloaded size.
   - By hand: a source file that another program holds open without read
     sharing makes that torrent fail with its sources intact, and the other
     torrents still move.
