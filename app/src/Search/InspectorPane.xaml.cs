using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Syno.TableView;
using Syno.TinyTorrent.Controls;

namespace Syno.TinyTorrent.Search;

/// <summary>
/// The Search page's content for the shared inspector. The window owns the
/// pane's presentation and handles the requests its Surface raises.
/// </summary>
public sealed partial class InspectorPane : UserControl
{
    private ScrollViewer? _sourceScroll;
    private FileBrowser? _files;

    public Browser Model { get; }

    internal InspectorPane(Browser model)
    {
        Model = model;
        InitializeComponent();
        SourceTable.SizeChanged += (_, _) => AttachScroll();
        SourceTable.Schema<SourceRow>().Key(row => row.Key);
        Aliases.Schema<AliasRow>().Key(row => row.Name);
        Loaded += (_, _) =>
        {
            model.PropertyChanged -= OnModel;
            model.PropertyChanged += OnModel;
            AttachScroll();
            ShowSection();
        };
        Unloaded += (_, _) =>
        {
            if (IsLoaded)
                return;
            model.PropertyChanged -= OnModel;
            if (_sourceScroll is not null)
                _sourceScroll.ViewChanged -= OnSourceScroll;
            _sourceScroll = null;
        };
        Sections.SelectedItem = GeneralSection;
        RefreshText();
    }

    internal void RefreshText()
    {
        _files?.RefreshText();
        Surface.RefreshText(Model.Text, "inspector");
        SourceTable.Strings = Aliases.Strings = Model.Text.Table;
        foreach (var table in new[] { SourceTable, Aliases })
            foreach (var column in table.Columns)
                column.DisplayName = Model.Text.Get("catalogue", column.Id!);
    }

    // A torrent hides the source table, so its facts also fill the table's row.
    private int FactRows(bool inspectsTorrent) => inspectsTorrent ? 2 : 1;

    private void OnModel(object? sender, PropertyChangedEventArgs args) => ShowSection();

    private void OnSection(SelectorBar sender, SelectorBarSelectionChangedEventArgs args) => ShowSection();

    private void ShowSection()
    {
        if (General is null)
            return;
        if ((Model.InspectsTorrent && Sections.SelectedItem == AliasesSection)
            || (!Model.OffersFiles && Sections.SelectedItem == FilesSection))
            Sections.SelectedItem = GeneralSection;
        General.Visibility = Model.HasSelection && Sections.SelectedItem == GeneralSection ? Visibility.Visible : Visibility.Collapsed;
        Aliases.Visibility = Model.HasSelection && Sections.SelectedItem == AliasesSection ? Visibility.Visible : Visibility.Collapsed;
        FileArea.Visibility = Model.HasSelection && Sections.SelectedItem == FilesSection ? Visibility.Visible : Visibility.Collapsed;
        if (Sections.SelectedItem == FilesSection && _files is null)
        {
            _files = new FileBrowser(Model.FileChoices);
            FilesHost.Content = _files;
        }
    }

    private async void OnSourceScroll(object? sender, ScrollViewerViewChangedEventArgs args)
    {
        if (_sourceScroll is { } scroll && scroll.VerticalOffset + scroll.ViewportHeight >= scroll.ExtentHeight - 100)
            await Model.MoreSources();
    }

    private void AttachScroll()
    {
        if (IsLoaded && _sourceScroll is null && Workspace.FindScroll(SourceTable) is { } sources)
        {
            _sourceScroll = sources;
            sources.ViewChanged += OnSourceScroll;
        }
    }
}
