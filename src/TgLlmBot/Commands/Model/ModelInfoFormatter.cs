using System;
using System.Text;
using System.Text.Json;

namespace TgLlmBot.Commands.Model;

public static class ModelInfoFormatter
{
    public const string ToolName = "get_model_info";

    public const string ToolHelp = "Возвращает JSON с провайдером, идентификатором модели и её возможностями. Используй этот инструмент, если пользователь спрашивает, на какой модели ты работаешь.";

    public const string ToolServiceKey = "model-info-tool";

    public static string Build(ModelCommandHandlerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var builder = new StringBuilder();
        builder.Append("Провайдер: `").Append(GetBaseUrl(options.Endpoint)).AppendLine("`");
        builder.Append("Модель: `").Append(options.Model).AppendLine("`");
        builder.AppendLine();
        builder.AppendLine("Возможности модели:");
        builder.Append("Картинки: `").Append(options.Image).AppendLine("`");
        builder.Append("Видео: `").Append(options.Video).AppendLine("`");
        builder.Append("Аудио: `").Append(options.Audio).AppendLine("`");
        return builder.ToString();
    }

    public static string BuildToolResponse(ModelCommandHandlerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return JsonSerializer.Serialize(new
        {
            provider = GetBaseUrl(options.Endpoint),
            model = options.Model,
            capabilities = new
            {
                image = options.Image,
                video = options.Video,
                audio = options.Audio
            }
        });
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
