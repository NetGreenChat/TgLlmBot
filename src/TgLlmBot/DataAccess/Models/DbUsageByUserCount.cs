namespace TgLlmBot.DataAccess.Models;

public class DbUsageByUserCount
{
    public required long FromUserId { get; init; }

    public required long ChatId { get; init; }

    /// <summary>
    /// Проверять, чтобы оно было актуальным и обновлять
    /// </summary>
    public required string? FromUsername { get; set; }

    /// <summary>
    /// Проверять, чтобы оно было актуальным и обновлять
    /// </summary>
    public required string? FromFirstName { get; set; }

    /// <summary>
    /// Проверять, чтобы оно было актуальным и обновлять
    /// </summary>
    public required string? FromLastName { get; set; }

    /// <summary>
    /// Увеличивать каждый раз когда LLM отвечает юзеру
    /// </summary>
    public required long Count { get; set; }

    /// <summary>
    /// Увеличивать каждый раз когда LLM отвечает юзеру
    /// </summary>
    public required decimal CostInUsd { get; set; }
}
