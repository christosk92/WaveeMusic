// ── Wavee.Tests/LibraryAudiobookFilterTests.cs — the Podcasts/Audiobooks split (Entities/User.Page.Library.cs, A2
// plan §3.6). Pure over ShowFlags: no scope, no edge, no episode table. Podcasts applies the inverse of this same
// predicate (User.Page.Library.cs's SplitShowKind), so what is pinned here is that the predicate itself partitions a
// mixed set with no overlap and no drop — the property the inverse-application trick depends on.

using Xunit;

namespace Wavee.Tests;

public class LibraryAudiobookFilterTests
{
    [Fact]
    public void IsAudiobookRow_True_WhenTheAudiobookFlagIsSet()
        => Assert.True(LibraryAudiobookFilter.IsAudiobookRow(ShowFlags.Audiobook));

    [Fact]
    public void IsAudiobookRow_True_WhenTheAudiobookFlagIsSetAlongsideOthers()
        => Assert.True(LibraryAudiobookFilter.IsAudiobookRow(ShowFlags.Audiobook | ShowFlags.Explicit | ShowFlags.CanRate));

    [Fact]
    public void IsAudiobookRow_False_ForAnOrdinaryPodcast()
        => Assert.False(LibraryAudiobookFilter.IsAudiobookRow(ShowFlags.None));

    [Theory]
    [InlineData(ShowFlags.Explicit)]
    [InlineData(ShowFlags.Video)]
    [InlineData(ShowFlags.Mixed)]
    [InlineData(ShowFlags.Exclusive)]
    [InlineData(ShowFlags.MusicAndTalk)]
    [InlineData(ShowFlags.CanRate)]
    public void IsAudiobookRow_False_ForEveryOtherFlagAlone(ShowFlags flags)
        => Assert.False(LibraryAudiobookFilter.IsAudiobookRow(flags));

    /// <summary>The property the library page's client-side split actually leans on: applying the predicate and its
    /// inverse over the SAME mixed set partitions it exactly — every row lands on one list, none on both, none
    /// dropped. Mirrors <c>User.Page.Library.cs</c>'s <c>SplitShowKind</c> (<c>audiobook == wantAudiobooks</c>) without
    /// touching a scope.</summary>
    [Fact]
    public void Predicate_PartitionsAMixedSet_WithNoOverlapAndNoDrop()
    {
        ShowFlags[] rows =
        [
            ShowFlags.None,                                   // an ordinary podcast
            ShowFlags.Audiobook,                                // The Manager's Path
            ShowFlags.Explicit,                                 // an ordinary, explicit podcast
            ShowFlags.Audiobook | ShowFlags.Explicit,           // an explicit audiobook
            ShowFlags.Video,                                    // a video podcast
            ShowFlags.Audiobook | ShowFlags.CanRate,            // a rateable audiobook
            ShowFlags.MusicAndTalk,
        ];

        var audiobooks = new System.Collections.Generic.List<int>();
        var podcasts = new System.Collections.Generic.List<int>();
        for (int i = 0; i < rows.Length; i++)
            (LibraryAudiobookFilter.IsAudiobookRow(rows[i]) ? audiobooks : podcasts).Add(i);

        Assert.Equal(3, audiobooks.Count);
        Assert.Equal(4, podcasts.Count);
        Assert.Empty(System.Linq.Enumerable.Intersect(audiobooks, podcasts));
        Assert.Equal(rows.Length, audiobooks.Count + podcasts.Count);
        Assert.Equal([1, 3, 5], audiobooks);
        Assert.Equal([0, 2, 4, 6], podcasts);
    }
}
