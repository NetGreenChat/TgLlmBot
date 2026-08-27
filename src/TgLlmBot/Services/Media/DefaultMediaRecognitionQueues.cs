using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace TgLlmBot.Services.Media;

public partial class DefaultMediaRecognitionQueues : IMediaRecognitionQueues
{
    private readonly ILogger<DefaultMediaRecognitionQueues> _logger;
    private readonly FrozenDictionary<long, ChatQueues> _queues;

    public DefaultMediaRecognitionQueues(
        DefaultMediaRecognitionQueuesOptions options,
        ILogger<DefaultMediaRecognitionQueues> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        var queues = new Dictionary<long, ChatQueues>(options.ChatIds.Count);
        var readers = new Dictionary<long, ChannelReader<MediaRecognitionJob>>(options.ChatIds.Count);
        foreach (var chatId in options.ChatIds)
        {
            var chatQueues = CreateChatQueues(chatId, options.CapacityPerChat, logger);
            queues.Add(chatId, chatQueues);
            readers.Add(chatId, chatQueues.Reader);
        }

        _queues = queues.ToFrozenDictionary();
        Readers = readers.ToFrozenDictionary();
    }

    public IReadOnlyDictionary<long, ChannelReader<MediaRecognitionJob>> Readers { get; }

    public async Task<bool> EnqueueAsync(long chatId, MediaRecognitionJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (!_queues.TryGetValue(chatId, out var queues))
        {
            return false;
        }

        if (job.RequiresResponse)
        {
            // Фронт блокирует постановку: сообщение, требующее ответа, дропать нельзя.
            // Ожидание отменяется токеном остановки приложения; отмена пробрасывается наружу.
            try
            {
                if (!queues.Front.Writer.TryWrite(job))
                {
                    Log.FrontFull(_logger, chatId, job.MessageId);
                    await queues.Front.Writer.WriteAsync(job, cancellationToken).ConfigureAwait(false);
                }

                Log.FrontJobEnqueued(_logger, chatId, job.MessageId);
                return true;
            }
            catch (ChannelClosedException)
            {
                return false;
            }
        }

        if (queues.Back.Writer.TryWrite(job))
        {
            Log.BackJobEnqueued(_logger, chatId, job.MessageId);
            return true;
        }

        // Спина переполнена: задание уже отброшено каналом (с логированием в колбэке дропа),
        // в базе вложение останется Pending и его подберёт подчистка
        return false;
    }

    public void Complete()
    {
        foreach (var queues in _queues.Values)
        {
            queues.Front.Writer.TryComplete();
            queues.Back.Writer.TryComplete();
        }
    }

    private static ChatQueues CreateChatQueues(long chatId, int capacity, ILogger<DefaultMediaRecognitionQueues> logger)
    {
        // Фронт: bounded-канал в режиме ожидания - постановка блокируется, пока нет места
        var frontOptions = new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        };
        var front = Channel.CreateBounded<MediaRecognitionJob>(frontOptions);

        // Спина: bounded-канал с дропом - историю при переполнении отбрасываем,
        // недоделанное подберёт подчистка
        var backOptions = new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        };
        var back = Channel.CreateBounded<MediaRecognitionJob>(
            backOptions,
            dropped => Log.BackJobDropped(logger, chatId, dropped.MessageId, capacity));

        return new ChatQueues(front, back);
    }

    private sealed class ChatQueues
    {
        public ChatQueues(Channel<MediaRecognitionJob> front, Channel<MediaRecognitionJob> back)
        {
            Front = front;
            Back = back;
            Reader = new PriorityChannelReader(front.Reader, back.Reader);
        }

        public Channel<MediaRecognitionJob> Front { get; }

        public Channel<MediaRecognitionJob> Back { get; }

        public ChannelReader<MediaRecognitionJob> Reader { get; }
    }

    /// <summary>
    ///     Читатель логической очереди чата: задания фронта строго раньше заданий спины.
    /// </summary>
    /// <remarks>
    ///     Ждать приходится сразу два канала, а <see cref="ChannelReader{T}.WaitToReadAsync" />
    ///     на каждый вызов заводит в канале отдельного ожидающего с регистрацией отмены. Ожидающий
    ///     проигравшего канала снимается только записью в этот канал, поэтому ожидания переживают
    ///     итерации цикла и вызовы метода: иначе в чате, куда годами идёт одна история, ожидающие
    ///     фронта копились бы на каждом задании и не отпускались до остановки приложения.
    ///     Читатель рассчитан на одного потребителя (SingleReader), поэтому состояние - поля.
    /// </remarks>
    private sealed class PriorityChannelReader : ChannelReader<MediaRecognitionJob>
    {
        private readonly ChannelReader<MediaRecognitionJob> _back;
        private readonly ChannelReader<MediaRecognitionJob> _front;

        private Task<bool>? _backWait;
        private Task<bool>? _frontWait;

        /// <summary>
        ///     Канал завершён и вычитан до конца - ждать по нему больше нечего.
        /// </summary>
        private bool _backClosed;

        private bool _frontClosed;

        public PriorityChannelReader(
            ChannelReader<MediaRecognitionJob> front,
            ChannelReader<MediaRecognitionJob> back)
        {
            _front = front;
            _back = back;
        }

        public override Task Completion => Task.WhenAll(_front.Completion, _back.Completion);

        public override bool TryRead(out MediaRecognitionJob item)
        {
            if (_front.TryRead(out item!))
            {
                return true;
            }

            return _back.TryRead(out item!);
        }

        public override async ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default)
        {
            while (true)
            {
                if (_front.TryPeek(out _) || _back.TryPeek(out _))
                {
                    return true;
                }

                if (_frontClosed && _backClosed)
                {
                    return false;
                }

                // Один из каналов завершился - ждём только оставшийся, иначе крутились бы впустую
                if (_frontClosed)
                {
                    if (await TakeAsync(false, cancellationToken).ConfigureAwait(false))
                    {
                        return true;
                    }

                    continue;
                }

                if (_backClosed)
                {
                    if (await TakeAsync(true, cancellationToken).ConfigureAwait(false))
                    {
                        return true;
                    }

                    continue;
                }

                var frontWait = _frontWait ??= _front.WaitToReadAsync(cancellationToken).AsTask();
                var backWait = _backWait ??= _back.WaitToReadAsync(cancellationToken).AsTask();
                if (!frontWait.IsCompleted && !backWait.IsCompleted)
                {
                    await Task.WhenAny(frontWait, backWait).ConfigureAwait(false);
                }

                // Ожидание проигравшего канала остаётся в поле и будет переиспользовано
                var ready = false;
                if (frontWait.IsCompleted && await TakeAsync(true, cancellationToken).ConfigureAwait(false))
                {
                    ready = true;
                }

                if (backWait.IsCompleted && await TakeAsync(false, cancellationToken).ConfigureAwait(false))
                {
                    ready = true;
                }

                if (ready)
                {
                    return true;
                }

                // Завершившийся канал отдал false - перепроверяем состояния и ждём оставшийся
            }
        }

        /// <summary>
        ///     Забирает результат ожидания канала и освобождает поле под следующее ожидание.
        /// </summary>
        /// <returns>
        ///     <see langword="false" />, если канал завершён и вычитан - тогда взводится флаг закрытия,
        ///     иначе следующий проход цикла завёл бы по завершённому каналу новое ожидание,
        ///     мгновенно отвечающее тем же false, и цикл стал бы холостым.
        /// </returns>
        private async ValueTask<bool> TakeAsync(bool isFront, CancellationToken cancellationToken)
        {
            var reader = isFront ? _front : _back;
            var wait = (isFront ? _frontWait : _backWait) ?? reader.WaitToReadAsync(cancellationToken).AsTask();
            if (isFront)
            {
                _frontWait = null;
            }
            else
            {
                _backWait = null;
            }

            if (await wait.ConfigureAwait(false))
            {
                return true;
            }

            if (isFront)
            {
                _frontClosed = true;
            }
            else
            {
                _backClosed = true;
            }

            return false;
        }
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Debug, Message = "Enqueued response-required attachments of message {MessageId} of chat {ChatId} to the front of the media recognition queue")]
        public static partial void FrontJobEnqueued(ILogger logger, long chatId, int messageId);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Front of the media recognition queue of chat {ChatId} is full, message {MessageId} is waiting for a free slot. Incoming messages are on hold meanwhile")]
        public static partial void FrontFull(ILogger logger, long chatId, int messageId);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Enqueued history attachments of message {MessageId} of chat {ChatId} to the back of the media recognition queue")]
        public static partial void BackJobEnqueued(ILogger logger, long chatId, int messageId);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Media recognition queue of chat {ChatId} is full ({Capacity}), history message {MessageId} dropped. It will be picked up by the sweep")]
        public static partial void BackJobDropped(ILogger logger, long chatId, int messageId, int capacity);
    }
}
