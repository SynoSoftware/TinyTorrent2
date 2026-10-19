using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Syno.TinyTorrent.Shared;

namespace Syno.TinyTorrent.Shell;

public sealed partial class MainWindow
{
    private readonly Dictionary<
        BackgroundWork.Operation,
        (WorkStatus Inline, WorkStatus Expanded, Border Divider, WorkObservation Observation)
    > _workViews = [];
    private bool _workOpen;
    private bool _recoverWorkFocus;

    private void ConfigureWork()
    {
        ((INotifyCollectionChanged)Model.Work.Items).CollectionChanged += OnWorkChanged;
        SyncWork();
    }

    private void OnWorkChanged(object? sender, NotifyCollectionChangedEventArgs args) => SyncWork();

    private void SyncWork()
    {
        if (_allowClose)
            return;
        foreach (var operation in _workViews.Keys.Except(Model.Work.Items).ToArray())
        {
            var views = _workViews[operation];
            operation.PropertyChanged -= OnWorkObservation;
            _recoverWorkFocus |= views.Inline.HasFocus || views.Expanded.HasFocus;
            WorkItems.Children.Remove(views.Divider);
            WorkItems.Children.Remove(views.Inline);
            WorkList.Items.Remove(views.Expanded);
            _workViews.Remove(operation);
        }
        foreach (var operation in Model.Work.Items)
        {
            if (_workViews.ContainsKey(operation))
                continue;
            var inline = new WorkStatus();
            var expanded = new WorkStatus { IsExpanded = true };
            var divider = new Border
            {
                Style = (Style)Root.Resources["TinyTorrentWorkDividerStyle"],
            };
            AutomationProperties.SetAccessibilityView(
                divider,
                Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw
            );
            inline.FocusInvalidated += OnWorkFocus;
            expanded.FocusInvalidated += OnWorkFocus;
            inline.Show(operation, Model.Text, Model.Page == WindowPage.Library);
            expanded.Show(operation, Model.Text, Model.Page == WindowPage.Library);
            _workViews.Add(operation, (inline, expanded, divider, operation.Current));
            operation.PropertyChanged += OnWorkObservation;
            WorkItems.Children.Add(divider);
            WorkItems.Children.Add(inline);
            WorkList.Items.Add(expanded);
            Model.Notifications.Announce(
                WorkStatus.Describe(operation.Current, Model.Text, Model.Page == WindowPage.Library)
            );
        }
        if (_workViews.Count == 0)
        {
            _recoverWorkFocus |= WorkButton.FocusState != FocusState.Unfocused || _workOpen;
            WorkFlyout.Hide();
        }
        QueueStatus();
    }

    private void OnWorkObservation(object? sender, PropertyChangedEventArgs args)
    {
        if (
            _allowClose
            || sender is not BackgroundWork.Operation operation
            || !_workViews.TryGetValue(operation, out var views)
        )
            return;
        var previous = views.Observation;
        views.Observation = operation.Current;
        _workViews[operation] = views;
        if (
            previous.Description != operation.Current.Description
            || previous.Execution != operation.Current.Execution
        )
        {
            var library = Model.Page == WindowPage.Library;
            var description = WorkStatus.Describe(operation.Current, Model.Text, library);
            if (description != WorkStatus.Describe(previous, Model.Text, library))
                Model.Notifications.Announce(description);
        }
        QueueStatus();
    }

    private void OnWorkFocus(object? sender, EventArgs args)
    {
        _recoverWorkFocus = true;
        QueueStatus();
    }

    private void RefreshWork()
    {
        foreach (var (operation, views) in _workViews)
        {
            views.Inline.Show(operation, Model.Text, Model.Page == WindowPage.Library);
            views.Expanded.Show(operation, Model.Text, Model.Page == WindowPage.Library);
            views.Inline.Fit(false);
        }
        WorkButton.Text = Model.Text.Format("work", "button", _workViews.Count);
        NameButton(WorkButton, Model.Text.Format("work", "tip", _workViews.Count));
        WorkTitle.Text = Model.Text.Get("work", "title");
        AutomationProperties.SetName(WorkList, WorkTitle.Text);
        ShowWork(_workOpen);
    }

    private void ShowWork(bool overflow)
    {
        var batch =
            Model.Work.Items.FirstOrDefault(operation =>
                IsBatch(operation) && operation.Cancel.CanExecute(null)
            ) ?? Model.Work.Items.FirstOrDefault(IsBatch);
        var previous = false;
        foreach (var operation in Model.Work.Items)
        {
            var views = _workViews[operation];
            var visible = !overflow || ReferenceEquals(operation, batch);
            if (!visible && views.Inline.HasFocus)
                _recoverWorkFocus = true;
            ShowStatus(views.Divider, visible && previous);
            ShowStatus(views.Inline, visible);
            previous |= visible;
        }
        ShowStatus(WorkButton, overflow && _workViews.Count > 0);
        ShowStatus(WorkSeparator, previous && WorkButton.Visibility == Visibility.Visible);
    }

    private static bool IsBatch(BackgroundWork.Operation operation) =>
        operation.Current.Description is WorkDescription.Metadata { Scope: MetadataScope.Batch };

    private void FitWork(bool minimal = false)
    {
        foreach (var views in _workViews.Values)
            views.Inline.Fit(true, minimal);
        ShowWork(_workOpen);
    }

    private void RecoverWorkFocus()
    {
        if (!_recoverWorkFocus)
            return;
        _recoverWorkFocus = false;
        foreach (var views in _workViews.Values)
            if ((_workOpen ? views.Expanded : views.Inline).FocusCancel())
                return;
        if (_workOpen)
            WorkFlyout.Hide();
        if (WorkButton.Visibility == Visibility.Visible)
            WorkButton.Focus(FocusState.Keyboard);
        else
            FocusPage();
    }

    private void OnWorkOpening(object sender, object args)
    {
        _workOpen = true;
        WorkContent.Width = Math.Min(480, Math.Max(0, Root.ActualWidth - 64));
        WorkList.MaxHeight = Math.Max(32, Root.ActualHeight - 160);
        WorkContent.RequestedTheme = Root.ActualTheme;
        WorkContent.FlowDirection = Root.FlowDirection;
    }

    private void OnWorkClosed(object sender, object args)
    {
        _workOpen = false;
        QueueStatus();
    }
}
