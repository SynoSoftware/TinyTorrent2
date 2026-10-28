# Torrent control

Steps 0 to 5 of this plan are implemented: steps 0 to 2 in b6c300c and steps
3 to 5 in the commit after it. Each torrent has one storage controller,
`Control`, with a checked transition test (`engine/tests/Transitions.cpp`);
file work runs per drive or server; and unfinished file work is saved before
it changes a file.
The [engine contract](../contracts/engine.md#state-and-work) and its
references ([files](../contracts/engine/files.md),
[lifetime](../contracts/engine/lifetime.md),
[notifications](../contracts/engine/notifications.md)) and the
[pipe API](../contracts/pipe-api.md) own that behavior now.

This plan keeps only what remains: the owner's dependency rebuild, the checks
that wait for it, and step 6, which needs separate authorization.

## Step P. Rebuild the dependencies (owner)

`engine/src/Dependencies.ps1` pins the libtorrent fork at 21f44eaa9, but the
engine still links the 39e37b746 build. Agents never run `Dependencies.ps1`,
because it takes over an hour of the owner's machine. The owner runs:

```powershell
pwsh -NoProfile -File ./engine/src/Dependencies.ps1
```

Until then, three fork behaviors the controller relies on do not ship:

- A stopped check keeps its position at the lowest piece in flight, so a check
  never restarts from zero.
- A save folder that is a link to a removed drive reports the drive as absent,
  so the torrent waits for its location instead of stopping.
- `clear_error` runs a failed resume-data check, a failed recheck setup or a
  seed's pending recheck again.

After the rebuild, these checks run once: `AbsentLink` in
`engine/tests/Checks.ps1`, and, when the owner asks, `StoppedHeld`,
`MagnetAbsent` and `DiskError` with `-Transfer`, which download real payload.

## Manual checks with a real share

These need a network share that can be disconnected, made to hang and
reconnected. They run once after step P. M2 runs again after step 6.

| # | Do | Expect |
|---|---|---|
| M1 | Disconnect the share during a download; reconnect after 3 minutes. Repeat with the share made to hang | The error shows; the location notice appears 2 to 3 minutes after the share stops answering; the torrent resumes by itself and the notice disappears. |
| M2 | Make the share hang while a torrent seeds from it, during a download, a rename, a deletion, a check, and the minute check of a failed final-folder move from it | Torrents on other drives keep their names, renames, deletions, reloads and minute checks, and, after step 6, their transfers and checks. |
| M3 | Delete one file on the share while it is away; reconnect | Only that file downloads again; no full check. |
| M4 | Fill the download disk; free space after the stop | One automatic retry, then Stopped with Retry; the minute check continues it. |
| M5 | Verify a torrent on the share; disconnect mid-check; reconnect | The check continues from its position, not from 0%. Stop during the check cancels it. |
| M6 | Save into a folder link to a removable drive; remove the drive | The torrent waits with the location notice, not Stopped; no progress is lost; it resumes when the drive returns. |
| M7 | Fail a move to the final folder on the share | It waits, runs once more when the share answers with room, then waits for Retry. |
| M8 | Choose Exit during: a move of a removed torrent to the share, the same while another torrent's names are unknown, a give-back after Abandon and after its renames, a Recycle Bin deletion, and a backlog of finished files on the share; start again, once with the share answering and once without | Each Exit ends in about 2 seconds. The next start continues each piece of work; work on the absent share waits with its location notice and runs when the share returns. |

## Step 6. Disk work per drive in the fork

**Needs separate authorization.**

libtorrent's `pread_disk_io` runs every torrent's disk jobs on one generic
pool and one hash pool, flushes every storage's cache and closes files on the
same threads, and applies one write-cache limit to all torrents. On a share
that hangs, ten blocked jobs hold all ten generic threads, and then no torrent
on any drive reads, writes, releases, renames or moves. The engine's own file
work is already per drive (step 4); this is the same rule inside libtorrent.

**Fork,** in `pread_disk_io`, `disk_io_thread_pool`, `disk_cache` and
`file_pool`:

- Each storage records its root, computed as `check_storage_root` computes
  it, and records it again when its save path changes.
- Each root has its own generic pool and hash pool, created on its first job.
  `aio_threads` and `hashing_threads` apply to each root. Idle pools end their
  threads after 60 seconds, as today.
- `pool_for_job` picks the job's root. A job without a storage uses a default
  pool. `move_storage` runs on its source root's pool.
- A root's threads flush only that root's cache blocks and fenced storages,
  and run only that root's storage ticks and file closes.
- The write cache applies back pressure per root.
- `FORK.rst` describes the change.

**Engine,** in `Turns.cpp`: a stalled check holds only the checks on its own
root. The files reference drops "A read that never returns stops every
check".

**Options that do not meet the rule:** the engine cannot bound libtorrent's
blocking jobs per drive; more threads move the limit but do not remove it;
cancelling a blocked read depends on the SMB redirector, which is not
established.

**Risk.** This changes libtorrent's disk scheduling, where the fork's own
comments record earlier fence and flush deadlocks. The step runs libtorrent's
disk tests (`test_disk_io`, `test_fence`, `test_disk_cache`, `test_storage`),
then a second dependency rebuild, then M2.

## Separate work

Verified in the code, outside torrent control:

- [#560](https://github.com/SynoSoftware/TinyTorrent2/issues/560): a torrent's
  handle and its restore parameters become one value.
- [#561](https://github.com/SynoSoftware/TinyTorrent2/issues/561): the app's
  message bar Retry reaches a removed torrent's failed give-back.
- A listed torrent's give-back and a removed torrent's give-back keep two
  records with one retry rule; the removed one has no drive turns or stall
  watch. A removal during a move is rare, and a hung try holds only its own
  worker.
- `checkpointPhase` mixes the request with the write (`Checkpoints.cpp`).
- A failed watch folder is reported once, ever (`Watching.cpp`).
