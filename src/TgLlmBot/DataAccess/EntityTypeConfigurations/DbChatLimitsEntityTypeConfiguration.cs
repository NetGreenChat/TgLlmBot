using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TgLlmBot.DataAccess.Models;

namespace TgLlmBot.DataAccess.EntityTypeConfigurations;

public class DbChatLimitsEntityTypeConfiguration : IEntityTypeConfiguration<DbChatLimit>
{
    public void Configure(EntityTypeBuilder<DbChatLimit> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.HasKey(x => x.ChatId);
    }
}
