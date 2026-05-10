using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Wavee.Core.Http.InspiredByMix;

internal sealed class InspiredByMixRawResponse
{
    [JsonPropertyName("total")]
    public int Total { get; set; }

    [JsonPropertyName("mediaItems")]
    public List<InspiredByMixRawItem>? MediaItems { get; set; }
}

internal sealed class InspiredByMixRawItem
{
    [JsonPropertyName("uri")]
    public string? Uri { get; set; }
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(InspiredByMixRawResponse))]
internal partial class InspiredByMixJsonContext : JsonSerializerContext;
