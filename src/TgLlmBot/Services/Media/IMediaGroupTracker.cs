using System.Threading;
using System.Threading.Tasks;

namespace TgLlmBot.Services.Media;

/// <summary>
///     Следит за тем, когда альбом приехал целиком.
/// </summary>
/// <remarks>
///     Пачку картинок Telegram разбирает на отдельные сообщения с общим MediaGroupId и отдаёт их
///     хоть и подряд, но не одномоментно. Подпись при этом лежит только на одной из частей, поэтому
///     запрос к LLM стартует раньше, чем приедет остаток альбома. Трекер даёт дождаться тишины.
/// </remarks>
public interface IMediaGroupTracker
{
    /// <summary>
    ///     Отмечает, что приехала очередная часть альбома.
    /// </summary>
    void Register(long chatId, string mediaGroupId);

    /// <summary>
    ///     Ждёт, пока новые части альбома перестанут приходить.
    /// </summary>
    Task WaitForSettleAsync(long chatId, string mediaGroupId, CancellationToken cancellationToken);

    /// <summary>
    ///     Разрешает запустить запрос к LLM по альбому и только по первой его части.
    /// </summary>
    /// <remarks>
    ///     Условию "адресовано боту" удовлетворяет каждая часть альбома (реплай проставлен на всех),
    ///     поэтому без этой проверки на пачку из пяти картинок прилетело бы пять ответов.
    /// </remarks>
    /// <returns><see langword="false" />, если по этому альбому запрос уже запускали.</returns>
    bool TryBeginLlmRequest(long chatId, string mediaGroupId);
}
