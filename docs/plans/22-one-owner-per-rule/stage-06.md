# Stage 6 gate record: custom-drawn controls

Gate done on 12 November 2026, by reading, on the working tree that holds
stages 1, 2, 3 and 16b. Nothing was built or run. This record takes the place
of [the stage's text](../22-one-owner-per-rule.md#6-custom-drawn-controls) for
the session that implements it. Paths without a prefix are under `app/src`;
line numbers are those of that working tree. Stages 4 and 5 are implemented
before this stage and edit some of the same files (`PeerMap/Navigation.cs`,
`PiecesMap.xaml.cs`, `SpeedGraph.xaml.cs`, `Week.cs`), so the implementer finds
each site by the member this record names, not by its line.

## Where the stage stands

Every item has an owner, an interface and its callers. Ten findings are kept
and one is dropped: "check then set a folder" in Move. The stage adds
three files in `Controls`: `Colour.cs`, `ValuePeer.cs` and `DragDistance.cs`.
It adds no product text, so no catalogue changes. Per-frame paths of the map
do no more work than today (decision 10). A person sees a change only in when
a drag starts: the week and the map follow the Windows drag distance, and a
diagonal pan of the map starts a little later (decisions 4 and 5).

## Gate step 1: the findings

| Finding | Rule | Result |
| --- | --- | --- |
| Brush-to-colour read, mix and alpha (to re-read) | A custom-drawn control paints a theme brush as XAML paints it, its colour at its opacity, and a mixed or faded colour rounds each channel. | Kept. Today the copies differ. `PiecesMap.Read` (`Torrents/PiecesMap.xaml.cs` 517) counts opacity and rounds. `PeerMap.Colour` (`PeerMap.xaml.cs` 590) and the four reads in `Speedometer.Draw` (`Settings/Speedometer.xaml.cs` 150, 151, 171, 192) ignore opacity. `PiecesMap.Mix` (507) and `MapRenderer.Blend` (`MapRenderer.cs` 974) truncate. `MapRenderer.Alpha` (971) truncates, and `Speedometer.Draw` scales alpha by hand and truncates (161, 199). The gate found a sixth copy: `PeerMap.Underlay` (609) reads a brush at its opacity through `MapRenderer.Alpha`. |
| Windows lets this animate (to re-read) | A control animates only while Windows animations are on; the peer map and the speedometer also stay still in High Contrast. | Kept. `Motion` (`Controls/Motion.cs` 12, read at 56, 82, 115), `SettingsExpander` (11, 75) and `Speedometer` (22, read at 55 and 121) each make a `UISettings` per instance; `MapRenderer` (39, 132) keeps a static one. Speedometer states its High Contrast condition twice (55, 121). |
| Read-only value automation peer | A custom-drawn control whose value a screen reader reads, and the person cannot type, offers the Value pattern with that text. | Kept. `PiecesMap.MapPeer` (732) and `SpeedGraph.GraphPeer` (`SpeedGraph.xaml.cs` 779) differ only in the class name and the value. The gate found the second half of the rule copied too: both raise the Value property's change the same way (`PiecesMap.ShowSelection` 703 to 705, `SpeedGraph.Announce` 579 to 581). |
| The visible span | The part of a picture the map shows is the one world copy around the view's centre. | Kept. `MapMotion.OnDraw` (1097 to 1099) computes `MapView.Bounds` (`MapViewport.cs` 160 to 168) again from the picture's view. The arithmetic and types are the same. |
| The 33 ms frame | The map's motion is paced in 33 ms frames: the render worker waits at least one frame between pictures, and the game loop stays awake through a wait of up to two. | Kept. `MapMotion.Frame` (29, read at 1018) and `MapRenderer.Frame` (129, read in `Delay` at 148). |
| The meridian (to re-read) | The map centres on the origin's longitude, or on 0° while the origin is unknown. | Kept. `MapModel.Meridian` (27), `Presentation.Meridian` (183), `Arrangement.Meridian` (312). The first has no reader in product code, capture journeys or `app/tests`; the presentation and the arrangement each compute it from the same point, because `Arrangement` builds the presentation from its `OriginPoint` (513). |
| Point-to-segment distance (to re-read) | A point's distance to a segment is its distance to the segment's nearest point. | Kept. `Error` inside the arc tracing (`MapRenderer.cs` 842 to 849) and `Inspection.HitTest` (1876 to 1880). Both compare squared distances. |
| The 10-second hold (to re-read) | A smaller dot size applies only after ten seconds of lower traffic. | Kept. `MapDots.Advance` (128), `Wait` (173), `Settle` (215). |
| The zoom step (to re-read) | One zoom step scales the view by 1.25: a wheel notch, a double-click or Plus; Minus scales by 1 / 1.25. | Kept. `PeerMap.OnWheel` (`PeerMap/Navigation.cs` 96), `OnDoubleTapped` (106), `OnKey` (265). The [peer location contract](../../contracts/peer-location.md#zoom-and-pan) states it. The gate found a fourth copy: the capture journey's wheel replay (`Shell/MainWindow/CapturePerformance.cs` 498). |
| Add: preview and apply (to re-read) | A preview the engine made for a source is applied only while the source is still wanted; otherwise the draft releases it, so the engine holds no preview that no source owns. | Kept. `AddDraft.AcceptMagnet` (426 to 434) and `AddDraft.Acquire` (611 to 619) run the same five steps and differ only in what "still wanted" means. |
| Move: check then set a folder (to re-read) | — | **Dropped.** The shared part is one `check_destination` send (`Torrents/MoveDraft.cs` 123; `Shell/MainViewModel/Actions.cs` 257). After it, the Move dialog stops when its selection changed and also checks before a move, and recovery acts only on the torrents that are still recoverable. An owner would forward one call or take each caller's rule as a callback. The engine's `move` and `redownload` refuse only a relative path at once (`engine/src/Engine/Move.cpp` 77 to 81) and meet an unusable folder later, as a problem (295 to 304 for a download), so the check itself stays at both callers. |

Copies that look alike and stay:

- `MapRenderer.Highlight.Colour` (242 to 248) dims a destination to grey at a
  quarter of its alpha. That is the dimming rule, and it already rounds. It
  runs for every dot in every frame of the game loop (`MapMotion.DrawDots`
  1170), where a call to the colour owner would add a clamp.
- The speedometer's 33 ms timer (`Speedometer.xaml.cs` 52) paces its own
  needle. It does not depend on the map's frame.
- The wheel notch of 120 (`SpeedGraph.Notch`, `PeerMap.OnWheel`) is Windows'
  `WHEEL_DELTA`, a platform fact written where it is used.
- `CaptureSettings.cs` 725 and `Capture.cs` 340 read the Windows animation
  and contrast settings into a capture report. They decide nothing.
- `CapturePeers.cs` 1606 and `CaptureComposition.cs` 384 step through zoom
  levels they choose for a measurement; they do not replay an input.
- `Controls/Splitter.xaml.cs` (range value) and `Settings/Week.cs` 763 (no
  pattern) are other peers. `SettingsExpander`'s 260 and 220 ms drawer is the
  Settings drawer motion, which [Settings](../../contracts/ui/settings.md)
  owns apart from `Motion`'s timing.
- The peer map's own frame queue (`PeerMap.QueueFrame`), as the plan says.

## Gate step 2: questions reading answered

1. **Where the owners live.** In `Controls`. The colours, the animation answer
   and the drag distance are read by `Torrents` and `Settings`, and the
   [direction rule](../22-one-owner-per-rule.md#direction-between-the-app-areas)
   puts a type that two features need in the base. The value peer has two
   `Torrents` callers but is framework mechanics, which
   [naming](../../agents/naming.md#app-folders) places in `Controls`.
2. **Whether "a brush's opacity counts" changes a colour today.** No. Every
   brush these controls read is a theme brush or an `App.xaml` brush with
   opacity 1: `Speedometer.xaml` 25 to 28, `PeerMap.xaml` 38 to 42,
   `App.xaml` 27, 76, 130, and the Pieces map swatches, whose read already
   counts opacity. Rounding moves a channel by at most one level.
3. **Whether one static `UISettings` serves every control.** Yes.
   `MapRenderer` already reads its static one (39, 132) from the UI thread, and
   `AnimationsEnabled` is read at each call, so a change in Windows applies at
   the next read, as today.
4. **What Windows does with a drag distance.** Windows keeps it as
   `SM_CXDRAG` and `SM_CYDRAG`, 4 pixels at 96 DPI by default, and a person
   can raise it for steadier clicks. The table library reads both
   (`lib/TableView/src/DragDistance.cs`) and starts a row drag outside that
   rectangle (`Table/Input.cs` 315). Week and the peer map write 4 by hand.
5. **Which map paths run every frame.** On the game loop: `MapMotion.OnDraw`,
   `DrawDots` and `MapDots.Advance`, with `Wait` when the loop may sleep. The
   render worker draws a picture when the view or the content needs one, at
   every step of a zoom. `PeerMap.Publish` runs on the UI thread for a changed
   hover, choice, setting or preparation, not per frame and not per pan move.
6. **What the value peer's class name becomes.** `Owner.GetType().Name` gives
   `PiecesMap` and `SpeedGraph`, the names `nameof` gives today. No capture
   journey or check script reads either peer (searched for the class names,
   `IValueProvider` and `ValuePattern`).

## Gate step 3: owners, interfaces and callers

Candidates compared with `/codebase-design`:

| Concern | Candidate | Result |
| --- | --- | --- |
| Colours | `MapRenderer.Alpha` and a public `Blend` stay the owners | Rejected. `Speedometer` would name `Torrents`, which the direction rule forbids. |
| Colours | Extension methods on `Color` and `Brush` | Rejected. The app has no extension methods; a first one is a new convention for three functions. |
| Colours | A static class in `Controls` with `Read`, `Alpha` and `Mix` | **Chosen.** The app's shared technical owners are static classes (`Svg`, `TextEditor`, `Trimming`). |
| Animation answer | A new type in `Controls` | Rejected. `Motion` is already the [one motion owner](../../contracts/ui.md#windows-layout-and-themes) and reads the setting three times. |
| Animation answer | A static member on `Motion` | **Chosen.** |
| Value peer | A shared interface the two controls implement | Rejected. [Naming](../../agents/naming.md#interfaces) adds an interface only at an external boundary or between real implementations. |
| Value peer | One sealed peer that takes the value as a delegate | **Chosen.** WinUI keeps one peer per element, so the delegate is made once per control. |
| Drag distance | A 4-DIP constant in `Controls` | Rejected. The tables beside the map and the week follow Windows; a constant gives a person two answers when they raise the Windows value. |
| Drag distance | One owner with the colours, as stage 1's decision 8 foresaw | Rejected. Colours and a pointer threshold are two responsibilities; one type for both would be a grab bag. |
| Drag distance | A static class that reads the Windows metric, shaped as the table's | **Chosen.** A reader who knows one finds the other. |

| Owner | Interface | Callers, and the member each calls |
| --- | --- | --- |
| `Colour` (`Controls/Colour.cs`, new) | `internal static Color Read(Brush brush)`: the colour XAML paints with a solid brush, its colour at its opacity. `internal static Color Alpha(Color colour, double opacity)`: alpha times opacity, rounded and kept within 0 to 255, because the map passes opacities above 1. `internal static Color Mix(Color under, Color over, double amount)`: each channel the given share of the way, rounded; an amount of 0 or less returns `under` at once, as `PiecesMap.Mix` does, because most Pieces map pixels mix nothing. | `Read`: `PiecesMap.Draw` palette (252 to 260, six reads); `PeerMap.Publish` palette (493 to 497); `PeerMap.Underlay` 609 as `Read(solid)` after its `is SolidColorBrush` test; `Speedometer.Draw` 150, 151, 171, 192. `Alpha`: `MapRenderer.Halo` 989, `DrawScene` 1203, 1224, 1259, the arc stops 2326; `MapMotion` 1267, 1268, 1269, 1347, 1366; `Speedometer.Draw` 161 (`Alpha(accent, lit)`) and 199 (`Alpha(ink, 0.7)`). `Mix`: `PiecesMap.Render` 430, 435, 445, 447; `MapRenderer.DrawScene` 1192. Deleted: `PiecesMap.Read`, `PiecesMap.Mix`, `PeerMap.Colour`, `MapRenderer.Alpha`, `MapRenderer.Blend`. |
| `Motion` (`Controls/Motion.cs`) | `internal static bool IsEnabled`: Windows animations are on. One private static `UISettings` replaces the instance field `_settings`. | `Motion.Change` 56, `Enter` 82, `Fade` 115; `SettingsExpander.Change` 75 (its `_settings` field deleted); `Speedometer`: a private `bool Animates => Motion.IsEnabled && !_accessibility.HighContrast`, read by the timer's tick (55) and `Show` (121), so High Contrast is stated once; `_settings` stays for the text scale. `MapRenderer.SystemAnimates => Motion.IsEnabled && !SystemAccessibility.HighContrast`; `SystemSettings` deleted. `SystemAnimates` keeps its name and its readers (`PeerMap` 74, 167, 507; `Shell/InspectorPane/Peers.cs` 145; `CapturePeers.cs` 403). |
| `ValuePeer` (`Controls/ValuePeer.cs`, new) | `internal sealed class ValuePeer(FrameworkElement owner, Func<string> read) : FrameworkElementAutomationPeer, IValueProvider`: class name `Owner.GetType().Name`, control type Custom, the Value pattern, `IsReadOnly` true, `Value` from `read`, `SetValue` throws. `internal static void Announce(UIElement owner, string previous, string current)` raises the Value property's change. | `PiecesMap.OnCreateAutomationPeer` → `new ValuePeer(this, () => _value)`; `PiecesMap.ShowSelection` 703 → `Announce`. `SpeedGraph.OnCreateAutomationPeer` → `new ValuePeer(this, () => _value)`; `SpeedGraph.Announce` keeps its `_announced` test and calls `ValuePeer.Announce`. Deleted: `MapPeer`, `GraphPeer`. |
| `DragDistance` (`Controls/DragDistance.cs`, new) | `internal static double Horizontal` and `Vertical`: `GetSystemMetricsForDpi(SM_CXDRAG or SM_CYDRAG, 96)`, read once, in DIPs. Same shape and P/Invoke as `lib/TableView/src/DragDistance.cs`. | `Week.UpdateDrag` 602: `Math.Abs(point.X - drag.Origin.X) >= DragDistance.Horizontal`, sideways only as today. `PeerMap.MoveDrag` 135: `Math.Abs(delta.X) >= DragDistance.Horizontal \|\| Math.Abs(delta.Y) >= DragDistance.Vertical`, the rectangle Windows and the table use. |
| `MapMotion` | `Frame` becomes `internal` | `MapMotion` 1018; `MapRenderer.Delay` 148 reads `MapMotion.Frame`; `MapRenderer.Frame` deleted. |
| `MapView` (`MapViewport.cs`) | `Bounds`, unchanged | `MapMotion.OnDraw`: `var rectangle = source.Bounds;` replaces 1097 to 1099, and `DrawLayer` draws at `(float)rectangle.X` in place of `left`. |
| `MapModel` | `private static float Meridian(GeoPoint? origin)`, carrying the doc comment of line 26 | `Presentation.Meridian => MapModel.Meridian(Origin)`; `Arrangement.Meridian => MapModel.Meridian(OriginPoint)`. The outer property (27), which nothing reads, is deleted. |
| `MapRenderer` | `private static float DistanceSquared(Vector2 point, Vector2 start, Vector2 line)`: the squared distance from `point` to the segment from `start` to `start + line` | `Error` in the arc tracing (842): `DistanceSquared(sample, from, chord)` after its wrap; `Inspection.HitTest` 1876 to 1880: `DistanceSquared(scaled, from, line) > 16`. The callers keep computing `chord` and `line`, so no step is added. |
| `MapDots` | `private const long Hold = 10`, in seconds | `Advance` 128 and `Wait` 173: `since + Hold * Stopwatch.Frequency`; `Settle` 215: `now - since >= Hold * Stopwatch.Frequency`. A constant keeps the code the JIT makes today. |
| `MapViewport` | `internal const float ZoomStep = 1.25f`, beside `MaxZoom`, because the contract states both in one sentence | `PeerMap.OnWheel`: `(float)Math.Pow(MapViewport.ZoomStep, delta / 120.0)`; `OnDoubleTapped`: `ZoomAt(MapViewport.ZoomStep, …)`; `OnKey`: `MapViewport.ZoomStep` or `1 / MapViewport.ZoomStep`, which folds to the same float as `.8f`; capture `CapturePerformance.cs` 498. |
| `AddDraft` | `private async Task<bool> Prepare(AddSource source, Func<bool> wanted)`: previews the source at the current destination, reads its files, releases the preview when `wanted()` is false, else applies it as `ApplyPreview` does; returns whether it applied. `ApplyPreview`, whose only callers these are, is merged into it. | `AcceptMagnet`: `if (!await Prepare(source, () => Magnet == input && (IsOpen \|\| _owner.IsClosing))) return false;`, then clears the magnet as today. `Acquire`: `await Prepare(source, () => Sources.Contains(source));` inside its `try`. `PreviewSource` and `ReadFiles` stay; `ReadFiles` keeps its name, because `app/tests/AddSourceChecks.ps1` 56 calls it. |

## Decisions the plan did not settle

1. **The colour owner is named `Colour`.** `Color` is the struct it works on
   and `Colors` is WinUI's, so neither can be the name. The code already
   spells the concept "colour" in its own identifiers (`Highlight.Colour`,
   `ShowColours`, `_dotColours`).
2. **`Motion` owns the animation answer**, because the UI contract names one
   motion owner. High Contrast stays a condition of the map
   (`MapRenderer.SystemAnimates`) and of the speedometer (`Animates`), as
   the plan settled.
3. **The value peer also announces the change**, so the two halves of the
   Value pattern have one owner.
4. **The drag distance follows Windows.** The plan's rule text and stage 1
   said "4 DIPs". Reading the Windows value gives 4 DIPs at the Windows
   default, and a person who raised it for steadier clicks gets it in the week
   and the map as in the tables. Established Windows desktop applications do
   this, and the table library already does it.
5. **The peer map tests the Windows rectangle**, not its circle of radius 4.
   Stage 1's record said each caller keeps its own measure. Two Windows values
   cannot set one radius, and the peers table beside the map starts its drags
   at the same rectangle. The week keeps its sideways measure, because a
   vertical slip on a period must stay a click.
6. **The frame belongs to `MapMotion`**, which runs the worker loop that waits
   for `Delay` (611) and the game loop.
7. **The zoom step lives in `MapViewport`**, beside `MaxZoom`, and the
   capture's wheel replay reads it, because that replay copies the product's
   wheel rule. The two capture loops that choose their own zoom levels stay.
8. **"Check then set a folder" is dropped** (step 1).
9. **`PeerMap.Publish` keeps reading its five brushes at each call.**
   Counting opacity adds one property read per brush there. A cached palette,
   cleared by `Restyle` as `_underlay` is, would remove the reads, but it is
   correct only if every colour change of those brushes reaches `Restyle`.
   Reading cannot prove that for a change of the Windows accent colour, which
   may change a theme brush's colour without replacing the brush, and today's
   read at each call keeps the map current in that case. `Publish` runs for a
   changed hover, choice, setting or preparation, not per frame and not per
   pan move, so the added reads stay off the paths the owner watches. The
   speedometer reads its four brushes at each draw, as today.
10. **The map's per-frame work does not grow.** `OnDraw` calls `Bounds`, the
    same arithmetic on a struct; `MapDots` reads a constant; `DrawDots` and
    `Highlight.Colour` are not touched. The render worker's border colour
    (`DrawScene` 1224) gains one rounding per border piece per picture, beside
    a Direct2D draw call for that piece. `PeerMap.Publish` gains five property
    reads (decision 9). Nothing allocates.

## Gate step 4: consumers of every changed name

Searched: `app/src` with the capture journeys and `#if CAPTURE` blocks, all
XAML, and `app/tests/*.ps1`.

- `MapRenderer.Alpha` (internal): `MapRenderer` 989, 1203, 1224, 1259, 2326;
  `MapMotion` 1267, 1268, 1269, 1347, 1366; `PeerMap.Underlay` 609. No capture
  journey or script.
- Private members deleted, each used only in its file: `MapRenderer.Blend`,
  `MapRenderer.Frame`, `MapRenderer.SystemSettings`, `PiecesMap.Read`,
  `PiecesMap.Mix`, `PiecesMap.MapPeer`, `PeerMap.Colour`,
  `SpeedGraph.GraphPeer`, `Motion._settings`, `SettingsExpander._settings`,
  `AddDraft.ApplyPreview`, `MapModel.Meridian` (the outer property; it was
  internal and has no reader).
- `MapMotion.Frame` becomes internal; no other reader.
- Names that stay and are reached by reflection: `PeerMap._underlay`
  (`CaptureComposition.cs` 434 reports its alpha, which may move by one
  level), `PeerMap.ZoomAt` (`CapturePeers.cs` 1503, `CapturePerformance.cs`
  498), `PeerMap._motion` and `_viewport` (several journeys),
  `AddDraft.ReadFiles` and `AddSource.Apply` (`AddSourceChecks.ps1` 26, 56),
  `SpeedGraph.Bucket`, `Points`, `Average` and `Extent` (`SpeedChecks.ps1`).
  None changes.
- New names collide with nothing in the files that import `Controls`:
  `Colour`, `ValuePeer`, `DragDistance`, `Motion.IsEnabled`,
  `MapViewport.ZoomStep`, `MapDots.Hold`, `MapRenderer.DistanceSquared`,
  `MapModel.Meridian(GeoPoint?)`, `AddDraft.Prepare`. No XAML element is
  named `Colour`, `Motion` or `DragDistance`, and the table library's
  `DragDistance` is internal to it.
- `PeerMap.xaml.cs`, `PeerMap/Navigation.cs`, `MapRenderer.cs`, `MapMotion.cs`
  and `SpeedGraph.xaml.cs` gain `using Syno.TinyTorrent.Controls;`. Their
  names do not collide with a `Controls` type: `MapRenderer.Progress` is a
  member, which wins over a namespace type.

## Files the change may edit

Product code: `Controls/Colour.cs` (new), `Controls/ValuePeer.cs` (new),
`Controls/DragDistance.cs` (new), `Controls/Motion.cs`,
`Controls/SettingsExpander.cs`; `Settings/Speedometer.xaml.cs`,
`Settings/Week.cs`; `Torrents/PiecesMap.xaml.cs`, `Torrents/SpeedGraph.xaml.cs`,
`Torrents/PeerMap.xaml.cs`, `Torrents/PeerMap/Navigation.cs`,
`Torrents/MapRenderer.cs`, `Torrents/MapMotion.cs`, `Torrents/MapModel.cs`,
`Torrents/MapDots.cs`, `Torrents/MapViewport.cs`, `Torrents/AddDraft.cs`.
Capture journey: `Shell/MainWindow/CapturePerformance.cs` (line 498 only).

Contracts:

- `docs/contracts/ui.md`, in "Windows, layout, and themes": a custom-drawn
  control paints each theme brush as XAML paints it, opacity included,
  animates only while Windows animations are on, and starts a drag at the
  Windows drag distance, as the tables do.
- `docs/contracts/ui/settings.md`: the speedometer's "Motion follows Windows
  animation preferences" adds "and stays still in High Contrast"; the week's
  gesture paragraph says a period moves or resizes once the pointer has moved
  the Windows drag distance sideways, and less is a click.
- `docs/contracts/peer-location.md`, "Zoom and pan": "the drag threshold"
  becomes the Windows drag distance.
- `docs/contracts/architecture.md`, ownership decisions: `Controls.DragDistance`
  and the table library's `DragDistance` each read the Windows drag distance,
  because the table uses no app code and publishes no Windows metric.

Not edited: `Torrents/MoveDraft.cs` and `Shell/MainViewModel/Actions.cs` (the
dropped finding), `CONTEXT.md` (no new product term), the language catalogues
(no new text).

## What changes for a person

- The week and the peer map follow the Windows drag distance, as the tables
  do. At the Windows default, nothing changes, except that a diagonal pan of
  the map starts after 4 DIPs along either axis, at most 5.7 DIPs of diagonal
  movement where it was 4.
- Colours the map, the Pieces map and the speedometer draw can move by one
  level of a channel. Every brush they read has opacity 1, so opacity changes
  no colour today.

Nothing else changes: the animation rules, the peer, the map's span, frame,
meridian, hit test, dot hold and zoom step, and the Add flow behave as before.

## Cost

No build in this session. The stage joins the next app batch, built with
`-Capture`, because `CapturePerformance.cs` changes. No check script and no
capture mode is needed: each question this stage raises is settled by reading
(step 2 and decision 10), and the drag distance read copies the table's
P/Invoke, which already works. For the reviewer, the scripts and modes whose
journeys run changed code are: `SpeedChecks.ps1` and `AddSourceChecks.ps1`
(neither runs a changed member); capture modes Map, Peers, MapComposition,
PerformanceMap, Performance, Details and the page capture of the Pieces map,
Connections (speedometer), Schedule (week), SettingsLayout (expanders) and
AddLayout (Add's preview path). About two hours of agent work for the change;
the gate took about two.

## Where this record departs from the plan and stage 1

- The plan's "What stays as it is" says the app has no copy of the table's
  drag distance. It has two, written as 4 DIPs in `Week.UpdateDrag` and
  `PeerMap.MoveDrag`. This stage replaces them with an app owner that reads
  the same Windows value; the table's own reader stays, because the library
  uses no app code.
- Stage 1's decision 8 foresaw one owner for custom-drawn controls holding
  the drag distance. This record makes three small owners instead
  (step 3, drag distance rows).
- Stage 1 recorded the drag rule as 4 DIPs, with each caller keeping its own
  measure. Decisions 4 and 5 change both for the reasons given there.

## Implementation notes

Implemented on 12 November 2026 on commit 5f8d2f6, by reading; nothing was
built or run. The stage's files held no other session's edits when it
started, and their lines matched this record. Where the code differs from the
record:

- **`Acquire` keeps its early return.** It calls
  `if (!await Prepare(...)) return;`, so a source removed during its preview
  skips the refresh after the `try`, as before. The record wrote a plain
  `await Prepare(...)`, which would have refreshed a removed source.
- **`DragDistance` declares `ExactSpelling = true`**, as the app's other
  P/Invoke declarations do (`App.cs`, `PeerMap/System.cs`); the table's has
  none. The call is the same.
- **`Motion`'s static field is `SystemSettings`**, the name `MapRenderer`
  gave its copy, so it does not read as the Settings area.
- **Braces were added** to every `if` whose condition or body changed, as the
  owner's code style asks: `Motion.Fade`, the speedometer's tick, `Show` and
  last digit, `PiecesMap.Render`'s two marks, `PeerMap.Underlay`,
  `Inspection.HitTest`, and both `Prepare` callers.
- **Usings the change left unused were removed:** `Microsoft.UI.Xaml.Media`
  in `Speedometer.xaml.cs` and `PiecesMap.xaml.cs`,
  `Microsoft.UI.Xaml.Automation.Provider` in `PiecesMap.xaml.cs` and
  `SpeedGraph.xaml.cs`, and `Windows.UI.ViewManagement` in
  `SettingsExpander.cs`. Reading found no other type from them in those
  files; the batch build is the proof.
- **`peer-location.md` keeps "drag threshold"** in its table and defines it
  once, in the paragraph below, as the Windows drag distance, horizontally or
  vertically.

## Review fixes

The review of stage 6 (scratchpad record `review-stage06.md`) found no
high or medium defect. The lead settled its three points.

- Where code rounds twice in a row, a colour can move by two levels of a
  channel, not one: the map's border alpha (`MapRenderer`, two `Alpha`
  calls) and the pieces map's chained `Mix` at the edges of its marks. No
  person can see two levels, and truncating there would break the settled
  rule that channels round, so the code stays and this record's "at most
  one level" is corrected here.
- The settled rule also adds work that decisions 9 and 10 did not list:
  four roundings per estimated country on each world copy of a map
  picture, on the render worker, and four brush opacity reads in each
  33 ms speedometer frame. Each costs nanoseconds beside a draw call and
  allocates nothing, so it stays.
- The UI contract's drag sentence now names only presses that can also be
  a click, the peer map and a schedule period; the week starts a new period
  after an hour of time, and the speed graph pans from the press.
