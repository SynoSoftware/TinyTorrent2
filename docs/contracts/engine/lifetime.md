# Engine lifetime

Part of the [engine contract](../engine.md). Read its shared state, edit and
persistence rules before the sections here that the change affects.

## Startup and activation

Opening TinyTorrent starts or activates the engine and opens WinUI. Optional
start-at-sign-in starts the engine with WinUI closed, and so does every start
when the person chose to start in the notification area. That choice is known
once startup has read the settings, so a start opens WinUI only then. Simultaneous launches
resolve to one owner; subsequent launches forward their activation and exit only
after ownership of that request has been accepted or rejection reported. A launch
that opens the window, adds torrents or changes the Windows registration
reports, instead of forwarding, when the running engine uses another data
folder, such as a test's, because that engine would show other torrents or an
off-screen window, or register its own program. The
[greeting](../pipe-api.md#encoding-and-validation) supplies that folder.

A different data folder offers Exit and Force exit in the existing acrylic
popup titled "Exit the other TinyTorrent?". This prompt takes the focus and has
a taskbar button and an Alt+Tab entry, so the person can return to it, but it is
not kept above other windows, so the other TinyTorrent's own confirmation stays
reachable. Exit is the default for Enter and requests the
running engine's normal shutdown, including its existing confirmations and draft
protection. Force exit ends that engine, and every product window in this
Windows session that runs from its program folder, without saving. Its program path stays visible on one line under Program location, with
the complete path available on hover, with Copy path on its context menu and
Ctrl+C. Force exit targets only the identified program and process. If Windows cannot identify
them, startup shows an explanation with Retry instead of asking a question with
unavailable actions. The launch continues as soon as the other engine and its
window have ended, however they ended, or another launch has started an
engine in its place, with its requested data folder and sources. Escape, Alt+F4 and the popup's close button dismiss this
launch without requesting an exit. A pending Exit does not block Force exit or
dismissal, because the other instance may be hung or waiting for a decision.

A launch that otherwise cannot start shows why in the failure popup and exits
when the person closes it; it releases the instance lock first, so another
launch is not blocked while it waits. The popup stays until closed, as every
failure does.
Retry starts the same launch again and appears only where the person can
resolve the cause meanwhile: another version is running, the running engine
could not be identified or did not accept the request, or `--exit` found TinyTorrent still
running. When the other engine's program folder is known, the detail line
names it, and Open folder shows it. Selecting
the message does nothing. The popup takes the focus, because it answers the
person's launch. A headless launch writes the reason to the error output
instead.

Shortcuts, file associations, and taskbar relaunch enter through the engine.
Use one [AppUserModelID](https://learn.microsoft.com/en-us/windows/win32/shell/appids)
for the cooperating processes, window, and engine-targeting shortcut. Without a
shortcut, set the window's [relaunch command and display-name resource](https://learn.microsoft.com/en-us/windows/win32/properties/props-system-appusermodel-relaunchcommand)
together. Verify pin/close/relaunch with the selected unpackaged installer.

A WinUI executable started directly enters through the engine and exits. When
the engine cannot start, the window opens instead and shows why it cannot
connect, as it does when its engine stops. The engine starts its window with `--engine`, which selects ordinary window startup
without repeating the hand-off. This is a launch mode, not authentication;
[pipe API isolation](../pipe-api.md#isolation) admits only the same user and logon.
The debugger's launch profile passes `--engine`, so the debugger stays attached
to the window, and capture reviews with an explicit disposable store retain
their direct window start. A window already running when the engine restarts
reconnects through the same pipe.

Every default-store engine uses the person's real AppData. A launcher can
redirect files into its private copy without giving its child a package
identity or changing the known-folder path, so startup checks AppContainer
isolation and the resolved location of a disposable write before claiming
instance ownership or reading settings. An AppContainer start reports that it
cannot reach the person's torrents and exits. A redirected desktop start
restarts once with Windows Explorer as its
[parent process](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-updateprocthreadattribute),
from which the restarted engine takes its token. A still-redirected restart
reports an error; it never opens an empty private store. `--exit` needs only
the pipe and instance locks and does not open a store. Explicit `--data` stores
remain available for isolated checks.

WinUI acquires one logon-scoped [mutex](https://learn.microsoft.com/en-us/windows/win32/sync/using-mutex-objects)
without waiting and holds it on its main thread from before window/draft creation
until exit, so one window owns the drafts, also across engine restarts. A WinUI
that cannot acquire the mutex forwards its activation through the engine to the
existing window and exits. When forwarding fails, it brings that window to the
front, which shows the engine's state. The mutex stores no product state; the engine still
owns launch policy.

Activation follows the interface's [Add policy](../ui/torrents.md#add). Several
sources are added directly; a single source opens or activates the Add dialog
when its torrent-file or magnet dialog preference is on. The two choices are
saved independently.
Direct additions use the same
guarded workflow at the default destination without opening or raising the
window, even if it is already running. Duplicate sources report the existing
torrent instead of opening a tracker-merge prompt. Queue and pause policy still
apply, so a background addition cannot lift a deliberate pause.

Bound pending additions and report overload. Readiness means the engine can
answer, rather than merely having a process or tray icon. While a WinUI the
engine started is still opening, further Open requests wait for it instead of
starting another; the engine observes that launch through its
[process handle](https://learn.microsoft.com/en-us/windows/win32/procthread/terminating-a-process).
The mutex, not this wait, guarantees one window. Bound readiness waiting and make
failed startup retryable. Endpoint and data-directory exclusion follow
[pipe API isolation](../pipe-api.md#isolation).

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
two live, nonclickable status rows, a separator, Show window and Pause all,
a separator, and Exit. The first status row shows aggregate download and upload
speed; the second shows active and queued counts. While the session is paused,
the second row is Paused with the torrent count. The [UI](../ui/workspace.md#main-window)
owns command names. Resume all replaces Pause all only while the person or the
schedule paused transfers, because Resume cannot lift a missing adapter's pause;
the tooltip names that adapter
instead. The pause command uses the [saved session pause](../engine.md#state-and-work) and keeps
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
for a window to appear and the Show the splash screen setting allows it. The
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
That toast has two buttons of equal width: Retry opens the window again, as
the tray's Show window does, and Open folder shows the program folder in File
Explorer, where a missing or blocked file can be checked. Open folder leaves
the toast open, so Retry stays available after the person checks the folder.
There is no second recovery dialog.
A launched WinUI that hands off to an existing window and exits is not a launch
failure. If readiness times out while the process is still alive, report that it
has not opened; a timeout does not authorize another launch. A launched window
that appears without `ready` shows its own connection failure, such as an engine
of another version, so the splash closes and nothing is reported; Open then
raises that window rather than waiting for a `ready` it will not send, and
closing it opens nothing.
Opening again reuses the existing activation handling and process checks,
without resubmitting an addition whose outcome is unknown. Existing transfers
continue after a failed Open. A late ready window ends the splash wait.

### Windows registration

One engine component registers, unregisters, and reads TinyTorrent's per-user
file/link handlers and start-at-sign-in entry. The registry is the authority for
these registrations; do not mirror them in saved settings. Read the expected
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

**Owner ruling: turning a registration switch on works at once.** The person
never finishes it in Windows Settings, because a switch that shows on while
Windows still opens another app, or still skips startup, leaves the person to
find and repeat the step elsewhere.

For a request to open torrents with TinyTorrent, register its handlers and write
the per-user `.torrent` association and `magnet` protocol open command to the
running engine. Both use the same quoted activation command as the registered
classes. Then delete the person's saved choice (`UserChoice`) for each kind
whose choice names another app. Windows refuses to let an app write that choice
but lets the person's account delete it, and without one Windows opens the
per-user association just written. Registration also records each TinyTorrent
class in `ApplicationAssociationToasts`, because without that record Windows
asks which app to use at the next open even while TinyTorrent is the default.
The direct associations and the deletion happen only on an explicit
registration request, because startup repair must not reclaim associations from
another app. A deletion Windows refuses fails the request and reports the
observed defaults. Query the effective defaults through the supported
[Windows defaults platform](https://learn.microsoft.com/en-us/windows/apps/develop/windows-integration/default-apps-platform)
and report the name of each kind's app. A remembered class without an open
command is not a default. Keep this small query at the registration owner, and
build no application chooser.

Observation also reports every `.torrent` or `magnet:` handler that starts a
missing executable, whichever application registered it, because choosing it
fails with no sign of why: the current default, the extension's or protocol's
own key and default class, the Open with lists, and each registered
application's class, for the current user and for all users. The request to
open torrents with TinyTorrent first removes, for the current user, each
broken class's open command and the references that offer that class for
`.torrent` or `magnet:`, so the next open asks which app to use instead of
failing. Nothing else of the class goes, because a class can serve other
types or hold shell extensions that still work, and a registered application
keeps its other associations; only an `Applications` key, which describes the
one missing program, goes whole. References stay while an all-users command of
the same class still works, because Windows then uses it. A removal Windows
refuses leaves that class reported and does not stop the others. Broken classes registered for all users
need an administrator, so the window's Repair then starts the engine with one
`--repair-class` option per reported class through Windows' administrator
prompt. The window names the classes because the administrator can be another
account, whose registry does not show which classes the person's Windows uses.
The engine checks each named class again and removes only an all-users open
command that starts a missing program, so the command line cannot remove a
working one. It keeps the references, which belong to the person's account.
That run changes nothing an engine owns, so it runs without instance
ownership, and declining the prompt leaves those classes reported. The same
rule as for TinyTorrent's
own entries decides that an executable is missing: only a full path on a fixed
local drive can be missing, and a network or removable drive, a name Windows
searches for, or a check that does not finish counts as existing.

Removal of other applications' entries waits for the person, but they learn of
it: once loading has ended, the engine looks for broken handlers and, while
Notify about problems is on, shows one problem notification that names the
first missing program not reported before and counts the others. It adds the
reported programs to `settings.json` and keeps them after they are no longer
found, so a problem the person leaves alone, or a check that timed out once,
does not return at every start. Selecting the notification opens Settings at
the Windows integration card, highlighted, where the handlers are repaired.

Every start on the default store points the installer's Start menu shortcut and
any taskbar pin to this copy when they start a missing TinyTorrent executable,
because the window sets its pins to start whichever copy was running. Shortcut
folders on a network share are skipped, because an unreachable share would
hold the start.

The sign-in switch adds or removes the Run entry through this same owner.
Windows Settings and Task Manager disable a Run entry without removing it: they
record an odd first byte in the entry's `StartupApproved` value. Observation
reports such an entry as disabled, and turning startup on or off removes that
value, so startup runs again once the person turns it on. Neither registration
nor observation creates a second saved setting.

**Owner ruling:** registrations stay with the registered copy while its
executable exists. Every start of an engine on the default store moves
TinyTorrent's existing handler and sign-in entries to its own executable only
when they start a missing one, so a moved copy keeps working and no entry
points to a deleted folder. Entries that start another existing copy stay
with it, because running a test copy, a build output or a second download
must not take them from the installed copy and leave them broken when that
copy is deleted. Use this copy in Settings moves an entry to
this copy. A copy that is not on a fixed local drive counts as existing,
because checking a network path can stall the engine and a removable drive
can return. Handler entries without the recorded request are the remains of a
partial change and are removed; a start never re-registers what the person
turned off. An engine started with its own store (`--data`), such as a
test's, is not the person's copy and leaves the entries alone. Observation
reports each registration as starting this copy, another TinyTorrent copy
(with that executable), or nothing.

The installer registers on first installation and unregisters before removing
program files, through the engine's maintenance commands, with individual
actions for any deselected setup choice. Settings sends the same
actions through the pipe. Maintenance
launches forward to a running engine or use the same owner in a short-lived
native process under instance exclusion, without starting transfers or WinUI.
Registration is completed for the installing user, not an administrator account
used to install a prerequisite.

## Closing and shutdown

Setup requests the existing coordinated Exit through the engine's `--exit`
command. It succeeds without starting an engine when none is running, and waits
up to 30 seconds for a running engine to release instance ownership. A prompt
that keeps it alive prevents file replacement; setup reports the condition and
can be retried. Setup force-terminates no process.

Closing WinUI normally exits without confirmation. Resolve actual unfinished
edits according to [the UI](../ui.md#committing-edits), and do not
silently drop changes already committed in the window but still being submitted.
**Owner ruling: Close and Exit hide the window at once.** The window hides
before it waits for anything, unless a draft, dialog or picker needs the person,
because a window that stays on screen while it closes looks frozen and holds the
screen for no one. Show it again only if an edit decision or failed close needs
the person's attention. Waiting for accepted work, saving and shutdown happen
with the window hidden; the tray icon stays until the final save ends.
Accepted operations and transfers continue in the engine. Window-only snapshots and
detail collection stop or are released with their last consumer; tray status,
queue policy, swarm activity, [speed history](../engine.md#state-and-work), and persistence
continue.

Exit is in the tray menu and in the window's File menu; the window's Close
only closes the window. The optional active-transfer
confirmation is on by default and belongs to the desktop host, so tray Exit and
window Exit use one check even when WinUI is closed. While the window is open,
the host asks it to show the prompt in the app's dialog style; the host asks in
its popup when the window is closed or cannot show a dialog at that moment.
Windows shutdown and headless operation bypass it. After confirmation, engine Exit closes
the window by the same rules as Close: a prompt appears only for actual unfinished
input, and Cancel in that prompt cancels Exit.

**Owner ruling: Exit ends within 10 seconds.** The interface never hangs, and
Exit never hangs. Exit has 10 seconds to stop, and that is a hard rule: at 10
seconds the process is ended, whatever still runs. Within that time Exit stops
gracefully. When Exit comes in the middle of long work, it saves that work's
state, and the next start resumes the work in a logical way, or restarts it
when resuming is impossible. Every case has its own graceful stop and logical
continuation; no work is exempt. A normal Exit ends in 2 to 3 seconds; the 10
seconds are only the watchdog.

The person who chooses Exit wants the program gone, so Exit never waits for
file work. The work in progress is already saved as
[unfinished tasks](files.md#unfinished-tasks), each from before it changed a
file. Exit adds none: its first save, before it waits for anything, records
only that Exit cut the tasks off. It then stops that work before its next
file, so each file is either done or untouched and the saved state names what
remains:

- A move stops between files, and a copy parks with its proof. The move's
  task continues it at the next start.
- A deletion, permanent or to the Recycle Bin, deletes no further file. Its
  task holds its files, and continuing it deletes the ones that remain.
- A move that the person abandoned gives its files back, and its marker
  records that. The next start continues the give-back without asking,
  because either answer would give the files back.
- A move whose torrent the person removed during the move cannot continue,
  because the torrent has left the list. Its task, saved with the removal,
  gives the files back to the old folder at the next start, without asking.
  When the person deleted the files, their deletion runs after the files are
  back.
- A mark of the web stops before it writes. A file that finishes from the
  start of Exit on is not marked until the next start.

Nothing before the final save waits on a drive. The moves, give-backs and
additions get 2 seconds to stop, and then the proofs of the copies are saved.
An addition whose disk work has not ended joins the list without its names and
stays paused, and the next start chooses them: its names are chosen on the drive's
worker, its new folder and renames are made on libtorrent's disk threads, and
a share that does not answer can hold either. A reload that has not yet
replaced its torrent's handle stops: once Exit starts, its end leaves the old
handle in place, which Exit pauses and saves. From the start of a reload until
a reload succeeds, each save of the torrent leaves out the pieces of its lost
files, so the next start downloads only those files again instead of checking
every file. Pending priority edits, the torrents' pauses and the final
transfer sample get 2 seconds each; one that times out is logged and Exit
continues, because the resume data still records what each torrent has.

After the final save, the work that still runs gets 1 second to end: a job on
a drive's worker, such as a rename to a final name, a mark of the web or a
name choice, a storage job, a give-back, a copy that has not parked, and the
probe that opens a drive root or share once a minute.
libtorrent's teardown would end the session under such a job, and the
engine's end would wait for a job that may never return. libtorrent's
teardown also waits for a stalled check's reads. So when work still runs after
that second, or such reads remain, the engine logs `teardown_skipped` and ends
the process with exit code 0 without the teardown. The next start does that
work again from the saved state, as
[unfinished tasks](files.md#unfinished-tasks) describe. Exit saves a second
time once the torrents are paused and their last checkpoints are saved, when
no file can finish any more, so the next start writes each mark that Exit
stopped or that a file finishing during Exit needs.

The engine reports the end of Exit once the work after the final save has
ended, or after that second. The desktop host ends the process with exit code
0 when the teardown has not ended 2 seconds after that report, because
everything is saved by then. It ends the process with exit code 2 when Exit
has not ended 10 seconds after the person chose it, also when a thread waits
on a drive that stopped responding; cancelling Exit in the window stops that
clock. A normal Exit ends well before that, so the end at 10 seconds firing
during one is a defect. A failed save ends Exit at once with exit code 1, as
after a crash, because saving again would fail again; the tasks and markers
saved before it still hold the work.

An Exit whose work ends in time takes these steps. One that ends without the
teardown takes steps 1 to 3, then removes the tray icon and ends the process.

1. Stop accepting new commands and settle accepted state changes and writes.
2. Pause the session and await transfer/disk quiescence without changing each
   torrent's saved paused/running intent. Track actual outstanding work rather
   than expecting a new pause alert from an already-idle torrent.
3. Settle outstanding resume requests, including success, not-modified, and
   failure outcomes. Obtain final resume data and await application storage
   commit; a resume-data alert alone does not prove payload writes have flushed.
4. Remove the tray icon and its popup, then destroy/join libtorrent, close the
   pipe, and release data-directory ownership last. The join waits up to 1
   second (`stop_tracker_timeout`) for trackers to acknowledge the stop, so a
   tracker that does not answer cannot bring Exit near its 10 seconds, and an
   icon left on screen during it looks like an exit that did nothing.

An active draft prompt is not an unresponsive window. WinUI acknowledges when
Exit is waiting for that choice; the window timeout pauses for the person and
resumes when closing continues. A failed window-close preparation cancels Exit
and preserves the window. Open during Exit is refused as shutting down rather than
acknowledged and discarded. Reopening a disconnected but living window retains its
process and starts the same bounded readiness wait used for a new window; a
window already on screen is raised instead.


Do not discard unsaved state automatically or offer recovery buttons on the
splash.

Keep the owner pumping messages while asynchronous shutdown work settles. Never
join a worker that still needs the owner to process its completion; final joins
follow those handoffs.

Session pause preserves individual torrent intent; per-torrent pause can change
the saved flag. The [pause completion](https://www.libtorrent.org/reference-Alerts.html#torrent_paused_alert),
resume-data generation, application commit, and session join are separate facts.
In the pinned [implementation](https://github.com/arvidn/libtorrent/blob/v2.1.2/src/torrent.cpp),
`save_resume_data(flush_disk_cache)` does not wait for file-release completion
before posting resume data. Its name is not a power-loss durability guarantee.

A stopped torrent keeps no peers. Stopping clears its peer list before its
resume data is saved, so neither memory nor the resume file holds peers it
cannot use; starting it again finds peers through trackers, DHT and local
discovery. Session pause, the schedule and temporary holds keep peers, because
they end by themselves.

A hung window ends the close wait with a tray notification and cancels that Exit
attempt, preserving unfinished input. The tray can request Exit again when the
window responds. Windows logoff/shutdown uses a bounded persistence path
that does not depend on an interactive confirmation. When it arrives after an
Exit that already saved, that path does nothing, because the session is gone
and the process is ending. Abrupt termination may lose
changes since the last successful checkpoint; do not promise zero loss.

A window crash leaves transfers running and the tray able to reopen it. An engine
failure makes WinUI report that downloads stopped and offer restart. Do not
register for Windows application restart: crash restart depends on Windows Error
Reporting, which the [privacy policy](../architecture.md#privacy) disables to prevent
crash uploads. With WinUI closed there is no resident watchdog. Relaunching the
app restores saved state through the same recovery path. There is no setting to
exit a separate tray while leaving the engine behind.

The [installed-update path](../architecture.md#installation-and-updates) waits for
coordinated application Exit; window close alone does not stop the engine.
Postpone replacement if Exit is cancelled
or cannot finish safely, preserving the installation and saved data. Launch the
engine and window from the same release. An externally forced termination still
uses the crash-recovery path. This rule does not require a resident updater.

## Diagnostics

Both process startup owners set `SEM_NOGPFAULTERRORBOX` through
[`SetErrorMode`](https://learn.microsoft.com/en-us/windows/win32/api/errhandlingapi/nf-errhandlingapi-seterrormode)
to prevent Windows Error Reporting from handling crashes. This is process-local;
it does not change the person's Windows diagnostics settings or disable
dependency usage telemetry. The [privacy policy](../architecture.md#privacy) owns
that distinction and the stock Windows App SDK limitation.

Write engine errors and major lifecycle events to a local log in the data
directory. Keep one current file and one previous file, each capped at 1 MiB.
Record operation kind, stable identifiers, and error codes; omit credentials,
raw magnet/tracker URLs, and payload contents. Do not log every transfer update.
Bound and coalesce repeated diagnostics, keeping file I/O off the state owner.
A full or unwritable log must not stop transfers. There is no automatic upload
or second telemetry pipeline.

The WinUI process records unexpected UI, managed-thread and unobserved task
exceptions in `%LOCALAPPDATA%\TinyTorrent\ui-error.log`, retaining the previous
report as `ui-error.previous.log`. Each report stays below 1 MiB and includes the
build, runtime, exception and stack trace; it does not collect application state
or upload anything. Fatal failures show a native Windows dialog with the report
path, Copy log and instructions to reopen the window. Copy log copies the report
itself, including when its file could not be saved, and confirms success without
closing the dialog. Close retains the runtime's normal termination behavior.
Unobserved task failures are logged without closing the
window. A failed report write must not replace the original failure.
