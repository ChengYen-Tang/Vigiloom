using Microsoft.Data.SqlClient;

namespace Vigiloom.Modules.ServiceObservability.Persistence;

/// <summary>Preserves whether saved evidence matches its owner after SQL rowversions are regenerated.</summary>
internal static class HealthVersionMigration
{
    internal static async Task CopyAsync(SqlConnection source, SqlConnection target, SqlTransaction transaction, CancellationToken token)
    {
        var rows = new List<(Guid Target, int Source, int Service, int Instance, int Check, int Network)>();
        await using (var query = new SqlCommand("""
            SELECT s.TargetId,
              CASE WHEN s.SourceVersion IS NULL THEN 0 WHEN s.SourceVersion = src.Version THEN 1 ELSE 2 END,
              CASE WHEN s.ServiceVersion IS NULL THEN 0 WHEN s.ServiceVersion = svc.Version THEN 1 ELSE 2 END,
              CASE WHEN s.InstanceVersion IS NULL THEN 0 WHEN s.InstanceVersion = ins.Version THEN 1 ELSE 2 END,
              CASE WHEN s.TargetVersion IS NULL THEN 0 WHEN s.TargetVersion = t.Version THEN 1 ELSE 2 END,
              CASE WHEN s.NetworkVersion IS NULL THEN 0 WHEN s.NetworkVersion = p.Version THEN 1 ELSE 2 END,
              s.LastEvaluatedAt, t.InstanceId
            FROM service.HealthSchedules s
            JOIN service.HealthTargets t ON t.Id = s.TargetId
            JOIN service.Services svc ON svc.Id = t.ServiceId
            JOIN service.Sources src ON src.Id = svc.SourceId
            LEFT JOIN service.Instances ins ON ins.Id = t.InstanceId
            LEFT JOIN service.HealthNetworkPolicies p ON p.Name = 'central'
            """, source))
        await using (var reader = await query.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token))
            {
                var row = (Target: reader.GetGuid(0), Source: reader.GetInt32(1), Service: reader.GetInt32(2),
                    Instance: reader.GetInt32(3), Check: reader.GetInt32(4), Network: reader.GetInt32(5));
                if (!reader.IsDBNull(6) && (row.Source != 1 || row.Service != 1 || row.Check != 1 ||
                    (reader.IsDBNull(7) ? row.Instance != 0 : row.Instance != 1)))
                    throw new InvalidOperationException("A health schedule has stale authorization evidence; resolve it before migration.");
                rows.Add(row);
            }

        const string update = """
            UPDATE s SET
              SourceVersion = CASE @source WHEN 0 THEN NULL WHEN 1 THEN src.Version ELSE 0x0000000000000000 END,
              ServiceVersion = CASE @service WHEN 0 THEN NULL WHEN 1 THEN svc.Version ELSE 0x0000000000000000 END,
              InstanceVersion = CASE @instance WHEN 0 THEN NULL WHEN 1 THEN ins.Version ELSE 0x0000000000000000 END,
              TargetVersion = CASE @check WHEN 0 THEN NULL WHEN 1 THEN t.Version ELSE 0x0000000000000000 END,
              NetworkVersion = CASE @network WHEN 0 THEN NULL WHEN 1 THEN p.Version ELSE 0x0000000000000000 END,
              LastTransitionGeneration = s.Generation, LeaseOwner = NULL, LeaseUntil = NULL
            FROM service.HealthSchedules s
            JOIN service.HealthTargets t ON t.Id = s.TargetId
            JOIN service.Services svc ON svc.Id = t.ServiceId
            JOIN service.Sources src ON src.Id = svc.SourceId
            LEFT JOIN service.Instances ins ON ins.Id = t.InstanceId
            LEFT JOIN service.HealthNetworkPolicies p ON p.Name = 'central'
            WHERE s.TargetId = @id
            """;
        foreach (var row in rows)
        {
            await using var command = new SqlCommand(update, target, transaction);
            command.Parameters.AddWithValue("@id", row.Target);
            command.Parameters.AddWithValue("@source", row.Source);
            command.Parameters.AddWithValue("@service", row.Service);
            command.Parameters.AddWithValue("@instance", row.Instance);
            command.Parameters.AddWithValue("@check", row.Check);
            command.Parameters.AddWithValue("@network", row.Network);
            if (await command.ExecuteNonQueryAsync(token) != 1) throw new InvalidOperationException("Health schedule migration failed.");
        }
    }
}
