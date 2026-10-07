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

Queue moves operate on libtorrent's download queue. Completed seeds have no
download queue position and are refused as move targets; a placement before a
seed means append to the download queue. Restore applies saved positions only
to torrents currently in that queue, so seeds cannot create gaps in its order.

Pause all pauses the libtorrent session, which keeps each torrent's own running
or paused state, so Resume all does not start torrents the person paused one by
one. The session pause is saved and survives a restart. While all are paused, a
torrent's own Resume takes effect at Resume all, and the window and the tray say
All paused, so the person sees why nothing moves.

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
missing checkpoints and query effective state.
If completion cannot be established, report recovery required; do not infer
success or wait forever for a lost alert. Increasing the queue limit alone does
not solve this. Copy or move borrowed alert data needed after the next
[`pop_alerts`](https://github.com/arvidn/libtorrent/blob/v2.1.2/include/libtorrent/session_handle.hpp);
the notification callback only wakes the state owner.

An operation can be accepted, still running, completed, or failed. Cancellation
of a caller's wait does not undo accepted work. The confirmed list the UI reads
again is a command's outcome, so the engine keeps no store of past outcomes. A
move shows its progress and failure on its torrent. A file deletion continues
after its torrent leaves the list, so its failure is
[notified](#notifications-and-sleep) and logged. Never discard an operation's
hold on files while it can still affect them. After an engine crash, unfinished
work is unknown until recovered, never inferred as success.

Routine status uses libtorrent state-update alerts without piece bitfields.
Retain the latest status per accepted torrent and classify it once for summary
rows, tray counts and power policy. This avoids synchronous per-torrent queries
on every tray or window refresh. A new or restored torrent gets one initial
status; metadata and file details remain on demand.

Completed means that received payload is ready on disk. After libtorrent reports
the download finished, keep its completion pending until `cache_flushed_alert`;
the row and completion notice share that acknowledgement. Rechecks do not create
download notices. Dropped-alert recovery queries effective finished state and
requests a fresh flush acknowledgement rather than inferring disk readiness.

Each torrent has one checkpoint write in flight. If newer resume data arrives
during it, retain the newest result and write it next. Generating resume data
clears libtorrent's dirty flags, so requesting it again cannot replace retaining
that result. The torrent stays checkpointing until these writes have settled.

The engine records the session's total download and upload rate once a second,
also while WinUI is closed, so the Speed view shows what happened while the
window was closed. Keep the last five minutes at one sample a second for a live
chart, and the last 24 hours at one averaged sample a minute to cover a day away
from the window. The history is session-wide: a history per torrent would cost
memory for every torrent while the window is closed, the state this product
measures first. History is not saved: the time while the engine was stopped is
unknown in any case, so after a restart the chart starts empty.

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

File edits accept the same priorities as addition, with padding always unwanted,
but may make every file unwanted. A torrent already in the list can keep its
content and seed while the person changes its selection; only addition needs at
least one wanted file to give the new download useful work.

Download in sequential order and Download first and last pieces first are
choices saved with each torrent in `settings.json`. Addition accepts them, and
a command changes them later for selected torrents. Sequential order is
libtorrent's `sequential_download` [torrent flag](https://github.com/arvidn/libtorrent/blob/v2.1.2/include/libtorrent/torrent_flags.hpp).
First and last pieces is not a libtorrent flag: the engine gives top priority
to 1 % of each wanted file at each end, at least one piece, so a media player
can read a file's header and index early. libtorrent sets
piece priorities again from the file priorities when a file-priority change
completes, so the engine raises those pieces again after each change and after
verification, which can recreate libtorrent's piece picker. Startup
discards the piece priorities in resume files, so a resume file older than the
choice cannot override it.

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
survives UI exit. When the Show the Add form preference is off, direct addition
confirms the same workflow without opening WinUI.

Matching sources staged by the same connection share one preview and merge their
tracker URLs. This keeps a file and its magnet from acquiring parallel handles.
Confirmation pauses a magnet preview and waits for libtorrent to acknowledge its
new save path before committing membership; its payload guard remains in place.
Unknown metadata still permits confirmation with all files wanted. Known metadata
accepts priorities 0 (unwanted), 1 (low), 4 (normal), and 7 (high), with at least
one wanted file. A batch shares destination and paused intent; individual file
choices belong to a single-source form or the Files inspector after addition.

Add to top of queue commits the new torrent at the front of the saved download
order before releasing its payload guard. The queue owner reapplies that order
when a torrent enters downloading, because libtorrent appends a torrent whose
wanted files change it from finished to downloading. The option defaults to off
and changes neither paused intent nor automatic queue limits. A duplicate keeps
its existing position, because it is not a new addition.

An accepted running magnet acquires metadata before entering automatic queue
management. When metadata arrives, the same intent owner applies normal priority
to every non-padding file for the saved all-files choice, then restores queue
management. In libtorrent 2.1.2, `default_dont_download` is an initial-parameter
flag; unsetting handle flags does not change its delayed initialization. Explicit
priorities after metadata prevent an accepted magnet from remaining finished
with no wanted payload. Unconfirmed previews retain their guard.
Until metadata provides a name, an unnamed accepted magnet shows its full info
hash, so the person can identify its row rather than seeing a blank torrent.

Keep the payload-write guard until membership and the initial user choices
commit. A failed commit must not leave an unrecorded addition writing files. After commit, apply
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

## Persistence and file safety

Keep one persistence owner for engine data. Store each torrent's libtorrent
resume data in its own file, named by its durable torrent identity, and settings and the other application facts in `settings.json`. Write
each file under a temporary name and rename it over the old one, so a crash
leaves the old file or the new one, never a partial file. One writer queue
performs every write in order. Each file changes on its own, so the rename makes
every write atomic and a database is not needed.

Refuse an unknown newer store format without rewriting it. Any required migration
belongs to this persistence owner and must preserve a recoverable last good state
before changing existing data.

A setting's key in `settings.json` is file format, and the settings command and
reply use the same key. Renaming a key therefore needs a migration. Without one,
loading does not find the saved value under the new key, and the setting returns
to its default for every user.

An unreadable resume file affects only its own torrent. Startup adds that
torrent again from the hashes `settings.json` saved, as a magnet link: it
fetches the metadata from peers and checks the files already on disk, so no
downloaded data is lost. An unreadable `settings.json` still refuses the whole
store, because it is the list of torrents.

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
  cost. The rename is the commit; do not add a second durable command log.

The first implementation checkpoints every 30 seconds and limits outstanding
resume requests to eight. Failed writes retain the need for another checkpoint.
Membership and explicit choices commit immediately through the same ordered
writer. The writer flushes temporary metadata before atomic replacement with
`MoveFileEx`; this does not claim payload power-loss durability. Periodic
checkpoints cover libtorrent's dirty state and retained failed saves, so idle
torrents do not require a disk flush every 30 seconds. Queue overload completes
as a failed storage outcome rather than escaping the state owner's dispatch.
Payload disk errors retain their own reason; checkpoint failures do not replace
the actual transfer status. Resume clears libtorrent's disk error and upload
mode before applying the saved running intent.

Mark each payload file as downloaded from the internet when it completes while
its torrent is downloading, by writing the internet zone to the file's
`Zone.Identifier` stream, as browsers do. Windows then shows its SmartScreen or
Protected View warning before the person runs or edits a file from a torrent.
Files that verification finds already complete are not marked, because they may
be the person's own files. There is no setting: the protection shows nothing
until it is needed, and a person who trusts a file unblocks it in its
Properties, as for any download. Marking is best effort. A volume, network
share, or permission that refuses the stream is written to diagnostics and
never fails the download, because the finished file matters more than the mark.

### Shared files

Several torrents can use the same files, for example the same content seeded
from two trackers. People who seed on several trackers depend on it. libtorrent
refuses a second torrent with the same info hash; the engine adds no other
ownership check. A
torrent that finds existing files verifies them before using them and downloads
the pieces that do not match, which overwrites those files. The
[Add form](interface.md#add) therefore names the torrents that already use files
at the chosen destination before the person confirms.

Delete files and Move must not reach the files of a torrent outside the command.
When either runs, compare its full paths, without case as Windows compares names,
with the files of the torrents in the list that the command does not include, so
a command that includes every torrent using a file can delete or move it. Delete
files keeps a file that a torrent outside the command uses, and reports it. Move
refuses when its scope contains such a file, names those torrents, and offers to
move them together. A move of several torrents moves the files once and points
the other torrents at the new folder with libtorrent's `reset_save_path` move
flag, which verifies there instead of moving again. libtorrent permits
[re-addition before work on an old handle has ended](https://www.libtorrent.org/reference-Session.html#remove-torrent()),
so a deletion or move that has not finished stays in this comparison after its
torrent leaves the list.

### Removal and relocation

For delete-data, commit membership removal before deleting payload. If that
commit fails, do not delete files. Removal and deletion have separate outcomes:
a failed deletion is notified after the row is gone. A deletion
interrupted by a crash is not repeated, because repeating an uncertain
destructive action is unsafe; the remaining files stay on disk, and recovery
does not restore the removed torrent.

A relocation moves the torrent's own files, as libtorrent's
[`move_storage`](https://github.com/arvidn/libtorrent/blob/v2.1.2/include/libtorrent/torrent_handle.hpp)
does: other files in its folder, such as added subtitles, stay where they are,
and only folders left empty are removed. Use `fail_if_exist`, so a file already
at the destination is reported as a collision instead of replaced; the default
replaces it. The flag's documented race, another program creating a destination
file during the move, is rare and needs no further mechanism.

A collision offers Use the files there. It points the torrent at the
destination with the `reset_save_path` move flag, which verifies the files
instead of moving them, so a person who moved the files or whose drive letter
changed gets the torrent back without downloading it again. Pieces that do not
match are downloaded again over those files.

One payload operation runs at a time. Its source and destination paths stay held
until disk work has a known outcome. Add confirmations wait by reporting Files
busy, including magnets whose paths are still unknown. A file operation also
waits for pending additions and outside torrents still acquiring metadata: an
unknown path cannot establish that destructive work is safe. Other torrents and
settings remain usable.

For a group with partially overlapping file lists, check the whole destination
before moving anything. The first member uses `fail_if_exist`; later members
whose files are already moved by that group use `reset_save_path`. A member with
both shared and distinct files uses `dont_replace` after checking its distinct
destination paths. This preserves the already moved group files while moving
its remaining files. Every member is verified before its saved intent resumes.
Two distinct source files cannot map to one destination path. An external
program creating a destination during a move retains the existing documented
race; no second file transaction or rollback mechanism is added.

Before a move starts, save its destination with the torrent and clear it when
the entire selected group's move ends. Reserve its paths before that commit,
so an addition cannot enter while the write is pending. If no disk move began,
a failed destination preflight clears the newly staged markers through the
same writer. An existing interrupted marker requires explicit Use the files
there; an ordinary retry cannot replace or clear that unresolved choice. Delete
files also refuses until this recovery establishes the actual folder, because a
held destination is not proof that existing files there belong to the torrent.
After a crash during a move, that saved destination keeps the
torrent paused with a Move interrupted error, so it does not download again
into the old folder. Moving it to the folder that holds the files offers Use the
files there, and verification establishes what is there. The user's running or
paused intent does not change, and the move is neither rolled back nor repeated.

A completed relocation requires verification after restart until a safe
destination checkpoint has committed. Restoring discards old piece claims and
seed mode when that verification is pending or the checkpoint names an older
path. Clear this requirement only after the saved destination checkpoint claims
no pieces that the current checked torrent lacks, and checking has ended. This
also covers Use the files there in the same folder: matching names and sizes do
not establish that the bytes match the torrent.

### Unfinished files

A file that is still downloading has the `.!tt` suffix after its real name, such
as `movie.mkv.!tt`, so a file with its real name is always a finished file.
People, media libraries, and other programs then never take a partial file for
a finished one. Nearly everyone wants this, so it is fixed behavior with no
setting.

- When a torrent is added, each file that does not exist at the destination
  gets its suffixed name through `add_torrent_params::renamed_files`. A file
  that already exists there keeps its name, so verification finds it instead of
  downloading it again.
- When a file completes, Windows gives it its real name in the same folder,
  without replacing an existing file, then `rename_file` updates libtorrent's
  mapping. Finishing is always a rename and never a copy. If another program
  has the file open, Windows refuses the rename; the file keeps its suffix and
  the engine tries again later, without reporting a download error.
- libtorrent saves the current names in the resume data, so they survive a
  restart.
- The shared-files comparison uses the torrent's original name and its actual
  disk name, so a file is the same file before and after it finishes. It does
  not strip suffixes from unrelated names that happen to end in `.!tt`.
- Open on a file that is still downloading starts the program registered for
  its real extension, through `ShellExecuteEx` with `SEE_MASK_CLASSNAME`, so
  watching a video during a sequential download still works in players that
  read the content.

## Startup and activation

Opening TinyTorrent starts or activates the engine and opens WinUI. Optional
start-at-sign-in starts the engine with WinUI closed, and so does every start
when the person chose to start in the notification area. That choice is known
once startup has read the settings, so a start opens WinUI only then. Simultaneous launches
resolve to one owner; subsequent launches forward their activation and exit only
after ownership of that request has been accepted or rejection reported.

Shortcuts, file associations, and taskbar relaunch enter through the engine.
Use one [AppUserModelID](https://learn.microsoft.com/en-us/windows/win32/shell/appids)
for the cooperating processes, window, and engine-targeting shortcut. Without a
shortcut, set the window's [relaunch command and display-name resource](https://learn.microsoft.com/en-us/windows/win32/properties/props-system-appusermodel-relaunchcommand)
together. Verify pin/close/relaunch with the selected unpackaged installer.

A WinUI executable started directly, for example from the debugger, starts the
engine if none is running and then connects like any other UI. It needs no
launch token or process check, because [protocol isolation](protocol.md#isolation)
already admits only the same user and logon. A UI already running when the
engine restarts reconnects through the same pipe.

WinUI acquires one logon-scoped [mutex](https://learn.microsoft.com/en-us/windows/win32/sync/using-mutex-objects)
without waiting and holds it on its main thread from before window/draft creation
until exit, so one window owns the drafts, also across engine restarts. A WinUI
that cannot acquire the mutex forwards its activation through the engine to the
existing window and exits. The mutex stores no product state; the engine still
owns launch policy.

An existing UI receives incoming sources and applies the shared Add-form choice,
so they join its open draft and reveal confirmed additions despite table filters.
With no UI and Show the Add form off, the engine confirms through the same
guarded addition workflow at the default destination.

Bound pending additions and report overload. Readiness means the engine can
answer, rather than merely having a process or tray icon. While a WinUI the
engine started is still opening, further Open requests wait for it instead of
starting another; the engine observes that launch through its
[process handle](https://learn.microsoft.com/en-us/windows/win32/procthread/terminating-a-process).
The mutex, not this wait, guarantees one window. Bound readiness waiting and make
failed startup retryable. Endpoint and data-directory exclusion follow
[protocol isolation](protocol.md#isolation).

For a user-directed Open, carry [foreground permission](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-allowsetforegroundwindow)
through the launch or forwarding path where Windows permits. Restore/show the
window and use normal taskbar attention if foreground activation is denied.
Background startup and status changes do not take focus.

The tray uses standard Win32 menus, keyboard behavior, accessibility, and system
colors. Restore its icon after Explorer restarts. Its tooltip shows the total
download and upload speed, or why transfers are stopped. While any torrent has
an error, the tooltip starts with the error count and, while Notify about
problems is on, the icon shows its error variant, so the problem stays visible
after its notification has gone. Both follow the error count and clear when no
torrent has an error. The complete menu has
two live, nonclickable status rows, a separator, Show window and Pause Transfers,
a separator, and Exit. The first status row shows aggregate download and upload
speed; the second shows active and queued counts. While the session is paused,
the second row is Paused with the torrent count, and Resume Transfers replaces
Pause Transfers. The pause command uses the saved session pause above and keeps
each torrent's own choice. A single left-click shows this same lightweight menu;
it never opens WinUI. Wait for Windows' double-click interval before showing it,
so a double-click opens the application once without first opening the menu.
Right-click shows the menu immediately; keyboard activation runs its default
Show window command. The menu has at
most two status rows and three commands; no secondary actions belong there, so
its immediate status and controls remain readable at a glance. The engine owns this
tray. Its status text uses the normal menu text color rather than a disabled
command's grey, because status remains information to read, not an unavailable
action. Only those two rows use native owner drawing; the commands retain normal
Windows behavior. The engine owns the
splash and native startup failure feedback; product dialogs belong to
WinUI.

The splash is a compact, rounded, captionless native blurred surface with the
application icon and short localised status. It has no buttons or recovery
choices: the tray owns Open and Exit. It follows the app's theme choice, which
resolves Follow Windows to the Windows light/dark mode, with a readable solid
fallback when transparency is disabled, contrast mode is active, or the blur is
unavailable. The blur is the accent blur of the original TinyTorrent splash; the
documented DWM system backdrop left the inactive splash a flat fill. Load no UI
framework for it.

The window alone decides that it is ready. It stays cloaked until its first
complete usable frame has rendered: its saved place, theme and language, and a
deliberate loading or content state. Then it sends `ready`; it does not wait for
later data such as statistics, and a failed placement or language load falls
back to the defaults. Startup reads `settings.json` before the resume files, so
the engine knows the saved choices early. The window still sees `loading` until
the transfers have loaded, so a large library delays a cold start's first frame;
showing the window earlier needs a protocol state for loaded settings.

The engine owns only the splash timing, and shows a splash only while it waits
for a window to appear and the Show the splash screen preference allows it. The
splash never waits before it appears. A cold launch, where the engine still
loads, always shows it. A warm launch shows it unless the average of the recent
warm launches, each timed from starting the window process to `ready`, is
within the splash's minimum time; then a splash would only delay the window.
The engine keeps those times in memory, so its first warm launch shows the
splash. A splash on screen stays for a minimum time: the engine answers `ready`
only then, the window appears, and the window's activate reply closes the
splash, so the window always appears over it. `minimumDwell` and
`launchSamples` in the splash source hold the values. Failure or Exit closes
the splash immediately, and a window whose connection fails appears at once
with that failure. An engine the window started itself has five seconds to
accept the connection before a timeout counts as that failure; showing the
window earlier would show it in the system theme and switch to the saved one
moments later. Sign-in with WinUI closed shows no splash.

On process-creation failure or unexpected exit before window readiness, close
the splash and report the reason through the existing tray notification path.
The tray's Show window retries opening; there is no second recovery dialog.
A launched WinUI that hands off to an existing window and exits is not a launch
failure. If readiness times out while the process is still alive, report that it
has not opened; a timeout does not authorize another launch.
Opening again reuses the existing activation handling and process checks,
without resubmitting an addition whose outcome is unknown. Existing transfers
continue after a failed Open. A late ready window ends the splash wait.

### Windows registration

One engine component registers, unregisters, and reads TinyTorrent's per-user
file/link handlers and start-at-sign-in entry. The registry is the authority for
these registrations; do not mirror them in saved preferences. Read the expected
values and target paths, not just whether a key exists, and report partial or
failed changes truthfully.

- Register per-user handlers for `.torrent` and `magnet:`, with the entries
  Windows needs to list TinyTorrent in Default apps. Handler commands enter the
  engine's ordinary activation path and treat the opened file or URI as input,
  never as engine maintenance options. Unregister only TinyTorrent's entries;
  leave shared keys and other applications untouched.
- Start at sign-in uses one per-user
  [Run entry](https://learn.microsoft.com/en-us/windows/win32/setupapi/run-and-runonce-registry-keys),
  which starts the engine with WinUI closed. Windows may delay or disable it.
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

**Owner ruling:** registrations follow the copy the person runs. Every start
of an engine on the default store moves TinyTorrent's existing handler and
sign-in entries to its own executable, so a moved, rebuilt or upgraded copy
keeps working and no entry points to a deleted folder. It never re-registers
what the person turned off. An engine started with its own store (`--data`),
such as a test's, is not the person's copy and leaves the entries alone.
Observation reports each registration as starting this copy, another
TinyTorrent copy (with that executable), or nothing.

The installer registers on first installation and unregisters before removing
program files, through the engine's maintenance commands, with individual
operations for any deselected setup choice. Preferences sends the same
operations through the pipe. Maintenance
launches forward to a running engine or use the same owner in a short-lived
native process under instance exclusion, without starting transfers or WinUI.
Registration is completed for the installing user, not an administrator account
used to install a prerequisite.

## Closing and shutdown

Closing WinUI normally exits without confirmation. Resolve actual unfinished
edits according to [the interface](interface.md#committing-edits), and do not
silently drop changes already committed in the UI but still being submitted.
Hide the window before waiting when no draft, dialog or picker needs it, so
closing does not leave a disabled window on screen. Show it again if an edit
decision or failed close needs the person's attention.
Accepted operations and transfers continue in the engine. UI-only snapshots and
detail collection stop or are released with their last consumer; tray status,
queue policy, swarm activity, [speed history](#state-and-work), and persistence
continue.

Exit is in the tray menu and, as Exit and stop transfers, in the window's File
menu; the window's own Exit only closes the window. Engine Exit first closes
the window by the same rules as Close: a prompt appears only for actual unfinished
input, and Cancel in that prompt cancels Exit. If a move or file deletion is running, Exit
waits for it to finish without a second prompt, because stopping it midway leaves
files in two places. Then:

1. Stop accepting new commands and settle accepted state changes and writes.
2. Pause the session and await transfer/disk quiescence without changing each
   torrent's saved paused/running intent. Track actual outstanding work rather
   than expecting a new pause alert from an already-idle torrent.
3. Settle outstanding resume requests, including success, not-modified, and
   failure outcomes. Obtain final resume data and await application storage
   commit; a resume-data alert alone does not prove payload writes have flushed.
4. Destroy/join libtorrent, close the pipe and tray, then release data-directory
   ownership last.

An active draft prompt is not an unresponsive window. WinUI acknowledges when
Exit is waiting for that choice; the window timeout pauses for the person and
resumes when closing continues. A failed window-close preparation cancels Exit
and preserves the window. Open during Exit is refused as stopping rather than
acknowledged and discarded. Reopening a disconnected but living UI retains its
process and starts the same bounded readiness wait used for a new window.

If the final save fails, report it through the tray and keep the engine alive.
Choosing Exit again retries the same shutdown operation. Do not discard unsaved
state automatically or offer recovery buttons on the splash.

Keep the owner pumping messages while asynchronous shutdown work settles. Never
join a worker that still needs the owner to process its completion; final joins
follow those handoffs.

Session pause preserves individual torrent intent; per-torrent pause can change
the saved flag. The [pause completion](https://www.libtorrent.org/reference-Alerts.html#torrent_paused_alert),
resume-data generation, application commit, and session join are separate facts.
In the pinned [implementation](https://github.com/arvidn/libtorrent/blob/v2.1.2/src/torrent.cpp),
`save_resume_data(flush_disk_cache)` does not wait for file-release completion
before posting resume data. Its name is not a power-loss durability guarantee.

A hung UI ends the close wait with a tray notification and cancels that Exit
attempt, preserving unfinished input. The tray can request Exit again when the
window responds. Windows logoff/shutdown uses a bounded persistence path
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

The network interface preference, by default any interface, limits torrent
traffic to one adapter, such as a VPN, through libtorrent's listen and outgoing
interface settings. While that adapter is absent, no torrent traffic flows and
the window and the tray tooltip say why, so traffic never leaks onto another
adapter.

Global download and upload limits and a second, alternative pair of limits use
libtorrent's session rate limits. One toggle in the window switches between the
two pairs. Both pairs include LAN and loopback peers: global means all torrent
traffic, with no undisclosed local-network exemption. The engine assigns every
peer socket type to libtorrent's global peer class while retaining its other
class defaults. This makes the displayed limits apply to local transfers too.
The deliberately small tray menu contains only its immediate session
controls.

Weekly periods repeat in local time with Monday numbered zero. Equal start and
end times span a full day beginning at that time; an earlier end spans midnight.
Pause wins over alternative limits on overlap. Manual Pause all remains saved
and authoritative. Explicit Resume during a scheduled pause bypasses that pause
until the next schedule-mode change; an explicit rate-pair choice similarly
overrides the current mode until that boundary. An absent selected adapter still
blocks transfers. These temporary schedule overrides are not saved or replayed
after restart.

Queue limits start at libtorrent's defaults: three downloads, five seeds and
200 connections. Zero means unlimited in these controls. Ratio and seeding-time
limits start disabled; reaching either pauses through the saved intent owner.
The ratio denominator is the greater of downloaded and verified bytes; seeding
time excludes paused time. Explicit Resume or Force of completed content saves a
per-torrent exemption, so it does not pause again immediately or after restart.

## libtorrent settings

The engine keeps the defaults of the pinned libtorrent release, v2.1.2, because
they are tuned and a changed value can slow transfers without a visible
benefit. Preferences change only the settings in
[Network preferences](#network-preferences), and the disk backend stays at its
default ([Disk write caching](#disk-write-caching)). The engine changes three
more defaults:

- `user_agent` is `TinyTorrent/<version>`, and `peer_fingerprint` is
  `lt::generate_fingerprint("TY", ...)` with the version. Trackers and peers
  then see TinyTorrent instead of a generic libtorrent client, and a private
  tracker that admits only known clients can admit it by name.
- `dht_bootstrap_nodes` lists `dht.libtorrent.org:25401`,
  `dht.transmissionbt.com:6881` and `router.bittorrent.com:6881`. The default
  names only the first, so when that host is unreachable DHT cannot start and a
  magnet link without trackers never gets its metadata.
- `active_dht_limit`, `active_lsd_limit` and `active_tracker_limit` are `-1`,
  as `active_limit` already is. Running torrents are auto-managed, so the
  default limits would stop all but 88 of them announcing on DHT.

## Notifications and sleep

By default, success is quiet and problems interrupt. A person who asked for a
download does not need to be told that it happened, but does need to know when
it stopped. Three switches let a person choose otherwise, including turning
every notification off: Notify when a download finishes and Notify when a
torrent is added start off; Notify about problems starts on.

A Windows notification uses the existing tray's
[`Shell_NotifyIcon`](https://learn.microsoft.com/en-us/windows/win32/shell/notification-area)
path. Respect Windows notification suppression and quiet time; delivery is best
effort and needs no WinUI process or new notification runtime. Send one only
while no window is open, because the open window shows the same events, and
only when its switch is on:

- **Finished.** Clicking a single notification opens the torrent's current
  folder; a combined one opens the application.
- **Added.** A direct addition, such as a double-click on a torrent file, added
  the torrent.
- **Problems.** A torrent stopped by an error, such as a full disk or a missing
  folder, with its name and the reason; a direct addition that could not be
  added, with the reason, because the person believes it is downloading; and a
  file deletion that fails, fully or in part, which is also written to the log,
  because the torrent has already left the list.

Coalesce a burst instead of flooding the desktop. Never execute a downloaded
file. Keep only bounded pending notification context, identified by durable
torrent identity rather than a stale path. While the window is open, problems
appear in it instead, following
[feedback placement](interface.md#feedback-placement).

In the open window, a finished download shows a short message with Open folder.
A torrent finishes when its currently wanted files finish downloading, not when
an existing seed is restored or rechecked, and only after libtorrent has written
the finished data to disk, so a file opened from that folder is whole.

The first time the window closes while the engine keeps running, show one
notification that TinyTorrent is still running in the notification area and that
Exit is in its menu. Windows 11 places new tray icons in the hidden overflow, so
without it the person cannot tell that transfers continue.

The idle-sleep preference starts enabled for active payload downloads on mains
power. A second preference, also while seeding, starts disabled; it keeps the PC
awake for a person who seeds overnight. Paused/queued torrents, metadata
previews, and torrents blocked by an error do not keep the PC awake. The first
switch governs idle-sleep prevention; Also while seeding extends it when enabled.
One
engine-owned [power request](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-powersetrequest)
with a localised reason is held only while needed. Clear it when the condition
ends, on battery power, or on Exit. The display may turn off;
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

Use the selected libtorrent release's default Windows disk backend and write
policy, leaving `session_params::disk_io_constructor` unset. In the reviewed
2.1.2 release that is pread with write-through, which `disk_write_mode` does not
configure. Disk caching is automatic engine policy, with no Preferences control
or saved override; add a choice only if a concrete user need and measured
behavior justify it. An imported `disk_cache_mb` value does not create one.

The default is the starting point, not a claim of lower memory use. Take the
first real workload measurement required by [testing](testing.md#resource-checks),
and compare mmap only if it shows a disk or memory problem mmap could plausibly
improve.
