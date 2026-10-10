using System.Diagnostics;
using System.Text;
using Syno.TinyTorrent.Helpers;
using Syno.TinyTorrent.Services;

namespace Syno.TinyTorrent.Library.Public;

internal sealed class Browser(ProviderHttp http)
{
    private const int Maximum = 8 * 1024 * 1024;
    internal static bool IsAvailable => Supports(ShellAssociation.Get("https", ShellAssociation.Executable));

    internal async Task<string> Read(Uri address, CancellationToken cancellation)
    {
        CancellationTokenSource route;
        try { route = http.BeginDirect(cancellation); }
        catch (HttpRouteException error)
        {
            throw new ProviderException("websites", "route_unavailable", inner: error);
        }
        using (route)
        {
            try
            {
                var html = await Task.Run(() => Capture(address, route.Token), route.Token).ConfigureAwait(false);
                route.Token.ThrowIfCancellationRequested();
                return html;
            }
            catch (OperationCanceledException error) when (!cancellation.IsCancellationRequested && route.IsCancellationRequested)
            {
                throw new ProviderException("websites", "route_unavailable", inner: error);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                cancellation.ThrowIfCancellationRequested();
                throw new ProviderException("websites", route.IsCancellationRequested ? "route_unavailable" : "unavailable", inner: error);
            }
        }
    }

    private static async Task<string> Capture(Uri address, CancellationToken cancellation)
    {
        if (address.Scheme != Uri.UriSchemeHttps || address.UserInfo.Length != 0)
            throw new ArgumentException("A website request requires HTTPS.", nameof(address));
        var executable = ShellAssociation.Get("https", ShellAssociation.Executable);
        if (!Supports(executable))
            throw new ProviderException("websites", "browser_unavailable");

        var profile = Path.Combine(Path.GetTempPath(), "TinyTorrent", "Browser", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var argument in new[] { "--headless", "--no-first-run", "--no-default-browser-check",
            "--disable-background-networking", "--dump-dom", "--timeout=15000", "--virtual-time-budget=3000",
            "--user-data-dir=" + profile, address.AbsoluteUri })
            start.ArgumentList.Add(argument);

        Process? process = null;
        Task? errors = null;
        try
        {
            cancellation.ThrowIfCancellationRequested();
            process = Process.Start(start) ?? throw new ProviderException("websites", "browser_unavailable");
            errors = Drain(process.StandardError, timeout.Token);
            var output = Read(process.StandardOutput, timeout.Token);
            await Task.WhenAll(output, process.WaitForExitAsync(timeout.Token), errors).ConfigureAwait(false);
            if (process.ExitCode != 0)
                throw new ProviderException("websites", "unavailable");
            return await output.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            throw new ProviderException("websites", "timed_out");
        }
        finally
        {
            if (process is not null)
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException) when (process.HasExited) { }
                await process.WaitForExitAsync().ConfigureAwait(false);
                process.Dispose();
            }
            timeout.Cancel();
            if (errors is not null)
            {
                try { await errors.ConfigureAwait(false); }
                catch (Exception error) when (error is OperationCanceledException or IOException) { }
            }
            try { Directory.Delete(profile, recursive: true); }
            catch (IOException error) { Debug.WriteLine("Browser profile cleanup: " + error.GetType().Name); }
            catch (UnauthorizedAccessException error) { Debug.WriteLine("Browser profile cleanup: " + error.GetType().Name); }
        }
    }

    private static bool Supports(string executable) => File.Exists(executable) &&
        (Path.GetFileName(executable).Equals("msedge.exe", StringComparison.OrdinalIgnoreCase) ||
         Path.GetFileName(executable).Equals("chrome.exe", StringComparison.OrdinalIgnoreCase));

    private static async Task<string> Read(StreamReader reader, CancellationToken cancellation)
    {
        var text = new StringBuilder();
        var buffer = new char[8192];
        int count;
        while ((count = await reader.ReadAsync(buffer, cancellation).ConfigureAwait(false)) > 0)
        {
            if (text.Length + count > Maximum)
                throw Provider.Invalid();
            text.Append(buffer, 0, count);
        }
        return text.ToString();
    }

    private static async Task Drain(StreamReader reader, CancellationToken cancellation)
    {
        var buffer = new char[4096];
        while (await reader.ReadAsync(buffer, cancellation).ConfigureAwait(false) > 0) { }
    }
}
