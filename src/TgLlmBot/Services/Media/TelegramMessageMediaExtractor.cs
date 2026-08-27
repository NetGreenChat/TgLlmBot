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

            // Статический стикер - это WEBP, его можно скормить модели как есть. Анимированный
            // (TGS, gzip-нутый Lottie) разворачивается в кадры своими руками, видео (WEBM VP9)
            // уходит файлом целиком - декодирует его сервер модели. Превью держим на всякий
            // случай: если ни то, ни другое не выйдет, модель увидит хотя бы один кадр.
            result.Add(new()
            {
                Order = result.Count + 1,
                Kind = DbMediaKind.Sticker,
                // Идентификатор именно стикера, а не превью: кэш описаний должен склеивать
                // один и тот же стикер независимо от того, что мы в итоге скачали
                FileUniqueId = sticker.FileUniqueId,
                DownloadFileId = sticker.FileId,
                ThumbnailFileId = sticker.Thumbnail?.FileId,
                Emoji = sticker.Emoji,
                SetName = sticker.SetName,
                IsAnimated = sticker.IsAnimated || sticker.IsVideo,
                Status = DbMediaRecognitionStatus.Pending
            });
        }

        if (message.Animation is not null)
        {
            var animation = message.Animation;
            result.Add(new()
            {
                Order = result.Count + 1,
                Kind = DbMediaKind.Animation,
                FileUniqueId = animation.FileUniqueId,
                DownloadFileId = animation.FileId,
                ThumbnailFileId = animation.Thumbnail?.FileId,
                IsAnimated = true,
                Status = DbMediaRecognitionStatus.Pending
            });
        }

        if (message.Video is not null)
        {
            var video = message.Video;
            result.Add(new()
            {
                Order = result.Count + 1,
                Kind = DbMediaKind.Video,
                FileUniqueId = video.FileUniqueId,
                DownloadFileId = video.FileId,
                ThumbnailFileId = video.Thumbnail?.FileId,
                IsAnimated = true,
                Status = DbMediaRecognitionStatus.Pending
            });
        }

        if (message.VideoNote is not null)
        {
            // Круглое видеосообщение: для истории чата это такое же видео, только без подписи
            var videoNote = message.VideoNote;
            result.Add(new()
            {
                Order = result.Count + 1,
                Kind = DbMediaKind.Video,
                FileUniqueId = videoNote.FileUniqueId,
                DownloadFileId = videoNote.FileId,
                ThumbnailFileId = videoNote.Thumbnail?.FileId,
                IsAnimated = true,
                Status = DbMediaRecognitionStatus.Pending
            });
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
