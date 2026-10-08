using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Syno.TableView;

public sealed partial class Table
{
    private UIElement? _defaultPlaceholder;
    private Placeholder _defaultKind;

    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.Register(
        nameof(Placeholder),
        typeof(Placeholder),
        typeof(Table),
        new PropertyMetadata(Placeholder.Empty, OnPlaceholderInputChanged)
    );

    public static readonly DependencyProperty LoadingContentProperty = DependencyProperty.Register(
        nameof(LoadingContent),
        typeof(object),
        typeof(Table),
        new PropertyMetadata(null, OnPlaceholderInputChanged)
    );

    public static readonly DependencyProperty LoadingContentTemplateProperty =
        DependencyProperty.Register(
            nameof(LoadingContentTemplate),
            typeof(DataTemplate),
            typeof(Table),
            new PropertyMetadata(null, OnPlaceholderInputChanged)
        );

    public static readonly DependencyProperty EmptyContentProperty = DependencyProperty.Register(
        nameof(EmptyContent),
        typeof(object),
        typeof(Table),
        new PropertyMetadata(null, OnPlaceholderInputChanged)
    );

    public static readonly DependencyProperty EmptyContentTemplateProperty =
        DependencyProperty.Register(
            nameof(EmptyContentTemplate),
            typeof(DataTemplate),
            typeof(Table),
            new PropertyMetadata(null, OnPlaceholderInputChanged)
        );

    public static readonly DependencyProperty NoResultsContentProperty =
        DependencyProperty.Register(
            nameof(NoResultsContent),
            typeof(object),
            typeof(Table),
            new PropertyMetadata(null, OnPlaceholderInputChanged)
        );

    public static readonly DependencyProperty NoResultsContentTemplateProperty =
        DependencyProperty.Register(
            nameof(NoResultsContentTemplate),
            typeof(DataTemplate),
            typeof(Table),
            new PropertyMetadata(null, OnPlaceholderInputChanged)
        );

    /// <summary>
    /// Which presentation stands in for rows while the view has none. Only the host can tell an
    /// empty source from a filter that excluded everything, or from a fetch in flight.
    /// </summary>
    public Placeholder Placeholder
    {
        get => (Placeholder)GetValue(PlaceholderProperty);
        set => SetValue(PlaceholderProperty, value);
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

    private static void OnPlaceholderInputChanged(
        DependencyObject d,
        DependencyPropertyChangedEventArgs e
    ) => ((Table)d).UpdatePlaceholder();

    // ----------------------------------------------------------- placeholder

    /// <summary>
    /// Section 17. It reads the private view, not <see cref="Placeholder"/>, so existing rows stay
    /// visible during a refresh; the placeholder says only which presentation an empty view gets.
    /// </summary>
    private void UpdatePlaceholder()
    {
        if (_placeholderPresenter is null)
        {
            return;
        }

        if (_view.Count > 0)
        {
            _placeholderPresenter.Content = null;
            _placeholderPresenter.ContentTemplate = null;
            _placeholderPresenter.Visibility = Visibility.Collapsed;
            return;
        }

        Placeholder kind = Placeholder;
        (object? content, DataTemplate? template) = kind switch
        {
            Placeholder.Loading => (LoadingContent, LoadingContentTemplate),
            Placeholder.NoResults => (NoResultsContent, NoResultsContentTemplate),
            _ => (EmptyContent, EmptyContentTemplate),
        };

        _placeholderPresenter.Content =
            content ?? (template is null ? DefaultPlaceholder(kind) : null);
        _placeholderPresenter.ContentTemplate = template;
        _placeholderPresenter.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// What an empty table shows when the host has configured nothing. Kept while the kind holds,
    /// so a table that rebuilds an empty view does not restart the ring it is showing.
    /// </summary>
    /// <remarks>
    /// Deliberately the least the platform can say: the ring at its own size, the two messages in
    /// the inherited foreground. No spacing, no colour and no font size is chosen here, because
    /// none of them is derivable and a host that wants more sets <see cref="LoadingContent"/>,
    /// <see cref="EmptyContent"/>, or <see cref="NoResultsContent"/>. What this replaces is a blank
    /// rectangle, which is what a table with no rows and nothing configured used to render.
    /// </remarks>
    private UIElement DefaultPlaceholder(Placeholder kind)
    {
        if (_defaultPlaceholder is not null && _defaultKind == kind)
        {
            return _defaultPlaceholder;
        }

        FrameworkElement element =
            kind == Placeholder.Loading ? new ProgressRing { IsActive = true } : new TextBlock();
        element.HorizontalAlignment = HorizontalAlignment.Center;
        element.VerticalAlignment = VerticalAlignment.Center;

        _defaultKind = kind;
        _defaultPlaceholder = element;
        RefreshPlaceholderText();
        return element;
    }

    /// <summary>Give the default placeholder the current <see cref="Strings"/>.</summary>
    private void RefreshPlaceholderText()
    {
        switch (_defaultPlaceholder)
        {
            case null:
                return;
            case TextBlock label:
                label.Text =
                    _defaultKind == Placeholder.NoResults ? Strings.NoResults : Strings.Empty;
                return;
            default:
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
                    _defaultPlaceholder,
                    Strings.Loading
                );
                return;
        }
    }
}
