# Stage 16b gate record: capture journeys use `CaptureMatrix`

Gate done on 12 November 2026, by reading, on the working tree that holds
stages 1 to 3. Nothing was built or run. This record takes the place of
[the stage's text](../22-one-owner-per-rule.md#16-test-and-diagnostic-harness)
for the session that implements it. Paths without a prefix are under
`app/src/Shell/MainWindow`; line numbers are those of that working tree.
These files compile only in the capture build.

## Where the stage stands

Every loop the stage names goes to one owner, with one decision: the loop that
runs while the engine is disconnected takes only the owner's sizes, because it
cannot select a theme or a language through Settings. No journey changes what
it captures, the names of its images or what it checks, except as listed under
[What changes in a review](#what-changes-in-a-review).

## Gate step 1: the findings

The stage marks no finding "to re-read". The gate read each loop; each
implements one rule: "the review captures each of English and Spanish, in
Light and Dark, at 720x560, 1040x680 and 1280x800 DIPs, each size resized in
physical pixels and never below the window's minimum width."

| Loop | What differs from `CaptureMatrix` today |
| --- | --- |
| `Capture.cs` 1387, Schedule | Nothing; prefix `schedule-` |
| `Capture.cs` 1538, Desktop | Nothing; prefix `desktop-` |
| `Capture.cs` 1749, Desktop while disconnected | One language (Spanish, chosen before the engine restarts), the theme set on the window root (`Root.RequestedTheme`), theme outside size |
| `Capture.cs` 2271, populated trackers | Selects the theme before the language; rounds the physical size (`Math.Round`); its outcome names the language and theme |
| `Capture.cs` 2456, SettingsLayout | Selects the language inside the theme loop; reads the language and theme in its conditions |
| `Capture.cs` 2837, the layout modes | Its languages depend on the mode (both for AddLayout and DetailsFiles, else the current one); no theme in Smoke; image names leave out the language outside AddLayout and DetailsFiles |
| `CaptureFiles.cs` 79, `Matrix` | Nothing; prefix `files-` |
| `CaptureSearch.cs` 53, `Matrix` | Theme before language; prefix `search-` |
| `CaptureTraffic.cs` 51 | Theme before language; prefix `traffic-` |

Every copy also lacks the layout wait before and after the resize that
`CaptureMatrix` has (`CaptureLibrary.cs` 498, 512).

Resizes of one size, and the matrices of other sizes, stay: the Shell matrix
(`Capture.cs` 923, widths 1280, 1040 and 720 at 680 DIPs), the settings
prototype (`CaptureSettings.cs` 95 to 107), the peers' portrait and landscape
(`CapturePeers.cs` 1317) and the Library files widths (`CaptureLibraryFiles.cs`).
Each is another set of sizes chosen for its own question.

## Gate step 2: questions the stage leaves open

1. **Whether the order of selection matters.** No. Every loop visits the same
   four language and theme pairs in the same order, English Light first;
   selecting the theme first only changes which setting is saved first, and
   each loop waits for both before it captures.
2. **Whether the disconnected loop can use the matrix.** Not whole. Settings
   cannot save a theme or a language while the engine is disconnected
   (`Capture.cs` 1736 waits for the disconnect first), so the loop sets the
   window root's theme and keeps Spanish. It takes the sizes and the resize
   rule from the owner.
3. **How a journey reads the language and theme.** From the model, which the
   matrix set and waited for: `Model.Text.Language` and `Model.Theme`, the
   theme's setting word.
4. **Smoke mode.** The layout loop runs no theme in Smoke today, so it captures
   and selects nothing there. The journey keeps that by not calling the matrix
   in Smoke; the matrix would otherwise select the current language again.

## Gate step 3: the owner

Two members in `Capture.cs`, beside `CapturePage` and `CaptureDialog`, which
the journeys share. `CaptureMatrix` moves there from `CaptureLibrary.cs`,
whose journey is one of nine users.

| Member | Interface |
| --- | --- |
| `CaptureMatrix(Func<string, SizeInt32, Task> capture, string[]? languages = null)` | For each language (English and Spanish by default) and each of Light and Dark, selects them through Settings and waits, then runs `CaptureSizes`. The callback receives the name part `en-light-720x560` and the size in DIPs, as today. |
| `CaptureSizes(Func<SizeInt32, Task> capture)` | For each of the three sizes: waits for layout, resizes in physical pixels, never below the minimum width, waits for layout, runs the callback. |

Candidates compared:

| Candidate | Result |
| --- | --- |
| Pass a record of language, theme and size to the callback | Rejected. Four callers use only the name part; three read the language or theme, which the model already holds after the matrix waited for it. A record is a new type for that. |
| A theme list parameter for Smoke | Rejected. Smoke runs no theme at all; the journey skips the call. |
| A theme-setting delegate, so the disconnected loop could use the whole matrix | Rejected. One caller, and its language is fixed; the size step is the rule it shares. |

| Caller | Member |
| --- | --- |
| `CaptureLibrary.cs` 198; `Capture.cs` 1857, 1922; `CapturePieces.cs` 28 | `CaptureMatrix`, unchanged |
| `Capture.cs` 1387, 1538, 2271, 2456; `CaptureFiles.cs` 79; `CaptureSearch.cs` 53; `CaptureTraffic.cs` 51 | `CaptureMatrix` |
| `Capture.cs` 2837 | `CaptureMatrix(…, languages)`, not called in Smoke |
| `Capture.cs` 1749 | `CaptureSizes`, once per theme |

## Gate step 4: consumers of changed names

`CaptureMatrix` keeps its name and callback; its four callers do not change.
`CaptureSizes` is new. No product code, XAML or test script names either:
`app/tests/Capture.ps1` reads image files by name, and the names stay.

## Files the change may edit

`Capture.cs`, `CaptureLibrary.cs`, `CaptureFiles.cs`, `CaptureSearch.cs`,
`CaptureTraffic.cs`.

## What changes in a review

- Every journey waits for layout before and after each resize, as the matrix
  already does. A capture can only become steadier.
- The populated trackers journey resizes by truncation, as every other
  journey does, not by rounding: at a scale whose product has a fraction, the
  window is at most one physical pixel smaller.
- Journeys that selected the theme before the language now select the
  language first. The captured pairs and their order stay.

## Cost

No build in this session; the stage joins batch A's one app build with
`-Capture`, which compiles these files. No check script runs these journeys.
The capture modes that run them are Schedule, Desktop, Details, SettingsLayout,
AddLayout, DetailsFiles, the full layout mode, Files, Search and Traffic; no
capture review is needed for the stage, because it moves no product code. About
one hour of agent work.
