# NTFS indexing research

Research checked on 2026-10-16. This is an informational feasibility note, not
a product contract, implementation plan, or performance measurement.
The [disk discovery plan](13-disk-discovery-plan.md) selects the complete-name
reader, interface, ownership and memory policy based on these findings.

## Finding

TinyTorrent can build its own fast file catalogue without installing or calling
Everything. Everything's developer describes the underlying approach as reading
NTFS's Master File Table (MFT) for the initial catalogue and using the USN change
journal to maintain it. Windows exposes documented APIs for this approach; that
does not mean Everything itself uses exactly those APIs internally.
[Everything developer explanation](https://www.voidtools.com/forum/viewtopic.php?t=357),
[Microsoft enumeration API](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ni-winioctl-fsctl_enum_usn_data).

## Documented Windows route

- Open a local volume such as `\\.\C:` with `CreateFile` and use `DeviceIoControl`.
  Microsoft's volume-access documentation requires administrative privileges,
  `OPEN_EXISTING`, and write sharing. This is the main deployment constraint.
  [CreateFile: physical disks and volumes](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-createfilew#physical-disks-and-volumes).
- `FSCTL_ENUM_USN_DATA` enumerates MFT-derived records in batches, starting with
  file reference zero and continuing from the returned cursor. This avoids a
  recursive directory walk.
  [FSCTL_ENUM_USN_DATA](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ni-winioctl-fsctl_enum_usn_data).
- Records contain file and parent identifiers, a name, and attributes. Paths can
  therefore be reconstructed from the directory relationships. The records are
  versioned and variable length; they are not a list of complete path strings or
  media metadata.
  [USN_RECORD_V2](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ns-winioctl-usn_record_v2).
- `FSCTL_QUERY_USN_JOURNAL` supplies the journal identity and readable positions;
  `FSCTL_READ_USN_JOURNAL` supplies subsequent changes.
  [Query](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ni-winioctl-fsctl_query_usn_journal),
  [Read](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ni-winioctl-fsctl_read_usn_journal).

Implementation inference: capture a journal position before initial enumeration,
then reconcile changes from that position so concurrent creates, deletes, and
renames do not leave a stale catalogue. A persistent catalogue would also need
its journal identity and processed position; the selected plan instead retains
them only with an open query and rebuilds on reconnect. Re-enumerate when the
journal has been replaced or
the required history has been discarded; the journal is bounded and is not a
complete inventory on its own. The disk discovery plan selects reconciliation;
implementation and validation remain.
[Journal identity, readable bounds, and truncation](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ns-winioctl-usn_journal_data_v0).

## Scope and remaining choices

- This verified route is for local NTFS volumes. The cited enumeration API does
  not support SMB. Everything uses folder indexing for FAT, FAT32, exFAT and
  network locations; its separate ReFS support is not evidence that the NTFS
  implementation can simply be reused there.
  [Microsoft scope](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ni-winioctl-fsctl_enum_usn_data),
  [Everything FAQ](https://www.voidtools.com/faq/),
  [Everything index types](https://www.voidtools.com/support/everything/indexes/).
- A file can have several hard-link names. A design promising every path must
  account for them instead of assuming one path per file identifier. Windows
  provides `FindFirstFileNameW`/`FindNextFileNameW` to enumerate those names.
  Completeness of a chosen fast enumeration strategy needs verification.
  [Hard-link enumeration](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-findfirstfilenamew).
- A narrowly privileged reader keeps the normal UI unelevated; Everything
  uses a service for that purpose. The disk discovery plan instead selects a
  separate executable elevated on explicit request, retained while idle to avoid
  repeated prompts. It installs no service and does not elevate the app or
  torrent engine. Library media discovery and engine movement tracking are
  independent opt-ins. Tracking starts the process with the engine but searches
  only for exact missing filenames/sizes when asked, retaining no all-files index.
  The existing [Library contract](../contracts/library.md) remains authoritative.
  [Everything privilege requirement](https://www.voidtools.com/faq/#how_do_i_prevent_the_uac_prompt_when_running_everything).

No code was implemented, disks scanned, benchmarks performed, or builds run for
this research. It establishes a supported technical route, not measured parity
with Everything or completion of Library integration.
