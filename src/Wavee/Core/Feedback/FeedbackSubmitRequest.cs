using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Wavee.Core.Feedback;

/// <summary>
/// DTO sent from client to the Feedback API.
/// </summary>
public sealed record FeedbackSubmitRequest
{
    [JsonPropertyName("type")]
    public required FeedbackType Type { get; init; }

    [JsonPropertyName("severity")]
    public FeedbackSeverity Severity { get; init; } = FeedbackSeverity.Medium;

    [JsonPropertyName("reproducibility")]
    public FeedbackReproducibility Reproducibility { get; init; } = FeedbackReproducibility.NotApplicable;

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("body")]
    public required string Body { get; init; }

    [JsonPropertyName("appVersion")]
    public string AppVersion { get; init; } = "";

    [JsonPropertyName("osVersion")]
    public string OsVersion { get; init; } = "";

    [JsonPropertyName("deviceInfo")]
    public string DeviceInfo { get; init; } = "";

    [JsonPropertyName("includeDiagnostics")]
    public bool IncludeDiagnostics { get; init; }

    [JsonPropertyName("includeDeviceMetadata")]
    public bool IncludeDeviceMetadata { get; init; }

    [JsonPropertyName("isAnonymous")]
    public bool IsAnonymous { get; init; } = true;

    [JsonPropertyName("diagnosticsLog")]
    public string? DiagnosticsLog { get; init; }

    [JsonPropertyName("contactEmail")]
    public string? ContactEmail { get; init; }

    [JsonPropertyName("attachments")]
    public List<FeedbackAttachment>? Attachments { get; init; }
}
