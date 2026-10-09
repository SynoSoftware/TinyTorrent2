# Settings prototype: implementation notes

Companion to [settings-options-prototype.html](settings-options-prototype.html).
Recorded 2026-10-08. Read both before implementing these Settings changes.

The HTML demonstrates presentation and interaction with sample data. This file
preserves decisions, reasons, ownership boundaries, and limitations that are easy
to miss in the HTML. Neither file proves production integration or performance.
This handoff was checked against source; no application was run or compiled.

For delivery order, existing owners, field coverage, and completion criteria,
use the [implementation plan](../docs/settings-implementation.md).

## Start here

1. Use **C: Search and index** as the selected direction: category cards and
   search, with D's compact setting rows. D remains a useful direct category
   view; A, B, and E are comparison variants, not additional product modes.
2. Read the [documentation guide](../docs/README.md),
   [interface contract](../docs/interface.md), and
   [WinUI instructions](AGENTS.md). Reconcile the specific differences listed
   below before changing their production owners. This companion is a design
   handoff, not a replacement engine or protocol contract.
3. Trace each proposed control to its existing owner. The HTML's `sections`
   array is the field inventory: labels, grouping, advanced visibility,
   dependencies, help, units, and sample values. Its identifiers are prototype
   identifiers, not proposed wire keys or persistence names.
4. Preserve working behavior while adopting the design. In particular, preserve
   the scheduler, background addition, individual pause intent, and existing
   settings that the HTML only partly demonstrates. Record actual integration
   and verification separately from this prototype.

The product direction is a capable, compact Windows app. qBittorrent was a
source of useful options, not a requirement to reproduce every menu or setting.
An attractive placeholder is not evidence that its backend feature exists.

## Navigation, disclosure, and editing

- Keep the existing app chrome. Settings uses the title-bar Back button and
  Alt+Left; an additional "All settings" row wastes vertical space. Back from
  Connection setup returns to Speed limits and restores the previous scroll
  position and focus to its launching row.
- There is **one Show advanced options switch**. It reveals advanced rows,
  cards, and the Advanced category together. Advanced options may belong inside
  an ordinary category; the Advanced tab is not a dump of every uncommon option.
  Hiding advanced options does not reset their values or stop their effects.
- Search covers all settings, including hidden advanced settings and familiar
  synonyms. Selecting a hidden result reveals it and goes to its real owner;
  search is not a second editable copy of Settings.
  Use its placeholder and accessible name without an extra "Find a setting" label.
- Ordinary settings use the existing individual commit and error behavior.
  There is no page-wide Save. Use the
  [committing-edits contract](../docs/interface.md#committing-edits) for pending,
  invalid, rejected, and uncertain edits; HTML assignments are not that protocol.
- Connection setup is a **Settings child page**, because it has measurement,
  temporary suspension, a proposal, and explicit Apply/Cancel. Proxy editing
  remains a compact dialog because its related fields must save together.
  Folder selection and default-app registration use their existing Windows
  surfaces. These tasks do not need the same container merely for consistency.

## Stable rows and shared alignment

The owner calls unexpected movement "shifting sand." A setting's state must not
move the controls below it just because help, validation, or a dependent value
changes. Keep dependent rows present and disable them when unavailable. Examples
are the incomplete folder, default versus last-used folder, proxy fields, and
sleep while seeding. Reserve space for state/error feedback when it is needed.
The explicit advanced switch and a responsive layout change are intentional
changes of composition, not reasons to allow ordinary state changes to reflow.

Labels, values, buttons, and work-surface messages stay on one line. Use native
end ellipsis with the full content accessible. Prefer short, clear wording and
adequate control widths; a clipped ComboBox choice is not acceptable. Help belongs
in native tooltips and accessibility descriptions, following the interface
contract. The multiline file-pattern editor contains user input; it is not
permission to wrap setting descriptions.

**Alignment is shared per visible tab/category, across all its cards.** This
supersedes the earlier per-card experiment:

| Visible content in the category | Numeric fields | Switches |
| --- | --- | --- |
| At least one field with a displayed unit or unit selector | Share a value column and reserve one trailing unit column, including for unitless fields | Track's right edge aligns with the numeric box's right edge; On/Off starts in the unit column |
| No fields with displayed units | Keep the ordinary right alignment without a reserved unit column | Keep their ordinary position; do not move them for this rule |

Determine this from the rows currently shown, not all fields in the model.
Recompute when advanced visibility changes. A disabled but visible unit still
occupies space. Unit selectors such as minutes/hours must fit the same trailing
column. Switch tracks retain their normal size. Other controls, such as paths
and full-width choices, keep their appropriate widths.

The HTML owns this once in the `.settings-category` layout rules. The all-settings
comparison view scopes it separately to each category. In WinUI, put this decision
at the shared category layout and let shared rows consume it; individual cards
must not measure independently or carry compensating margins. Reuse
[SettingsRow](src/Controls/SettingsRow.cs) and
[SettingsSection](src/Controls/SettingsSection.cs) rather than adding a parallel
settings framework. HTML pixel values illustrate alignment, not a requirement
to replace native control metrics.

## Controls, icons, and help

- Use native WinUI controls, theme resources, and the app's existing shared
  button/dialog styles. The prototype's browser controls, hard-coded palette,
  simulated caption buttons, and floating experiment toolbar do not ship.
- Unlimited-capable numeric fields accept a limit or Unlimited. Clearing removes
  the limit; keep a usable clear affordance on the focused, nonempty field.
  Preserve numeric validation and accessible naming. Choose the native edit
  control that supports this behavior; the browser's custom clear button is not
  a new control architecture. Map the UI's unlimited value through the existing
  engine contract, rather than assuming every engine limit uses HTML's zero.
- Changing duration units changes the presentation of the same duration, not
  its meaning. Preserve invalid input until corrected rather than silently
  changing a setting or losing the draft on a refresh.
- Use Lucide's bundled glyphs and established names from
  [Lucide.cs](../lib/Lucide/src/Lucide.cs). The HTML now loads the bundled font;
  recreate icons through the existing native integration, not hand-drawn SVGs.
  Its `icons`, section `icon`, and `categoryIcons` mappings show the reviewed
  choices. Categories have their own icons, independent of their first card.
- In Speed limits, Transfer limits uses sliders, Standard limits a gauge,
  Alternative limits switching arrows, Queue an ordered list, and Peer
  connections a network. Connection setup's navigation row has a trailing
  chevron and no redundant leading gauge. Its Test action can use the gauge.
  Reuse a glyph for the same meaning; variation for its own sake is not a goal.
- Use native `ToolTipService` behavior. Tooltips must not remain open merely
  because a mouse click left a control focused. Avoid unnecessary tooltips that
  repeat fully visible labels. Preserve keyboard access to full text and help,
  but do not reproduce the prototype's extra tab stops on every table value.
  The HTML's hover/focus timers are browser emulation, not production code.

## Where features belong

This is a placement guide, not a claim that every backend feature exists.
Consult `sections` for the complete field list instead of maintaining a second
copy of every label and range here.

| Category | Scope and implementation cautions |
| --- | --- |
| General | Default-app integration; sign-in/background startup, splash and start-paused choices; exit confirmation; notifications; sleep prevention on mains power; updates. Closing a window and exiting the engine remain distinct operations. |
| Transfers | Background versus dialog-based addition, start/queue placement, folder layout and duplicates, skipped files, download/incomplete folders, suffix and allocation, seeding stop rules, watched folders. Defaults for new torrents must not relocate existing data or rewrite existing torrent choices. |
| Subtitles | Automatic downloading, supplier access, and wanted languages. Follow the [subtitle design](../docs/subtitles.md) for early lookup, quiet recovery, and file ownership. |
| Network | Adapter, incoming port/mapping, encryption, transport and address family, connection attempts, proxy and peer discovery. Reuse the existing network and proxy owners, including private-torrent restrictions. |
| Speed limits | Connection setup, active speed-limit mode, standard caps, queue limits, peer limits, alternative caps, bandwidth accounting. Queue activity and transfer speed are separate concepts even though they share this category. |
| Schedule | Weekly schedule and its existing editing capabilities, plus a compact summary of the actual standard and alternative speeds. |
| Appearance | Language/theme, optional title-bar speeds, external IP and free-space visibility in the status bar. Title-bar speeds must fit the custom chrome; the status bar/tray remain available when title-bar space is insufficient. |
| Advanced | UI refresh interval, memory/open files, torrent checking, and engine-owned speed-history granularity. These controls have different owners despite sharing a category. |

Opening a torrent file or magnet link can add it without opening WinUI. The
background choice still honors the chosen destination, queue, pause, and
duplicate policies. An optional added notification provides feedback from the
tray; it is not a reason to open the main window. File associations, startup,
notifications, update checks, and sleep prevention use the existing desktop
integration. The prototype's sample dialogs and messages do not implement them.

## Subtitles

Subtitles has its own tab and category card in C's search and index layout.
Search includes supplier/account terms, captions, SRT, and language names; each
result opens the existing setting rather than a second editor. The shared field
inventory also makes these controls available in the comparison layouts.

The default is TinyTorrent's provisioned, account-free OpenSubtitles package.
Settings shows Subtitle supplier on the left and the clickable saved supplier
with Edit on the right, all in one row. The value and action align with the
language input and Add. Edit opens the supplier/account
dialog; user name and password there are optional for OpenSubtitles. Blank uses
TinyTorrent's access; a complete pair uses the person's account. No personal API
key or subscription is required. Check examines the draft, Save commits it as one
configuration, and Cancel/Escape discard it. Nothing applies merely on typing,
focus departure, or Check. Automatic downloading and language edits remain
immediate on the Settings page.

Automatic subtitles starts Off, ready to enable using the interface language.
An unset language choice follows the current interface language; choosing
languages makes that selection independent, and Reset restores the default.
Its tooltip says "Use the interface language for subtitles"; it keeps its Lucide
icon and remains visible, disabled, when the default is already active.
Edit, Check, Find, Recheck, Add, and Reset share one action size on the Subtitles page and
align to the right edge. Reset stays below the language list, right-aligned.
The HTML uses 96 by 32 pixels; native implementation sizes the shared action
column for the widest localized label at the current text scale, so equal sizes
never clip labels. The shared supplier/proxy dialog row uses the same equal action
size for Check, Save, and Cancel; this is the owner's exception to natural widths.
A searchable language input replaces the fixed checkbox list.
Add or Enter adds a matching language once; selections remain visible with Remove
actions. Removing the last explicit choice restores the interface-language default.
All configuration stays editable while Off. Supplier status belongs in the Supplier
card's footer beside Check, which checks the saved configuration directly. Edit
opens the compound editor; its Check tests the draft through the same operation.
There is no unrelated Status row under Automatic subtitles. Reserve footer space
so an access, allowance, or file problem does not move other controls.
The saved supplier name opens an anchored information flyout containing its
disclosure and policy links. The editor's information icon beside the supplier
label opens the same surface for the draft supplier. The supplier selector, user
name, and password fields share both horizontal edges; the information icon never
reduces an input's width. No disclaimer paragraph
or policy links occupy either work surface. Use a native WinUI Flyout in production;
Edit continues to use the existing shared Dialog base class. The flyout supports
keyboard opening, Escape and light dismissal. Its readable text may wrap because
this is a dedicated information surface, without adding rows to Settings.
The policy link remains explicitly a draft until release facts are established.
Secondary facts use Body text and the secondary color, not smaller type. Account
instructions are field tooltips and accessible descriptions. Keep visible error
feedback in its existing reserved space. No additional consent step is added.

The Automatic subtitles card has a second row, Finished downloads, with a Find
action. It follows the existing dependent-row pattern: while Automatic subtitles
is Off, the row is inactive and its tooltip asks the person to turn the switch
on. The value column beside Find, in the secondary color, tells the person what
Find will look for before they press it: "62 subtitles missing". A total stays
short however many languages are chosen. Find's tooltip, which also appears on
keyboard focus, and the count's tooltip give the breakdown: "Find 62 missing
subtitles: English 37, Spanish 25. Estimated downloads: 62. Retries may use
more." This is an estimate, not a billing cap. While lookups remain the column shows "40 subtitles left";
afterwards it shows "Found 52 of 62". All three states count subtitles, the
unit the allowance is spent in. With nothing missing it reads "No subtitles
missing" and Find is disabled. The column is always present, so the text never
moves other controls. The prototype uses sample counts per chosen language and
a quick countdown. Automatic subtitles help explains that work runs while the
window is open: accepted work resumes on reopening, and movies added and
finished entirely while closed need Find.

A third row, Subtitle files, has a Recheck action with the same layout. Its
value column reads "Not checked yet", then "Checking 96 of 240" while it runs,
then "Last checked 14:32". Recheck stays available while Automatic subtitles is
Off, because it reads only the drive. Find and Recheck each disable the other
while running; pressing a running action again does nothing, so focus never
lands on a disabled button. In the prototype, Recheck finds two English
subtitles the person added, so the English missing count drops by two.

Reopening the supplier dialog never shows a saved password or API key, because
WinUI does not receive it. The empty field's placeholder reads "Saved password"
or "Saved API key". Leaving it empty keeps the saved secret, typing replaces
it, and clearing both OpenSubtitles fields removes the account.

A chosen language that the saved supplier does not offer stays in the list
with "not offered by" and the supplier name in secondary text after its name,
like the existing "interface language" note. The prototype uses one sample
catalog for the suppliers, so it does not show this case. Unsupported choices
stay visible but are excluded from actionable Find counts and requests.

A persistent problem appears in critical text, never as a red card or surface.
This follows Fluent, which carries severity in the status text, and the existing
critical color for field errors. The Supplier footer shows it, and while
Automatic subtitles is On the Subtitles category card on the index shows it in
place of its summary, so the problem is visible without opening the category.
The text itself names the problem, so color is never the only signal.

C opens on its Settings index. A full-width horizontal Automatic subtitles help
card precedes the category grid, with its brief benefit and a right-aligned
Set up subtitles cue. The whole main surface opens Subtitles, matching the
category cards. A sibling dismiss button removes the help without navigating
and returns focus to the ordinary Subtitles category card. Leaving the
index also dismisses the help for the session; production persists that decision
so ignoring help never causes a reminder after restart. Search remains available.
The owner's requested discovery copy is the dedicated help exception; its
benefit stays on one line with the full text available on the card's keyboard
focus. The requested Set up subtitles cue is part of the help card's navigation
surface rather than a nested button. All category
summaries share the same trimming rule. Icon-only actions have hover and focus
tooltips; supplier changes also reset the password reveal tooltip.

The prototype shares one dialog implementation for account entry, password reveal,
reserved Check feedback, and Save/Cancel. Production shares the existing field
presentation and dialog mechanics where behavior matches; proxy and supplier
drafts, validation, secret handling and checks retain their own owners. No generic
editor workflow with mode flags or callback lists is required. Proxy values and
credentials never become subtitle values. Preserve None and SOCKS4's disabled
fields, Fluent spacing and equal footer button widths beside reserved feedback.

The supplier selector includes OpenSubtitles, SubDL, and SubSource. SubDL and
SubSource need the person's own API key, so for them the dialog hides User name
and labels the secret field API key; its reveal button reads Show API key. Each
supplier's flyout links that supplier's own privacy policy and terms. Selecting another supplier clears only draft
credentials; Cancel preserves the saved configuration. The prototype toolbar
supplies not-checked, successful-check, unconfigured, access-error,
proxy-refusal, quota, and save-error outcomes. The prototype has no real proxy,
so with a SOCKS4 proxy saved, Check and turning Automatic subtitles On simulate a
proxy that refuses host names; the toolbar can still select a successful check.
Saving a changed proxy returns the supplier status to Not checked. Status starts Not checked, and successful-check feedback is marked as a
sample. Check simulates the selected outcome; Not checked becomes a sample success
only when Check is invoked. Saved supplier/account changes invalidate the previous
result unless the unchanged draft has just been checked. Editing that draft clears
its result, and Cancel never changes the saved status. Blank
credentials or a complete form never imply successful access. Production Check and
saved-access validation call the same supplier operation. Setup remains editable
while Off; enabling requires readiness, and a later failure preserves On for quiet
recovery. Saving a changed supplier resets Off and cancels pending work; finished
targets need Find after re-enabling. These simulations do not establish
supplier authorization or real account validation. No supplier requests or file
downloads occur. Use sample credentials;
all edits last until reload, and the sample data view masks the subtitle password.
Production reads confirmed access and retry state from the C# subtitle owner under the
subtitle design, including actual quota reset times.

Production uses WinUI AutoSuggestBox with the supplier's full language catalog,
matching localized names, native names, and stable codes. Control choice follows
the WinUI Gallery `gallery-autosuggestbox-1` sample and Microsoft's
[AutoSuggestBox guidance](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/auto-suggest-box).
The HTML stand-in uses a native datalist and Add/Enter, with 188 ISO language and
regional samples, including Hebrew, Japanese, and Brazilian Portuguese. That sample
catalog is not a claim that a supplier supports every entry. Saved sample choices
use language codes. No additional language-selection library is required.

## Connection setup

### Page and proposal

Keep the page inside Settings, with measurement/manual speed entry and preset
choice on the left and Current/Proposed settings on the right. When those
sections no longer fit comfortably, stack measurement first and proposal
second. Base the transition on content width. No splitter or scrolling modal
is required. The page body may scroll on a small window; Apply/Cancel stay in
the persistent footer, aligned and reachable.

Test, Cancel (test), and Retry occupy one fixed action slot. Progress is directly
under it in reserved space. Avoid fabricated percentages or transfer counts;
show indeterminate progress unless the engine can supply meaningful progress.
Normal progress is inline, not a toast or dialog. Announce phase changes for
accessibility without announcing each countdown tick.

Users can test or enter download/upload capacity manually in **Mbps**. Reduced
and Balanced require valid capacity in both directions; Full speed can remove
caps without it. Measurements create a proposal; they never apply automatically.
Emphasize changed cells and subdue unchanged ones while keeping rows in a stable
order. Keep peer limits visible as preserved values. Apply is enabled only when
the proposal is valid, actually changes settings, and no test phase is busy.

### Preset meaning

The final direction is bandwidth plus queue activity, with **peer connection
limits preserved**. Earlier suggestions to scale peers or queues continuously
with Mbps were not adopted. Queue limits must recover when moving from Reduced
to Balanced or Full speed; simply leaving Reduced's small queue unchanged fails
the owner's requirement.

The current HTML's candidate policy is:

| Preset | Download/upload caps | Active downloads | Active seeds | Active overall |
| --- | --- | --- | --- | --- |
| Reduced | 50% of each entered capacity | 2 | 2 | 3 |
| Balanced | 85% of each entered capacity | 3 | 5 | 8 |
| Full speed | Unlimited bandwidth | 3 | 5 | 8 |

These are prototype policy values, not validated performance recommendations or
claims about qBittorrent/libtorrent defaults. Keep the agreed behavior above;
validate the final policy against TinyTorrent's actual queue owner before
shipping. Full speed means no bandwidth cap, not unlimited queue activity.
The percentage caps do not guarantee bandwidth availability or torrent speed.

**Custom is derived**, not a fourth saved profile. Compare the current two
standard bandwidth caps and three queue limits with the chosen policy and
capacity. Manual changes show Custom when they no longer match a preset;
matching again can restore its name. Peer limits are outside this policy, so
editing only those does not make it Custom. Preserve individual editing of all
limits; Custom must not be an extra gate before controls become available.

Apply only the proposal's intended changes through the existing settings owner.
Alternative caps, saved schedule periods, memory, slow-torrent handling, and
seeding stop rules stay intact. The HTML preserves an active Weekly schedule;
otherwise it proposes Standard limits so the generated standard caps take
effect. That mode change is included in the preview. Changing limits must not
silently lift an independent pause.

The prototype converts Mbps to KiB/s before comparing its settings. Production
must use the engine's canonical rate units and deliberate rounding; otherwise
the preview can show a change that disappears on apply or falsely show Custom.

### Test and temporary suspension

The revised lifecycle deliberately supports testing twice without starting and
stopping torrents between successful tests:

1. Temporarily suspend torrent traffic for the test and wait for it to stop.
2. Measure download, then upload.
3. After success, show results and retain the temporary suspension for up to
   **three minutes**, with a visible resumption countdown.
4. Test again during that interval using the existing suspension. A successful
   retest starts a new three-minute hold; editing fields alone does not extend it.
5. Leaving Connection setup, applying, cancelling the page, or expiration of the
   hold releases the suspension. Explicit test cancellation, failure, or stopping
   timeout restores activity immediately through the restoring phase.

Restoration means **release this operation's temporary suspension**, not Resume
all. Preserve individual paused/running intent, queue order, and independent
global, schedule, or adapter restrictions. Account for policy changes made
during the operation rather than replaying a stale snapshot of torrent states.
Do not individually pause every torrent or persist a new user pause mode.

The engine owns suspension, its deadline, and recovery: losing or closing the
window must not strand transfers. Browser timers, a synthetic zero speed, and a
UI completion message are not proof that peer traffic stopped or was restored.
Use the existing pause/policy owner and verify its real quiescence semantics;
setting queue limits to zero is not sufficient. Restoration failure must remain
an honest recoverable state rather than falsely announcing completion.

**The HTML test is entirely simulated.** Its short timers, failure selector,
200/40 Mbps result, and sample speed display exercise UI states only. No speed
test provider or production integration was selected. M-Lab NDT7 was discussed
as a candidate, not an approved dependency. A provider still needs a deliberate
integration decision; until then, manual capacity entry is the real input path.

## Polling, history, and memory are different controls

| Control | Owner and meaning |
| --- | --- |
| Advanced → Interface → Refresh interval | WinUI's periodic requests for fresh engine data. Display/edit in milliseconds; prototype default 1000 ms, range 1000–10000 ms. This does not change engine timing, transfers, or history sampling. |
| Advanced → Speed history → Recent sample interval | Engine-owned aggregation for the five-minute history. Prototype choices: 1, 5, or 10 seconds. |
| Advanced → Speed history → Older sample interval | Engine-owned aggregation for the 24-hour history. Prototype choices: 10 or 30 seconds, 1 or 5 minutes. |
| Disk write buffer and checking memory | Engine/libtorrent resource targets with separate purposes; neither is a total app-memory cap. |

Current [PipeClient](src/Services/PipeClient.cs) still creates a fixed one-second
refresh timer. Adding the prototype field did not change it. Keep command-driven
refreshes responsive and the [protocol's](../docs/protocol.md#snapshots-and-detail)
bounded, command-priority read behavior when making the periodic interval
configurable. Do not apply it to unrelated preview or engine timers by accident.

History belongs to the engine, continues while WinUI is closed, is session-wide,
and remains bounded in memory. UI polling never supplies its samples. The
current [SpeedHistory](../engine/inc/SpeedHistory.h) and
[history contract](../docs/engine.md#state-and-work) use fixed cadences; exposing
granularity requires real engine work. Coarser storage loses detail; choosing a
finer interval later cannot recreate it. The treatment of already stored samples
when the interval changes remains to be specified at that owner.

Follow [disk-write caching](../docs/engine.md#disk-write-caching) rather than
copying a qBittorrent memory setting by name. The pinned libtorrent 2.1 design
does not provide the old configurable disk-cache size. Lowering a write buffer
can reduce queued-write memory, with a throughput tradeoff; it cannot promise a
maximum process working set or control Windows' cache.

## Scheduler: preserve the working feature

Schedule retains its own category and existing engine behavior. The compact
standard/alternative download/upload summary sits above the week, with Edit
leading to the existing Speed limits fields, so users can see what those names
mean without adding another editor below an already tall schedule.

Preserve local-time periods, overnight continuation, overlap precedence, and
standard limits outside special periods through the existing schedule owner.
The HTML demonstrates editing saved periods while the schedule is off; changing
the mode retains them. Its week, simple period editor, Add/Remove/Undo, and
sample data do **not** cover the production scheduler's complete interaction.
Keep the existing drag creation/move/resize, keyboard routes, save ordering,
validation, and focus behavior described in the
[interface contract](../docs/interface.md#settings). Reuse the existing scheduler
and period editor rather than rewriting them from this browser demo.

## Differences to reconcile during implementation

These findings prevent the implementer from silently choosing whichever file
was read last. They are not permission to erase working behavior or reopen the
owner's settled UI decisions.

| Area | Prototype/session direction versus current contracts |
| --- | --- |
| Placement and scope | This prototype places Queue and Peer connections in Speed limits and expands Advanced. The current interface contract still places Queue in Transfers and names a smaller Advanced inventory. Update the relevant contract alongside adoption; do not classify every sample field as already implemented. |
| Dependent rows and help | The owner's stable-layout and tooltip-only decisions govern. Older Settings prose still mentions revealing dependencies and visible descriptions. Read it with the newer text/row rulings, not as a reason to reintroduce wrapping help. |
| Schedule when inactive | HTML keeps saved periods editable while inactive; current interface prose shows a neutral fixed-mode week and hides the period editor outside Weekly schedule. Resolve this presentation difference explicitly while preserving saved periods and scheduler capabilities. |
| Adding a schedule period | HTML uses a new-period draft with Save/Cancel. The current production contract creates/opens a default period and applies edits individually. Preserve the existing implementation unless this difference is deliberately resolved; copying HTML would change the journey. |
| Queue implementation | HTML's explanatory text mentions how slow torrents count. Verify it against the actual queue/policy implementation, rather than assuming raw libtorrent `active_*` settings have exactly that behavior. The prototype must not introduce a second queue owner. |
| New asynchronous operation | The speed-test suspension/recovery lifecycle needs engine/protocol support. It is not implemented by the HTML. |
| Cadences and policy values | Configurable polling/history and the preset table extend fixed current behavior. Keep the ownership boundaries above; validate ranges/defaults at the real owner and update the corresponding contracts when implementing. |

## Focused acceptance review

Use [testing guidance](../docs/testing.md) to choose evidence when implementation
is authorized. The current prototype work carries an explicit no-run/no-compile
instruction; these are handoff criteria, not authorization to launch anything.

- C retains search, category index, compact rows, one advanced switch, and every
  existing feature. Search can reach an advanced setting without a duplicate editor.
- Across Speed limits, numeric fields and switch tracks align even in different
  cards. Across a category with no units, controls keep their ordinary position.
  Advanced on/off recomputes this once for the category. Check duration selectors
  as well as plain units, disabled rows, narrower widths, and larger text.
- Changing folder mode, incomplete-folder use, validation, and switch state does
  not add a wrapped row or displace neighboring controls. Clear removes only
  unlimited-capable limits. Full choice labels and paths remain accessible.
- Tooltips dismiss normally after pointer/focus changes and do not obscure the
  page merely because the last clicked control remains focused. Native keyboard
  navigation and meaningful shortcuts still work.
- Reduced → Full speed restores queue capacity; manual controlled changes show
  Custom; peer-only edits preserve preset identity. Apply cannot commit invalid
  or unchanged proposals. Scheduled periods, peer limits, and seeding rules survive.
- Success, retest, cancellation, failure, timeout, navigation, and window loss
  all release only the test's temporary suspension at the appropriate time.
  Previously paused torrents stay paused; a restored state is confirmed, not timed.
- Changing UI refresh leaves engine history continuous while the window is
  closed. History settings affect engine aggregation only. Memory help makes no
  total-memory guarantee.
- Scheduler gestures, keyboard editing, pending/invalid saves, overnight periods,
  and preserved schedules work at least as well as before the Settings changes.
- Native English/Spanish, Light/Dark/High Contrast, and text scaling follow the
  existing [localisation](../docs/localisation.md) and interface contracts. The
  prototype's English-only language choice is not localisation evidence.
