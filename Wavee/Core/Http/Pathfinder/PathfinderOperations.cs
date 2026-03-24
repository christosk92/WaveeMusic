namespace Wavee.Core.Http.Pathfinder;

/// <summary>
/// Known Pathfinder GraphQL operation names and persisted query hashes.
/// </summary>
internal static class PathfinderOperations
{
    public const string SearchDesktop = "searchDesktop";
    public const string SearchDesktopHash = "fcad5a3e0d5af727fb76966f06971c19cfa2275e6ff7671196753e008611873c";

    public const string UserTopContent = "userTopContent";
    public const string UserTopContentHash = "49ee15704de4a7fdeac65a02db20604aa11e46f02e809c55d9a89f6db9754356";

    public const string FetchExtractedColors = "fetchExtractedColors";
    public const string FetchExtractedColorsHash = "36e90fcaea00d47c695fce31874efeb2519b97d4cd0ee1abfb4f8dc9348596ea";

    public const string Home = "home";
    public const string HomeHash = "3e8e118c033b10353783ec0404451de66ed44e5cb5e0caefc65e4fab7b9e0aef";

    public const string QueryArtistOverview = "queryArtistOverview";
    public const string QueryArtistOverviewHash = "5b9e64f43843fa3a9b6a98543600299b0a2cbbbccfdcdcef2402eb9c1017ca4c";

    public const string QueryArtistDiscographyAlbums = "queryArtistDiscographyAlbums";
    public const string QueryArtistDiscographyAlbumsHash = "4a41796610948485ef92db12ff32e3ab3a7ceb851a05dba4afe63889879be28d";

    public const string QueryArtistDiscographySingles = "queryArtistDiscographySingles";
    public const string QueryArtistDiscographySinglesHash = "5e07d323febb57b4a56a42abbf781490e58764aa45feb6e3dc0591564fc56599";

    public const string QueryArtistDiscographyCompilations = "queryArtistDiscographyCompilations";
    public const string QueryArtistDiscographyCompilationsHash = "0b3a55646384e771ed5b7da7cbf0e6e7e4b653d3da0eb4eb614eac4046c91e38";
}
