using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TgLlmBot.DataAccess.Models;

namespace TgLlmBot.DataAccess.EntityTypeConfigurations;

public class DbMediaDescriptionEntityTypeConfiguration : IEntityTypeConfiguration<DbMediaDescription>
{
    public void Configure(EntityTypeBuilder<DbMediaDescription> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.HasKey(x => x.FileUniqueId);
        builder.Property(x => x.FileUniqueId).HasMaxLength(64);
        // Без потолка - как и в колонке описания у самого вложения
        builder.Property(x => x.Description).HasColumnType("text");
        builder.HasIndex(x => x.CreatedAt).HasDatabaseName("idx_mediadescriptions_createdat");
    }
}
