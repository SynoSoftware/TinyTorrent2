using System.Net;
using System.Net.Http;
using System.Text;
using Syno.TinyTorrent.Services;

namespace Syno.TinyTorrent.Library.Public;

/// <summary>Reads pages without a browser, on the saved network route.</summary>
internal sealed class Http(ProviderHttp http) : IPageReader
{
    public bool IsAvailable => true;

    public async Task<string> Read(Uri address, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        ProviderReply reply;
        try
        {
            reply = await http.Send(request, Provider.PageLimit, cancellation)
                .ConfigureAwait(false);
        }
        catch (HttpRouteException error)
        {
            throw new RouteException("websites_http", error);
        }
        catch (InvalidDataException)
        {
            throw Provider.Invalid();
        }
        catch (Exception error) when (error is HttpRequestException or IOException)
        {
            throw Provider.Unreachable(error.GetBaseException().Message, error);
        }
        if (reply.Status != HttpStatusCode.OK)
            throw Provider.Failure((int)reply.Status);
        return Encoding.UTF8.GetString(reply.Bytes);
    }
}
