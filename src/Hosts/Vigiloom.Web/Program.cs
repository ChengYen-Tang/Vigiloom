using Vigiloom.Web.Components;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddInteractiveWebAssemblyComponents();
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();

var app = builder.Build();

// API errors must not be rewritten as a Blazor HTML page.
app.UseWhen(context => context.Request.Path.StartsWithSegments("/api"), api =>
{
    api.UseExceptionHandler();
    api.UseStatusCodePages();
});

if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
    app.MapOpenApi();
}
else
{
    app.UseWhen(context => !context.Request.Path.StartsWithSegments("/api"), web =>
        web.UseExceptionHandler("/Error", createScopeForErrors: true));
    app.UseHsts();
}

app.UseWhen(context => !context.Request.Path.StartsWithSegments("/api"), web =>
    web.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true));
app.UseHttpsRedirection();
app.UseAntiforgery();

app.MapHealthChecks("/health/live").AllowAnonymous();
app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(Vigiloom.Web.Client._Imports).Assembly);

app.Run();

// Exposes the entry point to WebApplicationFactory without executing an external server.
public partial class Program;
