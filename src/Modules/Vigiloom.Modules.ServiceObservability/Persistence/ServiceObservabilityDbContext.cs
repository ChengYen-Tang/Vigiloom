using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.ServiceObservability.Domain;
using Vigiloom.Modules.ServiceObservability.Domain.Health;
using Vigiloom.Modules.ServiceObservability.Domain.Telemetry;

namespace Vigiloom.Modules.ServiceObservability.Persistence;

public sealed class ServiceObservabilityDbContext(DbContextOptions<ServiceObservabilityDbContext> options) : DbContext(options)
{
    public DbSet<ServiceSource> Sources => Set<ServiceSource>();
    public DbSet<ServiceCandidate> Candidates => Set<ServiceCandidate>();
    public DbSet<MonitoredService> Services => Set<MonitoredService>();
    public DbSet<ServiceInstance> Instances => Set<ServiceInstance>();
    public DbSet<InstancePolicy> InstancePolicies => Set<InstancePolicy>();
    public DbSet<LogRecord> LogRecords => Set<LogRecord>();
    public DbSet<LogContext> LogContexts => Set<LogContext>();
    public DbSet<TraceContext> TraceContexts => Set<TraceContext>();
    public DbSet<TraceSpan> TraceSpans => Set<TraceSpan>();
    public DbSet<MetricContext> MetricContexts => Set<MetricContext>();
    public DbSet<MetricPoint> MetricPoints => Set<MetricPoint>();
    public DbSet<MetricRollupWindow> MetricRollupWindows => Set<MetricRollupWindow>();
    public DbSet<MetricRollupSeries> MetricRollupSeries => Set<MetricRollupSeries>();
    public DbSet<CollectorRegistration> Collectors => Set<CollectorRegistration>();
    public DbSet<CollectorRoute> CollectorRoutes => Set<CollectorRoute>();
    public DbSet<ReceptionRejectionSummary> ReceptionRejections => Set<ReceptionRejectionSummary>();
    public DbSet<HealthTarget> HealthTargets => Set<HealthTarget>();
    public DbSet<HealthNetworkPolicy> HealthNetworkPolicies => Set<HealthNetworkPolicy>();
    public DbSet<HealthNetworkRevision> HealthNetworkRevisions => Set<HealthNetworkRevision>();
    public DbSet<HealthSchedule> HealthSchedules => Set<HealthSchedule>();
    public DbSet<HealthObservation> HealthObservations => Set<HealthObservation>();
    public DbSet<HealthStateInterval> HealthStateIntervals => Set<HealthStateInterval>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("service");

        var healthNetworkPolicy = modelBuilder.Entity<HealthNetworkPolicy>();
        healthNetworkPolicy.ToTable("HealthNetworkPolicies", table =>
        {
            table.HasCheckConstraint("CK_HealthNetworkPolicies_Central", "[Name] = 'central'");
            table.HasCheckConstraint("CK_HealthNetworkPolicies_Generation", "[Generation] > 0");
        });
        healthNetworkPolicy.HasKey(x => x.Id).IsClustered(false);
        healthNetworkPolicy.Property(x => x.Id).ValueGeneratedNever();
        healthNetworkPolicy.Property(x => x.RowId).UseIdentityColumn();
        healthNetworkPolicy.HasIndex(x => x.RowId).IsUnique().IsClustered();
        healthNetworkPolicy.Property(x => x.Name).HasMaxLength(16).IsRequired();
        healthNetworkPolicy.HasIndex(x => x.Name).IsUnique();
        healthNetworkPolicy.Property(x => x.NetworksJson).HasMaxLength(4608).IsRequired();
        healthNetworkPolicy.Property(x => x.Version).IsRowVersion();

        var healthNetworkRevision = modelBuilder.Entity<HealthNetworkRevision>();
        healthNetworkRevision.ToTable("HealthNetworkRevisions");
        healthNetworkRevision.HasKey(x => x.RowId).IsClustered();
        healthNetworkRevision.Property(x => x.RowId).UseIdentityColumn();
        healthNetworkRevision.Property(x => x.NetworksJson).HasMaxLength(4608).IsRequired();
        healthNetworkRevision.HasIndex(x => new { x.PolicyId, x.Generation }).IsUnique();
        healthNetworkRevision.HasOne<HealthNetworkPolicy>().WithMany().HasForeignKey(x => x.PolicyId).OnDelete(DeleteBehavior.Restrict);

        var healthTarget = modelBuilder.Entity<HealthTarget>();
        healthTarget.ToTable("HealthTargets", table => table.HasCheckConstraint("CK_HealthTargets_Settings",
            "[IntervalSeconds] BETWEEN 5 AND 3600 AND [TimeoutSeconds] BETWEEN 1 AND 60 AND [TimeoutSeconds] <= [IntervalSeconds] AND [FailureThreshold] BETWEEN 1 AND 10 AND [RecoveryThreshold] BETWEEN 1 AND 10"));
        healthTarget.HasKey(x => x.Id).IsClustered(false);
        healthTarget.Property(x => x.Id).ValueGeneratedNever();
        healthTarget.Property(x => x.RowId).UseIdentityColumn();
        healthTarget.HasIndex(x => x.RowId).IsUnique().IsClustered();
        healthTarget.Property(x => x.Endpoint).HasMaxLength(2048).IsRequired();
        healthTarget.Property(x => x.Version).IsRowVersion();
        healthTarget.HasIndex(x => new { x.ServiceId, x.InstanceId });
        healthTarget.HasOne<MonitoredService>().WithMany().HasForeignKey(x => x.ServiceId).OnDelete(DeleteBehavior.Restrict);
        healthTarget.HasOne<ServiceInstance>().WithMany().HasForeignKey(x => new { x.InstanceId, x.ServiceId })
            .HasPrincipalKey(x => new { x.Id, x.ServiceId }).OnDelete(DeleteBehavior.Restrict);

        var healthSchedule = modelBuilder.Entity<HealthSchedule>();
        healthSchedule.ToTable("HealthSchedules", table =>
        {
            table.HasCheckConstraint("CK_HealthSchedules_Generation", "[Generation] >= 0");
            table.HasCheckConstraint("CK_HealthSchedules_State", "[State] BETWEEN 0 AND 3 AND [Streak] BETWEEN 0 AND 3 AND [StreakCount] BETWEEN 0 AND 10 AND (([State] = 0 AND [EvidenceAt] IS NULL) OR ([State] > 0 AND [EvidenceAt] IS NOT NULL)) AND (([Streak] = 0 AND [StreakCount] = 0 AND [StreakAt] IS NULL) OR ([Streak] > 0 AND [StreakCount] > 0 AND [StreakAt] IS NOT NULL))");
        });
        healthSchedule.HasKey(x => x.RowId).IsClustered();
        healthSchedule.Property(x => x.RowId).UseIdentityColumn();
        healthSchedule.Property(x => x.Version).IsRowVersion();
        healthSchedule.Property(x => x.SourceVersion).HasColumnType("binary(8)");
        healthSchedule.Property(x => x.ServiceVersion).HasColumnType("binary(8)");
        healthSchedule.Property(x => x.InstanceVersion).HasColumnType("binary(8)");
        healthSchedule.Property(x => x.TargetVersion).HasColumnType("binary(8)");
        healthSchedule.Property(x => x.NetworkVersion).HasColumnType("binary(8)");
        healthSchedule.HasIndex(x => x.TargetId).IsUnique();
        healthSchedule.HasIndex(x => new { x.Enabled, x.DueAt, x.RowId });
        healthSchedule.HasOne<HealthTarget>().WithMany().HasForeignKey(x => x.TargetId).OnDelete(DeleteBehavior.Restrict);

        var healthInterval = modelBuilder.Entity<HealthStateInterval>();
        healthInterval.ToTable("HealthStateIntervals", table => table.HasCheckConstraint("CK_HealthStateIntervals_Values",
            "[State] BETWEEN 0 AND 4 AND [Reason] BETWEEN 0 AND 9 AND ([EndedAt] IS NULL OR [EndedAt] >= [StartedAt])"));
        healthInterval.HasKey(x => x.RowId).IsClustered();
        healthInterval.Property(x => x.RowId).UseIdentityColumn();
        healthInterval.HasIndex(x => x.TargetId).IsUnique().HasFilter("[EndedAt] IS NULL");
        healthInterval.HasIndex(x => new { x.TargetId, x.StartedAt, x.RowId });
        healthInterval.HasOne<HealthTarget>().WithMany().HasForeignKey(x => x.TargetId).OnDelete(DeleteBehavior.Restrict);

        var healthObservation = modelBuilder.Entity<HealthObservation>();
        healthObservation.ToTable("HealthObservations", table => table.HasCheckConstraint("CK_HealthObservations_Values",
            "[LeaseGeneration] > 0 AND [NetworkGeneration] > 0 AND [Kind] BETWEEN 1 AND 7"));
        healthObservation.HasKey(x => x.RowId).IsClustered();
        healthObservation.Property(x => x.RowId).UseIdentityColumn();
        healthObservation.Property(x => x.SourceVersion).HasColumnType("binary(8)").IsRequired();
        healthObservation.Property(x => x.ServiceVersion).HasColumnType("binary(8)").IsRequired();
        healthObservation.Property(x => x.InstanceVersion).HasColumnType("binary(8)");
        healthObservation.Property(x => x.TargetVersion).HasColumnType("binary(8)").IsRequired();
        healthObservation.Property(x => x.NetworkVersion).HasColumnType("binary(8)").IsRequired();
        healthObservation.HasIndex(x => new { x.TargetId, x.ObservedAt, x.RowId });
        healthObservation.HasIndex(x => new { x.TargetId, x.LeaseGeneration }).IsUnique();
        healthObservation.HasOne<HealthTarget>().WithMany().HasForeignKey(x => x.TargetId).OnDelete(DeleteBehavior.Restrict);

        var rejections = modelBuilder.Entity<ReceptionRejectionSummary>();
        rejections.ToTable("ReceptionRejections", table => table.HasCheckConstraint("CK_ReceptionRejections_Kind", "[Kind] IN (1, 2, 3)"));
        rejections.HasKey(x => x.RowId).IsClustered();
        rejections.Property(x => x.RowId).UseIdentityColumn();
        rejections.HasIndex(x => x.Kind).IsUnique();
        rejections.HasOne<ServiceSource>().WithMany().HasForeignKey(x => x.ExampleSourceId).OnDelete(DeleteBehavior.SetNull);

        var source = modelBuilder.Entity<ServiceSource>();
        source.ToTable("Sources");
        source.HasKey(x => x.Id).IsClustered(false);
        source.Property(x => x.Id).ValueGeneratedNever();
        source.Property(x => x.RowId).UseIdentityColumn();
        source.HasIndex(x => x.RowId).IsUnique().IsClustered();
        source.Property(x => x.Name).HasMaxLength(128).IsRequired();
        source.Property(x => x.Verifier).HasColumnType("binary(32)").IsRequired();
        source.Property(x => x.Version).IsRowVersion();

        var collector = modelBuilder.Entity<CollectorRegistration>();
        collector.ToTable("Collectors");
        collector.HasKey(x => x.Id).IsClustered(false);
        collector.Property(x => x.Id).ValueGeneratedNever();
        collector.Property(x => x.RowId).UseIdentityColumn();
        collector.HasIndex(x => x.RowId).IsUnique().IsClustered();
        collector.Property(x => x.Name).HasMaxLength(128).IsRequired();
        collector.Property(x => x.Version).IsRowVersion();
        collector.HasIndex(x => x.MonitoringServiceId).IsUnique();
        collector.HasOne<MonitoredService>().WithMany().HasForeignKey(x => x.MonitoringServiceId).OnDelete(DeleteBehavior.Restrict);

        var route = modelBuilder.Entity<CollectorRoute>();
        route.ToTable("CollectorRoutes");
        route.HasKey(x => new { x.CollectorId, x.SourceId }).IsClustered();
        route.Property(x => x.Exporter).HasMaxLength(128).UseCollation("Latin1_General_100_BIN2").IsRequired();
        route.Property(x => x.ExporterBytes).HasColumnType("varbinary(256)")
            .HasComputedColumnSql("CONVERT(varbinary(256), [Exporter])", stored: true);
        route.HasIndex(x => new { x.CollectorId, x.ExporterBytes }).IsUnique();
        route.HasOne<CollectorRegistration>().WithMany().HasForeignKey(x => x.CollectorId).OnDelete(DeleteBehavior.Cascade);
        route.HasOne<ServiceSource>().WithMany().HasForeignKey(x => x.SourceId).OnDelete(DeleteBehavior.Restrict);

        var candidate = modelBuilder.Entity<ServiceCandidate>();
        candidate.ToTable("Candidates");
        candidate.HasKey(x => x.Id).IsClustered(false);
        candidate.Property(x => x.Id).ValueGeneratedNever();
        candidate.Property(x => x.RowId).UseIdentityColumn();
        candidate.HasIndex(x => x.RowId).IsUnique().IsClustered();
        candidate.Property(x => x.ServiceKey).HasMaxLength(128).UseCollation("Latin1_General_100_BIN2").IsRequired();
        candidate.Property(x => x.ServiceNamespace).HasMaxLength(128).UseCollation("Latin1_General_100_BIN2").HasDefaultValue("").IsRequired();
        candidate.Property(x => x.ServiceNamespaceBytes).HasColumnType("varbinary(256)").HasComputedColumnSql("CONVERT(varbinary(256), [ServiceNamespace])", stored: true);
        candidate.Property(x => x.Environment).HasMaxLength(64).UseCollation("Latin1_General_100_BIN2").IsRequired();
        candidate.Property(x => x.DisplayName).HasMaxLength(128).IsRequired();
        candidate.HasIndex(x => new { x.SourceId, x.ServiceNamespaceBytes, x.ServiceKey, x.Environment }).IsUnique();
        candidate.HasIndex(x => x.LastSeen);
        candidate.HasOne<ServiceSource>().WithMany().HasForeignKey(x => x.SourceId).OnDelete(DeleteBehavior.Restrict);

        var service = modelBuilder.Entity<MonitoredService>();
        service.ToTable("Services");
        service.HasKey(x => x.Id).IsClustered(false);
        service.Property(x => x.Id).ValueGeneratedNever();
        service.Property(x => x.RowId).UseIdentityColumn();
        service.Property(x => x.Version).IsRowVersion();
        service.HasIndex(x => x.RowId).IsUnique().IsClustered();
        service.Property(x => x.ServiceKey).HasMaxLength(128).UseCollation("Latin1_General_100_BIN2").IsRequired();
        service.Property(x => x.ServiceNamespace).HasMaxLength(128).UseCollation("Latin1_General_100_BIN2").HasDefaultValue("").IsRequired();
        service.Property(x => x.ServiceNamespaceBytes).HasColumnType("varbinary(256)").HasComputedColumnSql("CONVERT(varbinary(256), [ServiceNamespace])", stored: true);
        service.Property(x => x.Environment).HasMaxLength(64).UseCollation("Latin1_General_100_BIN2").IsRequired();
        service.Property(x => x.DisplayName).HasMaxLength(128).IsRequired();
        service.HasIndex(x => new { x.SourceId, x.ServiceNamespaceBytes, x.ServiceKey, x.Environment }).IsUnique();
        service.HasIndex(x => x.CandidateId).IsUnique();
        service.HasOne<ServiceSource>().WithMany().HasForeignKey(x => x.SourceId).OnDelete(DeleteBehavior.Restrict);
        service.HasAlternateKey(x => new { x.Id, x.SourceId });

        var instance = modelBuilder.Entity<ServiceInstance>();
        instance.ToTable("Instances");
        instance.HasKey(x => x.Id).IsClustered(false);
        instance.Property(x => x.Id).ValueGeneratedNever();
        instance.Property(x => x.RowId).UseIdentityColumn();
        instance.Property(x => x.Version).IsRowVersion();
        instance.HasIndex(x => x.RowId).IsUnique().IsClustered();
        instance.Property(x => x.ExternalInstanceId).HasMaxLength(256).IsRequired();
        instance.Property(x => x.ExternalInstanceIdBytes)
            .HasColumnType("varbinary(512)")
            .HasComputedColumnSql("CONVERT(varbinary(512), [ExternalInstanceId])", stored: true);
        instance.HasIndex(x => new { x.ServiceId, x.SourceScopeId, x.ExternalInstanceIdBytes })
            .IsUnique().HasFilter("[RetiredAt] IS NULL");
        instance.HasIndex(x => new { x.ServiceId, x.SourceScopeId, x.ExternalInstanceIdBytes, x.RetiredAt });
        instance.HasIndex(x => new { x.Id, x.ServiceId }).IsUnique();
        instance.HasOne<MonitoredService>().WithMany()
            .HasForeignKey(x => new { x.ServiceId, x.SourceScopeId })
            .HasPrincipalKey(x => new { x.Id, x.SourceId }).OnDelete(DeleteBehavior.Restrict);

        var policy = modelBuilder.Entity<InstancePolicy>();
        policy.ToTable("InstancePolicies", table =>
        {
            table.HasCheckConstraint("CK_InstancePolicies_Central", "[Name] = 'central'");
            table.HasCheckConstraint("CK_InstancePolicies_Limits", "[PerService] > 0 AND [Total] > 0");
        });
        policy.HasKey(x => x.Id).IsClustered(false);
        policy.Property(x => x.Id).ValueGeneratedNever();
        policy.Property(x => x.RowId).UseIdentityColumn();
        policy.HasIndex(x => x.RowId).IsUnique().IsClustered();
        policy.Property(x => x.Name).HasMaxLength(32).IsRequired();
        policy.HasIndex(x => x.Name).IsUnique();
        policy.Property(x => x.Version).IsRowVersion();

        var logContext = modelBuilder.Entity<LogContext>();
        logContext.ToTable("LogContexts", table => table.HasCheckConstraint("CK_LogContexts_Payload", "DATALENGTH([ResourcePayload]) <= 65536 AND DATALENGTH([ScopePayload]) <= 65536"));
        logContext.HasKey(x => x.RowId).IsClustered();
        logContext.Property(x => x.RowId).UseIdentityColumn();
        logContext.HasAlternateKey(x => new { x.RowId, x.ServiceId });
        logContext.Property(x => x.ResourcePayload).HasColumnType("varbinary(max)").IsRequired();
        logContext.Property(x => x.ScopePayload).HasColumnType("varbinary(max)").IsRequired();
        logContext.Property(x => x.ResourceSchemaUrl).HasMaxLength(2048).IsRequired();
        logContext.Property(x => x.ScopeSchemaUrl).HasMaxLength(2048).IsRequired();
        logContext.HasOne<MonitoredService>().WithMany().HasForeignKey(x => x.ServiceId).OnDelete(DeleteBehavior.Restrict);

        var log = modelBuilder.Entity<LogRecord>();
        log.ToTable("LogRecords", table =>
        {
            table.HasCheckConstraint("CK_LogRecords_Utc", "DATEPART(TZOFFSET, [OccurredAt]) = 0 AND DATEPART(TZOFFSET, [ReceivedAt]) = 0");
            table.HasCheckConstraint("CK_LogRecords_Payload", "DATALENGTH([RecordPayload]) <= 262144");
        });
        log.HasKey(x => x.RowId).IsClustered();
        log.Property(x => x.RowId).UseIdentityColumn();
        log.Property(x => x.TraceId).HasColumnType("binary(16)");
        log.Property(x => x.SpanId).HasColumnType("binary(8)");
        log.Property(x => x.RecordPayload).HasColumnType("varbinary(max)").IsRequired();
        log.HasIndex(x => new { x.ServiceId, x.OccurredAt, x.RowId });
        log.HasIndex(x => new { x.ServiceId, x.ReceivedAt });
        log.HasIndex(x => new { x.ServiceId, x.TraceId, x.OccurredAt }).HasFilter("[TraceId] IS NOT NULL");
        log.HasOne<MonitoredService>().WithMany().HasForeignKey(x => x.ServiceId).OnDelete(DeleteBehavior.Restrict);
        log.HasOne(x => x.Context).WithMany().HasForeignKey(x => new { x.ContextId, x.ServiceId })
            .HasPrincipalKey(x => new { x.RowId, x.ServiceId }).OnDelete(DeleteBehavior.Restrict);
        log.HasOne<ServiceInstance>().WithMany().HasForeignKey(x => new { x.InstanceId, x.ServiceId })
            .HasPrincipalKey(x => new { x.Id, x.ServiceId }).OnDelete(DeleteBehavior.Restrict);

        var traceContext = modelBuilder.Entity<TraceContext>();
        traceContext.ToTable("TraceContexts", table => table.HasCheckConstraint("CK_TraceContexts_Payload", "DATALENGTH([ResourcePayload]) <= 65536 AND DATALENGTH([ScopePayload]) <= 65536"));
        traceContext.HasKey(x => x.RowId).IsClustered();
        traceContext.Property(x => x.RowId).UseIdentityColumn();
        traceContext.HasAlternateKey(x => new { x.RowId, x.ServiceId });
        traceContext.Property(x => x.ResourcePayload).HasColumnType("varbinary(max)").IsRequired();
        traceContext.Property(x => x.ScopePayload).HasColumnType("varbinary(max)").IsRequired();
        traceContext.Property(x => x.ResourceSchemaUrl).HasMaxLength(2048).IsRequired();
        traceContext.Property(x => x.ScopeSchemaUrl).HasMaxLength(2048).IsRequired();
        traceContext.HasOne<MonitoredService>().WithMany().HasForeignKey(x => x.ServiceId).OnDelete(DeleteBehavior.Restrict);

        var span = modelBuilder.Entity<TraceSpan>();
        span.ToTable("TraceSpans", table =>
        {
            table.HasCheckConstraint("CK_TraceSpans_Utc", "DATEPART(TZOFFSET, [StartedAt]) = 0 AND DATEPART(TZOFFSET, [EndedAt]) = 0 AND DATEPART(TZOFFSET, [ReceivedAt]) = 0");
            table.HasCheckConstraint("CK_TraceSpans_Times", "[EndedAt] >= [StartedAt]");
            table.HasCheckConstraint("CK_TraceSpans_Payload", "DATALENGTH([SpanPayload]) <= 262144");
        });
        span.HasKey(x => x.RowId).IsClustered();
        span.Property(x => x.RowId).UseIdentityColumn();
        span.Property(x => x.TraceId).HasColumnType("binary(16)").IsRequired();
        span.Property(x => x.SpanId).HasColumnType("binary(8)").IsRequired();
        span.Property(x => x.ParentSpanId).HasColumnType("binary(8)");
        span.Property(x => x.SpanPayload).HasColumnType("varbinary(max)").IsRequired();
        span.HasIndex(x => new { x.ServiceId, x.StartedAt, x.RowId });
        span.HasIndex(x => new { x.ServiceId, x.ReceivedAt });
        span.HasIndex(x => new { x.ServiceId, x.TraceId, x.StartedAt, x.RowId });
        span.HasOne<MonitoredService>().WithMany().HasForeignKey(x => x.ServiceId).OnDelete(DeleteBehavior.Restrict);
        span.HasOne(x => x.Context).WithMany().HasForeignKey(x => new { x.ContextId, x.ServiceId })
            .HasPrincipalKey(x => new { x.RowId, x.ServiceId }).OnDelete(DeleteBehavior.Restrict);
        span.HasOne<ServiceInstance>().WithMany().HasForeignKey(x => new { x.InstanceId, x.ServiceId })
            .HasPrincipalKey(x => new { x.Id, x.ServiceId }).OnDelete(DeleteBehavior.Restrict);

        var metricContext = modelBuilder.Entity<MetricContext>();
        metricContext.ToTable("MetricContexts", table => table.HasCheckConstraint("CK_MetricContexts_Payload", "DATALENGTH([ResourcePayload]) <= 65536 AND DATALENGTH([ScopePayload]) <= 65536 AND DATALENGTH([MetricPayload]) <= 65536"));
        metricContext.HasKey(x => x.RowId).IsClustered();
        metricContext.Property(x => x.RowId).UseIdentityColumn();
        metricContext.HasAlternateKey(x => new { x.RowId, x.ServiceId });
        metricContext.Property(x => x.ResourcePayload).HasColumnType("varbinary(max)").IsRequired();
        metricContext.Property(x => x.ScopePayload).HasColumnType("varbinary(max)").IsRequired();
        metricContext.Property(x => x.MetricPayload).HasColumnType("varbinary(max)").IsRequired();
        metricContext.Property(x => x.MetricName).HasMaxLength(256).UseCollation("Latin1_General_100_BIN2");
        metricContext.Property(x => x.MetricNameBytes).HasColumnType("varbinary(512)")
            .HasComputedColumnSql("CONVERT(varbinary(512), [MetricName])", stored: true);
        metricContext.HasIndex(x => new { x.ServiceId, x.MetricNameBytes });
        metricContext.Property(x => x.ResourceSchemaUrl).HasMaxLength(2048).IsRequired();
        metricContext.Property(x => x.ScopeSchemaUrl).HasMaxLength(2048).IsRequired();
        metricContext.HasOne<MonitoredService>().WithMany().HasForeignKey(x => x.ServiceId).OnDelete(DeleteBehavior.Restrict);

        var point = modelBuilder.Entity<MetricPoint>();
        point.ToTable("MetricPoints", table =>
        {
            table.HasCheckConstraint("CK_MetricPoints_Utc", "DATEPART(TZOFFSET, [OccurredAt]) = 0 AND DATEPART(TZOFFSET, [ReceivedAt]) = 0");
            table.HasCheckConstraint("CK_MetricPoints_Kind", "[Kind] BETWEEN 1 AND 5");
            table.HasCheckConstraint("CK_MetricPoints_Payload", "DATALENGTH([PointPayload]) <= 262144");
        });
        point.HasKey(x => x.RowId).IsClustered();
        point.Property(x => x.RowId).UseIdentityColumn();
        point.Property(x => x.PointPayload).HasColumnType("varbinary(max)").IsRequired();
        point.HasIndex(x => new { x.ServiceId, x.OccurredAt, x.RowId });
        point.HasIndex(x => new { x.ServiceId, x.ReceivedAt });
        point.HasIndex(x => new { x.ContextId, x.ReceivedAt, x.RowId });
        point.HasOne<MonitoredService>().WithMany().HasForeignKey(x => x.ServiceId).OnDelete(DeleteBehavior.Restrict);
        point.HasOne(x => x.Context).WithMany().HasForeignKey(x => new { x.ContextId, x.ServiceId })
            .HasPrincipalKey(x => new { x.RowId, x.ServiceId }).OnDelete(DeleteBehavior.Restrict);
        point.HasOne<ServiceInstance>().WithMany().HasForeignKey(x => new { x.InstanceId, x.ServiceId })
            .HasPrincipalKey(x => new { x.Id, x.ServiceId }).OnDelete(DeleteBehavior.Restrict);

        var window = modelBuilder.Entity<MetricRollupWindow>();
        window.ToTable("MetricRollupWindows", table =>
        {
            table.HasCheckConstraint("CK_MetricRollupWindows_Utc", "DATEPART(TZOFFSET, [WindowStart]) = 0 AND ([ProjectedAt] IS NULL OR DATEPART(TZOFFSET, [ProjectedAt]) = 0)");
            table.HasCheckConstraint("CK_MetricRollupWindows_Aligned", "DATEPART(MINUTE, [WindowStart]) % 5 = 0 AND DATEPART(SECOND, [WindowStart]) = 0 AND DATEPART(NANOSECOND, [WindowStart]) = 0");
            table.HasCheckConstraint("CK_MetricRollupWindows_Revisions", "[InputRevision] > 0 AND (([ProjectedRevision] IS NULL AND [ProjectionVersion] IS NULL AND [ProjectedAt] IS NULL AND [IsComplete] = 0 AND [RawPruned] = 0) OR ([ProjectedRevision] IS NOT NULL AND [ProjectedRevision] > 0 AND [ProjectedRevision] <= [InputRevision] AND [ProjectionVersion] IS NOT NULL AND [ProjectionVersion] > 0 AND [ProjectedAt] IS NOT NULL))");
        });
        window.HasKey(x => x.RowId).IsClustered();
        window.Property(x => x.RowId).UseIdentityColumn();
        window.Property(x => x.Version).IsRowVersion();
        window.HasAlternateKey(x => new { x.RowId, x.ServiceId });
        window.HasIndex(x => new { x.ServiceId, x.WindowStart }).IsUnique();
        window.HasOne<MonitoredService>().WithMany().HasForeignKey(x => x.ServiceId).OnDelete(DeleteBehavior.Restrict);

        var rollupSeries = modelBuilder.Entity<MetricRollupSeries>();
        rollupSeries.ToTable("MetricRollupSeries", table =>
        {
            table.HasCheckConstraint("CK_MetricRollupSeries_Bounds", "DATALENGTH([IdentityHash]) = 32 AND DATALENGTH([IdentityBytes]) BETWEEN 1 AND 524288 AND ([ResultPayload] IS NULL OR DATALENGTH([ResultPayload]) <= 16777216) AND [CollisionOrdinal] >= 0 AND [Mode] BETWEEN 1 AND 6 AND [QualityFlags] >= 0 AND [ResultVersion] > 0 AND [InputCount] > 0 AND [InputBytes] >= 0");
            table.HasCheckConstraint("CK_MetricRollupSeries_Preservation", "([Mode] <> 6 OR [RequiresRaw] = 1) AND ([RequiresRaw] = 1 OR [ResultPayload] IS NOT NULL)");
        });
        rollupSeries.HasKey(x => x.RowId).IsClustered();
        rollupSeries.Property(x => x.RowId).UseIdentityColumn();
        rollupSeries.Property(x => x.Mode).HasConversion<byte>();
        rollupSeries.Property(x => x.IdentityHash).HasColumnType("binary(32)").IsRequired();
        rollupSeries.Property(x => x.IdentityBytes).HasColumnType("varbinary(max)").IsRequired();
        rollupSeries.Property(x => x.ResultPayload).HasColumnType("varbinary(max)");
        rollupSeries.HasIndex(x => new { x.WindowRowId, x.IdentityHash, x.CollisionOrdinal }).IsUnique();
        rollupSeries.HasOne<MetricRollupWindow>().WithMany().HasForeignKey(x => new { x.WindowRowId, x.ServiceId })
            .HasPrincipalKey(x => new { x.RowId, x.ServiceId }).OnDelete(DeleteBehavior.Cascade);
        rollupSeries.HasOne<ServiceInstance>().WithMany().HasForeignKey(x => new { x.InstanceId, x.ServiceId })
            .HasPrincipalKey(x => new { x.Id, x.ServiceId }).OnDelete(DeleteBehavior.Restrict);
    }
}
