using System;
using System.Collections.Generic;

namespace TgLlmBot.Services.Telegram.TypingStatus;

public class DefaultTypingStatusQueuesOptions
{
    public DefaultTypingStatusQueuesOptions(IReadOnlySet<long> chatIds)
    {
        ArgumentNullException.ThrowIfNull(chatIds);
        if (chatIds.Count < 1)
        {
            throw new ArgumentException("Value should contain at least 1 element", nameof(chatIds));
        }

        ChatIds = chatIds;
    }

    /// <summary>
    ///     Идентификаторы чатов, для которых создаются отдельные очереди.
    /// </summary>
    public IReadOnlySet<long> ChatIds { get; }
}
