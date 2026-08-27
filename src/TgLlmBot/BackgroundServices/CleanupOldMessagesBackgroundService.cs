using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TgLlmBot.DataAccess;

namespace TgLlmBot.BackgroundServices;

public partial class CleanupOldMessagesBackgroundService : BackgroundService
{
    private static readonly TimeSpan MediaDescriptionRetention = TimeSpan.FromDays(180);

    private readonly ILogger<CleanupOldMessagesBackgroundService> _logger;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly TimeProvider _timeProvider;

    public CleanupOldMessagesBackgroundService(
        IServiceScopeFactory serviceScopeFactory,
        TimeProvider timeProvider,
        ILogger<CleanupOldMessagesBackgroundService> logger)
    {
        ArgumentNullException.ThrowIfNull(serviceScopeFactory);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _serviceScopeFactory = serviceScopeFactory;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types")]
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogJobStart();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                LogIterationStart();

                await using (var asyncScope = _serviceScopeFactory.CreateAsyncScope())
                {
                    var dbContext = asyncScope.ServiceProvider.GetRequiredService<BotDbContext>();
                    await CleanupOldMessagesAsync(dbContext, stoppingToken);
                    await CleanupOldMediaDescriptionsAsync(dbContext, stoppingToken);
                }

                LogIterationComplete();
                await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                LogJobComplete();
                return;
            }
            catch (Exception ex)
            {
                LogIterationException(ex);
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
        }

        LogJobComplete();
    }

    private async Task CleanupOldMessagesAsync(BotDbContext dbContext, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var chatIds = await dbContext.ChatHistory
            .AsNoTracking()
            .Select(x => x.ChatId)
            .Distinct()
            .ToListAsync(cancellationToken);

        foreach (var chatId in chatIds)
        {
            var cutoffDate = await dbContext.ChatHistory
                .AsNoTracking()
                .Where(x => x.ChatId == chatId)
                .OrderByDescending(x => x.Date)
                .Select(x => x.Date)
                .Skip(200)
                .FirstOrDefaultAsync(cancellationToken);

            if (cutoffDate != default)
            {
                // Удаляем все сообщения старше этой даты для данного чата.
                // Массовое удаление идёт мимо сущностей, поэтому вложения подчищает
                // каскад внешнего ключа на стороне базы, а не EF
                var removedMessages = await dbContext.ChatHistory
                    .AsNoTracking()
                    .Where(x => x.ChatId == chatId && x.Date < cutoffDate)
                    .ExecuteDeleteAsync(cancellationToken);

                LogCleanupComplete(chatId, removedMessages);
            }
            else
            {
                LogCleanupComplete(chatId, 0);
            }
        }
    }

    /// <summary>
    ///     Чистит кэш описаний вложений от давно не встречавшихся файлов.
    /// </summary>
    /// <remarks>
    ///     Кэш ключуется идентификатором файла в Telegram и сам по себе не ограничен ничем.
    ///     Полгода - запас с большим избытком: мем или стикер, не появлявшийся столько времени,
    ///     дешевле описать заново, чем хранить вечно.
    /// </remarks>
    private async Task CleanupOldMediaDescriptionsAsync(BotDbContext dbContext, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var cutoffDate = _timeProvider.GetUtcNow().UtcDateTime - MediaDescriptionRetention;
        var removedDescriptions = await dbContext.MediaDescriptions
            .Where(x => x.CreatedAt < cutoffDate)
            .ExecuteDeleteAsync(cancellationToken);
        LogMediaDescriptionsCleanupComplete(removedDescriptions);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Starting cleanup job")]
    partial void LogJobStart();

    [LoggerMessage(Level = LogLevel.Information, Message = "Removed {RemovedCount} stale cached media descriptions")]
    partial void LogMediaDescriptionsCleanupComplete(int removedCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "Cleanup iteration started")]
    partial void LogIterationStart();

    [LoggerMessage(Level = LogLevel.Information, Message = "Cleanup iteration completed")]
    partial void LogIterationComplete();

    [LoggerMessage(Level = LogLevel.Error, Message = "Cleanup iteration failed with exception")]
    partial void LogIterationException(Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Completed cleanup for chat {ChatId}. Removed {RemovedCount} messages")]
    partial void LogCleanupComplete(long chatId, int removedCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "Completed cleanup job")]
    partial void LogJobComplete();
}
