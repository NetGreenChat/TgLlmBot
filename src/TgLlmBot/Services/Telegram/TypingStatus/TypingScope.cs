using System;
using System.Threading;

namespace TgLlmBot.Services.Telegram.TypingStatus;

/// <summary>
///     Одна просьба печатать в чате. Остановка гасит только её и допускает повторный вызов.
/// </summary>
/// <remarks>
///     Обработчики гасят печать перед отправкой ответа, а потом ещё раз в обработчиках ошибок -
///     повторная остановка здесь обязана быть безвредной, иначе лишний стоп списался бы с чужой
///     просьбы.
/// </remarks>
public sealed class TypingScope
{
    private Action? _stop;

    internal TypingScope(Action stop)
    {
        ArgumentNullException.ThrowIfNull(stop);
        _stop = stop;
    }

    public void Stop()
    {
        Interlocked.Exchange(ref _stop, null)?.Invoke();
    }
}
