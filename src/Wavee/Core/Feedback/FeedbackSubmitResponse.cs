using System.Text.Json.Serialization;

namespace Wavee.Core.Feedback;

/// <summary>
/// DTO returned from the Feedback API after submission.
/// </summary>
public sealed record FeedbackSubmitResponse
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }
}
