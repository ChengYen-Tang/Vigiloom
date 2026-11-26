using System.Security.Claims;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Devices;
using Vigiloom.Contracts.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Application.Detection;

namespace Vigiloom.Web.Components.Pages;

public partial class SigmaRule
{
    [SupplyParameterFromQuery(Name = "path")] private string? Path { get; set; }
    [SupplyParameterFromQuery(Name = "cursor")] private string? Cursor { get; set; }
    [SupplyParameterFromQuery(Name = "search")] private string? Search { get; set; }
    [SupplyParameterFromQuery(Name = "device")] private Guid? DeviceId { get; set; }
    [SupplyParameterFromQuery(Name = "hours")] private int? RangeHours { get; set; }
    [SupplyParameterFromForm(FormName = "sigma-rule-state")] private StateForm StateInput { get; set; } = default!;
    [SupplyParameterFromForm(FormName = "sigma-rule-test")] private TestForm TestInput { get; set; } = default!;
    [CascadingParameter] private HttpContext HttpContext { get; set; } = default!;

    private SigmaDetectionRulePage? page;
    private SigmaDetectionRuleItem? rule;
    private DevicePage? devices;
    private HostLogPage? logs;
    private string? LogCursor;
    private string? error;
    private string? message;
    private IReadOnlyList<SigmaRuleTestObservation>? testResults;
    private int SelectedHours => RangeHours is 168 or 720 ? RangeHours.Value : 24;
    private string BackUrl => Cursor is null ? "/settings/sigma" : $"/settings/sigma?cursor={Uri.EscapeDataString(Cursor)}";

    protected override async Task OnInitializedAsync()
    {
        HttpContext.Response.Headers.CacheControl = "no-store";
        var stateSubmitted = StateInput is not null;
        var testSubmitted = TestInput is not null;
        StateInput ??= new();
        TestInput ??= new();
        TestInput.Selected ??= [];
        TestInput.Samples ??= [];
        LogCursor = string.IsNullOrEmpty(TestInput.LogCursor) ? null : TestInput.LogCursor;
        if (TestInput.DeviceId is { Version: 7 } postedDevice) DeviceId = postedDevice;
        if (TestInput.RangeHours is 24 or 168 or 720) RangeHours = TestInput.RangeHours;
        if (TestInput.DeviceSearch is not null) Search = TestInput.DeviceSearch;
        await LoadAsync();
        if (!testSubmitted && rule?.LatestExceptions is { } exceptions)
            TestInput.Exceptions = string.Join('\n', exceptions.Select(x => $"{x.Field}={x.Value}"));
        if (!stateSubmitted) ResetState();
        if (string.IsNullOrEmpty(TestInput.Path)) TestInput.Path = Path ?? "";
    }

    private async Task LoadAsync()
    {
        page = null; rule = null; devices = null; logs = null; error = null;
        if (string.IsNullOrWhiteSpace(Path) || Path.Length > 512 || !SigmaRuleInventory.ValidCursor(Cursor))
        {
            error = L["找不到此規則，請回到清單重新選擇。"].Value;
            return;
        }
        try
        {
            page = await Inventory.GetAsync(Cursor, HttpContext.RequestAborted);
            if (page.Source.Availability != SigmaAvailability.Ready)
            {
                error = L["規則來源無法使用或已變更，請回到清單重新載入。"].Value;
                return;
            }
            rule = page.Items.SingleOrDefault(x => string.Equals(x.Source.Path, Path, StringComparison.Ordinal));
            if (rule is null) { error = L["找不到此規則，請回到清單重新選擇。"].Value; return; }
            if (!rule.Supported) return;
            devices = await Devices.ListAsync(Search, null, false, false, 1, HttpContext.RequestAborted);
            if (DeviceId is { Version: 7 } device)
            {
                var until = Clock.GetUtcNow();
                logs = await Logs.GetAsync(device, until.AddHours(-SelectedHours), until, LogCursor, null, HttpContext.RequestAborted);
                if (logs is null) error = L["找不到所選設備，請重新選擇。"].Value;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning("Could not load Sigma rule: {ErrorType}", ex.GetType().Name);
            error = L["無法讀取規則或日誌，請重新整理。"].Value;
        }
    }

    private void ResetState()
    {
        if (rule is null || page?.Source.SourceId is not { } source) return;
        StateInput = new() { SourceId = source, Path = rule.Source.Path, TestId = rule.LatestTestId,
            Version = rule.Version, Enable = !rule.Enabled };
    }

    private async Task ChangeStateAsync(EditContext _)
    {
        if (!Actor(out var actor) || page?.Source.SourceId != StateInput.SourceId ||
            !string.Equals(Path, StateInput.Path, StringComparison.Ordinal))
        {
            error = L["規則設定已變更，請重新載入。"].Value;
            return;
        }
        var request = new SigmaRuleStateRequest(StateInput.SourceId, StateInput.Path, StateInput.TestId, StateInput.Enable || StateInput.Apply, StateInput.Version ?? "");
        if (!SigmaRuleService.Valid(request)) { error = L["規則設定無效，請重新載入。"].Value; return; }
        try
        {
            var result = await Rules.SetAsync(request, actor, HttpContext.RequestAborted);
            message = result switch
            {
                SigmaRuleChangeStatus.Changed or SigmaRuleChangeStatus.Unchanged => request.Enabled ? L["規則已啟用。"].Value : L["規則已停用。"].Value,
                SigmaRuleChangeStatus.TestUnavailable => L["此版本需要先完成通過的試跑。"].Value,
                SigmaRuleChangeStatus.SourceUnavailable => L["規則來源無法使用或已變更，請重新載入。"].Value,
                _ => L["規則設定已變更，請重新載入。"].Value
            };
            await LoadAsync();
            ResetState();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning("Could not update Sigma rule: {ErrorType}", ex.GetType().Name);
            error = L["無法更新規則，請稍後再試。"].Value;
        }
    }

    private async Task TestAsync(EditContext _)
    {
        if (!Actor(out var actor) || !string.Equals(Path, TestInput.Path, StringComparison.Ordinal))
        {
            error = L["規則已變更，請重新載入。"].Value;
            return;
        }
        var chosen = TestInput.Selected.Where(x => !x.Remove).ToDictionary(x => (x.AgentId, x.MessageId));
        foreach (var item in TestInput.Samples.Where(x => !string.IsNullOrEmpty(x.Expected))) chosen[(item.AgentId, item.MessageId)] = item;
        if (chosen.Count > 20) { error = L["一次最多選擇 20 筆案例。"].Value; return; }
        TestInput.Selected = chosen.Values.ToList();
        TestInput.Samples = [];
        if (TestInput.Action == "next" && logs?.NextCursor is { } next)
        {
            LogCursor = next;
            TestInput.LogCursor = next;
            await LoadAsync();
            ResetState();
            return;
        }
        if (TestInput.Action == "search")
        {
            Search = TestInput.DeviceSearch;
            await LoadAsync();
            ResetState();
            return;
        }
        if (TestInput.Action == "range")
        {
            LogCursor = null;
            TestInput.LogCursor = null;
            await LoadAsync();
            ResetState();
            return;
        }
        if (TestInput.Action == "switch")
        {
            if (TestInput.NextDevice is not { Version: 7 } nextDevice) { error = L["請選擇設備。"].Value; return; }
            DeviceId = nextDevice;
            TestInput.DeviceId = nextDevice;
            LogCursor = null;
            TestInput.LogCursor = null;
            await LoadAsync();
            ResetState();
            return;
        }
        if (TestInput.Action == "update") return;
        var samples = new List<SigmaRuleTestSample>(chosen.Count);
        foreach (var item in chosen.Values)
        {
            if (!Enum.TryParse<SigmaExpectedOutcome>(item.Expected, out var expected) || !Enum.IsDefined(expected))
            {
                error = L["請選擇有效的預期結果。"].Value;
                return;
            }
            samples.Add(new(item.AgentId, item.MessageId, expected));
        }
        var exceptions = new List<SigmaRuleException>();
        foreach (var line in (TestInput.Exceptions ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var text = line.TrimEnd('\r');
            var separator = text.IndexOf('=');
            if (separator <= 0) { error = L["排除條件請每行輸入「欄位=值」。"].Value; return; }
            exceptions.Add(new(text[..separator].Trim(), text[(separator + 1)..]));
        }
        var request = new SigmaRuleTestRequest(TestInput.Path, samples, exceptions);
        if (!SigmaRuleService.Valid(request)) { error = L["請選擇 2 至 20 筆不同日誌作為案例。"].Value; return; }
        try
        {
            var result = await Rules.TestAsync(request, actor, HttpContext.RequestAborted);
            testResults = result.Result?.Observations;
            message = result.Status switch
            {
                SigmaRuleTestStatus.Saved when result.Result?.Passed == true => L["試跑通過，可以啟用此版本。"].Value,
                SigmaRuleTestStatus.Saved => L["試跑未通過，請檢查預期結果；須包含應命中及不應命中案例。"].Value,
                SigmaRuleTestStatus.EventUnavailable => L["部分日誌已不存在，請重新選擇案例。"].Value,
                SigmaRuleTestStatus.RuleUnsupported => L["此規則的格式或日誌來源尚不支援。"].Value,
                _ => L["規則來源無法使用或已變更，請重新載入。"].Value
            };
            await LoadAsync();
            ResetState();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning("Could not test Sigma rule: {ErrorType}", ex.GetType().Name);
            error = L["無法執行試跑，請稍後再試。"].Value;
        }
    }

    private bool Actor(out Guid actor) => Guid.TryParse(HttpContext.User.FindFirstValue(ClaimTypes.NameIdentifier), out actor) && actor.Version == 7;
    private string? SelectedExpected(HostLogEntry item) => TestInput.Samples.FirstOrDefault(x => x.AgentId == item.AgentId && x.MessageId == item.MessageId)?.Expected;
    private string ExpectedLabel(string? value) => value switch { "Match" => L["應命中"].Value, "NoMatch" => L["不應命中"].Value, "Unknown" => L["應為未評估"].Value, _ => L["未選擇"].Value };

    private sealed class StateForm
    {
        public Guid SourceId { get; set; }
        public string Path { get; set; } = "";
        public Guid? TestId { get; set; }
        public string Version { get; set; } = "";
        public bool Enable { get; set; }
        public bool Apply { get; set; }
    }
    private sealed class TestForm
    {
        public string Path { get; set; } = "";
        public string? LogCursor { get; set; }
        public string? Action { get; set; }
        public Guid? DeviceId { get; set; }
        public Guid? NextDevice { get; set; }
        public int? RangeHours { get; set; }
        public string? DeviceSearch { get; set; }
        public string? Exceptions { get; set; }
        public List<SampleForm> Selected { get; set; } = [];
        public List<SampleForm> Samples { get; set; } = [];
    }
    private sealed class SampleForm
    {
        public Guid AgentId { get; set; }
        public Guid MessageId { get; set; }
        public string? Expected { get; set; }
        public string? Label { get; set; }
        public bool Remove { get; set; }
    }
}
