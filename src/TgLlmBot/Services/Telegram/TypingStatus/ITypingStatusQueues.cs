using System.Collections.Generic;
using System.Threading.Channels;

namespace TgLlmBot.Services.Telegram.TypingStatus;

/// <summary>
///     Набор независимых очередей команд статуса "печатает" - по одной на каждый разрешённый чат.
/// </summary>
/// <remarks>
///     Очередь на чат нужна по двум причинам. Внутри чата важен порядок: включение и выключение
///     ходят парами, и выключение, обогнавшее своё включение, оставляет чат печатающим навсегда.
///     Между чатами порядок не важен вовсе, а вот независимость важна: разбор команды одного чата
///     дожидается отмены его цикла печати и не должен придерживать остальные чаты.
/// </remarks>
public interface ITypingStatusQueues
{
    /// <summary>
    ///     Читатели очередей, сгруппированные по идентификатору чата.
    /// </summary>
    IReadOnlyDictionary<long, ChannelReader<TypingCommand>> Readers { get; }

    /// <summary>
    ///     Помещает команду в очередь её чата.
    /// </summary>
    /// <returns>
    ///     <see langword="false" />, если для чата нет очереди (чат не разрешён) или очередь уже завершена.
    /// </returns>
    bool TryEnqueue(long chatId, TypingCommand command);

    /// <summary>
    ///     Завершает все очереди - новые команды больше не принимаются, уже поставленные будут дочитаны.
    /// </summary>
    void Complete();
}
