using System;

namespace TgLlmBot.DataAccess.Models;

/// <summary>
///     Кэш описаний вложений, ключ - стабильный идентификатор файла в Telegram.
/// </summary>
/// <remarks>
///     Один и тот же мем или стикер прилетает в чат десятки раз. Vision-модель тратит на картинку
///     заметно больше времени, чем стоит поход в базу, поэтому повторы описываются один раз.
/// </remarks>
public class DbMediaDescription
{
    public DbMediaDescription()
    {
    }

    public DbMediaDescription(string fileUniqueId, string description, DateTime createdAt)
    {
        FileUniqueId = fileUniqueId;
        Description = description;
        CreatedAt = createdAt;
    }

    public string FileUniqueId { get; set; } = default!;
    public string Description { get; set; } = default!;
    public DateTime CreatedAt { get; set; }
}
