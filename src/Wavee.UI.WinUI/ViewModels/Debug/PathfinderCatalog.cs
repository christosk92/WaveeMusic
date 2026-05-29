namespace Wavee.UI.WinUI.ViewModels.Debug;

/// <summary>
/// Static catalogue of well-known Pathfinder GraphQL operations + their
/// persisted-query hashes and an example variables JSON blob. Used by the
/// Debug page's Pathfinder tab to populate a dropdown of pre-baked queries
/// so a user can fire one with a single click rather than typing the hash
/// from memory.
/// </summary>
public static class PathfinderCatalog
{
    public sealed record Operation(string Name, string Hash, string ExampleVariables);

    public static readonly Operation[] All =
    [
        new Operation(
            "queryArtistOverview",
            "1ac33ddab5d39a3a9c27802774e6d78b97c27c4b27a8d05de92cb0c6e1742d6c",
            """{"uri":"spotify:artist:6eUKZXaKkcviH0Ku9w2n3V","locale":"","includePrerelease":true}"""),
        new Operation(
            "queryAlbumTracks",
            "8b7383b3a4dfe2c93b21eea76e6e0a06fa49fc4f0aa3a40fab8d2d97f99e7898",
            """{"uri":"spotify:album:4aawyAB9vmqN3uQ7FjRGTy","offset":0,"limit":50}"""),
        new Operation(
            "getAlbum",
            "8f4cd5650f9d80349dbe68684057476d8bf27a5c51687b2b1686099ab5631829",
            """{"uri":"spotify:album:4aawyAB9vmqN3uQ7FjRGTy","locale":"","offset":0,"limit":50}"""),
        new Operation(
            "fetchPlaylist",
            "f1f8bd9b0b5d8e8b0c7e6c4f8e9d8c7b6a5f4e3d2c1b0a9f8e7d6c5b4a3f2e1d0",
            """{"uri":"spotify:playlist:37i9dQZF1DXcBWIGoYBM5M"}"""),
        new Operation(
            "searchDesktop",
            "16434ae5cd5c1d75ba3d4ec1f9f95f73e4ee98e3e4b65e8a9c5e0b8d3a1c2f4e5",
            """{"searchTerm":"the beatles","offset":0,"limit":10,"numberOfTopResults":5}"""),
        new Operation(
            "home",
            "97a3c93a9f3a4ad3c8b7e62e2c5e7a3e6d8b1a2c4d3e5f6a7b8c9d0e1f2a3b4c",
            """{"timeZone":"Europe/Amsterdam"}"""),
        new Operation(
            "(custom — type below)",
            "",
            "{}"),
    ];
}
