using System;
using System.ComponentModel.DataAnnotations.Schema;

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
    ///     Идентификатор основного файла вложения - картинки, стикера, гифки или видео, -
    ///     который нужно скачать, чтобы показать вложение vision-модели.
    ///     Пусто, если показывать нечего.
    /// </summary>
    public string? DownloadFileId { get; set; }

    /// <summary>
    ///     Идентификатор статического превью, которое Telegram отдаёт к стикерам, гифкам и видео.
    ///     Подстраховка на случай, когда с основным файлом не сложилось: анимация не отрисовалась,
    ///     формат не опознался или файл слишком большой, чтобы показывать его модели целиком.
    /// </summary>
    public string? ThumbnailFileId { get; set; }

    /// <summary>
    ///     Модели есть что показать: у вложения нашёлся хоть какой-то файл - свой или превью.
    /// </summary>
    [NotMapped]
    public bool HasShowableFile =>
        !string.IsNullOrEmpty(DownloadFileId) || !string.IsNullOrEmpty(ThumbnailFileId);

    /// <summary>
    ///     Эмодзи, которому соответствует стикер.
    /// </summary>
    public string? Emoji { get; set; }

    /// <summary>
    ///     Название стикерпака, из которого пришёл стикер.
    /// </summary>
    public string? SetName { get; set; }

    /// <summary>
    ///     Вложение движется: анимированный (TGS) или видео (WEBM) стикер, гифка или видео.
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
