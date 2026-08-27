using System;

namespace TgLlmBot.Services.DataAccess.MediaDescriptions;

/// <summary>
///     Описание вложения, найденное в кэше, вместе с тем, насколько оно окончательное.
/// </summary>
public sealed class CachedMediaDescription
{
    public CachedMediaDescription(string description, bool isFallback)
    {
        ArgumentException.ThrowIfNullOrEmpty(description);
        Description = description;
        IsFallback = isFallback;
    }

    /// <summary>
    ///     Само описание.
    /// </summary>
    public string Description { get; }

    /// <summary>
    ///     Описание снято со статического превью, а не с самого файла. Разглядеть файл стоит
    ///     ещё раз: получится - описание в кэше заменится на полноценное.
    /// </summary>
    public bool IsFallback { get; }
}
