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

    internal CancellationTokenSource BeginDirect(CancellationToken cancellation)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellation.ThrowIfCancellationRequested();
            if (_route is null || _lifetime is null || _route.Type != ProxyType.None || _route.Adapter.Length > 0)
                throw new HttpRouteException("The confirmed network route does not allow a direct browser request.");
            return CancellationTokenSource.CreateLinkedTokenSource(cancellation, _lifetime.Token);
        }
    }

    // Redirects return to the supplier, which authorizes the next origin and
    // creates fresh headers; credentials never follow a redirect implicitly.
    internal async Task<ProviderReply> Send(HttpRequestMessage request, int maximum,
        CancellationToken cancellation)
    {
        if (request.RequestUri is not { IsAbsoluteUri: true, Scheme: "https", UserInfo: "" })
            throw new InvalidOperationException("Provider requests require HTTPS without URL credentials.");
        if (maximum <= 0 || maximum > FileLimit)
            throw new ArgumentOutOfRangeException(nameof(maximum));

        Task<HttpResponseMessage> pending;
        CancellationTokenSource deadline;
        try
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_route is null || _lifetime is null)
                    throw new HttpRouteException("The confirmed network route is not available.");
                _client ??= Create(_route);
                deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _lifetime.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(60));
                try
                {
                    pending = _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
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
                if (response.Content.Headers.ContentLength > maximum)
                    throw new InvalidDataException("Provider response exceeds its size limit.");
                await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
                var bytes = await Read(stream, maximum, deadline.Token).ConfigureAwait(false);
                var headers = response.Headers.Concat(response.Content.Headers)
                    .ToDictionary(pair => pair.Key, pair => string.Join(", ", pair.Value), StringComparer.OrdinalIgnoreCase);
                return new ProviderReply(response.StatusCode, bytes, headers);
            }
        }
        catch (HttpRequestException error) when (error.InnerException is HttpRouteException)
        {
            throw new HttpRouteException("The selected network route is unavailable.", error);
        }
        catch (OperationCanceledException error) when (!cancellation.IsCancellationRequested)
        {
            throw new HttpRequestException("Provider request was interrupted.", error);
        }
    }

    internal static async Task<byte[]> Read(Stream stream, int maximum, CancellationToken cancellation)
    {
        using var output = new MemoryStream();
        var buffer = new byte[16384];
        while (true)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, maximum - (int)output.Length + 1)),
                cancellation).ConfigureAwait(false);
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
                Credentials = route.Username.Length == 0 ? null : new NetworkCredential(route.Username, route.Password),
                BypassProxyOnLocal = false,
            };
        }
        if (route.Adapter.Length > 0)
            handler.ConnectCallback = (context, cancellation) => Connect(context.DnsEndPoint, route.Adapter, cancellation);
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(App.UserAgent);
        return client;
    }

    private static async ValueTask<Stream> Connect(DnsEndPoint endpoint, string adapterId,
        CancellationToken cancellation)
    {
        try { return await ConnectAdapter(endpoint, adapterId, cancellation).ConfigureAwait(false); }
        catch (Exception error) when (error is not HttpRouteException &&
            error is IOException or System.ComponentModel.Win32Exception)
        {
            throw new HttpRouteException("The selected network adapter cannot reach the provider route.", error);
        }
    }

    private static async ValueTask<Stream> ConnectAdapter(DnsEndPoint endpoint, string adapterId,
        CancellationToken cancellation)
    {
        var adapter = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(candidate =>
            string.Equals(candidate.Id, adapterId, StringComparison.OrdinalIgnoreCase));
        if (adapter is null || adapter.OperationalStatus != OperationalStatus.Up)
            throw new HttpRouteException("The selected network adapter is unavailable.");
        var properties = adapter.GetIPProperties();
        var local = properties.UnicastAddresses.Select(address => address.Address).ToArray();
        var index = adapter.Supports(NetworkInterfaceComponent.IPv4)
            ? properties.GetIPv4Properties().Index : properties.GetIPv6Properties().Index;
        var addresses = IPAddress.TryParse(endpoint.Host, out var literal)
            ? [literal] : await Resolve(endpoint.Host, (uint)index, cancellation).ConfigureAwait(false);
        Exception? failure = null;
        foreach (var address in addresses)
        {
            foreach (var source in local.Where(value => value.AddressFamily == address.AddressFamily &&
                (address.AddressFamily != AddressFamily.InterNetworkV6 || value.IsIPv6LinkLocal == address.IsIPv6LinkLocal)))
            {
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    // A source address alone can still route through another adapter.
                    var family = address.AddressFamily == AddressFamily.InterNetwork;
                    var routeIndex = family ? properties.GetIPv4Properties().Index : properties.GetIPv6Properties().Index;
                    // Winsock's IP_UNICAST_IF and IPV6_UNICAST_IF share option 31,
                    // which SocketOptionName does not expose.
                    socket.SetSocketOption(family ? SocketOptionLevel.IP : SocketOptionLevel.IPv6,
                        (SocketOptionName)31, family ? IPAddress.HostToNetworkOrder(routeIndex) : routeIndex);
                    socket.Bind(new IPEndPoint(source, 0));
                    await socket.ConnectAsync(new IPEndPoint(address, endpoint.Port), cancellation).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch (Exception error) when (error is SocketException or OperationCanceledException)
                {
                    socket.Dispose();
                    cancellation.ThrowIfCancellationRequested();
                    failure = error;
                }
            }
        }
        throw new IOException("The selected network adapter cannot reach the provider route.", failure);
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

internal sealed class HttpRouteException(string message, Exception? inner = null) : IOException(message, inner);

internal sealed record ProviderReply(HttpStatusCode Status, byte[] Bytes, IReadOnlyDictionary<string, string> Headers)
{
    internal string? Header(string name) => Headers.GetValueOrDefault(name);

    internal DateTimeOffset? RetryAt
    {
        get
        {
            var now = DateTimeOffset.UtcNow;
            var value = Header("Retry-After");
            if (long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
                return seconds > 0 && seconds <= (DateTimeOffset.MaxValue - now).Ticks / TimeSpan.TicksPerSecond
                    ? now.AddSeconds(seconds) : null;
            return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
                && date > now ? date : null;
        }
    }
}
