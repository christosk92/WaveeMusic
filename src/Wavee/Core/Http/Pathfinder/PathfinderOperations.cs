namespace Wavee.Core.Http.Pathfinder;

/// <summary>
/// Known Pathfinder GraphQL operation names and persisted query hashes.
/// </summary>
internal static class PathfinderOperations
{
    public const string SearchTopResultsList = "searchTopResultsList";
    public const string SearchTopResultsListHash = "795a87647895afbb1e3f1aa923ced808ab960ae0e04b8f052f8fe182378d2cae";
    public const string SearchArtists = "searchArtists";
    public const string SearchArtistsHash = "270905851ba5c7faca81cfe053c2dbd8ceb4f156a0e0ef4b385af75ab69ffd13";
    public const string SearchPlaylists = "searchPlaylists";
    public const string SearchPlaylistsHash = "af1730623dc1248b75a61a18bad1f47f1fc7eff802fb0676683de88815c958d8";
    public const string SearchTracks = "searchTracks";
    public const string SearchTracksHash = "59ee4a659c32e9ad894a71308207594a65ba67bb6b632b183abe97303a51fa55";
    public const string SearchAlbums = "searchAlbums";
    public const string SearchAlbumsHash = "5e7d2724fbef31a25f714844bf1313ffc748ebd4bd199eaad50628a4f246a7ab";
    public const string SearchPodcasts = "searchPodcasts";
    public const string SearchPodcastsHash = "0195d9f61b43606d490bca64c3456e3593528cea6cc05c7e822c7c42beed0f4e";
    public const string SearchUsers = "searchUsers";
    public const string SearchUsersHash = "d3f7547835dc86a4fdf3997e0f79314e7580eaf4aaf2f4cb1e71e189c5dfcb1f";
    public const string SearchGenres = "searchGenres";
    public const string SearchGenresHash = "9e1c0e056c46239dd1956ea915b988913c87c04ce3dadccdb537774490266f46";
    public const string SearchFullEpisodes = "searchFullEpisodes";
    public const string SearchFullEpisodesHash = "a63dea054bac1fccbcf2333feaca7165c8077a361649312b41d123791326d09f";

    public const string UserTopContent = "userTopContent";
    public const string UserTopContentHash = "49ee15704de4a7fdeac65a02db20604aa11e46f02e809c55d9a89f6db9754356";

    public const string FetchExtractedColors = "fetchExtractedColors";
    public const string FetchExtractedColorsHash = "36e90fcaea00d47c695fce31874efeb2519b97d4cd0ee1abfb4f8dc9348596ea";

    public const string Home = "home";
    public const string HomeHash = "3e8e118c033b10353783ec0404451de66ed44e5cb5e0caefc65e4fab7b9e0aef";
    public const string HomeWithFacetHash = "23e37f2e58d82d567f27080101d36609009d8c3676457b1086cb0acc55b72a5d";
    public const string FeedBaselineLookup = "feedBaselineLookup";
    public const string FeedBaselineLookupHash = "a950fb7c4ecdcaf2aad2f3ca9ee9c3aa4b9c43c97e1d07d05148c4d355bea7fc";

    public const string QueryArtistOverview = "queryArtistOverview";
    public const string QueryArtistOverviewHash = "7f86ff63e38c24973a2842b672abe44c910c1973978dc8a4a0cb648edef34527";

    public const string QueryArtistDiscographyAll = "queryArtistDiscographyAll";
    public const string QueryArtistDiscographyAllHash = "5e07d323febb57b4a56a42abbf781490e58764aa45feb6e3dc0591564fc56599";

    public const string QueryArtistDiscographyAlbums = "queryArtistDiscographyAlbums";
    public const string QueryArtistDiscographyAlbumsHash = "5e07d323febb57b4a56a42abbf781490e58764aa45feb6e3dc0591564fc56599";

    public const string QueryArtistDiscographySingles = "queryArtistDiscographySingles";
    public const string QueryArtistDiscographySinglesHash = "5e07d323febb57b4a56a42abbf781490e58764aa45feb6e3dc0591564fc56599";

    public const string QueryArtistDiscographyCompilations = "queryArtistDiscographyCompilations";
    public const string QueryArtistDiscographyCompilationsHash = "5e07d323febb57b4a56a42abbf781490e58764aa45feb6e3dc0591564fc56599";

    public const string QueryAlbumTracks = "queryAlbumTracks";
    public const string QueryAlbumTracksHash = "b9bfabef66ed756e5e13f68a942deb60bd4125ec1f1be8cc42769dc0259b4b10";

    public const string GetAlbum = "getAlbum";
    public const string GetAlbumHash = "b9bfabef66ed756e5e13f68a942deb60bd4125ec1f1be8cc42769dc0259b4b10";

    public const string FetchPlaylist = "fetchPlaylist";
    public const string FetchPlaylistHash = "a65e12194ed5fc443a1cdebed5fabe33ca5b07b987185d63c72483867ad13cb4";

    public const string UserLocation = "userLocation";
    public const string UserLocationHash = "079939378ca79b67c6d047be9152ea940d21f10bbfa2f5d4cf4d8320d87774c2";

    public const string ConcertLocationsByLatLon = "concertLocationsByLatLon";
    public const string ConcertLocationsByLatLonHash = "8a059d072a17a1199feb21fe846271f1680eda87010c832852ced0c55c6c7c96";

    public const string SaveLocation = "saveLocation";
    public const string SaveLocationHash = "5502351e9f201ae29014ca55d3b24b755ba261a1a9eb35fb498cb4c7df419353";

    public const string SearchConcertLocations = "searchConcertLocations";
    public const string SearchConcertLocationsHash = "43ededefcba8b3f519fd0c2d6c025dfeec9f742cf47d04a3c3711d95b27deda3";

    public const string Concert = "concert";
    public const string ConcertHash = "6313561b79fa89c9cd2f0f1c1392a5de6b0c6ab475648ecb176ecb8dc9b43d3a";

    public const string RecentSearches = "recentSearches";
    public const string RecentSearchesHash = "b77e1eb3eeb020bd38b95ae2a127065dd5e8616cc075186b8b62c90b1b77e1b2";

    public const string SearchSuggestions = "searchSuggestions";
    public const string SearchSuggestionsHash = "9fe3ad78e43a1684b3a9fabc741c5928928d4d30d7d8fd7fd193c7ebb4a544f4";

    public const string FetchEntitiesForRecentlyPlayed = "fetchEntitiesForRecentlyPlayed";
    public const string FetchEntitiesForRecentlyPlayedHash = "5bb408450626d595cb24363104b612e14f9b966430f599121696e8996ea03794";

    public const string QueryAlbumMerch = "queryAlbumMerch";
    public const string QueryAlbumMerchHash = "3ef44ed6f17be67299538fe77faffab4075aeaf9e1085f10fc835592266711b5";

    public const string QueryNpvArtist = "queryNpvArtist";
    public const string QueryNpvArtistHash = "047c9c225967d41a763949a4db3f0493e901c9f8689a6537408aabf9beffc177";

    public const string QueryNpvEpisode = "queryNpvEpisode";
    public const string QueryNpvEpisodeHash = "5460cf262b0eed4ca71be308a0e4991ac72184660ed504af77ee2440d79ba7b6";

    public const string QueryNpvEpisodeChapters = "queryNpvEpisodeChapters";
    public const string QueryNpvEpisodeChaptersHash = "367f0e93a0d219ae6f5874bcc460201db0a43467ae94f16298931a704ac62ea6";

    public const string QueryTrackCreditsModal = "queryTrackCreditsModal";
    public const string QueryTrackCreditsModalHash = "e2ca40d46cf1fde36562261ccec754f23fb31b561877252e9fe0d6834aabb84b";

    // "Watch next" / Spotify recommender — drives the YouTube-style
    // up-next list on the Now Playing video page. Same persisted query that
    // backs the SEO-related-tracks list on share pages.
    public const string InternalLinkRecommenderTrack = "internalLinkRecommenderTrack";
    public const string InternalLinkRecommenderTrackHash = "c77098ee9d6ee8ad3eb844938722db60570d040b49f41f5ec6e7be9160a7c86b";

    // Similar albums seeded from a track URI — drives AlbumPage's
    // "For this mood" / "Similar albums" shelf. Track-seeded: pass the
    // album's most-played track URI (fall back to Tracks[0]).
    public const string SimilarAlbumsBasedOnThisTrack = "similarAlbumsBasedOnThisTrack";
    public const string SimilarAlbumsBasedOnThisTrackHash = "1d1f93a737498adca2c892c73af87fc0b052afe4e1a33c989540c32413dfae17";

    // Full track payload — playcount, full album, firstArtist + discography.
    // Used by the video page hero to render plays count next to the title.
    // Track protobuf and npvArtist response don't carry playcount, so this
    // is a separate query.
    public const string GetTrack = "getTrack";
    public const string GetTrackHash = "612585ae06ba435ad26369870deaae23b5c8800a256cd8a57e08eddc25a37294";

    public const string GetEpisodeOrChapter = "getEpisodeOrChapter";
    public const string GetEpisodeOrChapterHash = "3416929067571ac4b79db16716be3c6ea5f6265f7975a0ee94b1fc5ee1dc1e9d";

    public const string InternalLinkRecommenderEpisode = "internalLinkRecommenderEpisode";
    public const string InternalLinkRecommenderEpisodeHash = "122f5c777aae5c0918baec11cd646b7034b8f213f260097b4d229ad947ec7f93";

    // Show metadata — drives the Show detail page hero (cover, title, publisher,
    // description, rating, topics, palette colors, first page of episodes).
    public const string QueryShowMetadataV2 = "queryShowMetadataV2";
    public const string QueryShowMetadataV2Hash = "aaad798a17a43c0f443c45d630a83df39d2ca1062a090c2e4fb045d6b00ab360";

    public const string BrowsePage = "browsePage";
    public const string BrowsePageHash = "f5c4e6d668f5716464a231c1cc8b22c1cbf6ad68b09929fd7de813a30581298b";

    public const string BrowseSection = "browseSection";
    public const string BrowseSectionHash = "b13c1cccbfcb6947753c2613411b3566485c21fd5f36d80a80bb64be61ba2d51";

    // Top-level "Browse all" surface — flat list of category entries
    // (Music / Podcasts / Audiobooks / Live Events / genres / moods / charts /…),
    // each with title.transformedLabel + backgroundColor.hex + uri. Drives the
    // genre selector at the bottom of Home.
    public const string BrowseAll = "browseAll";
    public const string BrowseAllHash = "dbd8b55e09a58afc52eab438bc228ba28fd72ac2f2148c6c26354980e4579001";

    // "More podcasts you might like" carousel for the Show detail page.
    public const string InternalLinkRecommenderShow = "internalLinkRecommenderShow";
    public const string InternalLinkRecommenderShowHash = "6c369ff272a666b31fef1629c169925a1bd80f372195396c82304142cacd89e8";

    public const string GetCommentsForEntity = "getCommentsForEntity";
    public const string GetCommentsForEntityHash = "bba34fe5f2da3aaa25ab5c90eef1fe2036d325bf32e791ae462b637665185d83";

    public const string GetReplies = "getReplies";
    public const string GetRepliesHash = "a2018b23184ee9c8f355f5bcb0584aa3afbacaed6912195a367aa1bb807359f6";

    public const string GetReactions = "getReactions";
    public const string GetReactionsHash = "0d209bf9507779887fe2b3032d1afd8f35de8425b01aead094698ff1abecda71";
}
