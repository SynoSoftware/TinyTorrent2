# Ring copy engine

**Won't implement.** Research, 2026-10-18. This document is not a plan of
work. It keeps the evidence about copy engines and the design that one of the
engine's own would need, so that a later question about faster moves starts
from that evidence instead of repeating it. The
[engine contract](../contracts/engine.md#removal-and-moves) owns how moves work:
a rename on one volume, otherwise `CopyFile2`.

## Why it is not implemented

The ring engine would write the torrent's data itself, with sector padding,
alignment, sparse ranges and streams. Every move is verified afterwards, so a
defect in the ring would be found, but only as pieces that fail their hash
check. The torrent would then download those pieces again, so the defect would
cost data that the person already had. `CopyFile2` is the copy code that
Explorer and robocopy use on every Windows machine.

The ring is clearly faster only in cases that are rare for torrent payload, or
in cases where the gain is a few seconds on a move that already shows its
progress:

| Move of 50 GB | `CopyFile2` | Ring |
|---|---|---|
| Same volume | Instant, a rename | Same |
| To another drive, such as a USB or second disk | 4–8 min, limited by the destination disk | Same: neither writes faster than the disk |
| NVMe to NVMe | About 21 s | About 16 s, from AngelCOPY's single measurement |
| One large file between two partitions of one hard disk | The default read/write cycle | Fast |
| Thousands of small files between two partitions of one hard disk | Limited by seeks: two per file | Fast: one pair of seeks per 256 MB buffer |

The ring would help only in the last two rows. It would fit behind
`Payload::Move`, so no caller would change.

## Evidence

Four open-source copiers and two deletion tools were read on 2026-10-18. No code
may be taken from them: FastCopy v3 and Ultracopier are GPLv3, AngelCOPY and
FastDelete have no licence, and TinyTorrent is MPL-2.0. Only designs and
measurements can be used. BetterCopy and rmbrr are MIT, but they are Rust.

| Copier | What it does | Evidence |
|---|---|---|
| FastCopy v3 (2019) | Its own unbuffered, overlapped `ReadFile`/`WriteFile` through one 256 MB ring buffer, 20 reads and 20 writes of 1 MB in flight, files opened ahead by 16 threads, up to 256 open files. Two physical disks: a reader thread and a writer thread run at the same time. One physical disk (`IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS`, disk number of the first extent): it reads files until the buffer is full, across many files, then writes all of it and waits until the writer has emptied it. Sources opened with `FILE_FLAG_NO_BUFFERING` and `FILE_FLAG_SEQUENTIAL_SCAN`; destinations unbuffered from 64 KB (128 KB on FAT); overlapped I/O from 512 KB. It never renames on one volume and does not keep sparse files sparse. | Its documentation: on one hard disk, parallel reads and writes "will be slow because many HDD head seeks will occur", so it has two modes. No published numbers. |
| AngelCOPY (native engine, 1.0.0 and later) | Local files of 32 MiB and more: its own unbuffered, overlapped ring over a completion port, 8 MiB blocks, 8 in flight, destination preallocated, tail trimmed afterwards, times and attributes set. It falls back to `CopyFileExW` when an unbuffered open is refused. Any network end uses `CopyFileExW`. | Its own cold-cache benchmark on one NVMe machine: one 8 GiB file at 3200 MB/s against robocopy's 2430 MB/s (+32%). Its benchmark also ran `CopyFileEx` with `COPY_FILE_NO_BUFFERING`, but the result is not published. Share to share, the ring took 6–7 s against `CopyFileExW`'s 0.2 s (server-side copy); share to local, the unbuffered ring was about 2× slower than buffered. |
| BetterCopy | `CopyFileExW` for every file, `COPY_FILE_NO_BUFFERING` from 256 MiB. An earlier own loop was deleted in favour of `CopyFileExW`. | One small-file result on NVMe. |
| Ultracopier | Its own buffered, overlapped I/O on a completion port, 8 buffers of 256 KB, `FILE_FLAG_SEQUENTIAL_SCAN`, destination space reserved with `FileAllocationInfo` so a crash does not leave a full-size file that looks finished. | None. |
| Robocopy | Closed source. Its switches (`/j`, `/sparse`, `/nooffload`, `/noclone`, `/compress`) control features of the Windows copy engine, so it uses `CopyFile2` or `CopyFileEx`. | AngelCOPY's comparison above is a comparison against the Windows copy engine. |

What the evidence does not settle:

- No project publishes a comparison of an own ring against `CopyFile2` with
  `COPY_FILE_NO_BUFFERING`.
- No project publishes a hard-disk measurement. The case for the hard-disk
  profile is FastCopy's design reasoning and seek arithmetic: a 1 MB cycle at
  150 MB/s transfers for about 7 ms and then seeks twice for about 20 ms, so
  about 25% of the time copies data; a 64 MB cycle copies about 95% of the time.
  For 5,000 files of 200 KB on one disk, two seeks per file cost about 100 s,
  while the data needs about 7 s. The Windows cache and the disk's command
  queue hide part of that, by an amount nobody has measured.
- `COPYFILE2_EXTENDED_PARAMETERS_V2::ioDesiredSize` (Windows 11, from
  `NTDDI_WIN10_FE`) sets the read/write cycle inside one file. It is not
  FastCopy's algorithm: it does not batch several files into one buffer, and
  Microsoft documents that it "may be reduced" and "may be ignored". It is an
  optional optimization, not a solution for one hard disk. `Payload::Move` does
  not set it, because nothing has measured its effect.

## Design

One engine with two profiles. Each profile uses the values of the copier that
tuned it.

| Setting | Hard-disk profile (FastCopy) | SSD profile (AngelCOPY) |
|---|---|---|
| Applies to | Every file of a local pair where either volume has a seek penalty | Files of 32 MiB and more on a local pair without a seek penalty |
| Block size | 1 MB | 8 MiB |
| Blocks in flight, each side | 20 | 8 |
| Buffer | 256 MB, shared by all files | The blocks in flight |
| One physical disk | Read until the buffer is full, then write all of it | Not used: an SSD has no seeks |
| Two disks | Reader and writer run at the same time | Each block is written as soon as it is read |
| Files opened ahead | 16 threads, up to 256 open files | One file at a time |

Common to both profiles:

- **Unbuffered.** Sources are opened with `FILE_FLAG_NO_BUFFERING`.
  Destinations of 64 KB and more are written with `FILE_FLAG_NO_BUFFERING`.
  Smaller destinations in the hard-disk profile are written through the cache,
  because a small unbuffered write waits for the disk. An unbuffered write is a
  whole number of sectors, so the last block is padded and the end of file is
  then set to the true size. Each block starts at a sector-aligned address; the
  sector size is the larger of the two volumes' sector sizes.
- **Overlapped I/O** with `FILE_FLAG_OVERLAPPED` on an I/O completion port, for
  files of 512 KB and more. Smaller files use synchronous I/O.
- **Preallocation.** The writer sets each destination to its full size before
  it writes, so the file system can place it in one piece.
- **Sparse files.** For a sparse source with holes, the reader reads only the
  allocated ranges (`FSCTL_QUERY_ALLOCATED_RANGES`). When the destination
  volume reports `FILE_SUPPORTS_SPARSE_FILES`, the writer first marks the
  destination sparse (`FSCTL_SET_SPARSE`), then sets its size, then writes
  only those ranges. In that order, setting the size allocates nothing. On exFAT
  or FAT32 the holes are written as zeros, after the free-space check that
  `Payload::Move` already makes. This works on every Windows version.
- **Metadata.** The data, every alternate data stream (including
  `Zone.Identifier`), the extended attributes, the file attributes, and the
  creation, access and write times. Streams and extended attributes are copied
  with `BackupRead`/`BackupWrite`, skipping the main data stream, as FastCopy
  does. Attributes and times are set with `FileBasicInfo`. Security descriptors
  are not copied, as `CopyFile2` does not copy them by default. AngelCOPY's ring
  copies only times and attributes, and so loses `Zone.Identifier`.
- **Fallback to `CopyFile2`** for a refused unbuffered open (some filters
  refuse `FILE_FLAG_NO_BUFFERING`), a declared symlink, and an EFS file.
- **Network.** Never: any remote end stays on `CopyFile2`, because of
  AngelCOPY's server-side copy and read-ahead measurements.
- **Progress.** Each byte counts once, when it is written. With one physical
  hard disk, the bar moves in steps of up to 256 MB.
- The buffer is allocated when a move starts and released when it ends.

## Files

`engine/inc/Payload/Ring.h` and `engine/src/Payload/Ring.cpp`, called only by
`Payload::Move` in `engine/src/Payload.cpp`. It is a separate file because it
owns its own threads, completion port and buffer. The volume classification
that `Payload::Move` already makes (remote, seek penalty) selects the profile.
The one-disk mode also needs each volume's physical disk numbers
(`IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS`), which `Payload::Move` does not query
yet.

## Verification it would need

Because moves skip libtorrent's check, the ring needs evidence that
`CopyFile2` does not: an engine check that moves torrents through each profile
and compares every file's hash at the destination, including a sparse
unfinished file, a file whose size is not a multiple of the sector size, a file
with a `Zone.Identifier` stream, and a file of 0 bytes. A speed comparison
against `CopyFile2` with `COPY_FILE_NO_BUFFERING` on a hard disk with two
partitions would show whether the hard-disk profile earns its place.
