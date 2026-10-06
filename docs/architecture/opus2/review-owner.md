# Owner review of the UI-thread proposals

Review of [findings.md](findings.md), [review-platform.md](review-platform.md)
and [review-state.md](review-state.md), 2026-10-06. Source and issues were
read. Nothing was built, launched or measured.

Labels: **Proven** means I read the code path. **Measured** means a closed
issue measured it. **Inferred** means reasoning that nobody has observed.

## Position

The lag I feel has three proven causes, and none of them is the per-second
notification fan-out:

1. The projection feeds the table one row at a time. Startup, a filter click,
   Move to bottom and a finished download each cost one full table rebuild
   per changed row (#113). Proven path, size not measured.
2. Under the default Queue sort, the table holds my own second queue move for
   up to 3 seconds. Proven.
3. The status bar buttons move sideways when a rate changes width (#48).
   Proven.

Fix those first. Each is small, and each deletes code or adds one condition.

The broad `MainViewModel.Refresh` is not a measured cost (#106: at most
3.5 ms). I still want it off the per-second path, but for a different reason
than the state review gives: it forces workarounds in every child, including
the ones in my own schedule work. That change comes last, after the Settings
owner (#119) and the connection phase (#20) remove the cross-object reads that
make it hard to do simply.

## Verdicts

### Platform review

| Proposal | Verdict |
| --- | --- |
| 4.3 Projection publishes one source update per change | **Do it.** Merged with state step 1. See step 1 below. |
| 4.5.1 Row-order column is never held by settling | **Do it,** after step 1, with one sentence in contract section 9. |
| 4.5.2 A rebuild that moves nothing skips the focus hand-off and the `SelectionMode` toggle | **Do it,** together with #54 item 2 in the same code. |
| M4 Status bar | **Do it the #48 way:** two fixed-width rate fields. Do not move the rates to the last column; #48 already decided the grouping. |
| 4.1 Stable `Peer` and `Tracker` instances | **Do it,** late. It is the torrent table's established pattern, and two ways of feeding a table is one too many. |
| 4.2 Keep the broad `Refresh` | **Overruled in part.** See "Refresh" below. |
| 4.4 Hidden views: nothing new | **Agree.** No visibility gates. `Preferences.Apply` becomes quiet when nothing changed. |
| 4.6 Should a live-sort settle animate? | **Keep today's behaviour.** `Generic.xaml` records that reorder transitions are wanted, it applies only to a sort I chose, and no clean mechanism is known. |
| M6 Narrator or indicator noise | Check it as part of the TableView step, not separately. |
| 5 The one measurement | **Do it, but it does not gate step 1.** Step 1 is justified because it deletes code. The probe confirms whether my lag went with it. |
| "Must not" list | **Agree** with all four. |

### State review

| Proposal | Verdict |
| --- | --- |
| Step 1 Replace `VisibleTorrents`, delete `Search`, move the inspector-removed check to `Apply` | **Do it.** |
| Step 2 `Torrent.Update` reports changes | **Drop,** except delete `queueChanged` now. |
| Step 3 Split `Refresh` into five reactions | **Do it differently:** two paths, after #119 and #20. Keep `Changed("")` on `MainViewModel`. |
| Step 3 detail: `ObserveUpdates` only when `Updates` changes | **Wrong.** It breaks the daily update check. See "Missed or wrong". |
| Step 3 detail: remove `StartPlacement` from `OnModelChanged` | **Do it.** |
| Step 4 Settings keystrokes and draft edits use one narrow path | **Do it** in the Settings pass (#114, #119). |
| Step 5 `FileSelection.Apply` refreshes changed nodes only | **Drop.** #106 stays closed. |
| Step 5 `InspectorForm` reacts by name | **Drop.** |
| Step 5 General view binds `Target.*` for downloaded and remaining | **Do it.** Formatting then has one owner, `Torrent`. |
| Inspector `Observe` and `Read` notify only on change | **Drop.** Measured small; the named lists would need upkeep. |
| Step 6 Explicit connection phase | **Do it** as #20. |
| "—" row: one settings owner, section on `Preference` | **Do it** as #119. |
| "—" row: `Torrents` as one membership store | **Do it differently.** See "Missed or wrong". |
| TableView 1 and 3 | **Do it.** Item 3 is a comment fix in `RebuildView`. |

## Disagreements settled

### The projection: both are right, and neither said why the order matters

Both reviews want one publish per change. Contract section 5.3 allows a new
list: "the host assigns it again after changing its membership or order".
`Project` compares the desired array with the current one and assigns only
when they differ. The `ItemsSource` binding becomes `OneWay`.

This also drops the constraint that #105 kept ("selection and queue dragging
see the same collection changes"). The table reconciles selection by key, and
contract 5.3 cancels a row drag on any source update, so per-row notifications
protect nothing. Proven from `RebuildView`, which calls `CancelRowDrag` on every
rebuild.

Order matters (proven from `Project` and `ViewOrder`). Today, Move to bottom on
the first of N rows raises N − 1 `Move` notifications. Under the Queue sort
all but the first are held, so they are cheap and invisible. If the row-order
exemption lands first, every one of those N − 1 rebuilds re-sorts and
reconciles the visible rows. So the exemption must come after the projection
change, never before.

### `Refresh`: neither "leave it" nor "five reactions"

The platform review is right about cost. #106 measured at most 3.5 ms, and
nothing a user sees comes from it.

The state review is right about structure, and wrong about the remedy. Five
reactions with named window properties (`Rates`, `Incoming`, `ErrorCount`,
`FilterLabel`, `TorrentError`) make every new getter a "remember to add it
here" rule. A missed name is a stale screen that nobody notices for weeks.
`Changed("")` on `MainViewModel` itself is fine: it is one object, and the
XAML property system ignores writes of equal values (inferred, consistent
with #106).

What is wrong is that every snapshot refreshes every child. That is the cause
of these, all proven:

- `Week.Draw` runs twice per snapshot once Settings was opened: once from
  `Preferences.Apply` and once from the parent's `Refresh`. This assumes a
  collapsed page stays loaded, which #114 and both reviews assume too.
- `Week.OnModel` needs its `HasDraft` skip and `Preferences.RefreshDraft`
  exists, because the broad path would otherwise redraw on every draft step.
- `ObserveUpdates` runs on every `Preferences` event, including every
  keystroke in a Settings field.

So the target is two paths:

- The **broad** `Refresh`, unchanged in content minus `Project`. It runs on
  rare transitions: connection phase, `CanEdit`, picker, closing, language,
  errors.
- A **window-only** notification for a snapshot, a selection and a filter
  change: `Changed("")` on `MainViewModel`, the filter choices and the command
  list. `Apply` takes the broad path when the connection phase or `CanEdit`
  changed.

This is simple only after #119 and #20. Today the children read `_settings`,
`Theme`, `_writable` and four other flags, and the condition "something a
child reads changed" has no clean form. After #119, the children read only
`CanEdit`, `IsConnected` and `Message`. I checked `AddDraft`, `FileOperation`
and `SpeedLimits` for parent reads. After #20, all three follow from one phase
value.

### Hidden views

The platform review is right: a view should not know that it is hidden. #114
asks for a visibility gate. Drop that part of #114. Once the snapshot stops
refreshing `Preferences`, a hidden `Week` receives nothing.

### Torrent change detection

Drop it. A per-row "did anything change" needs a stored copy of the last row.
That is a derived value stored without a measured reason, which `AGENTS.md`
rules out. The platform's cheap no-move rebuild in the table covers the idle
case at the owner of the cost.

## Missed or wrong in both reviews

1. **The daily update check depends on per-snapshot polling.** Proven.
   `CheckUpdates` returns early when the last check is less than a day old.
   Only `ObserveUpdates` starts it. Its only caller is `OnTaskChanged`, for
   `Preferences` events. The state review would call it only when `Updates`
   changes, so a window that stays open for more than a day never checks
   again. Fix: `Apply` calls `ObserveUpdates` once per snapshot, explicitly.
   That keeps the daily check and removes the per-keystroke call.
2. **`queueChanged` is provably redundant.** The Queue column's sort key is
   `QueueOrder` (`MainWindow`, the `Schema` call), the same key `Project`
   orders by. Any queue change that moves a row under the Queue sort already
   raises a source change. Under any other sort, `RefreshView` runs anyway.
   Delete it and the `bool` on `SnapshotApplied` now.
3. **"Membership is stored twice" is overstated.** A list for order and a
   dictionary for lookup is an index, not a second owner. The real defects
   are smaller. `Torrents` is an `ObservableCollection` that nothing observes:
   no XAML binds it and no code subscribes to `CollectionChanged`. And
   `Inspector.IsAvailable` and `IsRemoved` search it linearly. Make `Torrents`
   a plain list, and let the inspector ask `_byId`. Keep the list: the order of
   torrents with equal `QueueOrder` comes from it.
4. **Deleting `Search` leaves wrong text.** `no_matches_detail` says "Clear the
   search or Errors filter". The search box does not filter the table. Fix the
   wording in `en.json` and `es.json` in the same change.
5. **The probe does not exist.** Both reviews say "use the #106 probe". No
   `WM_NULL` or `SendMessageTimeout` code is in the repository; it was a
   throwaway. Write it again as a small tool outside the product, not as a
   capture journey (#124).
6. **The platform review ignores #48's decision.** #48 decided on 2026-10-05:
   "separate fixed-width rate items". Moving the rates to the last column
   would undo #48's grouping of rates with the alternative-limits toggle.
7. **"Finding 4 is one-off" is not quite right.** `UpdateChrome` runs on up to
   five events per resize step, not once per action. It is still a few
   region calls, and nothing shows it costs anything. No work item.
8. **The uncommitted tree is larger than the five files named.**
   `MainViewModel/Finding.cs`, `MainWindow.cs`, `MainWindow/Workspace.cs`,
   `PreferencesForm.xaml.cs` and both catalogues also have uncommitted search
   work. Step 1 edits `Finding.cs`, so that work must be committed first.

## Work underway

Verdict: good direction. Commit it after two changes.

Keep:

- `PeriodDraft.SetSpan`: one draft mutation for create, reschedule and drag.
- `PeriodDraft.TimeLabel` replacing `Preferences.PreviewLabel`. The draft owns
  its own label.
- `DrawGrid` separate from `Draw`. The grid is rebuilt only on size, text and
  scale. This is the real saving.
- One named `Tip` element instead of a new tooltip on every draw.
- The unchanged-span guard in `UpdateDrag`. Spans snap to 15 minutes
  (`PeriodSpan.Snap`), so a drag draws at most once per snap step.

Change before committing:

1. **Drop the element pooling** (`GetBorder`, `Trim` and the property resets).
   Because of the snap guard, `Draw` runs only when a span changes by 15
   minutes, so reuse saves almost nothing (inferred). Pooling makes every use
   reset Width, Height, BorderThickness and CornerRadius on a shared
   `Outlines` canvas that also holds handles and the cursor. Nothing enforces
   those resets. Clear the dynamic layers and create the few elements again,
   as the old code did.
2. **Parse the capture mode once** (#124). This diff adds "search" and
   "library" in `IsCaptureReview` and again in the `CaptureReview` if-chain.
   That is the duplication #124 describes, and #124 says to fix it "the next
   time the capture code changes".

Accept as interim, and delete in step 6: `Preferences.RefreshDraft` and the
`HasDraft` skip in `Week.OnModel`. They are correct, and they exist only
because of the broad refresh.

Not yet consistent, fix in step 4: the `PeriodDraft.Start`, `End` and
`IsPaused` setters and `DayChoice.IsChecked` still call `owner.Refresh()`. The
time pickers and the drag take two paths for one change.

`CaptureSearch.cs` and `CaptureLibrary.cs` earn their place: Ctrl+K behaviour
is a current release goal, and the 300-torrent library is the only fixture
with a realistic list. `CaptureLibrary` calls `VisibleTorrents.IndexOf`, which
step 1 changes.

## Deferred issues

| Issue | Disposition |
| --- | --- |
| #113 bulk projection | **Absorb:** step 1 closes it. |
| #105 (closed) | Stays closed. Its "same collection changes" constraint is dropped on purpose, for the reason above. Unchanged snapshots keep its 7 ms result, because an unchanged order publishes nothing. |
| #54 TableView selection | **Absorb item 2** into step 2: restore focus to the row that held physical focus, and skip the hand-off when nothing moved. Items 1 and 3 stay open. |
| #48 status bar | **Absorb** as step 3, per its 2026-10-05 decision. |
| #114 scheduler redraws | **Change:** drop the visibility gate. Steps 4 and 6 fix it. |
| #119 one settings owner | **Absorb** as step 4. My decision on its open question: delete the Limits dialog. One editor for four values. |
| #122, #112, #120 | **Absorb** into step 4, in #119's order. On #120's question: keep the two prompt buttons and correct the contract sentence. |
| #20 message bar | **Absorb** as step 5, including the connection phase. |
| #124 capture size | **Absorb its enum item** into the work-underway commit. Its two owner questions are outside this review. |
| #56 TableView | **Leave.** The exemption is in `ViewOrder`, not the `Sort` setter. |
| #116 selection owner | **Leave.** Step 1 moves the inspector-removed check into `Apply`'s removal loop; #116 should start from that. |
| #106, #107, #111 (closed) | **Unchanged.** No off-thread preparation, no asynchronous queries, and Files keeps its per-node refresh. |
| #103, #12 (closed) | **Unchanged.** `PipeClient` stays the only scheduler; no typed codec. |
| #29 | **Leave.** Not touched by these proposals. |

## Delete outright

- `MainViewModel.Search`, `_search` and the name filter in `Project`.
- The `Remove`, `Add` and `Move` loops in `Project`, and the
  `ObservableCollection` type on `VisibleTorrents` and `Torrents`.
- `queueChanged`, its second read of `queue`, and the `bool` on
  `SnapshotApplied`.
- The `StartPlacement()` call in `MainWindow.OnModelChanged`.
  `ShowWhenReady` already calls it, and `DataDirectory` is set at the pipe
  greeting, before any snapshot (proven in `PipeClient`).
- The Limits dialog: `SpeedLimits`, `LimitChoice`, `ShowLimits` and their
  entries (#119).
- `_settings`, `Setting` and `Limit` (#119).
- `_writable`, `_ready` and the `_sessionId` emptiness test as state flags
  (#20).
- `Inspector.Downloaded` and `Inspector.Remaining`.
- After step 6: `Preferences.RefreshDraft`, the `HasDraft` skip in
  `Week.OnModel`, and the `ObserveUpdates` call in `OnTaskChanged`.
- The comment in `Table.RebuildView` that says a row drag is cancelled "only
  once the order beneath it moved". The code cancels on every rebuild, as
  contract 5.3 requires.

## Order of work

Each step builds and ships alone. Build once per step, after all its edits.

0. **Commit the work underway**, with the two changes above. The schedule work
   and the search work are separate commits.
1. **One projection publish.** `VisibleTorrents` becomes a read-only list that
   `Project` replaces only when membership or order differs. `Project` runs
   from `Apply`, the `Filter` setter and `ClearFinding`, not from `Refresh`.
   Delete `Search` and fix the empty-state text. Delete `queueChanged`.
   `Torrents` becomes a plain list. Adjust `CaptureLibrary`. Rewrite the probe
   and run it before and after: 2,000 generated torrents; startup, a filter
   click and back, and Move to bottom.
2. **TableView.** The row-order column is not held by settling, with one
   sentence in contract section 9. A rebuild that moved nothing leaves focus
   and the hosted selection mode alone; when it does move focus, focus returns
   to the row that had it (#54 item 2). Fix the drag comment. Verify: a second
   Move up within 3 s shows at once, and 10 s under a Down sort with a focused
   row raises no focus events.
3. **Status bar** (#48). Two fixed-width rate fields.
4. **Settings pass:** #122, then #119 with #112 and #120. `Preference.Input`
   and the period draft setters raise their own names and tell `Preferences`
   that the draft changed. `Preferences.Apply` raises nothing when no
   confirmed value changed. Theme becomes a `Preference`.
5. **Connection phase** (#20).
6. **Snapshot path stops refreshing the children.** Add the window-only
   notification and use it for snapshot, selection and filter. `Apply` calls
   `ObserveUpdates` directly. `Week` redraws on `WeekChanged`, size, theme and
   text only. Delete the interim workarounds listed above.
7. **Inspector.** The General view binds `Target.*`. `Peer` and `Tracker`
   instances stay stable per endpoint and URL, as `Torrent` does. Watch Peers
   during a real transfer before and after.

Not on the list, by decision: per-row change detection, named window
notifications, five refresh reactions, visibility gates, partial Files
refresh, a settle-animation change, and any work moved off the UI thread.
