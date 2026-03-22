using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Wavee.Core.Http.Pathfinder;

public sealed record ExtractedColorsResponse
{
    [JsonPropertyName("data")]
    public ExtractedColorsData? Data { get; init; }
}

public sealed record ExtractedColorsData
{
    [JsonPropertyName("extractedColors")]
    public IReadOnlyList<ExtractedColorEntry>? ExtractedColors { get; init; }
}

public sealed record ExtractedColorEntry
{
    [JsonPropertyName("colorDark")]
    public ColorValue? ColorDark { get; init; }

    [JsonPropertyName("colorLight")]
    public ColorValue? ColorLight { get; init; }

    [JsonPropertyName("colorRaw")]
    public ColorValue? ColorRaw { get; init; }
}

public sealed record ColorValue
{
    [JsonPropertyName("hex")]
    public string? Hex { get; init; }

    [JsonPropertyName("isFallback")]
    public bool? IsFallback { get; init; }
}

// Simplified result for consumers
public sealed record ExtractedColor(string? DarkHex, string? LightHex, string? RawHex);

[JsonSerializable(typeof(ExtractedColorsResponse))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal partial class ExtractedColorsJsonContext : JsonSerializerContext { }
