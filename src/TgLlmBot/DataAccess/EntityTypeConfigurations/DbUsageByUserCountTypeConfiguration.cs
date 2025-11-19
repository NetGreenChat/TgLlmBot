using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TgLlmBot.DataAccess.Models;

namespace TgLlmBot.DataAccess.EntityTypeConfigurations;

public class DbUsageByUserCountTypeConfiguration : IEntityTypeConfiguration<DbUsageByUserCount>
{
    public void Configure(EntityTypeBuilder<DbUsageByUserCount> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.HasKey(x => new {x.FromUserId, x.ChatId});
        builder.HasIndex(x => x.FromUserId).IsUnique(false);
        builder.HasIndex(x => x.ChatId).IsUnique(false);
        builder.Property(x => x.FromUsername).HasMaxLength(32);
        builder.Property(x => x.FromFirstName).HasMaxLength(64);
        builder.Property(x => x.FromLastName).HasMaxLength(64);
        builder.Property(x => x.CostInUsd).HasPrecision(16, 8)
            .HasConversion(
                v => decimal.Round(v, 8, MidpointRounding.ToZero),
                v => v);
    }
}
