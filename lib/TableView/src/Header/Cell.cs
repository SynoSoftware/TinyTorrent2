using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Syno.TableView.Header;

/// <summary>
/// One header cell. It shows the column's own header content when the host supplied any, and a
/// one-line label from <see cref="Column.DisplayName"/>, or its optional short name, otherwise.
/// </summary>
public sealed partial class Cell : Control
{
    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private sealed class Peer(Cell cell) : FrameworkElementAutomationPeer(cell), IInvokeProvider
    {
        protected override AutomationControlType GetAutomationControlTypeCore() =>
            AutomationControlType.HeaderItem;

        protected override bool IsContentElementCore() => false;

        protected override object GetPatternCore(PatternInterface pattern) =>
            pattern == PatternInterface.Invoke && cell.Column?.CanSort == true
                ? this
                : base.GetPatternCore(pattern);

        public void Invoke()
        {
            if (!IsEnabled())
                throw new ElementNotEnabledException();
            cell.ActivateSort();
        }
    }

    private const string ContentPartName = "PART_Header";
    private const string SortGlyphPartName = "PART_SortGlyph";

    /// <summary>
    /// The sort indicators, from the library's own icon set rather than the platform's. They sit
    /// two inches from the menu these headers open, so drawing them from a second family was the
    /// most visible place the control disagreed with itself about what an icon looks like.
    /// </summary>
    private const string AscendingGlyph = Lucide.ChevronUp;
    private const string DescendingGlyph = Lucide.ChevronDown;

    private ContentPresenter? _presenter;
    private FontIcon? _sortGlyph;
    private SortDirection? _sort;
    private Strings _strings = Strings.English;
    private FrameworkElement? _label;

    public Cell() => DefaultStyleKey = typeof(Cell);

    internal Column? Column { get; private set; }

    internal void ActivateSort()
    {
        if (Column?.CanSort != true || Body.Row.FindOwner(this) is not Table table)
            return;
        table.ActivateSort(Column);
        FrameworkElementAutomationPeer
            .FromElement(this)
            ?.RaiseAutomationEvent(AutomationEvents.InvokePatternOnInvoked);
    }

    /// <summary>
    /// Section 11's "the dragged header remains identifiable". The state carries the platform's own
    /// drag opacity, so no colour and no new resource is involved, and it applies without a
    /// transition because section 19 requires drag feedback to track the input.
    /// </summary>
    internal void SetDragging(bool dragging) =>
        VisualStateManager.GoToState(
            this,
            dragging ? "Dragging" : "NotDragging",
            useTransitions: false
        );

    internal void SetColumn(Column column)
    {
        if (ReferenceEquals(Column, column))
        {
            return;
        }

        Column = column;
        AutomationProperties.SetName(this, column.DisplayName);
        ApplyContent();
    }

    /// <summary>
    /// Section 9: the active header shows its direction with a native theme-aware glyph and states
    /// it through UI Automation. Null is "this column is not the sorted one".
    /// </summary>
    internal void SetSort(SortDirection? direction)
    {
        _sort = direction;
        ApplySort();
    }

    internal void RefreshText(Strings strings)
    {
        _strings = strings;
        if (Column is Column column)
        {
            AutomationProperties.SetName(this, column.DisplayName);
        }
        ApplyContent();
        ApplySort();
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _presenter = GetTemplateChild(ContentPartName) as ContentPresenter;
        _label = null;
        _sortGlyph = GetTemplateChild(SortGlyphPartName) as FontIcon;
        ApplyContent();
        ApplySort();
    }

    private void ApplySort()
    {
        AutomationProperties.SetItemStatus(
            this,
            _sort switch
            {
                SortDirection.Ascending => _strings.SortedAscending,
                SortDirection.Descending => _strings.SortedDescending,
                _ => string.Empty,
            }
        );

        if (_sortGlyph is null)
        {
            return;
        }

        _sortGlyph.FontFamily = Lucide.Font;
        _sortGlyph.Glyph = _sort == SortDirection.Descending ? DescendingGlyph : AscendingGlyph;
        _sortGlyph.Visibility = _sort is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ApplyContent()
    {
        if (_presenter is null || Column is null)
        {
            return;
        }

        if (Column.Header is not null || Column.HeaderTemplate is not null)
        {
            _label = null;
            if (!ReferenceEquals(_presenter.Content, Column.Header))
            {
                _presenter.Content = Column.Header;
            }
            _presenter.ContentTemplate = Column.HeaderTemplate;
            return;
        }

        _presenter.ContentTemplate = null;
        if (Column.ShortName is { Length: > 0 } shortName)
        {
            if (_label is not Label label)
                _label = label = new Label { VerticalAlignment = VerticalAlignment.Center };
            label.SetNames(Column.DisplayName, shortName);
        }
        else
        {
            if (_label is not TextBlock label)
                _label = label = new TextBlock
                {
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    TextWrapping = TextWrapping.NoWrap,
                    VerticalAlignment = VerticalAlignment.Center,
                };
            label.Text = Column.DisplayName;
        }
        ToolTipService.SetToolTip(_label, Column.DisplayName);
        if (!ReferenceEquals(_presenter.Content, _label))
        {
            _presenter.Content = _label;
        }
    }

    private sealed partial class Label : Panel
    {
        private readonly TextBlock _full = new();
        private readonly TextBlock _short = new();

        internal Label()
        {
            foreach (var label in new[] { _full, _short })
            {
                label.TextTrimming = TextTrimming.CharacterEllipsis;
                label.TextWrapping = TextWrapping.NoWrap;
                AutomationProperties.SetAccessibilityView(label, AccessibilityView.Raw);
                Children.Add(label);
            }
        }

        internal void SetNames(string full, string shortName)
        {
            _full.Text = full;
            _short.Text = shortName;
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            var natural = new Size(double.PositiveInfinity, availableSize.Height);
            _full.Measure(natural);
            _short.Measure(natural);
            // Fits have no width constraint; the short name keeps the header from
            // widening a column whose values need less room.
            var width = double.IsPositiveInfinity(availableSize.Width)
                || _full.DesiredSize.Width > availableSize.Width
                ? _short.DesiredSize.Width
                : _full.DesiredSize.Width;
            return new Size(width, Math.Max(_full.DesiredSize.Height, _short.DesiredSize.Height));
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var full = _full.DesiredSize.Width <= finalSize.Width;
            _full.Opacity = full ? 1 : 0;
            _short.Opacity = full ? 0 : 1;
            var bounds = new Rect(new Point(), finalSize);
            _full.Arrange(bounds);
            _short.Arrange(bounds);
            return finalSize;
        }
    }
}
