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

    public DbMediaDescription(string fileUniqueId, string description, bool isFallback, DateTime createdAt)
    {
        FileUniqueId = fileUniqueId;
        Description = description;
        IsFallback = isFallback;
        CreatedAt = createdAt;
    }

    public string FileUniqueId { get; set; } = default!;
    public string Description { get; set; } = default!;

    /// <summary>
    ///     Описание снято со статического превью, а не с самого файла: с основным файлом
    ///     в тот раз не сложилось.
    /// </summary>
    /// <remarks>
    ///     Такое описание держится в кэше только до первой удачной попытки разглядеть сам файл:
    ///     иначе один сбой рендеринга навсегда оставил бы анимированный стикер одним кадром.
    /// </remarks>
    public bool IsFallback { get; set; }

    public DateTime CreatedAt { get; set; }
}
