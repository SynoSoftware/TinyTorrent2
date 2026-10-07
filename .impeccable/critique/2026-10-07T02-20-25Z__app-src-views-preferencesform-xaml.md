---
target: Every Settings tab, app consistency and Fluent 2
total_score: 28
max_score: 40
na_heuristics: 
p0_count: 0
p1_count: 0
target_identity: "file:C:\\SynoSoftware\\TinyTorrent2\\app\\src\\Views\\PreferencesForm.xaml"
target_fingerprint: "sha256:2477833a07e0f7babbd75ebb5cf7e0305dead6989d562b3f661500e5bfb2d2b2"
target_path: "C:\\SynoSoftware\\TinyTorrent2\\app\\src\\Views\\PreferencesForm.xaml"
timestamp: 2026-10-07T02-20-25Z
slug: app-src-views-preferencesform-xaml
---
Method: dual-agent (A: /root/settings_design · B: /root/settings_evidence)

Expanded Settings critique — all five tabs and consistency with the app

The previous audit did not critique every tab. This expanded assessment reviews every tab's current source and available saved captures, and compares Settings with Add and the inspector. Every tab is not yet in its best shape. The app has a coherent native foundation; its command presentation and information treatment are not fully consistent.

| Tab | Assessment |
|---|---|
| General | Clear task groups and useful Windows integration, but too much explanatory prose under the latest owner rules. Browse is separated from the path rather than sharing the established Add-form composition. |
| Transfers | Good organization: speed, alternative limits, queue and seeding. Units and numeric alignment work. Presentation still has wrapping explanations and smaller captions; preserve essential zero/unlimited meaning in labels or accessible tooltips when fixing that. |
| Network | Compact, coherent group with useful specific validation. Recent captures prove an invalid port pushes the following three controls down by 21 px. |
| Schedule | Strong functional design: combined week, exact native time fields, saved periods and keyboard alternatives. Weakest command consistency: plain multiword buttons, an accented Add action, Cancel before Save, and no corresponding default Enter commit. |
| Appearance | Closest to finished. Two native selections use the existing language/theme owners. The extra explanatory heading and large gear decoration give a sparse page unnecessary visual weight. |

Design specificity: authored for a small native torrent utility, with sensible product grouping. The native controls and shared semantic resources are appropriate. The oversized decorative gears are generic Settings decoration and add attention cost without helping the person's task.

| Nielsen heuristic | Score /4 | Main observation |
|---|---:|---|
| Visibility of status | 3 | Useful adjacent errors; layout shifts on validation |
| Match with real-world language | 3 | Task-oriented labels and useful units |
| User control and freedom | 3 | Immediate independent choices, cancellable coherent drafts |
| Consistency and standards | 2 | Schedule command grammar and typography differ |
| Error prevention | 3 | Validation and draft protection |
| Recognition rather than recall | 3 | Familiar categories; navigation scrolls away on long pages |
| Flexibility and efficiency | 3 | Search/deep links and keyboard schedule routes |
| Aesthetic and minimalist design | 2 | Explanatory prose and large background gears |
| Error diagnosis and recovery | 3 | Specific error messages; announcement behavior unverified |
| Help and documentation | 3 | Guidance exists but conflicts with its current product placement policy |
| Total | 28/40 | Good foundation; concrete work remains |

Priority findings:
1. [P2] Settings retains a separate explanatory text treatment. PreferencesForm.xaml:20–31,56,76–80 and Scheduler.xaml:28,32,57,80 use wrapping guidance and Caption text. Current docs/interface.md forbids help prose/wrapping on work surfaces and specifies Body roles. Move explanatory guidance to focus-accessible tooltips while retaining labels, values, states, errors and essential numeric meaning. This is a settled project-policy mismatch. Microsoft explicitly allows optional descriptions in settings cards; do not call descriptions intrinsically a Fluent violation. Suggested command: $impeccable clarify.

2. [P2] Schedule commands do not share the app's command grammar. Scheduler.xaml:26,85–87,107 and Scheduler.cs:104–109 use plain buttons such as Add period/Save period, accent Add even though it opens an editor, and put Cancel before Save. InspectorForm.xaml:95 uses shared ActionButton with Save before Cancel. Use the shared command controls and tooltip/accessible naming, neutral Add, the established action ordering and a clear default Save keyboard behavior. The accent should carry the meaning promised by the product contract. Suggested command: $impeccable polish.

3. [P2] The same destination operation has two compositions. PreferencesForm.xaml:130–137 places Browse next to the heading with the path below; AddForm.xaml:42–47 places Browse beside the path through the shared command treatment. Use the established path-and-Browse row so the control relationship and command appearance are recognizable. Suggested command: $impeccable layout.

4. [P2] Validation destabilizes the form. PreferencesForm.xaml:79 and SettingsRow.cs:57 collapse an empty error inside an autosized stack. Current Network captures show PortRow 37→58 px and MappingRow/InterfaceRow/ConnectionsRow all move +21 px, returning to baseline after correction. Resolve feedback at the shared row owner using existing row space; do not reserve blank lines, which conflicts with the separate owner ruling. Suggested command: $impeccable layout.

5. [P2] Oversized background gears weaken the app's quiet visual identity. PreferencesForm.xaml:11–17,33–39,92–95 draws 320 px cogs behind title/navigation and card edges; Add and Inspector have no corresponding motif. Remove them and retain the useful category/section icons. This is a design judgment grounded in Fluent's focus principle and the product's explicit attention-cost rule, not a Microsoft prohibition on illustration. Suggested command: $impeccable distill.

What works: native controls and state visuals; semantic light/dark/high-contrast resources; common title/body/icon owners; one stable centered column; immediate independent settings; dependent field visibility; Schedule's explicit coherent editor and accessible alternatives to dragging. Different density between the Settings page and torrent inspector is intentional in the contract and need not be made identical.

Fluent judgment: broadly aligned, not verified as fully conforming. Microsoft's WinUI settings guidance supports immediate changes, full-page scrolling, BodyStrong sections, right-aligned native setting controls and a theme ComboBox. The current app follows much of that. Fluent also prioritizes familiar behavior, adaptive layout and lower visual noise. Project-specific rules (one-word icon buttons, no visible help prose, no wrapping) are stricter local decisions and must be reported separately.
Sources: https://learn.microsoft.com/en-us/windows/apps/design/app-settings/guidelines-for-app-settings ; https://fluent2.microsoft.design/design-principles ; https://fluent2.microsoft.design/layout

Cognitive load/personas: Appearance is lowest, General/Schedule highest. First-time users need units, unlimited meaning and overnight consequences preserved as the page is simplified. Power users need predictable Save/default behavior. Keyboard and screen-reader users need actual focus, tooltip-on-focus and announcement verification.

Earlier audit corrections: current SettingsRow now includes Error, Description, Caution and a dedicated HelpText in the accessible description and applies it to both Content and Detail. Notification hints are now owned by the row. The earlier two accessibility findings are resolved in source; do not repeat them as current defects.

Run notes: target slug app-src-views-preferencesform-xaml; ignore list absent. Assessments independent. Detector ran once and returned [], 0 findings; it does not validate WinUI. No browser/overlay/server applies to this native target. Current General, Network and Appearance captures exist; all-tab captures exist but Transfers/Schedule include historical geometry. Saved captures contain no source fingerprint, exclude native chrome/desktop acrylic, and sometimes nominal720 is actually837/863. No current complete five-tab matrix, high contrast, increased text scale or Narrator proof. No app launch, builds or tests. No product code edited by this critique.
Questions skipped: the user requested an assessment, and the remaining priorities are concrete.
