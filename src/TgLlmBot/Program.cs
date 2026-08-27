using System;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Diagnostics.CodeAnalysis;
using System.Net.Http;
using System.Text;
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
using TgLlmBot.Commands.ChatWithLlm.Queues;
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
using TgLlmBot.Services.DataAccess.MediaDescriptions;
using TgLlmBot.Services.DataAccess.SystemPrompts;
using TgLlmBot.Services.DataAccess.TelegramMessages;
using TgLlmBot.Services.Llm.Compression;
using TgLlmBot.Services.Llm.Vision;
using TgLlmBot.Services.Mcp.Clients.Github;
using TgLlmBot.Services.Mcp.Enums;
using TgLlmBot.Services.Mcp.Tools;
using TgLlmBot.Services.Media;
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

    private const string LlmVisionHttpClient = "llm-vision-http-client";

    private const string LlmVisionClientKey = "llm-vision";

    private const string LlmCompactionHttpClient = "llm-compaction-http-client";

    private const string LlmCompactionClientKey = "llm-compaction";

    private const int LlmRequestQueueCapacityPerChat = 200;

    private const int MediaRecognitionQueueCapacityPerChat = 200;

    private static readonly TimeSpan MediaSweepInterval = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan LlmRequestTimeout = TimeSpan.FromSeconds(3600);

    private static readonly TimeSpan LlmVisionRequestTimeout = TimeSpan.FromSeconds(300);

    private static readonly TimeSpan LlmCompactionRequestTimeout = TimeSpan.FromSeconds(600);

    [SuppressMessage("ReSharper", "ConvertToUsingDeclaration")]
    [SuppressMessage("Design", "CA1031:Do not catch general exception types")]
    public static async Task<int> Main(string[] args)
    {
        var exitCode = 0;
        Console.OutputEncoding = Encoding.UTF8;
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
        builder.Services.AddSingleton(new ModelCommandHandlerOptions(
            config.Llm.Endpoint,
            config.Llm.Model,
            config.Llm.Vision.Endpoint,
            config.Llm.Vision.Model));
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
        // Separate LLM request queue per allowed chat, so different chats are processed in parallel
        builder.Services.AddSingleton(new DefaultLlmRequestQueuesOptions(
            config.Telegram.AllowedChatIds,
            LlmRequestQueueCapacityPerChat));
        builder.Services.AddSingleton<ILlmRequestQueues>(resolver =>
        {
            var queuesOptions = resolver.GetRequiredService<DefaultLlmRequestQueuesOptions>();
            var queuesLogger = resolver.GetRequiredService<ILogger<DefaultLlmRequestQueues>>();
            var queues = new DefaultLlmRequestQueues(queuesOptions, queuesLogger);
            var hostLifetime = resolver.GetRequiredService<IHostApplicationLifetime>();
            hostLifetime.ApplicationStopping.Register(queues.Complete);
            return queues;
        });
        // Background services
        builder.Services.AddHostedService<LlmRequestsBackgroundService>();
        builder.Services.AddHostedService<CleanupOldMessagesBackgroundService>();
        builder.Services.AddHostedService<TypingStatusBackgroundService>();

        // LLM
        builder.Services.AddHttpClient(LlmHttpClient, httpClient => httpClient.Timeout = LlmRequestTimeout);
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
                    NetworkTimeout = LlmRequestTimeout,
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
        // LLM - Vision (отдельный инстанс с мультимодальной моделью, распознающей изображения)
        builder.Services.AddHttpClient(LlmVisionHttpClient, httpClient => httpClient.Timeout = LlmVisionRequestTimeout);
        builder.Services.AddKeyedSingleton<OpenAIClient>(LlmVisionClientKey, (resolver, _) =>
        {
            var httpClientFactory = resolver.GetRequiredService<IHttpClientFactory>();
            var loggerFactory = resolver.GetRequiredService<ILoggerFactory>();
            var httpClient = httpClientFactory.CreateClient(LlmVisionHttpClient);
            return new OpenAIClient(
                new ApiKeyCredential(config.Llm.Vision.ApiKey),
                new()
                {
                    Endpoint = config.Llm.Vision.Endpoint,
                    NetworkTimeout = LlmVisionRequestTimeout,
                    Transport = new HttpClientPipelineTransport(httpClient, true, loggerFactory)
                });
        });
        builder.Services.AddKeyedSingleton<IChatClient>(LlmVisionClientKey, (resolver, serviceKey) =>
        {
            var openAiClient = resolver.GetRequiredKeyedService<OpenAIClient>(serviceKey);
            var loggerFactory = resolver.GetRequiredService<ILoggerFactory>();
            // Инструменты vision-модели не отдаём: она только описывает картинку, вызывать MCP - работа основной модели.
            return openAiClient.GetChatClient(config.Llm.Vision.Model)
                .AsIChatClient()
                .AsBuilder()
                .UseLogging(loggerFactory)
                .Build();
        });
        builder.Services.AddSingleton<IImageRecognizer>(resolver =>
        {
            var visionChatClient = resolver.GetRequiredKeyedService<IChatClient>(LlmVisionClientKey);
            var recognizerLogger = resolver.GetRequiredService<ILogger<DefaultImageRecognizer>>();
            return new DefaultImageRecognizer(visionChatClient, recognizerLogger);
        });
        // Распознавание вложений: отдельные от LLM-запросов per-chat очереди, потому что описывать
        // надо все картинки чата, а не только те, что пришли вместе с обращением к боту
        builder.Services.AddSingleton<ITelegramMediaDownloader, DefaultTelegramMediaDownloader>();
        builder.Services.AddSingleton<IMediaDescriptionCache, DefaultMediaDescriptionCache>();
        builder.Services.AddSingleton<IMediaGroupTracker, DefaultMediaGroupTracker>();
        // Ужимает подробные описания до размера истории - уже основной моделью, а не vision:
        // отдельный инстанс той же модели, но без инструментов, с запасом на историю в запросе
        builder.Services.AddHttpClient(LlmCompactionHttpClient, httpClient => httpClient.Timeout = LlmCompactionRequestTimeout);
        builder.Services.AddKeyedSingleton<OpenAIClient>(LlmCompactionClientKey, (resolver, _) =>
        {
            var httpClientFactory = resolver.GetRequiredService<IHttpClientFactory>();
            var loggerFactory = resolver.GetRequiredService<ILoggerFactory>();
            var httpClient = httpClientFactory.CreateClient(LlmCompactionHttpClient);
            return new OpenAIClient(
                new ApiKeyCredential(config.Llm.ApiKey),
                new()
                {
                    Endpoint = config.Llm.Endpoint,
                    NetworkTimeout = LlmCompactionRequestTimeout,
                    Transport = new HttpClientPipelineTransport(httpClient, true, loggerFactory)
                });
        });
        builder.Services.AddKeyedSingleton<IChatClient>(LlmCompactionClientKey, (resolver, serviceKey) =>
        {
            var openAiClient = resolver.GetRequiredKeyedService<OpenAIClient>(serviceKey);
            var loggerFactory = resolver.GetRequiredService<ILoggerFactory>();
            // Инструменты компактинг-клиенту не отдаём: задача чисто текстовая
            return openAiClient.GetChatClient(config.Llm.Model)
                .AsIChatClient()
                .AsBuilder()
                .UseLogging(loggerFactory)
                .Build();
        });
        builder.Services.AddSingleton<IMediaDescriptionCompressor>(resolver =>
        {
            var compactionChatClient = resolver.GetRequiredKeyedService<IChatClient>(LlmCompactionClientKey);
            var compressorLogger = resolver.GetRequiredService<ILogger<DefaultMediaDescriptionCompressor>>();
            return new DefaultMediaDescriptionCompressor(compactionChatClient, compressorLogger);
        });
        builder.Services.AddSingleton(new DefaultMediaRecognitionQueuesOptions(
            config.Telegram.AllowedChatIds,
            MediaRecognitionQueueCapacityPerChat));
        builder.Services.AddSingleton<IMediaRecognitionQueues>(resolver =>
        {
            var queuesOptions = resolver.GetRequiredService<DefaultMediaRecognitionQueuesOptions>();
            var queuesLogger = resolver.GetRequiredService<ILogger<DefaultMediaRecognitionQueues>>();
            var queues = new DefaultMediaRecognitionQueues(queuesOptions, queuesLogger);
            var hostLifetime = resolver.GetRequiredService<IHostApplicationLifetime>();
            hostLifetime.ApplicationStopping.Register(queues.Complete);
            return queues;
        });
        builder.Services.AddSingleton(new MediaRecognitionBackgroundServiceOptions(MediaSweepInterval));
        builder.Services.AddHostedService<MediaRecognitionBackgroundService>();
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
