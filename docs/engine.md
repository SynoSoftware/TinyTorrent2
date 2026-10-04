# Engine contract

Target behavior for the native engine in the [architecture](architecture.md).
This document owns torrent operations, persistence, activation, and lifetime.
[Protocol](protocol.md) owns their transport representation.

Dependency selection follows [the architecture](architecture.md#dependencies-and-cost).
The version-specific source evidence below was reviewed against
[libtorrent 2.1.2](https://github.com/arvidn/libtorrent/releases/tag/v2.1.2), the
latest stable release checked on 2026-10-03. This is an evidence baseline, not a
requirement to retain that version when a newer stable release is available.
Revalidate the referenced behavior against the release selected for the build.

## State and work

libtorrent executes transfers and supplies metadata. The engine's main Win32
message-loop thread owns application state and serializes changes from alerts
and commands. Use libtorrent's queueing and transfer limits; the engine owns the
user's policy, not another transfer scheduler. The tray, pipe, and UI cannot
implement their own queue policy or reconstruct state from command
acknowledgements.

Callbacks and worker completions wake the owner through an engine-owned window.
Create that window before producers start and retain it until they have stopped.
Use window messages rather than [`PostThreadMessage`](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-postthreadmessagew),
whose messages can be lost in native modal loops. Bound each dispatch batch, and
finish a state transition before opening a menu or dialog that pumps messages.
Headless execution uses the same owner without showing tray or splash surfaces.

Keep blocking storage and serialization off the tray message loop and libtorrent
alert callbacks. Use libtorrent's existing asynchronous operations and
[resume-data alerts](https://www.libtorrent.org/tutorial.html); an application
scheduler or event framework is not needed merely to receive them.

Peer limits constrain connected peers; upload slots constrain concurrent
unchoked peers. Expose each setting according to its libtorrent meaning.

Enable the alert categories needed by accepted work and drain alerts even when
WinUI is closed. The pinned [alert queue](https://github.com/arvidn/libtorrent/blob/v2.1.2/include/libtorrent/aux_/alert_manager.hpp)
can drop even critical completion alerts. Bound batches of outstanding work and
handle `alerts_dropped_alert` by reconciling affected operations: regenerate
missing checkpoints, query effective state, and retain unresolved storage claims.
If completion cannot be established, report recovery required; do not infer
success or wait forever for a lost alert. Increasing the queue limit alone does
not solve this. Copy or move borrowed alert data needed after the next
[`pop_alerts`](https://github.com/arvidn/libtorrent/blob/v2.1.2/include/libtorrent/session_handle.hpp);
the notification callback only wakes the state owner.

An operation can be accepted, still running, completed, or failed. Cancellation
of a caller's wait does not undo accepted work. Retain enough bounded operation
state to report completion after a torrent disappears from the list, including
after UI reconnection within the same engine instance. Bound retained outcome
records separately from active work; never discard an operation's ownership
while it can still affect files or state. Missing or expired outcome records follow
[protocol reconciliation](protocol.md#outcomes-and-reconnection). After an engine
crash, unfinished outcomes are unknown until recovered, never inferred as success.

## Committed edits

Each edit command carries only the user's intended changes, not a replacement
of an old snapshot. Apply commands in the state owner's accepted order; the
latest explicit choice for an edited field wins. Preserve fields the user did
not edit. An already-satisfied choice needs no further state change, and transfer
telemetry does not invalidate an editor.

Check durable torrent identity and whether the requested operation is legal
when it executes. A removed target or a path held by an unfinished move is still
a reason to refuse. With one active UI, there is no general draft-revision or
conflict-resolution protocol. The [interface](interface.md#committing-edits)
decides when an edit is committed; confirmation and persistence still follow
this contract even when the interaction applies a choice immediately.

Some edits apply asynchronously. In particular, [file priorities](https://github.com/arvidn/libtorrent/blob/v2.1.2/include/libtorrent/torrent_handle.hpp)
can remain unchanged until disk work completes, or be only partly applied on
failure. Keep accepted choices, effective state, and saved state distinct while
that work is pending. Apply dependent edits in accepted order without rebuilding
them from stale effective values. An earlier resume checkpoint cannot establish
that newer choices were applied and saved. On partial failure report the actual
state and operation failure; do not promise rollback.

## Addition and identity

libtorrent is the sole metadata parser, including v1, v2, and hybrid torrents.
The UI supplies source and choices and renders the engine's preview. Magnet
metadata acquisition must neither download payload nor create payload files
before confirmation. Establish that guard before adding the preview; reacting
to metadata arrival is too late. libtorrent's
[magnet guidance](https://www.libtorrent.org/manual-ref.html#magnet-links)
warns that `upload_mode` alone can create empty files. Initial zero file
priorities, including unknown magnet files, must survive automatic management
and any priorities supplied by the source. In the pinned build, set
[`torrent_flags::default_dont_download`](https://github.com/arvidn/libtorrent/blob/v2.1.2/include/libtorrent/torrent_flags.hpp)
before adding a preview and replace any explicit nonzero file priorities with
zero; the flag covers only files without an explicit priority. Build preview
parameters under engine control: clear supplied piece priorities and disable
payload-enabling modes such as `share_mode`. The pinned [initializer](https://github.com/arvidn/libtorrent/blob/v2.1.2/src/torrent.cpp#L2044-L2065)
applies piece priorities after file priorities. Validate that no payload is
downloaded or created with the pinned build.

An unconfirmed preview belongs to its UI connection and is released on disconnect.
Cancel releases that preview, never the files of an existing duplicate. Confirm
rechecks duplicates and transfers ownership to the engine; the accepted addition
survives UI exit. Direct addition, when explicitly enabled, confirms the same
workflow without opening WinUI.

Keep the payload-write guard until membership and the initial user choices
commit, as well as until [storage claims](#payload-ownership) are validated. A failed
commit must not leave an unrecorded addition writing files. After commit, apply
the choices through the same operation owner. Recovery in that interval uses
the committed choices, not an earlier resume checkpoint with preview priorities.
Direct addition follows the same ordering.

Keep full v1/v2 info hashes and discovered hybrid aliases for content and
duplicate detection. Metadata can reveal an alias conflict after initial
addition. Resolve a preview conflict in favor of the accepted torrent. If two
accepted additions conflict, report it before choosing the surviving membership;
never silently merge incompatible choices or delete their files. The pinned
[conflict handling](https://github.com/arvidn/libtorrent/blob/v2.1.2/src/torrent.cpp)
can pause both handles; that pause must not become the user's saved intent.
Receiving an existing handle does not give a preview ownership of that torrent.

Assign each accepted addition a durable torrent identity, distinct from its
info hashes and transient libtorrent handle. Use it for commands, saved state,
and UI drafts. Late alerts and file-operation outcomes belong to that addition;
remove/re-add creates a new identity even for the same content. An old draft or
completion must never target the new addition.

On reconnect, preserve the user's preview inputs and draft choices, but reacquire
and validate the preview. If confirmation may have succeeded, reconcile the
torrent first. A stale preview identifier is not reusable.

If importing old saved data becomes required, recover content hashes from
metadata or resume data and assign each imported addition a durable torrent
identity. Preserve and report records that cannot be recovered; a truncated
best-hash value is not an adequate v2 identity. An import path must not become a
second runtime model.

## Persistence and file safety

Keep one persistence owner for engine data. Store libtorrent state in its resume
representation and only the additional membership, settings, and application
facts the product needs. No storage implementation has been selected here;
choose it against these recovery rules before committing to a format or database.

Refuse an unknown newer store format without rewriting it. Any required migration
belongs to this persistence owner and must preserve a recoverable last good state
before changing existing data.

- Checkpoint dirty transfer state periodically; recovery cannot depend on a
  successful final shutdown.
- Order writes so a late save cannot resurrect a removed torrent or replace newer
  state. Receiving an alert or queueing a write is not storage completion.
- Retain an unsaved checkpoint or the need to regenerate it until commit succeeds.
  libtorrent clears its dirty flag when generating resume data; that flag must
  not suppress a retry after the application's write fails. See the pinned
  [resume-data contract](https://github.com/arvidn/libtorrent/blob/v2.1.2/include/libtorrent/torrent_handle.hpp).
- Confirm additions, removals, and settings as saved only after storage commit
  succeeds. The periodic timer may lose recent progress after a crash; it cannot
  be the reason a confirmed removal returns after restart.
- Report write failures honestly. Live settings and successfully saved settings
  are distinct facts until the write succeeds.
- Choose checkpoint frequency and flush semantics against recovery loss and disk
  cost. Use the chosen store's commit mechanisms, not a second durable command log.

### Payload ownership

One engine owner tracks storage claims for accepted torrents and unfinished file
operations. Reject overlapping storage claims between different torrent identities
before ordinary downloads can create or write payload, not just before deletion
or relocation. Pausing a torrent does not surrender its claim. Sharing a parent
download directory is allowed when the affected files do not overlap; different
hashes or differently spelled paths alone do not establish disjoint storage.
Check absolute paths with relative segments resolved, directory case-sensitivity,
junctions and symbolic links, and existing file identities for hard links.
For paths not yet created, resolve the existing ancestor before comparing the
remaining names. If the affected paths cannot be established, report that
specific problem before allowing access; string spelling is not sufficient.

Simultaneous shared-file seeding is outside the initial
[product scope](architecture.md#product-and-scope). Multiple trackers on one
torrent are a different case. Removing an old torrent while keeping its files
can allow a later addition to reuse them, after the old handle's work ends,
storage claims are released, and the new torrent verifies the content. A UI
must not automate this as a Remove-then-Add sequence or assume the two commands
form one successful replacement.

Acquire claims when paths become known, before enabling payload access. An
accepted magnet with unknown paths remains unable to write until metadata has
been checked. On restart, restore membership and outstanding file-operation
claims before allowing transfers to write. Existing files may be reused only as
the selected torrent's content and verified as needed; presence is not ownership.

If metadata reveals an overlap after a magnet was accepted, retain the addition
without payload writes and report the affected path and its existing owner.
Let the user change the new addition's destination or remove it without deleting
files. Revalidate a new destination before continuing with the saved running or
paused intent; an ownership hold is not a change to that intent.

Deletion and relocation retain claims over every path they can affect, including
both source and destination for a move. Release an operation's claim only when
work has stopped and the resulting locations are known; a failure alert alone
may leave unresolved placement. Row removal and UI disconnection do not release
claims. Reject conflicting additions and moves while unrelated torrents remain
eligible to run. libtorrent permits
[re-addition before work on an old handle has ended](https://www.libtorrent.org/reference-Session.html#remove-torrent()),
so identity and path checks must outlive list membership.

### Removal and relocation

For delete-data, commit membership removal and the facts needed to recover the
pending deletion before starting to delete payload. If that commit fails, do not
delete files. Removal and deletion have separate outcomes: failed or uncertain
deletion remains reportable after the row is gone. Recovery must neither restore
the removed membership nor automatically retry an uncertain destructive action.

A relocation moves the scope presented to the user. For a dedicated torrent
folder, include its contents, such as user-added subtitles, when no other
torrent has claims inside it. A shared download directory is not that torrent's
folder: confine the move to its payload, or refuse if that cannot be done safely.
Check and retain claims for the actual move scope at both ends. Do not overwrite
destination collisions or discard source copies in favor of unverified files.
The pinned
[`move_storage` contract](https://github.com/arvidn/libtorrent/blob/v2.1.2/include/libtorrent/torrent_handle.hpp)
defaults to replacement, documents a race in `fail_if_exist`, and permits moving
unrelated files from the torrent's directory. Choosing that flag or doing a
preflight scan is not itself a no-overwrite guarantee.

Before relocation starts, commit the torrent identity, move scope, source,
destination, and unfinished-operation state needed for recovery in the existing
store. After an interruption, keep the affected torrent unable to write until
actual file locations and contents have been reconciled and the resulting
location is committed. Preserve the user's running/paused intent separately
from this safety hold. A partial move is not rolled back merely because an alert reports failure;
retain the affected claims and report recovery required when placement remains
uncertain. These are recovery facts for unfinished work, not a durable command
history or another persistence owner.

## Startup and activation

Opening TinyTorrent starts or activates the engine and opens WinUI. Optional
start-at-sign-in starts the engine with WinUI closed. Simultaneous launches
resolve to one owner; subsequent launches forward their activation and exit only
after ownership of that request has been accepted or rejection reported.

Shortcuts, file associations, and taskbar relaunch enter through the engine.
Use one [AppUserModelID](https://learn.microsoft.com/en-us/windows/win32/shell/appids)
for the cooperating processes, window, and engine-targeting shortcut. Without a
shortcut, set the window's [relaunch command and display-name resource](https://learn.microsoft.com/en-us/windows/win32/properties/props-system-appusermodel-relaunchcommand)
together. Verify pin/close/relaunch with the selected unpackaged installer.

A freshly started WinUI executable without an engine-reserved launch forwards
its activation through the engine and exits without creating a product window.
The engine validates its reserved child by its live process identity; a command
line switch alone does not establish ownership. That child connects to become
the UI instead of forwarding again. A UI already running when the engine restarts
reconnects through the existing pipe and is adopted as the live owner. Bind that
adoption to the [actual pipe-client process](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-getnamedpipeclientprocessid)
and its live handle under protocol isolation, not a PID supplied in a message.

WinUI acquires one logon-scoped [mutex](https://learn.microsoft.com/en-us/windows/win32/sync/using-mutex-objects)
without waiting and holds it on its main thread from before window/draft creation
until exit. This preserves one draft owner across engine restarts. If a launch
races the surviving UI's reconnection, the candidate that cannot acquire the
guard reports that fact and exits; Open waits boundedly for the surviving UI or
reports it unavailable.
Candidate exit clears only that candidate's reservation, never an adopted UI's
ownership. The guard stores no product state; the engine still owns launch policy.

Bound pending additions and report overload. Readiness means the engine can
answer, rather than merely having a process or tray icon. Reserve a UI launch
before starting it asynchronously, so repeated Open requests share one launch
and one owner of drafts. Bind that ownership to the UI process lifetime, not its
pipe: a timeout or disconnect can leave a live UI with unsaved input. Release the
reservation on confirmed process exit or failed launch. Bound readiness waiting
and make failed startup retryable without starting a second live UI owner.
Use the [process handle](https://learn.microsoft.com/en-us/windows/win32/procthread/terminating-a-process)
to observe exit. Endpoint and data-directory exclusion follow
[protocol isolation](protocol.md#isolation).

For a user-directed Open, carry [foreground permission](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-allowsetforegroundwindow)
through the launch or forwarding path where Windows permits. Restore/show the
window and use normal taskbar attention if foreground activation is denied.
Background startup and status changes do not take focus.

The tray uses standard Win32 menus, keyboard behavior, accessibility, and system
colors. Restore its icon after Explorer restarts. The engine owns this tray,
the splash, and native startup failure feedback; product dialogs belong to WinUI.

The splash is a small native window with the application icon and short localised
status. Use the documented [DWM transient backdrop](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/ne-dwmapi-dwm_systembackdrop_type)
where supported, with a readable solid fallback on older Windows versions or
when system settings require it. Load no UI framework for it. Show it only while
opening WinUI, avoiding a flash for an already-ready window; close it when the
product window is visible or a bounded wait expires. Sign-in with WinUI closed
shows no splash.

Closing the splash must not hide a failed Open. On process-creation failure or
unexpected exit before window readiness, show a localised native error dialog
with a useful reason, Retry, and Close. Keep it independent of the WinUI runtime.
An expected candidate handoff to the surviving UI is not a launch failure.
If readiness times out while the process is still alive, report that it has not
opened and retain its ownership; a timeout does not authorize another launch.
Retry becomes available after failure or confirmed exit and reuses the existing
activation handling, without resubmitting an addition whose outcome is unknown.
Closing this feedback leaves existing transfers running. A late ready window
resolves the startup feedback rather than leaving a stale failure visible.

### Windows registration

One engine component registers, unregisters, and reads TinyTorrent's per-user
file/link handlers and start-at-sign-in entry. The registry is the authority for
these registrations; do not mirror them in saved preferences. Read the expected
values and target paths, not just whether a key exists, and report partial or
failed changes truthfully.

- Register application-specific ProgIDs for `.torrent` and `magnet:` under
  `HKCU\Software\Classes`, plus the capabilities/RegisteredApplications entries
  Windows needs to list TinyTorrent in Default apps. Handler commands enter the
  engine's ordinary activation path. Quote paths and treat the opened file or
  URI as input, never as engine maintenance options. Unregister only TinyTorrent's
  entries and references; leave shared keys and other applications untouched.
- Use one value under
  `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` to start the engine with
  WinUI closed. The documented [Run mechanism](https://learn.microsoft.com/en-us/windows/win32/setupapi/run-and-runonce-registry-keys)
  may be delayed or disabled by Windows. Remove only that value when requested.
  Never edit undocumented StartupApproved data or override Windows policy.

For a request to open torrents with TinyTorrent, register its handlers, query
the current defaults through the supported
[Windows defaults platform](https://learn.microsoft.com/en-us/windows/apps/develop/windows-integration/default-apps-platform),
and direct the person to Windows' default-app choice only where needed. Refresh
the observed defaults when they return. Keep this small query at the registration
owner; do not modify UserChoice or build an application chooser. Cancelling
Windows' choice leaves the existing defaults intact.

The sign-in switch adds or removes the Run entry through this same owner. It
expresses TinyTorrent's startup setting; Windows can independently disable
startup. Provide access to Windows Startup settings without trying to reverse
its override through undocumented keys. Neither registration nor observation
creates a second saved preference.

The installer uses `--register` on first installation and `--unregister` before
removing program files, with individual operations for any deselected setup
choice. Preferences sends the same operations through the pipe. Maintenance
launches forward to a running engine or use the same owner in a short-lived
native process under instance exclusion, without starting transfers or WinUI.
On upgrade, repair only registrations still requested; never use an unconditional
`--register` to undo the user's off choice. Registration is completed for the
installing user, not an administrator account used to install a prerequisite.

## Closing and shutdown

Closing WinUI normally exits without confirmation. Resolve actual unfinished
edits according to [the interface](interface.md#committing-edits), and do not
silently drop changes already committed in the UI but still being submitted.
Accepted operations and transfers continue in the engine. UI-only snapshots,
detail collection, and graph history stop or are released with their last
consumer; tray status, queue policy, swarm activity, and persistence continue.

Tray Exit asks WinUI to prepare for close while engine commands still accept
committed edits. WinUI agrees without a dialog when there is no unfinished work;
it can cancel Exit when the user chooses to keep an unfinished edit. Agreement
keeps the window available for status and necessary recovery, with new editing
disabled, until the engine finishes Exit or abandons it. After agreement:

1. Stop accepting ordinary new mutations and settle accepted state changes and
   writes. Continue to accept the recovery actions needed to finish safely.
2. Safely interrupt resumable work where supported. Keep status and recovery
   actions available for relocation or other work that cannot yet stop safely.
3. Pause the session and await transfer/disk quiescence without changing each
   torrent's saved paused/running intent. Track actual outstanding work rather
   than expecting a new pause alert from an already-idle torrent.
4. Settle outstanding resume requests, including success, not-modified, and
   failure outcomes. Obtain final resume data and await application storage
   commit; a resume-data alert alone does not prove payload writes have flushed.
5. Tell the attached WinUI process to close and observe its exit, using the hung
   UI policy below if needed. Destroy/join libtorrent, close the pipe and tray,
   then release data-directory ownership last.

If recovery or a required write prevents safe completion, keep its status and
actions available and allow the user to cancel Exit. Abandoning Exit restores
normal interaction and resumes the session without changing individual torrent
intent.

Keep the owner pumping messages while asynchronous shutdown work settles. Never
join a worker that still needs the owner to process its completion; final joins
follow those handoffs.

Session pause preserves individual torrent intent; per-torrent pause can change
the saved flag. The [pause completion](https://www.libtorrent.org/reference-Alerts.html#torrent_paused_alert),
resume-data generation, application commit, and session join are separate facts.
In the pinned [implementation](https://github.com/arvidn/libtorrent/blob/v2.1.2/src/torrent.cpp),
`save_resume_data(flush_disk_cache)` does not wait for file-release completion
before posting resume data. Its name is not a power-loss durability guarantee.

A hung UI cannot block exit forever; report it and let the user choose whether
to discard any unfinished input. A short deadline does not justify killing an
unsafe file operation. Windows logoff/shutdown uses a bounded persistence path
that does not depend on an interactive confirmation. Abrupt termination may lose
changes since the last successful checkpoint; do not promise zero loss.

A UI crash leaves transfers running and the tray able to reopen it. An engine
failure makes WinUI report that downloads stopped and offer restart. Register the
engine for [Windows application restart](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-registerapplicationrestart)
with arguments that restore background state, never replay the original addition
or a file command. For crashes and hangs, Windows offers restart with user
consent and only after at least 60 seconds of runtime; this is not a guarantee of
unattended recovery. With WinUI closed there is no resident watchdog. If Windows
does not restart it, relaunching the app restores saved state through the same
recovery path. There is no setting to exit a separate tray while leaving the
engine behind.

The [installed-update path](architecture.md#installation-and-updates) waits for
coordinated application Exit; window close alone does not stop the engine.
Postpone replacement if Exit is cancelled
or cannot finish safely, preserving the installation and saved data. Launch the
engine and UI from the same release. An externally forced termination still
uses the crash-recovery path. This rule does not require a resident updater.

## Network preferences

Use libtorrent's port mapping with UPnP and NAT-PMP enabled by default, one
on/off preference, and a configurable listen port. The engine applies those
choices; it does not implement another router client. Keep encryption at the
pinned [upstream defaults](https://github.com/arvidn/libtorrent/blob/v2.1.2/src/settings_pack.cpp).
Mapping success is not proof of public reachability or firewall permission.

## Notifications and sleep

Completion notifications are enabled by default and use the existing tray's
[`Shell_NotifyIcon`](https://learn.microsoft.com/en-us/windows/win32/shell/notification-area)
path. Respect Windows notification suppression and quiet time; delivery is best
effort and needs no WinUI process or new notification runtime. Notify when the
currently wanted files finish downloading, not when an existing seed is restored
or rechecked. Coalesce a burst of completions instead of flooding the desktop.
Clicking a single completion opens its current folder; a combined notification
opens the application. Never execute a downloaded file. Keep only bounded pending
notification context, identified by durable torrent identity rather than a stale
path. Completion state remains visible in WinUI when Windows suppresses feedback.

The idle-sleep preference starts enabled for active payload downloads on mains
power. Seeding alone, paused/queued torrents, metadata previews, and torrents
blocked by an error do not keep the PC awake. One engine-owned power request
uses the documented [power-request API](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-powersetrequest)
with system-required and execution-required requests while needed, and a
localised reason. Clear them when the condition ends, on battery power, or on
Exit. The display may turn off;
explicit Sleep, lid closure, and Windows power policy remain authoritative.
Reconcile the request after resume or a power-source change. Do not use away
mode or promise uninterrupted transfers through user-requested sleep.

## Diagnostics

Write engine errors and major lifecycle events to a local log in the data
directory. Keep one current file and one previous file, each capped at 1 MiB.
Record operation kind, stable identifiers, and error codes; omit credentials,
raw magnet/tracker URLs, and payload contents. Do not log every transfer update.
Bound and coalesce repeated diagnostics, keeping file I/O off the state owner.
A full or unwritable log must not stop transfers. There is no automatic upload
or second telemetry pipeline.

## Disk write caching

Start with the selected release's upstream Windows disk backend and write policy.
In the reviewed **2.1.2** release, leaving
`session_params::disk_io_constructor` unset selects pread. The reviewed
[constructor](https://github.com/arvidn/libtorrent/blob/v2.1.2/src/session.cpp)
is the authority for that selection. Its Windows
[write policy](https://github.com/arvidn/libtorrent/blob/v2.1.2/src/settings_pack.cpp) is
`disk_io_write_mode = write_through`. The
[pread implementation](https://github.com/arvidn/libtorrent/blob/v2.1.2/src/pread_storage.cpp)
uses that setting; `disk_write_mode = always_mmap_write` does not configure it.

Disk caching is automatic engine policy, with no Preferences control, saved
override, or user-facing restart state. Add a choice only if a concrete user
need and measured behavior justify it. An imported `disk_cache_mb` value is not
an equivalent budget and does not create an override.

The default is the starting point, not a claim of lower memory use. Take the
first real workload measurement required by [testing](testing.md#resource-checks).
Compare mmap if that reveals a disk or memory problem it could plausibly improve;
choose from memory, throughput, and correctness together. No backend comparison
or performance result exists in this repository yet.
