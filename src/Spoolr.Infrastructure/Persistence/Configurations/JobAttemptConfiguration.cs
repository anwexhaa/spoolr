using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Spoolr.Core.Jobs;

namespace Spoolr.Infrastructure.Persistence.Configurations;

internal sealed class JobAttemptConfiguration : IEntityTypeConfiguration<JobAttempt>
{
    public void Configure(EntityTypeBuilder<JobAttempt> builder)
    {
        builder.ToTable("JobAttempts");

        builder.HasKey(a => a.Id);

        builder.Property(a => a.Error).HasMaxLength(1024);

        builder.Ignore(a => a.Duration);

        builder.HasIndex(a => new { a.JobId, a.AttemptNumber })
            .IsUnique()
            .HasDatabaseName("IX_JobAttempts_JobId_AttemptNumber");
    }
}
