using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Syno.TinyTorrent.Helpers;
using Syno.TinyTorrent.Models;
using Syno.TinyTorrent.Views;
using Windows.Graphics;

namespace Syno.TinyTorrent;

public sealed partial class MainWindow
{
    private async Task CaptureEdits(Torrent target, List<object> outcomes, List<string> completed)
    {
        Model.SelectLanguage("en");
        await CaptureReady(Model, () => Model.CanClose && Model.Text.Language == "en");
        await Model.SelectTheme("light");
        await CaptureReady(Model, () => Model.CanClose && Model.Theme == "light");
        var scale = Root.XamlRoot.RasterizationScale;
        var minimum = ((Microsoft.UI.Windowing.OverlappedPresenter)AppWindow.Presenter).PreferredMinimumWidth ?? 0;
        AppWindow.Resize(new SizeInt32(Math.Max((int)(1040 * scale), minimum), (int)(680 * scale)));
        await ShowTorrents();
        Model.CloseInspector();
        Torrents.Selection = new Syno.TableView.Selection([target], target);
        await SelectTorrent();

        var preferences = Model.Preferences;
        if (preferences.IsEditing || preferences.IsPending || preferences.Fields.Any(field => field.HasDraft))
            throw new InvalidOperationException("The edit review requires confirmed fixture preferences.");
        var rate = preferences.Download;
        var port = preferences.Port;
        var originalRate = rate.Input;
        var originalPort = port.Input;
        var originalPeriods = preferences.Periods.ToArray();
        SchedulePeriod? created = null;
        Task<bool>? departure = null;

        bool PeriodsRetained() => preferences.Periods.Count == originalPeriods.Length &&
            preferences.Periods.Zip(originalPeriods).All(pair => pair.First.Matches(pair.Second));

        async Task<TextBox> Edit(Preference field, PreferenceSection section, string input)
        {
            await ShowPreferences(new(section, field.Name));
            await CaptureLayout();
            var form = _preferencesForm ?? throw new InvalidOperationException("The edit review preferences did not open.");
            var number = CaptureElements(form).OfType<NumberBox>().Single(control => ReferenceEquals(control.Tag, field));
            var editor = TextEditor.Find(number) ?? throw new InvalidOperationException("The native numeric editor did not load.");
            editor.Focus(FocusState.Programmatic);
            editor.Text = input;
            await CaptureLayout();
            if (field.Input != input) throw new InvalidOperationException("The native numeric edit did not reach its preference.");
            return editor;
        }

        async Task<bool> Leave(string? button = null, string? scene = null)
        {
            departure = ShowTorrents();
            await CaptureLayout();
            if (button is null)
            {
                if (_closePrompt is not null)
                    throw new InvalidOperationException("A numeric preference unexpectedly requested a draft decision.");
            }
            else
            {
                var prompt = _closePrompt ?? throw new InvalidOperationException("Leaving a schedule draft did not ask for a decision.");
                await CapturePage(scene ?? throw new InvalidOperationException("The draft decision has no capture name."));
                CaptureInvoke(CaptureElements(prompt).OfType<Button>().Single(control => control.Name == button));
            }
            var navigated = await departure.WaitAsync(TimeSpan.FromSeconds(20));
            departure = null;
            await CaptureLayout();
            if (_closePrompt is not null) throw new InvalidOperationException("The completed departure retained a decision prompt.");
            return navigated;
        }

        async Task<SchedulePeriod> AddPeriod()
        {
            await ShowPreferences(new(PreferenceSection.Schedule));
            await CaptureLayout();
            var form = _preferencesForm ?? throw new InvalidOperationException("The native schedule did not open.");
            CaptureInvoke(CaptureElements(form).OfType<Button>().Single(control => AutomationProperties.GetAutomationId(control) == "AddPeriod"));
            await CaptureLayout();
            var draft = preferences.Draft ?? throw new InvalidOperationException("The native Add period did not open its editor.");
            CaptureElements(form).OfType<TimePicker>().Single(control => AutomationProperties.GetAutomationId(control) == "PeriodStart").SelectedTime = TimeSpan.FromMinutes(127);
            CaptureElements(form).OfType<TimePicker>().Single(control => AutomationProperties.GetAutomationId(control) == "PeriodEnd").SelectedTime = TimeSpan.FromMinutes(151);
            foreach (var day in draft.Days)
            {
                var control = CaptureElements(form).OfType<CheckBox>().Single(control => AutomationProperties.GetAutomationId(control) == day.AutomationId);
                if (control.IsChecked == (day.Index == 6)) continue;
                if (FrameworkElementAutomationPeer.CreatePeerForElement(control).GetPattern(PatternInterface.Toggle) is not IToggleProvider toggle)
                    throw new InvalidOperationException("The native schedule day does not expose Toggle.");
                toggle.Toggle();
            }
            await CaptureLayout();
            if (draft.Start?.TotalMinutes != 127 || draft.End?.TotalMinutes != 151 || draft.IsPaused ||
                !draft.Days.Where(day => day.IsChecked).Select(day => day.Index).SequenceEqual(new[] { 6 }))
                throw new InvalidOperationException("The native schedule edits did not retain the chosen period.");
            var period = new SchedulePeriod(preferences, draft);
            if (originalPeriods.Any(existing => existing.Matches(period)))
                throw new InvalidOperationException("The review period already exists in the fixture schedule.");
            return period;
        }

        try
        {
            var valid = originalRate == "64" ? "65" : "64";
            await Edit(rate, PreferenceSection.Transfers, valid);
            await CapturePage("edits-valid-rate-before-leaving");
            if (!await Leave() || Model.Page != WindowPage.Torrents || rate.Input != valid || rate.HasDraft || rate.IsPending || rate.Message.Length != 0)
                throw new InvalidOperationException("Leaving a valid native rate edit did not apply it and navigate.");
            outcomes.Add(new { journey = "valid rate departure", applied = true, navigated = true, prompted = false, input = rate.Input });
            completed.Add("edits-valid-rate-departure");

            var invalidEditor = await Edit(port, PreferenceSection.Network, "70000");
            await CapturePage("edits-invalid-port-before-leaving");
            if (!await Leave() || Model.Page != WindowPage.Torrents || port.Input != originalPort || invalidEditor.Text != originalPort || port.HasDraft || port.Message.Length != 0)
                throw new InvalidOperationException("Leaving an invalid native port edit did not restore the saved value and navigate.");
            outcomes.Add(new { journey = "invalid port departure", restored = true, nativeTextRestored = true, navigated = true, prompted = false });
            completed.Add("edits-invalid-port-departure");

            var concurrent = rate.Input == "96" ? "97" : "96";
            await Edit(rate, PreferenceSection.Transfers, concurrent);
            await CapturePage("edits-pending-rate-before-leaving");
            var save = preferences.Commit(rate);
            var pending = rate.IsPending;
            departure = ShowTorrents();
            await Task.WhenAll(save, departure).WaitAsync(TimeSpan.FromSeconds(20));
            var navigated = await departure;
            departure = null;
            await CaptureLayout();
            if (!pending || !navigated || Model.Page != WindowPage.Torrents || _closePrompt is not null ||
                rate.Input != concurrent || rate.HasDraft || rate.IsPending || rate.Message.Length != 0)
                throw new InvalidOperationException("The first departure during an active numeric save did not await it and navigate.");
            outcomes.Add(new { journey = "pending rate departure", pendingObserved = pending, applied = true, firstDepartureNavigated = true, prompted = false });
            completed.Add("edits-pending-rate-departure");

            var unsavedEditor = await Edit(rate, PreferenceSection.Transfers, "128");
            var first = preferences.Commit(rate);
            if (!rate.IsPending) throw new InvalidOperationException("The newer-input review did not start a pending save.");
            rate.Input = "129";
            await first.WaitAsync(TimeSpan.FromSeconds(20));
            await CaptureLayout();
            if (rate.Input != "129" || unsavedEditor.Text != "129" || !rate.HasDraft || rate.IsPending || rate.Message.Length != 0)
                throw new InvalidOperationException($"A pending acknowledgement did not retain draft 129: input={rate.Input}, nativeText={unsavedEditor.Text}, draft={rate.HasDraft}, pending={rate.IsPending}, error={rate.Message}.");
            await CapturePage("edits-pending-newer-input-retained");
            rate.Cancel();
            await CaptureLayout();
            if (rate.Input != "128" || unsavedEditor.Text != "128" || rate.HasDraft)
                throw new InvalidOperationException($"Cancel did not expose confirmed 128: input={rate.Input}, nativeText={unsavedEditor.Text}, draft={rate.HasDraft}.");
            outcomes.Add(new { journey = "pending acknowledgement with newer input", layer = "preference owner with native display", newerInputRetained = true, newerInputStayedDraft = true, confirmedInput = rate.Input });
            completed.Add("edits-pending-newer-input");

            var queuedEditor = await Edit(rate, PreferenceSection.Transfers, "256");
            var active = preferences.Commit(rate);
            if (!rate.IsPending) throw new InvalidOperationException("The queued-input review did not start a pending save.");
            rate.Input = "257";
            var queued = preferences.Commit(rate);
            rate.Input = "258";
            await Task.WhenAll(active, queued).WaitAsync(TimeSpan.FromSeconds(20));
            await CaptureLayout();
            if (rate.Input != "258" || queuedEditor.Text != "258" || !rate.HasDraft || rate.IsPending || rate.Message.Length != 0)
                throw new InvalidOperationException($"A queued explicit save did not retain draft 258: input={rate.Input}, nativeText={queuedEditor.Text}, draft={rate.HasDraft}, pending={rate.IsPending}, error={rate.Message}.");
            await CapturePage("edits-queued-save-newer-input-retained");
            rate.Cancel();
            await CaptureLayout();
            if (rate.Input != "257" || queuedEditor.Text != "257" || rate.HasDraft)
                throw new InvalidOperationException($"Cancel did not expose explicitly confirmed 257: input={rate.Input}, nativeText={queuedEditor.Text}, draft={rate.HasDraft}.");
            outcomes.Add(new { journey = "explicit save during pending acknowledgement", layer = "preference owner with native display", explicitSubmissionSaved = true, laterInputRetained = true, laterInputStayedDraft = true, confirmedInput = rate.Input });
            completed.Add("edits-pending-explicit-save");

            var cancelEditor = await Edit(rate, PreferenceSection.Transfers, "512");
            var cancelledSave = preferences.Commit(rate);
            if (!rate.IsPending) throw new InvalidOperationException("The pending Cancel review did not start a save.");
            rate.Cancel();
            await cancelledSave.WaitAsync(TimeSpan.FromSeconds(20));
            await CaptureLayout();
            if (rate.Input != "512" || cancelEditor.Text != "512" || rate.HasDraft || rate.IsPending || rate.Message.Length != 0)
                throw new InvalidOperationException($"Cancel during a pending save did not follow confirmed 512: input={rate.Input}, nativeText={cancelEditor.Text}, draft={rate.HasDraft}, pending={rate.IsPending}, error={rate.Message}.");
            await CapturePage("edits-pending-cancel-confirmed");
            outcomes.Add(new { journey = "Cancel during pending acknowledgement", followedConfirmation = true, retainedDraft = false, confirmedInput = rate.Input });
            completed.Add("edits-pending-cancel");

            await Edit(rate, PreferenceSection.Transfers, originalRate);
            await preferences.Commit(rate);
            if (rate.Input != originalRate || rate.HasDraft) throw new InvalidOperationException("The original rate was not restored.");

            var added = await AddPeriod();
            created = added;
            await CapturePage("edits-period-save-before-leaving");
            if (!await Leave("PrimaryButton", "edits-period-save-prompt") || Model.Page != WindowPage.Torrents || preferences.IsEditing ||
                preferences.Periods.Count != originalPeriods.Length + 1 || !preferences.Periods.Any(period => period.Matches(added)) ||
                !originalPeriods.All(existing => preferences.Periods.Any(period => period.Matches(existing))))
                throw new InvalidOperationException("Save on departure did not retain the new period and the original schedule.");
            outcomes.Add(new { journey = "schedule departure Save", saved = true, navigated = true, days = added.Days, start = added.Start, end = added.End });
            completed.Add("edits-period-save-departure");
            await ShowPreferences(new(PreferenceSection.Schedule));
            preferences.Select(preferences.Periods.Single(period => period.Matches(added)));
            await CapturePage("edits-period-saved");
            await preferences.Remove(preferences.Periods.Single(period => period.Matches(added)));
            if (!PeriodsRetained()) throw new InvalidOperationException("Removing the created review period changed the original schedule.");
            created = null;

            await AddPeriod();
            if (!await Leave("SecondaryButton", "edits-period-discard-prompt") || Model.Page != WindowPage.Torrents || preferences.IsEditing || !PeriodsRetained())
                throw new InvalidOperationException("Discard on departure changed the saved schedule or failed to navigate.");
            outcomes.Add(new { journey = "schedule departure Discard", originalPeriodsRetained = true, navigated = true, editorClosed = true });
            completed.Add("edits-period-discard-departure");

            var cancelled = await AddPeriod();
            var retained = preferences.Draft;
            if (await Leave("CloseButton", "edits-period-cancel-prompt") || Model.Page != WindowPage.Preferences ||
                preferences.Draft != retained || retained is null || !retained.HasChanges ||
                !new SchedulePeriod(preferences, retained).Matches(cancelled) || !PeriodsRetained())
                throw new InvalidOperationException("Cancel on departure lost the native draft or left Settings.");
            await CapturePage("edits-period-cancel-retained");
            outcomes.Add(new { journey = "schedule departure Cancel", stayedInSettings = true, draftRetained = true, exactPeriodRetained = true, originalPeriodsRetained = true });
            completed.Add("edits-period-cancel-departure");
        }
        finally
        {
            _closePrompt?.Hide();
            if (departure is not null) await departure.WaitAsync(TimeSpan.FromSeconds(20));
            await CaptureReady(preferences, () => !preferences.IsPending);
            if (preferences.IsEditing) Run(preferences.CancelPeriod);
            if (created is { } saved && preferences.Periods.SingleOrDefault(period => period.Matches(saved)) is { } remaining)
                await preferences.Remove(remaining);
            port.Cancel();
            rate.Cancel();
            if (rate.Input != originalRate)
            {
                await Edit(rate, PreferenceSection.Transfers, originalRate);
                await preferences.Commit(rate);
            }
            if (rate.Input != originalRate || rate.HasDraft || port.Input != originalPort || port.HasDraft || !PeriodsRetained())
                throw new InvalidOperationException("The edit review did not restore its fixture preferences and schedule.");
        }
        outcomes.Add(new { journey = "restore edit review fixture", rateRestored = true, portRetained = true, originalPeriodsRetained = true });
    }
}
