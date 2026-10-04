using System.Collections;
using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;

namespace Synapse;

public sealed partial class TableView
{
    private static readonly Thickness DefaultCellPadding = new(12, 6, 12, 6);

    public static readonly DependencyProperty ItemsSourceProperty =
        DependencyProperty.Register(
            nameof(ItemsSource),
            typeof(IEnumerable),
            typeof(TableView),
            new PropertyMetadata(null, OnItemsSourceChanged));

    public static readonly DependencyProperty IsFitAllButtonEnabledProperty =
        DependencyProperty.Register(
            nameof(IsFitAllButtonEnabled),
            typeof(bool),
            typeof(TableView),
            new PropertyMetadata(false, OnFitAllButtonEnabledChanged));

    public static readonly DependencyProperty PlaceholderProperty =
        DependencyProperty.Register(
            nameof(Placeholder),
            typeof(TablePlaceholder),
            typeof(TableView),
            new PropertyMetadata(TablePlaceholder.Empty, OnStateInputChanged));

    public static readonly DependencyProperty CellPaddingProperty =
        DependencyProperty.Register(
            nameof(CellPadding),
            typeof(Thickness),
            typeof(TableView),
            new PropertyMetadata(DefaultCellPadding));

    public static readonly DependencyProperty LoadingContentProperty =
        DependencyProperty.Register(
            nameof(LoadingContent),
            typeof(object),
            typeof(TableView),
            new PropertyMetadata(null, OnStateInputChanged));

    public static readonly DependencyProperty LoadingContentTemplateProperty =
        DependencyProperty.Register(
            nameof(LoadingContentTemplate),
            typeof(DataTemplate),
            typeof(TableView),
            new PropertyMetadata(null, OnStateInputChanged));

    public static readonly DependencyProperty EmptyContentProperty =
        DependencyProperty.Register(
            nameof(EmptyContent),
            typeof(object),
            typeof(TableView),
            new PropertyMetadata(null, OnStateInputChanged));

    public static readonly DependencyProperty EmptyContentTemplateProperty =
        DependencyProperty.Register(
            nameof(EmptyContentTemplate),
            typeof(DataTemplate),
            typeof(TableView),
            new PropertyMetadata(null, OnStateInputChanged));

    public static readonly DependencyProperty NoResultsContentProperty =
        DependencyProperty.Register(
            nameof(NoResultsContent),
            typeof(object),
            typeof(TableView),
            new PropertyMetadata(null, OnStateInputChanged));

    public static readonly DependencyProperty NoResultsContentTemplateProperty =
        DependencyProperty.Register(
            nameof(NoResultsContentTemplate),
            typeof(DataTemplate),
            typeof(TableView),
            new PropertyMetadata(null, OnStateInputChanged));

    /// <summary>The host's already filtered projection. The table never filters it further.</summary>
    public IEnumerable? ItemsSource
    {
        get => (IEnumerable?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    /// <summary>
    /// The immutable column baseline. Setup-only: the table captures it at its first
    /// <c>Loaded</c> and a structural change afterwards is a configuration error.
    /// </summary>
    public ObservableCollection<TableColumn> Columns { get; } = new();

    /// <summary>
    /// Offer <see cref="AutoFitVisibleColumns"/> as a button in the header's trailing space.
    /// Off by default.
    /// </summary>
    /// <remarks>
    /// The command already exists in the header context menu of section 12; this is the same
    /// command made discoverable, in space that is otherwise empty. It is opt-in rather than on
    /// by default because the table should not add a visible control to a host's header uninvited,
    /// and a host with its own fit button would otherwise show two of them.
    /// <para>
    /// Turning it on does not guarantee it is shown. The strip hides it whenever the columns reach
    /// far enough right to want that space, so it never covers a header.
    /// </para>
    /// </remarks>
    public bool IsFitAllButtonEnabled
    {
        get => (bool)GetValue(IsFitAllButtonEnabledProperty);
        set => SetValue(IsFitAllButtonEnabledProperty, value);
    }

    /// <summary>
    /// Which presentation stands in for rows while the view has none. Only the host can tell an
    /// empty source from a filter that excluded everything, or from a fetch in flight.
    /// </summary>
    public TablePlaceholder Placeholder
    {
        get => (TablePlaceholder)GetValue(PlaceholderProperty);
        set => SetValue(PlaceholderProperty, value);
    }

    /// <summary>
    /// The inset inside every column, applied to the header cell and the row cell alike so that
    /// the two cannot drift apart. Read as each cell is realized, so a host that wants its own
    /// sets it in XAML or in an implicit <c>Style</c>, the way any control default is overridden.
    /// </summary>
    /// <remarks>
    /// The horizontal 12 is the platform's: it is what the resource tree publishes for an item in a
    /// list, corroborated three ways — <c>ListBoxItemPadding</c> 12,9,12,12,
    /// <c>SelectorBarItemPadding</c> 12,10,12,7, and <c>PivotItemMargin</c> 12,0,12,0 — and it is
    /// also what both hosts here had arrived at independently. The vertical 6 has no platform
    /// source: enumerating all 7,484 <c>Thickness</c> resources found no cell padding at all, and
    /// what settles it is that the two hosts had independently written 6 as well.
    /// </remarks>
    public Thickness CellPadding
    {
        get => (Thickness)GetValue(CellPaddingProperty);
        set => SetValue(CellPaddingProperty, value);
    }

    public object? LoadingContent
    {
        get => GetValue(LoadingContentProperty);
        set => SetValue(LoadingContentProperty, value);
    }

    public DataTemplate? LoadingContentTemplate
    {
        get => (DataTemplate?)GetValue(LoadingContentTemplateProperty);
        set => SetValue(LoadingContentTemplateProperty, value);
    }

    public object? EmptyContent
    {
        get => GetValue(EmptyContentProperty);
        set => SetValue(EmptyContentProperty, value);
    }

    public DataTemplate? EmptyContentTemplate
    {
        get => (DataTemplate?)GetValue(EmptyContentTemplateProperty);
        set => SetValue(EmptyContentTemplateProperty, value);
    }

    public object? NoResultsContent
    {
        get => GetValue(NoResultsContentProperty);
        set => SetValue(NoResultsContentProperty, value);
    }

    public DataTemplate? NoResultsContentTemplate
    {
        get => (DataTemplate?)GetValue(NoResultsContentTemplateProperty);
        set => SetValue(NoResultsContentTemplateProperty, value);
    }

    private static void OnItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((TableView)d).SetItemsSource(e.NewValue as IEnumerable);

    private static void OnStateInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((TableView)d).UpdateStateLayer();

    private static void OnFitAllButtonEnabledChanged(
        DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((TableView)d)._headerStrip?.UpdateFitAllVisibility();
}
