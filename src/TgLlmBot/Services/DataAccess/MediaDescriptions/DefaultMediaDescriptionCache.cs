using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TgLlmBot.DataAccess;
using TgLlmBot.DataAccess.Models;
using TgLlmBot.Models;

namespace TgLlmBot.Services.DataAccess.MediaDescriptions;

[SuppressMessage("Style", "IDE0063:Use simple \'using\' statement")]
[SuppressMessage("ReSharper", "ConvertToUsingDeclaration")]
public class DefaultMediaDescriptionCache : IMediaDescriptionCache
{
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly TimeProvider _timeProvider;

    public DefaultMediaDescriptionCache(
        IServiceScopeFactory serviceScopeFactory,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(serviceScopeFactory);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _serviceScopeFactory = serviceScopeFactory;
        _timeProvider = timeProvider;
    }

    public async Task<Result<string>> TryGetAsync(string fileUniqueId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrEmpty(fileUniqueId))
        {
            return Result<string>.Fail();
        }

        await using (var asyncScope = _serviceScopeFactory.CreateAsyncScope())
        {
            var dbContext = asyncScope.ServiceProvider.GetRequiredService<BotDbContext>();
            var cached = await dbContext.MediaDescriptions
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.FileUniqueId == fileUniqueId, cancellationToken);
            if (cached is not null && !string.IsNullOrWhiteSpace(cached.Description))
            {
                return Result<string>.Success(cached.Description);
            }
        }

        return Result<string>.Fail();
    }

    public async Task StoreAsync(string fileUniqueId, string description, CancellationToken cancellationToken)
    {
        const string sql = $"""
                            INSERT INTO "{nameof(BotDbContext.MediaDescriptions)}" ("{nameof(DbMediaDescription.FileUniqueId)}", "{nameof(DbMediaDescription.Description)}", "{nameof(DbMediaDescription.CreatedAt)}")
                            VALUES (@{nameof(DbMediaDescription.FileUniqueId)}, @{nameof(DbMediaDescription.Description)}, @{nameof(DbMediaDescription.CreatedAt)})
                            ON CONFLICT ("{nameof(DbMediaDescription.FileUniqueId)}") DO NOTHING;
                            """;
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrEmpty(fileUniqueId) || string.IsNullOrWhiteSpace(description))
        {
            return;
        }

        await using (var asyncScope = _serviceScopeFactory.CreateAsyncScope())
        {
            var dbContext = asyncScope.ServiceProvider.GetRequiredService<BotDbContext>();
            await dbContext.Database.ExecuteSqlRawAsync(
                sql,
                [
                    new NpgsqlParameter($"{nameof(DbMediaDescription.FileUniqueId)}", fileUniqueId),
                    new NpgsqlParameter($"{nameof(DbMediaDescription.Description)}", description),
                    new NpgsqlParameter($"{nameof(DbMediaDescription.CreatedAt)}", _timeProvider.GetUtcNow().UtcDateTime)
                ],
                cancellationToken);
        }
    }
}
