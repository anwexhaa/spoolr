using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Spoolr.Core.Printers;

namespace Spoolr.Infrastructure.Persistence.Configurations;

internal sealed class PrinterConfiguration : IEntityTypeConfiguration<Printer>
{
    public void Configure(EntityTypeBuilder<Printer> builder)
    {
        builder.ToTable("Printers");

        builder.HasKey(p => p.Id);

        // Assigned by the domain, not the database.
        builder.Property(p => p.Id).ValueGeneratedNever();

        builder.Property(p => p.Version).IsConcurrencyToken();

        builder.Property(p => p.Name).HasMaxLength(128).IsRequired();
        builder.Property(p => p.Location).HasMaxLength(256).IsRequired();
        builder.Property(p => p.Model).HasMaxLength(128).IsRequired();

        // PBKDF2 output plus its salt and parameters, encoded as one string.
        builder.Property(p => p.DeviceKeyHash).HasMaxLength(256).IsRequired();

        builder.Property(p => p.ReportedStatus).HasConversion<string>().HasMaxLength(16).IsRequired();

        builder.HasIndex(p => p.Name)
            .IsUnique()
            .HasDatabaseName("IX_Printers_Name");
    }
}
