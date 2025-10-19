using System.Text.Json.Serialization;

namespace Wavee.Core.Session;

/// <summary>
/// JSON serializer context for Session types with Native AOT support.
/// Uses source generators to eliminate reflection at runtime.
/// </summary>
[JsonSerializable(typeof(Wavee.Core.Session.ApResolver.ApResolveResponse))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    GenerationMode = JsonSourceGenerationMode.Default)]
internal partial class SessionJsonSerializerContext : JsonSerializerContext
{
}
