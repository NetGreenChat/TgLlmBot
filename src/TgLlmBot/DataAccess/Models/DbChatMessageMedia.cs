using System;

namespace TgLlmBot.DataAccess.Models;

/// <summary>
///     Метаданные одного вложения сообщения: что это, откуда качать и что на нём изображено.
/// </summary>
/// <remarks>
///     Отдельная таблица, привязанная к сообщению внешним ключом с каскадным удалением: чистка
///     старой истории сносит сообщения массовой операцией, и вложения должна убирать сама база.
///     Подробное описание от vision-модели в базе не хранится вовсе: оно живёт только в памяти
///     воркера на время обработки, а в историю уходит сжатое (<see cref="ShortDescription" />).
/// </remarks>
public class DbChatMessageMedia
{
    public Guid Id { get; set; }

    /// <summary>
    ///     Сообщение, к которому приложено вложение.
    /// </summary>
    public Guid ChatMessageId { get; set; }

    /// <summary>
    ///     Порядковый номер вложения внутри сообщения, начиная с 1.
    /// </summary>
    public int Order { get; set; }

    /// <summary>
    ///     Вид вложения.
    /// </summary>
    public DbMediaKind Kind { get; set; }

    /// <summary>
    ///     Стабильный идентификатор файла в Telegram. Не меняется между пересылками,
    ///     поэтому используется ключом кэша описаний (<see cref="DbMediaDescription" />) -
    ///     кэшируются при этом только стикеры.
    /// </summary>
    public string FileUniqueId { get; set; } = default!;

    /// <summary>
    ///     Идентификатор файла, который нужно скачать, чтобы показать вложение vision-модели.
    ///     Для анимированных и видео-стикеров это статическое превью, а не сам стикер.
    ///     Пусто, если показывать нечего.
    /// </summary>
    public string? DownloadFileId { get; set; }

    /// <summary>
    ///     Эмодзи, которому соответствует стикер.
    /// </summary>
    public string? Emoji { get; set; }

    /// <summary>
    ///     Название стикерпака, из которого пришёл стикер.
    /// </summary>
    public string? SetName { get; set; }

    /// <summary>
    ///     Вложение движется: анимированный (TGS) или видео (WEBM) стикер.
    ///     Распознаётся при этом только статическое превью.
    /// </summary>
    public bool IsAnimated { get; set; }

    /// <summary>
    ///     Состояние распознавания.
    /// </summary>
    public DbMediaRecognitionStatus Status { get; set; }

    /// <summary>
    ///     Сжатое основной моделью описание - то, что остаётся в истории чата.
    ///     Именно оно уходит в контекст LLM при каждом сообщении.
    /// </summary>
    public string? ShortDescription { get; set; }

    public DbChatMessage? Message { get; set; }
}
