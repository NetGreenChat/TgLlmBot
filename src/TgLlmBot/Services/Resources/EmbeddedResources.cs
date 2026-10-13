using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;

namespace TgLlmBot.Services.Resources;

public static class EmbeddedResources
{
    public static readonly byte[] NoJpg = ReadResource("no.jpg");
    public static readonly byte[] StopJpg = ReadResource("stop.jpg");
    public static readonly byte[] AlzJpg = ReadResource("alz.jpg");
    public static readonly byte[] HandsJpg = ReadResource("hands.jpg");

    [SuppressMessage("Style", "IDE0063:Use simple \'using\' statement")]
    [SuppressMessage("ReSharper", "ConvertToUsingDeclaration")]
    private static byte[] ReadResource(string name)
    {
        var resourceName = $"TgLlmBot.Resources.{name}";

        using (var resourceStream = typeof(EmbeddedResources).Assembly.GetManifestResourceStream(resourceName))
        {
            if (resourceStream is null)
            {
                throw new InvalidOperationException($"Can't read resource: {resourceName}");
            }

            using (var memoryStream = new MemoryStream())
            {
                resourceStream.CopyTo(memoryStream);
                memoryStream.Seek(0L, SeekOrigin.Begin);
                return memoryStream.ToArray();
            }
        }
    }
}
