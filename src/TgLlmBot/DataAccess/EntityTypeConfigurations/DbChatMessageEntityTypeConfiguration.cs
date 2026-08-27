using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TgLlmBot.DataAccess.Models;

namespace TgLlmBot.DataAccess.EntityTypeConfigurations;

public class DbChatMessageEntityTypeConfiguration : IEntityTypeConfiguration<DbChatMessage>
{
    public void Configure(EntityTypeBuilder<DbChatMessage> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id)
            .HasColumnType("uuid")
            .HasDefaultValueSql("uuidv7()");
        builder.Property(x => x.FromUsername).HasMaxLength(32);
        builder.Property(x => x.FromFirstName).HasMaxLength(64);
        builder.Property(x => x.FromLastName).HasMaxLength(64);
        builder.Property(x => x.Text).HasMaxLength(4096);
        builder.Property(x => x.Caption).HasMaxLength(4096);
        builder.Property(x => x.MediaGroupId).HasMaxLength(64);

        // Индекс для CTE target_message (MessageId, ChatId)
        builder.HasIndex(e => new
            {
                e.MessageId,
                e.ChatId
            })
            .HasDatabaseName("idx_chathistory_messageid_chatid");

        // Основной композитный индекс (ChatId, Date DESC)
        builder.HasIndex(e => new
            {
                e.ChatId,
                e.Date
            })
            .HasDatabaseName("idx_chathistory_chatid_date_desc")
            .IsDescending(false, true); // ChatId ASC, Date DESC

        // Дополнительный индекс (опционально)
        builder.HasIndex(e => new
            {
                e.ChatId,
                e.MessageId,
                e.Date
            })
            .HasDatabaseName("idx_chathistory_chatid_messageid_date")
            .IsDescending(false, false, true); // ChatId ASC, MessageId ASC, Date DESC

        // Сборка альбома обратно в одно логическое сообщение
        builder.HasIndex(e => new
            {
                e.ChatId,
                e.MediaGroupId
            })
            .HasDatabaseName("idx_chathistory_chatid_mediagroupid")
            .HasFilter("\"MediaGroupId\" IS NOT NULL");
    }
}
