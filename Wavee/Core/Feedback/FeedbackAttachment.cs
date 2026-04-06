using System.Text.Json.Serialization;

namespace Wavee.Core.Feedback;

public sealed record FeedbackAttachment
{
    [JsonPropertyName("fileName")]
    public required string FileName { get; init; }

    [JsonPropertyName("contentType")]
    public required string ContentType { get; init; }

    [JsonPropertyName("base64Data")]
    public required string Base64Data { get; init; }
}
