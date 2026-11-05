using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Syno.TinyTorrent.Controls;

internal static partial class MenuItems
{
    // Native Invoke closes the submenu before FlyoutBase.Closing can refuse it.
    // Each native base keeps its template and state; Activation owns the input.
    private sealed class Item : MenuFlyoutItem
    {
        private readonly Activation _activation;

        internal Item(Action changed) => _activation = new(this, changed);

        protected override void OnPointerPressed(PointerRoutedEventArgs args)
        {
            _activation.Press(args);
            base.OnPointerPressed(args);
        }

        protected override void OnPointerReleased(PointerRoutedEventArgs args)
        {
            _activation.Release(args);
            base.OnPointerReleased(args);
        }

        protected override void OnKeyDown(KeyRoutedEventArgs args)
        {
            if (!args.Handled)
                args.Handled = _activation.Key(args.Key, pressed: true);
            base.OnKeyDown(args);
        }

        protected override void OnKeyUp(KeyRoutedEventArgs args)
        {
            if (!args.Handled)
                args.Handled = _activation.Key(args.Key, pressed: false);
            base.OnKeyUp(args);
        }

        protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this, _activation);
    }

    // RadioMenuFlyoutItem hides its toggle base, preventing a compatible custom
    // automation peer; retained radio choices use the public toggle base instead.
    private sealed class Toggle : ToggleMenuFlyoutItem
    {
        private readonly Activation _activation;

        internal Toggle(Action changed, bool radio = false)
        {
            _activation = new(this, changed);
            IsRadio = radio;
        }

        internal bool IsRadio { get; }

        protected override void OnApplyTemplate()
        {
            base.OnApplyTemplate();
            if (IsRadio && GetTemplateChild("CheckGlyph") is FontIcon glyph)
                glyph.Glyph = Segoe.RadioButtonOn;
        }

        protected override void OnPointerPressed(PointerRoutedEventArgs args)
        {
            _activation.Press(args);
            base.OnPointerPressed(args);
        }

        protected override void OnPointerReleased(PointerRoutedEventArgs args)
        {
            _activation.Release(args);
            base.OnPointerReleased(args);
        }

        protected override void OnKeyDown(KeyRoutedEventArgs args)
        {
            if (!args.Handled)
                args.Handled = _activation.Key(args.Key, pressed: true);
            base.OnKeyDown(args);
        }

        protected override void OnKeyUp(KeyRoutedEventArgs args)
        {
            if (!args.Handled)
                args.Handled = _activation.Key(args.Key, pressed: false);
            base.OnKeyUp(args);
        }

        protected override AutomationPeer OnCreateAutomationPeer() => new CheckPeer(this, _activation);
    }

    private sealed class Activation
    {
        private readonly MenuFlyoutItem _item;
        private readonly Action _changed;
        private uint? _pointer;
        private VirtualKey? _key;

        internal Activation(MenuFlyoutItem item, Action changed)
        {
            _item = item;
            _changed = changed;
            item.PointerExited += (_, _) => _pointer = null;
            item.PointerCanceled += (_, _) => _pointer = null;
            item.PointerCaptureLost += (_, _) => _pointer = null;
            item.LostFocus += (_, _) => _key = null;
            item.Unloaded += (_, _) =>
            {
                _pointer = null;
                _key = null;
            };
            item.AccessKeyInvoked += (_, args) =>
            {
                args.Handled = true;
                Invoke();
            };
        }

        internal void Press(PointerRoutedEventArgs args)
        {
            if (args.Handled || !args.GetCurrentPoint(_item).Properties.IsLeftButtonPressed)
                return;
            _pointer = args.Pointer.PointerId;
            args.Handled = true;
            VisualStateManager.GoToState(_item, "Pressed", true);
        }

        internal void Release(PointerRoutedEventArgs args)
        {
            if (args.Handled || _pointer != args.Pointer.PointerId)
                return;
            _pointer = null;
            args.Handled = true;
            VisualStateManager.GoToState(_item, "PointerOver", true);
            _item.Focus(FocusState.Pointer);
            Invoke();
        }

        internal bool Key(VirtualKey key, bool pressed)
        {
            if (key is not (VirtualKey.Enter or VirtualKey.Space or VirtualKey.GamepadA))
                return false;
            if (pressed)
            {
                _key ??= key;
                VisualStateManager.GoToState(_item, "Pressed", true);
            }
            else if (_key == key)
            {
                _key = null;
                VisualStateManager.GoToState(_item, "Normal", true);
                Invoke();
            }
            return true;
        }

        internal void Invoke()
        {
            if (!_item.IsEnabled)
                return;
            var command = _item.Command;
            var parameter = _item.CommandParameter;
            if (command?.CanExecute(parameter) != true)
                return;
            command.Execute(parameter);
            _changed();
        }
    }

    private sealed class Peer(MenuFlyoutItem item, Activation activation)
        : MenuFlyoutItemAutomationPeer(item)
    {
        private readonly Provider _provider = new(item, activation);

        protected override object? GetPatternCore(PatternInterface pattern) =>
            _provider.Pattern(pattern) ?? base.GetPatternCore(pattern);
    }

    private sealed class CheckPeer(ToggleMenuFlyoutItem item, Activation activation)
        : ToggleMenuFlyoutItemAutomationPeer(item)
    {
        private readonly Provider _provider = new(item, activation);

        protected override object? GetPatternCore(PatternInterface pattern) =>
            _provider.Pattern(pattern) ?? base.GetPatternCore(pattern);
    }

    private sealed class Provider(MenuFlyoutItem item, Activation activation)
        : IInvokeProvider, IToggleProvider, ISelectionItemProvider
    {
        internal object? Pattern(PatternInterface pattern) => pattern switch
        {
            PatternInterface.Invoke => this,
            PatternInterface.Toggle => item.DataContext is MenuChoice ? this : null,
            PatternInterface.SelectionItem => item.DataContext is MenuChoice { Group: not null } ? this : null,
            _ => null,
        };

        public void Invoke()
        {
            if (!item.IsEnabled)
                throw new ElementNotEnabledException();
            activation.Invoke();
        }

        public void Toggle() => Invoke();

        public ToggleState ToggleState => item.DataContext is MenuChoice choice
            ? choice.State switch
            {
                true => ToggleState.On,
                false => ToggleState.Off,
                null => ToggleState.Indeterminate,
            }
            : ToggleState.Off;

        public bool IsSelected => item.DataContext is MenuChoice { State: true };

        public IRawElementProviderSimple? SelectionContainer => null;

        public void Select() => Invoke();

        public void AddToSelection() => Select();

        public void RemoveFromSelection() =>
            throw new InvalidOperationException("A radio choice cannot be cleared.");
    }
}
