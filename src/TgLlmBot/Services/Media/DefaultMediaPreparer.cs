using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TgLlmBot.DataAccess.Models;
using TgLlmBot.Models;

namespace TgLlmBot.Services.Media;

/// <summary>
///     Готовит вложение к показу vision-модели, выбирая способ по формату скачанного файла.
/// </summary>
/// <remarks>
///     Способов три. Картинка уходит как есть. Видео (WEBM видео-стикеров, MP4 гифок и видео)
///     уходит файлом целиком: декодировать его и нарезать кадры будет сервер модели, у которого
///     на это есть и OpenCV, и вычислительные ресурсы. Анимированный стикер (TGS) не откроет ни
///     один декодер видео, поэтому его кадры рисуются здесь, на месте.
///     Если с основным файлом не сложилось, в дело идёт статическое превью от Telegram:
///     один кадр в истории всё равно лучше, чем "разглядеть не удалось".
///     Своего потолка на размер файла здесь нет: хватает того, что Bot API отдаёт не больше 20 МБ.
///     Замер на 18,5-мегабайтном видео - около 220 МБ пиковой памяти (сам файл, base64 в UTF-16,
///     тело запроса, буфер HTTP), и столько запросов в полёте, сколько чатов обрабатывается
///     одновременно. Если разрешённых чатов станет заметно больше пары, потолок придётся вернуть.
/// </remarks>
public partial class DefaultMediaPreparer : IMediaPreparer
{
    private readonly ITelegramMediaDownloader _downloader;
    private readonly ILogger<DefaultMediaPreparer> _logger;
    private readonly IAnimatedStickerRenderer _renderer;

    public DefaultMediaPreparer(
        ITelegramMediaDownloader downloader,
        IAnimatedStickerRenderer renderer,
        ILogger<DefaultMediaPreparer> logger)
    {
        ArgumentNullException.ThrowIfNull(downloader);
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(logger);
        _downloader = downloader;
        _renderer = renderer;
        _logger = logger;
    }

    public async Task<Result<PreparedMedia>> PrepareAsync(DbChatMessageMedia media, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(media);

        var downloadFileId = media.DownloadFileId;
        if (!string.IsNullOrEmpty(downloadFileId))
        {
            var prepared = await PrepareFromFileAsync(media, downloadFileId, cancellationToken);
            if (!prepared.IsFailed)
            {
                return prepared;
            }
        }

        var thumbnailFileId = media.ThumbnailFileId;
        if (string.IsNullOrEmpty(thumbnailFileId))
        {
            return Result<PreparedMedia>.Fail();
        }

        var thumbnail = await _downloader.DownloadAsync(thumbnailFileId, cancellationToken);
        if (thumbnail.IsFailed || !MediaFormatDetector.IsImage(thumbnail.Value.Format))
        {
            return Result<PreparedMedia>.Fail();
        }

        Log.ThumbnailUsed(_logger, media.FileUniqueId, media.Kind);
        return Result<PreparedMedia>.Success(PreparedMedia.ThumbnailImage(
            thumbnail.Value.Content,
            thumbnail.Value.MediaType));
    }

    private async Task<Result<PreparedMedia>> PrepareFromFileAsync(
        DbChatMessageMedia media,
        string downloadFileId,
        CancellationToken cancellationToken)
    {
        var downloaded = await _downloader.DownloadAsync(downloadFileId, cancellationToken);
        if (downloaded.IsFailed)
        {
            return Result<PreparedMedia>.Fail();
        }

        var content = downloaded.Value.Content;
        var format = downloaded.Value.Format;
        if (format is MediaFormat.LottieSticker)
        {
            var rendered = _renderer.Render(content);
            if (rendered.IsFailed)
            {
                return Result<PreparedMedia>.Fail();
            }

            return Result<PreparedMedia>.Success(PreparedMedia.RenderedFrames(rendered.Value));
        }

        // Настоящий GIF среди движущихся вложений тоже отдаём как видео: media type в data-url
        // vLLM всё равно не смотрит, а OpenCV разложит гифку на кадры не хуже MP4.
        // Как картинку такое отправлять жалко: модель увидела бы только первый кадр.
        if (MediaFormatDetector.IsVideo(format) || (format is MediaFormat.Gif && media.IsAnimated))
        {
            return Result<PreparedMedia>.Success(PreparedMedia.VideoFile(content, downloaded.Value.MediaType));
        }

        if (MediaFormatDetector.IsImage(format))
        {
            return Result<PreparedMedia>.Success(PreparedMedia.Image(content, downloaded.Value.MediaType));
        }

        Log.FormatNotShowable(_logger, media.FileUniqueId, format);
        return Result<PreparedMedia>.Fail();
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "Falling back to the static thumbnail of {Kind} {FileUniqueId}")]
        public static partial void ThumbnailUsed(ILogger logger, string fileUniqueId, DbMediaKind kind);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Nothing to show the model for file {FileUniqueId} of {Format}")]
        public static partial void FormatNotShowable(ILogger logger, string fileUniqueId, MediaFormat format);
    }
}
