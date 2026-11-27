using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Contracts.Operations;
using Vigiloom.Modules.AlertingOperations.Application.Line;
using Vigiloom.Web.Operations;

namespace Vigiloom.Web.Tests;

[TestClass]
public sealed class RemoteLineTests(TestContext context)
{
    [TestMethod]
    public async Task PreservesAdministratorSettings()
    {
        var actor = Guid.CreateVersion7();
        using var client = new HttpClient(new Handler(actor)) { BaseAddress = new Uri("http://operations/") };
        var remote = new RemoteLine(client);
        var view = await remote.GetAsync(context.CancellationToken);
        Assert.IsTrue(view.Enabled);
        Assert.AreEqual(10, view.AggregationSeconds);
        Assert.IsTrue(await remote.SaveAsync(new(true, "token", "secret", 15), actor, context.CancellationToken));
    }

    [TestMethod]
    public async Task RemoteSettingsDoNotRequireLocalOperationsServices()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Services:Operations:Url"] = "http://operations" }).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddHttpClient<RemoteLine>(client => client.BaseAddress = new Uri("http://operations/"))
            .ConfigurePrimaryHttpMessageHandler(() => new Handler(Guid.Empty));
        services.AddScoped<LineManagement>();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var view = await scope.ServiceProvider.GetRequiredService<LineManagement>().GetAsync(context.CancellationToken);
        Assert.IsTrue(view.Enabled);
    }

    private sealed class Handler(Guid actor) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method == HttpMethod.Get)
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new LineChannelInfo(true, true, true, [], 10)) };
            if (request.Method == HttpMethod.Put)
            {
                var input = await request.Content!.ReadFromJsonAsync<LineChannelChange>(token);
                Assert.IsNotNull(input);
                Assert.AreEqual(actor, input.ActorId);
                Assert.AreEqual(15, input.AggregationSeconds);
                return new(HttpStatusCode.NoContent);
            }
            throw new InvalidOperationException("Unexpected LINE request.");
        }
    }
}
