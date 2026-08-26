using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;
using TgLlmBot.CommandDispatcher.Abstractions;
using TgLlmBot.Services.Telegram.Markdown;

namespace TgLlmBot.Commands.Model;

public class ModelCommandHandler : AbstractCommandHandler<ModelCommand>
{
    private readonly TelegramBotClient _bot;
    private readonly string _response;

    public ModelCommandHandler(
        ModelCommandHandlerOptions options,
        TelegramBotClient bot,
        ITelegramMarkdownConverter markdownConverter)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(bot);
        ArgumentNullException.ThrowIfNull(markdownConverter);
        _response = BuildResponseTemplate(markdownConverter, options);
        _bot = bot;
    }

    public override async Task HandleAsync(ModelCommand command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(command);
        await _bot.SendMessage(
            command.Message.Chat,
            _response,
            ParseMode.MarkdownV2,
            new()
            {
                MessageId = command.Message.MessageId
            },
            cancellationToken: cancellationToken);
    }

    private static string BuildResponseTemplate(ITelegramMarkdownConverter markdownConverter, ModelCommandHandlerOptions options)
    {
        var builder = new StringBuilder();
        builder.Append("Провайдер: `").Append(GetBaseUrl(options.Endpoint)).AppendLine("`");
        builder.Append("Модель: `").Append(options.Model).AppendLine("`");
        builder.AppendLine();
        builder.Append("Провайдер (распознавание изображений): `").Append(GetBaseUrl(options.VisionEndpoint)).AppendLine("`");
        builder.Append("Модель (распознавание изображений): `").Append(options.VisionModel).AppendLine("`");
        var rawMarkdown = builder.ToString();
        var optimizedMarkdown = markdownConverter.ConvertToSolidTelegramMarkdown(rawMarkdown);
        return optimizedMarkdown;
    }

    private static string GetBaseUrl(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri)
        {
            throw new ArgumentException("URI должен быть абсолютным", nameof(uri));
        }

        return uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.Unescaped);
    }
}
