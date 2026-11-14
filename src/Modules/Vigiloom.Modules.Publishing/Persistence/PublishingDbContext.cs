using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.Publishing.Domain;

namespace Vigiloom.Modules.Publishing.Persistence;

public sealed class PublishingDbContext(DbContextOptions<PublishingDbContext> options) : DbContext(options)
{
    public DbSet<PublicationTemplate> Templates => Set<PublicationTemplate>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("publishing");
        var template = modelBuilder.Entity<PublicationTemplate>();
        template.ToTable("Templates", table =>
        {
            table.HasCheckConstraint("CK_Templates_DefaultActive", "[IsDefault] = 0 OR [IsRetired] = 0");
            table.HasCheckConstraint("CK_Templates_HistoryPeriod", "([HistoryDays] = 0 AND [HistorySince] IS NULL) OR ([HistoryDays] BETWEEN 1 AND 7 AND [HistorySince] IS NOT NULL)");
        });
        template.HasKey(x => x.Id).IsClustered(false);
        template.Property(x => x.Id).ValueGeneratedNever();
        template.Property(x => x.RowId).UseIdentityColumn();
        template.HasIndex(x => x.RowId).IsUnique().IsClustered();
        template.Property(x => x.Name).HasMaxLength(256).UseCollation("Latin1_General_100_CI_AS_SC").IsRequired();
        template.HasIndex(x => x.Name).IsUnique().HasFilter("[IsRetired] = 0");
        template.HasIndex(x => x.IsDefault).IsUnique().HasFilter("[IsDefault] = 1");
        template.Property(x => x.Version).IsRowVersion();
    }
}
