using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;

namespace NotificationHub.Api.Commons;

public interface IUrlPolicy { Task Validate(string url, CancellationToken ct = default); }
public sealed class UrlPolicy(IOptions<HubOptions> options) : IUrlPolicy
{
    public async Task Validate(string url, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Fragment) || (uri.Scheme != "https" && !(options.Value.AllowPrivateNetworks && uri.Scheme == "http")))
            throw new HubException("unsafe_url");
        ValidateHost(uri.IdnHost);
        var addresses = await Dns.GetHostAddressesAsync(uri.IdnHost, ct);
        if (addresses.Length == 0 || (!options.Value.AllowPrivateNetworks && addresses.Any(IsPrivate))) throw new HubException("unsafe_url");
    }
    public void ValidateHost(string host)
    {
        if (!options.Value.AllowedHosts.Contains(host, StringComparer.OrdinalIgnoreCase)) throw new HubException("unsafe_url");
    }
    public static bool IsPrivate(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return true;
        var b = ip.GetAddressBytes();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
            return b[0] is 0 or 10 or 127 or >= 224 || (b[0] == 169 && b[1] == 254)
                || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168)
                || (b[0] == 100 && b[1] is >= 64 and <= 127) || (b[0] == 198 && b[1] is 18 or 19);
        return ip.Equals(IPAddress.IPv6Any) || ip.IsIPv6LinkLocal || ip.IsIPv6Multicast || ip.IsIPv6SiteLocal
            || (b[0] & 0xfe) == 0xfc || !(b[0] is >= 0x20 and <= 0x3f);
    }
    public SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false, UseProxy = false, UseCookies = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2), ConnectTimeout = TimeSpan.FromSeconds(5),
        ConnectCallback = async (context, ct) =>
        {
            ValidateHost(context.DnsEndPoint.Host);
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
            if (addresses.Length == 0 || (!options.Value.AllowPrivateNetworks && addresses.Any(IsPrivate))) throw new HubException("unsafe_url");
            foreach (var address in addresses)
            {
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try { await socket.ConnectAsync(address, context.DnsEndPoint.Port, ct); return new NetworkStream(socket, ownsSocket: true); }
                catch { socket.Dispose(); }
                ct.ThrowIfCancellationRequested();
            }
            throw new HttpRequestException("Connection failed.");
        }
    };
}
