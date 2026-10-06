---
target: Settings page (PreferencesForm.xaml)
total_score: 19
max_score: 40
na_heuristics: 
p0_count: 1
p1_count: 3
target_identity: "file:C:\\SynoSoftware\\TinyTorrent2\\app\\src\\PreferencesForm.xaml"
target_fingerprint: "sha256:e711add87b002242e1638061c5fec6c1b454b3691f5ba292fcff826b670413bf"
target_path: "C:\\SynoSoftware\\TinyTorrent2\\app\\src\\PreferencesForm.xaml"
timestamp: 2026-10-05T23-16-03Z
slug: app-src-preferencesform-xaml
---
Method: dual-agent (A: design review · B: detector and capture measurements)

## Design Health Score (before changes): 19/40, Poor

| # | Heuristic | Score | Key issue |
|---|---|---|---|
| 1 | Visibility of system status | 2 | Each toggle disabled itself, opened an "Applying…" InfoBar and showed a header ProgressRing |
| 2 | Match with the real world | 2 | "Listen port", "UPnP / NAT-PMP", "0" meaning unlimited explained once |
| 3 | User control and freedom | 3 | Immediate apply, Escape cancels |
| 4 | Consistency and standards | 1 | Three layouts in five tabs; column width changed per tab (23%-86% of window) |
| 5 | Error prevention | 2 | Ratio 0 read as "stop now" |
| 6 | Recognition rather than recall | 2 | Units and the unlimited rule shown once above four fields |
| 7 | Flexibility and efficiency | 3 | Search jumps to fields; Enter commits |
| 8 | Aesthetic and minimalist design | 1 | Bare controls on the backdrop, 480 px number boxes, blank line under Default app |
| 9 | Error recovery | 2 | Good wording, but heavy InfoBars that move the page |
| 10 | Help and documentation | 1 | No descriptions |

## Priority issues
- [P0] No settings surface; layout width came from content (Grid MaxWidth 880, HorizontalAlignment Left).
- [P1] Pending feedback disabled the focused control and shifted the page three ways.
- [P1] "0 means unlimited" shown once; no descriptions.
- [P1] Paused schedule segments: white on caution yellow in dark theme (about 1.3:1).
- [P2] Options in the wrong place: startup switches under Appearance; empty DefaultsMessage line; closed InfoBars adding 24-96 px dead space per tab.

## Detector
0 findings in PreferencesForm.xaml and MainWindow.xaml; the detector matches web CSS/HTML text only, so it cannot judge XAML.
