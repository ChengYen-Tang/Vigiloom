using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Vigiloom.Modules.IdentityAccess.Domain;

namespace Vigiloom.Modules.IdentityAccess.Persistence;

public sealed class IdentityAccessDbContext(DbContextOptions<IdentityAccessDbContext> options)
    : IdentityDbContext<Account, Role, Guid>(options)
{
    public DbSet<AccountAuditEvent> AccountAuditEvents => Set<AccountAuditEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.HasDefaultSchema("identity");
        var account = modelBuilder.Entity<Account>();
        account.ToTable("AspNetUsers", table => table.HasCheckConstraint("CK_AspNetUsers_Id_NotEmpty", "[Id] <> '00000000-0000-0000-0000-000000000000'"));
        account.HasKey(x => x.Id).IsClustered(false);
        account.Property(x => x.Id).ValueGeneratedNever();
        account.Property(x => x.RowId).UseIdentityColumn().Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Ignore);
        account.HasIndex(x => x.RowId).IsUnique().IsClustered();
        account.Property(x => x.Version).IsRowVersion();
        var role = modelBuilder.Entity<Role>();
        role.ToTable("AspNetRoles", table => table.HasCheckConstraint("CK_AspNetRoles_Id_NotEmpty", "[Id] <> '00000000-0000-0000-0000-000000000000'"));
        role.HasKey(x => x.Id).IsClustered(false);
        role.Property(x => x.Id).ValueGeneratedNever();
        role.Property(x => x.RowId).UseIdentityColumn().Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Ignore);
        role.HasIndex(x => x.RowId).IsUnique().IsClustered();
        role.Property(x => x.Version).IsRowVersion();
        var audit = modelBuilder.Entity<AccountAuditEvent>();
        audit.ToTable("AccountAuditEvents");
        audit.HasKey(x => x.RowId).IsClustered();
        audit.Property(x => x.RowId).UseIdentityColumn();
        audit.Property(x => x.Action).HasMaxLength(64).IsRequired();
        audit.HasIndex(x => new { x.SubjectId, x.OccurredAt, x.RowId });
        // Identity owns the related claim, login, token and membership keys.
        modelBuilder.Entity<IdentityUserRole<Guid>>().HasKey(x => new { x.UserId, x.RoleId });
    }
}
