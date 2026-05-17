namespace Wavee.UI.Models;

public sealed record DeviceCodeInfo(
    string UserCode,
    string VerificationUri,
    string? VerificationUriComplete,
    int ExpiresInSeconds);