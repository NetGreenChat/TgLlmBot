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
using TgLlmBot.Commands.SetChatLimit;
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
using TgLlmBot.Services.Llm.Descriptions;
using TgLlmBot.Services.Llm.Multimodal;
using TgLlmBot.Services.Mcp.Clients.Exa;
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

    private const string LlmMediaHttpClient = "llm-media-http-client";

    private const string LlmMediaClientKey = "llm-media";

    private const int LlmRequestQueueCapacityPerChat = 200;

    private const int MediaRecognitionQueueCapacityPerChat = 200;

    private static readonly TimeSpan MediaSweepInterval = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan LlmRequestTimeout = TimeSpan.FromSeconds(3600);

    private static readonly TimeSpan LlmMediaRequestTimeout = TimeSpan.FromSeconds(600);

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

            var github = asyncScope.ServiceProvider.GetKeyedService<McpClient>(McpClientName.Github);
            if (github is not null)
            {
                var githubTools = await github.ListToolsAsync();
                toolsProvider.AddTools(githubTools);
            }

            var exa = asyncScope.ServiceProvider.GetKeyedService<McpClient>(McpClientName.Exa);
            if (exa is not null)
            {
                var exaTools = await exa.ListToolsAsync();
                toolsProvider.AddTools(exaTools);
            }
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
            config.Llm.Capabilities.Image,
            config.Llm.Capabilities.Video,
            config.Llm.Capabilities.Audio));
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
        builder.Services.AddSingleton<SetChatLimitCommandHandler>();
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

        // LLM: один инстанс модели на всё - и текст, и вложения, если капабилити позволяют
        builder.Services.AddSingleton(config.Llm.Capabilities);
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
            // Мультимодальный клиент обязан стоять ниже FunctionInvokingChatClient: тот на каждом
            // раунде tool-loop'а дополняет список сообщений, и подменять тело запроса нужно по
            // актуальному списку, а не по тому, с которого начали
            var logging = chatClient.AsIChatClient()
                .AsBuilder()
                .UseLogging(loggerFactory)
                .Build();
            return new MultimodalChatClient(logging, disableThinking: false)
                .AsBuilder()
                .UseFunctionInvocation()
                .Build();
        });
        // LLM - Media (тот же инстанс модели, но без инструментов: компактные описания вложений
        // для истории чата готовятся в фоне, отдельным клиентом с запасом по таймауту)
        builder.Services.AddHttpClient(LlmMediaHttpClient, httpClient => httpClient.Timeout = LlmMediaRequestTimeout);
        builder.Services.AddKeyedSingleton<OpenAIClient>(LlmMediaClientKey, (resolver, _) =>
        {
            var httpClientFactory = resolver.GetRequiredService<IHttpClientFactory>();
            var loggerFactory = resolver.GetRequiredService<ILoggerFactory>();
            var httpClient = httpClientFactory.CreateClient(LlmMediaHttpClient);
            return new OpenAIClient(
                new ApiKeyCredential(config.Llm.ApiKey),
                new()
                {
                    Endpoint = config.Llm.Endpoint,
                    NetworkTimeout = LlmMediaRequestTimeout,
                    Transport = new HttpClientPipelineTransport(httpClient, true, loggerFactory)
                });
        });
        builder.Services.AddKeyedSingleton<IChatClient>(LlmMediaClientKey, (resolver, serviceKey) =>
        {
            var openAiClient = resolver.GetRequiredKeyedService<OpenAIClient>(serviceKey);
            var loggerFactory = resolver.GetRequiredService<ILoggerFactory>();
            // Инструменты описателю не отдаём: задача чисто описательная, лазить в интернет за ней некуда
            var logging = openAiClient.GetChatClient(config.Llm.Model)
                .AsIChatClient()
                .AsBuilder()
                .UseLogging(loggerFactory)
                .Build();
            return new MultimodalChatClient(logging, disableThinking: true);
        });
        builder.Services.AddSingleton<IMediaDescriber>(resolver =>
        {
            var mediaChatClient = resolver.GetRequiredKeyedService<IChatClient>(LlmMediaClientKey);
            var describerLogger = resolver.GetRequiredService<ILogger<DefaultMediaDescriber>>();
            return new DefaultMediaDescriber(mediaChatClient, describerLogger);
        });
        // Описания вложений для истории: отдельные от LLM-запросов per-chat очереди, потому что
        // описывать надо все картинки чата, а не только те, что пришли вместе с обращением к боту
        builder.Services.AddSingleton<ITelegramMediaDownloader, DefaultTelegramMediaDownloader>();
        // Анимированные стикеры (TGS) не откроет ни один декодер видео - их кадры рисуются на месте,
        // остальное подготовщик отдаёт модели как есть
        builder.Services.AddSingleton<IAnimatedStickerRenderer, DefaultAnimatedStickerRenderer>();
        builder.Services.AddSingleton<IMediaPreparer, DefaultMediaPreparer>();
        builder.Services.AddSingleton<IMediaDescriptionCache, DefaultMediaDescriptionCache>();
        builder.Services.AddSingleton<IMediaGroupTracker, DefaultMediaGroupTracker>();
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
        builder.Services.AddSingleton(new MediaRecognitionBackgroundServiceOptions(MediaSweepInterval, config.Telegram.BotName));
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
        // MCP - Github (опциональный: отключается через Mcp:Github:Enabled, тогда PAT не требуется)
        if (config.Mcp.Github is { } github)
        {
            builder.Services.AddHttpClient(DefaultGithubMcpClientFactory.GithubHttpClientName);
            builder.Services.AddSingleton(new DefaultGithubMcpClientFactoryOptions(
                github.Endpoint,
                github.PersonalAccessToken));
            builder.Services.AddSingleton<IGithubMcpClientFactory, DefaultGithubMcpClientFactory>();
            builder.Services.AddKeyedSingleton<McpClient>(McpClientName.Github,
                (resolver, _) =>
                {
                    var githubFactory = resolver.GetRequiredService<IGithubMcpClientFactory>();
                    return githubFactory.CreateAsync(CancellationToken.None).GetAwaiter().GetResult();
                });
        }
        // MCP - Exa (опциональный: отключается через Mcp:Exa:Enabled, тогда API-ключ не требуется)
        if (config.Mcp.Exa is { } exa)
        {
            builder.Services.AddHttpClient(DefaultExaMcpClientFactory.ExaHttpClientName);
            builder.Services.AddSingleton(new DefaultExaMcpClientFactoryOptions(exa.Endpoint, exa.ApiKey));
            builder.Services.AddSingleton<IExaMcpClientFactory, DefaultExaMcpClientFactory>();
            builder.Services.AddKeyedSingleton<McpClient>(McpClientName.Exa,
                (resolver, _) =>
                {
                    var exaFactory = resolver.GetRequiredService<IExaMcpClientFactory>();
                    return exaFactory.CreateAsync(CancellationToken.None).GetAwaiter().GetResult();
                });
        }
        // OpenRouter stats
        builder.Services.AddSingleton(new DefaultOpenRouterKeyUsageProviderOptions(config.Llm.ApiKey));
        builder.Services.AddHttpClient<IOpenRouterKeyUsageProvider, DefaultOpenRouterKeyUsageProvider>();
        // Separate typing status queue per allowed chat, so a slow cancellation in one chat
        // never holds up the others
        builder.Services.AddSingleton(new DefaultTypingStatusQueuesOptions(config.Telegram.AllowedChatIds));
        builder.Services.AddSingleton<ITypingStatusQueues>(resolver =>
        {
            var queuesOptions = resolver.GetRequiredService<DefaultTypingStatusQueuesOptions>();
            var queues = new DefaultTypingStatusQueues(queuesOptions);
            var hostLifetime = resolver.GetRequiredService<IHostApplicationLifetime>();
            hostLifetime.ApplicationStopping.Register(queues.Complete);
            return queues;
        });
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
