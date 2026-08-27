using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TgLlmBot.Commands.ChatWithLlm.Queues;
using TgLlmBot.DataAccess.Models;
using TgLlmBot.Services.DataAccess.MediaDescriptions;
using TgLlmBot.Services.DataAccess.TelegramMessages;
using TgLlmBot.Services.Llm;
using TgLlmBot.Services.Llm.Compression;
using TgLlmBot.Services.Llm.Vision;
using TgLlmBot.Services.Media;
using TgLlmBot.Services.Telegram.TypingStatus;

namespace TgLlmBot.BackgroundServices;

/// <summary>
///     Разбирает per-chat очереди распознавания: скачивает вложения, прогоняет их через vision-модель,
///     ужимает описания основной моделью с оглядкой на историю чата и продолжает обработку сообщений,
///     требующих ответа, в LLM-очереди.
/// </summary>
/// <remarks>
///     Работает отдельно от очередей LLM-запросов, потому что описывать надо в том числе картинки
///     из сообщений, которые боту не адресованы: их задача - просто осесть в истории чата,
///     но осесть уже с описанием, иначе спросить про присланный полчаса назад мем будет не о чем.
///     Подробные описания живут только в памяти воркера: в базу уходит сжатое.
/// </remarks>
public partial class MediaRecognitionBackgroundService : BackgroundService
{
    private readonly IMediaDescriptionCache _descriptionCache;
    private readonly ITelegramMediaDownloader _downloader;
    private readonly IMediaGroupTracker _mediaGroupTracker;
    private readonly IImageRecognizer _imageRecognizer;
    private readonly ILogger<MediaRecognitionBackgroundService> _logger;
    private readonly ILlmRequestQueues _llmRequestQueues;
    private readonly MediaRecognitionBackgroundServiceOptions _options;
    private readonly IMediaRecognitionQueues _queues;
    private readonly IMediaDescriptionCompressor _compressor;
    private readonly ITelegramMessageStorage _storage;

    /// <summary>
    ///     Сообщения, задания по которым подчистка уже переставила в очередь и которые ещё не разобраны.
    /// </summary>
    private readonly ConcurrentDictionary<(long ChatId, int MessageId), byte> _sweptMessages = new();

    private readonly TimeProvider _timeProvider;
    private readonly ITypingStatusService _typingStatusService;

    public MediaRecognitionBackgroundService(
        MediaRecognitionBackgroundServiceOptions options,
        TimeProvider timeProvider,
        IMediaRecognitionQueues queues,
        ITelegramMediaDownloader downloader,
        IImageRecognizer imageRecognizer,
        IMediaDescriptionCompressor compressor,
        IMediaDescriptionCache descriptionCache,
        ITelegramMessageStorage storage,
        IMediaGroupTracker mediaGroupTracker,
        ITypingStatusService typingStatusService,
        ILlmRequestQueues llmRequestQueues,
        ILogger<MediaRecognitionBackgroundService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(queues);
        ArgumentNullException.ThrowIfNull(downloader);
        ArgumentNullException.ThrowIfNull(imageRecognizer);
        ArgumentNullException.ThrowIfNull(compressor);
        ArgumentNullException.ThrowIfNull(descriptionCache);
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(mediaGroupTracker);
        ArgumentNullException.ThrowIfNull(typingStatusService);
        ArgumentNullException.ThrowIfNull(llmRequestQueues);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _timeProvider = timeProvider;
        _queues = queues;
        _downloader = downloader;
        _imageRecognizer = imageRecognizer;
        _compressor = compressor;
        _descriptionCache = descriptionCache;
        _storage = storage;
        _mediaGroupTracker = mediaGroupTracker;
        _typingStatusService = typingStatusService;
        _llmRequestQueues = llmRequestQueues;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        var readers = _queues.Readers;
        Log.BackgroundServiceStarted(_logger, readers.Count);
        try
        {
            // Каждый чат обрабатывается своим воркером, поэтому запросы из разных чатов идут параллельно,
            // а внутри одного чата - строго последовательно.
            var workers = new List<Task>(readers.Count + 1);
            foreach (var (chatId, reader) in readers)
            {
                // stoppingToken намеренно не передаётся в Task.Run - воркер сам обрабатывает отмену внутри
                workers.Add(Task.Run(() => ProcessChatQueueAsync(chatId, reader, stoppingToken), CancellationToken.None));
            }

            workers.Add(Task.Run(() => SweepUnfinishedMediaAsync(stoppingToken), CancellationToken.None));
            await Task.WhenAll(workers);
        }
        finally
        {
            Log.BackgroundServiceCompleted(_logger);
        }
    }

    /// <summary>
    ///     Периодически возвращает в очередь вложения, застрявшие на полпути.
    /// </summary>
    /// <remarks>
    ///     Подбирает и не распознанное с прошлого запуска, и отброшенное переполнившейся спиной:
    ///     в базе такие вложения висят в состоянии <see cref="DbMediaRecognitionStatus.Pending" />.
    ///     Вложение остаётся <see cref="DbMediaRecognitionStatus.Pending" /> всё время, пока задание
    ///     стоит в очереди и обрабатывается, поэтому уже переставленное подчистка пропускает: иначе
    ///     на разгребании длинной очереди каждый проход набивал бы спину копиями того, что в ней и так
    ///     лежит, и свежие сообщения чата вытеснялись бы дропом.
    /// </remarks>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types")]
    private async Task SweepUnfinishedMediaAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var unfinishedMessages = await _storage.SelectMessagesWithUnfinishedMediaAsync(stoppingToken);
                var requeued = 0;
                foreach (var unfinishedMessage in unfinishedMessages)
                {
                    var media = unfinishedMessage.Media.ToArray();
                    if (media.Length is 0)
                    {
                        continue;
                    }

                    var key = (unfinishedMessage.ChatId, unfinishedMessage.MessageId);
                    if (!_sweptMessages.TryAdd(key, 0))
                    {
                        continue;
                    }

                    var job = MediaRecognitionJob.FromStoredMessage(unfinishedMessage);
                    if (await _queues.EnqueueAsync(unfinishedMessage.ChatId, job, stoppingToken))
                    {
                        requeued++;
                    }
                    else
                    {
                        // В очередь не попало - пусть попробует следующий проход
                        _sweptMessages.TryRemove(key, out _);
                    }
                }

                if (requeued > 0)
                {
                    Log.UnfinishedMediaRequeued(_logger, requeued);
                }

                await Task.Delay(_options.SweepInterval, _timeProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // graceful shutdown
                return;
            }
            catch (Exception ex)
            {
                // Не смогли подобрать хвост - не повод не обрабатывать новые сообщения
                Log.SweepFailed(_logger, ex);
                try
                {
                    await Task.Delay(_options.SweepInterval, _timeProvider, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
            }
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types")]
    [SuppressMessage("ReSharper", "RedundantWithCancellation")]
    private async Task ProcessChatQueueAsync(
        long chatId,
        ChannelReader<MediaRecognitionJob> reader,
        CancellationToken stoppingToken)
    {
        Log.ChatWorkerStarted(_logger, chatId);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await foreach (var job in reader.ReadAllAsync(stoppingToken).WithCancellation(stoppingToken))
                    {
                        await HandleJobAsync(job, stoppingToken);
                    }

                    // очередь завершена и вычитана до конца
                    break;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    // graceful shutdown
                    break;
                }
                catch (Exception ex)
                {
                    Log.UnknownException(_logger, chatId, ex);
                }
            }
        }
        finally
        {
            Log.ChatWorkerCompleted(_logger, chatId);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types")]
    private async Task HandleJobAsync(MediaRecognitionJob job, CancellationToken cancellationToken)
    {
        Log.HandlingJob(_logger, job.ChatId, job.MessageId, job.RequiresResponse);
        if (job.RequiresResponse)
        {
            // Индикатор печати на всё время распознавания: пользователь видит, что бот работает
            _typingStatusService.StartTyping(job.ChatId);
        }

        try
        {
            var mediaItems = await CollectMediaAsync(job, cancellationToken);
            var fullDescriptions = new Dictionary<Guid, string>();
            var changedMedia = new List<DbChatMessageMedia>();

            // Фаза 1: распознавание. Подробные описания - только в памяти воркера
            foreach (var (message, media) in mediaItems)
            {
                if (media.Status is not DbMediaRecognitionStatus.Pending)
                {
                    continue;
                }

                var description = await RecognizeAsync(media, job.RelatedText, cancellationToken);
                if (description is null)
                {
                    // Распознать не удалось: фиксируем состояние в базе, иначе подчистка
                    // будет возвращать вложение в очередь бесконечно
                    changedMedia.Add(media);
                    continue;
                }

                fullDescriptions[media.Id] = description;
            }

            // Фаза 2: компактинг. Каждое вложение ужимается с историей до своего сообщения
            foreach (var (message, media) in mediaItems)
            {
                if (!fullDescriptions.TryGetValue(media.Id, out var fullDescription))
                {
                    continue;
                }

                var history = await _storage.SelectContextMessagesBeforeAsync(
                    job.ChatId,
                    message.MessageId,
                    message.Date,
                    cancellationToken);
                var historyJson = ChatHistoryJsonBuilder.BuildJsonHistory(history);
                var request = new MediaCompressionRequest(
                    fullDescription,
                    media.Kind,
                    media.IsAnimated,
                    job.RelatedText,
                    historyJson);
                var compressed = await _compressor.CompressAsync(request, cancellationToken);

                // Не сжалось - в историю уйдёт обрезанное подробное описание: пусть обрубленное,
                // но лучше, чем потерять картинку из памяти совсем
                media.ShortDescription = Truncate(
                    compressed.IsFailed ? fullDescription : compressed.Value,
                    MediaDescriptionLimits.ShortMaxLength);
                media.Status = DbMediaRecognitionStatus.Ready;
                changedMedia.Add(media);
            }

            if (changedMedia.Count > 0 && !cancellationToken.IsCancellationRequested)
            {
                await _storage.UpdateMediaAsync(changedMedia.ToArray(), cancellationToken);
            }

            Log.HandledJob(_logger, job.ChatId, job.MessageId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // graceful shutdown: недоделанное подберёт следующий проход подчистки
        }
        catch (Exception ex)
        {
            Log.JobFailed(_logger, job.ChatId, job.MessageId, ex);
        }
        finally
        {
            // Задание разобрано - подчистка снова вправе переставить сообщение, если что-то
            // осталось Pending (например, упали посреди компактинга)
            _sweptMessages.TryRemove((job.ChatId, job.MessageId), out _);

            // Фаза 3: континуация ответа. Безусловна: даже если вложения распознать не удалось,
            // ответ пойдёт с fallback-описаниями, иначе сообщение останется без ответа
            if (job.RequiresResponse)
            {
                _typingStatusService.StopTyping(job.ChatId);
                if (!cancellationToken.IsCancellationRequested && job.Command is not null && !_llmRequestQueues.TryEnqueue(job.ChatId, job.Command))
                {
                    Log.ContinuationFailed(_logger, job.ChatId, job.MessageId);
                }
            }
        }
    }

    /// <summary>
    ///     Собирает вложения логического сообщения: для требующего ответа задания с альбомом - вложения
    ///     всех частей альбома в порядке отправки, иначе - вложения одного сообщения.
    /// </summary>
    /// <remarks>
    ///     Пачку картинок Telegram разбирает на отдельные сообщения, поэтому фронтовое задание сначала
    ///     ждёт, пока приедет весь альбом, а затем обрабатывает его целиком. Задания истории каждой
    ///     части обрабатывают только свою часть: если её уже разобрало фронтовое задание, вложения
    ///     будут в состоянии <see cref="DbMediaRecognitionStatus.Ready" /> и задание станет no-op.
    /// </remarks>
    private async Task<List<(DbChatMessage Message, DbChatMessageMedia Media)>> CollectMediaAsync(
        MediaRecognitionJob job,
        CancellationToken cancellationToken)
    {
        var chatId = job.ChatId;
        var mediaGroupId = job.StoredMessage.MediaGroupId;
        var isAlbum = !string.IsNullOrEmpty(mediaGroupId);

        DbChatMessage[] rows;
        if (isAlbum && job.RequiresResponse)
        {
            await _mediaGroupTracker.WaitForSettleAsync(chatId, mediaGroupId!, cancellationToken);
            rows = await _storage.SelectMediaGroupMessagesAsync(chatId, mediaGroupId!, cancellationToken);
        }
        else if (isAlbum)
        {
            rows = await _storage.SelectMediaGroupMessagesAsync(chatId, mediaGroupId!, cancellationToken);
        }
        else
        {
            var row = await _storage.SelectMessageAsync(chatId, job.MessageId, cancellationToken);
            rows = row is null ? [] : [row];
        }

        var result = new List<(DbChatMessage, DbChatMessageMedia)>();
        foreach (var row in rows)
        {
            foreach (var media in row.Media.OrderBy(x => x.Order))
            {
                result.Add((row, media));
            }
        }

        return result;
    }

    /// <summary>
    ///     Распознаёт одно вложение и возвращает подробное описание - только в память, не в базу.
    /// </summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types")]
    private async Task<string?> RecognizeAsync(DbChatMessageMedia media, string? relatedText, CancellationToken cancellationToken)
    {
        if (media.Status is not DbMediaRecognitionStatus.Pending)
        {
            return null;
        }

        if (string.IsNullOrEmpty(media.DownloadFileId))
        {
            // Показать модели нечего: например, анимированный стикер без превью
            media.Status = DbMediaRecognitionStatus.Unsupported;
            return null;
        }

        string? description = null;
        // Кэш описаний - только для стикеров: они прилетают в чат одни и те же по многу раз,
        // а фото дешевле распознать заново, чем хранить их описания вечно
        if (media.Kind is DbMediaKind.Sticker)
        {
            var cached = await _descriptionCache.TryGetAsync(media.FileUniqueId, cancellationToken);
            if (!cached.IsFailed)
            {
                Log.DescriptionServedFromCache(_logger, media.FileUniqueId);
                description = cached.Value;
            }
        }

        if (description is null)
        {
            var downloaded = await _downloader.DownloadAsync(media.DownloadFileId, cancellationToken);
            if (downloaded.IsFailed)
            {
                media.Status = DbMediaRecognitionStatus.Failed;
                return null;
            }

            var request = new ImageRecognitionRequest(
                downloaded.Value.Content,
                downloaded.Value.MediaType,
                media.Kind,
                media.IsAnimated,
                relatedText);
            var recognized = await _imageRecognizer.DescribeAsync(request, cancellationToken);
            if (recognized.IsFailed)
            {
                media.Status = DbMediaRecognitionStatus.Failed;
                return null;
            }

            description = Truncate(recognized.Value, MediaDescriptionLimits.FullMaxLength);
            if (media.Kind is DbMediaKind.Sticker)
            {
                await _descriptionCache.StoreAsync(media.FileUniqueId, description, cancellationToken);
            }
        }

        return description;
    }

    private static string Truncate(string description, int maxLength)
    {
        if (description.Length <= maxLength)
        {
            return description;
        }

        return string.Concat(description.AsSpan(0, maxLength), "...");
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = $"{nameof(MediaRecognitionBackgroundService)} started with {{ChatsCount}} per-chat queues")]
        public static partial void BackgroundServiceStarted(ILogger logger, int chatsCount);

        [LoggerMessage(Level = LogLevel.Information, Message = $"{nameof(MediaRecognitionBackgroundService)} completed")]
        public static partial void BackgroundServiceCompleted(ILogger logger);

        [LoggerMessage(Level = LogLevel.Information, Message = "Started media recognition worker for chat {ChatId}")]
        public static partial void ChatWorkerStarted(ILogger logger, long chatId);

        [LoggerMessage(Level = LogLevel.Information, Message = "Completed media recognition worker for chat {ChatId}")]
        public static partial void ChatWorkerCompleted(ILogger logger, long chatId);

        [LoggerMessage(Level = LogLevel.Information, Message = "Processing attachments of message {MessageId} in chat {ChatId}, requires response: {RequiresResponse}")]
        public static partial void HandlingJob(ILogger logger, long chatId, int messageId, bool requiresResponse);

        [LoggerMessage(Level = LogLevel.Information, Message = "Processed attachments of message {MessageId} in chat {ChatId}")]
        public static partial void HandledJob(ILogger logger, long chatId, int messageId);

        [LoggerMessage(Level = LogLevel.Error, Message = "Failed to process attachments of message {MessageId} in chat {ChatId}")]
        public static partial void JobFailed(ILogger logger, long chatId, int messageId, Exception exception);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Reused a cached description for file {FileUniqueId}")]
        public static partial void DescriptionServedFromCache(ILogger logger, string fileUniqueId);

        [LoggerMessage(Level = LogLevel.Information, Message = "Requeued {RequeuedCount} message(s) with attachments left unfinished")]
        public static partial void UnfinishedMediaRequeued(ILogger logger, int requeuedCount);

        [LoggerMessage(Level = LogLevel.Error, Message = "Failed to sweep messages with attachments left unfinished")]
        public static partial void SweepFailed(ILogger logger, Exception exception);

        [LoggerMessage(Level = LogLevel.Error, Message = "Unknown exception in media recognition worker of chat {ChatId}")]
        public static partial void UnknownException(ILogger logger, long chatId, Exception exception);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to continue message {MessageId} of chat {ChatId} into LLM request queue")]
        public static partial void ContinuationFailed(ILogger logger, long chatId, int messageId);
    }
}
