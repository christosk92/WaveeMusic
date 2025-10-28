using System.Text.Json.Serialization;

namespace Wavee.Core.Authentication;

/// <summary>
/// JSON serializer context for Authentication types with Native AOT support.
/// Uses source generators to eliminate reflection at runtime.
/// </summary>
[JsonSerializable(typeof(Credentials))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    GenerationMode = JsonSourceGenerationMode.Metadata | JsonSourceGenerationMode.Serialization,
    IncludeFields = false,
    Converters = [typeof(Base64ByteArrayConverter)])]
internal partial class AuthenticationJsonSerializerContext : JsonSerializerContext
{
}
