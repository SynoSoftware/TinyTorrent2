---
target: Current TinyTorrent prototype through a WinUI 3 lens
total_score: 30
max_score: 40
na_heuristics: 
p0_count: 0
p1_count: 0
target_identity: "file:C:\\SynoSoftware\\TinyTorrent2\\app\\prototype.html"
target_fingerprint: "sha256:f73e53edf029c7f1c523109b57277e985bd4fb897b5baf85d1454873ab82e0ec"
target_path: "C:\\SynoSoftware\\TinyTorrent2\\app\\prototype.html"
timestamp: 2026-10-04T20-55-28Z
slug: app-prototype-html
closed: true
---
Method: dual-agent (A: /root/critique_design_a · B: /root/critique_evidence_b)

Target: app/prototype.html, variant C, at commit 4082f7f. Operate mode; WinUI 3 lens. This is a browser prototype review, not native runtime verification.

The composition fits TinyTorrent. The single title bar, transfer table, optional status drawer, and torrent inspector support different tasks without becoming a generic dashboard. Keep the approved layout. The biggest opportunity is making keyboard actions as predictable as the pointer path.

| Heuristic | Score | Finding |
|---|---:|---|
| System status | 3 | Clear progress, transfer states, counts, and connection feedback. |
| Real-world match | 3 | Familiar torrent and file vocabulary. |
| Control and freedom | 3 | Clear exits; focus return has gaps. |
| Consistency and standards | 3 | Shared file browser; keyboard activation differs from pointer activation. |
| Error prevention | 3 | Add review, validation, and safe destructive defaults. |
| Recognition over recall | 3 | Current filter stays visible; contextual controls are named. |
| Efficiency | 2 | Useful search and tree navigation, but shortcuts and repeated keyboard work fail. |
| Minimalist design | 4 | Compact chrome and restrained, useful emphasis. |
| Error recovery | 3 | Specific messages and preserved drafts; native recovery unverified. |
| Contextual help | 3 | Tooltips and accessible descriptions preserve uncluttered working surfaces. |
| **Total** | **30/40** | **Good** |

Assessment A scored 31/40 independently. The synthesis lowers Efficiency from 3 to 2 because Assessment B reproduced additional failures in keyboard command scope and focus return. There is no demonstrated P0 or P1 issue.

**What works.** Closing the drawer leaves Downloading · 2 in the title bar, with two matching rows. Its counts, named states, selection cue, and keyboard navigation fit a Windows utility. A supplemental pane that shares space with content matches Microsoft's [SplitView guidance](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/split-view); it does not require reintroducing application-page navigation in the pane.

The title bar separates application pages, adding torrents, and commands for the selection. Global search shows scope labels that distinguish Pause all from Pause for the selected torrent. Microsoft supports embedded search and controls in a [custom title bar](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/title-bar); native implementation must still reserve the drag region and system caption area.

Properties retains the accepted C inspector layout, while Settings retains its LabForms composition. Add and Files share a tree and priority vocabulary. F2 on Movies focuses its Priority control. Folder Leave unchanged remains a useful no-op that preserves child choices.

**Priority issues, in order.**

1. **[P2] Enter can open an empty inspector for a focused torrent.** Reset, choose Errors, focus the Added column header, then Tab to OpenStreetMap and press Enter. The row is focused but not selected; the inspector says Select a torrent to see its details. Clicking the same row works. Select the focused torrent through the existing selection path before opening its properties. Keep command ownership shared with pointer activation. Source: app/prototype.html:1200. Suggested command: $impeccable harden.

2. **[P2] Sorting and closing the inspector lose keyboard focus.** Press Enter on the Status header, or activate Close inspector with the keyboard. In both cases focus becomes BODY. Preserve the active sort header after rendering; when the inspector closes, return focus to its torrent row, with a valid fallback if that row is no longer visible. Source: app/prototype.html:752, :1035, :1090. Suggested command: $impeccable harden.

3. **[P2] Advertised shortcuts work only with row focus.** With Ubuntu selected, Ctrl+P on the Pause toolbar button leaves it Downloading. Ctrl+P on its row pauses it. Selection commands should use the current selection from the toolbar and inspector as well, while retaining the existing exclusions for text editing and composition. Route accelerators through the same command owner. Source: app/prototype.html:1209. Suggested command: $impeccable harden.

4. **[P2] Add clips the Priority dropdown at its normal size.** At 1280×720, Add torrent file → Open → Sample folder tree shows a 696-pixel file viewport containing a 760-pixel tree. Leave unchanged is clipped and its dropdown arrow sits beyond the right edge. This is a routine control that should fit at the default Add width. Increase the dialog's usable content width to fit the shared tree, keeping the protected Name width and standard Priority control. Retain horizontal overflow for genuinely constrained windows. Source: app/prototype.html:183, :253, :254, :267. Suggested command: $impeccable layout.

**Cognitive load and emotional journey.** Six filters, six inspector tabs, and five Settings categories exceed the skill's literal four-choice rule. These are recognizable navigation sets rather than simultaneous editing decisions. Splitting them would add work. The literal checklist counts two failures, but practical load is low: related commands are grouped, filtering stays explicit, and complexity appears when requested. The main view feels calm; Add provides identity, selected bytes, and destination review. Its clipped control and inconsistent keyboard behavior introduce avoidable friction.

**Persona checks.** Alex can use search, folder choices, and F2, but loses momentum when shortcuts depend on focus location. Sam encounters lost focus and empty properties during keyboard work; HTML semantics do not prove native UI Automation. Jordan has understandable Add labels, but should not have to scroll to discover the Priority arrow. The logo-to-menu transformation is an explicitly requested design choice, so it is retained as a discoverability tradeoff rather than reopened as a new priority issue.

**Minor observations.** Escape from More actions produced a repeatable InvalidStateError in tooltip showPopover while the menu was closing (app/prototype.html:466, :1021, :1144). The menu closed and focus returned correctly. Fix this during prototype hardening; it is not evidence of a WinUI defect. The folder-error message suggests choosing an accessible folder, while Move is available in More actions; an adjacent recovery route is a future discoverability improvement. Table clipping with the drawer open is ordinary horizontal table overflow, not a reason to replace the selected native TableView.

**Deterministic evidence.** One detector run returned seven matches: six advisories and one warning. gpt-thin-border-wide-shadow maps to app/prototype.html:73, :242, :249, :251, :326; side-tab maps to :57; repeating-stripes-gradient maps to :221 and :306. The detector supplied line 0, so locations were mapped manually. These are contextual false positives: transient elevation, prototype scaffolding, the native selection cue, and semantic rare/paused hatching. No additional product defect was established by the detector. Browser evidence comes from screenshots and DOM inspection, not injected overlays.

Sampled text contrast passed in both themes: light header/footer/error/seeding ratios 6.08/5.67/6.88/6.36:1; dark equivalents 8.56/9.34/8.76/8.64:1. At 960×720, caption and drawer remain within the window; the 722-pixel torrent viewport scrolls its 1000-pixel table. Disconnected preserves last-known rows and disables transfer actions; Loading exposes a busy status. These checks are targeted evidence, not a full accessibility audit. Native contrast themes, DPI, text scaling, actual pickers, engine behavior, and UI Automation remain unverified.

**Questions to consider.** Can every selection command operate consistently from the row, toolbar, and inspector? Can Add fit all routine file-choice controls while retaining readable filenames?

**Fix verification.** All four Priority Issues were corrected in one focused pass, preserving the shell and existing command owners. Errors → Added → Tab → Enter now selects OpenStreetMap and opens its populated inspector. Enter on Status retains header focus in both sort directions; closing the inspector returns focus to the selected Ubuntu row. Ctrl+P and Ctrl+S work from the toolbar and inspector; Ctrl+M and Ctrl+R invoke the existing commands, row shortcuts still work, and Ctrl+S in the search input leaves the selection unchanged. Add torrent file → Open → Sample folder tree at an actual 1280×720 viewport now has a 760px file viewport and 760px content width; Leave unchanged and its arrow fit completely. At 800×720 the constrained dialog retains horizontal scrolling. Folder Leave unchanged preserves child priorities, and the shared Files tree remains available. JavaScript parsing and the final scoped layout detector passed; the detector returned no findings. No generated output directories were found outside artifacts. The minor tooltip exception was outside this approved pass.
