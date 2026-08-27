using System;
using System.Collections.Generic;

namespace TgLlmBot.Services.Media;

public class DefaultMediaRecognitionQueuesOptions
{
    public DefaultMediaRecognitionQueuesOptions(
        IReadOnlySet<long> chatIds,
        int capacityPerChat)
    {
        ArgumentNullException.ThrowIfNull(chatIds);
        if (chatIds.Count < 1)
        {
            throw new ArgumentException("Value should contain at least 1 element", nameof(chatIds));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(capacityPerChat, 1);
        ChatIds = chatIds;
        CapacityPerChat = capacityPerChat;
    }

    /// <summary>
    ///     Идентификаторы чатов, для которых создаются отдельные очереди.
    /// </summary>
    public IReadOnlySet<long> ChatIds { get; }

    /// <summary>
    ///     Максимальное количество ожидающих обработки заданий во фронте и в спине очереди одного чата.
    /// </summary>
    public int CapacityPerChat { get; }
}
