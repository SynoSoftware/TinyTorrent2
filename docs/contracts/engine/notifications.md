# Notifications and sleep

Part of the [engine contract](../engine.md). Read its shared state, edit and
persistence rules before changing this behavior.

By default, success is quiet and problems interrupt. A person who asked for a
download does not need to be told that it happened, but does need to know when
it stopped. Four switches let a person choose otherwise, including turning
every notification off: Notify when a download finishes and Notify when a
torrent is added start off; Notify about problems and Notify when the window
closes and transfers continue start on. A failure of
something the person just asked for, such as the final save on Exit, opening a
source or starting the window, is not one of these notifications and always
shows, because staying silent would leave the person believing it worked.

The engine shows a toast in a popup that it draws itself, with the splash's
acrylic look, in the corner of the taskbar's monitor where Windows shows its
own notifications. It does not use Windows' own notifications or balloons,
because Windows lets a person turn those off for every app at once, and a
toast the person turned on in Settings must still appear. It slides in from
the screen's edge and back out, unless Windows animation effects are off or
another monitor lies beyond that edge, takes no focus,
stays for Windows' notification duration and stays while the pointer is over
it. Every toast ends its buttons with Dismiss, and keeps its close button. A
failure of something the person just asked for stays until dismissed,
without a timeout or replacement by later toasts, so the repair instructions
remain available. When it shows at once in
answer to the person's own action, such as a launch, Exit or adding a torrent,
it takes the focus without a taskbar button, so Tab, Enter and Escape reach
its buttons; Enter runs Dismiss until another button has the focus. A failure
that arrives in the background, such as a window that ended unexpectedly or
did not open within its time, or one that waits behind another popup, never
takes the focus, because the person may be typing elsewhere. It does not
appear over a full-screen app or a presentation, and waits until Windows shows
notifications again, so a torrent that stopped meanwhile is still reported.
The toast that the window closed is the exception: it is dropped, because it
only helps at the moment the window closes.
While the pointer is over a popup, its header shows a More button (…) beside
the close button, as Windows' own notifications do; otherwise each popup shows
only its message. More opens a menu laid out as Windows' own, drawn on the
popup's acrylic surface in the popup's theme: the title See fewer
notifications, a separator, and commands that each show their Lucide icon
from the font beside the engine: a command that turns off the
switch for that kind of toast, named for the switch, such as Don't notify when
a torrent is added; Turn off all notifications, which turns off every switch;
and Go to notification settings, which opens the window at the Notifications
card of Settings and highlights it. A failure of something the person just
asked for has no More button, because it always shows. Debug builds accept
`--toast`, which shows a sample Added popup whatever the switches say, so the
popup and its menu can be checked without adding a torrent. A running engine
shows it; otherwise the engine starts in the tray to show it. Selecting the rest of the popup acts on the toast
where each kind below says so, and otherwise does nothing; only the close
button and Dismiss close a toast before its time. Show a toast
only when its switch is on. Problems and completions use the open window when
one exists; background additions still use the tray so they do not raise that
window:

- **Finished.** Clicking a single notification opens the torrent's current
  folder; a combined one opens the application.
- **Added.** A direct addition, such as a double-click on a torrent file, added
  the torrent.
- **Problems.** Torrents that wait for the person share one notice, so one
  Retry continues them all: torrents whose storage error the engine stopped
  retrying by itself, and torrents whose move to the final folder failed. With
  one torrent it gives its name, reason and current download folder, plus Show
  torrent and Retry; with several it counts them, gives the reason they share,
  if any, and offers Retry, and selecting it opens the window, which lists
  them. Show torrent opens or raises the product window and selects that
  torrent. Retry keeps each torrent's Start, Stop or Force choice. Torrents
  that wait for a drive or share to answer get one notice per drive root or
  UNC share, even across different folders and bursts. It gives the cause,
  root and affected count, without listing torrent names or offering Show
  torrent, and Open folder targets the root; each affected row retains its own
  reason. A share that hangs gets the same notice, whatever work waits on it:
  the engine opens each root that holds a running torrent, or on which a
  deletion or a give-back of files runs, once a minute, on a thread of its
  own. A root whose open has not returned after two minutes does not answer,
  so the notice counts at once the running torrents under it and the torrents
  whose deletion or give-back runs there, removed ones included.
  A popup that shows a notice whose count changes also changes its buttons and
  what selecting it does. Missing or invalid paths, known drive/share
  disconnections and full disks use typed native error codes. Healthy seeds on
  a full drive remain unaffected. Both notices appear only once their
  condition has lasted two minutes, because most absent shares, such as a NAS
  waking up or Wi-Fi reconnecting, return sooner and the engine continues their
  torrents by itself. A gap shorter than two minutes does not restart that
  wait, so a problem that returns right after Retry shows again at once. The
  notices follow the current state: counts and reasons update quietly, and a
  notice disappears without a success toast as soon as its torrents continue.
  A later failure can then notify again. A storage problem that hides another
  problem of the torrent for a while, such as Move interrupted, does not make
  that problem notify a second time when it ends.
  A direct addition that could not be
  added, with the reason, because the person believes it is downloading; and a
  file deletion that fails, fully or in part, which is also written to the log,
  because the torrent has already left the list. Its notification offers Open
  folder, where the person deletes the rest.

Coalesce problem bursts before routing them to either the desktop or the open
window. Storage changes coalesce by live condition identity and survive event
queue pressure; their retained context is bounded by the actual incidents,
while disposable event batches keep their fixed bound. Updated/resolved storage conditions do not announce again or recreate
a dismissed warning. Keep failures of explicit user commands separate from
background conditions so those instructions survive. Never execute a downloaded
file. Keep only bounded pending notification context, identified by durable
torrent identity rather than a stale path. While the window is open, problems
appear in it instead, following
[feedback placement](../ui/feedback.md#feedback-placement).

In the open window, a finished download shows a short message with Open folder.
A torrent finishes when its currently wanted files finish downloading, not when
an existing seed is restored or rechecked, and only after libtorrent has written
the finished data to disk and the finished files have their real names, so a
file opened from that folder is whole.

Each time the window closes while the engine keeps running, show a
notification that TinyTorrent is still running in the notification area and
that Exit is in its menu. Windows 11 places new tray icons in the hidden
overflow, so without it the person cannot tell that transfers continue. Notify
when the window closes and transfers continue starts on. Selecting the rest of
this popup does nothing, because opening the window the person just closed
would undo their action.

The idle-sleep setting starts enabled for active payload downloads on mains
power. A second setting, also while seeding, starts disabled; it keeps the PC
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
