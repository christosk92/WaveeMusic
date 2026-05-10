using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Wavee.Core.Http.RadioApollo;

public sealed record RadioApolloResponse(
    IReadOnlyList<RadioApolloTrack> Tracks,
    string? NextPageUrl,
    string? CorrelationId);

public sealed record RadioApolloTrack(
    string Uri,
    string? Uid,
    string? DecisionId);

internal sealed class RadioApolloRawResponse
{
    [JsonPropertyName("tracks")]
    public List<RadioApolloRawTrack>? Tracks { get; set; }

    [JsonPropertyName("next_page_url")]
    public string? NextPageUrl { get; set; }

    [JsonPropertyName("correlation_id")]
    public string? CorrelationId { get; set; }
}

internal sealed class RadioApolloRawTrack
{
    [JsonPropertyName("uri")]
    public string? Uri { get; set; }

    [JsonPropertyName("uid")]
    public string? Uid { get; set; }

    [JsonPropertyName("metadata")]
    public RadioApolloRawTrackMetadata? Metadata { get; set; }
}

internal sealed class RadioApolloRawTrackMetadata
{
    [JsonPropertyName("decision_id")]
    public string? DecisionId { get; set; }
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(RadioApolloRawResponse))]
internal partial class RadioApolloJsonContext : JsonSerializerContext;
