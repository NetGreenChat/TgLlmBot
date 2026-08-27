using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using TgLlmBot.Models;

namespace TgLlmBot.Services.Media;

public partial class DefaultTelegramMediaDownloader : ITelegramMediaDownloader
{
    /// <summary>
    ///     Bot API отдаёт файлы не больше 20 МБ, но верить этому на слово, читая ответ в память, не стоит.
    ///     Всё, что крупнее, Telegram и так не отдаст - такое вложение уедет в модель превью.
    /// </summary>
    private const int MaxFileSizeBytes = 20 * 1024 * 1024;

    private readonly TelegramBotClient _bot;
    private readonly ILogger<DefaultTelegramMediaDownloader> _logger;

    public DefaultTelegramMediaDownloader(
        TelegramBotClient bot,
        ILogger<DefaultTelegramMediaDownloader> logger)
    {
        ArgumentNullException.ThrowIfNull(bot);
        ArgumentNullException.ThrowIfNull(logger);
        _bot = bot;
        _logger = logger;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types")]
    [SuppressMessage("ReSharper", "ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract")]
    public async Task<Result<DownloadedMedia>> DownloadAsync(string fileId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrEmpty(fileId))
        {
            return Result<DownloadedMedia>.Fail();
        }

        try
        {
            var file = await _bot.GetFile(fileId, cancellationToken);
            if (file is null || string.IsNullOrEmpty(file.FilePath))
            {
                Log.FileMetadataMissing(_logger, fileId);
                return Result<DownloadedMedia>.Fail();
            }

            // Размер проверяем по метаданным, до самой загрузки: тащить в память то,
            // что модели всё равно не отправишь, незачем
            if (file.FileSize > MaxFileSizeBytes)
            {
                Log.FileTooLarge(_logger, fileId, file.FileSize.Value, MaxFileSizeBytes);
                return Result<DownloadedMedia>.Fail();
            }

            byte[] content;
            using (var memoryStream = new MemoryStream())
            {
                await _bot.DownloadFile(file.FilePath, memoryStream, cancellationToken);
                content = memoryStream.ToArray();
            }

            var format = MediaFormatDetector.Detect(content);
            if (format is null)
            {
                Log.UnsupportedFormat(_logger, fileId, content.Length);
                return Result<DownloadedMedia>.Fail();
            }

            Log.FileDownloaded(_logger, fileId, content.Length, format.Value);
            return Result<DownloadedMedia>.Success(new(content, format.Value));
        }
        catch (Exception ex)
        {
            Log.DownloadFailed(_logger, fileId, ex);
            return Result<DownloadedMedia>.Fail();
        }
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Debug, Message = "Downloaded Telegram file {FileId}: {ContentLength} bytes of {Format}")]
        public static partial void FileDownloaded(ILogger logger, string fileId, int contentLength, MediaFormat format);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Telegram returned no downloadable path for file {FileId}")]
        public static partial void FileMetadataMissing(ILogger logger, string fileId);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Telegram file {FileId} of {FileSize} bytes exceeds the limit of {MaxFileSize} bytes")]
        public static partial void FileTooLarge(ILogger logger, string fileId, long fileSize, int maxFileSize);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Telegram file {FileId} of {ContentLength} bytes is of a format the vision model cannot open")]
        public static partial void UnsupportedFormat(ILogger logger, string fileId, int contentLength);

        [LoggerMessage(Level = LogLevel.Error, Message = "Failed to download Telegram file {FileId}")]
        public static partial void DownloadFailed(ILogger logger, string fileId, Exception exception);
    }
}
