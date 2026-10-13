using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Syno.TinyTorrent.Controls;
using Syno.TinyTorrent.Helpers;

namespace Syno.TinyTorrent.Views;

public sealed partial class ProxyDialog : UserControl
{
    public MainViewModel Model { get; }
    public Proxy Proxy => Model.Settings.Proxy;
    internal ActionButton CheckAction => Status.Check;

    public ProxyDialog(MainViewModel model)
    {
        Model = model;
        InitializeComponent();
        // The dialog shows Check and its result in its footer, outside this
        // content.
        Root.Children.Remove(Status);
        Status.Check.SetBinding(
            IsEnabledProperty,
            new Binding
            {
                Source = Proxy,
                Path = new PropertyPath(nameof(Proxy.CanCheck)),
                Mode = BindingMode.OneWay,
            }
        );
        Status.Check.Click += OnCheck;
        Status.Loaded += (_, _) =>
        {
            Proxy.PropertyChanged += OnProxyChanged;
            RefreshStatus();
        };
        Status.Unloaded += (_, _) => Proxy.PropertyChanged -= OnProxyChanged;
        Password.Loaded += (_, _) => RefreshText();
        RefreshStatus();
    }

    internal void RefreshText()
    {
        Bindings.Update();
        RefreshStatus();
        TextEditor.RevealTip(Password, Model.Text.Get("dialog", "reveal_password"));
    }

    private void OnProxyChanged(object? sender, PropertyChangedEventArgs args) => RefreshStatus();

    private void RefreshStatus()
    {
        Status.Check.Text = Model.Text.Get("settings", "check");
        ToolTipService.SetToolTip(Status.Check, Model.Text.Get("settings", "check_tip"));
        Status.Refresh(
            Proxy.IsChecking,
            Proxy.HasResult ? Proxy.Succeeded : null,
            Proxy.Result,
            Proxy.ResultTip
        );
    }

    private async void OnCheck(object sender, RoutedEventArgs args) => await Proxy.Check();
}
