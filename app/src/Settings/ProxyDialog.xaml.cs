using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Syno.TinyTorrent.Controls;
using Syno.TinyTorrent.Shell;

namespace Syno.TinyTorrent.Settings;

public sealed partial class ProxyDialog : UserControl
{
    public MainViewModel Model { get; }
    public Proxy Proxy => Model.Settings.Proxy;

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
        Proxy.PropertyChanged += OnProxyChanged;
        Password.Loaded += (_, _) => RefreshText();
        RefreshStatus();
    }

    // The proxy outlives the dialog, so the dialog's opener detaches it when
    // the dialog closes.
    internal void Detach() => Proxy.PropertyChanged -= OnProxyChanged;

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
        var feedback = Proxy.IsChecking
            ? Model.Text.Get("settings", "proxy_checking")
            : string.Empty;
        Status.Refresh(Proxy.IsChecking, null, feedback, feedback);
    }

    private async void OnCheck(object sender, RoutedEventArgs args) => await Proxy.Check();
}
