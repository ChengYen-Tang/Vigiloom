namespace Vigiloom.UpdateSecurity;

internal static class UpdateHttp
{
    internal static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(10)
    }) { Timeout = Timeout.InfiniteTimeSpan };
}
