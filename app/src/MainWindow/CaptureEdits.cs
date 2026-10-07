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
        await Model.Preferences.SelectTheme("light");
        await CaptureReady(Model, () => Model.CanClose && Model.Theme == "light");
        var scale = Root.XamlRoot.RasterizationScale;
        var minimum = ((Microsoft.UI.Windowing.OverlappedPresenter)AppWindow.Presenter).PreferredMinimumWidth ?? 0;
        AppWindow.Resize(new SizeInt32(Math.Max((int)(1040 * scale), minimum), (int)(680 * scale)));
        await ShowTorrents();
        Model.CloseInspector();
        Torrents.Selection = new Syno.TableView.Selection([target], target);
        await SelectTorrent();

        var preferences = Model.Preferences;
        if (preferences.Schedule.IsOpen || preferences.IsPending || preferences.Fields.Any(field => field.HasDraft))
            throw new InvalidOperationException("The edit review requires confirmed fixture preferences.");
        var rate = preferences.Download;
        var upload = preferences.Upload;
        var port = preferences.Port;
        var originalRate = rate.Input;
        var originalUpload = upload.Input;
        var originalPort = port.Input;
        var originalPeriods = preferences.Schedule.Periods.ToArray();
        var originalLimits = Model.LimitsIndex;
        Task<bool>? departure = null;

        bool PeriodsRetained() => preferences.Schedule.Periods.Count == originalPeriods.Length &&
            preferences.Schedule.Periods.Zip(originalPeriods).All(pair => pair.First.Matches(pair.Second));

        async Task<TextBox> Edit(Preference field, string input)
        {
            await ShowPreferences(new(field.Section, field.Name));
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

        async Task<bool> Leave()
        {
            departure = ShowTorrents();
            await CaptureLayout();
            if (_interaction?.Dialog is not null)
                throw new InvalidOperationException("Leaving Settings unexpectedly requested a draft decision.");
            var navigated = await departure.WaitAsync(TimeSpan.FromSeconds(20));
            departure = null;
            await CaptureLayout();
            return navigated;
        }

        async Task<SchedulePeriod> AddPeriod()
        {
            // Periods show and change only under the weekly schedule.
            await Model.ChooseLimits(LimitMode.Schedule);
            if (!Model.FollowsSchedule) throw new InvalidOperationException("The edit review could not choose the weekly schedule.");
            await ShowPreferences(new(PreferenceSection.Limits));
            await CaptureLayout();
            var form = _preferencesForm ?? throw new InvalidOperationException("The native schedule did not open.");
            CaptureInvoke(CaptureElements(form).OfType<Button>().Single(control => AutomationProperties.GetAutomationId(control) == "AddPeriod"));
            await CaptureReady(preferences.Schedule, () => !preferences.Schedule.IsPending && preferences.Schedule.IsOpen);
            if (preferences.Schedule.OpenPeriod is not { } opened || originalPeriods.Any(existing => existing.Matches(opened)))
                throw new InvalidOperationException("The fixture schedule already holds the period that Add creates.");
            var draft = preferences.Schedule.Draft ?? throw new InvalidOperationException("The native Add period did not open its editor.");
            CaptureElements(form).OfType<TimePicker>().Single(control => AutomationProperties.GetAutomationId(control) == "PeriodStart").SelectedTime = TimeSpan.FromMinutes(127);
            CaptureElements(form).OfType<TimePicker>().Single(control => AutomationProperties.GetAutomationId(control) == "PeriodEnd").SelectedTime = TimeSpan.FromMinutes(151);
            var days = CaptureElements(form).OfType<ItemsRepeater>().Single(repeater => ReferenceEquals(repeater.ItemsSource, draft.Days));
            foreach (var day in draft.Days)
            {
                var element = days.TryGetElement(day.Index);
                if (element is not CheckBox control || AutomationProperties.GetAutomationId(control) != day.AutomationId)
                    throw new InvalidOperationException($"The native schedule day {day.Index} expected {day.AutomationId}, received {(element is null ? "unrealized" : element.GetType().Name + " " + AutomationProperties.GetAutomationId(element))}.");
                if (control.IsChecked == (day.Index == 6)) continue;
                if (FrameworkElementAutomationPeer.CreatePeerForElement(control).GetPattern(PatternInterface.Toggle) is not IToggleProvider toggle)
                    throw new InvalidOperationException("The native schedule day does not expose Toggle.");
                toggle.Toggle();
            }
            await CaptureLayout();
            if (draft.Start?.TotalMinutes != 127 || draft.End?.TotalMinutes != 151 || draft.Mode != ScheduleMode.Alternative ||
                !draft.Days.Where(day => day.IsChecked).Select(day => day.Index).SequenceEqual(new[] { 6 }))
                throw new InvalidOperationException("The native schedule edits did not retain the chosen period.");
            var period = new SchedulePeriod(preferences.Schedule, draft);
            if (originalPeriods.Any(existing => existing.Matches(period)))
                throw new InvalidOperationException("The review period already exists in the fixture schedule.");
            await CaptureReady(preferences.Schedule, () => !preferences.Schedule.IsPending && preferences.Schedule.OpenPeriod?.Matches(period) == true);
            return period;
        }

        try
        {
            var valid = originalRate == "64" ? "65" : "64";
            await Edit(rate, valid);
            await CapturePage("edits-valid-rate-before-leaving");
            if (!await Leave() || Model.Page != WindowPage.Torrents || rate.Input != valid || rate.HasDraft || rate.IsPending || rate.Message.Length != 0)
                throw new InvalidOperationException("Leaving a valid native rate edit did not apply it and navigate.");
            outcomes.Add(new { journey = "valid rate departure", applied = true, navigated = true, prompted = false, input = rate.Input });
            completed.Add("edits-valid-rate-departure");

            var downloadEditor = await Edit(rate, "160");
            var uploadValue = originalUpload == "32" ? "33" : "32";
            await Edit(upload, uploadValue);
            await CaptureReady(rate, () => !rate.IsPending && !rate.HasDraft);
            var injected = false;
            void OnUpload(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
            {
                if (injected || !upload.IsPending) return;
                injected = true;
                downloadEditor.Text = "161";
            }
            upload.PropertyChanged += OnUpload;
            try
            {
                var left = await Leave();
                var applied = rate.Input == "161" && upload.Input == uploadValue && !rate.HasDraft && !upload.HasDraft &&
                    !rate.IsPending && !upload.IsPending && rate.Message.Length == 0 && upload.Message.Length == 0;
                outcomes.Add(new { journey = "late earlier-field departure", injectedDuringUpload = injected, applied,
                    navigated = left, prompted = false, download = rate.Input, upload = upload.Input });
                if (!injected || !left || Model.Page != WindowPage.Torrents || !applied)
                    throw new InvalidOperationException("The first departure did not apply the native Download edit made during Upload's save.");
                completed.Add("edits-late-earlier-field-departure");
            }
            finally { upload.PropertyChanged -= OnUpload; }

            var invalidEditor = await Edit(port, "70000");
            var connections = CaptureElements(_preferencesForm!).OfType<NumberBox>()
                .Single(control => ReferenceEquals(control.Tag, preferences.Connections));
            connections.Focus(FocusState.Programmatic);
            await CaptureReady(port, () => port.Input == originalPort && !port.HasDraft);
            await CaptureLayout();
            if (invalidEditor.Text != originalPort || port.Message.Length != 0)
                throw new InvalidOperationException("Moving to another field did not restore the invalid port's saved value.");
            outcomes.Add(new { journey = "invalid port field departure", restored = true, nativeTextRestored = true });
            completed.Add("edits-invalid-port-field-departure");
            invalidEditor = await Edit(port, "70000");
            await CapturePage("edits-invalid-port-before-leaving");
            if (!await Leave() || Model.Page != WindowPage.Torrents || port.Input != originalPort || invalidEditor.Text != originalPort || port.HasDraft || port.Message.Length != 0)
                throw new InvalidOperationException("Leaving an invalid native port edit did not restore the saved value and navigate.");
            outcomes.Add(new { journey = "invalid port departure", restored = true, nativeTextRestored = true, navigated = true, prompted = false });
            completed.Add("edits-invalid-port-departure");

            var concurrent = rate.Input == "96" ? "97" : "96";
            await Edit(rate, concurrent);
            await CapturePage("edits-pending-rate-before-leaving");
            var save = preferences.Commit(rate);
            var pending = rate.IsPending;
            departure = ShowTorrents();
            await Task.WhenAll(save, departure).WaitAsync(TimeSpan.FromSeconds(20));
            var navigated = await departure;
            departure = null;
            await CaptureLayout();
            if (!pending || !navigated || Model.Page != WindowPage.Torrents || _interaction?.Dialog is not null ||
                rate.Input != concurrent || rate.HasDraft || rate.IsPending || rate.Message.Length != 0)
                throw new InvalidOperationException("The first departure during an active numeric save did not await it and navigate.");
            outcomes.Add(new { journey = "pending rate departure", pendingObserved = pending, applied = true, firstDepartureNavigated = true, prompted = false });
            completed.Add("edits-pending-rate-departure");

            var unsavedEditor = await Edit(rate, "128");
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

            var queuedEditor = await Edit(rate, "256");
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

            var cancelEditor = await Edit(rate, "512");
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

            await Edit(rate, originalRate);
            await preferences.Commit(rate);
            if (rate.Input != originalRate || rate.HasDraft) throw new InvalidOperationException("The original rate was not restored.");

            var added = await AddPeriod();
            var draft = preferences.Schedule.Draft!;
            draft.Start = TimeSpan.FromMinutes(137);
            if (!preferences.Schedule.IsPending) throw new InvalidOperationException("The schedule edit did not start a save.");
            draft.End = TimeSpan.FromMinutes(167);
            await preferences.Schedule.Close();
            added = preferences.Schedule.Periods.Single(period => period.Start == 137 && period.End == 167);
            if (preferences.Schedule.IsOpen || preferences.Schedule.IsPending)
                throw new InvalidOperationException("Closing the period did not finish its pending edits.");
            outcomes.Add(new { journey = "close period during pending save", start = added.Start, end = added.End });
            await CapturePage("edits-period-save-before-leaving");
            if (!await Leave() || Model.Page != WindowPage.Torrents ||
                preferences.Schedule.Periods.Count != originalPeriods.Length + 1 || !preferences.Schedule.Periods.Any(period => period.Matches(added)) ||
                !originalPeriods.All(existing => preferences.Schedule.Periods.Any(period => period.Matches(existing))))
                throw new InvalidOperationException("Leaving Settings did not apply the new period and retain the original schedule.");
            outcomes.Add(new { journey = "schedule departure applies", saved = true, navigated = true, days = added.Days, start = added.Start, end = added.End });
            completed.Add("edits-period-save-departure");
            await ShowPreferences(new(PreferenceSection.Limits));
            await preferences.Schedule.Open(preferences.Schedule.Periods.Single(period => period.Matches(added)));
            await CapturePage("edits-period-saved");
            await preferences.Schedule.Remove(preferences.Schedule.Periods.Single(period => period.Matches(added)));
            if (!PeriodsRetained()) throw new InvalidOperationException("Removing the created review period changed the original schedule.");
        }
        finally
        {
            _interaction?.Dialog?.Hide();
            if (departure is not null) await departure.WaitAsync(TimeSpan.FromSeconds(20));
            await CaptureReady(preferences, () => !preferences.IsPending);
            await preferences.Schedule.Close();
            // Every period change saves at once, so the review period may
            // remain in any of its intermediate forms.
            if (preferences.Schedule.Periods.FirstOrDefault(period => !originalPeriods.Any(existing => existing.Matches(period))) is { } remaining)
                await preferences.Schedule.Remove(remaining);
            if (originalLimits >= 0) await Model.ChooseLimits((LimitMode)originalLimits);
            port.Cancel();
            rate.Cancel();
            upload.Cancel();
            if (rate.Input != originalRate)
            {
                await Edit(rate, originalRate);
                await preferences.Commit(rate);
            }
            if (upload.Input != originalUpload)
            {
                await Edit(upload, originalUpload);
                await preferences.Commit(upload);
            }
            if (rate.Input != originalRate || rate.HasDraft || upload.Input != originalUpload || upload.HasDraft ||
                port.Input != originalPort || port.HasDraft || !PeriodsRetained() || Model.LimitsIndex != originalLimits)
                throw new InvalidOperationException("The edit review did not restore its fixture preferences and schedule.");
        }
        outcomes.Add(new { journey = "restore edit review fixture", rateRestored = true, uploadRestored = true, portRetained = true, originalPeriodsRetained = true });
    }
}
