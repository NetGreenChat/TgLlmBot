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

    public DbSet<DbKickedUser> KickedUsers { get; set; }

    public DbSet<DbChatSystemPrompt> ChatSystemPrompts { get; set; }
    public DbSet<DbPersonalChatSystemPrompt> PersonalChatSystemPrompts { get; set; }
    public DbSet<DbUserLimit> Limits { get; set; }
    public DbSet<DbChatUsage> Usage { get; set; }
    public DbSet<DbChatMessageMedia> ChatMessageMedia { get; set; }
    public DbSet<DbMediaDescription> MediaDescriptions { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.ApplyConfiguration(new DbChatMessageEntityTypeConfiguration());
        modelBuilder.ApplyConfiguration(new DbKickedUserEntityTypeConfiguration());
        modelBuilder.ApplyConfiguration(new DbChatSystemPromptEntityTypeConfiguration());
        modelBuilder.ApplyConfiguration(new DbPersonalChatSystemPromptEntityTypeConfiguration());
        modelBuilder.ApplyConfiguration(new DbUserLimitsEntityTypeConfiguration());
        modelBuilder.ApplyConfiguration(new DbChatUsageEntityTypeConfiguration());
        modelBuilder.ApplyConfiguration(new DbChatMessageMediaEntityTypeConfiguration());
        modelBuilder.ApplyConfiguration(new DbMediaDescriptionEntityTypeConfiguration());
        base.OnModelCreating(modelBuilder);
    }
}
