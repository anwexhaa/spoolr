using Microsoft.EntityFrameworkCore;
using Spoolr.Core.Jobs;
using Spoolr.Core.Printers;

namespace Spoolr.Infrastructure.Persistence;

/// <summary>
/// The service database. Backed by Azure SQL in deployed environments and by SQLite for
/// local development and tests, so the same mappings are exercised in both.
/// </summary>
public sealed class SpoolrDbContext(DbContextOptions<SpoolrDbContext> options) : DbContext(options)
{
    public DbSet<PrintJob> Jobs => Set<PrintJob>();

    public DbSet<JobAttempt> JobAttempts => Set<JobAttempt>();

    public DbSet<Printer> Printers => Set<Printer>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(SpoolrDbContext).Assembly);
    }
}
