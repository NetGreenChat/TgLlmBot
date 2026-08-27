using System;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot.Types;
using TgLlmBot.DataAccess.Models;

namespace TgLlmBot.Services.DataAccess.TelegramMessages;

public interface ITelegramMessageStorage
{
    /// <summary>
    ///     Сохраняет сообщение вместе с метаданными его вложений (в состоянии
    ///     <see cref="DbMediaRecognitionStatus.Pending" /> - распознавание идёт отдельно и позже).
    /// </summary>
    /// <returns>Сохранённая строка истории.</returns>
    Task<DbChatMessage> StoreMessageAsync(
        Message message,
        User self,
        CancellationToken cancellationToken);

    /// <summary>
    ///     Возвращает историю чата до указанного сообщения по общему правилу
    ///     (200 сообщений или 30 000 символов бюджета), исключая само сообщение.
    /// </summary>
    Task<DbChatMessage[]> SelectContextMessagesAsync(
        Message message,
        CancellationToken cancellationToken);

    /// <summary>
    ///     Возвращает историю чата до сообщения с указанными идентификатором и датой по общему правилу
    ///     (200 сообщений или 30 000 символов бюджета), исключая само сообщение.
    /// </summary>
    /// <remarks>
    ///     Используется медиа-пайплайном при компактинге описаний: сжатое описание должно учитывать
    ///     контекст чата, который сложился до появления вложения.
    /// </remarks>
    Task<DbChatMessage[]> SelectContextMessagesBeforeAsync(
        long chatId,
        int messageId,
        DateTime date,
        CancellationToken cancellationToken);

    /// <summary>
    ///     Возвращает одно сообщение чата по его идентификатору.
    /// </summary>
    Task<DbChatMessage?> SelectMessageAsync(
        long chatId,
        int messageId,
        CancellationToken cancellationToken);

    /// <summary>
    ///     Возвращает все части альбома в порядке их отправки.
    /// </summary>
    Task<DbChatMessage[]> SelectMediaGroupMessagesAsync(
        long chatId,
        string mediaGroupId,
        CancellationToken cancellationToken);

    /// <summary>
    ///     Возвращает сообщения, вложения которых так и не доехали до конца обработки:
    ///     ещё не распознаны.
    /// </summary>
    /// <remarks>
    ///     Очередь распознавания живёт только в памяти, поэтому всё, что стояло в ней на момент
    ///     остановки, иначе висело бы недоделанным навсегда - подчистка ставит такие вложения
    ///     в очередь заново.
    /// </remarks>
    Task<DbChatMessage[]> SelectMessagesWithUnfinishedMediaAsync(
        CancellationToken cancellationToken);

    /// <summary>
    ///     Сохраняет результаты обработки вложений: сжатое описание и состояние.
    /// </summary>
    Task UpdateMediaAsync(
        DbChatMessageMedia[] media,
        CancellationToken cancellationToken);
}
