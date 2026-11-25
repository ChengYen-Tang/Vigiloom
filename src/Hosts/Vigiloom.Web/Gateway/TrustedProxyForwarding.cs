using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace Vigiloom.Web.Gateway;

internal static class TrustedProxyForwarding
{
    public static ForwardedHeadersOptions? Create(IConfiguration configuration)
    {
        var addresses = configuration.GetSection("Vigiloom:Gateway:TrustedProxies").Get<string[]>() ?? [];
        if (addresses.Length == 0) return null;
        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost,
            ForwardLimit = 1
        };
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();
        foreach (var address in addresses) options.KnownProxies.Add(IPAddress.Parse(address));
        return options;
    }
}
