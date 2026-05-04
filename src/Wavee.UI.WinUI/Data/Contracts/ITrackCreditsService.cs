using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Wavee.UI.WinUI.Data.Contracts;

/// <summary>
/// Service for fetching track credits with de-duplicated contributors and resolved artist images.
/// </summary>
public interface ITrackCreditsService
{
    Task<TrackCreditsResult> GetCreditsAsync(string trackUri, CancellationToken ct = default);
}

// ── Domain result types ──

public sealed record TrackCreditsResult
{
    public required List<CreditGroupResult> Groups { get; init; }
    public string? RecordLabel { get; init; }
}

public sealed record CreditGroupResult
{
    public required string RoleName { get; init; }
    public required List<CreditContributorResult> Contributors { get; init; }
}

public sealed record CreditContributorResult
{
    public string? Name { get; init; }
    public string? ArtistUri { get; init; }
    public string? ImageUrl { get; set; }
    public required List<string> Roles { get; init; }
    public string RolesText => string.Join(", ", Roles);
}
