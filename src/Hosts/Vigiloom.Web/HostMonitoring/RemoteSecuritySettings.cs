using System.Data.Common;
using System.Net.Http.Json;
using Vigiloom.Contracts.Agent.Integrity;
using Vigiloom.Contracts.Agent.Inventory;

namespace Vigiloom.Web.HostMonitoring;

// The observation belongs to Host; desired policy belongs to Security and is read separately.
internal sealed class RemoteSecuritySettings(IHttpClientFactory clients) : IInventorySettingsReader, IIntegritySettingsReader
{
    async Task<InventorySettingsSnapshot> IInventorySettingsReader.ReadAsync(DbConnection connection, DbTransaction transaction, Guid deviceId, CancellationToken token) => await InventoryAsync(deviceId, token);

    public async Task<InventorySettingsSnapshot> InventoryAsync(Guid deviceId, CancellationToken token) =>
        await clients.CreateClient("security").GetFromJsonAsync<InventorySettingsSnapshot>($"internal/v1/agent-settings/{deviceId:D}/inventory", token)
            ?? throw new InvalidOperationException("Security returned empty inventory settings.");

    async Task<IntegritySettingsSnapshot> IIntegritySettingsReader.ReadAsync(DbConnection connection, DbTransaction transaction, Guid deviceId, CancellationToken token) => await IntegrityAsync(deviceId, token);

    public async Task<IntegritySettingsSnapshot> IntegrityAsync(Guid deviceId, CancellationToken token) =>
        await clients.CreateClient("security").GetFromJsonAsync<IntegritySettingsSnapshot>($"internal/v1/agent-settings/{deviceId:D}/integrity", token)
            ?? throw new InvalidOperationException("Security returned empty integrity settings.");
}
