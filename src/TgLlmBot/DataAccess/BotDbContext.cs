using System;
using Microsoft.EntityFrameworkCore;
using TgLlmBot.DataAccess.EntityTypeConfigurations;
using TgLlmBot.DataAccess.Models;

namespace TgLlmBot.DataAccess;

public class BotDbContext : DbContext
{
    public BotDbContext(DbContextOptions<BotDbContext> options)
        : base(options)
    {
    }

    public DbSet<DbChatMessage> ChatHistory { get; set; }

    public DbSet<KickedUser> KickedUsers { get; set; }

    public DbSet<DbUsageByUserCount> UsageByUserCounts { get; set; }


    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.ApplyConfiguration(new DbChatMessageEntityTypeConfiguration());
        modelBuilder.ApplyConfiguration(new KickedUserEntityTypeConfiguration());
        modelBuilder.ApplyConfiguration(new DbUsageByUserCountTypeConfiguration());
        base.OnModelCreating(modelBuilder);
    }
}
