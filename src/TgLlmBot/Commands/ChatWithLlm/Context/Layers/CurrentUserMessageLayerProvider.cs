using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Telegram.Bot;
using Telegram.Bot.Types;
using TgLlmBot.Commands.ChatWithLlm.Services;

namespace TgLlmBot.Commands.ChatWithLlm.Context.Layers
{
    public sealed class CurrentUserMessageLayerProvider : ILlmContextLayerProvider
    {
        private readonly TelegramBotClient _bot;
        private readonly DefaultLlmChatHandlerOptions _options;

        public CurrentUserMessageLayerProvider(
            TelegramBotClient bot,
            DefaultLlmChatHandlerOptions options)
        {
            ArgumentNullException.ThrowIfNull(bot);
            ArgumentNullException.ThrowIfNull(options);

            _bot = bot;
            _options = options;
        }

        public async Task<IReadOnlyList<LlmContextLayer>> BuildLayersAsync(
            LlmContextBuildRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);

            var command = request.Command;
            var imageAttached = false;
            var resultContent = new List<AIContent>();

            var builder = new StringBuilder()
                .Append("Пользователь с FromUserId=")
                .Append(command.Message.From?.Id ?? 0)
                .Append(", FromUsername=@")
                .Append(command.Message.From?.Username?.Trim())
                .Append(", FromFirstName=")
                .Append(command.Message.From?.FirstName?.Trim())
                .Append(" и FromLastName=")
                .Append(command.Message.From?.LastName?.Trim());

            if (command.Message.ReplyToMessage is not null)
            {
                var text = command.Message.ReplyToMessage.Text?.Trim()
                           ?? command.Message.ReplyToMessage.Caption?.Trim();

                builder = builder
                    .Append(" сделал реплай на более раннее сообщение с MessageId=")
                    .Append(command.Message.ReplyToMessage.Id)
                    .Append(" (которое ");

                if (command.Message.ReplyToMessage.Photo?.Length > 0)
                {
                    var jpeg = await DownloadPhotoAsync(
                        command.Message.ReplyToMessage.Photo,
                        request.CancellationToken);

                    if (jpeg is not null)
                    {
                        resultContent.Add(new DataContent(jpeg, "image/jpeg"));
                        builder = builder.Append("содержало JPEG картинку и ");
                        imageAttached = true;
                    }
                }

                builder = builder
                    .Append("было отправлено пользователем с FromUserId=")
                    .Append(command.Message.ReplyToMessage.From!.Id)
                    .Append(", FromUsername=@")
                    .Append(command.Message.ReplyToMessage.From.Username?.Trim())
                    .Append(", FromFirstName=")
                    .Append(command.Message.ReplyToMessage.From.FirstName?.Trim())
                    .Append(", FromLastName=")
                    .Append(command.Message.ReplyToMessage.From.LastName?.Trim())
                    .Append(", Text=")
                    .Append(text)
                    .Append(')')
                    .Append(" и");
            }

            builder = builder
                .Append(" отправил тебе (")
                .Append(_options.BotName)
                .Append(", твой FromUserId=")
                .Append(command.Self.Id)
                .Append(", твой FromUsername=@")
                .Append(command.Self.Username?.Trim())
                .Append(") сообщение с MessageId=")
                .Append(command.Message.Id);

            if (command.Message.Photo?.Length > 0 && !imageAttached)
            {
                var jpeg = await DownloadPhotoAsync(
                    command.Message.Photo,
                    request.CancellationToken);

                if (jpeg is not null)
                {
                    resultContent.Add(new DataContent(jpeg, "image/jpeg"));
                    builder = builder.Append(", которое содержит JPEG картинку");
                }
            }

            builder = builder
                .Append(" и Text=")
                .Append(command.Prompt?.Trim());

            resultContent.Add(new TextContent(builder.ToString()));

            return
            [
                new LlmContextLayer
            {
                Id = "current-user-message",
                Stage = LlmContextStage.CurrentRequest,
                Role = ChatRole.User,
                Contents = resultContent,
                IsRequired = true
            }
            ];
        }

        private async Task<byte[]?> DownloadPhotoAsync(
            PhotoSize[] photo,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var photoSize = SelectPhotoSizeForLlm(photo);
            if (photoSize is null)
            {
                return null;
            }

            var tgPhoto = await _bot.GetFile(photoSize.FileId, cancellationToken);

            if (tgPhoto is not null
                && !string.IsNullOrEmpty(tgPhoto.FilePath)
                && tgPhoto.FileSize.HasValue)
            {
                await using var memoryStream = new MemoryStream();
                await _bot.DownloadFile(tgPhoto.FilePath, memoryStream, cancellationToken);

                var downloadedImageBytes = memoryStream.ToArray();
                if (downloadedImageBytes.Length < 3)
                {
                    return null;
                }

                if (downloadedImageBytes[0] == 0xff
                    && downloadedImageBytes[1] == 0xd8
                    && downloadedImageBytes[2] == 0xff)
                {
                    return downloadedImageBytes;
                }
            }

            return null;
        }

        private static PhotoSize? SelectPhotoSizeForLlm(PhotoSize[] photo)
        {
            var photoSize = photo.MaxBy(x => x.Width);
            if (photoSize is null)
            {
                return null;
            }

            if (photoSize.Width > photoSize.Height)
            {
                return photoSize;
            }

            return photo.MaxBy(x => x.Height);
        }
    }
}
