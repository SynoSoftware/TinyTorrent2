using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Syno.TinyTorrent.Controls;

public sealed partial class FileBrowser : UserControl
{
    public static readonly DependencyProperty ModelProperty = DependencyProperty.Register(nameof(Model), typeof(FileSelection), typeof(FileBrowser), new PropertyMetadata(null));
    public FileSelection Model { get => (FileSelection)GetValue(ModelProperty); set => SetValue(ModelProperty, value); }

    public FileBrowser(FileSelection model)
    {
        Model = model;
        InitializeComponent();
        RefreshText();
    }

    internal void RefreshText()
    {
        Bindings.Update();
        ToolTipService.SetToolTip(All, Model.Text.Get("files", "all_matching"));
        AutomationProperties.SetHelpText(All, Model.Text.Get("files", "all_matching"));
        Model.Refresh();
    }

    private void OnAll(object sender, RoutedEventArgs args)
    {
        Model.SelectMatching(Model.AllMatching != true);
        // The click has already toggled the box; show the selection that resulted.
        All.IsChecked = Model.AllMatching;
    }

    private void OnExpand(object sender, RoutedEventArgs args) => Model.Expand(true);
    private void OnCollapse(object sender, RoutedEventArgs args) => Model.Expand(false);

    // The tree owns the keyboard: arrows move between rows, Space toggles the
    // focused row as its check box would, and F2 opens its priority. The check
    // box stays out of the Tab order, so Tab crosses a long list in two stops.
    private void OnFilesKey(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key is not (VirtualKey.F2 or VirtualKey.Space)) return;
        var element = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        while (element is not null && !ReferenceEquals(element, sender))
        {
            if (element is ComboBox) return;
            if (element is TreeViewItem item)
            {
                if (args.Key == VirtualKey.Space)
                {
                    if (item.DataContext is FileNode { IsEnabled: true } node)
                    {
                        node.Wanted = node.Wanted != true;
                        args.Handled = true;
                    }
                }
                else if (item.Content is Grid row && row.Children.OfType<ComboBox>().FirstOrDefault() is { IsEnabled: true } priority)
                {
                    priority.Focus(FocusState.Keyboard);
                    priority.IsDropDownOpen = true;
                    args.Handled = true;
                }
                return;
            }
            element = VisualTreeHelper.GetParent(element);
        }
    }
}
