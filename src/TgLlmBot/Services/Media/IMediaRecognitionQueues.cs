using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace TgLlmBot.Services.Media;

/// <summary>
///     Пул per-chat очередей распознавания вложений - по одной очереди на каждый разрешённый чат.
///     Внутри чата задания обрабатываются строго последовательно, разные чаты - параллельно.
/// </summary>
/// <remarks>
///     Каждая очередь состоит из двух частей: фронта (сообщения, требующие ответа) и спины
///     (история). Фронт всегда обрабатывается раньше спины.
/// </remarks>
public interface IMediaRecognitionQueues
{
    /// <summary>
    ///     Читатели очередей, сгруппированные по идентификатору чата. Читатель отдаёт задания
    ///     фронта строго раньше заданий спины.
    /// </summary>
    IReadOnlyDictionary<long, ChannelReader<MediaRecognitionJob>> Readers { get; }

    /// <summary>
    ///     Помещает задание в очередь чата, к которому оно относится.
    /// </summary>
    /// <remarks>
    ///     Задание с <see cref="MediaRecognitionJob.RequiresResponse" /> попадает во фронт, постановка
    ///     блокирующая: если фронт полон, вызов ждёт место (отменяется токеном отмены). Задания истории
    ///     попадают в спину, при переполнении - отбрасываются с логированием.
    /// </remarks>
    /// <returns>
    ///     <see langword="false" />, если для чата нет очереди, очередь завершена или задание
    ///     отброшено переполнившейся спиной.
    /// </returns>
    Task<bool> EnqueueAsync(long chatId, MediaRecognitionJob job, CancellationToken cancellationToken);

    /// <summary>
    ///     Завершает все очереди - новые задания больше не принимаются, уже поставленные будут дочитаны.
    /// </summary>
    void Complete();
}
