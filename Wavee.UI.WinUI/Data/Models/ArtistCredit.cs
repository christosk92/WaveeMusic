namespace Wavee.UI.WinUI.Data.Models;

/// <summary>
/// A single artist credit (name + optional URI for navigation).
/// </summary>
public sealed record ArtistCredit(string Name, string? Uri);
