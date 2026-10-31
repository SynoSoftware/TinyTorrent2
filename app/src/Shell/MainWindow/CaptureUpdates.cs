using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Syno.TinyTorrent.Shared;
using Syno.TinyTorrent.Shared.Network;

namespace Syno.TinyTorrent.Shell;

public sealed partial class MainWindow
{
    private async Task CaptureUpdateCheck(List<object> outcomes)
    {
        var ready = Stopwatch.StartNew();
        while (HelpMenu.Items.Count == 0 || !Model.Features.HasRoute)
        {
            if (ready.Elapsed > TimeSpan.FromSeconds(20))
                throw new InvalidOperationException("The update menu or confirmed route is not ready.");
            await Task.Delay(20);
            await CaptureLayout();
        }
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var http = (ProviderHttp)typeof(Updates).GetField("_http", flags)!.GetValue(Model.Updates)!;
        var field = typeof(ProviderHttp).GetField("_client", flags)!;
        var saved = field.GetValue(http);
        using var client = new HttpClient(new ReleaseReply());
        field.SetValue(http, client);
        try
        {
            foreach (var search in new[] { false, true })
            {
                foreach (var shown in Model.Notifications.Toasts.ToArray())
                    Model.Notifications.Dismiss(shown);
                if (search)
                    Search.Focus(FocusState.Programmatic);
                else
                    Torrents.FocusRows(FocusState.Programmatic);
                await CaptureLayout();
                var completion = new TaskCompletionSource<WorkResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                void Completed(object? sender, WorkResult result)
                {
                    if (result.Observation.Description is WorkDescription.Update)
                        completion.TrySetResult(result);
                }
                Model.Work.Completed += Completed;
                try
                {
                    var expand = (IExpandCollapseProvider)FrameworkElementAutomationPeer
                        .CreatePeerForElement(HelpMenu).GetPattern(PatternInterface.ExpandCollapse);
                    expand.Expand();
                    await CaptureLayout();
                    var item = HelpMenu.Items.OfType<MenuFlyoutItem>()
                        .Single(item => item.Command == Model.Updates.Check);
                    CaptureInvoke(item);
                    var result = await completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
                    await CaptureLayout();
                    await CapturePage(search ? "update-after-search" : "update-from-list");
                    var toast = Model.Notifications.Toasts.OfType<Toast.Work>()
                        .SingleOrDefault(shown => ReferenceEquals(shown.Result, result));
                    var visible = ToastLayer.IsOpen && ToastStack.Visibility == Visibility.Visible;
                    outcomes.Add(new { search, result.Outcome, result.SummaryKey, visible,
                        editorFocused = HasEditorFocus(), focus = FocusManager.GetFocusedElement(Root.XamlRoot)?.GetType().Name,
                        toast = toast?.Message });
                    if (result.SummaryKey != "up_to_date" || toast is null || !visible)
                        throw new InvalidOperationException("The Help update check did not show its up-to-date result.");
                }
                finally
                {
                    Model.Work.Completed -= Completed;
                }
            }
            foreach (var shown in Model.Notifications.Toasts.ToArray())
                Model.Notifications.Dismiss(shown);
            WorkResult[] examples =
            [
                new(new(new WorkDescription.Metadata(MetadataScope.Batch), new WorkProgress.Unknown(), new WorkExecution.Finishing()),
                    WorkOutcome.Completed, null, "metadata_result", [0, 3, 3]),
                new(new(new WorkDescription.Subtitles(SubtitleWork.Manual), new WorkProgress.Unknown(), new WorkExecution.Finishing()),
                    WorkOutcome.Cancelled, null, "subtitles_result", [1, 2, 0]),
                new(new(new WorkDescription.Update(UpdatePhase.Checking), new WorkProgress.Unknown(), new WorkExecution.Finishing()),
                    WorkOutcome.Failed, new UpdateException(UpdateFailure.Unavailable), null, []),
                new(new(new WorkDescription.Imdb(ImdbPhase.Installing), new WorkProgress.Unknown(), new WorkExecution.Finishing()),
                    WorkOutcome.Completed, null, null, []),
            ];
            foreach (var result in examples)
                Model.Notifications.ShowWorkResult(result);
            await CaptureLayout();
            await CapturePage("work-results");
            Model.SelectLanguage("nl");
            await CaptureReady(Model, () => Model.CanClose && Model.Text.Language == "nl");
            await CapturePage("work-results-dutch");
        }
        finally
        {
            field.SetValue(http, saved);
        }
    }

    private sealed class ReleaseReply : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"tag_name\":\"v" + App.Version + "\"}"),
            });
    }
}
