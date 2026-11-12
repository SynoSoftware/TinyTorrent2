using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI.Input.Preview.Injection;

namespace Syno.TableView.Tests;

/// <summary>
/// Reaches the parts of the control an independent verification pass has to drive, and records
/// every measurement it takes so the run leaves an auditable trace.
/// </summary>
/// <remarks>
/// TableView grants no <c>InternalsVisibleTo</c> and the pointer gestures are private handlers taking
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
        MethodInfo info =
            Find(target.GetType(), method)
            ?? throw new MissingMethodException(target.GetType().Name, method);
        return info.Invoke(target, args);
    }

    internal static T Field<T>(object target, string name) =>
        (T)FindField(target.GetType(), name).GetValue(target)!;

    internal static void SetField(object target, string name, object? value) =>
        FindField(target.GetType(), name).SetValue(target, value);

    /// <summary>
    /// The named member of the enum a field holds. The field supplies the type, so a test never
    /// names an internal enum, and renaming or moving that enum cannot break the lookup.
    /// </summary>
    internal static object Parse(object target, string field, string member) =>
        Enum.Parse(FindField(target.GetType(), field).FieldType, member);

    private static MethodInfo? Find(Type type, string name)
    {
        for (Type? t = type; t is not null; t = t.BaseType)
        {
            MethodInfo? found = t.GetMethod(
                name,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public
            );
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
                name,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public
            );
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
        AutomationPeer peer =
            FrameworkElementAutomationPeer.CreatePeerForElement(item)
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

        throw new InvalidOperationException(
            $"'{Label(item)}' exposes no invoke or toggle pattern."
        );
    }

    internal static string Label(MenuFlyoutItemBase item) =>
        item switch
        {
            MenuFlyoutSeparator => "---",
            MenuFlyoutSubItem sub => sub.Text,
            MenuFlyoutItem plain => plain.Text,
            _ => item.GetType().Name,
        };

    // ---------------------------------------------------------------- input injection

    /// <summary>The handle of the window that shows <paramref name="element"/>.</summary>
    internal static IntPtr WindowHandle(FrameworkElement element) =>
        Win32Interop.GetWindowFromWindowId(element.XamlRoot.ContentIslandEnvironment.AppWindowId);

    /// <summary>
    /// Bring <paramref name="element"/>'s window to the foreground and return an injector for real
    /// input. Injected input goes to the foreground window, so the test ends inconclusive rather
    /// than inject anything while another window holds it.
    /// </summary>
    internal static async Task<InputInjector> TakeForegroundAsync(FrameworkElement element)
    {
        IntPtr window = WindowHandle(element);

        SetForegroundWindow(window);
        await Task.Delay(250);

        if (GetForegroundWindow() != window)
        {
            Assert.Inconclusive(
                "The test window could not take the foreground, so real input could not be "
                    + "injected without sending it to another application."
            );
        }

        return InputInjector.TryCreate()
            ?? throw new AssertFailedException("InputInjector.TryCreate() returned null.");
    }

    /// <summary>
    /// One press of <paramref name="key"/>, inside a hold of <paramref name="modifier"/> when one is
    /// given. The caller waits for its effect.
    /// </summary>
    internal static void Press(InputInjector injector, VirtualKey key, VirtualKey? modifier = null)
    {
        List<InjectedInputKeyboardInfo> keys = new();
        if (modifier is VirtualKey held)
        {
            keys.Add(Key(held, up: false));
        }

        keys.Add(Key(key, up: false));
        keys.Add(Key(key, up: true));
        if (modifier is VirtualKey release)
        {
            keys.Add(Key(release, up: true));
        }

        injector.InjectKeyboardInput(keys);
    }

    internal static InjectedInputKeyboardInfo Key(VirtualKey key, bool up) =>
        new()
        {
            VirtualKey = (ushort)key,
            KeyOptions = up ? InjectedInputKeyOptions.KeyUp : InjectedInputKeyOptions.None,
        };

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    // ---------------------------------------------------------------- geometry

    internal static double TranslateX(UIElement element) =>
        element.RenderTransform is TranslateTransform t ? t.X : 0;

    internal static double TranslateY(UIElement element) =>
        element.RenderTransform is TranslateTransform t ? t.Y : 0;
}

/// <summary>One rendered frame of an element, addressed in device-independent pixels.</summary>
internal sealed class Shot
{
    private byte[] _pixels = Array.Empty<byte>();

    private int Width { get; set; }

    private int Height { get; set; }

    private double Scale { get; set; } = 1;

    internal static async Task<Shot> TakeAsync(FrameworkElement element)
    {
        RenderTargetBitmap bitmap = new();
        await bitmap.RenderAsync(element);
        IBuffer buffer = await bitmap.GetPixelsAsync();
        byte[] pixels = new byte[buffer.Length];
        DataReader.FromBuffer(buffer).ReadBytes(pixels);

        Shot shot = new()
        {
            _pixels = pixels,
            Width = bitmap.PixelWidth,
            Height = bitmap.PixelHeight,
            Scale = element.XamlRoot?.RasterizationScale ?? 1,
        };

        Assert.IsTrue(
            shot.Width > 0 && shot.Height > 0,
            "The control rendered an empty bitmap."
        );
        return shot;
    }

    internal int X(double dips) => Math.Clamp((int)Math.Round(dips * Scale), 0, Width - 1);

    internal int Y(double dips) => Math.Clamp((int)Math.Round(dips * Scale), 0, Height - 1);

    /// <summary>One pixel's colour as 0xRRGGBB.</summary>
    internal uint At(int x, int y)
    {
        int i = ((y * Width) + x) * 4;
        return (uint)(_pixels[i] | (_pixels[i + 1] << 8) | (_pixels[i + 2] << 16));
    }

    /// <summary>
    /// The strongest contrast against <paramref name="reference"/> inside <paramref name="area"/>,
    /// given in DIPs. An area of height 0 is one scan.
    /// </summary>
    internal double Contrast(uint reference, Rect area)
    {
        double best = 0;
        for (int y = Y(area.Top); y <= Y(area.Bottom); y++)
        {
            for (int x = X(area.Left); x <= X(area.Right); x++)
            {
                best = Math.Max(best, Ratio(At(x, y), reference));
            }
        }

        return best;
    }

    /// <summary>How many pixels of one scan are painted with something other than the background.</summary>
    internal int DrawnWidth(uint background, double fromDips, double toDips, double atDips)
    {
        int drawn = 0;
        int y = Y(atDips);
        for (int x = X(fromDips); x <= X(toDips); x++)
        {
            if (Ratio(At(x, y), background) > 1.1)
            {
                drawn++;
            }
        }

        return drawn;
    }

    private static double Ratio(uint a, uint b)
    {
        double first = Luminance(a);
        double second = Luminance(b);
        return (Math.Max(first, second) + 0.05) / (Math.Min(first, second) + 0.05);
    }

    private static double Luminance(uint rgb) =>
        (0.2126 * Linear((rgb >> 16) & 0xFF))
        + (0.7152 * Linear((rgb >> 8) & 0xFF))
        + (0.0722 * Linear(rgb & 0xFF));

    private static double Linear(uint channel)
    {
        double value = channel / 255.0;
        return value <= 0.03928 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
    }
}
