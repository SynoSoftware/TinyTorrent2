using Microsoft.UI.Xaml;

namespace Synapse_Tests;

/// <summary>
/// The XAML application that hosts the tests.
/// </summary>
/// <remarks>
/// The WinUI unit-test template starts the MSTest in-app runner from <c>OnLaunched</c>. That path
/// needs a packaged host. This project instead runs under <c>dotnet test</c>, so the application is
/// started by <see cref="TestHost"/> on a private UI thread and only supplies the XAML resources,
/// the window, and the dispatcher.
/// </remarks>
public partial class UnitTestApp : Application
{
    public UnitTestApp()
    {
        InitializeComponent();
        UnhandledException += (_, e) => TestHost.OnUnhandledException(e);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args) => TestHost.OnAppLaunched();
}
