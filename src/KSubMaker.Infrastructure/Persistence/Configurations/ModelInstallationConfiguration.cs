using KSubMaker.Domain.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KSubMaker.Infrastructure.Persistence.Configurations;

public sealed class ModelInstallationConfiguration : IEntityTypeConfiguration<ModelInstallation>
{
    public void Configure(EntityTypeBuilder<ModelInstallation> builder)
    {
        builder.ToTable("Models");
        builder.HasKey(m => m.Id);

        builder.Property(m => m.Id).HasMaxLength(128).IsRequired();

        // Stored by name for the same reason as JobStatus: the catalog gains model kinds over time.
        builder.Property(m => m.Type).HasConversion<string>().HasMaxLength(32).IsRequired();

        builder.Property(m => m.Name).HasMaxLength(256).IsRequired();
        builder.Property(m => m.Version).HasMaxLength(32).IsRequired();
        builder.Property(m => m.LocalPath).HasMaxLength(1024);
        builder.Property(m => m.DownloadUrl).HasMaxLength(1024);

        // Hex SHA-256 of the manifest.
        builder.Property(m => m.Sha256).HasMaxLength(64);

        builder.HasIndex(m => m.Type).HasDatabaseName("IX_Models_Type");
    }
}
