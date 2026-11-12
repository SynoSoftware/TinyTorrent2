using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;

namespace Syno.TinyTorrent.Controls;

// A value led by its icon and label, using the shared templates in App.xaml.
public sealed partial class Field : ContentControl
{
    public static readonly DependencyProperty GlyphProperty = Text(nameof(Glyph));
    public static readonly DependencyProperty LabelProperty = Text(nameof(Label));
    public static readonly DependencyProperty DimsZerosProperty = DependencyProperty.Register(
        nameof(DimsZeros), typeof(bool), typeof(Field), new PropertyMetadata(false, OnDimsZerosChanged)
    );
    private TextBlock? _label;
    private TextHighlighter? _zeros;
    private bool _dimmed;
    // The label callback, which only a field that dims zeros registers: a callback
    // calls into .NET on every label change, and most fields never dim zeros.
    private long? _labelCallback;
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
        _label = GetTemplateChild("PART_Label") as TextBlock;
        if (_label is { } label)
        {
            Trimming.SetShowsToolTip(label, Trimming.GetShowsToolTip(this));
        }
        _zeros = null;
        _dimmed = false;
        RefreshZeros();
    }

    private static void OnDimsZerosChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var field = (Field)sender;
        if (field.DimsZeros && field._labelCallback is null)
        {
            field._labelCallback = field.RegisterPropertyChangedCallback(LabelProperty, OnLabelChanged);
        }
        else if (!field.DimsZeros && field._labelCallback is { } callback)
        {
            field.UnregisterPropertyChangedCallback(LabelProperty, callback);
            field._labelCallback = null;
        }
        field.RefreshZeros();
    }

    private static void OnLabelChanged(DependencyObject sender, DependencyProperty property) =>
        ((Field)sender).RefreshZeros();

    // Most fields never dim zeros, so they never read the highlighter: each
    // read and change of its ranges is a call into WinUI on every label change.
    private void RefreshZeros()
    {
        if (!DimsZeros && !_dimmed)
        {
            return;
        }
        _zeros ??= _label?.TextHighlighters.FirstOrDefault();
        if (_zeros is null)
        {
            return;
        }
        _zeros.Ranges.Clear();
        _dimmed = false;
        if (!DimsZeros)
        {
            return;
        }
        foreach (Match match in Zeros().Matches(Label))
        {
            _zeros.Ranges.Add(new TextRange { StartIndex = match.Index, Length = match.Length });
            _dimmed = true;
        }
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

    private static DependencyProperty Text(string name) =>
        DependencyProperty.Register(
            name,
            typeof(string),
            typeof(Field),
            new PropertyMetadata(string.Empty)
        );
}
