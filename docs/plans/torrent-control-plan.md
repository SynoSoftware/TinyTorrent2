# Torrent control

Steps 0 to 6 of this plan are implemented: steps 0 to 2 in b6c300c, steps 3
to 5 in 78eb5b3, and step 6 in the commit after it. Each torrent has one
storage controller, `Control`, with a checked transition test
(`engine/tests/Transitions.cpp`); the engine's file work and libtorrent's disk
threads, cache and open files are per drive letter or server; and unfinished
file work is saved before it changes a file.
The [engine contract](../contracts/engine.md#state-and-work) and its
references ([files](../contracts/engine/files.md),
[lifetime](../contracts/engine/lifetime.md),
[notifications](../contracts/engine/notifications.md),
[settings](../contracts/engine/settings.md)) and the
[pipe API](../contracts/pipe-api.md) own that behavior now. The fork's
`FORK.rst` describes its changes.

This plan keeps step 7, the checks that need a share that can be
disconnected, and the separate work.

## Step 7. Storage belongs to where its folder really is

**Owner ruling (2026-10-28):** storage is classified by its actual
destination, not by the drive letter in the configured path.

A save folder `C:\Media` that is a link to `\\nas\media` belongs to the NAS
server. Today libtorrent's disk threads (`lt::storage_root_key`) and the
engine's drive workers, file-work gates, stalled checks, probes and location
notices all key it by `C:`. When the NAS hangs, every torrent on C: stops,
and the location notice names `C:\` or does not appear. A deletion or
give-back under the link runs again every minute, because the probe of `C:\`
answers. Drive turns already follow links (`Drives::Of`), so today the
engine uses two different answers for the same folder.

**Resolve without touching the target.** Opening a link follows it, and that
open hangs on the target this step keeps apart. The engine reads where a
folder really is from metadata on the volume that holds each link:

- Walk the path from its root. For each existing component that is a reparse
  point, read its target with `FSCTL_GET_REPARSE_POINT`, opened with
  `FILE_FLAG_OPEN_REPARSE_POINT`: symbolic links, junctions and volume mount
  points. Map a drive letter through `QueryDosDevice`, so a `subst` letter
  becomes its folder and a mapped network letter becomes its server and
  share.
- Stop at a network path: its server is the location, and the engine cannot
  see behind it.
- The result is the folder's real path. Its key is
  `lt::storage_root_key(real path)`, so one parser still names every key.
- A component that cannot be read leaves the path's own root as the answer,
  and the folder still works as today.

**One owner.** The engine keeps one map from folder to real path, filled off
the main thread and read by `RootOf`, the probes, the notices and `Drives::Of`
(which then opens the real path on the real root's worker). Work whose folder
is not resolved yet waits as it waits for its drive lookup today. A folder is
resolved again with the minute probe of its root, so a link that now points
elsewhere moves its work to the new root.

**Fork.** libtorrent cannot resolve links itself: it assigns a storage's
root on its network thread, under its job lock, where no file I/O may run.
The engine gives it the key:

- `add_torrent_params` and `move_storage` take an optional root key;
  without one, libtorrent keys the path as today.
- A new fenced job re-keys a storage whose link changed; it uses the same
  root change as a move.

**Contracts.** files.md drops "a folder that links to another drive counts
under its own letter"; the drive-turn, worker, probe and notice text names the
real location; notifications.md names the share a link points to.

**Checks.** A fork test adds a storage with a given key and re-keys it while
jobs wait. An engine check saves into a junction to a `subst` drive, removes
the `subst` drive, and expects the location notice for the target while a
torrent on the junction's own drive keeps downloading. M6 covers the real
removable drive.

## Manual checks with a real share

These need a network share that can be disconnected, made to hang and
reconnected, and, for M6, a removable drive.

| # | Do | Expect |
|---|---|---|
| M1 | Disconnect the share during a download; reconnect after 3 minutes. Repeat with the share made to hang | The error shows; the location notice appears 2 to 3 minutes after the share stops answering; the torrent resumes by itself and the notice disappears. |
| M2 | Make the share hang while a torrent seeds from it, during a download, a rename, a deletion, a check, and the minute check of a failed final-folder move from it | Torrents on other drives keep their transfers, checks, names, renames, deletions, reloads and minute checks. |
| M3 | Delete one file on the share while it is away; reconnect | Only that file downloads again; no full check. |
| M4 | Fill the download disk; free space after the stop | One automatic retry, then Stopped with Retry; the minute check continues it. |
| M5 | Verify a torrent on the share; disconnect mid-check; reconnect | The check continues from its position, not from 0%. Stop during the check cancels it. |
| M6 | Save into a folder link to a removable drive; remove the drive. Repeat while a check of a torrent on the link's own drive letter waits | The torrent waits with the location notice, not Stopped; no progress is lost; it resumes when the drive returns. The other check is not stopped with `drive_stalled`. |
| M7 | Fail a move to the final folder on the share | It waits, runs once more when the share answers with room, then waits for Retry. |
| M8 | Choose Exit during: a move of a removed torrent to the share, the same while another torrent's names are unknown, a give-back after Abandon and after its renames, a Recycle Bin deletion, and a backlog of finished files on the share; start again, once with the share answering and once without | Each Exit ends in about 2 seconds. The next start continues each piece of work; work on the absent share waits with its location notice and runs when the share returns. |

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
- No engine test runs a stalled check on one drive with checks on the same
  and another drive, so the per-drive stall rule in `Turns.cpp` is covered
  only by review and M2.
- No test runs sequences of file-work events (`State::FileWork`): a deletion
  part that waits for an absent share while moves and give-backs run on
  another drive, and give-backs taking turns among checks. Review and M2 cover
  them.
- Renames stay outside `State::FileWork`, so a rename that hangs on a share no
  running torrent uses gets no probe and no location notice; the row shows
  Moving until Windows returns.
