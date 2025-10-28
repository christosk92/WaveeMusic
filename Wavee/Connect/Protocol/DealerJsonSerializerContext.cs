using System.Text.Json.Serialization;

namespace Wavee.Connect.Protocol;

/// <summary>
/// JSON serializer context for dealer protocol messages.
/// Enables Native AOT compatibility.
/// </summary>
[JsonSerializable(typeof(RawDealerMessage))]
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class DealerJsonSerializerContext : JsonSerializerContext
{
}
