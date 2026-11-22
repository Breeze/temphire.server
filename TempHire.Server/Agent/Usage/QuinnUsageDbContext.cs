using Microsoft.EntityFrameworkCore;

namespace TempHire.Server.Agent.Usage;

/// <summary>
/// Quinn's usage records, in a context of their own rather than in <c>TempHireDbContext</c>.
///
/// <para>
/// The business context is the client's data model: Breeze builds its metadata from it, the
/// client generates its entity classes from that, and every entity in it is tenant-filtered and
/// saved through Breeze's rules.  Usage records are none of that - the server alone writes them -
/// so they live here, where nothing about them can leak into the client's model.
/// </para>
/// <para>
/// The tables are in the same database as the business data (the <c>TempHire</c> connection
/// string), so there is nothing new to configure or run.  This context has its own migrations
/// (<c>Migrations/QuinnUsage</c>) and its own migrations history table, so the two contexts'
/// migrations never mix.  Being in another context, the rows have no foreign key to
/// <c>Tenants</c>; the tenant id comes from the user's token.
/// </para>
/// </summary>
public class QuinnUsageDbContext(
    DbContextOptions<QuinnUsageDbContext> options)
    : DbContext(options)
{
    /// <summary>This context's migrations history, apart from <c>TempHireDbContext</c>'s.</summary>
    public const string MigrationsHistoryTable = "__QuinnUsageMigrationsHistory";

    public DbSet<QuinnUsage> QuinnUsage { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<QuinnUsage>(b =>
        {
            b.ToTable("QuinnUsage");
            b.Property(e => e.Provider).HasMaxLength(50).IsRequired();
            b.Property(e => e.Model).HasMaxLength(200).IsRequired();
            // One row per tenant, user, model and day.  The key is also the index the quota
            // checks use: a company's rows, from a date on.
            b.HasKey(e => new { e.TenantId, e.UserId, e.Provider, e.Model, e.Day });
        });
    }
}
