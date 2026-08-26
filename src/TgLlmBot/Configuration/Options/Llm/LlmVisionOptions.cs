using System.ComponentModel.DataAnnotations;

namespace TgLlmBot.Configuration.Options.Llm;

public class LlmVisionOptions
{
    [Required]
    [MaxLength(10000)]
    [Url]
    public string Endpoint { get; set; } = default!;

    [Required]
    [MaxLength(10000)]
    public string ApiKey { get; set; } = default!;

    [Required]
    [MaxLength(10000)]
    public string Model { get; set; } = default!;
}
