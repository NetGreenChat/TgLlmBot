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
using TgLlmBot.Configuration.TypedConfiguration.Llm;
using TgLlmBot.DataAccess.Models;
using TgLlmBot.Services.DataAccess.MediaDescriptions;
using TgLlmBot.Services.DataAccess.TelegramMessages;
using TgLlmBot.Services.Llm;
using TgLlmBot.Services.Llm.Descriptions;
using TgLlmBot.Services.Media;

namespace TgLlmBot.BackgroundServices;

/// <summary>
///     Разбирает per-chat очереди описаний: скачивает вложения и просит основную модель описать
///     их компактно - с оглядкой на историю чата. Готовые описания оседают в базе.
/// </summary>
/// <remarks>
///     Ответ бота от этого воркера не зависит: вложения сообщений, на которые бот отвечает,
///     уезжают в ответный запрос сами по себе. Здесь готовится память на будущее - описания
///     нужны затем, чтобы через час спросить про присланную картинку было о чём.
/// </remarks>
public partial class MediaRecognitionBackgroundService : BackgroundService
{
    private readonly IMediaDescriptionCache _descriptionCache;
    private readonly IMediaDescriber _describer;
    private readonly ILogger<MediaRecognitionBackgroundService> _logger;
    private readonly MediaRecognitionBackgroundServiceOptions _options;
    private readonly IMediaPreparer _preparer;
    private readonly IMediaRecognitionQueues _queues;
    private readonly ITelegramMessageStorage _storage;
    private readonly LlmCapabilitiesConfiguration _capabilities;

    /// <summary>
    ///     Сообщения, задания по которым подчистка уже переставила в очередь и которые ещё не разобраны.
    /// </summary>
    private readonly ConcurrentDictionary<(long ChatId, int MessageId), byte> _sweptMessages = new();

    private readonly TimeProvider _timeProvider;

    public MediaRecognitionBackgroundService(
        MediaRecognitionBackgroundServiceOptions options,
        TimeProvider timeProvider,
        IMediaRecognitionQueues queues,
        IMediaPreparer preparer,
        IMediaDescriber describer,
        IMediaDescriptionCache descriptionCache,
        ITelegramMessageStorage storage,
        LlmCapabilitiesConfiguration capabilities,
        ILogger<MediaRecognitionBackgroundService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(queues);
        ArgumentNullException.ThrowIfNull(preparer);
        ArgumentNullException.ThrowIfNull(describer);
        ArgumentNullException.ThrowIfNull(descriptionCache);
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _timeProvider = timeProvider;
        _queues = queues;
        _preparer = preparer;
        _describer = describer;
        _descriptionCache = descriptionCache;
        _storage = storage;
        _capabilities = capabilities;
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
            // Каждый чат обрабатывается своим воркером, поэтому задания из разных чатов идут параллельно,
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
    ///     Подбирает и не описанное с прошлого запуска, и отброшенное переполнившейся очередью:
    ///     в базе такие вложения висят в состоянии <see cref="DbMediaRecognitionStatus.Pending" />.
    ///     Вложение остаётся <see cref="DbMediaRecognitionStatus.Pending" /> всё время, пока задание
    ///     стоит в очереди и обрабатывается, поэтому уже переставленное подчистка пропускает: иначе
    ///     на разгребании длинной очереди каждый проход набивал бы её копиями того, что в ней и так
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
                    if (unfinishedMessage.Media.Count is 0)
                    {
                        continue;
                    }

                    var key = (unfinishedMessage.ChatId, unfinishedMessage.MessageId);
                    if (!_sweptMessages.TryAdd(key, 0))
                    {
                        continue;
                    }

                    var job = new MediaRecognitionJob(unfinishedMessage);
                    if (_queues.TryEnqueue(unfinishedMessage.ChatId, job))
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
        Log.HandlingJob(_logger, job.ChatId, job.MessageId);
        try
        {
            // Перечитываем строку из базы: статус вложений могли изменить соседние задания
            var row = await _storage.SelectMessageAsync(job.ChatId, job.MessageId, cancellationToken);
            if (row is null)
            {
                return;
            }

            var relatedText = (row.Caption ?? row.Text)?.Trim();
            var changedMedia = new List<DbChatMessageMedia>();

            // История одна на всё сообщение - грузится лениво, по первому вложению, которое
            // реально придётся описывать
            string? historyTranscript = null;
            var historyLoaded = false;

            foreach (var media in row.Media.OrderBy(static x => x.Order))
            {
                if (media.Status is not DbMediaRecognitionStatus.Pending)
                {
                    continue;
                }

                if (!historyLoaded)
                {
                    var history = await _storage.SelectContextMessagesBeforeAsync(
                        row.ChatId,
                        row.MessageId,
                        row.Date,
                        cancellationToken);
                    historyTranscript = ChatTranscriptBuilder.BuildTranscript(history, _options.BotName);
                    historyLoaded = true;
                }

                var description = await DescribeAsync(row, media, relatedText, historyTranscript, cancellationToken);
                if (description is not null)
                {
                    media.ShortDescription = Truncate(description, MediaDescriptionLimits.ShortMaxLength);
                    media.Status = DbMediaRecognitionStatus.Ready;
                }

                changedMedia.Add(media);
            }

            if (changedMedia.Count > 0 && !cancellationToken.IsCancellationRequested)
            {
                await _storage.UpdateMediaAsync([.. changedMedia], cancellationToken);
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
            // осталось Pending (например, упали посреди обработки)
            _sweptMessages.TryRemove((job.ChatId, job.MessageId), out _);
        }
    }

    /// <summary>
    ///     Описывает одно вложение и возвращает компактный текст либо <see langword="null" />,
    ///     если описать не удалось (состояние вложения меняет на себя).
    /// </summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types")]
    private async Task<string?> DescribeAsync(
        DbChatMessage row,
        DbChatMessageMedia media,
        string? relatedText,
        string? historyTranscript,
        CancellationToken cancellationToken)
    {
        if (!_capabilities.Supports(media.Kind, media.IsAnimated))
        {
            // Капабилити модели вложение не покрывают: ни качать, ни описывать его незачем -
            // в историю уходит пометка, что распознавание такого вложения не поддерживается
            media.ShortDescription = LlmCapabilitiesConfiguration.DescribeUnsupported(media.Kind, media.IsAnimated);
            media.Status = DbMediaRecognitionStatus.Unsupported;
            return null;
        }

        if (!media.HasShowableFile)
        {
            // Показать модели нечего: у вложения нет ни своего файла, ни превью
            media.Status = DbMediaRecognitionStatus.Unsupported;
            return null;
        }

        // Кэш описаний - только для стикеров: они прилетают в чат одни и те же по многу раз,
        // а картинки, гифки и видео дешевле описать заново, чем хранить их описания вечно
        CachedMediaDescription? cached = null;
        if (media.Kind is DbMediaKind.Sticker)
        {
            var lookup = await _descriptionCache.TryGetAsync(media.FileUniqueId, cancellationToken);
            cached = lookup.IsFailed ? null : lookup.Value;
        }

        // Описание, снятое с самого стикера, окончательно: лучше него уже не будет
        if (cached is { IsFallback: false })
        {
            Log.DescriptionServedFromCache(_logger, media.FileUniqueId);
            return cached.Description;
        }

        var prepared = await _preparer.PrepareAsync(media, cancellationToken);
        if (prepared.IsFailed)
        {
            if (cached is not null)
            {
                // Разглядеть заново не вышло: описание с превью хуже, чем хотелось бы, но лучше, чем ничего
                Log.DescriptionServedFromCache(_logger, media.FileUniqueId);
                return cached.Description;
            }

            media.Status = DbMediaRecognitionStatus.Failed;
            return null;
        }

        if (cached is not null && prepared.Value.IsThumbnailFallback)
        {
            // Превью, с которого описание уже снято, гонять через модель второй раз незачем
            Log.DescriptionServedFromCache(_logger, media.FileUniqueId);
            return cached.Description;
        }

        var request = new MediaDescriptionRequest(
            prepared.Value,
            media.Kind,
            media.IsAnimated,
            relatedText,
            historyTranscript);
        var described = await _describer.DescribeAsync(request, cancellationToken);
        if (described.IsFailed)
        {
            if (cached is not null)
            {
                Log.DescriptionServedFromCache(_logger, media.FileUniqueId);
                return cached.Description;
            }

            media.Status = DbMediaRecognitionStatus.Failed;
            return null;
        }

        if (media.Kind is DbMediaKind.Sticker)
        {
            await _descriptionCache.StoreAsync(
                media.FileUniqueId,
                described.Value,
                prepared.Value.IsThumbnailFallback,
                cancellationToken);
        }

        return described.Value;
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

        [LoggerMessage(Level = LogLevel.Information, Message = "Started media description worker for chat {ChatId}")]
        public static partial void ChatWorkerStarted(ILogger logger, long chatId);

        [LoggerMessage(Level = LogLevel.Information, Message = "Completed media description worker for chat {ChatId}")]
        public static partial void ChatWorkerCompleted(ILogger logger, long chatId);

        [LoggerMessage(Level = LogLevel.Information, Message = "Describing attachments of message {MessageId} in chat {ChatId}")]
        public static partial void HandlingJob(ILogger logger, long chatId, int messageId);

        [LoggerMessage(Level = LogLevel.Information, Message = "Described attachments of message {MessageId} in chat {ChatId}")]
        public static partial void HandledJob(ILogger logger, long chatId, int messageId);

        [LoggerMessage(Level = LogLevel.Error, Message = "Failed to describe attachments of message {MessageId} in chat {ChatId}")]
        public static partial void JobFailed(ILogger logger, long chatId, int messageId, Exception exception);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Reused a cached description for file {FileUniqueId}")]
        public static partial void DescriptionServedFromCache(ILogger logger, string fileUniqueId);

        [LoggerMessage(Level = LogLevel.Information, Message = "Requeued {RequeuedCount} message(s) with attachments left unfinished")]
        public static partial void UnfinishedMediaRequeued(ILogger logger, int requeuedCount);

        [LoggerMessage(Level = LogLevel.Error, Message = "Failed to sweep messages with attachments left unfinished")]
        public static partial void SweepFailed(ILogger logger, Exception exception);

        [LoggerMessage(Level = LogLevel.Error, Message = "Unknown exception in media description worker of chat {ChatId}")]
        public static partial void UnknownException(ILogger logger, long chatId, Exception exception);
    }
}
