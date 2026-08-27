using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;
using TgLlmBot.CommandDispatcher.Abstractions;
using TgLlmBot.Services.Telegram.Markdown;

namespace TgLlmBot.Commands.DisplayHelp;

public class DisplayHelpCommandHandler : AbstractCommandHandler<DisplayHelpCommand>
{
    private readonly TelegramBotClient _bot;
    private readonly string _response;

    public DisplayHelpCommandHandler(
        DisplayHelpCommandHandlerOptions options,
        TelegramBotClient bot,
        ITelegramMarkdownConverter markdownConverter)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(bot);
        ArgumentNullException.ThrowIfNull(markdownConverter);
        _response = BuildHelpTemplate(markdownConverter, options.BotName);
        _bot = bot;
    }

    public override async Task HandleAsync(DisplayHelpCommand command, CancellationToken cancellationToken)
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

    private static string BuildHelpTemplate(ITelegramMarkdownConverter markdownConverter, string botName)
    {
        var builder = new StringBuilder();
        builder.Append('`').Append(botName).Append('`').AppendLine(" - префикс для того чтобы задать вопрос LLM");
        builder.AppendLine();
        builder.AppendLine("Например:");
        builder.Append('`').Append(botName).Append(" напиши Hello World на C#").Append('`').AppendLine();
        builder.AppendLine();
        builder.Append("Картинки, стикеры, гифки и видео бот тоже понимает: пришли вложение с подписью, начинающейся с `").Append(botName)
            .AppendLine("`, или ответь такой подписью на сообщение с ним");
        builder.AppendLine();
        builder.AppendLine(
            "Пачку картинок можно отправить одним альбомом - бот разглядит их все и запомнит, в каком порядке они шли. Анимированные стикеры, гифки и видео он смотрит целиком, по кадрам, а вот звука не слышит. Всё, что присылают в чат, он разглядывает и запоминает сам, так что спрашивать про вложение можно и потом");
        builder.AppendLine();
        builder.AppendLine();
        builder.AppendLine("Список команд:");
        builder.AppendLine();
        builder.AppendLine("* `!ping` - проверка работоспособности бота");
        builder.AppendLine("* `!model` - отображает текущую используемую LLM и endpoint к которому идут обращения");
        builder.AppendLine("* `!repo` - ссылка на GitHub репозиторий с исходным кодом бота");
        builder.AppendLine("* `!usage` - статистика использования API ключа");
        builder.AppendLine("* `!rating` - измеряет уровень кринжа и показывает рейтинг щитпостеров");
        builder.AppendLine("* `!chat_role` - модифицирует системный промпт по-умолчанию (для всего чата) произвольным образом (например: `!chat_role общайся на древнерусском`)");
        builder.AppendLine("* `!chat_role_reset` - сбрасывает системный промпт по-умолчанию (для всего чата) на стандартный");
        builder.AppendLine("* `!chat_role_show` - показывает текущий системный промпт по-умолчанию (для всего чата)");
        builder.AppendLine("* `!personal_role` - модифицирует системный промпт для общения конкретно с тобой произвольным образом (например: `!personal_role отвечай кратко`). Имеет приоритет над `!chat_role`");
        builder.AppendLine("* `!personal_role_reset` - сбрасывает системный промпт для общения конкретно с тобой на стандартный");
        builder.AppendLine("* `!personal_role_show` - показывает текущий системный для общения конкретно с тобой");
        builder.AppendLine(
            "* `!set_limit` - устанавливает пользователю лимит на общение с LLM (для этого нужно отправить эту команду реплаем на сообщение того, кому нужно установить лимит и указать количество сообщений, которое будет ему доступно в день; например: `!set_limit 5`)");
        var rawMarkdown = builder.ToString();
        var optimizedMarkdown = markdownConverter.ConvertToSolidTelegramMarkdown(rawMarkdown);
        return optimizedMarkdown;
    }
}
