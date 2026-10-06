using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Syno.TableView;
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

    // The TableView's own default, so this list's cells cannot drift from the
    // product's tables.
    public static Thickness Cell { get; } = (Thickness)Table.CellPaddingProperty.GetMetadata(typeof(Table)).DefaultValue;
    public static Thickness Trail { get; } = new(0, 0, Cell.Right, 0);

    // WinUI's TreeViewItem insets a row by 4 pixels and reserves a 40-pixel
    // expander column (14 + 12 + 14). A list without folders never shows an
    // expander, so its rows move left over that column. Either way a top-level
    // row's check box starts one cell inset past the expanders it shows.
    private const double Inset = 4;
    private const double Expander = 40;

    public static Thickness Lead(bool folders) => new(Cell.Left + (folders ? Expander : 0), 0, Cell.Right, 0);

    public static Thickness Indent(bool folders) => new(Lead(folders).Left - Inset - Expander, 0, 0, 0);

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
