using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;

namespace Syno.TinyTorrent.Controls;

/// <summary>
/// The automation peer of a custom-drawn control whose text a screen reader
/// reads as its value, which the person cannot edit.
/// </summary>
internal sealed class ValuePeer(FrameworkElement owner, Func<string> read)
    : FrameworkElementAutomationPeer(owner),
        IValueProvider
{
    protected override string GetClassNameCore() => Owner.GetType().Name;

    protected override AutomationControlType GetAutomationControlTypeCore() =>
        AutomationControlType.Custom;

    protected override object? GetPatternCore(PatternInterface pattern) =>
        pattern == PatternInterface.Value ? this : base.GetPatternCore(pattern);

    public bool IsReadOnly => true;
    public string Value => read();

    public void SetValue(string value) => throw new InvalidOperationException();

    /// <summary>Tells a screen reader that the value of <paramref name="element"/> changed.</summary>
    internal static void Announce(UIElement element, string previous, string current) =>
        FromElement(element)
            ?.RaisePropertyChangedEvent(ValuePatternIdentifiers.ValueProperty, previous, current);
}
