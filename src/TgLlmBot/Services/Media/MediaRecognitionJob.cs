using System;
using Telegram.Bot.Types;
using TgLlmBot.Commands.ChatWithLlm;
using TgLlmBot.DataAccess.Models;

namespace TgLlmBot.Services.Media;

/// <summary>
///     Задание на распознавание вложений одного логического сообщения.
/// </summary>
/// <remarks>
///     Для сообщений, требующих ответа, задание несёт готовую команду (<see cref="Command" />):
///     после распознавания и компактинга воркер поставит её в per-chat LLM-очередь.
/// </remarks>
public sealed class MediaRecognitionJob
{
    public MediaRecognitionJob(
        Message? message,
        DbChatMessage storedMessage,
        string? relatedText,
        bool requiresResponse,
        ChatWithLlmCommand? command)
    {
        ArgumentNullException.ThrowIfNull(storedMessage);
        if (requiresResponse != (command is not null))
        {
            throw new ArgumentException("Command must be provided if and only if RequiresResponse is true.", nameof(command));
        }

        Message = message;
        StoredMessage = storedMessage;
        RelatedText = relatedText;
        RequiresResponse = requiresResponse;
        Command = command;
    }

    /// <summary>
    ///     Восстанавливает задание из строки истории для подчистки: оригинального сообщения
    ///     Telegram больше нет, поэтому ответ по нему не восстанавливается.
    /// </summary>
    public static MediaRecognitionJob FromStoredMessage(DbChatMessage storedMessage)
    {
        ArgumentNullException.ThrowIfNull(storedMessage);
        return new(
            message: null,
            storedMessage,
            storedMessage.Caption ?? storedMessage.Text,
            requiresResponse: false,
            command: null);
    }

    public long ChatId => StoredMessage.ChatId;

    public int MessageId => StoredMessage.MessageId;

    /// <summary>
    ///     Оригинальное сообщение Telegram. Есть у заданий из диспетчера и нет у восстановленных
    ///     подчисткой из базы.
    /// </summary>
    public Message? Message { get; }

    /// <summary>
    ///     Сохранённая строка истории с коллекцией вложений - источник вложений для распознавания.
    /// </summary>
    public DbChatMessage StoredMessage { get; }

    /// <summary>
    ///     Текст, с которым вложение пришло в чат (подпись к картинке).
    /// </summary>
    public string? RelatedText { get; }

    /// <summary>
    ///     Сообщение требует ответа: задание уходит в начало очереди, а после обработки
    ///     команда продолжает обычный flow в LLM-очереди.
    /// </summary>
    public bool RequiresResponse { get; }

    /// <summary>
    ///     Готовая команда для континуации ответа. Не <see langword="null" /> тогда и только тогда,
    ///     когда <see cref="RequiresResponse" />.
    /// </summary>
    public ChatWithLlmCommand? Command { get; }
}
