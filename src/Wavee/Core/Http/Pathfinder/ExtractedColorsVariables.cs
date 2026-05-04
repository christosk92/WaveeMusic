using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Wavee.Core.Http.Pathfinder;

/// <summary>
/// Variables for the fetchExtractedColors GraphQL query.
/// </summary>
public sealed record ExtractedColorsVariables
{
    [JsonPropertyName("imageUris")]
    public IReadOnlyList<string> ImageUris { get; init; } = [];
}
