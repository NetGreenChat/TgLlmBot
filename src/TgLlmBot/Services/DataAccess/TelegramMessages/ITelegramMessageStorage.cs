using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot.Types;
using TgLlmBot.DataAccess.Models;
using TgLlmBot.Services.Llm;

namespace TgLlmBot.Services.DataAccess.TelegramMessages;

public interface ITelegramMessageStorage
{
    /// <summary>
    ///     Сохраняет сообщение вместе с метаданными его вложений (в состоянии
    ///     <see cref="DbMediaRecognitionStatus.Pending" /> - распознавание идёт отдельно и позже).
    /// </summary>
    /// <remarks>
    ///     Сообщение помечается как написанное без дополнительных просьб к системному промпту:
    ///     это верно и для сообщений пользователей, и для служебных ответов команд.
    /// </remarks>
    /// <returns>Сохранённая строка истории.</returns>
    Task<DbChatMessage> StoreMessageAsync(
        Message message,
        User self,
        CancellationToken cancellationToken);

    /// <summary>
    ///     Сохраняет ответ бота вместе с пометкой о том, под какой дополнительной просьбой
    ///     к системному промпту он был сгенерирован.
    /// </summary>
    /// <remarks>
    ///     Пометка нужна, чтобы разовая стилевая просьба одного пользователя не растекалась через
    ///     историю чата на ответы остальным: читая историю, модель по ней отличает свои ответы
    ///     под чужой просьбой от ответов в обычном стиле.
    /// </remarks>
    /// <returns>Сохранённая строка истории.</returns>
    Task<DbChatMessage> StoreMessageAsync(
        Message message,
        User self,
        AppliedCustomPrompt customPrompt,
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
    ///     Используется медиа-пайплайном при описании вложений: описание должно учитывать
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
    ///     Возвращает сообщения чата по их идентификаторам вместе с вложениями, в хронологическом
    ///     порядке. Чего в базе нет, того в результате не будет; сообщения выгнанных участников
    ///     не возвращаются, как и в выборке истории.
    /// </summary>
    /// <remarks>
    ///     Нужен ради цитат в строках реплаев: реплай из истории может указывать на сообщение,
    ///     которое в окно истории уже не попало, а модели важно видеть, на что отвечали.
    /// </remarks>
    Task<DbChatMessage[]> SelectMessagesAsync(
        long chatId,
        IReadOnlyCollection<int> messageIds,
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
