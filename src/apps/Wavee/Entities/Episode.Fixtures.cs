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

    /// <summary>The reader fixture for an episode the fake catalogue HOLDS (its row knows its title); for one it does not (the pending probe
    /// `epzzqx`, whose own read never answers) the ask never answers either, so the pending page stays on its skeleton instead of drawing the
    /// fixture's chapters, transcript and comments under a loading head (audit B7, A2's finding).</summary>
    internal static Task<T> Answer<T>(string episodeUri, T fixture, CancellationToken ct)
        => Holds(episodeUri) ? Task.FromResult(fixture) : Never<T>(ct);

    static bool Holds(string episodeUri)
    {
        if (!EntityId.TryParse(episodeUri, out var id)) return false;
        return Entities.Current.Episodes.TryGetSlot(id, out int slot) && new Episode(slot).Knows(EpisodeFields.Title);
    }

    static async Task<T> Never<T>(CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
        throw new OperationCanceledException(ct);
    }
}
