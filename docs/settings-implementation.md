# Native Settings implementation

The approved reference is `app/settings-windows-prototype.html`, variant C,
with the decisions recorded in `app/settings-windows-prototype.md`. The browser
holds sample data; native Settings continues to use its existing state, save
paths, dialogs and engine contracts. This work changes composition rather than
replacing those owners.

## Plan and completion evidence

1. Replace the category strip and introductory index with the approved native
   sidebar and Home composition. Keep the title-bar search, acrylic gear marks,
   advanced disclosure, stable category instances and existing start-page
   preference. Verify category selection, translated titles and narrow layout.
2. Apply one shared settings layout: 24 DIP major-section gaps, 8 DIP sibling
   gaps, 8 DIP heading gaps, 16 DIP horizontal insets, themed row separators and
   trailing controls aligned to the actual card edge. Use stock Expanders and
   consistent header descriptions. Verify Light, Dark, contrast and text scaling.
3. Regroup General, Transfers, Network, Library and Advanced around the approved
   tasks. Move category metadata with the fields so search reveals the right
   destination. Preserve the native proxy dialog, registration repair, subtitle
   suppliers, validation, IME handling and asynchronous save ownership. Reuse
   the supplier catalogue for inline language selection, retaining selected
   languages while filtering. Find and Scan feedback occupy stable slots.
4. Render both speed profiles and Connection setup through one shared native
   composition on Speed limits and Schedule. Keep both profiles editable,
   TextBox numeric input, the four existing operating modes and the existing
   scheduler model. Keep the Schedule mode selector accessible outside the
   expander. Adopt the approved period editor: derived when/behavior summaries,
   two behavior radios, inline days and 24-hour Start/End beside Remove.
   Share decimal MB/s formatting across profiles, Home, Schedule and Connection
   setup, with at most two decimals and `<0.01` for tiny positive summaries.
   Viewing rounded values never changes saved limits; capacities remain Mbps.
5. Replace the theme dropdown with three equally sized keyboard-accessible
   preview choices. Reuse the existing theme command and confirmed state;
   preserve language selection and the other Appearance settings.
6. Extend the existing navigation owner to remember Settings destinations.
   Home, related links and search use one reveal-expand-scroll-focus path with
   the approved three-second neutral destination outline. Home uses existing
   native facts and shared Windows integration controls. Session and lifetime
   payload totals come from the engine's cumulative libtorrent counters and
   existing saved document. Verify that a settings save and engine restart retain
   lifetime totals without counting a session twice.
   Connection setup alone has a hierarchical breadcrumb. Back restores the
   previous related destination and its scroll position. Sidebar categories
   replace the current Settings visit, so Back returns to the workspace without
   stepping through every category. The clickable TinyTorrent brand and the
   footer's last action return directly to the originating workspace through
   its existing command; the label names Torrents or Library. Show advanced
   settings sits above that action. Connection setup stays inside the same
   Settings sidebar, with Speed limits selected and its caller retained for Back.
7. Refresh English and Spanish catalogues and superseded interface prose.
   Have Astra adversarially review the complete source and resolve findings,
   including coding standards, ownership, navigation and save behavior.
8. Only after implementation and source approval, compile the affected native
   target in Debug x64 and run the focused existing capture review, extended
   for expander content, category Back/scroll and the changed editors. Inspect
   native screenshots and bounds, resolve defects, obtain final Astra approval,
   and report evidence and any remaining limits.

No CommunityToolkit dependency, duplicate persistence owner, browser state
model, generic settings renderer or replacement proxy dialog is needed.

## Authority and review

`Settings` and `Setting` own ordinary edits; `Schedule`, `ConnectionSetup`,
`Subtitles.Settings` and its batch owner retain their workflows. `MainWindow`
owns dialog routing and `Navigation` owns history. Shared speed editors consume
the same four settings and the existing edit/commit path on both pages.

Astra reviews the implementation against AGENTS.md and the approved reference.
Source review precedes compilation. Completion requires real native behavior,
not just a visual match or a successful build.

## Initial review — 12 October 2026

Astra approved the accumulated source changes on 12 October 2026, including
engine transfer accounting, Settings navigation, compact sidebar, shared
related-setting cards, Schedule layout, direct switches and the copy audit.
Sidebar selection replaces the Settings visit; related links preserve their
return path. The existing capture journeys now exercise twenty sidebar
selections, direct exit and a native KiB/s threshold edit and refusal.

The artwork payload includes only the reduced page images and theme JPEGs in
`app/src/Assets/`; the generated masters stay outside the repository, in
`C:\SynoSoftware\TinyTorrent2-artwork\settings-artwork\`. Settings reveals the existing
window acrylic, and the title-bar search uses rounded corners. Queue thresholds
display KiB/s while the engine continues to store bytes per second.

Debug x64 engine and capture-enabled app builds passed in `artifacts/lanes/2`,
with zero warnings and errors. The compiled navigation-owner checks passed,
including twenty sidebar selections followed by one Back to Torrents. Output
checks found no generated folders outside the designated artifact tree. The
eleven reduced artwork files total 516,983 bytes; originals are absent from the
built payload.

With the owner's permission, the running app exited through its supported Exit
command for disposable verification. Renewed evidence:

- `artifacts/evidence/Accounting-11d94fbc-f108-400c-a1a4-433bfc45d959` passed
  seeded 64-bit lifetime totals, settings saves, repeated restarts and legacy
  defaults. It does not establish positive payload accumulation.
- `artifacts/lanes/2/evidence/Capture-edits-3962e324-33ee-4f39-97ec-e22389a9db36`
  passed native edits, pending-save races, departure, KiB/s conversion and refusal,
  and Schedule persistence.
- `artifacts/lanes/2/evidence/Capture-schedule-d0898107-2a4f-4c3c-bb8e-5cc3a153a275`
  passed all twelve language/theme/size cases, exact-minute overnight periods,
  invalid-input retention, moving and removal.
- `artifacts/lanes/2/evidence/Capture-settings-prototype-276cf5e2-d8d1-4b8d-bf87-1f98c44e9440`
  passed English Dark and narrow Spanish Light, related-link Back and scroll
  restoration, the native footer exit button, twenty sidebar selections,
  filtered subtitle selection, rapid changes and Connection setup submission.

Astra granted final native approval after reviewing these captures and journeys.
The footer exit uses the existing workspace command and remains available when
the sidebar is compact. Library disconnect now notifies its existing readiness
owner so the UI reflects the disconnected state. An earlier startup timeout did
not recur; its root cause remains unconfirmed.

High Contrast, increased text scaling and physical input were not verified.
XAML captures retain alpha and exclude the desktop acrylic, so their displayed
colors do not establish the composited desktop appearance.

After verification, the updated app and engine were reopened from lane 2 using
the user's normal store. Disposable verification processes had already exited.

## Follow-up review — 13 October 2026

The owner requested screenshots of every visible Settings category with Advanced
off and on, with its expanders collapsed and expanded, including every subpage
and dialog. Each image must be reviewed and defects corrected before completion.
The existing capture engine waits before rendering and requires a longer period
of unchanged pixels; frame metadata identifies captures that did not settle.
The review includes resizing from compact to expanded navigation and checking
content alignment against the page title and window edge.

Desktop interaction is stopped at the owner's request. The shared Windows folder
picker was inspected through a desktop screenshot before that instruction;
XAML captures do not include system dialogs.

The owner subsequently stopped all screenshot capture and requested code review.
The resize capture reproduced the reported clipping; setting horizontal content
alignment alone did not correct it. Settings and Connection setup now give the
scroll viewer an uncapped content surface with a capped, centered inner column.
Expanded headers collapse hidden state summaries so they do not reserve a blank
line. Independent code review approved these changes; they have not been built
or visually verified. The earlier evidence above describes the initial
implementation, not approval of every current Settings state.

The subsequent source pass applies the owner's cumulative corrections:

- Settings expanders use the LabForms Orders drawer approach: animated height,
  clipped full-height content, automatic sizing after opening, and reversal from
  the current height. Native headers and accessibility remain in use.
- Schedule creation requires an intentional drag; ordinary clicks select or
  dismiss an editor. Its focus and navigation cues cover the complete card.
- Connection setup uses the existing page motion with the sidebar stationary;
  its slider cards retain the concise layout and native breadcrumbs have space
  around their chevron.
- Shared setting rows center their controls in the primary row's available
  height. Local display preferences use the existing optimistic setting value,
  and Home avoids unrelated draft-status refreshes.
- Proxy Check enablement binds to its model, and feedback observes the actual
  footer's lifetime. Proxy and account dialogs size to accommodate the check
  result beside their actions.
- Theme selection observes its own draft setting instead of repeatedly
  refreshing for every unrelated Settings notification. The sidebar uses larger
  icons and labels, with a navigation-aligned Advanced state and switch.
- Caption commands use the requested gear and lightbulb icons. Caption search
  uses the standard control radius and native text-editor padding.
- All nine Settings tab headers share artwork, title and two Lucide facts from
  the existing owners. Measured widths move facts below the title or into
  separate rows. Home has one Home title beside the logo, with activity beneath
  it and larger session and all-time totals; the duplicate hero is removed.

Independent source review approved the cumulative corrections and the shared
headers, including conditional row spacing that avoids an empty strip in the
wide header. The affected XAML parses and the scoped diff has no whitespace
errors. These are source checks only; this pass runs no builds, tests, launches
or screenshots. A separate blank-page incident remains unresolved: card contents
stayed missing after closing the dialog and navigating, requiring a restart;
the owner cannot currently reproduce it. No speculative rendering patch has
been applied, and this incident is not counted as fixed.

### Motion source review

Each gesture has one moving surface. Entering Settings uses the shell transition;
entering or leaving Connection setup uses its clipped page host; settled category
changes use the existing short directional entrance. The enclosing storyboard's
actual state suppresses nested entrances, including rapid navigation.

| Element or interaction | Motion owner and page behavior |
| --- | --- |
| Navigation selection and sidebar controls | Native NavigationView and ToggleSwitch states; no extra icon or label entrance |
| Category and Connection navigation | Existing Motion, 180 ms cubic ease-out; one enclosing surface at a time |
| Settings and period expanders | Shared SettingsExpander, 260 ms opening / 220 ms closing; full-height content behind the clip |
| Shared period editor handoff | An empty outgoing drawer settles immediately; the one editor moves to the newly opened row |
| Conditional setting rows | Existing native reposition transition; no separate decorative entrance |
| Buttons, switches, check boxes, radio choices, sliders and editors | Native hover, press, focus, selection and gesture behavior |
| Theme tiles | Native 83 ms background transition and fast checkmark opacity transition; draft selection remains immediate |
| Subtle icon buttons | Native 83 ms background transition restored in the shared style |
| ComboBox, TimePicker, menus, tooltips and information flyouts | Existing native popup transitions; no second page-local effect |
| Dialogs | Existing native scale/fade states in the shared Dialog template |
| Pending connection, proxy and account checks | Existing native progress indicators only while work is pending |
| Schedule dragging and resizing | Direct feedback on rendering frames, with no easing behind the pointer |
| Current time, totals, rates and other live facts | Immediate quiet updates; no repeated entrance or number animation |
| Validation and result text | Immediate state and accessible feedback; no shake or delayed message |
| Focus and destination cues | Native focus or the existing temporary outline; no decorative pulse |
| Responsive reflow and static artwork/headings | Immediate layout, without an animation competing with navigation |

Custom motion checks the Windows animation preference. Stock control transitions
retain WinUI's handling of that preference. Source review covers interruption,
ownership and clipping; reduced-motion rendering and live interaction timing have
not been verified at runtime.
