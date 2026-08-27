using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace TgLlmBot.Services.Media;

public partial class DefaultMediaGroupTracker : IMediaGroupTracker
{
    /// <summary>
    ///     Сколько тишины считать признаком того, что альбом приехал целиком.
    /// </summary>
    /// <remarks>
    ///     Bot API про альбом ничего не рассказывает: бот получает N независимых сообщений с общим
    ///     <c>media_group_id</c>, и ни размера группы, ни признака последней части в апдейте нет.
    ///     Единственный доступный признак "приехало всё" - тишина после последней части, поэтому
    ///     задержка платится с каждого адресованного боту альбома. Части обычно приезжают одним
    ///     батчем getUpdates с разницей в десятки миллисекунд, так что запаса тут нужно ровно
    ///     на один лишний round-trip до Telegram, если группу всё же разрезало между батчами.
    /// </remarks>
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(800);

    /// <summary>
    ///     Потолок ожидания: если части всё сыпятся и сыпятся, отвечать всё равно когда-то надо.
    /// </summary>
    /// <remarks>
    ///     Страховка от патологии, а не рабочий путь: собравшийся альбом выходит по
    ///     <see cref="MaxAlbumParts" /> или по тишине заметно раньше.
    /// </remarks>
    private static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(15);

    /// <summary>
    ///     Записи, до которых никто так и не пришёл, чистятся по этому возрасту.
    /// </summary>
    private static readonly TimeSpan StaleEntryAge = TimeSpan.FromMinutes(5);

    /// <summary>
    ///     Сколько вложений Telegram кладёт в одну медиа-группу максимум.
    /// </summary>
    /// <remarks>
    ///     Bot API документирует у <c>sendMediaGroup</c> предел "must include 2-10 items", столько же
    ///     разрешают собрать клиенты: одиннадцатая картинка уезжает отдельной группой со своим
    ///     <c>media_group_id</c>. Значит на десятой части ждать больше нечего - это единственная
    ///     точка, где про альбом можно узнать хоть что-то определённое, не гадая по таймеру.
    ///     Если предел когда-нибудь поднимут, лишние части подберут свои задания истории.
    /// </remarks>
    private const int MaxAlbumParts = 10;

    private const int PruneThreshold = 256;

    private readonly ConcurrentDictionary<(long ChatId, string MediaGroupId), MediaGroupState> _mediaGroups = new();

    /// <summary>
    ///     Альбомы, по которым запрос к LLM уже запускали.
    /// </summary>
    private readonly ConcurrentDictionary<(long ChatId, string MediaGroupId), DateTimeOffset> _llmRequestStarted = new();

    private readonly ILogger<DefaultMediaGroupTracker> _logger;
    private readonly TimeProvider _timeProvider;

    public DefaultMediaGroupTracker(
        TimeProvider timeProvider,
        ILogger<DefaultMediaGroupTracker> logger)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public void Register(long chatId, string mediaGroupId)
    {
        if (string.IsNullOrEmpty(mediaGroupId))
        {
            return;
        }

        var state = _mediaGroups.GetOrAdd((chatId, mediaGroupId), static _ => new MediaGroupState());
        state.RegisterPart(_timeProvider.GetUtcNow());
        PruneIfNeeded();
    }

    public async Task WaitForSettleAsync(long chatId, string mediaGroupId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrEmpty(mediaGroupId))
        {
            return;
        }

        var key = (chatId, mediaGroupId);
        var giveUpAt = _timeProvider.GetUtcNow() + MaxWait;
        while (_mediaGroups.TryGetValue(key, out var state))
        {
            // Группа набралась целиком - досиживать тишину незачем
            if (state.IsFull.IsCompleted)
            {
                Log.SettledByPartsLimit(_logger, chatId, mediaGroupId, MaxAlbumParts);
                break;
            }

            var idle = _timeProvider.GetUtcNow() - state.LastArrival;
            if (idle >= SettleDelay)
            {
                break;
            }

            if (_timeProvider.GetUtcNow() >= giveUpAt)
            {
                Log.SettleTimedOut(_logger, chatId, mediaGroupId, MaxWait.TotalSeconds);
                break;
            }

            // Просыпаемся не только по таймеру тишины, но и сразу, как только приехала последняя
            // возможная часть: иначе проверка на MaxAlbumParts дожидалась бы конца дебаунса
            // и не экономила ничего
            var settled = Task.Delay(SettleDelay - idle, _timeProvider, cancellationToken);
            await Task.WhenAny(settled, state.IsFull).ConfigureAwait(false);

            // Task.WhenAny отмену глотает, а наверх её отдать надо: на остановке приложения
            // задание должно свернуться, а не крутиться на мгновенно готовой задержке
            cancellationToken.ThrowIfCancellationRequested();
        }

        _mediaGroups.TryRemove(key, out _);
    }

    public bool TryBeginLlmRequest(long chatId, string mediaGroupId)
    {
        if (string.IsNullOrEmpty(mediaGroupId))
        {
            return true;
        }

        return _llmRequestStarted.TryAdd((chatId, mediaGroupId), _timeProvider.GetUtcNow());
    }

    private void PruneIfNeeded()
    {
        var staleBefore = _timeProvider.GetUtcNow() - StaleEntryAge;
        Prune(_mediaGroups, static x => x.LastArrival, staleBefore);
        // Отметки о запущенных запросах живут дольше самих альбомов: часть альбома может доехать
        // с заметным опозданием, и до этого момента её нельзя считать поводом ответить ещё раз
        Prune(_llmRequestStarted, static x => x, staleBefore);
    }

    private static void Prune<TValue>(
        ConcurrentDictionary<(long ChatId, string MediaGroupId), TValue> entries,
        Func<TValue, DateTimeOffset> timestampSelector,
        DateTimeOffset staleBefore)
    {
        if (entries.Count <= PruneThreshold)
        {
            return;
        }

        foreach (var (key, value) in entries.ToArray())
        {
            if (timestampSelector(value) < staleBefore)
            {
                entries.TryRemove(key, out _);
            }
        }
    }

    /// <summary>
    ///     Что известно про одну медиа-группу: когда приехала последняя часть и набралась ли группа
    ///     целиком.
    /// </summary>
    private sealed class MediaGroupState
    {
        private readonly TaskCompletionSource _isFull = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private long _lastArrivalUtcTicks;
        private int _partsCount;

        /// <summary>
        ///     Момент прихода последней известной части.
        /// </summary>
        public DateTimeOffset LastArrival => new(Interlocked.Read(ref _lastArrivalUtcTicks), TimeSpan.Zero);

        /// <summary>
        ///     Завершается, когда в группе набралось <see cref="MaxAlbumParts" /> частей - больше
        ///     Telegram в одну группу не положит, значит ждать нечего.
        /// </summary>
        public Task IsFull => _isFull.Task;

        public void RegisterPart(DateTimeOffset arrivedAt)
        {
            // Тики, а не DateTimeOffset: запись идёт из потока, разгребающего апдейты, а чтение -
            // из воркера чата, и рвать 12-байтовое значение между ними не хочется
            Interlocked.Exchange(ref _lastArrivalUtcTicks, arrivedAt.UtcTicks);
            if (Interlocked.Increment(ref _partsCount) >= MaxAlbumParts)
            {
                _isFull.TrySetResult();
            }
        }
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Warning, Message = "Media group {MediaGroupId} of chat {ChatId} kept growing for {MaxWaitSeconds} seconds, proceeding with what already arrived")]
        public static partial void SettleTimedOut(ILogger logger, long chatId, string mediaGroupId, double maxWaitSeconds);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Media group {MediaGroupId} of chat {ChatId} reached the limit of {MaxAlbumParts} parts, proceeding without waiting for the settle delay")]
        public static partial void SettledByPartsLimit(ILogger logger, long chatId, string mediaGroupId, int maxAlbumParts);
    }
}
