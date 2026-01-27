using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;
using TgLlmBot.CommandDispatcher.Abstractions;
using TgLlmBot.Services.DataAccess.SystemPrompts;
using TgLlmBot.Services.DataAccess.TelegramMessages;
using TgLlmBot.Commands.ChatWithLlm.Services;
using TgLlmBot.Services.PromptRewrite;

namespace TgLlmBot.Commands.SetPersonalSystemPrompt;

public class SetPersonalSystemPromptCommandHandler : AbstractCommandHandler<SetPersonalSystemPromptCommand>
{
    private readonly TelegramBotClient _bot;
    private readonly ITelegramMessageStorage _storage;
    private readonly ISystemPromptService _systemPrompt;
    private readonly IPromptRewriteService _promptRewrite;
    private readonly DefaultLlmChatHandlerOptions _llmOptions;

    public SetPersonalSystemPromptCommandHandler(
        TelegramBotClient bot,
        ISystemPromptService systemPrompt,
        ITelegramMessageStorage storage,
        IPromptRewriteService promptRewrite,
        DefaultLlmChatHandlerOptions llmOptions)
    {
        ArgumentNullException.ThrowIfNull(bot);
        ArgumentNullException.ThrowIfNull(systemPrompt);
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(promptRewrite);
        ArgumentNullException.ThrowIfNull(llmOptions);
        _bot = bot;
        _systemPrompt = systemPrompt;
        _storage = storage;
        _promptRewrite = promptRewrite;
        _llmOptions = llmOptions;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types")]
    public override async Task HandleAsync(SetPersonalSystemPromptCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var prompt = $"{command.Message.Text?.Trim()}".Trim();
            if (prompt.StartsWith("!personal_role", StringComparison.Ordinal))
            {
                prompt = prompt["!personal_role".Length..].Trim();
            }

            if (string.IsNullOrWhiteSpace(prompt) || command.Message.From is null)
            {
                var response = await _bot.SendMessage(
                    command.Message.Chat,
                    "❌ Не удалось поменять персональный системный промпт",
                    ParseMode.MarkdownV2,
                    new()
                    {
                        MessageId = command.Message.MessageId
                    },
                    cancellationToken: cancellationToken);
                await _storage.StoreMessageAsync(response, command.Self, cancellationToken);
            }
            else
            {
                var globalPrompt = DefaultLlmChatHandler.BuildBasePrompt(_llmOptions.BotName);
                var rewriteResult = await _promptRewrite.RewriteIfViolatesAsync(globalPrompt, prompt, cancellationToken);
                await _systemPrompt.SetUserChatPromptAsync(command.Message.Chat.Id, command.Message.From.Id, rewriteResult.Prompt, cancellationToken);
                var message = rewriteResult.WasRewritten
                    ? "✅ Персональный системный промпт изменён \\(скорректирован модератором\\)"
                    : "✅ Персональный системный промпт успешно изменён";
                var response = await _bot.SendMessage(
                    command.Message.Chat,
                    message,
                    ParseMode.MarkdownV2,
                    new()
                    {
                        MessageId = command.Message.MessageId
                    },
                    cancellationToken: cancellationToken);
                await _storage.StoreMessageAsync(response, command.Self, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            var response = await _bot.SendMessage(
                command.Message.Chat,
                ex.Message,
                ParseMode.None,
                new()
                {
                    MessageId = command.Message.MessageId
                },
                cancellationToken: cancellationToken);
            await _storage.StoreMessageAsync(response, command.Self, cancellationToken);
        }
    }
}
