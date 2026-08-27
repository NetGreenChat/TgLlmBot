using System;
using System.Collections.Generic;
using System.Linq;
using Telegram.Bot.Types;
using TgLlmBot.DataAccess.Models;

namespace TgLlmBot.Services.Media;

/// <summary>
///     Достаёт из сообщения Telegram список вложений, которые имеет смысл показать vision-модели.
/// </summary>
/// <remarks>
///     Telegram кладёт в одно сообщение не больше одного медиа-вложения: альбом приезжает
///     отдельными сообщениями с общим <see cref="Message.MediaGroupId" />. Метод всё равно возвращает
///     массив - так модель хранения не придётся менять, если у сообщения появится несколько вложений.
/// </remarks>
public static class TelegramMessageMediaExtractor
{
    public static DbChatMessageMedia[] Extract(Message message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var result = new List<DbChatMessageMedia>(1);
        if (message.Photo?.Length > 0)
        {
            var photoSize = SelectPhotoSizeForLlm(message.Photo);
            if (photoSize is not null)
            {
                result.Add(new()
                {
                    Order = result.Count + 1,
                    Kind = DbMediaKind.Photo,
                    FileUniqueId = photoSize.FileUniqueId,
                    DownloadFileId = photoSize.FileId,
                    Status = DbMediaRecognitionStatus.Pending
                });
            }
        }

        if (message.Sticker is not null)
        {
            var sticker = message.Sticker;

            // Статический стикер - это WEBP, его можно скормить модели как есть.
            // Анимированный (TGS, gzip-нутый Lottie) и видео (WEBM VP9) без внешнего рендерера
            // не развернуть, зато Telegram отдаёт к ним статическое превью - его и показываем.
            if (!sticker.IsAnimated && !sticker.IsVideo)
            {
                result.Add(new()
                {
                    Order = result.Count + 1,
                    Kind = DbMediaKind.Sticker,
                    // Идентификатор именно стикера, а не превью: кэш описаний должен склеивать
                    // один и тот же стикер независимо от того, что мы в итоге скачали
                    FileUniqueId = sticker.FileUniqueId,
                    DownloadFileId = sticker.FileId,
                    Emoji = sticker.Emoji,
                    SetName = sticker.SetName,
                    IsAnimated = false,
                    Status = DbMediaRecognitionStatus.Pending
                });
            }
        }

        return result.ToArray();
    }

    /// <summary>
    ///     Telegram отдаёт картинку в нескольких разрешениях. Берём самое крупное,
    ///     ориентируясь на большую из сторон, чтобы не потерять детали у вертикальных скриншотов.
    /// </summary>
    private static PhotoSize? SelectPhotoSizeForLlm(PhotoSize[] photo)
    {
        var photoSize = photo.MaxBy(x => x.Width);
        if (photoSize is null)
        {
            return null;
        }

        if (photoSize.Width > photoSize.Height)
        {
            return photoSize;
        }

        return photo.MaxBy(x => x.Height);
    }
}
