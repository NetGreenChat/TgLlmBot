using TgLlmBot.DataAccess.Models;

namespace TgLlmBot.Services.Media;

/// <summary>
///     Как вложение называется в промптах основной модели.
/// </summary>
/// <remarks>
///     Названия общие для всех промптов - и истории чата, и запроса на ответ, и задания на
///     компактинг: одно и то же вложение не должно быть в одном месте "гифкой", а в другом "видео".
/// </remarks>
public static class MediaKindNames
{
    /// <summary>
    ///     Именительный падеж: "картинка", "анимированный стикер".
    /// </summary>
    public static string Describe(DbMediaKind kind, bool isAnimated)
    {
        return kind switch
        {
            DbMediaKind.Photo => "картинка",
            DbMediaKind.Sticker => isAnimated ? "анимированный стикер" : "стикер",
            DbMediaKind.Animation => "гифка",
            DbMediaKind.Video => "видео",
            _ => "вложение"
        };
    }

    /// <summary>
    ///     Родительный падеж: "картинки", "анимированного стикера".
    /// </summary>
    public static string DescribeGenitive(DbMediaKind kind, bool isAnimated)
    {
        return kind switch
        {
            DbMediaKind.Photo => "картинки",
            DbMediaKind.Sticker => isAnimated ? "анимированного стикера" : "стикера",
            DbMediaKind.Animation => "гифки",
            DbMediaKind.Video => "видео",
            _ => "вложения"
        };
    }

    /// <summary>
    ///     Формы для счётного оборота: "картинку", "3 картинки", "5 картинок".
    /// </summary>
    public static (string One, string Few, string Many) DescribeCountable(DbMediaKind? kind)
    {
        return kind switch
        {
            DbMediaKind.Photo => ("картинку", "картинки", "картинок"),
            DbMediaKind.Sticker => ("стикер", "стикера", "стикеров"),
            DbMediaKind.Animation => ("гифку", "гифки", "гифок"),
            DbMediaKind.Video => ("видео", "видео", "видео"),
            _ => ("вложение", "вложения", "вложений")
        };
    }
}
