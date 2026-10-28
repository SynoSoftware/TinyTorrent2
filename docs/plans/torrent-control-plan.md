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

This plan keeps only what needs the owner: pushing the fork, the checks that
need a real share or download real payload, and the separate work.

## Push the fork (owner)

`engine/src/Dependencies.ps1` pins the libtorrent fork at df0da6bf1. That
commit and the two before it exist only in the local checkout
`3rdParty/libtorrent`, branch `Synapse/TinyTorrent`. A machine without that
checkout cannot fetch the pin until the owner pushes the branch to
`SynoSoftware/libtorrent`.

## Checks that download real payload (owner)

When the owner asks, `StoppedHeld`, `MagnetAbsent` and `DiskError` in
`engine/tests/Checks.ps1` run once with `-Transfer`.

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
