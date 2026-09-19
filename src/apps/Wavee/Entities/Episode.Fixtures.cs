namespace Wavee;

/// <summary>Offline reader content for the existing --fake smoke-test mode.</summary>
internal static class PodcastReaderFixtures
{
    internal static readonly Spotify.Podcasts.Page<Spotify.Podcasts.Chapter> Chapters = new(
        [new("fake:chapter:opening", "Opening", 0, 60000), new("fake:chapter:conversation", "The conversation", 60000, 0)], "", 2, "", 200);
    internal static readonly Spotify.Podcasts.Transcript Transcript = new("en",
        [new(0, "Opening", true), new(0, "Welcome to this episode.", false),
         new(15000, "Today we explore the ideas behind the story.", false),
         new(60000, "The conversation", true), new(60000, "Let us start with the first question.", false)], 200);
    internal static readonly Spotify.Podcasts.Page<Spotify.Podcasts.Comment> Comments = new(
        [new("fake:comment:reader", "A thoughtful conversation. Thank you for sharing it.", "Listener", "", "2026-09-19T10:00:00Z",
            false, false, true, 0, 0, "", true)], "", 1, "", 200);
}
