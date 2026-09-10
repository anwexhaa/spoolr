using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Spoolr.Core.Jobs;

namespace Spoolr.Infrastructure.Persistence.Configurations;

internal sealed class PrintJobConfiguration : IEntityTypeConfiguration<PrintJob>
{
    public void Configure(EntityTypeBuilder<PrintJob> builder)
    {
        builder.ToTable("PrintJobs");

        builder.HasKey(j => j.Id);

        // Guards the claim: two dispatcher replicas reading the same row both write with
        // the version they read, and the loser gets a concurrency exception instead of
        // silently overwriting the winner.
        builder.Property(j => j.Version).IsConcurrencyToken();

        builder.Property(j => j.DocumentName).HasMaxLength(260).IsRequired();
        builder.Property(j => j.SubmittedBy).HasMaxLength(256).IsRequired();
        builder.Property(j => j.LastError).HasMaxLength(1024);

        // Stored as text so an on-call engineer reading the table sees "Queued" rather
        // than having to remember what 0 means. Status is only ever compared for equality,
        // so text costs nothing here.
        builder.Property(j => j.Status).HasConversion<string>().HasMaxLength(16).IsRequired();

        // Priority stays numeric, unlike Status. The dispatcher orders on it, and ordering
        // the text form would rank High below Low and Normal alphabetically.
        builder.Property(j => j.Priority).IsRequired();

        builder.Ignore(j => j.IsTerminal);
        builder.Ignore(j => j.HasAttemptsRemaining);

        builder.HasMany(j => j.Attempts)
            .WithOne()
            .HasForeignKey(a => a.JobId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Metadata
            .FindNavigation(nameof(PrintJob.Attempts))!
            .SetPropertyAccessMode(PropertyAccessMode.Field);

        // The dispatcher's hot path: claimable jobs for one printer, best first.
        builder.HasIndex(j => new { j.PrinterId, j.Status, j.AvailableAt })
            .HasDatabaseName("IX_PrintJobs_Claim");

        // Backs the stalled-dispatch sweep and the queue-depth gauge.
        builder.HasIndex(j => new { j.Status, j.AvailableAt })
            .HasDatabaseName("IX_PrintJobs_Status_AvailableAt");

        builder.HasIndex(j => j.SubmittedBy)
            .HasDatabaseName("IX_PrintJobs_SubmittedBy");
    }
}
