using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using TgLlmBot.CommandDispatcher.Abstractions;
using TgLlmBot.Services.DataAccess.SystemPrompts;
using TgLlmBot.Services.DataAccess.TelegramMessages;
using TgLlmBot.Commands.ChatWithLlm.Services;
using TgLlmBot.Services.PromptRewrite;
using TgLlmBot.Services.Resources;

namespace TgLlmBot.Commands.SetChatSystemPrompt;

public class SetChatSystemPromptCommandHandler : AbstractCommandHandler<SetChatSystemPromptCommand>
{
    private readonly TelegramBotClient _bot;
    private readonly ITelegramMessageStorage _storage;
    private readonly ISystemPromptService _systemPrompt;
    private readonly IPromptRewriteService _promptRewrite;
    private readonly DefaultLlmChatHandlerOptions _llmOptions;

    public SetChatSystemPromptCommandHandler(
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
    public override async Task HandleAsync(SetChatSystemPromptCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var prompt = $"{command.Message.Text?.Trim()}".Trim();
            if (prompt.StartsWith("!chat_role", StringComparison.Ordinal))
            {
                prompt = prompt["!chat_role".Length..].Trim();
            }

            if (string.IsNullOrWhiteSpace(prompt))
            {
                var errorResponse = await _bot.SendMessage(
                    command.Message.Chat,
                    "❌ Промпт не может быть пустым",
                    ParseMode.MarkdownV2,
                    new()
                    {
                        MessageId = command.Message.MessageId
                    },
                    cancellationToken: cancellationToken);
                await _storage.StoreMessageAsync(errorResponse, command.Self, cancellationToken);
                return;
            }

            var isAdmin = await IsAdminMessageAsync(command, cancellationToken);
            if (isAdmin)
            {
                var globalPrompt = DefaultLlmChatHandler.BuildBasePrompt(_llmOptions.BotName);
                var rewriteResult = await _promptRewrite.RewriteIfViolatesAsync(globalPrompt, prompt, cancellationToken);
                await _systemPrompt.SetChatPromptAsync(command.Message.Chat.Id, rewriteResult.Prompt, cancellationToken);
                var message = rewriteResult.WasRewritten
                    ? "✅ Системный промпт чата изменён \\(скорректирован модератором\\)"
                    : "✅ Системный промпт чата успешно изменён";
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
            else
            {
                var response = await _bot.SendPhoto(
                    command.Message.Chat,
                    new InputFileStream(new MemoryStream(EmbeddedResources.NoJpg), "no.jpg"),
                    "❌ Только администраторы могут менять системный промпт чата",
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

    private async Task<bool> IsAdminMessageAsync(SetChatSystemPromptCommand command, CancellationToken cancellationToken)
    {
        if (command.Message.Chat.Type is ChatType.Group or ChatType.Supergroup && command.Message.From is not null)
        {
            var admins = await _bot.GetChatAdministrators(command.Message.Chat, cancellationToken);
            return admins.Any(x => x.User.Id == command.Message.From.Id);
        }

        return true;
    }
}
