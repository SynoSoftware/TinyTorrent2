using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;

namespace Syno.TinyTorrent.Controls;

// A value led by its icon and label, using the shared templates in App.xaml.
public sealed partial class Field : ContentControl
{
    public static readonly DependencyProperty GlyphProperty = Text(nameof(Glyph));
    public static readonly DependencyProperty LabelProperty = Text(nameof(Label), OnLabelChanged);
    public static readonly DependencyProperty DimsZerosProperty = DependencyProperty.Register(
        nameof(DimsZeros), typeof(bool), typeof(Field), new PropertyMetadata(false, OnLabelChanged)
    );
    private TextHighlighter? _zeros;
    public static readonly DependencyProperty ActionProperty = DependencyProperty.Register(
        nameof(Action),
        typeof(object),
        typeof(Field),
        new PropertyMetadata(null)
    );

    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }
    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    /// <summary>De-emphasizes standalone zero counts, including (0), in the inline style.</summary>
    public bool DimsZeros
    {
        get => (bool)GetValue(DimsZerosProperty);
        set => SetValue(DimsZerosProperty, value);
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _zeros = (GetTemplateChild("PART_Label") as TextBlock)?.TextHighlighters.FirstOrDefault();
        RefreshZeros();
    }

    private static void OnLabelChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((Field)sender).RefreshZeros();

    private void RefreshZeros()
    {
        if (_zeros is null)
            return;
        _zeros.Ranges.Clear();
        if (!DimsZeros)
            return;
        foreach (Match match in Zeros().Matches(Label))
            _zeros.Ranges.Add(new TextRange { StartIndex = match.Index, Length = match.Length });
    }

    [GeneratedRegex(@"\(0\)|\b0\b")]
    private static partial Regex Zeros();

    // A button that follows the value and acts on it. A stretched
    // HorizontalContentAlignment puts it at the field's right end instead.
    public object? Action
    {
        get => GetValue(ActionProperty);
        set => SetValue(ActionProperty, value);
    }

    private static DependencyProperty Text(string name, PropertyChangedCallback? changed = null) =>
        DependencyProperty.Register(
            name,
            typeof(string),
            typeof(Field),
            new PropertyMetadata(string.Empty, changed)
        );
}
