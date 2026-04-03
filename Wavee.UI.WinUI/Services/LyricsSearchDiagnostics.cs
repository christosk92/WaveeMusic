using System;
using System.Collections.Generic;

namespace Wavee.UI.WinUI.Services;

public sealed class LyricsSearchDiagnostics
{
    public string? QueryTitle { get; init; }
    public string? QueryArtist { get; init; }
    public double QueryDurationMs { get; init; }
    public string? TrackId { get; init; }
    public List<ProviderDiagnostic> Providers { get; init; } = [];
    public string? SelectedProvider { get; init; }
    public string? SelectionReason { get; init; }
    public TimeSpan TotalSearchTime { get; init; }
}

public sealed class ProviderDiagnostic
{
    public string Name { get; init; } = "";
    public ProviderStatus Status { get; init; }
    public string? Error { get; init; }
    public int LineCount { get; init; }
    public bool HasSyllableSync { get; init; }
    public string? RawPreview { get; init; }
}

public enum ProviderStatus
{
    Success,
    NoResult,
    Timeout,
    Error
}
