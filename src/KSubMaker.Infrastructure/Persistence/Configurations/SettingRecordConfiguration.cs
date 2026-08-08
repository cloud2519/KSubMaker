using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KSubMaker.Infrastructure.Persistence.Configurations;

public sealed class SettingRecordConfiguration : IEntityTypeConfiguration<SettingRecord>
{
    public void Configure(EntityTypeBuilder<SettingRecord> builder)
    {
        builder.ToTable("AppSettings");
        builder.HasKey(s => s.Key);

        builder.Property(s => s.Key).HasMaxLength(128).IsRequired();

        // The glossary is stored as a JSON blob under a single key, so no practical length limit.
        builder.Property(s => s.Value).IsRequired();
    }
}
