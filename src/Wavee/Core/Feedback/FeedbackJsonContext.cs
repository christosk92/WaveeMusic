using System.Text.Json.Serialization;

namespace Wavee.Core.Feedback;

[JsonSerializable(typeof(FeedbackSubmitRequest))]
[JsonSerializable(typeof(FeedbackSubmitResponse))]
[JsonSerializable(typeof(FeedbackAttachment))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
public sealed partial class FeedbackJsonContext : JsonSerializerContext;
