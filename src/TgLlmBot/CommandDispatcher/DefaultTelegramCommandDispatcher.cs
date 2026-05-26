using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using TgLlmBot.Commands.ChatWithLlm;
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
using TgLlmBot.Services.DataAccess.TelegramMessages;
using TgLlmBot.Services.Telegram.SelfInformation;
using RatingCommandHandler = TgLlmBot.Commands.Rating.RatingCommandHandler;
using TgLlmBot.DataAccess.Models;

namespace TgLlmBot.CommandDispatcher;

public class DefaultTelegramCommandDispatcher : ITelegramCommandDispatcher
{
    private static readonly HashSet<MessageType> AllowedMessageTypes =
    [
        MessageType.Text,
        MessageType.Photo
    ];

    private readonly ChatWithLlmCommandHandler _chatWithLlm;
    private readonly DisplayHelpCommandHandler _displayHelp;
    private readonly ITelegramMessageStorage _messageStorage;
    private readonly ModelCommandHandler _model;
    private readonly DefaultTelegramCommandDispatcherOptions _options;
    private readonly PingCommandHandler _ping;
    private readonly RatingCommandHandler _rating;
    private readonly RepoCommandHandler _repo;
    private readonly ResetChatSystemPromptCommandHandler _resetChatSystemPrompt;
    private readonly ResetPersonalSystemPromptCommandHandler _resetPersonalSystemPrompt;
    private readonly ITelegramSelfInformation _self;
    private readonly SetChatSystemPromptCommandHandler _setChatSystemPrompt;
    private readonly SetLimitCommandHandler _setLimit;
    private readonly SetPersonalSystemPromptCommandHandler _setPersonalSystemPrompt;
    private readonly ShowChatSystemPromptCommandHandler _showChatSystemPrompt;
    private readonly ShowPersonalSystemPromptCommandHandler _showPersonalSystemPrompt;
    private readonly UsageCommandHandler _usage;

    public DefaultTelegramCommandDispatcher(
        DefaultTelegramCommandDispatcherOptions options,
        ITelegramSelfInformation self,
        ITelegramMessageStorage messageStorage,
        DisplayHelpCommandHandler displayHelp,
        ChatWithLlmCommandHandler chatWithLlm,
        PingCommandHandler ping,
        RepoCommandHandler repo,
        ModelCommandHandler model,
        UsageCommandHandler usage,
        RatingCommandHandler rating,
        SetChatSystemPromptCommandHandler setChatSystemPrompt,
        ResetChatSystemPromptCommandHandler resetChatSystemPrompt,
        SetPersonalSystemPromptCommandHandler setPersonalSystemPrompt,
        ResetPersonalSystemPromptCommandHandler resetPersonalSystemPrompt,
        ShowChatSystemPromptCommandHandler showChatSystemPrompt,
        ShowPersonalSystemPromptCommandHandler showPersonalSystemPrompt,
        SetLimitCommandHandler setLimit)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(self);
        ArgumentNullException.ThrowIfNull(messageStorage);
        ArgumentNullException.ThrowIfNull(displayHelp);
        ArgumentNullException.ThrowIfNull(chatWithLlm);
        ArgumentNullException.ThrowIfNull(ping);
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(usage);
        ArgumentNullException.ThrowIfNull(rating);
        ArgumentNullException.ThrowIfNull(setChatSystemPrompt);
        ArgumentNullException.ThrowIfNull(resetChatSystemPrompt);
        ArgumentNullException.ThrowIfNull(setPersonalSystemPrompt);
        ArgumentNullException.ThrowIfNull(resetPersonalSystemPrompt);
        ArgumentNullException.ThrowIfNull(showChatSystemPrompt);
        ArgumentNullException.ThrowIfNull(showPersonalSystemPrompt);
        ArgumentNullException.ThrowIfNull(setLimit);
        _options = options;
        _self = self;
        _messageStorage = messageStorage;
        _displayHelp = displayHelp;
        _chatWithLlm = chatWithLlm;
        _ping = ping;
        _repo = repo;
        _model = model;
        _usage = usage;
        _rating = rating;
        _setChatSystemPrompt = setChatSystemPrompt;
        _resetChatSystemPrompt = resetChatSystemPrompt;
        _setPersonalSystemPrompt = setPersonalSystemPrompt;
        _resetPersonalSystemPrompt = resetPersonalSystemPrompt;
        _showChatSystemPrompt = showChatSystemPrompt;
        _showPersonalSystemPrompt = showPersonalSystemPrompt;
        _setLimit = setLimit;
    }

    public async Task HandleMessageAsync(Message? message, UpdateType type, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (message is null)
        {
            return;
        }

        if (!AllowedMessageTypes.Contains(message.Type))
        {
            return;
        }

        var self = _self.GetSelf();
        // await _messageStorage.StoreMessageAsync(message, self, cancellationToken);
        // ReSharper disable once ConditionalAccessQualifierIsNonNullableAccordingToAPIContract
        var rawPrompt = $"{message.Text?.Trim()?.ToLowerInvariant()}";
        if (IsServiceCommand(rawPrompt))
        {
            await _messageStorage.StoreMessageAsync(
                message,
                self,
                cancellationToken,
                DbChatMessageKind.ServiceCommand);
        }
        else
        {
            await _messageStorage.StoreMessageAsync(
                message,
                self,
                cancellationToken,
                DbChatMessageKind.UserMessage);
        }
        switch (rawPrompt)
        {
            case "!help":
                {
                    var command = new DisplayHelpCommand(message, type);
                    await _displayHelp.HandleAsync(command, cancellationToken);
                    return;
                }
            case "!ping":
                {
                    var command = new PingCommand(message, type);
                    await _ping.HandleAsync(command, cancellationToken);
                    return;
                }
            case "!repo":
                {
                    var command = new RepoCommand(message, type);
                    await _repo.HandleAsync(command, cancellationToken);
                    return;
                }
            case "!model":
                {
                    var command = new ModelCommand(message, type);
                    await _model.HandleAsync(command, cancellationToken);
                    return;
                }
            case "!usage":
                {
                    var command = new UsageCommand(message, type);
                    await _usage.HandleAsync(command, cancellationToken);
                    return;
                }
            case "!rating":
                {
                    var command = new RatingCommand(message, type, self);
                    await _rating.HandleAsync(command, cancellationToken);
                    return;
                }
            case "!chat_role_reset":
                {
                    var command = new ResetChatSystemPromptCommand(message, type, self);
                    await _resetChatSystemPrompt.HandleAsync(command, cancellationToken);
                    return;
                }
            case "!personal_role_reset":
                {
                    var command = new ResetPersonalSystemPromptCommand(message, type, self);
                    await _resetPersonalSystemPrompt.HandleAsync(command, cancellationToken);
                    return;
                }
            case "!personal_role_show":
                {
                    var command = new ShowPersonalSystemPromptCommand(message, type, self);
                    await _showPersonalSystemPrompt.HandleAsync(command, cancellationToken);
                    return;
                }
            case "!chat_role_show":
                {
                    var command = new ShowChatSystemPromptCommand(message, type, self);
                    await _showChatSystemPrompt.HandleAsync(command, cancellationToken);
                    return;
                }
        }

        if (rawPrompt.StartsWith("!chat_role", StringComparison.Ordinal))
        {
            var command = new SetChatSystemPromptCommand(message, type, self);
            await _setChatSystemPrompt.HandleAsync(command, cancellationToken);
            return;
        }

        if (rawPrompt.StartsWith("!personal_role", StringComparison.Ordinal))
        {
            var command = new SetPersonalSystemPromptCommand(message, type, self);
            await _setPersonalSystemPrompt.HandleAsync(command, cancellationToken);
            return;
        }

        if (rawPrompt.StartsWith("!set_limit", StringComparison.Ordinal))
        {
            var command = new SetLimitCommand(message, type, self);
            await _setLimit.HandleAsync(command, cancellationToken);
            return;
        }

        var prompt = message.Text ?? message.Caption;
        if (message.Chat.Type == ChatType.Private)
        {
            var command = new ChatWithLlmCommand(message, type, self, prompt);
            await _chatWithLlm.HandleAsync(command, cancellationToken);
        }
        else if (message.Chat.Type is ChatType.Group or ChatType.Supergroup)
        {
            if (prompt?.StartsWith(_options.BotName, StringComparison.OrdinalIgnoreCase) is true
                || message.ReplyToMessage?.From?.Id == self.Id)
            {
                var command = new ChatWithLlmCommand(message, type, self, prompt);
                await _chatWithLlm.HandleAsync(command, cancellationToken);
            }
        }
    }

    private static bool IsServiceCommand(string rawPrompt)
    {
        return rawPrompt is "!help"
            or "!ping"
            or "!repo"
            or "!model"
            or "!usage"
            or "!rating"
            or "!chat_role_reset"
            or "!personal_role_reset"
            or "!personal_role_show"
            or "!chat_role_show"
            || rawPrompt.StartsWith("!chat_role", StringComparison.Ordinal)
            || rawPrompt.StartsWith("!personal_role", StringComparison.Ordinal)
            || rawPrompt.StartsWith("!set_limit", StringComparison.Ordinal);
    }
}
