using Microsoft.UI.Xaml;

namespace Syno.TableView;

public sealed partial class Table
{
    public static readonly DependencyProperty StringsProperty = DependencyProperty.Register(
        nameof(Strings),
        typeof(Strings),
        typeof(Table),
        new PropertyMetadata(null, OnStringsChanged)
    );

    /// <summary>Prepared control text. Assign on the UI thread with the host's translated bindings.</summary>
    public Strings Strings
    {
        get => (Strings?)GetValue(StringsProperty) ?? Syno.TableView.Strings.English;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            SetValue(StringsProperty, value);
        }
    }

    internal event EventHandler? TextChanged;

    private static void OnStringsChanged(
        DependencyObject sender,
        DependencyPropertyChangedEventArgs args
    ) => ((Table)sender).RefreshText();

    private void OnColumnTextChanged(object? sender, EventArgs args) => RefreshText();

    private void RefreshText()
    {
        _headerStrip?.RefreshText();
        RefreshDragText();
        RefreshPlaceholderText();
        TextChanged?.Invoke(this, EventArgs.Empty);
    }
}
