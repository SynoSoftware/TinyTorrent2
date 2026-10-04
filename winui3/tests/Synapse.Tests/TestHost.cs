using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinRT;

namespace Synapse_Tests;

/// <summary>
/// Starts one WinUI application on a private UI thread for the whole test assembly and marshals
/// test bodies onto it.
/// </summary>
/// <remarks>
/// <para>
/// <c>dotnet test</c> loads the test assembly into <c>testhost.exe</c>, which never runs this
/// project's generated <c>Main</c>. Without a running <see cref="Application"/> there is no XAML
/// runtime, so no <see cref="Control"/> can be constructed. This class supplies one.
/// </para>
/// <para>
/// Two project settings make that possible. <c>WindowsPackageType=None</c> makes the Windows App
/// SDK module initializer use the unpackaged bootstrapper instead of the deployment manager, which
/// needs the package identity <c>testhost.exe</c> does not have.
/// <c>ProjectPriFileName=resources.pri</c> puts the merged resource index where
/// <c>ResourceLoader.GetDefaultResourceFilePath()</c> looks for it.
/// </para>
/// </remarks>
[TestClass]
public static class TestHost
{
    private static readonly TaskCompletionSource<bool> Ready = new();
    private static UnitTestAppWindow? _window;
    private static TaskCompletionSource<Exception>? _trap;
    private static Exception? _unexpected;

    /// <summary>The dispatcher of the single UI thread every UI test runs on.</summary>
    public static DispatcherQueue Dispatcher { get; private set; } = null!;

    /// <summary>A live panel in the window. Adding a control here makes <c>Loaded</c> fire.</summary>
    public static Grid RootPanel => _window!.RootPanel;

    [AssemblyInitialize]
    public static void Start(TestContext context)
    {
        _ = context;

        Thread thread = new(() =>
        {
            ComWrappersSupport.InitializeComWrappers();
            Application.Start(callbackParams =>
            {
                _ = callbackParams;
                DispatcherQueue queue = DispatcherQueue.GetForCurrentThread();
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherQueueSynchronizationContext(queue));
                _ = new UnitTestApp();
            });
        })
        {
            IsBackground = true,
            Name = "Synapse test UI thread",
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        if (!Ready.Task.Wait(TimeSpan.FromSeconds(60)))
        {
            throw new InvalidOperationException("The test UI thread did not start within 60 s.");
        }
    }

    internal static void OnAppLaunched()
    {
        _window = new UnitTestAppWindow();
        _window.Activate();
        Dispatcher = _window.DispatcherQueue;
        Ready.TrySetResult(true);
    }

    /// <summary>
    /// Take over every exception raised on the UI thread. A control that throws from a
    /// <c>Loaded</c> handler would otherwise terminate the whole test host, losing the run.
    /// An expected throw goes to the armed trap; anything else fails the surrounding test.
    /// </summary>
    internal static void OnUnhandledException(Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        e.Handled = true;

        TaskCompletionSource<Exception>? trap = Interlocked.Exchange(ref _trap, null);
        if (trap is not null)
        {
            trap.TrySetResult(e.Exception);
            return;
        }

        _unexpected ??= e.Exception;
    }

    /// <summary>Arm the trap and return the exception the UI thread throws next.</summary>
    public static Task<Exception> TrapAsync()
    {
        TaskCompletionSource<Exception> trap = new();
        _trap = trap;
        return trap.Task;
    }

    public static void DisarmTrap() => _trap = null;

    /// <summary>Run a test body on the UI thread and re-throw its failure on the test thread.</summary>
    public static Task RunAsync(Func<Task> body)
    {
        TaskCompletionSource tcs = new();

        bool enqueued = Dispatcher.TryEnqueue(async () =>
        {
            _unexpected = null;
            try
            {
                await body();

                if (_unexpected is Exception stray)
                {
                    tcs.TrySetException(new AssertFailedException(
                        "An unexpected exception reached the UI thread: " + stray));
                }
                else
                {
                    tcs.TrySetResult();
                }
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
            finally
            {
                DisarmTrap();
                RootPanel.Children.Clear();
            }
        });

        if (!enqueued)
        {
            tcs.TrySetException(new InvalidOperationException("Could not reach the test UI thread."));
        }

        return tcs.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
