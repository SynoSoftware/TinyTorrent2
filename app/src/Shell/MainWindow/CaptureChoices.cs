using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Syno.TinyTorrent.Controls;
using Syno.TinyTorrent.Shared;
using Windows.System;

namespace Syno.TinyTorrent.Shell;

public sealed partial class MainWindow
{
    private async Task CaptureChoices(List<object> outcomes)
    {
        var selected = false;
        var enabled = false;
        var commands = 0;
        var closes = 0;
        var unloads = 0;
        var menu = MenuItems.Flyout(Describe, () => Model.Text);
        menu.Closing += (_, _) => closes++;
        menu.ShowAt(Root);
        try
        {
            await CaptureLayout();
            var outer = (MenuFlyoutSubItem)menu.Items[0];
            var outerPeer = Expand(outer);
            await CaptureLayout();
            var inner = (MenuFlyoutSubItem)outer.Items[0];
            var innerPeer = Expand(inner);
            await CaptureLayout();
            var choice = (MenuFlyoutItem)inner.Items[0];
            choice.Unloaded += (_, _) => unloads++;
            var automation = FrameworkElementAutomationPeer.CreatePeerForElement(choice);
            ((ISelectionItemProvider)automation.GetPattern(PatternInterface.SelectionItem)).Select();
            await CaptureLayout();
            Retained();
            if (!selected || !((ISelectionItemProvider)automation.GetPattern(PatternInterface.SelectionItem)).IsSelected)
                throw new InvalidOperationException("The radio choice did not update in place.");
            ((IInvokeProvider)automation.GetPattern(PatternInterface.Invoke)).Invoke();
            await CaptureLayout();
            Retained();
            if (commands != 2)
                throw new InvalidOperationException("A repeated radio activation did not execute once.");

            choice.Focus(FocusState.Keyboard);
            var activation = choice.GetType().GetField("_activation", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(choice)!;
            var key = activation.GetType().GetMethod("Key", BindingFlags.Instance | BindingFlags.NonPublic)!;
            key.Invoke(activation, [VirtualKey.Space, true]);
            key.Invoke(activation, [VirtualKey.Space, true]);
            if (commands != 2)
                throw new InvalidOperationException("A held key executed before release.");
            key.Invoke(activation, [VirtualKey.Space, false]);
            await CaptureLayout();
            Retained();
            if (commands != 3 || !ReferenceEquals(Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(Root.XamlRoot), choice))
                throw new InvalidOperationException("Keyboard activation lost focus or executed more than once.");

            innerPeer.Collapse();
            await CaptureLayout();
            var checks = (MenuFlyoutSubItem)outer.Items[1];
            var checksPeer = Expand(checks);
            await CaptureLayout();
            var toggle = (ToggleMenuFlyoutItem)checks.Items[0];
            toggle.Unloaded += (_, _) => unloads++;
            var before = unloads;
            var togglePeer = FrameworkElementAutomationPeer.CreatePeerForElement(toggle);
            ((IToggleProvider)togglePeer.GetPattern(PatternInterface.Toggle)).Toggle();
            await CaptureLayout();
            if (!enabled || !toggle.IsChecked || closes != 0 || unloads != before
                || checksPeer.ExpandCollapseState != ExpandCollapseState.Expanded)
                throw new InvalidOperationException("A native checkbox dismissed its submenu or lost its state.");

            var radio = (ToggleMenuFlyoutItem)checks.Items[1];
            var radioPeer = FrameworkElementAutomationPeer.CreatePeerForElement(radio);
            ((ISelectionItemProvider)radioPeer.GetPattern(PatternInterface.SelectionItem)).Select();
            await CaptureLayout();
            if (selected || !radio.IsChecked || closes != 0 || unloads != before
                || checksPeer.ExpandCollapseState != ExpandCollapseState.Expanded)
                throw new InvalidOperationException("A native radio dismissed its submenu or lost its state.");
            ((IToggleProvider)radioPeer.GetPattern(PatternInterface.Toggle)).Toggle();
            await CaptureLayout();
            if (closes != 0 || unloads != before
                || checksPeer.ExpandCollapseState != ExpandCollapseState.Expanded)
                throw new InvalidOperationException("The radio's toggle automation dismissed its submenu.");

            var command = (MenuFlyoutItem)checks.Items[2];
            ((IInvokeProvider)FrameworkElementAutomationPeer.CreatePeerForElement(command)
                .GetPattern(PatternInterface.Invoke)).Invoke();
            await CaptureLayout();
            if (menu.IsOpen || commands != 6)
                throw new InvalidOperationException("An ordinary command did not execute and dismiss the menu.");
            outcomes.Add(new { check = "menu-choices", commands, closes, retained = true });

            void Retained()
            {
                if (!menu.IsOpen || closes != 0 || unloads != 0
                    || outerPeer.ExpandCollapseState != ExpandCollapseState.Expanded
                    || innerPeer.ExpandCollapseState != ExpandCollapseState.Expanded
                    || !ReferenceEquals(choice, inner.Items[0]))
                    throw new InvalidOperationException("A radio choice closed or rebuilt its submenu.");
            }
        }
        finally
        {
            menu.Hide();
        }

        var picker = MenuItems.Picker(() => [Choice(null)], () => Model.Text);
        picker.ShowAt(Root);
        try
        {
            await CaptureLayout();
            var choice = (MenuFlyoutItem)picker.Items[0];
            ((IInvokeProvider)FrameworkElementAutomationPeer.CreatePeerForElement(choice)
                .GetPattern(PatternInterface.Invoke)).Invoke();
            await CaptureLayout();
            if (picker.IsOpen || !selected)
                throw new InvalidOperationException("A picker did not apply its choice and dismiss.");
        }
        finally
        {
            picker.Hide();
        }

        IReadOnlyList<MenuEntry> Describe() =>
        [
            new MenuSubmenu("Options", Segoe.Settings,
            [
                new MenuSubmenu("Animation", Segoe.Play, [Choice(null)]),
                new MenuSubmenu("Checks", Segoe.Settings,
                [
                    new MenuChoice("Enabled", Segoe.Check, enabled,
                        new RelayCommand(() => enabled = !enabled, () => true)),
                    new MenuChoice("Off", Segoe.Play, !selected,
                        new RelayCommand(() => { selected = false; commands++; }, () => true))
                    { Group = "MenuCapture" },
                    new MenuCommand("Done", Segoe.Check,
                        new RelayCommand(() => commands++, () => true)),
                ]),
            ]),
        ];

        MenuChoice Choice(string? glyph) => new("Normal", glyph, selected,
            new RelayCommand(() => { selected = true; commands++; }, () => true))
        { Group = "MenuCapture" };

        static IExpandCollapseProvider Expand(MenuFlyoutSubItem item)
        {
            var peer = (IExpandCollapseProvider)FrameworkElementAutomationPeer.CreatePeerForElement(item)
                .GetPattern(PatternInterface.ExpandCollapse);
            peer.Expand();
            return peer;
        }
    }
}
