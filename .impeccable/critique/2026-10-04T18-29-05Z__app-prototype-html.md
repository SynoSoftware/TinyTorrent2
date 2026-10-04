---
target: WinUI 3 critique of TinyTorrent prototype
total_score: 31
max_score: 40
na_heuristics:
p0_count: 0
p1_count: 0
target_identity: "file:C:\\SynoSoftware\\TinyTorrent2\\app\\prototype.html"
target_fingerprint: "sha256:04dd32410b83cefa4531ef3fb6ebfb9e0903a62dc4cf8d4c3545e815034a8df0"
target_path: "C:\\SynoSoftware\\TinyTorrent2\\app\\prototype.html"
timestamp: 2026-10-04T18-29-05Z
slug: app-prototype-html
closed: true
---
Method: dual-agent (A: /root/winui_design_review · B: /root/winui_evidence_review)

Yes: Add and torrent Properties → Files share fileBrowser(scope), including hierarchy, wanted selections and folder priority actions. The instructional Add and Preferences subtitles are removed; supplementary guidance is in tooltips. Units and mains-power conditions remain in labels, and status/errors/deletion consequences remain visible.

The design feels like a focused Windows utility. Keep the main table, C inspector and LabForms-style preferences. The next improvement is clearer control states and native interaction fidelity.

| Heuristic | Score /4 | Assessment |
|---|---:|---|
| System status | 3 | Schedule Off is weaker than its bright bands. |
| Real-world match | 3 | Familiar files/folders and destination controls. |
| Control and freedom | 3 | Cancel, close and independent file choices work. |
| Consistency and standards | 4 | Shared file tree and cohesive Windows composition. |
| Error prevention | 3 | Safe destructive defaults and Add validation. |
| Recognition over recall | 3 | Some distinctions require tooltip access. |
| Efficiency | 3 | Bulk choices and shortcuts; tree keyboard gap. |
| Minimalist design | 4 | Clear hierarchy without redundant subtitles. |
| Error recovery | 3 | Specific errors and retained drafts in source. |
| Contextual help | 2 | HTML titles do not establish keyboard access. |
| **Total** | **31/40** | **Good** |

These scores assess the prototype, not verified native WinUI runtime behavior.

**Specificity and strengths.** The composition serves torrent tasks rather than a generic dashboard. Folder hierarchy, wanted counts and selected bytes stay together. Leave unchanged preserves individual priorities. Properties and Preferences have distinct layouts with consistent light/dark hierarchy.

**Priority issues**

1. **[P2] The scheduler's picture contradicts its Off state.** Saturated alternative-limit bands and paused hatching still look operational while the switch is off. A two-hour Normal limits band also clips: 58 pixels available for 73 pixels of text at 1280×720. Give saved periods an unmistakably inactive appearance while Off, keeping them editable and legible. Omit labels that cannot fit; preserve the legend and full period tooltip. No additional help paragraph. Suggested command: $impeccable polish.

2. **[P2] Protect file names at narrow widths.** At the tree's 600-pixel minimum, Size, Progress and Priority reserve 400 pixels, leaving about 200 for Name before indentation, checkbox and icons. Deep filenames can become difficult to distinguish. Give Name a meaningful minimum and permit horizontal scrolling when required; preserve standard ComboBox sizing. This is a source-supported risk, not a reproduced compact-window failure. Suggested command: $impeccable adapt.

3. **[P2] Complete native keyboard and tooltip behavior.** The browser Files tree has 58 individual focus stops across 24 rows; ArrowDown does not move to another tree item. Some help is attached to nonfocusable headings or row containers. In WinUI, use TreeView navigation with a predictable path into row controls, and attach ToolTips to the actual focusable controls with appropriate automation descriptions. Keep help off the working surface. This is a prototype gap and native handoff requirement, not evidence that a shipped native app fails. Suggested command: $impeccable harden.

Microsoft documents focus-triggered ToolTips and composite keyboard navigation: https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/tooltips and https://learn.microsoft.com/en-us/windows/apps/develop/input/keyboard-interactions.

**Cognitive load and emotional journey.** Low at the reviewed desktop size. Five Preferences categories and six inspector tabs are coherent navigation, not six simultaneous editing decisions. Opening Files is direct and reassuring; the main uncertainty is whether the brightly rendered schedule is active.

**Persona checks.**
- Power user: navigating a large tree should not require dozens of Tab presses.
- Occasional downloader: an Off schedule should not look like it currently governs transfers.
- Keyboard user: supplementary help and full paths must be available without hovering.

**Minor observation.** Add has both a scrolling dialog body and a scrolling tree. Destination appears below the initial complex-tree view, but Add/Cancel remain reachable. This is content pressure, not a blocked task.

**Deterministic evidence.** Five advisory findings in app/prototype.html: gpt-thin-border-wide-shadow ×4, manually mapped to lines 210, 217, 219 and 290; repeating-stripes-gradient ×1, mapped to lines 189 and 272. The detector supplied line 0. All are false positives for this brief: intentional desktop transient elevation/prototype scaffolding and semantic rare/paused hatching. No reliable injected overlay was available; screenshots and DOM measurements provided browser evidence.

**Questions to consider.** Can Off be understood from the weekly map alone? Can deep files be distinguished without hovering? Does every control's supplementary tip appear on keyboard focus?

**Follow-up — recommended procedures completed in order: polish → adapt → harden.**

- Polish: saved schedule periods use neutral dashed outlines while Off. Labels appear only in bands wide enough to fit; the legend, period list, and full weekday tooltips remain.
- Adapt: the shared file browser reserves at least 480 pixels for Name. Size, Progress, and Priority retain their sizing, with horizontal scrolling at narrower widths. The Add viewport measured 696 pixels against 760 pixels of content, preserving the 480-pixel Name column.
- Harden: the tree has one active row, with arrow navigation, Home/End, parent/child expansion, Space for wanted choices, and Tab/Enter/F2 paths into row controls. Focus and scroll survive updates. Focus tooltips and accessible descriptions expose full paths and supplementary control help. A 24-row tree uses three focus stops at the active row rather than 58 across all rows. Mixed child priorities survive Leave unchanged, including after adding the preview and opening Properties.

The owner's subsequent shell correction is included: one caption row, Add and selection controls restored beside the theme button, Transfers removed, and global search replaces the separate torrent filter. Errors remains available in the status bar. Search holds its position between Torrents and Settings.

Verification: light and dark browser inspection at 1280×720; inactive and active schedules; the constrained Add viewport; shared Add/Properties file choices; keyboard focus and tooltips; global search; loading, empty, and disconnected states. Disconnected speeds show em dashes and transfer actions are disabled. The final script parses, browser error logs are empty, and generated-output inspection found no output outside artifacts. The mechanical detector reports six advisory findings: transient/prototype borders with elevation and semantic paused/rare hatching remain intentional Windows design exceptions.

The original 31/40 score is historical and has not been rescored. These changes establish browser prototype behavior; native WinUI TreeView, ToolTipService, UI Automation, and Windows contrast-theme runtime verification remain implementation work.

**Follow-up — status filter drawer.**

The single caption row now includes a labeled Filters button. It opens a 196-pixel inline left pane, following the WinUI Gallery SplitView Inline pattern. The pane starts closed and contains All torrents, Downloading, Seeding, Paused, Queued, and Errors, each with a count derived from the sample session. Closing the pane keeps the active filter and count visible in the caption. Application pages remain in the hamburger menu. Filters are also available through global search. Changing the filter clears selections outside its visible rows, keeping selection commands scoped to what the user can see.

Verification: all six filters return the expected sample torrents; counts update after individual Pause and session Pause all/Resume all while preserving individually paused samples. Arrow keys, Home/End, and Escape preserve predictable focus, with one tab stop in the filter list. Settings hides the torrent pane and returning to Torrents restores its filter. Light/dark inspection and a 960×720 compact-window check show one caption row without overlapping controls; the torrent table scrolls horizontally as required. Loading, disconnected, and empty scenarios remain usable, disconnected speeds are em dashes, the script parses, browser error logs are empty, and generated-output inspection found no output outside artifacts.

The detector reports the six existing advisory findings and one new side-tab warning. The short selected-row accent indicator is an intentional Fluent selection cue rather than a decorative card border; native selection recognition earns this exception. No new elevation or gradients were added. Native SplitView/ListView and Windows contrast-theme verification remain implementation work.
