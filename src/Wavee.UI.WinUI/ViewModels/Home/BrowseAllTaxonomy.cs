using System.Collections.Generic;

namespace Wavee.UI.WinUI.ViewModels.Home;

// Maps stable Spotify URIs to a Browse All bucket. URIs are language-
// independent: spotify:page:0JQ5DAqbMKFSi39LMRT0Cy is "Music" in en-US, "음악"
// in ko-KR, etc. — so URI-keyed classification is locale-safe.
//
// To add a new category from a future Pathfinder browseAll response: append
// one entry below. Anything not listed falls into BrowseAllGroupKind.More.
internal static class BrowseAllTaxonomy
{
    private static readonly Dictionary<string, BrowseAllGroupKind> Map = new()
    {
        // TOP
        ["spotify:page:0JQ5DAqbMKFSi39LMRT0Cy"]  = BrowseAllGroupKind.Top, // Music
        ["spotify:page:0JQ5DArNBzkmxXHCqFLx2J"]  = BrowseAllGroupKind.Top, // Podcasts
        ["spotify:page:0JQ5DAqbMKFETqK4t8f1n3"]  = BrowseAllGroupKind.Top, // Audiobooks
        ["spotify:xlink:0JQ5DAozXW0GUBAKjHsifL"] = BrowseAllGroupKind.Top, // Live Events

        // FOR YOU
        ["spotify:page:0JQ5DAt0tbjZptfcdMSKl3"]  = BrowseAllGroupKind.ForYou, // Made For You
        ["spotify:page:0JQ5DAqbMKFz6FAsUtgAab"]  = BrowseAllGroupKind.ForYou, // New Releases
        ["spotify:page:0JQ5DAqbMKFOOxftoKZxod"]  = BrowseAllGroupKind.ForYou, // RADAR
        ["spotify:page:0JQ5DAqbMKFPw634sFwguI"]  = BrowseAllGroupKind.ForYou, // EQUAL
        ["spotify:page:0JQ5DAqbMKFImHYGo3eTSg"]  = BrowseAllGroupKind.ForYou, // Fresh Finds
        ["spotify:page:0JQ5DAqbMKFRKBHIxJ5hMm"]  = BrowseAllGroupKind.ForYou, // Trendsetter
        ["spotify:page:0JQ5DAqbMKFGnsSfvg90Wo"]  = BrowseAllGroupKind.ForYou, // GLOW
        ["spotify:page:0JQ5DAtOnAEpjOgUKwXyxj"]  = BrowseAllGroupKind.ForYou, // Discover (추천곡)
        ["spotify:page:0JQ5DAqbMKFQIL0AXnG5AK"]  = BrowseAllGroupKind.ForYou, // Trending
        ["spotify:page:0JQ5DAqbMKFDBgllo2cUIN"]  = BrowseAllGroupKind.ForYou, // Spotify Singles

        // GENRES
        ["spotify:page:0JQ5DAqbMKFEC4WFtoNRpw"]  = BrowseAllGroupKind.Genres, // Pop
        ["spotify:page:0JQ5DAqbMKFQ00XGBls6ym"]  = BrowseAllGroupKind.Genres, // Hip-Hop
        ["spotify:page:0JQ5DAqbMKFHOzuVTgTizF"]  = BrowseAllGroupKind.Genres, // Dance/Electronic
        ["spotify:page:0JQ5DAqbMKFCLroFGPFVr5"]  = BrowseAllGroupKind.Genres, // Dutch music
        ["spotify:page:0JQ5DAqbMKFEZPnFQSFB1T"]  = BrowseAllGroupKind.Genres, // R&B
        ["spotify:page:0JQ5DAqbMKFNQ0fGp4byGU"]  = BrowseAllGroupKind.Genres, // Afro
        ["spotify:page:0JQ5DAqbMKFGvOw3O4nLAf"]  = BrowseAllGroupKind.Genres, // K-pop
        ["spotify:page:0JQ5DAqbMKFIpEuaCnimBj"]  = BrowseAllGroupKind.Genres, // Soul
        ["spotify:page:0JQ5DAqbMKFDXXwE9BDJAr"]  = BrowseAllGroupKind.Genres, // Rock
        ["spotify:page:0JQ5DAqbMKFxXaXKP7zcDp"]  = BrowseAllGroupKind.Genres, // Latin
        ["spotify:page:0JQ5DAqbMKFCWjUTdzaG0e"]  = BrowseAllGroupKind.Genres, // Indie
        ["spotify:page:0JQ5DAqbMKFDkd668ypn6O"]  = BrowseAllGroupKind.Genres, // Metal
        ["spotify:page:0JQ5DAqbMKFy78wprEpAjl"]  = BrowseAllGroupKind.Genres, // Folk & Acoustic
        ["spotify:page:0JQ5DAqbMKFKLfwjuJMoNC"]  = BrowseAllGroupKind.Genres, // Country
        ["spotify:page:0JQ5DAqbMKFPrEiAOxgac3"]  = BrowseAllGroupKind.Genres, // Classical
        ["spotify:page:0JQ5DAqbMKFFtlLYUHv8bT"]  = BrowseAllGroupKind.Genres, // Alternative
        ["spotify:page:0JQ5DAqbMKFAjfauKLOZiv"]  = BrowseAllGroupKind.Genres, // Punk
        ["spotify:page:0JQ5DAqbMKFLjmiZRss79w"]  = BrowseAllGroupKind.Genres, // Ambient
        ["spotify:page:0JQ5DAqbMKFQiK2EHwyjcU"]  = BrowseAllGroupKind.Genres, // Blues
        ["spotify:page:0JQ5DAqbMKFObNLOHydSW8"]  = BrowseAllGroupKind.Genres, // Caribbean
        ["spotify:page:0JQ5DAqbMKFAJ5xb0fwo9m"]  = BrowseAllGroupKind.Genres, // Jazz
        ["spotify:page:0JQ5DAqbMKFFsW9N8maB6z"]  = BrowseAllGroupKind.Genres, // Funk & Disco
        ["spotify:page:0JQ5DAqbMKFJKoGyUMo2hE"]  = BrowseAllGroupKind.Genres, // Reggae
        ["spotify:page:0JQ5DAqbMKFQ1UFISXj59F"]  = BrowseAllGroupKind.Genres, // Arab

        // MOOD & ACTIVITY
        ["spotify:page:0JQ5DAqbMKFzHmL4tf05da"]  = BrowseAllGroupKind.MoodAndActivity, // Mood
        ["spotify:page:0JQ5DAqbMKFFzDl7qN9Apr"]  = BrowseAllGroupKind.MoodAndActivity, // Chill
        ["spotify:page:0JQ5DAqbMKFA6SOHvT3gck"]  = BrowseAllGroupKind.MoodAndActivity, // Party
        ["spotify:page:0JQ5DAqbMKFCuoRTxhYWow"]  = BrowseAllGroupKind.MoodAndActivity, // Sleep
        ["spotify:page:0JQ5DAqbMKFAUsdyVjCQuL"]  = BrowseAllGroupKind.MoodAndActivity, // Love
        ["spotify:page:0JQ5DAqbMKFCbimwdOYlsl"]  = BrowseAllGroupKind.MoodAndActivity, // Focus
        ["spotify:page:0JQ5DAqbMKFAXlCG6QvYQ4"]  = BrowseAllGroupKind.MoodAndActivity, // Workout Music
        ["spotify:page:0JQ5DAqbMKFJ6dHNHTv6Mx"]  = BrowseAllGroupKind.MoodAndActivity, // Fitness
        ["spotify:page:0JQ5DAqbMKFIRybaNTYXXy"]  = BrowseAllGroupKind.MoodAndActivity, // In the car / Driving
        ["spotify:page:0JQ5DAqbMKFx0uLQR2okcc"]  = BrowseAllGroupKind.MoodAndActivity, // At Home
        ["spotify:page:0JQ5DAqbMKFAQy4HL4XU2D"]  = BrowseAllGroupKind.MoodAndActivity, // Travel
        ["spotify:page:0JQ5DAqbMKFRY5ok2pxXJ0"]  = BrowseAllGroupKind.MoodAndActivity, // Cooking & Dining
        ["spotify:page:0JQ5DAqbMKFLb2EqgLtpjC"]  = BrowseAllGroupKind.MoodAndActivity, // Wellness
        ["spotify:page:0JQ5DAqbMKFSCjnQr8QZ3O"]  = BrowseAllGroupKind.MoodAndActivity, // Songwriters
        ["spotify:page:0JQ5DAqbMKFI3pNLtYMD9S"]  = BrowseAllGroupKind.MoodAndActivity, // Nature & Noise

        // CHARTS
        ["spotify:page:0JQ5DAudkNjCgYMM0TZXDw"]  = BrowseAllGroupKind.Charts, // Charts
        ["spotify:page:0JQ5DAB3zgCauRwnvdEQjJ"]  = BrowseAllGroupKind.Charts, // Podcast Charts
    };

    // Canonical order for items inside the Top group (Music → Podcasts →
    // Audiobooks → Live Events). Sorted by URI position to stay locale-safe.
    public static readonly string[] TopOrder =
    {
        "spotify:page:0JQ5DAqbMKFSi39LMRT0Cy",  // Music
        "spotify:page:0JQ5DArNBzkmxXHCqFLx2J",  // Podcasts
        "spotify:page:0JQ5DAqbMKFETqK4t8f1n3",  // Audiobooks
        "spotify:xlink:0JQ5DAozXW0GUBAKjHsifL", // Live Events
    };

    public static BrowseAllGroupKind Classify(string? uri)
        => !string.IsNullOrEmpty(uri) && Map.TryGetValue(uri, out var kind)
            ? kind
            : BrowseAllGroupKind.More;
}
