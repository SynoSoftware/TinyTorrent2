---
target: settings page
total_score: 28
max_score: 40
na_heuristics: 
p0_count: 0
p1_count: 1
target_identity: "file:C:\\SynoSoftware\\TinyTorrent2\\app\\src\\Views\\PreferencesForm.xaml"
target_fingerprint: "sha256:1aebfec687462ddec17d0d75e709c9b94bff2ad46d5c2fb16b6682496bbe880c"
target_path: "C:\\SynoSoftware\\TinyTorrent2\\app\\src\\Views\\PreferencesForm.xaml"
timestamp: 2026-10-07T02-16-55Z
slug: app-src-views-preferencesform-xaml
---
Method: dual-agent (A: /root/design_review · B: /root/detector_evidence)

Settings-page critique — TinyTorrent2

Requested by the user for this chat. This is a critique handoff, not authorization to broaden ongoing implementation. Preserve existing owner changes and settled immediate-save/category decisions.

Target: C:/SynoSoftware/TinyTorrent2/app/src/Views/PreferencesForm.xaml. Current source inspected with HEAD b244ee8 and concurrent local changes. Mode: Operate.

Design verdict
The page has a sound native Windows foundation: familiar categories, task-based sections, and direct controls. The best next step is a focused correction of feedback and text ownership. A replacement layout is not justified.

Design health: 28/40 — Good (provisional source/capture assessment)
| Heuristic | Score /4 | Main evidence |
|---|---:|---|
| System status | 3 | Selected categories and field errors are clear; startup error placement is weak. |
| Match with real world | 3 | Familiar desktop/torrent vocabulary. |
| User control and freedom | 3 | Back, Escape and immediate settings commits. |
| Consistency and standards | 3 | Native controls; presentation diverges from current owner rules. |
| Error prevention | 3 | Port/numeric validation and preserved rejected input. |
| Recognition rather than recall | 3 | Visible labels and task categories. |
| Flexibility and efficiency | 3 | Enter/Escape and direct navigation to settings. |
| Aesthetic and minimalist design | 2 | Repeated explanations and decorative gears compete with values. |
| Error recovery | 2 | Useful field validation; registration feedback can be remote. |
| Help and documentation | 3 | Relevant help exists, with placement/ownership problems. |

All ten apply. These scores are design judgments, not runtime conformance results.

What works
- Five recognizable categories and native controls support scanning without another navigation system. Five categories alone is not a cognitive-load defect.
- Ordinary settings apply individually. Escape cancels unfinished field input; refused values retain a correction route. Keep the owner's no-page-wide-Save/no-success-indicator behavior.
- Notification importance order, dependent seeding-sleep disclosure, accessible control names and section headings are appropriate.

Priority issues

1. [P1] Startup failures appear under Default app.
Evidence: app/src/Views/Preferences.cs:217–239 routes sign-in changes and the Windows Startup settings link through the shared registration operation/error. The only RegistrationMessage presentation is inside DefaultsSection at app/src/Views/PreferencesForm.xaml:190. Startup's rows at :164–179 have no corresponding error binding.
Impact: a failed sign-in change can look ignored, while its explanation appears in a different section, potentially below the viewport.
Recommendation: retain one registration operation owner and associate its error with the initiating action. Do not duplicate registration implementations.
Suggested command: $impeccable harden.
Confidence: source-confirmed presentation/data-flow issue; failure not exercised live in this review.

2. [P2] Permanent explanatory text contradicts the current work-surface ruling.
Evidence: PreferencesForm.xaml:56 and :77–78 render section/row descriptions; PreferencesForm.xaml.cs:71–95 populates them. Current Network captures visibly repeat label meanings, such as explaining that other peers use the incoming port.
Impact: explanations add reading and vertical height before users reach subsequent controls.
Recommendation: keep labels, values, states and errors visible; move explanations to keyboard-accessible tooltips/help. Preserve necessary semantics such as units and unlimited values in the appropriate label/help owner.
Suggested command: $impeccable distill.
Authority: docs/interface.md:143–160. Its explicit owner ruling governs over the older description recipe at :744–747. That documentation conflict should be reconciled without reopening the ruling.
Confidence: source-confirmed across categories and visually confirmed in current Network captures.

3. [P2] SettingsRow overwrites notification accessibility help.
Evidence: app/src/Controls/SettingsRow.cs:61 unconditionally sets the child control's AutomationProperties.HelpText from Description/Caution. The three notification rows supply neither, but their toggles explicitly bind HelpText at PreferencesForm.xaml:150, :155 and :160. Row updates can replace that help with an empty string.
Impact: pointer users can receive the tooltip explaining that Windows notifications appear while the window is closed, while assistive-technology users may lose the same context.
Recommendation: establish one owner for contextual help and preserve the complete keyboard/screen-reader route.
Suggested command: $impeccable harden.
Confidence: source-confirmed competing assignments; Narrator behavior remains unverified.

Accepted behavior and lower-priority observations
- Port validation grows its row by 21 pixels and moves following rows by 21 pixels. This is measured, but NOT counted as a new priority defect: docs/morning-report.md:50–71 and the Settings validation checkpoint in docs/handover.md explicitly accept the same capture matrix as readable feedback with retained focus/input, and retain no product change. The general no-movement text and this specific disposition are inconsistent. Do not reopen accepted Settings design solely on this observation.
- Wrapping settings labels/messages conflicts with the current single-line owner ruling. Review it with issue 2; no new minimum-width clipping defect is established.
- Large corner gears add generic decoration, including behind the heading in narrower captures. Removing them is optional polish, not a prerequisite or an approved redesign.
- Categories scroll with the page, and Browse uses text-only presentation. These are observations for a later focused pass, not demonstrated task blockers.

Cognitive load and emotional journey
Two weak areas: unnecessary explanatory reading and remembering which action caused remotely placed feedback. Task grouping and progressive disclosure are otherwise effective. Entry feels familiar; ordinary changes are quick; startup failure is the main confidence gap. A validation message itself is useful and accepted.

Persona checks
- Power user: preserve immediate edits and direct field navigation; remove unnecessary reading.
- First-time user: keep failure explanations beside the action; preserve optional explanations when removing inline prose.
- Keyboard/screen-reader user: resolve HelpText ownership; physical navigation and announcements still need native verification.

Evidence: current source and representative native Network captures in artifacts/evidence/UiSelfCapture-ca4f1446-b968-474e-84d8-84995a700d27/captures; source-led findings are explicitly marked. Detector returned zero findings with unspecified XAML coverage. No live runtime review was performed.

Method: dual-agent extension (A: /root/design_review · B: /root/detector_evidence)

Settings critique addendum — every tab

The user clarified “every tab.” This completes explicit coverage of General, Transfers, Network, Schedule, and Appearance. It supplements the earlier report; the three actionable priorities remain unchanged. No product source was edited.

| Tab | What works | Critique and recommendation |
|---|---|---|
| General | Downloads, Notifications, Startup, Default app, and Power have recognizable task groups. Notification order emphasizes problems, and seeding sleep is disclosed only when relevant. | Highest priority: place startup-operation errors beside Startup, not inside Default app; stop SettingsRow overwriting notification HelpText. Remove explanatory prose through the approved tooltip/help path. Browse’s text-only presentation is a smaller consistency observation. |
| Transfers | Four understandable groups with two inputs each: normal speeds, alternative speeds, queue counts, seeding limits. Units remain visible. | The long alternative-limits explanation burdens scanning and wraps in the Spanish capture. Keep the grouping and native numeric fields. Move explanations to accessible help without losing unlimited-value semantics or the consequence of reaching seeding limits. No additional input/commit defect established. |
| Network | Four focused controls and a native adapter selector. The latest validation matrix demonstrates readable correction with rejected input and focus retained. | Explanations repeat the port/mapping/adapter labels and add vertical reading. Apply the shared text correction. The accepted 21-pixel error expansion is not a new defect; do not redesign these rows merely to reverse the documented disposition. |
| Schedule | Strongest product-specific surface: one combined week, exact selected-period details, one saved-period expander, and an explicit coherent editor. Keyboard routes and adaptive time fields exist. | Highest text density: section explanation, gesture instructions, preview instructions, status, legends and editor compete. Move instructions to accessible help, preserve actual schedule state/selection/unsaved-preview facts. Review wrapping and Caption-sized operational facts against the shared single-line/body-text policy. Keep the timeline, exact editor and saved-period escape route. No newly demonstrated scheduling behavior defect. |
| Appearance | Two labeled native comboboxes make language and theme simple. Follow Windows/Light/Dark is sufficient. | Its description restates the controls. Remove/move that shared explanatory text; otherwise preserve the short page. Empty space is appropriate for two settings and does not require new options or decoration. No tab-specific functional defect found. |

Source locations
- General: app/src/Views/PreferencesForm.xaml:127–208; registration error :190 versus Startup :164–179; app/src/Views/Preferences.cs:217–239; app/src/Controls/SettingsRow.cs:59–61.
- Transfers: PreferencesForm.xaml:210–266; app/src/Resources/en.json:136,138,142.
- Network: PreferencesForm.xaml:268–297; PreferencesForm.xaml.cs:86–90.
- Schedule: app/src/Controls/Scheduler.xaml:28–38,57,80 for text presentation; :58–94 for editor; :95–106 for saved-period access. Scheduler.cs handles adaptive time layout and Escape; app/src/Controls/Week.cs:362–391 supplies keyboard routes. Seven day choices are a natural week, not a generic “too many options” defect.
- Appearance: PreferencesForm.xaml:304–325; PreferencesForm.xaml.cs:95–105.
- Shared text authority: docs/interface.md:143–160 and :193–197. Older description recipes conflict with these owner rulings.

Evidence boundaries
Both independent passes inspected source for all five tabs and representative existing native captures. Parent also viewed representative images for every tab. This is not a fresh all-tabs runtime certification.
- General: UiSelfCapture-2b863117-f4fb-41ce-a6de-22a156b58197, 22:22 UTC captures show current separate Notifications grouping but predate later outer-template edits.
- Transfers: UiSelfCapture-aee5a18a-1456-40c8-a79f-c87d8a498401 and older f6c1eec4 captures support the enduring composition only.
- Network: UiSelfCapture-ca4f1446-b968-474e-84d8-84995a700d27 is the latest matching validation evidence. Use it over older screenshots.
- Schedule: UiSelfCapture-e2d0ce8c-c2ff-4ca2-8bad-d1a2bc3484c2 captures follow Scheduler's local edits, but predate later shared/outer-template styling.
- Appearance: UiSelfCapture-464e3040-2e1a-450e-9d94-fadc20e79d07 plus older composition evidence. Do not infer current pixel metrics from those images.
All evidence is under C:/SynoSoftware/TinyTorrent2/artifacts/evidence. Capture timestamps above are recorded UTC identifiers, not user-local dates.

The existing Preferences detector result was reused; one supplemental Scheduler.xaml scan also returned exit 0 / []. Neither reports native-XAML coverage, so neither certifies accessibility or layout. No browser overlay applies to this native surface. No launches, builds, tests, or temporary servers. Current physical keyboard, Narrator, High Contrast, text scaling, and registration-failure journeys remain unverified.

Overall score remains the provisional 28/40 from the same critique, not a new measured trend. No additional defect was manufactured to make the five tabs appear equally problematic.

Follow-up choices, if the owner wants implementation:
1. Feedback/accessibility first, or shared text presentation across all five tabs first?
2. Three substantive corrections only, or those plus optional gear/button polish?
