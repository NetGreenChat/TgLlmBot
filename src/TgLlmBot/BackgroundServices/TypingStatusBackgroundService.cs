using System;
using System.Collections.Concurrent;
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
///     Команды разбираются одним циклом в порядке поступления. Это существенно: включение и
///     выключение приходят парами, и стоило им разъехаться по разным очередям, как стоп начинал
///     обгонять свой старт - чат оставался печатающим навсегда.
/// </remarks>
public partial class TypingStatusBackgroundService : BackgroundService
{
    private const int TypingIntervalMs = 4_000;

    private readonly ConcurrentDictionary<long, CancellationTokenSource> _activeTypingTimersCts = new();
    private readonly TelegramBotClient _bot;
    private readonly ILogger<TypingStatusBackgroundService> _logger;

    private readonly ChannelReader<TypingCommand> _typingChannelReader;

    public TypingStatusBackgroundService(
        ChannelReader<TypingCommand> typingChannelReader,
        TelegramBotClient bot,
        ILogger<TypingStatusBackgroundService> logger)
    {
        ArgumentNullException.ThrowIfNull(typingChannelReader);
        ArgumentNullException.ThrowIfNull(bot);
        ArgumentNullException.ThrowIfNull(logger);
        _typingChannelReader = typingChannelReader;
        _bot = bot;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogTypingStatusWorkerStarted();
        try
        {
            await foreach (var cmd in _typingChannelReader.ReadAllAsync(stoppingToken))
            {
                if (cmd.IsTyping)
                {
                    StartTyping(cmd.ChatId, stoppingToken);
                }
                else
                {
                    await StopTypingAsync(cmd.ChatId);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // graceful shutdown
        }

        LogTypingStatusWorkerStopped();
    }

    /// <remarks>
    ///     CTS переходит во владение запущенного цикла печати: он же его и освобождает, когда
    ///     выходит. Дождаться цикла здесь нельзя - он живёт ровно столько, сколько чат печатает.
    /// </remarks>
    [SuppressMessage("Reliability", "CA2025:Ensure tasks using 'IDisposable' instances complete before the instances are disposed")]
    private void StartTyping(long chatId, CancellationToken stoppingToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        if (!_activeTypingTimersCts.TryAdd(chatId, cts))
        {
            // В чате уже печатаем - второй таймер ни к чему
            cts.Dispose();
            return;
        }

        _ = RunTypingAsync(chatId, cts);
    }

    /// <remarks>
    ///     CTS здесь только отменяется: освобождает его цикл печати, которому отменённый токен
    ///     нужен ещё какое-то время, чтобы досмотреть отмену и выйти.
    /// </remarks>
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope")]
    private async Task StopTypingAsync(long chatId)
    {
        if (_activeTypingTimersCts.TryRemove(chatId, out var cts))
        {
            // Освобождает CTS сам цикл печати: пока он не вышел, отменённый токен ему ещё нужен
            await cts.CancelAsync();
            LogRemovedTypingState(chatId);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types")]
    private async Task RunTypingAsync(long chatId, CancellationTokenSource cts)
    {
        LogTypingActionStarted(chatId);

        var ct = cts.Token;
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
        catch (Exception ex)
        {
            LogFailedToSendChatActionToChat(chatId, ex);
        }
        finally
        {
            // Убираем именно свою запись: пока мы досматривали отмену, чат мог начать печатать
            // заново, и снести чужой, ещё живой таймер нельзя
            _activeTypingTimersCts.TryRemove(new KeyValuePair<long, CancellationTokenSource>(chatId, cts));
            cts.Dispose();
        }
    }

    private async Task SendTypingRequest(long chatId, CancellationToken ct)
    {
        await _bot.SendChatAction(chatId, ChatAction.Typing, cancellationToken: ct);
        LogTypingActionSent(chatId);
    }

    [LoggerMessage(LogLevel.Information, "Typing Service Started")]
    partial void LogTypingStatusWorkerStarted();

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
