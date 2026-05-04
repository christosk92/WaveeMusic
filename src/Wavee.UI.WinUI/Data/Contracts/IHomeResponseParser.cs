using System.Collections.Generic;
using Wavee.Core.Http.Pathfinder;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Data.Contracts;

/// <summary>
/// Versioned parser for Spotify's home feed API response.
/// Implementations handle different response formats (V1 content-based, V2 entity/trait-based).
/// </summary>
public interface IHomeResponseParser
{
    /// <summary>
    /// Returns true if this parser can handle the given response format.
    /// </summary>
    bool CanParse(HomeResponse response);

    /// <summary>
    /// Parses the response into sections and items ready for the UI.
    /// </summary>
    HomeParseResult Parse(HomeResponse response);
}

/// <summary>
/// The result of parsing a home feed response.
/// </summary>
public sealed record HomeParseResult(
    string? Greeting,
    List<HomeSection> Sections,
    List<HomeChipViewModel> Chips);
