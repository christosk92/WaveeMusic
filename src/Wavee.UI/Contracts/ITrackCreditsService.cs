using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Wavee.UI.Contracts;

/// <summary>
/// Service for fetching track credits with de-duplicated contributors and resolved artist images.
/// </summary>
public interface ITrackCreditsService
{
    Task<TrackCreditsResult> GetCreditsAsync(string trackUri, CancellationToken ct = default);
}

// ── Domain result types ──

[global::WinRT.GeneratedBindableCustomProperty]
public sealed partial record TrackCreditsResult
{
    public required List<CreditGroupResult> Groups { get; init; }
    public string? RecordLabel { get; init; }
}

[global::WinRT.GeneratedBindableCustomProperty]
public sealed partial record CreditGroupResult
{
    public required string RoleName { get; init; }
    public required List<CreditContributorResult> Contributors { get; init; }
}

[global::WinRT.GeneratedBindableCustomProperty]
public sealed partial record CreditContributorResult
{
    public string? Name { get; init; }
    public string? ArtistUri { get; init; }
    public string? ImageUrl { get; set; }
    public required List<string> Roles { get; init; }
    public string RolesText => string.Join(", ", Roles);
}