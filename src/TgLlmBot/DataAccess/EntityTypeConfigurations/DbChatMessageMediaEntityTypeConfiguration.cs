using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TgLlmBot.DataAccess.Models;
using TgLlmBot.Services.Media;

namespace TgLlmBot.DataAccess.EntityTypeConfigurations;

public class DbChatMessageMediaEntityTypeConfiguration : IEntityTypeConfiguration<DbChatMessageMedia>
{
    public void Configure(EntityTypeBuilder<DbChatMessageMedia> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id)
            .HasColumnType("uuid")
            .HasDefaultValueSql("uuidv7()");
        builder.Property(x => x.FileUniqueId).HasMaxLength(64).IsRequired();
        builder.Property(x => x.DownloadFileId).HasMaxLength(256);
        builder.Property(x => x.Emoji).HasMaxLength(32);
        builder.Property(x => x.SetName).HasMaxLength(128);
        // Сжатое описание ограничено по построению, поэтому обычная varchar с запасом
        builder.Property(x => x.ShortDescription).HasMaxLength(MediaDescriptionLimits.ShortMaxLength * 2);
        // Перечисления строками: в базу чаще всего смотрят глазами, а не джойнят по ним
        builder.Property(x => x.Kind).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(32).IsRequired();

        // Каскад именно на стороне базы: чистка старой истории удаляет сообщения массовой
        // операцией, в обход загрузки сущностей, и подчищать вложения должна сама база
        builder.HasOne(x => x.Message)
            .WithMany(x => x.Media)
            .HasForeignKey(x => x.ChatMessageId)
            .OnDelete(DeleteBehavior.Cascade)
            .IsRequired();

        // Подтягивание вложений к уже выбранным сообщениям
        builder.HasIndex(e => new
            {
                e.ChatMessageId,
                e.Order
            })
            .HasDatabaseName("idx_chatmessagemedia_chatmessageid_order");

        // Поиск недоделанного хвоста: ещё не распознанных вложений
        builder.HasIndex(e => e.Status)
            .HasDatabaseName("idx_chatmessagemedia_status");

        // Переиспользование описаний одного и того же файла
        builder.HasIndex(e => e.FileUniqueId)
            .HasDatabaseName("idx_chatmessagemedia_fileuniqueid");
    }
}
