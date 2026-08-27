namespace TgLlmBot.Services.Media;

/// <summary>
///     Чем подготовленное вложение является для vision-модели.
/// </summary>
public enum PreparedMediaKind
{
    /// <summary>
    ///     Одна статическая картинка.
    /// </summary>
    Image = 1,

    /// <summary>
    ///     Файл видео целиком: на кадры его разложит уже сервер модели.
    /// </summary>
    VideoFile = 2,

    /// <summary>
    ///     Цепочка кадров, отрендеренных из анимации у себя.
    /// </summary>
    RenderedFrames = 3
}
