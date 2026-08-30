using System;
using System.Diagnostics.CodeAnalysis;
using TgLlmBot.DataAccess.Models;

namespace TgLlmBot.Services.Llm;

/// <summary>
///     Дополнительная часть системного промпта, под которой формируется конкретный ответ бота:
///     персональная просьба автора запроса, просьба на весь чат или ничего.
/// </summary>
/// <remarks>
///     Просьба действует ровно на тот ответ, ради которого её подмешали в системный промпт,
///     а сам ответ остаётся в истории чата. Поэтому вместе с ответом сохраняется и то, откуда
///     взялся его стиль (<see cref="DbChatMessage.CustomPromptScope" />) - иначе модель, читая
///     историю при ответе другому пользователю, принимает чужую разовую стилистику за свою
///     собственную и продолжает в ней отвечать.
/// </remarks>
public sealed class AppliedCustomPrompt
{
    /// <summary>
    ///     Дополнительных просьб нет: ответ формируется в обычном стиле бота.
    /// </summary>
    public static readonly AppliedCustomPrompt None = new(DbCustomPromptScope.None, null, null);

    private AppliedCustomPrompt(DbCustomPromptScope scope, long? userId, string? prompt)
    {
        Scope = scope;
        UserId = userId;
        Prompt = prompt;
    }

    public DbCustomPromptScope Scope { get; }

    /// <summary>
    ///     Автор персональной просьбы. Заполнен только при <see cref="DbCustomPromptScope.Personal" />.
    /// </summary>
    public long? UserId { get; }

    /// <summary>
    ///     Текст просьбы. Заполнен, когда <see cref="IsApplied" />.
    /// </summary>
    public string? Prompt { get; }

    [MemberNotNullWhen(true, nameof(Prompt))]
    public bool IsApplied => Scope is not DbCustomPromptScope.None;

    /// <summary>
    ///     Просьба, заданная на весь чат (<c>!role</c>).
    /// </summary>
    public static AppliedCustomPrompt ForChat(string prompt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        return new(DbCustomPromptScope.Chat, null, prompt);
    }

    /// <summary>
    ///     Персональная просьба одного пользователя (<c>!personal_role</c>).
    /// </summary>
    public static AppliedCustomPrompt ForUser(long userId, string prompt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        return new(DbCustomPromptScope.Personal, userId, prompt);
    }
}
