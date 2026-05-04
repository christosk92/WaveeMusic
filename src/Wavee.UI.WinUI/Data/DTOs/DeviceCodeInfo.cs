namespace Wavee.UI.WinUI.Data.DTOs;

public sealed record DeviceCodeInfo(
    string UserCode,
    string VerificationUri,
    string? VerificationUriComplete,
    int ExpiresInSeconds);
