using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using TgLlmBot.DataAccess.Models;
using TgLlmBot.Models;
using TgLlmBot.Services.Media;

namespace TgLlmBot.Services.Llm.Compression;

public partial class DefaultMediaDescriptionCompressor : IMediaDescriptionCompressor
{
    private const int MaxOutputTokens = 4096;

    private readonly IChatClient _chatClient;
    private readonly ILogger<DefaultMediaDescriptionCompressor> _logger;

    public DefaultMediaDescriptionCompressor(
        IChatClient chatClient,
        ILogger<DefaultMediaDescriptionCompressor> logger)
    {
        ArgumentNullException.ThrowIfNull(chatClient);
        ArgumentNullException.ThrowIfNull(logger);
        _chatClient = chatClient;
        _logger = logger;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types")]
    public async Task<Result<string>> CompressAsync(MediaCompressionRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);

        // Короткое описание жать незачем: оно и так влезает в историю
        if (request.FullDescription.Length <= MediaDescriptionLimits.ShortMaxLength)
        {
            return Result<string>.Success(request.FullDescription);
        }

        var context = new List<ChatMessage>
        {
            new(ChatRole.System, BuildSystemPrompt()),
            new(ChatRole.User, BuildUserPrompt(request))
        };
        var chatOptions = new ChatOptions
        {
            MaxOutputTokens = MaxOutputTokens,
            // Инструменты не отдаём: задача чисто текстовая, лазить в интернет за ней некуда
            RawRepresentationFactory = static _ => LlmRawRequestFactory.CreateChatCompletionOptions()
        };
        try
        {
            var response = await _chatClient.GetResponseAsync(context, chatOptions, cancellationToken);
            var compressed = response.Text.Trim();
            if (string.IsNullOrEmpty(compressed))
            {
                Log.EmptyCompression(_logger, request.FullDescription.Length);
                return Result<string>.Fail();
            }

            Log.DescriptionCompressed(_logger, request.FullDescription.Length, compressed.Length);
            return Result<string>.Success(compressed);
        }
        catch (Exception ex)
        {
            Log.CompressionFailed(_logger, request.FullDescription.Length, ex);
            return Result<string>.Fail();
        }
    }

    private static string BuildSystemPrompt()
    {
        var maxLength = MediaDescriptionLimits.ShortMaxLength.ToString(CultureInfo.InvariantCulture);
        return $"""
                Ты сжимаешь описание вложения из группового чата. Сжатое описание останется в истории переписки
                надолго и будет единственным, что ты про это вложение помнишь, - подробного описания больше не будет.

                Обязательно сохрани:
                * что на вложении изображено и что там происходит
                * дословно текст, который на нём виден, если он несёт смысл: надписи мема, заголовки, имена, числа, названия
                * детали, важные в контексте истории чата, присланной ниже

                Смело выкидывай:
                * перечисление второстепенных предметов и деталей фона
                * описание цветов, освещения, стиля рисунка и композиции, если обсуждали не их

                Уложись в {maxLength} символов, лучше меньше.
                Пиши одним абзацем простым текстом, без Markdown, без заголовков и без вступлений вроде "на изображении".
                Ничего не добавляй от себя: в сжатом описании может быть только то, что было в подробном описании.

                История чата нужна тебе ровно для одного - понять, какие детали вложения важно сохранить.
                Не пересказывай её, не отвечай на неё и не упоминай, кто что писал: история и так лежит в чате отдельно,
                и дублировать её в описании вложения - только зря занимать место. Описывай только само вложение.
                В ответе не должно быть ничего, кроме самого сжатого описания.
                """;
    }

    private static string BuildUserPrompt(MediaCompressionRequest request)
    {
        var kind = request.Kind switch
        {
            DbMediaKind.Sticker when request.IsAnimated => "анимированного стикера",
            DbMediaKind.Sticker => "стикера",
            _ => "картинки"
        };
        var builder = new StringBuilder()
            .Append("Вот подробное описание ")
            .Append(kind)
            .AppendLine(", присланного в чат:")
            .AppendLine("<description>")
            .AppendLine(request.FullDescription)
            .AppendLine("</description>");

        var attachedText = request.AttachedText?.Trim();
        if (!string.IsNullOrEmpty(attachedText))
        {
            builder
                .AppendLine()
                .AppendLine("Вложение пришло в чат с таким текстом:")
                .AppendLine("<caption>")
                .AppendLine(attachedText)
                .AppendLine("</caption>");
        }

        var historyContext = request.HistoryContext?.Trim();
        if (!string.IsNullOrEmpty(historyContext))
        {
            builder
                .AppendLine()
                .AppendLine("Вот история чата до этого сообщения (JSON, по общему правилу: 200 сообщений или 30 000 символов):")
                .AppendLine("<history>")
                .AppendLine(historyContext)
                .AppendLine("</history>")
                .AppendLine()
                // Запрет держится заметно лучше, когда стоит вплотную к самому заданию,
                // а не только в системном промпте
                .AppendLine("История нужна тебе только как подсказка, какие детали вложения важно сохранить.")
                .AppendLine("В сжатом описании не должно быть ни слова о ней: ни \"в чате\", ни имён,")
                .AppendLine("ни пересказа чьих-либо реплик. Только то, что есть на самом вложении.");
        }

        return builder
            .AppendLine()
            .Append("Сожми описание.")
            .ToString();
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "Compressed a media description from {FullLength} to {CompressedLength} characters")]
        public static partial void DescriptionCompressed(ILogger logger, int fullLength, int compressedLength);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Main model returned an empty compression for a description of {FullLength} characters")]
        public static partial void EmptyCompression(ILogger logger, int fullLength);

        [LoggerMessage(Level = LogLevel.Error, Message = "Failed to compress a media description of {FullLength} characters")]
        public static partial void CompressionFailed(ILogger logger, int fullLength, Exception exception);
    }
}
