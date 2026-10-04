using System.Reflection;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Synapse;
using Windows.Foundation;

namespace Synapse_Tests;

/// <summary>
/// Reaches the parts of the control an independent verification pass has to drive, and records
/// every measurement it takes so the run leaves an auditable trace.
/// </summary>
/// <remarks>
/// Synapse grants no <c>InternalsVisibleTo</c> and the pointer gestures are private handlers taking
/// <c>PointerRoutedEventArgs</c>, which cannot be constructed. Reflection reaches the same methods a
/// real pointer event would reach, one step inside the event unwrapping.
/// </remarks>
internal static class Proof
{
    private static readonly StringBuilder Log = new();

    internal static void Note(string line)
    {
        lock (Log)
        {
            Log.AppendLine(line);
        }

        Console.WriteLine(line);
    }

    internal static void Flush(string path)
    {
        lock (Log)
        {
            File.WriteAllText(path, Log.ToString());
        }
    }

    // ---------------------------------------------------------------- reflection

    internal static object? Call(object target, string method, params object?[] args)
    {
        MethodInfo info = Find(target.GetType(), method)
            ?? throw new MissingMethodException(target.GetType().Name, method);
        return info.Invoke(target, args);
    }

    internal static T Field<T>(object target, string name) =>
        (T)FindField(target.GetType(), name).GetValue(target)!;

    internal static void SetField(object target, string name, object? value) =>
        FindField(target.GetType(), name).SetValue(target, value);

    private static MethodInfo? Find(Type type, string name)
    {
        for (Type? t = type; t is not null; t = t.BaseType)
        {
            MethodInfo? found = t.GetMethod(
                name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private static FieldInfo FindField(Type type, string name)
    {
        for (Type? t = type; t is not null; t = t.BaseType)
        {
            FieldInfo? found = t.GetField(
                name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (found is not null)
            {
                return found;
            }
        }

        throw new MissingFieldException(type.Name, name);
    }

    // ---------------------------------------------------------------- tree

    internal static T? Descendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                return match;
            }

            if (Descendant<T>(child) is T deeper)
            {
                return deeper;
            }
        }

        return null;
    }

    internal static List<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        List<T> found = new();
        Walk(root, found);
        return found;

        static void Walk(DependencyObject node, List<T> into)
        {
            int count = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(node, i);
                if (child is T match)
                {
                    into.Add(match);
                }

                Walk(child, into);
            }
        }
    }

    // ---------------------------------------------------------------- menu invocation

    /// <summary>
    /// Invoke a menu item the way a click on it does: through its own automation peer, which is
    /// what raises <c>Click</c>. Constructing a <c>RoutedEventArgs</c> for it is not possible.
    /// </summary>
    internal static void InvokeMenuItem(MenuFlyoutItemBase item)
    {
        AutomationPeer peer = FrameworkElementAutomationPeer.CreatePeerForElement(item)
            ?? throw new InvalidOperationException($"No peer for '{Label(item)}'.");

        if (peer.GetPattern(PatternInterface.Toggle) is IToggleProvider toggle)
        {
            toggle.Toggle();
            return;
        }

        if (peer.GetPattern(PatternInterface.Invoke) is IInvokeProvider invoke)
        {
            invoke.Invoke();
            return;
        }

        throw new InvalidOperationException($"'{Label(item)}' exposes no invoke or toggle pattern.");
    }

    internal static string Label(MenuFlyoutItemBase item) => item switch
    {
        MenuFlyoutSeparator => "---",
        MenuFlyoutSubItem sub => sub.Text,
        MenuFlyoutItem plain => plain.Text,
        _ => item.GetType().Name,
    };

    internal static string Describe(MenuFlyoutItemBase item)
    {
        string check = item is MenuFlyoutItem { Icon: not null } ? "[x] " : string.Empty;
        string enabled = item.IsEnabled ? string.Empty : " (disabled)";
        return check + Label(item) + enabled;
    }

    // ---------------------------------------------------------------- geometry

    internal static Rect BoundsIn(FrameworkElement element, UIElement reference) => element
        .TransformToVisual(reference)
        .TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));

    internal static double TranslateX(UIElement element) =>
        element.RenderTransform is TranslateTransform t ? t.X : 0;

    internal static double TranslateY(UIElement element) =>
        element.RenderTransform is TranslateTransform t ? t.Y : 0;
}
