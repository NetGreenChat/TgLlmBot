using System;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Diagnostics.CodeAnalysis;
using System.Net.Http;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using ModelContextProtocol.Client;
using OpenAI;
using OpenAI.Chat;
using Telegram.Bot;
using TgLlmBot.BackgroundServices;
using TgLlmBot.CommandDispatcher;
using TgLlmBot.Commands.ChatWithLlm;
using TgLlmBot.Commands.ChatWithLlm.BackgroundServices.LlmRequests;
using TgLlmBot.Commands.ChatWithLlm.Services;
using TgLlmBot.Commands.DisplayHelp;
using TgLlmBot.Commands.Model;
using TgLlmBot.Commands.Ping;
using TgLlmBot.Commands.Rating;
using TgLlmBot.Commands.Repo;
using TgLlmBot.Commands.ResetChatSystemPrompt;
using TgLlmBot.Commands.ResetPersonalSystemPrompt;
using TgLlmBot.Commands.SetChatSystemPrompt;
using TgLlmBot.Commands.SetLimit;
using TgLlmBot.Commands.SetPersonalSystemPrompt;
using TgLlmBot.Commands.ShowChatSystemPrompt;
using TgLlmBot.Commands.ShowPersonalSystemPrompt;
using TgLlmBot.Commands.Usage;
using TgLlmBot.Configuration.Options;
using TgLlmBot.Configuration.TypedConfiguration;
using TgLlmBot.DataAccess;
using TgLlmBot.DataAccess.Design;
using TgLlmBot.Extensions.Configuration;
using TgLlmBot.Services.DataAccess.KickedUsers;
using TgLlmBot.Services.DataAccess.Limits;
using TgLlmBot.Services.DataAccess.SystemPrompts;
using TgLlmBot.Services.DataAccess.TelegramMessages;
using TgLlmBot.Services.Mcp.Clients.Github;
using TgLlmBot.Services.Mcp.Enums;
using TgLlmBot.Services.Mcp.Tools;
using TgLlmBot.Services.OpenAIClient.Costs;
using TgLlmBot.Services.OpenAIClient.HttpClient.DelegatingHandlers;
using TgLlmBot.Services.OpenRouter;
using TgLlmBot.Services.Telegram.Markdown;
using TgLlmBot.Services.Telegram.RequestHandler;
using TgLlmBot.Services.Telegram.SelfInformation;
using TgLlmBot.Services.Telegram.TypingStatus;

namespace TgLlmBot;

[SuppressMessage("Design", "CA1052:Static holder types should be Static or NotInheritable")]
public partial class Program
{
    private const string LlmHttpClient = "llm-http-client";

    [SuppressMessage("ReSharper", "ConvertToUsingDeclaration")]
    [SuppressMessage("Design", "CA1031:Do not catch general exception types")]
    public static async Task<int> Main(string[] args)
    {
        var exitCode = 0;
        try
        {
            var selfInfo = new DefaultTelegramSelfInformation();
            var builder = CreateHostApplicationBuilder(args, selfInfo);

            using (var host = builder.Build())
            {
                await ApplyMigrationsAsync(host);
                await InitializeMcpClientsAsync(host);
                var hostLoggerFactory = host.Services.GetRequiredService<ILoggerFactory>();
                var logger = hostLoggerFactory.CreateLogger<Program>();
                LogApplicationStarting(logger);
                var botClient = host.Services.GetRequiredService<TelegramBotClient>();
                var requestHandler = host.Services.GetRequiredService<ITelegramRequestHandler>();
                LogGettingSelfInformation(logger);
                var self = await botClient.GetMe(CancellationToken.None);
                selfInfo.SetSelf(self);
                LogGotSelfInformationSuccessful(logger);
                botClient.OnMessage += requestHandler.OnMessageAsync;
                botClient.OnError += requestHandler.OnErrorAsync;
                botClient.OnUpdate += requestHandler.OnUpdateAsync;
                await host.RunAsync(CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            LogHostCrash(ex);
            exitCode = 1;
        }

        return exitCode;
    }

    [SuppressMessage("ReSharper", "ConvertToUsingDeclaration")]
    [SuppressMessage("Style", "IDE0063:Use simple \'using\' statement")]
    private static async Task ApplyMigrationsAsync(IHost host)
    {
        var scopeFactory = host.Services.GetRequiredService<IServiceScopeFactory>();
        await using (var asyncScope = scopeFactory.CreateAsyncScope())
        {
            var dbContext = asyncScope.ServiceProvider.GetRequiredService<BotDbContext>();
            await dbContext.Database.MigrateAsync(CancellationToken.None);
        }
    }

    [SuppressMessage("ReSharper", "ConvertToUsingDeclaration")]
    [SuppressMessage("Style", "IDE0063:Use simple \'using\' statement")]
    [SuppressMessage("ReSharper", "ConditionalAccessQualifierIsNonNullableAccordingToAPIContract")]
    private static async Task InitializeMcpClientsAsync(IHost host)
    {
        var scopeFactory = host.Services.GetRequiredService<IServiceScopeFactory>();
        await using (var asyncScope = scopeFactory.CreateAsyncScope())
        {
            var toolsProvider = asyncScope.ServiceProvider.GetRequiredService<DefaultMcpToolsProvider>();

            var github = asyncScope.ServiceProvider.GetRequiredKeyedService<McpClient>(McpClientName.Github);

            var githubTools = await github.ListToolsAsync();

            toolsProvider.AddTools(githubTools);
        }
    }

    private static HostApplicationBuilder CreateHostApplicationBuilder(
        string[] args,
        DefaultTelegramSelfInformation selfInfo)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Logging.ClearProviders();
        builder.Logging.AddConfiguration(builder.Configuration.GetRequiredSection("Logging"));
        builder.Logging.AddSimpleConsole(options =>
        {
            options.ColorBehavior = LoggerColorBehavior.Enabled;
            options.UseUtcTimestamp = true;
            options.TimestampFormat = "[yyyy-MM-dd HH:mm:ss] ";
        });
        builder.Configuration.AddUserSecrets(typeof(Program).Assembly, true);

        var config = builder.Configuration
            .GetTypedConfigurationFromOptions<ApplicationOptions, ApplicationConfiguration>(static x =>
                ApplicationConfiguration.Convert(x));
        // Time provider
        builder.Services.AddSingleton<TimeProvider>(_ => TimeProvider.System);
        // Telegram client
        builder.Services.AddSingleton(new TelegramBotClient(config.Telegram.BotToken));
        // Telegram markdown
        builder.Services.AddSingleton<ITelegramMarkdownConverter, DefaultTelegramMarkdownConverter>();
        // Telegram bot self-info (to allow the bot to know about itself)
        builder.Services.AddSingleton<ITelegramSelfInformation>(selfInfo);
        // Request handling
        builder.Services.AddSingleton(resolver =>
        {
            var timeProvider = resolver.GetRequiredService<TimeProvider>();
            var currentTime = DateTimeOffset.FromUnixTimeSeconds(timeProvider.GetUtcNow().ToUnixTimeSeconds());
            return new DefaultTelegramRequestHandlerOptions(currentTime, config.Telegram.AllowedChatIds);
        });
        builder.Services.AddSingleton<ITelegramRequestHandler, DefaultTelegramRequestHandler>();
        // Command dispatch
        builder.Services.AddSingleton(new DefaultTelegramCommandDispatcherOptions(config.Telegram.BotName));
        builder.Services.AddSingleton<ITelegramCommandDispatcher, DefaultTelegramCommandDispatcher>();
        // Command handlers
        builder.Services.AddSingleton(new DisplayHelpCommandHandlerOptions(config.Telegram.BotName));
        builder.Services.AddSingleton<DisplayHelpCommandHandler>();
        builder.Services.AddSingleton<ChatWithLlmCommandHandler>();
        builder.Services.AddSingleton(new ModelCommandHandlerOptions(config.Llm.Endpoint, config.Llm.Model));
        builder.Services.AddSingleton<ModelCommandHandler>();
        builder.Services.AddSingleton<PingCommandHandler>();
        builder.Services.AddSingleton<RepoCommandHandler>();
        builder.Services.AddSingleton<UsageCommandHandler>();
        builder.Services.AddSingleton(new RatingCommandHandlerOptions(config.Telegram.BotName));
        builder.Services.AddSingleton<RatingCommandHandler>();
        builder.Services.AddSingleton<ResetChatSystemPromptCommandHandler>();
        builder.Services.AddSingleton<SetChatSystemPromptCommandHandler>();
        builder.Services.AddSingleton<ResetPersonalSystemPromptCommandHandler>();
        builder.Services.AddSingleton<SetPersonalSystemPromptCommandHandler>();
        builder.Services.AddSingleton<ShowPersonalSystemPromptCommandHandler>();
        builder.Services.AddSingleton<ShowChatSystemPromptCommandHandler>();
        builder.Services.AddSingleton<SetLimitCommandHandler>();
        // Channel to communicate with LLM
        var llmRequestChannel = Channel.CreateBounded<ChatWithLlmCommand>(new BoundedChannelOptions(20)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = false,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
        builder.Services.AddSingleton<ChannelWriter<ChatWithLlmCommand>>(resolver =>
        {
            var hostLifetime = resolver.GetRequiredService<IHostApplicationLifetime>();
            hostLifetime.ApplicationStopping.Register(() => llmRequestChannel.Writer.Complete());
            return llmRequestChannel.Writer;
        });
        builder.Services.AddSingleton(llmRequestChannel.Reader);
        // Background services
        builder.Services.AddHostedService<LlmRequestsBackgroundService>();
        builder.Services.AddHostedService<CleanupOldMessagesBackgroundService>();
        builder.Services.AddHostedService<TypingStatusBackgroundService>();

        // LLM
        builder.Services.AddSingleton<ICostContextStorage, DefaultCostContextStorage>();
        builder.Services.AddTransient<ModifyChatCompletionsRequestDelegatingHandler>();
        builder.Services.AddHttpClient(LlmHttpClient)
            .AddHttpMessageHandler<ModifyChatCompletionsRequestDelegatingHandler>();
        builder.Services.AddSingleton(resolver =>
        {
            var httpClientFactory = resolver.GetRequiredService<IHttpClientFactory>();
            var loggerFactory = resolver.GetRequiredService<ILoggerFactory>();
            var httpClient = httpClientFactory.CreateClient(LlmHttpClient);
            return new OpenAIClient(
                new ApiKeyCredential(config.Llm.ApiKey),
                new()
                {
                    Endpoint = config.Llm.Endpoint,
                    Transport = new HttpClientPipelineTransport(httpClient, true, loggerFactory)
                });
        });
        builder.Services.AddSingleton(resolver =>
        {
            var openAiClient = resolver.GetRequiredService<OpenAIClient>();
            return openAiClient.GetChatClient(config.Llm.Model);
        });
        builder.Services.AddSingleton(resolver =>
        {
            var chatClient = resolver.GetRequiredService<ChatClient>();
            var loggerFactory = resolver.GetRequiredService<ILoggerFactory>();
            return chatClient.AsIChatClient()
                .AsBuilder()
                .UseLogging(loggerFactory)
                .UseFunctionInvocation()
                .Build();
        });
        // LLM Chat
        builder.Services.AddSingleton(new DefaultLlmChatHandlerOptions(config.Telegram.BotName, config.Llm.DefaultResponse));
        builder.Services.AddSingleton<ILlmChatHandler, DefaultLlmChatHandler>();
        // DataAccess
        builder.Services.AddDbContext<BotDbContext>(dbContextOptions =>
        {
            dbContextOptions.UseNpgsql(
                config.DataAccess.PostgresConnectionString,
                options =>
                {
                    options.SetPostgresVersion(18, 0);
                    options.MigrationsAssembly(typeof(DesignTimeBotDbContextFactory).Assembly);
                });
        });
        builder.Services.AddSingleton<ITelegramMessageStorage, DefaultTelegramMessageStorage>();
        builder.Services.AddSingleton<ITelegramKickedUsersStorage, DefaultTelegramKickedUsersStorage>();
        builder.Services.AddSingleton<ISystemPromptService, DefaultSystemPromptService>();
        builder.Services.AddSingleton<ILlmLimitsService, DefaultLlmLimitsService>();
        // MCP
        builder.Services.AddSingleton<DefaultMcpToolsProvider>();
        builder.Services.AddSingleton<IMcpToolsProvider>(resolver => resolver.GetRequiredService<DefaultMcpToolsProvider>());
        // MCP - Github
        builder.Services.AddHttpClient(DefaultGithubMcpClientFactory.GithubHttpClientName);
        builder.Services.AddSingleton(new DefaultGithubMcpClientFactoryOptions(
            config.Mcp.Github.PersonalAccessToken,
            config.Mcp.Github.WorkingDirectory,
            config.Mcp.Github.Command));
        builder.Services.AddSingleton<IGithubMcpClientFactory, DefaultGithubMcpClientFactory>();
        builder.Services.AddKeyedSingleton<McpClient>(McpClientName.Github,
            (resolver, _) =>
            {
                var githubFactory = resolver.GetRequiredService<IGithubMcpClientFactory>();
                return githubFactory.CreateAsync(CancellationToken.None).GetAwaiter().GetResult();
            });
        // OpenRouter stats
        builder.Services.AddSingleton(new DefaultOpenRouterKeyUsageProviderOptions(config.Llm.ApiKey));
        builder.Services.AddHttpClient<IOpenRouterKeyUsageProvider, DefaultOpenRouterKeyUsageProvider>();
        // Channel to send typing status to chats
        var startTypingStatusChannel = Channel.CreateBounded<StartTypingCommand>(new BoundedChannelOptions(20)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = false,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
        builder.Services.AddSingleton<ChannelWriter<StartTypingCommand>>(resolver =>
        {
            var hostLifetime = resolver.GetRequiredService<IHostApplicationLifetime>();
            hostLifetime.ApplicationStopping.Register(() => startTypingStatusChannel.Writer.Complete());
            return startTypingStatusChannel.Writer;
        });
        builder.Services.AddSingleton(startTypingStatusChannel.Reader);
        // Channel to stop sending typing status to chats
        var stopSendingTypingStatusChannel = Channel.CreateBounded<StopTypingCommand>(new BoundedChannelOptions(20)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = false,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
        builder.Services.AddSingleton<ChannelWriter<StopTypingCommand>>(resolver =>
        {
            var hostLifetime = resolver.GetRequiredService<IHostApplicationLifetime>();
            hostLifetime.ApplicationStopping.Register(() => stopSendingTypingStatusChannel.Writer.Complete());
            return stopSendingTypingStatusChannel.Writer;
        });
        builder.Services.AddSingleton(stopSendingTypingStatusChannel.Reader);
        // Typing sender service
        builder.Services.AddSingleton<ITypingStatusService, TypingStatusService>();
        return builder;
    }


    [SuppressMessage("ReSharper", "ConvertToUsingDeclaration")]
    private static void LogHostCrash(Exception ex)
    {
        var loggingHostBuilder = Host.CreateApplicationBuilder();
        loggingHostBuilder.Logging.ClearProviders();
        loggingHostBuilder.Logging.SetMinimumLevel(LogLevel.Trace);
        loggingHostBuilder.Logging.AddSimpleConsole(options =>
        {
            options.ColorBehavior = LoggerColorBehavior.Enabled;
            options.UseUtcTimestamp = true;
            options.TimestampFormat = "[yyyy-MM-dd HH:mm:ss] ";
        });
        using (var tempHost = loggingHostBuilder.Build())
        {
            var tempLoggerFactory = tempHost.Services.GetRequiredService<ILoggerFactory>();
            var tempLogger = tempLoggerFactory.CreateLogger<Program>();
            LogHostCrash(tempLogger, ex);
        }
    }

    [LoggerMessage(EventId = -1, Level = LogLevel.Critical, Message = "Host terminated unexpectedly")]
    private static partial void LogHostCrash(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 0, Level = LogLevel.Information, Message = "Application starting")]
    private static partial void LogApplicationStarting(ILogger logger);

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Getting information about telegram bot itself")]
    private static partial void LogGettingSelfInformation(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Successful got information about telegram bot itself")]
    private static partial void LogGotSelfInformationSuccessful(ILogger logger);
}
