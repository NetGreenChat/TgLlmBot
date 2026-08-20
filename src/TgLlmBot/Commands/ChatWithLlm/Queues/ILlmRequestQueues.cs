using System.Collections.Generic;
using System.Threading.Channels;

namespace TgLlmBot.Commands.ChatWithLlm.Queues;

/// <summary>
///     Набор независимых очередей LLM-запросов - по одной на каждый разрешённый чат.
///     Запросы внутри одного чата обрабатываются последовательно, разные чаты - параллельно.
/// </summary>
public interface ILlmRequestQueues
{
    /// <summary>
    ///     Читатели очередей, сгруппированные по идентификатору чата.
    /// </summary>
    IReadOnlyDictionary<long, ChannelReader<ChatWithLlmCommand>> Readers { get; }

    /// <summary>
    ///     Помещает запрос в очередь чата, к которому он относится.
    /// </summary>
    /// <returns>
    ///     <see langword="false" />, если для чата нет очереди (чат не разрешён) или очередь уже завершена.
    /// </returns>
    bool TryEnqueue(long chatId, ChatWithLlmCommand command);

    /// <summary>
    ///     Завершает все очереди - новые запросы больше не принимаются, уже поставленные будут дочитаны.
    /// </summary>
    void Complete();
}
