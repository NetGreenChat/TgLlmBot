using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using TgLlmBot.CommandDispatcher.Abstractions;
using TgLlmBot.Services.DataAccess.Limits;
using TgLlmBot.Services.DataAccess.TelegramMessages;
using TgLlmBot.Services.Llm;
using TgLlmBot.Services.Resources;
using TgLlmBot.Services.Telegram.Markdown;
using TgLlmBot.Services.Telegram.TypingStatus;

namespace TgLlmBot.Commands.Rating;

public class RatingCommandHandler : AbstractCommandHandler<RatingCommand>
{
    /// <summary>
    ///     Схема ответа модели. Порядок свойств не случаен: короткое обоснование модель пишет
    ///     до оценки, и оценка опирается на него, а не наоборот.
    /// </summary>
    private const string LlmResponseJsonSchema = """
                                                 {
                                                     "$schema": "https://json-schema.org/draft/2020-12/schema",
                                                     "type": "object",
                                                     "properties": {
                                                         "Data": {
                                                             "type": "array",
                                                             "items": {
                                                                 "type": "object",
                                                                 "properties": {
                                                                     "Participant": {
                                                                         "type": "integer",
                                                                         "minimum": 1
                                                                     },
                                                                     "Reason": {
                                                                         "type": "string"
                                                                     },
                                                                     "Level": {
                                                                         "type": "integer",
                                                                         "minimum": 0,
                                                                         "maximum": 100
                                                                     }
                                                                 },
                                                                 "required": [
                                                                     "Participant",
                                                                     "Reason",
                                                                     "Level"
                                                                 ],
                                                                 "additionalProperties": false
                                                             }
                                                         }
                                                     },
                                                     "required": [
                                                         "Data"
                                                     ],
                                                     "additionalProperties": false
                                                 }
                                                 """;

    private const int ReportTopCount = 5;

    /// <summary>
    ///     Потолок обоснования оценки в отчёте: модель просят уложиться в него, а что не влезло -
    ///     обрезается.
    /// </summary>
    private const int ReasonMaxLength = 120;

    private readonly TelegramBotClient _bot;
    private readonly IChatClient _chatClient;
    private readonly ILlmLimitsService _limits;
    private readonly RatingCommandHandlerOptions _options;
    private readonly ITelegramMessageStorage _storage;
    private readonly ITelegramMarkdownConverter _telegramMarkdownConverter;
    private readonly TimeProvider _timeProvider;
    private readonly ITypingStatusService _typingStatusService;

    public RatingCommandHandler(
        RatingCommandHandlerOptions options,
        TelegramBotClient bot,
        IChatClient chatClient,
        ITelegramMessageStorage storage,
        ITelegramMarkdownConverter telegramMarkdownConverter,
        TimeProvider timeProvider,
        ITypingStatusService typingStatusService,
        ILlmLimitsService limits)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(bot);
        ArgumentNullException.ThrowIfNull(chatClient);
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(telegramMarkdownConverter);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(typingStatusService);
        ArgumentNullException.ThrowIfNull(limits);
        _options = options;
        _bot = bot;
        _chatClient = chatClient;
        _storage = storage;
        _telegramMarkdownConverter = telegramMarkdownConverter;
        _timeProvider = timeProvider;
        _typingStatusService = typingStatusService;
        _limits = limits;
    }


    [SuppressMessage("Design", "CA1031:Do not catch general exception types")]
    public override async Task HandleAsync(RatingCommand command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(command);
        var typing = _typingStatusService.StartTyping(command.Message.Chat.Id);
        try
        {
            if (command.Message.From?.Id is not null)
            {
                var isAllowed = await _limits.IsLLmInteractionAllowedAsync(command.Message.Chat.Id, command.Message.From.Id, cancellationToken);
                if (!isAllowed)
                {
                    typing.Stop();
                    var response = await _bot.SendPhoto(
                        command.Message.Chat,
                        new InputFileStream(new MemoryStream(EmbeddedResources.StopJpg), "stop.jpg"),
                        "❌ Превышен лимит сообщений",
                        ParseMode.MarkdownV2,
                        new()
                        {
                            MessageId = command.Message.MessageId
                        },
                        ephemeralMessageParameters: new()
                        {
                            ReceiverUserId = command.Message.From.Id
                        },
                        cancellationToken: cancellationToken);
                    await _storage.StoreMessageAsync(response, command.Self, cancellationToken);
                    return;
                }

                await _limits.IncrementUsageAsync(command.Message.Chat.Id, command.Message.From.Id, cancellationToken);
            }

            var contextMessages = await _storage.SelectContextMessagesAsync(
                command.Message,
                cancellationToken);

            // Кого оценивать, решается до запроса: модель получает нумерованный список и
            // историю, в заголовках которой у этих участников стоят их номера
            var authors = ChatAuthorDirectory.FromMessages(contextMessages);
            var participants = RatingParticipant.SelectAndLabel(contextMessages, command.Self.Id, authors);
            if (participants.Length is 0)
            {
                typing.Stop();
                await RespondWithMarkdownAsync(
                    command,
                    $"🤷 Оценивать некого: для рейтинга у участника должно быть хотя бы {RatingParticipant.MinMessagesCount.ToString(CultureInfo.InvariantCulture)} сообщений в истории",
                    cancellationToken);
                return;
            }

            var transcript = ChatTranscriptBuilder.BuildTranscript(contextMessages, _options.BotName, authors);
            var context = BuildContext(participants, transcript);
            var responseFormat = ChatResponseFormat.ForJsonSchema(
                JsonSerializer.Deserialize<JsonDocument>(LlmResponseJsonSchema, AIJsonUtilities.DefaultOptions)!.RootElement,
                "chat_cringe_rating_data",
                "Ответ на запрос об анализе уровня кринжа в чате");
            var chatOptions = new ChatOptions
            {
                ConversationId = Guid.NewGuid().ToString("N"),
                Temperature = 0.3f,
                AllowMultipleToolCalls = false,
                ToolMode = new NoneChatToolMode(),
                ResponseFormat = responseFormat,
                RawRepresentationFactory = static _ => LlmRawRequestFactory.CreateChatCompletionOptions()
            };
            var llmResponse = await _chatClient.GetResponseAsync(context, chatOptions, cancellationToken);
            // ReSharper disable once ConditionalAccessQualifierIsNonNullableAccordingToAPIContract
            var rawLLmResponse = llmResponse.Text?.Trim();
            typing.Stop();

            if (TryDeserializeLlmResponse(rawLLmResponse, participants, out var ratings))
            {
                if (ratings.Count > 0)
                {
                    var report = BuildRatingReport(ratings, contextMessages.Length);
                    await RespondWithMarkdownAsync(command, report, cancellationToken);
                }
                else
                {
                    await RespondWithMarkdownAsync(command, "🤷 LLM ответила корректно, но не хватило данных", cancellationToken);
                }
            }
            else
            {
                await RespondWithMarkdownAsync(command, "😞 Не удалось проанализировать сообщения", cancellationToken);
            }
        }
        catch (Exception ex)
        {
            typing.Stop();
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

    private async Task RespondWithMarkdownAsync(RatingCommand command, string text, CancellationToken cancellationToken)
    {
        var telegramMarkdown = _telegramMarkdownConverter.ConvertToSolidTelegramMarkdown(text);
        var response = await _bot.SendMessage(
            command.Message.Chat,
            telegramMarkdown,
            ParseMode.MarkdownV2,
            new()
            {
                MessageId = command.Message.MessageId
            },
            cancellationToken: cancellationToken);
        await _storage.StoreMessageAsync(response, command.Self, cancellationToken);
    }

    private static string BuildRatingReport(IReadOnlyList<ParticipantRating> ratings, int analyzedMessagesCount)
    {
        var builder = new StringBuilder();
        builder.AppendLine("🤡 **Рейтинг Щитпостеров** 🤡");
        builder.AppendLine();
        var top = ratings
            .OrderByDescending(static x => x.Level)
            .ThenBy(static x => x.Participant.Number)
            .Take(ReportTopCount)
            .ToList();
        for (var i = 0; i < top.Count; i++)
        {
            var rating = top[i];
            var rank = i + 1;
            var medal = rank switch
            {
                1 => "🥇",
                2 => "🥈",
                3 => "🥉",
                _ => "  "
            };
            builder.AppendLine(CultureInfo.InvariantCulture, $"{medal} #{rank}: `{rating.Participant.ReportName}`");
            builder.AppendLine(CultureInfo.InvariantCulture, $"   Степень кринжа: {rating.Level:D}/100");
            if (!string.IsNullOrEmpty(rating.Reason))
            {
                builder.AppendLine(CultureInfo.InvariantCulture, $"   Вердикт: {rating.Reason}");
            }

            builder.AppendLine();
        }

        builder.AppendLine();
        builder
            .Append("Проанализировано: ")
            .Append(analyzedMessagesCount.ToString(CultureInfo.InvariantCulture))
            .Append(' ')
            .AppendLine(PluralizeMessages(analyzedMessagesCount));

        return builder.ToString();
    }

    /// <summary>
    ///     Разбирает ответ модели: оценки сопоставляются с участниками по номеру, неизвестные
    ///     номера и повторы отбрасываются.
    /// </summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types")]
    private static bool TryDeserializeLlmResponse(
        string? rawResponse,
        RatingParticipant[] participants,
        [NotNullWhen(true)] out List<ParticipantRating>? ratings)
    {
        if (string.IsNullOrWhiteSpace(rawResponse))
        {
            ratings = null;
            return false;
        }

        try
        {
            var deserializedData = JsonSerializer.Deserialize<CringeRatingData?>(rawResponse, AIJsonUtilities.DefaultOptions);
            if (deserializedData?.Data is null)
            {
                ratings = null;
                return false;
            }

            var byNumber = participants.ToDictionary(static x => (long) x.Number);
            var rated = new HashSet<long>();
            var resultAccumulator = new List<ParticipantRating>();
            foreach (var rating in deserializedData.Data)
            {
                if (rating.Participant is not { } number
                    || rating.Level is not { } level
                    || level is < 0 or > 100
                    || !byNumber.TryGetValue(number, out var participant)
                    || !rated.Add(number))
                {
                    continue;
                }

                resultAccumulator.Add(new(participant, level, SanitizeReason(rating.Reason)));
            }

            ratings = resultAccumulator;
            return true;
        }
        catch (Exception)
        {
            ratings = null;
            return false;
        }
    }

    /// <summary>
    ///     Обоснование - одной строкой и не длиннее потолка: в отчёте оно стоит под оценкой.
    /// </summary>
    private static string? SanitizeReason(string? reason)
    {
        var line = ChatTranscriptFormat.Excerpt(reason);
        if (line.Length is 0)
        {
            return null;
        }

        return line.Length <= ReasonMaxLength
            ? line
            : string.Concat(line.AsSpan(0, ReasonMaxLength).TrimEnd(), "…");
    }

    private ChatMessage[] BuildContext(RatingParticipant[] participants, string transcript)
    {
        return
        [
            BuildSystemPrompt(),
            BuildUserPrompt(participants, transcript)
        ];
    }

    private ChatMessage BuildSystemPrompt()
    {
        var formattedDate = ChatTranscriptFormat.FormatTimestamp(_timeProvider.GetUtcNow());
        var botParticipant = ChatTranscriptFormat.FormatBotParticipant(_options.BotName);
        return new(
            ChatRole.System,
            $"""
             Ты - инструмент для анализа "уровня щитпостинга" в телеграм-чате. Ты не участник чата и ни на какие сообщения не отвечаешь: твоя задача - прочитать историю и оценить участников из списка.

             ## Как читать историю
             История идёт в хронологическом порядке, сообщения отделены друг от друга пустой строкой. Каждое сообщение начинается с заголовка:
             #4521 | 2026-10-09 12:34 | №3 Имя Фамилия (@username)
             Здесь #4521 - id сообщения, дальше дата и время по UTC, дальше автор. №3 - номер участника из списка, который дан перед историей. Сообщения без номера в заголовке и сообщения бота «{botParticipant}» оценивать не нужно - это только контекст.
             Кто написал сообщение, определяй только по заголовку: автор - тот, кто в нём указан, а не тот, кого упоминают в тексте.
             Между заголовком и текстом могут стоять служебные строки: «↳ в ответ на сообщение #id от участника ...: «начало сообщения»» означает, что это реплай, а строки в квадратных скобках - вложения с описанием того, что на них. Альбом из нескольких вложений показан одним сообщением с несколькими id в заголовке.

             ## Что считать кринжем
             Шкала от 0 до 100: 0 - серьёзное, качественное обсуждение, 50 - смесь серьёзного обсуждения и шуток, 100 - чистый щитпостинг (shitpost) и мемы.
             Признаки кринжа:
             - Бессмысленные или провокационные реплики
             - Спам похожими сообщениями
             - Спам эмодзи, капслок
             - Низкокачественный или тупой юмор, "угар", мемы (при этом качественный/остроумный юмор - это НЕ кринж)
             Оценивай участника только по его собственным сообщениям, с учётом того, кому и на что он отвечал. Чужие сообщения ему не приписывай.

             ## Формат ответа
             Ответ - JSON-объект с единственным свойством Data: массив, в котором ровно один элемент на каждого участника из списка - никого не пропускай и никого не добавляй. У каждого элемента три свойства, в таком порядке:
             - Participant - номер участника из списка (целое число, без знака №)
             - Reason - одна короткая фраза о том, за что такая оценка (до {ReasonMaxLength.ToString(CultureInfo.InvariantCulture)} символов)
             - Level - оценка от 0 до 100 (целое число)

             Текущая дата и время по UTC: `{formattedDate}`
             """);
    }

    private static ChatMessage BuildUserPrompt(RatingParticipant[] participants, string transcript)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Участники, которых нужно оценить:");
        foreach (var participant in participants)
        {
            builder
                .Append(participant.Label)
                .Append(" - ")
                .Append(participant.MessagesCount.ToString(CultureInfo.InvariantCulture))
                .Append(' ')
                .AppendLine(PluralizeMessages(participant.MessagesCount));
        }

        builder.AppendLine();
        builder.AppendLine("<history>");
        builder.AppendLine(transcript);
        builder.AppendLine("</history>");
        builder.AppendLine();
        builder.Append("Оцени участников ")
            .Append(participants[0].Label.Split(' ', 2)[0]);
        if (participants.Length > 1)
        {
            builder.Append('-').Append(participants[^1].Label.Split(' ', 2)[0]);
        }

        builder.Append(" по правилам из системного промпта и верни JSON.");
        return new(ChatRole.User, builder.ToString());
    }

    private static string PluralizeMessages(int count)
    {
        var lastTwoDigits = count % 100;
        var lastDigit = count % 10;
        if (lastTwoDigits is >= 11 and <= 14 || lastDigit is 0 or >= 5)
        {
            return "сообщений";
        }

        return lastDigit is 1 ? "сообщение" : "сообщения";
    }

    private sealed class CringeRatingData
    {
        public CringeRatingData(CringeRating[]? data)
        {
            Data = data;
        }

        public CringeRating[]? Data { get; }
    }

    private sealed class CringeRating
    {
        public CringeRating(long? participant, string? reason, long? level)
        {
            Participant = participant;
            Reason = reason;
            Level = level;
        }

        public long? Participant { get; }
        public string? Reason { get; }
        public long? Level { get; }
    }

    /// <summary>
    ///     Оценка участника, сопоставленная с ним по номеру.
    /// </summary>
    private sealed class ParticipantRating
    {
        public ParticipantRating(RatingParticipant participant, long level, string? reason)
        {
            Participant = participant;
            Level = level;
            Reason = reason;
        }

        public RatingParticipant Participant { get; }
        public long Level { get; }
        public string? Reason { get; }
    }
}
