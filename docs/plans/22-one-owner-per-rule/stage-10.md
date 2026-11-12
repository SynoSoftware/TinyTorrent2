# Stage 10 gate record: subtitle and Library operations

Gate done on 12 November 2026, by reading. Nothing was built or run. This
record takes the place of the stage's text in
[the plan](../22-one-owner-per-rule.md#10-subtitle-and-library-operations) for
the session that implements it.

Paths without a prefix are under `app/src`. Line numbers are those of commit
a4312a7. Stages 1, 2, 3, 8 and 9 edit some of these files first, so each
reference also names its member; find the member when the line moved.

## Where the stage stands

Every decision is made. The implementer needs no further design. The stage
changes no text a person reads. What a person can meet differently is under
[What changes for a person](#what-changes-for-a-person).

The stage creates four small owners and five private members of
`Acquisition`. The decisions, each with its reason below:

1. A view of subtitle work owns one `Subtitles.Attachment`. It listens to the
   model only while the view is shown, refreshes the view once for a burst of
   changes, and runs the actions the person starts.
2. A view stays attached when it is hidden or closed. It is detached only
   when subtitle work has no model. So a failure that arrives after the
   person left the view is still reported.
3. In `Acquisition`, the three operations that ask the supplier for the
   person (`Check`, `Candidates`, `Choose`) run through one member, `Ask`.
   All five operations the person starts end through `ReleaseForPerson`.
4. One member, `LookupFor`, builds the lookup of a target. Six places built
   it by hand.
5. Three of the five settings writes are one rule and run through
   `Reconfigure`. `WriteExtras` and `Suspend` are other rules and stay.
6. Every settings write waits ahead of the worker, as the person's other
   operations do.
7. One member, `ShowScan`, runs a scan as a work item. `Invalidate` calls
   `ReportWorkWaiting`.
8. `Shared.Rerun` owns "run the pass again when it is requested during its
   run". Subtitle reconciliation, the check of displayed movies and
   Library's file-properties read use it.
9. `Library.Pass<T>` owns the accepted targets of one automatic pass over the
   entries on screen. The metadata sweep and the file-properties read use it.
   `WorkProgress.Counted` owns "a fraction, or unknown when nothing is
   counted".

## Gate step 1: the findings, read again

Each finding is kept with the one sentence that states its rule, or dropped.

| Finding | Result | The rule, or the reason to drop |
| --- | --- | --- |
| One shell for an operation the person starts: `Candidates` (`Subtitles/Acquisition/Choose.cs` 11), `Choose` (85), `Check` (`Subtitles/Acquisition.cs` 561) | Kept | An operation that asks the supplier for the person runs alone, ahead of the worker; a configuration change, the window's close or its caller's cancel ends it; when it ends, the allowance is read again, the views are told and the worker continues. |
| The same shell in `Rescan` (`Acquisition/Discovery.cs` 131) | Kept in part | `Rescan` shares the wait ahead of the worker and the end. It asks no supplier, so it reads no allowance, and it has a work item. It uses `TakeForPerson` and `ReleaseForPerson`, not `Ask`. |
| Not in the plan: `Find` (`Acquisition/Acquire.cs` 839) | Added | `Find` is the fifth operation the person starts. It shares the wait and the end. Its wait is ended only by the window's close; see [the waits](#the-waits-traced). |
| One lookup factory (`Choose.cs` 35, 70, 109; `Acquire.cs` 275, 370; `Acquisition/Storage.cs` 713) | Kept | The lookup of a target asks for the target's file name in the target's language at the target's stage, and is valid while the target's videos are current. |
| One settings write: `WriteEnabled` (`Acquisition.cs` 275), `WriteLanguages` (329), `SaveAccount` (468) | Kept | A configuration change that changes jobs is saved alone under the operation gate; the counts are read again before other work continues; then the views are told and membership is reconciled. |
| The same write in `WriteExtras` (383) | Dropped | It changes no job and no count, and it does not outdate work in progress. It saves one value under the gate. Recounting and reconciling for it would read the whole library for nothing. |
| The same write in `Suspend` (192) | Dropped | It is the host's hold on subtitle work while the host changes shared protection state (`Shell/Features.cs` 264, 599). It wakes the worker and does not reconcile, because the host holds `Features._access` and Library's lookups while it waits; a reconciliation there would add a folder scan to that hold. |
| One scan run: `Rescan` (`Discovery.cs` 129), `Discover` (243) | Kept | A scan that the work list shows is one Discovery work item: it ends Cancelled when stopped, Failed with its error or when a folder could not be read, and Completed otherwise, and its summary is the files found and the folders unreadable. |
| `Invalidate` (`Acquisition.cs` 641) calls `ReportWorkWaiting` (`Acquire.cs` 29) | Kept | Both show the manual and the automatic work item as Waiting. |
| One rerun loop: `QueueInspect` and `InspectChanges` (`Discovery.cs` 33 to 67), `Reconcile` and `ReconcileChanges` (`Storage.cs` 203 to 245) | Kept, with a third copy | A request made while a pass runs causes exactly one more pass after it. `FileFacts.Start` and the tail of `FileFacts.Run` (`Library/FileFacts.cs` 53 to 60, 259 to 264) are the same rule. |
| One change subscription for the three views: `Batch.Attach`, `SetVisible`, `OnChanged` (`Subtitles/Batch.cs` 85 to 126); `Settings.Attach`, `SetVisible`, `OnChanged` (`Subtitles/Settings.cs` 304 to 339); `Automatic.Attach`, `OnChanged` (`Subtitles/Automatic.cs` 40 to 52) | Kept | A view listens to its model only while it is shown, and refreshes once on the UI thread for a burst of changes. |
| One action runner: `Batch.Find`, `Stop` (`Batch.cs` 258, 286); `Settings.Save`, `Check`, `Scan`, `CanReport` (`Settings.cs` 569, 592, 628, 657); `Automatic.Save` (`Automatic.cs` 77) | Kept | An action the person starts in a view is not a failure when it is cancelled, reports its failure in the window unless the model closed or was replaced, and refreshes the view when it ends. |
| One on-screen work loop: `FileFacts.Run` (`Library/FileFacts.cs` 118, 267); `VideoLookup.PrepareSweep`, `ServeSweep`, `StopSweep` (`Library/VideoLookup.cs` 1199, 1257 to 1269, 1362) | Kept, as a type and not as a loop | An automatic pass over the entries on screen accepts the pending targets the person has not cancelled; a target whose row scrolled away before its turn waits for a later pass; when the person cancels the pass, later passes skip the targets it did not finish. |
| The same loop in `VideoLookup.ReportBatch` (751), `ReportSweep` (1343), `FileFacts` 267 | Kept, as a second rule | Progress of counted work is a fraction of its total, and unknown while nothing is counted, because a fraction needs a positive total. The batch is not on-screen work; it shares only this rule. |
| The rerun in `VideoLookup` (`_again`, 360, 1206, 1405) | Dropped | `_again` records that the displayed entries or the connection changed after `PrepareSweep` read them. Whether a new sweep may start is decided by `Refresh` (354 to 407) from the batch, the person's requests, the provider failure and the context. That is not "run the pass again". |

## Gate step 2: the questions the gate had to answer

The plan lists no open question for stage 10. Reading raised these.

**Is `Find` cancelled by a configuration change while it waits?** No, and it
stays so. `Find` waits with `_lifetime.Token` only and reads `_requests.Token`
after it has the gate (`Acquire.cs` 843 to 850). `Check`, `Candidates` and
`Choose` read `_requests.Token` before they wait (`Acquisition.cs` 564 to 572;
`Choose.cs` 18 to 26, 92 to 100), so `Invalidate` ends them while they wait.
The difference is correct. A check, a choice list and a chosen subtitle belong
to the account, the supplier and the languages they started under. Find acts
on the configuration that is current when it runs. If a configuration change
ended a waiting Find, the person's click would vanish without a message,
because `Batch.Find` treats a cancellation as no failure (`Batch.cs` 271).

**What does "attached to its model" mean?** The plan settles that a view
"reports a failure only while it is attached to its model". Today the views
disagree. `Batch.Find` and `Stop` report always (`Batch.cs` 277, 300).
`Settings` and `Automatic` report when the model is not closed and the view
has no model or the same model (`Settings.cs` 657; `Automatic.cs` 91). Hosts
detach a view to hide it: `SettingsPage.RefreshSubtitles` passes null to the
Home switch when it is not shown (`Settings/SettingsPage/Subtitles.cs` 27),
`SettingsPage.Release` detaches both views (`Settings/SettingsPage.xaml.cs`
104 to 109), and the batch dialog detaches when it closes
(`Shell/MainWindow/Subtitles.cs` 57). So "the view has no model" today means
"the view is hidden", and the failure is reported.

Decision: hiding and closing never detach. A view has no model only when
subtitle work has none (`MainViewModel.Subtitles` is null). Then the plan's
sentence holds as written, and the person still gets the failure of an action
after leaving the view. The reason is the
[first usability rule](../../contracts/architecture.md#usability-comes-first)
and the owner's rule that failed input never vanishes: a person who turns the
Home switch and opens Torrents at once must still learn that the save failed.

**Does the view's refresh need stage 4's "once per turn" owner?** No.
`Acquisition.Changed` is raised on worker threads (`Acquisition.cs` 125 to
128 state it for `FileFailed`; `Notify` runs in every `finally` of the
worker). Stage 4 has no gate record yet, and its callers are all on the UI
thread. `Attachment` therefore keeps the one interlocked flag that
`Batch.OnChanged` has today (`Batch.cs` 114 to 126). Rule for the
implementer: when stage 4's owner, as built, documents that any thread may
request it, `Attachment` uses it and the flag goes; otherwise the flag stays.

**Does `Acquisition` report the failures itself?** No. The choice dialog and
the account dialog show a failure inside the dialog
(`Subtitles/ChoiceDialog.cs` 164 to 168; `Shell/MainWindow/Subtitles.cs` 108
to 116), and the Settings views show it in the window's notifications. The
surface decides, so the runner belongs to the views.

**Which check script reaches these members by name?**
`app/tests/SourceChecks.ps1` only. It reads the fields `_operation` (839),
`_account` (549, 697, 711, 933), `_inspecting` (1238) and `_deletions`
(1321) of `Acquisition`, the field `_batch` of `VideoLookup` (1128) with
`Queue`, `Work`, `Observation`, `Progress` and `Total`, and the types
`Acquisition+SearchIntent` and `VideoLookup+Context`. It calls 30 methods of
`Acquisition` by name; the list is under
[gate step 4](#gate-step-4-consumers-of-every-changed-name). One of all
these changes: `_inspecting`.

**Do the capture journeys reach a changed name?** No.
`Shell/MainWindow/CaptureSettings.cs` 23, 296 to 300, 847, 990, 996 and
`CaptureLibraryFiles.cs` 106, 109, 469 to 481, 517 use `SetEnabled`,
`SetLanguages`, `SelectSupplier`, `Edit`, `Save`, `Count`, `Saving`,
`Subtitles.Settings.Field` and the type `Subtitles.Batch`. All stay. No file
under `Subtitles`, and neither `FileFacts.cs` nor `VideoLookup.cs`, has a
`#if CAPTURE` block.

## The waits, traced

For each operation: what it waits for, what ends each wait, what can change
before it completes, and what guards a late result. "The gate" is
`Acquisition._operation`.

| Operation | Waits for | What ends the wait | What can change meanwhile | Guard on the result |
| --- | --- | --- | --- | --- |
| `Check` | The gate; one supplier request | The holder releases: the worker stops before its next search request (`Supplier.cs` 263), and a request already sent finishes within the HTTP limits. `Invalidate`, the window's close or the caller's token cancel the wait and the request. | The saved account (`SaveAccount`) | The answer is recorded under `_requestSync`, only when the token is not cancelled and `_account` is still the checked account (`Acquisition.cs` 588 to 605). |
| `Candidates` | The gate; the search pages, at most 60 seconds together | As `Check`. The dialog cancels the earlier search when the person changes language (`ChoiceDialog.cs` 145). | The video leaves its torrent; the configuration | `Validate` runs before every request (`Acquire.cs` 226). The dialog keeps a result only when its request is still the current one (`ChoiceDialog.cs` 161, 171). |
| `Choose` | The gate; the download | As `Check` | The video leaves; the folder refuses the file | `Validate` before the request and before publication; the file's record and the job are written with no token once the file is published (`Acquire.cs` 811 to 818; `Choose.cs` 146). |
| `Find` | The gate | The holder releases. Only the window's close cancels the wait. | The configuration, while it waits | `CanFind` is read again inside the gate (`Acquire.cs` 846); the token is read inside the gate. |
| `Rescan` | The gate; the folder walks | The holder releases; `Invalidate` or the item's Cancel in the work list cancels the wait and the walk. | Movies move or change stamp | A movie that is not current at its path and stamp gets no scan marker (`Discovery.cs` 509 to 516). `Settings.Scan` keeps the result only for the same model (`Settings.cs` 638). |
| `WriteEnabled`, `WriteLanguages`, `SaveAccount` when the account changed | The earlier queued write; the gate | `Invalidate` cancels the holder's requests, so the holder releases at once. | Nothing: writes are queued in click order (`Queue`, `Acquisition.cs` 245) | The row and the field change inside the gate. |
| `WriteExtras`, `SaveAccount` when the account is the same | The earlier queued write; the gate | The holder releases. Today nothing shortens this wait: the write waits for the worker's whole step. After this stage the worker stops before its next search request. A running `Rescan` still holds the gate until it ends. | Nothing | As above |
| The check of displayed movies (`Inspect`) | The folder walk; the gate, for each folder's record only | `Invalidate` cancels both. The walk holds no gate, so a sleeping drive delays nothing else. | The displayed entries; a movie moves | As `Rescan`. A change of the displayed entries during the check requests one more check. |
| `ReconcileOnce` | The gate; the scan of torrents that just finished | `Invalidate`; the scan item's Cancel, which also decides the completion searches as none (`Storage.cs` 264 to 274) | Membership | Every write checks `Current` in its transaction. |
| `FileFacts`, one read | The database; three Windows property calls for each file | The connection's token or the item's Cancel; a call already made may have to return | A row scrolls away; the file changes | A row off screen at its turn is left for a later read. `Save` writes only when the file still has the read path, size and readability (`FileFacts.cs` 298 to 309). |
| The metadata sweep | Start-up to settle (`PrepareSweep`, 1202); the provider request; a hold (`Wait`, 994) | The context's token; new work wakes a hold; a hold is waited in parts of one day at most | A row scrolls away; the batch takes precedence | Unchanged by this stage. `Collect` owns the saving. |

One wait has no short end and this stage does not change it. `DisposeAsync`
awaits the queued settings writes before it cancels anything
(`Acquisition.cs` 706). A `WriteExtras`, or a `SaveAccount` of the same
account, that waits for the gate behind a running `Rescan` therefore delays
the close of subtitle work until the scan ends. The stage leaves it, because
the remedy is a choice between cancelling the person's scan and not saving
their setting, and neither belongs to "one owner per rule". The lead decides
whether it becomes an issue.

**Lead decision, 12 November 2026: the stage fixes this wait.** Closing
subtitle work first ends the person's running operation, as the window's
close already ends `Find`, and then awaits the queued settings writes. So a
setting the person chose is still saved, and the close ends within the time
a cancelled scan takes to return. Reason: a close that waits for a whole
scan is a wait with no visible end, and the person who closes no longer
waits for that scan. The stage owns this code path, because it reshapes
`ReleaseForPerson` and the person's operations.

## Gate step 3: the design

The gate compared the candidates for each owner itself, with the
`/codebase-design` vocabulary; no sub-agent designed one. A candidate is
judged by whether the rule gets one place to change.

### 1. `Subtitles.Attachment`

New file `Subtitles/Attachment.cs`. Each of the three views owns one.

| Candidate | Result |
| --- | --- |
| A base class for the three views | Rejected. No host treats the three as one type, and [naming](../../agents/naming.md#interfaces) allows a base class only where a caller selects between implementations. |
| `Acquisition` raises `Changed` on the UI thread, once for a burst, and reports its own failures | Rejected. `Acquisition` would take a `DispatcherQueue`, and the check script builds it without one (`SourceChecks.ps1` 508). Where a failure shows is the surface's choice, as [gate step 2](#gate-step-2-the-questions-the-gate-had-to-answer) says. |
| An object each view owns | **Chosen.** The subscription, the burst and the runner get one implementation; a view keeps its controls and its own flags. |

```csharp
namespace Syno.TinyTorrent.Subtitles;

/// <summary>
/// A view's attachment to the acquisition it shows. While the view is
/// shown, a burst of the model's changes refreshes it once, on the UI
/// thread. An action the person starts through it reports its failure in
/// the window unless the model closed or was replaced meanwhile.
/// </summary>
internal sealed class Attachment(
    DispatcherQueue dispatcher,
    Action refresh,
    Action<Exception, string> report
)
{
    internal Acquisition? Model { get; private set; }
    internal bool IsVisible { get; private set; }

    /// <summary>Attaches the view to the model, or to none. Returns whether the model changed.</summary>
    internal bool Attach(Acquisition? model);

    /// <summary>Returns whether the visibility changed.</summary>
    internal bool SetVisible(bool visible);

    /// <summary>
    /// Runs an action the person started on the attached model. The action
    /// is called before this method returns, so settings writes queue in
    /// click order. Returns whether the action completed.
    /// </summary>
    internal async Task<bool> Run(Func<Acquisition, Task> action, string failure)
    {
        if (Model is not { } model)
            return false;
        try
        {
            var running = action(model);
            refresh();
            await running;
            return true;
        }
        catch (OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine($"Subtitle action cancelled ({failure}).");
            return false;
        }
        catch (Exception error)
        {
            if (ReferenceEquals(model, Model) && !model.IsDisposed)
                report(error, failure);
            return false;
        }
        finally
        {
            refresh();
        }
    }
}
```

Rules of the implementation:

- `Attach` and `SetVisible` subscribe to `Model.Changed` exactly while
  `Model` is not null and `IsVisible` is true. Both run on the UI thread.
- The handler of `Changed` runs on any thread. It posts one callback to
  `dispatcher` for a burst, with the interlocked flag of `Batch.OnChanged`,
  and clears the flag when the post is refused. The callback calls `refresh`
  only when `IsVisible` is still true.
- `Run` awaits without `ConfigureAwait(false)`, so the view's code after the
  action runs on the UI thread. `failure` is the notification title key that
  the views pass today, such as `"failed_settings"`.
- `refresh` is the view's own `Refresh`. Each view's `Refresh` already
  returns early while hidden (`Batch.cs` 159; `Settings.cs` 407).

How each view uses it. The fields `_model`, `_visible` and `_queued` of the
views go; a view reads `_attachment.Model` and `_attachment.IsVisible`.

| View member | After |
| --- | --- |
| `Batch.Attach` (`Batch.cs` 85) | `if (!_attachment.Attach(model)) return;` then the reset of `_counts` and `_batchId`, then `Refresh()` |
| `Batch.SetVisible` (99) | `if (_attachment.SetVisible(visible)) Refresh();` |
| `Batch.OnChanged` (114) | Deleted |
| `Batch.Find` (258) | The guard and the reads of the two selections stay. `_busy = true;` then `await _attachment.Run(async model => { try { await model.Find(scope, guesses, _entries); _batchId = model.BatchId; } finally { _busy = false; } }, "failed_subtitle_find");` |
| `Batch.Stop` (286) | `_attachment.Run(model => model.StopFinding(), "failed_subtitle_stop")` |
| `Settings.Attach` (`Settings.cs` 304) | `if (!_attachment.Attach(model)) return; _scanned = null; _batch.Attach(model); _automatic.Attach(model); Refresh();` |
| `Settings.SetVisible` (319) | `if (!_attachment.SetVisible(visible)) return;` then `_batch.SetVisible(visible && _tasks.SelectedItem == _searchTab); _automatic.SetVisible(visible);` and the two lines for `_motion` and `RefreshText` as today |
| `Settings.OnChanged` (339), `CanReport` (657) | Deleted |
| `Settings.Save(Task)` (569) and its four callers (252, 267, 273, 565) | `await _attachment.Run(model => model.SetExtras(extras), "failed_settings")`, and the same for `SelectSupplier`, `FollowLanguage` and `ChangeLanguages`. `Save` is deleted. |
| `Settings.Check` (592) | See below |
| `Settings.Scan` (628) | The guard stays. `_scanning = true; _scanned = null;` then `Run(async model => { try { var result = await model.Rescan(); if (ReferenceEquals(model, _attachment.Model)) _scanned = result; } finally { _scanning = false; } }, "failed_subtitle_scan")` |
| `Settings`, the `Unloaded` handler (295) | `_motion.Stop(); SetVisible(false);` It no longer detaches. |
| `Automatic.Attach` (`Automatic.cs` 40) | `if (_attachment.Attach(model)) Refresh();` |
| `Automatic.SetVisible` | New, internal: `if (_attachment.SetVisible(visible) && visible) Refresh();` |
| `Automatic.OnChanged` (52) | Deleted |
| `Automatic.Save` (77) | `_attachment.Run(model => model.SetEnabled(enabled), "failed_settings")`. The test for a closed model before the write goes: a closed model's queue cancels the write (`Acquisition.Queue`, 259), and a cancellation is no failure. |
| `Automatic`, the `Unloaded` handler (36) | `SetVisible(false)` |

A view's busy flag is cleared inside the action, in its `finally`, so the one
refresh at the end of `Run` already shows the flag cleared.

`Settings.Check` keeps today's two steps, because the retry can end with
another model attached (`Settings.cs` 600 to 604):

```csharp
private async Task Check()
{
    if (_checking || _attachment.Model is not { CanCheck: true })
        return;
    _checking = true;
    try
    {
        if (await _attachment.Run(_ => _retry(), "failed_subtitle_check"))
            await _attachment.Run(
                model => model.CanCheck ? Checked(model) : Task.CompletedTask,
                "failed_subtitle_check"
            );
    }
    finally
    {
        _checking = false;
        Refresh();
    }
}
```

`Checked(model)` is a private method that awaits `model.Check()` and keeps
the one `catch` with its comment: a used-up allowance whose return time is
ahead is not reported, because the status line already says when it returns
(612 to 615).

The hosts:

| Host | Today | After |
| --- | --- | --- |
| `SettingsPage.RefreshSubtitles` (`Settings/SettingsPage/Subtitles.cs` 27) | `_automatic?.Attach(visible && _category is null ? Main.Subtitles : null);` | `_automatic?.Attach(Main.Subtitles); _automatic?.SetVisible(visible && _category is null);` |
| `SettingsPage.Release` (`Settings/SettingsPage.xaml.cs` 104 to 109) | Detaches the Home switch; hides and detaches the card | `_automatic?.SetVisible(false);` and `subtitles.SetVisible(false);` |
| `MainWindow.DownloadSubtitles` (`Shell/MainWindow/Subtitles.cs` 56, 57) | `content.SetVisible(false); content.Attach(null);` | `content.SetVisible(false);` |

`SettingsPage.RefreshSubtitles` 28 to 32 and `MainWindow.DownloadSubtitles`
25, 26 already call `Attach` and `SetVisible` for the card and the dialog,
and stay.

### 2. The operations the person starts, in `Acquisition`

Owner: `Acquisition`, private members in `Subtitles/Acquisition.cs` beside
`TakeForPerson` (626). The
[search protection contract](../../contracts/search-protection.md#ownership-and-interface)
keeps the gate in `Acquisition` and rejects a shared executor, so no type
outside `Acquisition` is added.

What each caller needs:

| Caller | Token that ends the wait | Reads the allowance at the end | Work item |
| --- | --- | --- | --- |
| `Check` | Requests, lifetime, caller | Yes | No |
| `Candidates` | Requests, lifetime, caller | Yes | No |
| `Choose` | Requests, lifetime, caller | Yes | No |
| `Rescan` | Requests, its work item | No | Yes |
| `Find` | Lifetime | No | No |

| Candidate | Result |
| --- | --- |
| One wrapper for all five, which always reads the allowance | Rejected. `Find` needs another token, which becomes a parameter that one caller sets. For `Find` and `Rescan` the allowance read is work with no cause, and a database failure in it would discard a finished scan's result. |
| A scope object, `await using var turn = await Enter(token)` | Rejected. Every call needs `ConfigureAwait(false)` on the wait and on the scope, which hides the one line that matters. |
| A pair only, `TakeForPerson` and `ReleaseForPerson`, written out in all five | Rejected for the three supplier operations: each would still repeat the linked token and the nested `finally` that reads the allowance before the release. |
| `Ask` for the three that ask the supplier; the pair for `Rescan` and `Find` | **Chosen.** Each of the two rules has one place: "what ends a person's operation" in `ReleaseForPerson`, and "an operation that asks the supplier" in `Ask`. |

```csharp
// Ends an operation the person started: the views show its result, and
// the worker continues.
private void ReleaseForPerson()
{
    _operation.Release();
    Notify();
    Wake();
}

// Runs an operation that asks the supplier for the person, ahead of the
// worker's next search. It belongs to the configuration it started under,
// so a configuration change ends it, waiting or running. The supplier's
// reply can change the allowance, so the allowance is read again.
private async Task<T> Ask<T>(
    CancellationToken cancellation,
    Func<CancellationToken, Task<T>> operation
)
{
    CancellationToken requests;
    lock (_requestSync)
        requests = _requests.Token;
    using var pending = CancellationTokenSource.CreateLinkedTokenSource(
        requests,
        _lifetime.Token,
        cancellation
    );
    await TakeForPerson(pending.Token).ConfigureAwait(false);
    try
    {
        return await operation(pending.Token).ConfigureAwait(false);
    }
    finally
    {
        try
        {
            await RefreshAllowance().ConfigureAwait(false);
        }
        finally
        {
            ReleaseForPerson();
        }
    }
}

private Task Ask(CancellationToken cancellation, Func<CancellationToken, Task> operation) =>
    Ask(
        cancellation,
        async pending =>
        {
            await operation(pending).ConfigureAwait(false);
            return true;
        }
    );
```

| Caller | After |
| --- | --- |
| `Candidates` (`Choose.cs` 11) | `async`; `return await Ask(cancellation, async pending => { var target = (await Chosen(entryId, language, pending)) with { Manual = true }; return await Lookup(LookupFor(target) with { Fresh = Fresh(target, command) }, pending, choices: true); });` with `ConfigureAwait(false)` on each await |
| `Choose` (`Choose.cs` 85) | `async`; `await Ask(cancellation, async pending => { … });` The body is today's `try` block (103 to 149) with `LookupFor(target) with { Fresh = Fresh(target, command) }` for the lookup. |
| `Check` (`Acquisition.cs` 561) | `async`; the first line that reads the account stays outside. `await Ask(cancellation, async pending => { … });` The body is today's `try` block and its `catch (SubtitleException)` (575 to 607), with `pending` as the token. |
| `Rescan` (`Discovery.cs` 129) | [Section 5](#5-showscan-and-invalidate) |
| `Find` (`Acquire.cs` 839) | `await TakeForPerson(_lifetime.Token)`; `try { … } finally { ReleaseForPerson(); }`. The `Wake()` after the `finally` (898) goes. |

The three methods stay `async`, so a failure before the wait, such as
`Interpret` refusing a draft, still arrives in the returned task.

Settled differences between the copies:

- **The worker is woken after every operation.** `Candidates` and `Rescan`
  did not wake it, and `Find` woke it only when it accepted work. A wake that
  finds nothing costs the worker one query of the due jobs (`Pump`,
  `Acquire.cs` 75 to 109).
- **The views are told after every operation.** `Find` told them only
  through `RefreshCounts`.
- **`Check` reads the saved account's allowance through `RefreshAllowance`.**
  Today it reads it only when the checked account is the saved one
  (`Acquisition.cs` 612). For a draft of another account the new read returns
  the values `_allowance` already has.
- **A video that left before the operation started is `Changed`.**
  `Candidates` and `Choose` throw a cancellation for it (`Choose.cs` 33,
  107), and `Protection` throws `SubtitleException(SubtitleFailure.Changed)`
  (69). The dialog turns the cancellation into `Changed` too
  (`ChoiceDialog.Explain`, 240). `Chosen` (168) therefore returns the
  `Target` and throws `SubtitleException(SubtitleFailure.Changed)` when it
  finds none, and its three callers lose their null test. The video that
  `Chosen` returned is no longer needed: see `LookupFor`. A video that leaves
  during a request still cancels through `Validate`, which the worker's
  `catch` reads (`Acquire.cs` 463) and which `SourceChecks.ps1` 665 to 670
  requires.

### 3. `LookupFor`

Owner: `Acquisition`, in `Subtitles/Acquisition/Acquire.cs` beside `Lookup`.
The name differs from `Lookup` because the check script calls `Lookup` by
name (`SourceChecks.ps1` 545, 900, 912, 914).

```csharp
// What the supplier is asked for a target: its file's name and language at
// its stage. The lookup is valid while the target's videos are current.
private SubtitleLookup LookupFor(Target target) =>
    new(
        Path.GetFileName(target.Videos[0].FinalPath),
        target.Language,
        token => Validate(target, token)
    )
    {
        Stage = Stage(target),
        Guesses = target.Guesses,
    };

// The fresh answer the person's command asks for; none without a command.
private FreshRequest? Fresh(Target target, RetryCommand? command) =>
    command is null
        ? null
        : new(command, SearchCount(target.Videos[0].FinalPath, target.Language));
```

| Copy | After |
| --- | --- |
| `Candidates` (`Choose.cs` 35 to 45) | `LookupFor(target) with { Fresh = Fresh(target, command) }` |
| `Protection` (`Choose.cs` 70 to 77) | `LookupFor(target)` |
| `Choose` (`Choose.cs` 109 to 118) | `LookupFor(target) with { Fresh = Fresh(target, command) }` |
| `Acquire`, the saved reference (`Acquire.cs` 275 to 283) | `LookupFor(target) with { Fresh = Fresh(target) }` |
| `Acquire`, the search (`Acquire.cs` 370 to 382) | `LookupFor(target) with { Validate = ValidateLookup, Hash = hash, Size = video.Size, Stage = stage, Fresh = Fresh(target), GivesWay = () => PersonWaits }` |
| `CanLookup` (`Storage.cs` 713 to 720) | `LookupFor(target)` |

Settled differences between the copies:

- **The lookup names the target's first video.** The copies name the first
  video, the video `Chosen` found or the first video that exists on disk.
  Every caller holds a target that `Eligible` accepted, and `Eligible`
  refuses a target whose videos differ in path (`Storage.cs` 477; `Target.IsAmbiguous`,
  934). So the names are equal, except in letter case when two torrents hold
  one file. With one name the search key is the same for the worker and for
  the dialog.
- **`Stage` and `Guesses` are always set.** `Choose` and the saved-reference
  lookup set no stage, and three copies set no guesses. The download reads
  neither, and `Lookup` with `choices` returns before it reads `Guesses`
  (`Acquire.cs` 598), so no result changes.
- **`Protection` and `CanLookup` get the real `Validate`.** They pass a
  delegate that does nothing. They only build keys from the lookup
  (`Storage.cs` 741 to 766), so `Validate` is never called there.
- The search in `Acquire` keeps the stage it read from the job row (287 to
  307), which is not `Stage(target)` for a supplier that uses hashes.

### 4. `Reconfigure`, and settings writes ahead of the worker

Owner: `Acquisition`, in `Subtitles/Acquisition.cs` beside `Queue`.

| Candidate | Result |
| --- | --- |
| One member for all five, with a flag for "outdates work in progress" and a flag for "changes jobs" | Rejected. `WriteExtras` needs neither, `SaveAccount` of the same account needs only the second, and `Suspend` wakes where the others reconcile. Two flags for five callers is the function with a flag for each caller that the plan's rules forbid. |
| A thin wrapper that only takes and releases the gate | Rejected. Its interface is as large as the four lines it hides. |
| One member for the writes that change jobs; the caller invalidates first when its change outdates work in progress | **Chosen.** Three callers share five ordered steps. The call `Invalidate()` stays visible where the write decides it. |

```csharp
// Saves a configuration change that changes jobs, alone under the
// operation gate. The counts are read again before other work continues.
// Reconcile takes the gate itself, so it runs after the release.
private async Task Reconfigure(Func<Task> save)
{
    await TakeForPerson(_lifetime.Token).ConfigureAwait(false);
    try
    {
        await save().ConfigureAwait(false);
        await RefreshCounts().ConfigureAwait(false);
    }
    finally
    {
        _operation.Release();
    }
    Notify();
    await Reconcile().ConfigureAwait(false);
}
```

| Caller | After |
| --- | --- |
| `WriteEnabled` (`Acquisition.cs` 275) | `Invalidate(); await Reconfigure(async () => { await Run(…); Enabled = enabled; });` The transaction is today's (281 to 295). |
| `WriteLanguages` (329) | The validation (331 to 338) stays first. `Invalidate(); await Reconfigure(async () => { await Run(…); Languages = values; });` |
| `SaveAccount` (468) | `changed`, `username` and `secret` are computed first, and `if (changed) Invalidate();` stays. `await Reconfigure(async () => { … });` The body is today's `try` block (483 to 550) without its last line, `RefreshCounts`. |
| `WriteExtras` (383) | Stays. Its wait becomes `await TakeForPerson(_lifetime.Token)`. |
| `Suspend` (192), `Initialize` (130) | Stay as they are. |

Settled differences:

- **A settings write waits ahead of the worker.** The four writes wait with
  `_operation.WaitAsync`, so a write that does not invalidate waits for the
  worker's whole step, and the views keep Check and Edit disabled until then
  (`Settings.cs` 426, 427). `TakeForPerson` makes the worker stop before its
  next search request. The
  [matching contract](../../contracts/subtitles/matching.md#best-guess)
  lists the person's own work that goes first; a settings change joins it.
- **`WriteLanguages` tells the views after the release**, as the other two
  do. Today it relies on `RefreshCounts` and on `Queue`.

### 5. `ShowScan` and `Invalidate`

Owner: `Acquisition`, in `Subtitles/Acquisition/Discovery.cs` beside `Scan`.

```csharp
// Runs a scan as an item of the work list, which the person can stop.
private async Task<ScanResult> ShowScan(
    CancellationToken cancellation,
    Func<WorkRun, CancellationToken, Task<ScanResult>> scan
)
{
    var run = new WorkRun(this, SubtitleWork.Discovery);
    try
    {
        using var request = CancellationTokenSource.CreateLinkedTokenSource(
            cancellation,
            run.Token
        );
        var result = await scan(run, request.Token).ConfigureAwait(false);
        run.Operation.Summarize("scan_result", result.Found, result.Unreadable);
        run.Operation.Outcome = result.Unreadable > 0
            ? WorkOutcome.Failed
            : WorkOutcome.Completed;
        return result;
    }
    catch (OperationCanceledException)
    {
        run.Operation.Outcome = WorkOutcome.Cancelled;
        throw;
    }
    catch (Exception error)
    {
        run.Operation.Error = error;
        throw;
    }
    finally
    {
        await run.Finish().ConfigureAwait(false);
    }
}
```

| Caller | After |
| --- | --- |
| `Discover` (`Discovery.cs` 243) | The read of the movies and the early return (245 to 250) stay. `await ShowScan(cancellation, (run, token) => Scan(run, movies, accepted, token));` |
| `Rescan` (`Discovery.cs` 129) | Reads `_requests.Token` under `_requestSync`, then `return await ShowScan(requests, async (run, token) => { await TakeForPerson(token); try { … } finally { ReleaseForPerson(); } });` The body is today's 145 to 209, and it returns `result with { Unreadable = result.Unreadable + unreadable }`. The flag `entered` goes. |
| `Inspect` (`Discovery.cs` 103) | Stays: `Scan(null, …)` shows no item. |

Settled difference: **the work item of `Rescan` ends after the gate is
released**, not before. The
[background work contract](../../contracts/background-work.md#lifetime-and-shutdown)
asks for that order: an operation "disposes after its outer cleanup,
including … release of domain gates".

`Invalidate` (`Acquisition.cs` 641) becomes:

```csharp
private void Invalidate()
{
    lock (_requestSync)
    {
        ReportWorkWaiting();
        _requests.Cancel();
        _requests.Dispose();
        _requests = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
    }
}
```

`ReportWorkWaiting` takes the same lock again, which a monitor allows.

### 6. `Shared.Rerun`

New file `Shared/Rerun.cs`. It is in the base because `Subtitles` and
`Library` both use it, and the
[direction rule](../22-one-owner-per-rule.md#direction-between-the-app-areas)
puts a type that two features need there.

| Candidate | Result |
| --- | --- |
| A private class in `Acquisition`, used twice | Rejected. `FileFacts` would keep the third copy of the rule. |
| Members that take the caller's lock and its flags | Rejected. The interface would be the implementation. |
| A type with its own lock that the caller requests | **Chosen.** |

```csharp
namespace Syno.TinyTorrent.Shared;

/// <summary>
/// A background pass that runs again when it is requested during its run.
/// Requests made during one pass cause one more pass after it, so a burst
/// of requests starts no pass for each and never loses the last one.
/// </summary>
internal sealed class Rerun(Func<Task> pass)
{
    /// <summary>
    /// Requests a pass on the thread pool. The task completes when no
    /// requested pass is left, and fails when a pass throws; the next
    /// request then starts again.
    /// </summary>
    internal Task Request();

    /// <summary>The task of the passes requested so far; completed while none runs.</summary>
    internal Task Completion { get; }
}
```

The implementation is the loop of `ReconcileChanges` (`Storage.cs` 217 to
245) with its own lock: a flag set by `Request`, a task started with
`Task.Run` when none runs, a loop that clears the flag before each pass and
ends when the flag is clear, and a `catch` that forgets the task and
rethrows. `Request` may be called while the caller holds its own lock,
because `Request` never runs the pass on the calling thread.

`Acquisition` and `FileFacts` have primary constructors, so each creates its
`Rerun` at the first request, under the lock that already guards the request.

| Copy | After |
| --- | --- |
| `Acquisition._reconciling`, `_reconcilePending`, `ReconcileChanges` (`Storage.cs` 203 to 245) | Field `private Rerun? _reconciliation;`. `Reconcile()` keeps its lock and its test, then `return (_reconciliation ??= new(ReconcilePass)).Request();`. `ReconcilePass` awaits `ReconcileOnce()` and then calls `QueueInspect()` under `_requestSync`, as the loop does today (232 to 236). `ReconcileChanges` is deleted. |
| `Acquisition._inspecting`, `_inspectPending`, `InspectChanges` (`Discovery.cs` 14, 15, 33 to 67) | Field `private Rerun? _inspection;`. `QueueInspect` keeps its test, then `(_inspection ??= new(Inspect)).Request();`. `InspectChanges` is deleted. |
| `Acquisition.DisposeAsync` (`Acquisition.cs` 713) | `background = [_reconciliation?.Completion, _inspection?.Completion];` The loop that awaits them stays. |
| `FileFacts._work`, `_again`, `Start`, the head and tail of `Run` (`FileFacts.cs` 13, 14, 53 to 76, 259 to 264) | Field `private Rerun? _reads;`. `Start` keeps its test, then `(_reads ??= new(Run)).Request();`. `Run` loses the lines that set `_work` and `_again`; its first test becomes `if (!_connected \|\| _lifetime.IsCancellationRequested) return;`. `DisposeAsync` awaits `_reads?.Completion`, read under `_gate`. |

Each pass already begins with the test that the loops repeat today
(`ReconcileOnce`, `Storage.cs` 249; `Inspect`, `Discovery.cs` 81;
`FileFacts.Run`, 68), so a pass requested after a disconnect returns at once.

### 7. `Library.Pass<T>` and `WorkProgress.Counted`

`WorkProgress.Counted` is a static member of `WorkProgress` in
`Shared/WorkObservation.cs`:

```csharp
/// <summary>
/// The progress of counted work: a fraction of the total, or unknown
/// while nothing is counted, because a fraction needs a positive total.
/// </summary>
internal static WorkProgress Counted(int completed, int total, WorkUnit unit) =>
    total > 0 ? new Fraction(completed, total, unit) : new Unknown();
```

`Pass<T>` is a new file, `Library/Pass.cs`.

| Candidate | Result |
| --- | --- |
| One loop that owns the pending read, the work item, the turn of each target and the rerun, with delegates for the read and for one target | Rejected. The sweep shares `VideoLookup`'s work loop with the batch, serves one file for each turn of that loop, and waits out provider holds (`VideoLookup.Run`, 798; `ServeSweep`, 1249). The loop would need the batch's precedence and the holds as inputs, so its interface would be as wide as `ServeSweep`. |
| A type that also owns the `CancellableWork` | Rejected. It needs the owner's gate, its description, its cancellation source and a callback, which is six parameters, and the [ownership decisions](../../contracts/architecture.md#ownership-decisions) keep the acceptance of a stop under each feature owner's gate. |
| One long-lived object for each owner that holds the displayed entries, the cancelled targets and the current pass | Rejected. A late Cancel from an earlier pass's work item must not mark the targets of the next pass. With one object for each pass, the stop delegate holds its own pass, as [cancellation has one path](../../contracts/background-work.md#cancellation-has-one-path) requires. |
| One object for each pass, with the owner's set of cancelled targets | **Chosen.** |

```csharp
namespace Syno.TinyTorrent.Library;

/// <summary>
/// The targets that one automatic pass over the entries on screen
/// accepted, in the order it serves them. A pass accepts the pending
/// targets the person has not cancelled, leaves a target whose row
/// scrolled away to the pass that finds it on screen again, and on the
/// person's Cancel remembers the targets it did not finish. It is not
/// synchronized: its owner calls it under the gate that guards the
/// displayed entries.
/// </summary>
internal sealed class Pass<T>(IEnumerable<T> pending, HashSet<T> cancelled, Func<T, long> entry)
    where T : class
{
    /// <summary>How many targets the pass finished.</summary>
    internal int Done { get; }

    /// <summary>How many targets the pass accepted and has not left to a later pass.</summary>
    internal int Count { get; }

    internal bool IsFinished => Done == Count;

    internal WorkProgress Progress => WorkProgress.Counted(Done, Count, WorkUnit.Files);

    /// <summary>
    /// The next target on screen, or null when none is left. The targets
    /// before it that scrolled away leave the pass.
    /// </summary>
    internal T? Next(IReadOnlySet<long> displayed);

    /// <summary>Counts the target that Next returned as finished.</summary>
    internal void Advance();

    /// <summary>The person cancelled the pass: later passes skip the targets it did not finish.</summary>
    internal void Cancel();
}
```

The implementation holds `pending.Where(target => !cancelled.Contains(target))`
as a list. `Next` removes the targets at `Done` whose entry is not in
`displayed` and returns the target at `Done`. `Cancel` adds the targets from
`Done` on to `cancelled`, which includes the target in progress, as both
copies do today (`FileFacts.cs` 291; `VideoLookup.StopSweep`, 1368).

| Copy | After |
| --- | --- |
| `FileFacts.Run`, the accepted targets (`FileFacts.cs` 118 to 121) | `lock (_gate) pass = new Pass<Target>(pending, _cancelled, target => target.EntryId);` then `if (pass.Count == 0) return;` |
| `FileFacts.Run`, the work item's first progress (127) | `pass.Progress` |
| `FileFacts.Run`, the loop (133 to 222) | A loop that first checks the token, then under `_gate` takes `pass.Next(_displayed)` and reports when `Count` changed, and ends when it is null. After the save: `pass.Advance()` and `Report()` under `_gate`. The `continue` for a lost connection (209 to 211) stays: the token is cancelled then, so the check at the top of the loop ends the pass. The locals `accepted`, `skipped` and `done` go. |
| `FileFacts.Run`, the summary (249 to 254) | `Summarize("properties_result", pass.Done, pass.Count, unreadable)` |
| `FileFacts`, the local `Report` (269 to 282) | `run!.Report(run.Observation with { Progress = pass.Progress });` |
| `FileFacts`, the local `Stop` (286 to 295) | `if (stopping.TryCancel(out var signalling)) pass.Cancel();` |
| `VideoLookup.Sweep.Targets`, `Done` (1424, 1437) | One property, `Pass<VideoTarget>? Pass`. It is set in the same lock block as `Work` (`PrepareSweep`, 1212 to 1231), so a reader that has `Work` has `Pass`. |
| `VideoLookup.PrepareSweep` (1216 to 1218) | `sweep.Pass = new(pending, _cancelled, target => target.Entry.EntryId);` then `if (sweep.Pass.Count == 0) return null;` |
| `VideoLookup.ServeSweep`, the target's turn (1257 to 1269) | Under `_gate`: `target = pass.Next(_displayed);` and `ReportSweep(sweep, new WorkExecution.Waiting())` when `Count` changed |
| `VideoLookup.ServeSweep`, 1286 and 1303 | `pass.Advance()`; `if (pass.IsFinished)` |
| `VideoLookup.ReportSweep` (1343) | `Progress = pass.Progress` |
| `VideoLookup.StopSweep` (1368, 1369) | `sweep.Pass?.Cancel();` |
| `VideoLookup.FinishSweep` (1385 to 1390) | `Summarize("metadata_result", sweep.Found, pass.Done, pass.Count)` |
| `VideoLookup.ReportBatch` (751 to 765) | `Progress = WorkProgress.Counted(run.Done, run.Done + run.Queue.Count, WorkUnit.Files)` |

`VideoLookup._cancelled` (1187) and `Retry`, which clears it (428), stay.
`FileFacts._cancelled` stays and is never cleared, as the
[background work contract](../../contracts/background-work.md#registered-work)
rules: "its cancelled unchanged inputs stay suppressed for the window
lifetime".

Settled difference: **the sweep drops every target that scrolled away before
the next one on screen in one turn.** Today it drops one target for each turn
of the work loop (1263 to 1268). The files identified are the same.

## Every listed caller, mapped

| Plan item | Member it calls after the stage |
| --- | --- |
| `Choose.cs` 11, 85; `Acquisition.cs` 561 | `Ask` |
| `Discovery.cs` 131 | `ShowScan`, `TakeForPerson`, `ReleaseForPerson` |
| `Choose.cs` 35, 70, 109; `Acquire.cs` 275, 370; `Storage.cs` 713 | `LookupFor` |
| `Acquisition.cs` 275, 329, 468 | `Reconfigure` |
| `Acquisition.cs` 192, 383 | Stay, with the reasons in [gate step 1](#gate-step-1-the-findings-read-again) |
| `Discovery.cs` 129, 243 | `ShowScan` |
| `Acquisition.cs` 641 | `ReportWorkWaiting` |
| `Discovery.cs` 33; `Storage.cs` 203 | `Rerun.Request` |
| `Batch.cs` 85; `Settings.cs` 304; `Automatic.cs` 40 | `Attachment.Attach`, `Attachment.SetVisible` |
| `Batch.cs` 258, 286; `Settings.cs` 569, 592, 628; `Automatic.cs` 77 | `Attachment.Run` |
| `VideoLookup.cs` 751 | `WorkProgress.Counted` |
| `VideoLookup.cs` 1199, 1343; `FileFacts.cs` 118, 267 | `Pass<T>` |

## Gate step 4: consumers of every changed name

Searched: `app/src` with the capture journeys, and `app/tests/*.ps1`.

| Name | Consumers | Change |
| --- | --- | --- |
| `Acquisition._inspecting` | `Discovery.cs` 14, 40, 53, 64; `Acquisition.cs` 713; `SourceChecks.ps1` 1238 (`Wait-Inspection`) | The field is `_inspection`, a `Rerun`. The script reads that field and awaits its `Completion` property. |
| `Acquisition._reconciling` | `Storage.cs` 203, 213, 227, 242; `Acquisition.cs` 713 | `_reconciliation`. No script reads it; `SourceChecks.ps1` 1307 invokes `Reconcile` and awaits the task, which stays. |
| `Acquisition._inspectPending`, `_reconcilePending`, `InspectChanges`, `ReconcileChanges` | Their own files only | Deleted |
| `Acquisition.Chosen` | `Choose.cs` 30, 68, 104 | Returns `Target`; throws `Changed` |
| `Acquisition._operation` | The members of `Acquisition`; `SourceChecks.ps1` 839, 840, 889 take and release it as a `SemaphoreSlim` | Stays a `SemaphoreSlim` with that name |
| `Acquisition._account`, `_deletions`, `SearchIntent` | `SourceChecks.ps1` 549, 697, 711, 933; 1321; 847, 881 | Stay |
| Methods of `Acquisition` that `SourceChecks.ps1` calls by name: `Initialize`, `Load`, `SetConnected`, `SetDisplayed`, `SetEnabled`, `SetLanguages`, `SelectSupplier`, `Edit`, `Save`, `Interpret`, `Answered`, `Targets`, `SearchKey`, `SearchCount`, `CanLookup`, `Protection`, `ResetProtection`, `Lookup`, `Accept`, `Acquire`, `Record`, `Recorded`, `Count`, `Find`, `Candidates`, `Choose`, `Reconcile`, `Rescan`, `CaptureDeletion`, `Delete` | Lines 510 to 1517 | Names, parameters and results stay. `Candidates`, `Choose` and `Find` keep their parameters. |
| `SubtitleLookup` | `Supplier.cs` 549; the three suppliers; `SourceChecks.ps1` 530, 897, 911 build it with its constructor | The record does not change |
| `Batch`, `Settings`, `Automatic`: `Attach`, `SetVisible`, `Refresh`, `Act`, `CanAct`, `Field`, `Coverage` | `Settings/SettingsPage.xaml.cs` 104 to 109, 350, 351, 546, 805 to 810; `Settings/SettingsPage/Subtitles.cs` 15, 27 to 32; `Settings/SettingsPage/Home.cs` 57; `Shell/MainWindow/Subtitles.cs` 17 to 57; `CaptureSettings.cs` 296, 847; `CaptureLibraryFiles.cs` 517 | The three hosts change as [section 1](#1-subtitlesattachment) lists. `Automatic.SetVisible` is new. The capture journeys do not change. |
| `Settings.Save(Task)`, `Settings.CanReport`, the three `OnChanged` | Their own files only | Deleted |
| `FileFacts._work`, `_again` | `FileFacts.cs` only | Replaced by `_reads` |
| `FileFacts` constructor, `SetConnected`, `SetDisplayed`, `Changed`, `Failed` | `Shell/Features.cs` 171; `Library/Browser.cs` 477 to 520, 699, 709, 794, 1297 to 1323 | Stay |
| `VideoLookup.Sweep.Targets`, `Sweep.Done` | `VideoLookup.cs` 1216 to 1218, 1260 to 1265, 1286, 1303, 1350 to 1353, 1368, 1387 to 1389 | `Sweep.Pass` |
| `VideoLookup._batch`, `BatchRun.Queue`, `BatchRun.Work` | `SourceChecks.ps1` 1128 to 1146 | Stay. The script requires that a batch of one file shows a total of 1 (1134); `Counted(0, 1, …)` is that fraction. |
| `WorkProgress` | 11 construction sites of `Fraction` in 8 files | `Counted` is added. Four sites go (`FileFacts.cs` 127, 275; `VideoLookup.cs` 757, 1351): `Pass.Progress` and `ReportBatch` call `Counted`. `Shared/Network/ProviderHttp.cs` 179 and `Torrents/AddDraft.cs` 528 choose between other cases and stay. |

New names, with the check that [naming](../../agents/naming.md) asks for:

- `Attachment`: the noun of `Attach`, which the views and the
  [configuration contract](../../contracts/subtitles/configuration.md)
  already use. WinUI has no type of that name in the namespaces the views
  import.
- `Rerun`: the plan's word for the rule. `Request` is the act; `Completion`
  is the .NET word that `CancellableWork.Completion` already uses.
- `Pass`: the [background work contract](../../contracts/background-work.md#registered-work)
  calls this work "a finite enrichment pass" and "each nonempty pass". The
  private `Sweep` keeps its name: it is the metadata pass with its provider
  context.
- `Ask`, `Reconfigure`, `LookupFor`, `ShowScan`, `ReleaseForPerson`,
  `Counted`: one or two words each. `ReleaseForPerson` mirrors
  `TakeForPerson`. `ShowScan` uses "show" as the file's comments do for
  presence in the work list.

No new domain term enters [CONTEXT.md](../../../CONTEXT.md): the six names
are structure of the code, and the text a person reads does not change.

## Files the implementer may edit

Product code:

- `Shared/Rerun.cs` (new), `Shared/WorkObservation.cs`
- `Library/Pass.cs` (new), `Library/FileFacts.cs`, `Library/VideoLookup.cs`
  (`ReportBatch`, `PrepareSweep`, `ServeSweep`, `ReportSweep`, `StopSweep`,
  `FinishSweep`, `Sweep`)
- `Subtitles/Attachment.cs` (new), `Subtitles/Batch.cs`,
  `Subtitles/Settings.cs`, `Subtitles/Automatic.cs`
- `Subtitles/Acquisition.cs`, `Subtitles/Acquisition/Choose.cs`,
  `Subtitles/Acquisition/Acquire.cs`, `Subtitles/Acquisition/Discovery.cs`,
  `Subtitles/Acquisition/Storage.cs`
- `Settings/SettingsPage.xaml.cs` (`Release`),
  `Settings/SettingsPage/Subtitles.cs` (`RefreshSubtitles`)
- `Shell/MainWindow/Subtitles.cs` (`DownloadSubtitles`)

Capture journeys: none.

Test scripts: `app/tests/SourceChecks.ps1`, the function `Wait-Inspection`
(1237 to 1240).

Documents: `docs/contracts/architecture.md`,
`docs/contracts/background-work.md`,
`docs/contracts/subtitles/configuration.md`,
`docs/contracts/subtitles/matching.md`.

No catalogue under `Resources` changes. A file that is not in this list is
edited only when the compiler names it, and the batch report says which.

Stage 8 edits `Library/VideoLookup.cs` (`ServeBatch`, `ServeSweep`'s `catch`
blocks) and one line of `Acquire.cs` (`Lookup`) first. Stage 2 edits
`Subtitles/Batch.cs` (`Waiting`) and `Subtitles/Acquisition.cs`
(`SubtitleException`) first. This stage changes other members of those
files.

## Contract changes, as proposed text

**`docs/contracts/architecture.md`, "Ownership decisions"**, four new items:

> - [Bridge](../../app/src/Subtitles/Bridge.cs) connects each view of
>   subtitle work to its `Acquisition`. It listens only while the view is
>   shown, refreshes the view once for a burst of changes, and runs the
>   actions the person starts there. A failed action is reported in the
>   window unless subtitle work closed or was replaced meanwhile. A host
>   hides a view and never detaches it, so the failure of an action is
>   reported also after the person left the view. The three views have no
>   common base class, because no host treats them as one. `Acquisition`
>   does not report these failures itself, because the choice and account
>   dialogs show a failure inside the dialog.
> - In `Acquisition`, each operation the person starts takes the operation
>   gate through `TakeForPerson` and ends through `ReleaseForPerson`. Check,
>   the choice list and the chosen download run through `Ask`: they belong
>   to the configuration they started under, so a configuration change ends
>   them. Find and a settings write act on the current configuration, so
>   only the window's close ends their wait. `Reconfigure` saves a change
>   that changes jobs; the extras count changes none and saves by itself.
> - [Pass](../../app/src/Library/Pass.cs) holds the targets that one
>   automatic pass over the entries on screen accepted, for the metadata
>   sweep and the file-properties read. Each owner keeps its work item and
>   accepts a stop under its own gate. One loop for both was rejected,
>   because the sweep shares its work loop with the batch and waits out
>   provider holds.
> - [Rerun](../../app/src/Shared/Rerun.cs) runs a background pass again
>   when it is requested during its run. Subtitle reconciliation, the check
>   of displayed movies and the file-properties read use it. The metadata
>   sweep does not, because `VideoLookup.Refresh` decides whether a new
>   sweep may start.

**`docs/contracts/architecture.md`**, the table of owners that the plan's
[done condition](../22-one-owner-per-rule.md#done-when) adds: the row
"Subtitle operations | `Subtitles.Acquisition`" stays, and three rows join
it.

> | A subtitle view's subscription and actions | `Subtitles.Bridge` |
> | An automatic pass over the entries on screen | `Library.Pass` |
> | A background pass that runs again on request | `Shared.Rerun` |

**`docs/contracts/background-work.md`, "Registered work"**, the paragraph
"For automatic metadata and file-facts passes". The sentence "This policy
lives at the feature owner, with no general revision framework in the
registry." becomes:

> This policy lives at the feature owner, with no general revision
> framework in the registry: Library's `Pass` holds it once for both
> passes, and each pass's owner keeps the set of cancelled members.

**`docs/contracts/subtitles/configuration.md`, "Discovery and permission"**,
after "Failures are reported through the window's feedback, because the
status line holds state, not events.":

> The failure of Find, Stop, Check, Scan or a settings change is reported
> also after the person left the view. It is dropped only when subtitle
> work closed or was replaced, for example by a database reset, because it
> then describes work that no longer exists.

**`docs/contracts/subtitles/matching.md`, "Best guess"**, the paragraph "The
person's own subtitle work goes first". Its first sentence becomes:

> The person's own subtitle work goes first: the Download Subtitle dialog's
> search and download, Find, Scan torrent folders, the account Check and a
> change of the subtitle settings.

## What changes for a person

- A failed Find or Stop is no longer reported when subtitle work closed or
  was replaced while it ran. This is the plan's "Settled as" choice.
- The Subtitles card and the Home switch refresh once for a burst of
  changes. The plan settles this too. A batch that runs while the card is
  shown no longer refreshes the card for each change.
- A change of the extras count, or a Save of an unchanged account, takes
  effect when the worker reaches its next search request. Today it waits
  for the worker's whole step, and Check and Edit stay disabled until then.
  This is a gate decision.

Nothing else is visible. The worker is woken after `Candidates` and `Rescan`
and finds nothing new. The views are told once more after `Find`. A rescan's
work item ends an instant later, after the gate is released.

## What stays as it is

- `Suspend`, `Initialize` and `WriteExtras`, with the reasons in
  [gate step 1](#gate-step-1-the-findings-read-again).
- `VideoLookup`'s `_again`, `Wake` and `Run` (781 to 828). They serve the
  batch and the sweep in one loop that waits for holds; they are not a
  rerun.
- `Acquisition.Pump` and `_signal` (`Acquire.cs` 43 to 224). It is the
  worker, which waits for a time or a wake.
- `Batch.Recount` (`Batch.cs` 166). It discards an outdated count by a
  version; it is not a rerun of a pass.
- `StopWork` (`Acquisition/Work.cs` 159). The item's Cancel already cancels
  the worker's step through the run's token, so it needs no wait ahead of
  the worker.
- `Features.Post` (`Shell/Features.cs` 123), which posts each `Changed` of
  `Acquisition` to the window without joining a burst. It is `Shell`'s
  subscription for five sources and is outside this stage.
- The rerun loops on the UI thread in `Shell`: `Features.RefreshSource`
  (287), `MainViewModel` (`Actions.cs` 271, `Selection.cs` 33) and
  `Navigation` (62). `Rerun` runs a pass on the thread pool; these run on
  the UI thread and are outside this stage.

## Done when

- A search of `Subtitles` finds `Changed +=` only in `Bridge.cs`, and
  finds `_operation.Release()` only in `ReleaseForPerson`, `Reconfigure`,
  `WriteExtras`, `Suspend`, `Initialize`, `Pump`, `ReconcileOnce`,
  `StopWork`, `DisposeAsync` and the two places that record a folder
  (`Inspect`, `Scan`).
- A search of `Subtitles/Acquisition` finds `new SubtitleLookup(` only in
  `LookupFor`, and `new WorkRun(this, SubtitleWork.Discovery)` only in
  `ShowScan`.
- A search of `Subtitles` and `Library/FileFacts.cs` finds none of
  `_inspectPending`, `_reconcilePending` and `_again`.
- `Library/VideoLookup.cs` and `Library/FileFacts.cs` contain no
  `new WorkProgress.Fraction(`, no `_cancelled.Contains` and no
  `_cancelled.Add`.
- The contract text above is in the four contracts.

## Cost

No build in this session. The stage joins batch C's one app build with
`-Capture`, with stages 8 and 9.

Checks on that build whose subject the stage changes:
`app/tests/SourceChecks.ps1`, which covers the person's operations, the
scans, reconciliation, the check of displayed movies and the metadata batch.
Capture modes whose journeys run changed code: `subtitle-tasks` and
`settings-layout` (the Subtitles card and the Home switch) and
`library-files` (the batch dialog, the file-properties read and the sweep).
Under [testing](../../agents/testing.md) the lead runs a mode only for an
open question; reading leaves the two under
[What reading could not settle](#what-reading-could-not-settle).

About five hours of agent work: three for the change, one for the reviewer's
findings, one for the check script and the contracts. The gate took one
session of the strongest model.

## What reading could not settle

- **Whether stage 4's "once per turn" owner accepts a request from a worker
  thread.** Stage 4 has no gate record. The rule under
  [gate step 2](#gate-step-2-the-questions-the-gate-had-to-answer) decides
  when the implementer sees the built type.
- **Whether a view's `Unloaded` and a later `Loaded` always pass through
  `SettingsPage.RefreshSubtitles`.** The design keeps today's dependence:
  after `Unloaded` hides a view, only its host shows it again. Reading found
  the eight calls of `RefreshSubtitles` (`Settings/SettingsPage.xaml.cs` 88
  to 590) and no path that loads the card without one, but did not trace
  every navigation.
- **The order in which the gate serves its waiters.** `TakeForPerson`
  relies on the gate serving a waiting person before the worker that
  returns to wait, as today's `Check`, `Candidates`, `Choose`, `Rescan` and
  `Find` already do (`Acquire.cs` 195 to 201). The stage adds the settings
  writes to that dependence and does not change it.

## Lead decision on a name, 12 November 2026

The new owner of a view's subscription and action runner is not named
`Attachment`: in a subtitle context the word reads as a file embedded in a
video. The implementer chooses a noun for the role, a view's live
connection to subtitle work, through the naming review, and records it here.

**Chosen: `Subtitles.Bridge`**, in `Subtitles/Bridge.cs`, held by each view
as `_bridge`. Everywhere this record says `Attachment`, read `Bridge`; the
contract text above already says it.

The naming review:

1. The concept is a view's two-way connection to the acquisition it shows:
   the model's changes cross to the view as one refresh for a burst, and
   the person's actions cross to the model, with one rule for their
   failures. Neither .NET nor WinUI has a type for this role.
2. The busiest use sites are inside the three views: `_bridge.Model`,
   `_bridge.IsVisible`, `_bridge.Attach(model)`, `_bridge.SetVisible(visible)`
   and `_bridge.Run(model => model.SetExtras(extras), "failed_settings")`.
3. The name must carry that it connects the view and its model in both
   directions. The namespace says it is subtitle work, and the type of
   `Model` says what it connects to.
4. The one word repeats neither the namespace nor a member.
5. It has no word to remove.
6. No qualifier accumulates.

Rejected nouns, each for a reading that is already taken here: `Link` (a
supplier's download link, in [matching](../../contracts/subtitles/matching.md)),
`Connection` (the engine connection, `Acquisition.SetConnected`),
`Subscription` (the pipe API's subscriptions, and only half the role),
`Binding` (WinUI's `Microsoft.UI.Xaml.Data.Binding`), `Session` (a supplier
sign-in), `Watcher` (`FileSystemWatcher`, which reads as subtitle file
discovery), `Relay` (`RelayCommand`), `Presenter` (it would suggest that the
presentation moved out of the view) and `Observer` (the product vocabulary
avoids "observation" for its snapshots, and it names only half the role).
No type named `Bridge` exists in the repository or in the WinUI namespaces
the views import.

## Implementation notes, 12 November 2026

The stage is implemented as this record designs it, with the two lead
decisions. Nothing was built or run. The focused review passes were done
on the diff by reading. The "Done when" searches hold, with `Bridge.cs` in
place of `Attachment.cs`.

### Settled while implementing

- **Stage 4's owner accepts a request from any thread.**
  `Shared/TurnCallback.cs` states it, and `Request` uses only
  `Interlocked.Exchange` and `DispatcherQueue.TryEnqueue`. So `Bridge` owns
  one `TurnCallback` at `Normal` priority, the priority the views used, and
  the interlocked flag of `Batch.OnChanged` is gone.
- **The close (lead decision 1).** `Acquisition.DisposeAsync` first cancels
  `_requests` under `_requestSync` and does not replace it. That ends the
  person's Check, choice search, download and Scan, the worker's step, a
  reconciliation and the check of displayed movies, waiting or running, and
  a Find that gets the gate reads the cancelled token at once. Then it
  awaits the queued settings writes, which wait with `_lifetime.Token` and
  are still saved. The second `_requests.Cancel()` after `_lifetime.Cancel()`
  is gone, because every `_requests` that a write's `Invalidate` creates
  afterwards is linked to `_lifetime`.

### Differences from the record

- The owner is `Bridge`; see the lead decision above.
- `Candidates`, `Choose` and `Rescan` are not `async`; they return the task
  of `Ask` or `ShowScan`. Both are `async`, so a failure before the wait
  still arrives in the returned task, which is the record's reason. `Check`
  stays `async`, because `Interpret` runs before `Ask`.
- `Settings.Check` has no private `Checked(model)` method. The second `Run`
  takes an inline lambda that tests `CanCheck`, awaits `model.Check()` and
  keeps the one `catch` with its comment, so no method is named with a
  participle.
- `Settings.Scan` keeps `_bridge.Model is null` in its guard, because
  `_scanning` is set before `Run`, and `Run` with no model would leave it
  set.
- `Fresh(Target, RetryCommand?)` sits beside the existing `Fresh(Target)` in
  `Acquire.cs`, not beside `LookupFor`, so both forms of the fresh answer
  are in one place.
- `Pass<T>` has an explicit constructor, not a primary one, because its
  `cancelled` is read both by a field initializer and by `Cancel`, and no
  build was allowed to show whether that raises a compiler warning.
  `Pass.Next` removes the targets that scrolled away with one `RemoveRange`.
- `FileFacts.Run` keeps the `continue` for a lost connection, with a comment
  that the token is cancelled then, so the next turn ends the pass instead
  of serving the same target again.
- The architecture's second ownership item says that a configuration change
  does not end the wait of Find or a settings write, and adds the order of
  the close. The proposed text said "only the window's close ends their
  wait", which lead decision 1 made inexact for settings writes.
- **The architecture has no table of owners yet.** The plan keeps it until
  its done step moves it into the architecture. These three rows join the
  row "Subtitle operations | `Subtitles.Acquisition`" then:

  | Concept | Owner |
  | --- | --- |
  | A subtitle view's subscription and actions | `Subtitles.Bridge` |
  | An automatic pass over the entries on screen | `Library.Pass` |
  | A background pass that runs again on request | `Shared.Rerun` |

### What changes for a person, in addition to the list above

- Closing subtitle work, at the window's close, a database reset or a
  recovery, no longer waits for a running Scan torrent folders or a
  supplier request when a settings change is queued behind it. The scan
  ends as Cancelled, and the setting is saved.
- Leaving Settings no longer clears the Subtitles card. Its last scan result
  and its last Find result show again when the person comes back, while
  subtitle work stays the same, and the movie counts show their last values
  until they are counted again instead of 0.

### Files changed

- New: `app/src/Subtitles/Bridge.cs`, `app/src/Shared/Rerun.cs`,
  `app/src/Library/Pass.cs`.
- Changed: `Shared/WorkObservation.cs`, `Library/FileFacts.cs`,
  `Library/VideoLookup.cs`, `Subtitles/Batch.cs`, `Subtitles/Settings.cs`,
  `Subtitles/Automatic.cs`, `Subtitles/Acquisition.cs`,
  `Subtitles/Acquisition/Choose.cs`, `Subtitles/Acquisition/Acquire.cs`,
  `Subtitles/Acquisition/Discovery.cs`, `Subtitles/Acquisition/Storage.cs`,
  `Settings/SettingsPage.xaml.cs` (`Release`),
  `Settings/SettingsPage/Subtitles.cs` (`RefreshSubtitles`),
  `Shell/MainWindow/Subtitles.cs` (`DownloadSubtitles`),
  `app/tests/SourceChecks.ps1` (`Wait-Inspection`), the four contracts and
  this record. No file outside the record's list changed.

### What reading could not settle

- The two items under
  [What reading could not settle](#what-reading-could-not-settle) about
  `Unloaded` and the order of the gate's waiters stay open.
- A settings write that changes jobs ends with a reconciliation, and a write
  queued at the close still runs it before the close ends, with a fresh
  request token. It reads the database and scans only the torrents that
  just finished, so it is normally short; the stage leaves it, because it
  needs a change of the automatic switch, the languages or the supplier in
  the moment of the close.
- Whether the change compiles, and every behavior above, waits for batch C's
  build, `SourceChecks.ps1` and the capture modes `subtitle-tasks`,
  `settings-layout` and `library-files`.
