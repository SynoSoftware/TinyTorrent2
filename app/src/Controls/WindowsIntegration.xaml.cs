using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Syno.TinyTorrent.Views;

namespace Syno.TinyTorrent.Controls;

public sealed partial class WindowsIntegration : UserControl
{
    private bool _refreshing;
    public Settings Model { get; }
    internal SettingsSection Section => IntegrationSection;

    internal WindowsIntegration(Settings model)
    {
        Model = model;
        InitializeComponent();
        Loaded += (_, _) =>
        {
            Model.PropertyChanged += OnModel;
            Refresh();
        };
        Unloaded += (_, _) => Model.PropertyChanged -= OnModel;
    }

    public static Visibility HasText(string text) =>
        text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    private void OnModel(object? sender, PropertyChangedEventArgs args)
    {
        if (string.IsNullOrEmpty(args.PropertyName))
            Refresh();
    }

    private void Refresh()
    {
        _refreshing = true;
        Bindings.Update();
        _refreshing = false;
    }

    internal Control? Field(string name) =>
        name switch
        {
            "start_signin" => Startup,
            "open_defaults" => Handlers,
            _ => null,
        };

    private async void OnStartup(object sender, RoutedEventArgs args)
    {
        if (!_refreshing && Model.HasRegistration && Startup.IsOn != Model.Startup)
            await Model.SetStartup(Startup.IsOn);
    }

    private async void OnHandlers(object sender, RoutedEventArgs args)
    {
        if (!_refreshing && Model.HasRegistration && Handlers.IsOn != Model.Handlers)
            await Model.SetHandlers(Handlers.IsOn);
    }
}
