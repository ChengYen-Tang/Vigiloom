using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using System.Text.Json;
using System.Globalization;
using Vigiloom.Contracts.Publishing;

namespace Vigiloom.Web.Client.Components.PublicStatus;

public partial class PublicStatusPanel : IAsyncDisposable
{
    [Inject] private IPublicStatusClient Client { get; set; } = default!;
    [Inject] private ILogger<PublicStatusPanel> Logger { get; set; } = default!;
    [Inject] private IJSRuntime Javascript { get; set; } = default!;
    [Inject] private TimeProvider Clock { get; set; } = default!;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim historyStopGate = new(1, 1);
    private CancellationTokenSource? historyCancellation;
    private Task? historyWork, refreshLoop, initialization;
    private PublicStatusSubscription? subscription;
    private int selectionVersion, subscriptionGeneration = -1;
    private int? pendingStop;
    private PublicPage<PublicGroup>? groups;
    private PublicPage<PublicDevice>? devices;
    private PublicPage<PublicService>? services;
    private readonly Dictionary<Guid, PublicServiceHistory?> histories = [];
    private readonly HashSet<Guid> failedHistories = [];
    private readonly Dictionary<Guid, int> historyRevisions = [];
    private readonly Dictionary<Guid, DateTimeOffset> historyUpdatedAt = [];
    private readonly Dictionary<Guid, (DateTimeOffset From, DateTimeOffset To)> historyRanges = [];
    private Guid? selectedGroup;
    private PublicDevice? selectedDevice;
    private PublicService? selectedService;
    private PublicDeviceHistory? deviceHistory;
    private PublicServiceHealthPeriods? servicePeriods;
    private IReadOnlyList<TimeSeriesPoint> devicePoints = [];
    private PublicHealthDay? selectedDay;
    private string searchInput = "", search = "";
    private string rangeChoice = "7";
    private string? rangeError;
    private string customStart = "", customEnd = "";
    private DateTimeOffset? customFrom, customTo;
    private int groupPage = 1, devicePage = 1, servicePage = 1, historyDays = 7, detailVersion, historyRevision, listVersion;
    private bool loading, refreshPending, initialLoading = true, loadError, detailLoading, detailError, detailHistoryUnavailable, subscriptionWarning, changingRange, disposed;

    protected override void OnInitialized()
    {
        var now = Clock.GetUtcNow();
        customStart = now.AddDays(-7).ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture);
        customEnd = now.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture);
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender && RendererInfo.IsInteractive)
        {
            subscription = new PublicStatusSubscription(Javascript, this);
            initialization = InitializeInteractiveAsync();
            await initialization;
        }
        if (disposed) return;
        if (pendingStop is { } previous && subscription is not null)
        {
            pendingStop = null;
            try { await subscription.StopAsync(previous); }
            catch (JSDisconnectedException) { }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (JSException ex) { Logger.LogWarning(ex, "Unable to stop public status subscription."); }
        }
    }

    private async Task InitializeInteractiveAsync()
    {
        await RefreshAsync();
        if (disposed) return;
        initialLoading = false;
        StateHasChanged();
        refreshLoop = RefreshPeriodicallyAsync(lifetime.Token);
    }

    private async Task RefreshAsync()
    {
        if (loading || changingRange || lifetime.IsCancellationRequested) return;
        loading = true;
        var currentListVersion = listVersion;
        historyRevision++;
        var deviceId = selectedDevice?.Id;
        var serviceId = selectedService?.Id;
        var day = selectedDay;
        var version = ++detailVersion;
        if (deviceId is not null || serviceId is not null)
        {
            deviceHistory = null; devicePoints = []; selectedDay = null; servicePeriods = null;
            detailLoading = true; detailError = detailHistoryUnavailable = false;
        }
        var previousServices = services?.Items.Select(x => x.Id).ToArray();
        if (previousServices is null) { await StopHistoriesAsync(); ClearHistories(); }
        try
        {
            if (selectedGroup is { } group && await Client.GroupAsync(group, lifetime.Token) is null)
            { selectedGroup = null; devicePage = servicePage = 1; ClearDetail(); }
            var groupTask = Client.GroupsAsync(null, groupPage, lifetime.Token);
            var deviceTask = Client.DevicesAsync(search, devicePage, selectedGroup, lifetime.Token);
            var serviceTask = Client.ServicesAsync(search, servicePage, selectedGroup, lifetime.Token);
            await Task.WhenAll(groupTask, deviceTask, serviceTask);
            if (currentListVersion != listVersion) { refreshPending = true; return; }
            groups = await groupTask; devices = await deviceTask; services = await serviceTask;
            if (previousServices is not null && !previousServices.SequenceEqual(services.Items.Select(x => x.Id)))
            { await StopHistoriesAsync(); ClearHistories(); }
            if (version == detailVersion)
            {
                if (deviceId is { } id)
                {
                    selectedDevice = devices.Items.FirstOrDefault(x => x.Id == id);
                    if (selectedDevice is null) ClearDetail();
                }
                else if (serviceId is { } selectedServiceId)
                {
                    selectedService = services.Items.FirstOrDefault(x => x.Id == selectedServiceId);
                    if (selectedService is null) ClearDetail();
                }
            }
            loadError = false;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Unable to read public status.");
            groups = null; devices = null; services = null; ClearDetail(); loadError = true;
        }
        finally
        {
            loading = false;
            if (refreshPending && !lifetime.IsCancellationRequested)
            {
                refreshPending = false;
                await RefreshAsync();
            }
        }
        if (!loadError && RendererInfo.IsInteractive)
        {
            if (historyWork is { IsCompleted: true }) await StopHistoriesAsync();
            StartHistories();
            if (version == detailVersion && deviceId is { } currentDevice) await RevalidateDeviceHistoryAsync(currentDevice, version);
            else if (version == detailVersion && serviceId is { } currentService && day is not null) await RevalidateServiceDayAsync(currentService, day, version);
            else if (version == detailVersion) detailLoading = false;
        }
    }

    private void StartHistories()
    {
        if (services is null || historyWork is { IsCompleted: false } || lifetime.IsCancellationRequested) return;
        historyCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        historyWork = LoadHistoriesAsync(services.Items, historyCancellation.Token);
    }

    private async Task LoadHistoriesAsync(IReadOnlyList<PublicService> items, CancellationToken token)
    {
        foreach (var item in items)
        {
            try
            {
                var (start, end) = HistoryRange();
                var history = await Client.ServiceHistoryAsync(item.Id, start, end, token);
                await InvokeAsync(() => { if (token.IsCancellationRequested || disposed) return; histories[item.Id] = history; failedHistories.Remove(item.Id); historyRevisions[item.Id] = historyRevision; historyUpdatedAt[item.Id] = Clock.GetUtcNow(); historyRanges[item.Id] = (start, end); StateHasChanged(); });
            }
            catch (Exception) when (token.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Unable to read public service history.");
                await InvokeAsync(() => { if (token.IsCancellationRequested || disposed) return; histories.Remove(item.Id); failedHistories.Add(item.Id); historyRevisions[item.Id] = historyRevision; historyUpdatedAt.Remove(item.Id); StateHasChanged(); });
            }
        }
    }

    private void ClearHistories() { histories.Clear(); failedHistories.Clear(); historyRevisions.Clear(); historyUpdatedAt.Clear(); historyRanges.Clear(); }

    private async Task StopHistoriesAsync()
    {
        await historyStopGate.WaitAsync();
        try
        {
            var cancellation = historyCancellation;
            var work = historyWork;
            if (cancellation is null) return;
            historyCancellation = null; historyWork = null;
            try { await cancellation.CancelAsync(); if (work is not null) await work; }
            finally { cancellation.Dispose(); }
        }
        finally { historyStopGate.Release(); }
    }

    private async Task RefreshPeriodicallyAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15), Clock);
        try { while (await timer.WaitForNextTickAsync(token)) await InvokeAsync(async () => { await RefreshAsync(); if (!disposed) StateHasChanged(); }); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private async Task SelectGroupAsync(Guid? id) { if (loading || changingRange) return; selectedGroup = id; devicePage = servicePage = 1; devices = null; services = null; ClearDetail(); await RefreshAsync(); }
    private async Task SearchAsync() { if (loading || changingRange) return; search = searchInput.Trim(); devicePage = servicePage = 1; devices = null; services = null; ClearDetail(); await RefreshAsync(); }
    private async Task ChangeGroupPageAsync(int delta) { if (loading || changingRange) return; groupPage += delta; await RefreshAsync(); }
    private async Task ChangeDevicePageAsync(int delta) { if (loading || changingRange) return; devicePage += delta; devices = null; services = null; ClearDetail(); await RefreshAsync(); }
    private async Task ChangeServicePageAsync(int delta) { if (loading || changingRange) return; servicePage += delta; devices = null; services = null; ClearDetail(); await RefreshAsync(); }
    private async Task ChangeHistoryRangeAsync(ChangeEventArgs args)
    {
        if (loading || changingRange) return;
        var choice = args.Value?.ToString();
        if (choice == "custom") { rangeChoice = choice; rangeError = null; return; }
        if (!int.TryParse(choice, out var value) || value is not (7 or 30 or 90)) return;
        changingRange = true;
        try
        {
            await StopHistoriesAsync();
            rangeChoice = choice; rangeError = null; customFrom = customTo = null;
            historyDays = value; ClearDetail();
        }
        finally { changingRange = false; }
        await RefreshAsync();
    }

    private async Task ApplyCustomRangeAsync()
    {
        if (loading || changingRange) return;
        if (!DateTime.TryParse(customStart, CultureInfo.InvariantCulture, DateTimeStyles.None, out var start) ||
            !DateTime.TryParse(customEnd, CultureInfo.InvariantCulture, DateTimeStyles.None, out var end))
        { rangeError = "請輸入有效的開始與結束時間。"; return; }
        var from = new DateTimeOffset(DateTime.SpecifyKind(start, DateTimeKind.Utc));
        var to = new DateTimeOffset(DateTime.SpecifyKind(end, DateTimeKind.Utc));
        if (from >= to) { rangeError = "開始時間必須早於結束時間。"; return; }
        if (to - from > TimeSpan.FromDays(90)) { rangeError = "時間範圍最多 90 天。"; return; }
        changingRange = true;
        try
        {
            await StopHistoriesAsync();
            customFrom = from; customTo = to; rangeError = null;
            ClearDetail();
        }
        finally { changingRange = false; }
        await RefreshAsync();
    }

    private (DateTimeOffset From, DateTimeOffset To) HistoryRange()
    {
        if (customFrom is { } from && customTo is { } to) return (from, to);
        var end = Clock.GetUtcNow();
        return (end.AddDays(-historyDays), end);
    }

    private (DateTimeOffset From, DateTimeOffset To) DeviceHistoryRange()
    {
        var (from, to) = HistoryRange();
        return (to - from > TimeSpan.FromDays(7) ? to.AddDays(-7) : from, to);
    }

    private async Task SelectDeviceAsync(Guid id)
    {
        if (selectedDevice?.Id == id) { ClearDetail(); return; }
        ClearDetail(); var version = detailVersion; detailLoading = true;
        try
        {
            var device = await Client.DeviceAsync(id, lifetime.Token);
            if (device is null || version != detailVersion) return;
            selectedDevice = device;
            await StartSubscriptionAsync("devices", id);
            if (version != detailVersion) return;
            var (from, to) = DeviceHistoryRange();
            var history = await Client.DeviceHistoryAsync(id, from, to, lifetime.Token);
            if (version != detailVersion) return;
            var currentDevice = await Client.DeviceAsync(id, lifetime.Token);
            if (version != detailVersion) return;
            if (currentDevice is null) { ClearDetail(); return; }
            selectedDevice = currentDevice;
            deviceHistory = history;
            devicePoints = history?.Points.Select(x => new TimeSeriesPoint(x.From, x.AverageLatencyMilliseconds)).ToArray() ?? [];
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { Logger.LogWarning(ex, "Unable to read public device history."); if (version == detailVersion) detailError = true; }
        finally { if (version == detailVersion) detailLoading = false; }
    }

    private async Task SelectServiceAsync(Guid id)
    {
        ClearDetail(); var version = detailVersion;
        try
        {
            var result = await Client.ServiceAsync(id, lifetime.Token);
            if (version != detailVersion || result is null) return;
            selectedService = result;
            await StartSubscriptionAsync("services", id);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { Logger.LogWarning(ex, "Unable to read public service."); if (version == detailVersion) loadError = true; }
    }

    private async Task SelectServiceDayAsync((Guid ServiceId, PublicHealthDay Day) selection)
    {
        ClearDetail(); var version = detailVersion; detailLoading = true;
        try
        {
            var current = await Client.ServiceAsync(selection.ServiceId, lifetime.Token);
            if (version != detailVersion || current is null) return;
            selectedService = current;
            await StartSubscriptionAsync("services", selection.ServiceId);
            if (version != detailVersion) return;
            await RevalidateServiceDayAsync(selection.ServiceId, selection.Day, version);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { Logger.LogWarning(ex, "Unable to read public service periods."); if (version == detailVersion) detailError = true; }
        finally { if (version == detailVersion) detailLoading = false; }
    }

    private async Task RevalidateDeviceHistoryAsync(Guid id, int version)
    {
        try
        {
            var (from, to) = DeviceHistoryRange();
            var history = await Client.DeviceHistoryAsync(id, from, to, lifetime.Token);
            var current = await Client.DeviceAsync(id, lifetime.Token);
            if (version != detailVersion) return;
            if (current is null) { ClearDetail(); return; }
            selectedDevice = current; deviceHistory = history;
            devicePoints = history?.Points.Select(x => new TimeSeriesPoint(x.From, x.AverageLatencyMilliseconds)).ToArray() ?? [];
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { Logger.LogWarning(ex, "Unable to refresh public device history."); if (version == detailVersion) detailError = true; }
        finally { if (version == detailVersion) detailLoading = false; }
    }

    private async Task RevalidateServiceDayAsync(Guid id, PublicHealthDay day, int version)
    {
        try
        {
            var history = await Client.ServiceHistoryAsync(id, day.From, day.To, lifetime.Token);
            if (version != detailVersion) return;
            var currentDay = history?.Days.FirstOrDefault();
            if (currentDay is null) { detailHistoryUnavailable = true; return; }
            var periods = await Client.ServicePeriodsAsync(id, currentDay.From, currentDay.To, lifetime.Token);
            if (periods is null)
            {
                var confirmed = await Client.ServiceHistoryAsync(id, currentDay.From, currentDay.To, lifetime.Token);
                if (version != detailVersion) return;
                currentDay = confirmed?.Days.FirstOrDefault();
                if (currentDay is null) { detailHistoryUnavailable = true; return; }
            }
            var current = await Client.ServiceAsync(id, lifetime.Token);
            if (version != detailVersion) return;
            if (current is null) { ClearDetail(); return; }
            selectedService = current; selectedDay = currentDay; servicePeriods = periods;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { Logger.LogWarning(ex, "Unable to refresh public service history."); if (version == detailVersion) detailError = true; }
        finally { if (version == detailVersion) detailLoading = false; }
    }

    private void ClearDetail()
    {
        detailVersion++; selectionVersion++;
        if (subscriptionGeneration >= 0) pendingStop = subscriptionGeneration;
        subscriptionGeneration = -1;
        selectedDevice = null; selectedService = null; selectedDay = null;
        deviceHistory = null; devicePoints = []; servicePeriods = null; detailLoading = detailError = detailHistoryUnavailable = false;
    }

    private async Task StartSubscriptionAsync(string kind, Guid id)
    {
        if (subscription is null) return;
        var generation = selectionVersion;
        subscriptionGeneration = generation;
        try { await subscription.StartAsync(kind, id, generation, lifetime.Token); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (JSDisconnectedException) { if (generation == selectionVersion) subscriptionWarning = true; }
        catch (JSException ex) { Logger.LogWarning(ex, "Unable to start public status subscription."); if (generation == selectionVersion) subscriptionWarning = true; }
    }

    internal bool IsSubscriptionCurrent(int generation) => generation == selectionVersion && !lifetime.IsCancellationRequested;

    [JSInvokable]
    public Task OnPublicSubscription(int generation, string eventType, string? payload) => InvokeAsync(async () =>
    {
        if (generation != selectionVersion || lifetime.IsCancellationRequested) return;
        if (eventType == "unavailable")
        {
            listVersion++;
            if (loading) refreshPending = true;
            var deviceId = selectedDevice?.Id; var serviceId = selectedService?.Id;
            if (devices is not null && deviceId is { } removedDevice) devices = devices with { Items = devices.Items.Where(x => x.Id != removedDevice).ToArray() };
            if (services is not null && serviceId is { } removedService) services = services with { Items = services.Items.Where(x => x.Id != removedService).ToArray() };
            groups = null; ClearDetail();
            StateHasChanged();
            await StopHistoriesAsync();
            ClearHistories();
            await RefreshAsync(); if (!disposed) StateHasChanged(); return;
        }
        if (eventType == "disconnected")
        {
            subscriptionWarning = true;
            if (selectedDevice is not null) selectedDevice = selectedDevice with { State = "Unknown" };
            if (selectedService is not null) selectedService = selectedService with { State = "Unknown" };
            deviceHistory = null; devicePoints = []; selectedDay = null; servicePeriods = null;
            await RefreshAsync(); if (!disposed) StateHasChanged(); return;
        }
        if (eventType != "status" || payload is null) return;
        PublicStatusUpdate? update;
        try { update = JsonSerializer.Deserialize<PublicStatusUpdate>(payload, JsonSerializerOptions.Web); }
        catch (JsonException) { subscriptionWarning = true; StateHasChanged(); return; }
        if (generation != selectionVersion || update is null) return;
        subscriptionWarning = false;
        if (selectedDevice is { } device && update.Device is { } nextDevice && nextDevice.Id == device.Id) selectedDevice = nextDevice;
        if (selectedService is { } service && update.Service is { } nextService && nextService.Id == service.Id) selectedService = nextService;
        StateHasChanged();
    });

    public async ValueTask DisposeAsync()
    {
        disposed = true;
        await lifetime.CancelAsync();
        if (initialization is not null) await initialization;
        await StopHistoriesAsync();
        if (refreshLoop is not null) await refreshLoop;
        if (subscription is not null) await subscription.DisposeAsync();
        historyStopGate.Dispose();
        lifetime.Dispose();
    }
}
