using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor.Services;
using Vigiloom.Web.Client.Components;

namespace Vigiloom.Web.Tests;

[TestClass]
public sealed class SharedComponentsTests
{
    [TestMethod]
    public void TimeSeries_UsesActualUnevenTimestampsAndDoesNotConnectMissingSamples()
    {
        var start = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        TimeSeriesPoint[] points = [
            new(start, 1), new(start.AddMinutes(1), null), new(start.AddHours(12), 3)
        ];
        var build = typeof(TimeSeriesChart).GetMethod("CreateOptions", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.IsNotNull(build);
        var options = (Vizor.ECharts.ChartOptions)build.Invoke(null, [points])!;
        Assert.AreEqual(Vizor.ECharts.AxisType.Time, options.XAxis!.Type);
        var series = (Vizor.ECharts.LineSeries)options.Series![0];
        Assert.IsFalse(series.ConnectNulls ?? true);
        var values = (object?[][])series.Data!;
        Assert.AreEqual(60_000L, (long)values[1][0]! - (long)values[0][0]!);
        Assert.AreEqual(43_140_000L, (long)values[2][0]! - (long)values[1][0]!);
        Assert.IsNull(values[1][1]);
    }

    [TestMethod]
    public async Task Components_PrerenderWithoutJavaScript_AndPreserveMissingSamples()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMudServices();
        services.AddSingleton<IJSRuntime, UnavailableJavaScript>();
        services.AddSingleton<NavigationManager, TestNavigationManager>();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var badge = await renderer.RenderComponentAsync<StatusBadge>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(StatusBadge.Text)] = "資料過期", [nameof(StatusBadge.Tone)] = StatusTone.Warning
            }));
            Assert.Contains("資料過期", System.Net.WebUtility.HtmlDecode(badge.ToHtmlString()));
            var timestamp = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
            var chart = await renderer.RenderComponentAsync<TimeSeriesChart>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(TimeSeriesChart.Title)] = "延遲", [nameof(TimeSeriesChart.Unit)] = "ms",
                [nameof(TimeSeriesChart.Points)] = new[] { new TimeSeriesPoint(timestamp, 12), new TimeSeriesPoint(timestamp.AddMinutes(1), null) }
            }));
            var html = System.Net.WebUtility.HtmlDecode(chart.ToHtmlString());
            Assert.Contains("以表格查看資料", html);
            Assert.Contains("無資料", html);
            Assert.Contains("2026-11-01", html);
        });
    }

    private sealed class UnavailableJavaScript : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => throw new InvalidOperationException("JavaScript is unavailable while prerendering.");
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => throw new InvalidOperationException("JavaScript is unavailable while prerendering.");
    }
    private sealed class TestNavigationManager : NavigationManager
    {
        public TestNavigationManager() => Initialize("https://localhost/", "https://localhost/");
        protected override void NavigateToCore(string uri, bool forceLoad) => throw new NotSupportedException();
    }
}
