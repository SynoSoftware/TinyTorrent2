using Microsoft.UI.Xaml;
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
        Search.PlaceholderText = Model.Text.Get("files", "search");
        AllLabel.Text = Model.Text.Get("files", "all_matching");
        NoneLabel.Text = Model.Text.Get("files", "none_matching");
        NameLabel.Text = Model.Text.Get("files", "name");
        SizeLabel.Text = Model.Text.Get("columns", "size");
        ProgressLabel.Text = Model.Text.Get("columns", "progress");
        PriorityLabel.Text = Model.Text.Get("files", "priority");
        Model.Refresh();
    }

    private void OnAll(object sender, RoutedEventArgs args) => Model.SelectMatching(true);
    private void OnNone(object sender, RoutedEventArgs args) => Model.SelectMatching(false);

    private void OnFilesKey(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != VirtualKey.F2) return;
        var element = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        while (element is not null && !ReferenceEquals(element, sender))
        {
            if (element is ComboBox) return;
            if (element is TreeViewItem item)
            {
                if (item.Content is Grid row && row.Children.OfType<ComboBox>().FirstOrDefault() is { IsEnabled: true } priority)
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
