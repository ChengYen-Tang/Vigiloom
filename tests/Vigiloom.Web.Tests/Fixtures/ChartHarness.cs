using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Vigiloom.Web.Client.Components;

namespace Vigiloom.Web.Tests.Fixtures;

// Registered only by the isolated browser-test host, never by the product application.
internal sealed class ChartHarnessStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, nextMiddleware) =>
        {
            if (context.Request.Path == "/__test/chart")
                await new RazorComponentResult<ChartHarness>().ExecuteAsync(context);
            else await nextMiddleware(context);
        });
        next(app);
    };
}

public sealed class ChartHarness : ComponentBase
{
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.AddMarkupContent(0, "<!DOCTYPE html><html lang=\"zh-Hant\"><head><meta charset=\"utf-8\"><base href=\"/\"></head><body>");
        builder.OpenComponent<TimeSeriesChart>(1);
        builder.AddAttribute(2, nameof(TimeSeriesChart.Title), "延遲");
        builder.AddAttribute(3, nameof(TimeSeriesChart.Unit), "ms");
        var start = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        builder.AddAttribute(4, nameof(TimeSeriesChart.Points), new[]
        {
            new TimeSeriesPoint(start, 10), new TimeSeriesPoint(start.AddMinutes(1), null), new TimeSeriesPoint(start.AddHours(12), 20)
        });
        builder.AddComponentRenderMode(new InteractiveAutoRenderMode());
        builder.CloseComponent();
        builder.AddMarkupContent(5, "<script src=\"/_content/Vizor.ECharts/js/vizor-echarts-bundle-min.js\"></script><script src=\"/_framework/blazor.web.js\"></script></body></html>");
    }
}
