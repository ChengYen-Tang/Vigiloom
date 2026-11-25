var builder = WebApplication.CreateBuilder(args);
builder.Services.AddServiceDiscovery();
builder.Services.ConfigureHttpClientDefaults(client => client.AddServiceDiscovery());
builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"))
    .AddServiceDiscoveryDestinationResolver();

var app = builder.Build();
app.UseWebSockets();
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/internal"))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }
    await next(context);
});
app.MapReverseProxy();
app.Run();

public partial class Program;
