using System;

namespace TgLlmBot.Commands.Model;

public class ModelCommandHandlerOptions
{
    public ModelCommandHandlerOptions(Uri endpoint, string model, Uri visionEndpoint, string visionModel)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(visionEndpoint);
        if (string.IsNullOrWhiteSpace(model))
        {
            throw new ArgumentException("Value cannot be null or whitespace.", nameof(model));
        }

        if (string.IsNullOrWhiteSpace(visionModel))
        {
            throw new ArgumentException("Value cannot be null or whitespace.", nameof(visionModel));
        }

        Endpoint = endpoint;
        Model = model;
        VisionEndpoint = visionEndpoint;
        VisionModel = visionModel;
    }

    public Uri Endpoint { get; }
    public string Model { get; }
    public Uri VisionEndpoint { get; }
    public string VisionModel { get; }
}
