using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;
using TgLlmBot.Services.Telegram.TypingStatus;

namespace TgLlmBot.BackgroundServices;

/// <summary>
///     Держит статус "печатает" в чатах, пока его просят держать.
/// </summary>
/// <remarks>
///     У каждого чата свой воркер и своя очередь команд. Внутри чата команды разбираются строго
///     по порядку: включение и выключение приходят парами, и стоило им разъехаться, как стоп
///     начинал обгонять свой старт - чат оставался печатающим навсегда. Между чатами общего нет
///     ничего: ни очереди, ни состояния, ни ожиданий, поэтому долгая отмена в одном чате
///     не задерживает остальные.
/// </remarks>
public partial class TypingStatusBackgroundService : BackgroundService
{
    private const int TypingIntervalMs = 4_000;

    private readonly TelegramBotClient _bot;
    private readonly ILogger<TypingStatusBackgroundService> _logger;
    private readonly ITypingStatusQueues _queues;

    public TypingStatusBackgroundService(
        ITypingStatusQueues queues,
        TelegramBotClient bot,
        ILogger<TypingStatusBackgroundService> logger)
    {
        ArgumentNullException.ThrowIfNull(queues);
        ArgumentNullException.ThrowIfNull(bot);
        ArgumentNullException.ThrowIfNull(logger);
        _queues = queues;
        _bot = bot;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var readers = _queues.Readers;
        LogTypingStatusWorkerStarted(readers.Count);
        try
        {
            var workers = new List<Task>(readers.Count);
            foreach (var (chatId, reader) in readers)
            {
                // stoppingToken намеренно не передаётся в Task.Run - воркер сам обрабатывает отмену внутри
                workers.Add(Task.Run(() => ProcessChatQueueAsync(chatId, reader, stoppingToken), CancellationToken.None));
            }

            await Task.WhenAll(workers);
        }
        finally
        {
            LogTypingStatusWorkerStopped();
        }
    }

    /// <remarks>
    ///     Состояние печати чата живёт здесь, в локальных переменных воркера, и больше нигде:
    ///     трогать его может только этот цикл, поэтому ни словаря, ни блокировок не нужно.
    ///     Печать держится, пока есть хотя бы одна незакрытая просьба: в одном чате они приходят
    ///     от нескольких источников одновременно, и выключение одной не должно гасить остальные.
    /// </remarks>
    [SuppressMessage("Reliability", "CA2025:Ensure tasks using 'IDisposable' instances complete before the instances are disposed")]
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope")]
    private async Task ProcessChatQueueAsync(
        long chatId,
        ChannelReader<TypingCommand> reader,
        CancellationToken stoppingToken)
    {
        CancellationTokenSource? cts = null;
        Task? typing = null;
        var requests = new HashSet<Guid>();
        try
        {
            await foreach (var command in reader.ReadAllAsync(stoppingToken))
            {
                if (command.IsTyping)
                {
                    requests.Add(command.RequestId);
                }
                else
                {
                    // Повторный стоп той же просьбы - штатный случай, множество его просто не заметит
                    requests.Remove(command.RequestId);
                }

                if (requests.Count > 0)
                {
                    if (typing is not null)
                    {
                        // Уже печатаем - второй таймер ни к чему
                        continue;
                    }

                    cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    typing = RunTypingAsync(chatId, cts.Token);
                }
                else if (typing is not null)
                {
                    await StopTypingAsync(cts!, typing);
                    cts = null;
                    typing = null;
                    LogRemovedTypingState(chatId);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // graceful shutdown
        }
        finally
        {
            if (typing is not null)
            {
                await StopTypingAsync(cts!, typing);
            }
        }
    }

    /// <summary>
    ///     Гасит цикл печати и дожидается его выхода, прежде чем освободить его токен.
    /// </summary>
    /// <remarks>
    ///     Ожидание здесь принципиально: освободить CTS раньше, чем цикл его отпустил, - значит
    ///     получить <see cref="ObjectDisposedException" /> из его же await. Ждём только свой чат.
    /// </remarks>
    private static async Task StopTypingAsync(CancellationTokenSource cts, Task typing)
    {
        await cts.CancelAsync();
        // RunTypingAsync не выпускает исключений наружу, так что ожидание безопасно
        await typing;
        cts.Dispose();
    }

    /// <remarks>
    ///     Цикл живёт до отмены. Неудачная отправка - таймаут, сетевая ошибка, лимит Telegram -
    ///     только логируется: раньше она выкидывала из цикла, и до следующей просьбы чат
    ///     оставался без индикатора, хотя ответ ещё генерировался.
    /// </remarks>
    private async Task RunTypingAsync(long chatId, CancellationToken ct)
    {
        LogTypingActionStarted(chatId);

        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(TypingIntervalMs));

            // отправляем тайпинг статус сразу
            await SendTypingRequest(chatId, ct);

            while (await timer.WaitForNextTickAsync(ct))
            {
                // каждые 4 секунды продлеваем тайпинг статус
                await SendTypingRequest(chatId, ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types")]
    private async Task SendTypingRequest(long chatId, CancellationToken ct)
    {
        try
        {
            await _bot.SendChatAction(chatId, ChatAction.Typing, cancellationToken: ct);
            LogTypingActionSent(chatId);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogFailedToSendChatActionToChat(chatId, ex);
        }
    }

    [LoggerMessage(LogLevel.Information, "Typing Service Started with {ChatsCount} per-chat queues")]
    partial void LogTypingStatusWorkerStarted(int chatsCount);

    [LoggerMessage(LogLevel.Information, "Typing Service Stopped")]
    partial void LogTypingStatusWorkerStopped();

    [LoggerMessage(LogLevel.Debug, "Started typing loop for chat {chatId}")]
    partial void LogTypingActionStarted(long chatId);

    [LoggerMessage(LogLevel.Debug, "Stopped typing loop for chat {chatId}")]
    partial void LogRemovedTypingState(long chatId);

    [LoggerMessage(LogLevel.Trace, "Sent typing action to {chatId}")]
    partial void LogTypingActionSent(long chatId);

    [LoggerMessage(LogLevel.Error, "Error sending typing action to {chatId}")]
    partial void LogFailedToSendChatActionToChat(long chatId, Exception ex);
}
