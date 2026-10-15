using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Syno.TinyTorrent.Models;

namespace Syno.TinyTorrent.Services;

internal sealed partial class ProviderHttp : IDisposable
{
    internal const int JsonLimit = 2 * 1024 * 1024;
    internal const int FileLimit = 16 * 1024 * 1024;
    // How long a request waits for its reply; a download restarts the wait
    // with each block it receives.
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(60);
    private readonly object _sync = new();
    private HttpRoute? _route;
    private HttpClient? _client;
    private CancellationTokenSource? _lifetime;
    private bool _disposed;

    internal void Configure(HttpRoute route)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_route == route)
                return;
            _lifetime?.Cancel();
            _client?.Dispose();
            _lifetime?.Dispose();
            _client = null;
            _route = route;
            _lifetime = new();
        }
    }

    // Redirects return to the supplier, which authorizes the next origin and
    // creates fresh headers; credentials never follow a redirect implicitly.
    internal Task<ProviderReply> Send(ProviderCall call, int maximum, CancellationToken cancellation) =>
        Send(call.Message, maximum, cancellation, call);

    internal async Task<ProviderReply> Send(
        HttpRequestMessage request,
        int maximum,
        CancellationToken cancellation,
        ProviderCall? call = null
    )
    {
        if (maximum <= 0 || maximum > FileLimit)
            throw new ArgumentOutOfRangeException(nameof(maximum));
        return await Exchange(
            request,
            async (response, deadline) =>
            {
                var headers = Headers(response);
                if (call?.Observe is { } observe)
                    await observe(new(response.StatusCode, [], headers)).ConfigureAwait(false);
                if (response.Content.Headers.ContentLength > maximum)
                    throw new InvalidDataException("Provider response exceeds its size limit.");
                await using var stream = await response
                    .Content.ReadAsStreamAsync(deadline.Token)
                    .ConfigureAwait(false);
                byte[] bytes;
                try
                {
                    bytes = await Read(stream, maximum, deadline.Token).ConfigureAwait(false);
                }
                catch (IOException) when (call is not null)
                {
                    await call.Policy.Fail(call.Request.Access, new(RequestBlockKind.Backoff)).ConfigureAwait(false);
                    throw;
                }
                return new ProviderReply(response.StatusCode, bytes, headers);
            },
            cancellation,
            call
        ).ConfigureAwait(false);
    }

    /// <summary>
    /// Downloads a large file to <paramref name="path"/>, reports the bytes
    /// received so far and returns the file's length. Unlike
    /// <see cref="Send"/>, it fails only when no bytes arrive for a minute, so
    /// a slow line can finish a large file.
    /// </summary>
    internal async Task<long> Download(
        ProviderCall call,
        string path,
        Action<long> received,
        CancellationToken cancellation
    )
    {
        return await Exchange(
                call.Message,
                async (response, deadline) =>
                {
                    if (call.Observe is { } observe)
                        await observe(new(response.StatusCode, [], Headers(response))).ConfigureAwait(false);
                    if (response.StatusCode != HttpStatusCode.OK)
                        throw new HttpRequestException(
                            "The download was refused.",
                            null,
                            response.StatusCode
                        );
                    await using var source = await response
                        .Content.ReadAsStreamAsync(deadline.Token)
                        .ConfigureAwait(false);
                    await using var target = new FileStream(
                        path,
                        FileMode.Create,
                        FileAccess.Write,
                        FileShare.None,
                        1 << 16,
                        useAsync: true
                    );
                    var buffer = new byte[1 << 16];
                    long total = 0;
                    while (true)
                    {
                        int count;
                        try
                        {
                            count = await source.ReadAsync(buffer, deadline.Token).ConfigureAwait(false);
                        }
                        catch (IOException)
                        {
                            await call.Policy.Fail(call.Request.Access, new(RequestBlockKind.Backoff)).ConfigureAwait(false);
                            throw;
                        }
                        if (count == 0)
                            break;
                        deadline.CancelAfter(WaitLimit);
                        await target
                            .WriteAsync(buffer.AsMemory(0, count), deadline.Token)
                            .ConfigureAwait(false);
                        total += count;
                        received(total);
                    }
                    await call.Policy.Succeed(call.Request.Access).ConfigureAwait(false);
                    return total;
                },
                cancellation,
                call
            )
            .ConfigureAwait(false);
    }

    // Sends on the confirmed route and lets read consume the response before
    // the deadline, which a timeout, the caller or a route change cancels.
    private async Task<T> Exchange<T>(
        HttpRequestMessage request,
        Func<HttpResponseMessage, CancellationTokenSource, Task<T>> read,
        CancellationToken cancellation,
        ProviderCall? call = null
    )
    {
        if (request.RequestUri is not { IsAbsoluteUri: true, Scheme: "https", UserInfo: "" })
            throw new InvalidOperationException(
                "Provider requests require HTTPS without URL credentials."
            );
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_route is null || _lifetime is null)
                throw new HttpRouteException("The confirmed network route is not available.");
        }
        if (call is not null)
            await call.Policy.Reserve(call.Request, call.Fresh, cancellation).ConfigureAwait(false);
        Task<HttpResponseMessage> pending;
        CancellationTokenSource deadline;
        CancellationToken route = default;
        try
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_route is null || _lifetime is null)
                    throw new HttpRouteException("The confirmed network route is not available.");
                _client ??= Create(_route);
                route = _lifetime.Token;
                deadline = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellation,
                    _lifetime.Token
                );
                deadline.CancelAfter(WaitLimit);
                try
                {
                    pending = _client.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        deadline.Token
                    );
                }
                catch
                {
                    deadline.Dispose();
                    throw;
                }
            }
            using (deadline)
            using (var response = await pending.ConfigureAwait(false))
            {
                return await read(response, deadline).ConfigureAwait(false);
            }
        }
        catch (HttpRequestException error) when (error.InnerException is HttpRouteException)
        {
            throw new HttpRouteException("The selected network route is unavailable.", error);
        }
        catch (OperationCanceledException error) when (!cancellation.IsCancellationRequested)
        {
            if (route.IsCancellationRequested)
            {
                System.Diagnostics.Debug.WriteLine($"Provider request cancelled ({request.RequestUri?.Host}): network route changed.");
                throw new HttpRouteException("The selected network route changed.", error);
            }
            System.Diagnostics.Debug.WriteLine($"Provider request timed out or was interrupted ({request.RequestUri?.Host}).");
            if (call is not null)
                await call.Policy.Fail(call.Request.Access, new(RequestBlockKind.Backoff)).ConfigureAwait(false);
            throw new HttpRequestException("Provider request was interrupted.", error);
        }
        catch (HttpRequestException error) when (error.StatusCode is null)
        {
            if (call is not null)
                await call.Policy.Fail(call.Request.Access, new(RequestBlockKind.Backoff)).ConfigureAwait(false);
            throw;
        }
    }

    private static Dictionary<string, string> Headers(HttpResponseMessage response) =>
        response.Headers.Concat(response.Content.Headers).ToDictionary(
            pair => pair.Key, pair => string.Join(", ", pair.Value), StringComparer.OrdinalIgnoreCase);

    internal static async Task<byte[]> Read(
        Stream stream,
        int maximum,
        CancellationToken cancellation
    )
    {
        using var output = new MemoryStream();
        var buffer = new byte[16384];
        while (true)
        {
            var count = await stream
                .ReadAsync(
                    buffer.AsMemory(0, Math.Min(buffer.Length, maximum - (int)output.Length + 1)),
                    cancellation
                )
                .ConfigureAwait(false);
            if (count == 0)
                return output.ToArray();
            if (output.Length + count > maximum)
                throw new InvalidDataException("Provider response exceeds its size limit.");
            output.Write(buffer, 0, count);
        }
    }

    private static HttpClient Create(HttpRoute route)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            MaxResponseHeadersLength = 32,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            UseProxy = route.Type != ProxyType.None,
        };
        if (route.Type != ProxyType.None)
        {
            var scheme = route.Type switch
            {
                ProxyType.Socks5 => "socks5",
                ProxyType.Socks4 => "socks4a",
                ProxyType.Http => "http",
                _ => throw new HttpRouteException("Unsupported proxy type."),
            };
            if (string.IsNullOrWhiteSpace(route.Host) || route.Port is < 1 or > 65535)
                throw new HttpRouteException("The confirmed proxy is incomplete.");
            handler.Proxy = new WebProxy(new UriBuilder(scheme, route.Host, route.Port).Uri)
            {
                Credentials =
                    route.Username.Length == 0
                        ? null
                        : new NetworkCredential(route.Username, route.Password),
                BypassProxyOnLocal = false,
            };
        }
        if (route.Adapter.Length > 0)
            handler.ConnectCallback = (context, cancellation) =>
                Connect(context.DnsEndPoint, route.Adapter, cancellation);
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(App.UserAgent);
        return client;
    }

    private static async ValueTask<Stream> Connect(
        DnsEndPoint endpoint,
        string adapterId,
        CancellationToken cancellation
    )
    {
        try
        {
            return await ConnectAdapter(endpoint, adapterId, cancellation).ConfigureAwait(false);
        }
        catch (Exception error)
            when (error is not HttpRouteException
                && error is IOException or System.ComponentModel.Win32Exception
            )
        {
            throw new HttpRouteException(
                "The selected network adapter cannot reach the provider route.",
                error
            );
        }
    }

    private static async ValueTask<Stream> ConnectAdapter(
        DnsEndPoint endpoint,
        string adapterId,
        CancellationToken cancellation
    )
    {
        var adapter = NetworkInterface
            .GetAllNetworkInterfaces()
            .FirstOrDefault(candidate =>
                string.Equals(candidate.Id, adapterId, StringComparison.OrdinalIgnoreCase)
            );
        if (adapter is null || adapter.OperationalStatus != OperationalStatus.Up)
            throw new HttpRouteException("The selected network adapter is unavailable.");
        var properties = adapter.GetIPProperties();
        var local = properties.UnicastAddresses.Select(address => address.Address).ToArray();
        var index = adapter.Supports(NetworkInterfaceComponent.IPv4)
            ? properties.GetIPv4Properties().Index
            : properties.GetIPv6Properties().Index;
        var addresses = IPAddress.TryParse(endpoint.Host, out var literal)
            ? [literal]
            : await Resolve(endpoint.Host, (uint)index, cancellation).ConfigureAwait(false);
        Exception? failure = null;
        foreach (var address in addresses)
        {
            foreach (
                var source in local.Where(value =>
                    value.AddressFamily == address.AddressFamily
                    && (
                        address.AddressFamily != AddressFamily.InterNetworkV6
                        || value.IsIPv6LinkLocal == address.IsIPv6LinkLocal
                    )
                )
            )
            {
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    // A source address alone can still route through another adapter.
                    var family = address.AddressFamily == AddressFamily.InterNetwork;
                    var routeIndex = family
                        ? properties.GetIPv4Properties().Index
                        : properties.GetIPv6Properties().Index;
                    // Winsock's IP_UNICAST_IF and IPV6_UNICAST_IF share option 31,
                    // which SocketOptionName does not expose.
                    socket.SetSocketOption(
                        family ? SocketOptionLevel.IP : SocketOptionLevel.IPv6,
                        (SocketOptionName)31,
                        family ? IPAddress.HostToNetworkOrder(routeIndex) : routeIndex
                    );
                    socket.Bind(new IPEndPoint(source, 0));
                    await socket
                        .ConnectAsync(new IPEndPoint(address, endpoint.Port), cancellation)
                        .ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch (Exception error)
                    when (error is SocketException or OperationCanceledException)
                {
                    socket.Dispose();
                    cancellation.ThrowIfCancellationRequested();
                    failure = error;
                }
            }
        }
        throw new IOException(
            "The selected network adapter cannot reach the provider route.",
            failure
        );
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            _lifetime?.Cancel();
            _client?.Dispose();
            _lifetime?.Dispose();
        }
    }
}

internal sealed record HttpRoute
{
    internal ProxyType Type { get; init; }
    internal string Host { get; init; } = string.Empty;
    internal int Port { get; init; }
    internal string Username { get; init; } = string.Empty;
    internal string Password { get; init; } = string.Empty;
    internal string Adapter { get; init; } = string.Empty;

    public override string ToString() => nameof(HttpRoute);
}

internal sealed class HttpRouteException(string message, Exception? inner = null)
    : IOException(message, inner);

internal sealed record ProviderCall(HttpRequestMessage Message, RequestPolicy Policy, OutboundRequest Request)
{
    internal FreshRequest? Fresh { get; init; }
    internal Func<ProviderReply, Task>? Observe { get; init; }
}

internal sealed record ProviderReply(
    HttpStatusCode Status,
    byte[] Bytes,
    IReadOnlyDictionary<string, string> Headers
)
{
    internal string? Header(string name) => Headers.GetValueOrDefault(name);

    internal DateTimeOffset? RetryAt
    {
        get
        {
            var now = DateTimeOffset.UtcNow;
            var value = Header("Retry-After");
            if (
                long.TryParse(
                    value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var seconds
                )
            )
                return
                    seconds > 0
                    && seconds <= (DateTimeOffset.MaxValue - now).Ticks / TimeSpan.TicksPerSecond
                    ? now.AddSeconds(seconds)
                    : null;
            return
                DateTimeOffset.TryParse(
                    value,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal,
                    out var date
                )
                && date > now
                ? date
                : null;
        }
    }
}
