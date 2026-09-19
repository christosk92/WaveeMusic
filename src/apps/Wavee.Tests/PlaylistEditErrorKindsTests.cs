// ── Wavee.Tests/PlaylistEditErrorKindsTests.cs — WP-5.O stream A ───────────────────────────────────────────────────
// 0.2.9's Actions/PlaylistEditErrorKindsTests, ported verbatim against Entities/Playlist.cs §8. Dropped: the
// "every key exists in en-US.json" fact (it read an asset file off disk through CallerFilePath; the keys are generated
// `Strings` consts, so a rename is a compile error — a deleted catalog entry is the loc pipeline's check). Added: the
// 0.3 transport half (`KindOfStatus`) and the NoOp / Invalid rows the 0.2.9 theory skipped.

using System;
using Wavee;
using Xunit;

namespace Wavee.Tests;

/// <summary>The playlist-edit failure map: every failure has a SENTENCE (never engine prose), and classification reads the
/// TYPED failure rather than sniffing message text.</summary>
public class PlaylistEditErrorKindsTests
{
    [Theory]
    [InlineData(PlaylistMutationFailure.Unknown)]
    [InlineData(PlaylistMutationFailure.Conflict)]
    [InlineData(PlaylistMutationFailure.Forbidden)]
    [InlineData(PlaylistMutationFailure.Deleted)]
    [InlineData(PlaylistMutationFailure.Offline)]
    [InlineData(PlaylistMutationFailure.Pending)]
    [InlineData(PlaylistMutationFailure.NotSupported)]
    [InlineData(PlaylistMutationFailure.NoOp)]
    [InlineData(PlaylistMutationFailure.Invalid)]
    public void EveryKind_RoundTripsFromItsTypedException(PlaylistMutationFailure kind)
    {
        var ex = new PlaylistMutationException(kind, "engine prose nobody should ever read");
        Assert.Equal(kind, PlaylistEditErrorKinds.KindOf(ex));
    }

    [Theory]
    [InlineData(PlaylistMutationFailure.Unknown)]
    [InlineData(PlaylistMutationFailure.Conflict)]
    [InlineData(PlaylistMutationFailure.Forbidden)]
    [InlineData(PlaylistMutationFailure.Deleted)]
    [InlineData(PlaylistMutationFailure.Offline)]
    [InlineData(PlaylistMutationFailure.Pending)]
    [InlineData(PlaylistMutationFailure.NotSupported)]
    [InlineData(PlaylistMutationFailure.NoOp)]
    [InlineData(PlaylistMutationFailure.Invalid)]
    public void EveryKind_HasANonEmptyKeyForEveryVerb(PlaylistMutationFailure kind)
    {
        foreach (PlaylistEditVerb verb in Enum.GetValues<PlaylistEditVerb>())
        {
            string key = PlaylistEditErrorKinds.KeyFor(kind, verb);
            Assert.False(string.IsNullOrWhiteSpace(key), $"{kind}/{verb} has no copy");
            Assert.Contains('.', key);                 // a loc KEY, never a literal sentence
        }
    }

    [Fact]
    public void Conflict_HasItsOwnReorderSentence()
    {
        string generic = PlaylistEditErrorKinds.KeyFor(PlaylistMutationFailure.Conflict);
        string reorder = PlaylistEditErrorKinds.KeyFor(PlaylistMutationFailure.Conflict, PlaylistEditVerb.Reorder);
        Assert.NotEqual(generic, reorder);
        Assert.Equal(generic, PlaylistEditErrorKinds.KeyFor(PlaylistMutationFailure.Conflict, PlaylistEditVerb.Remove));
    }

    [Fact]
    public void Pending_HasItsOwnReorderSentence()
    {
        string generic = PlaylistEditErrorKinds.KeyFor(PlaylistMutationFailure.Pending);
        string reorder = PlaylistEditErrorKinds.KeyFor(PlaylistMutationFailure.Pending, PlaylistEditVerb.Reorder);
        Assert.NotEqual(generic, reorder);
        Assert.Equal(Strings.Drag.StillSyncing, reorder);
        Assert.True(PlaylistEditErrorKinds.IsInformational(PlaylistMutationFailure.Pending));
        Assert.Equal(generic, PlaylistEditErrorKinds.KeyFor(PlaylistMutationFailure.Pending, PlaylistEditVerb.Add));
    }

    /// <summary>The drag chip's own two sentences for a refused reorder; every other verb still says "Something went
    /// wrong" (W22b records that as a 0.2.9 copy gap).</summary>
    [Fact]
    public void NoOpAndInvalid_BorrowTheDragSentencesForAReorderOnly()
    {
        Assert.Equal(Strings.Drag.AlreadyThere, PlaylistEditErrorKinds.KeyFor(PlaylistMutationFailure.NoOp, PlaylistEditVerb.Reorder));
        Assert.Equal(Strings.Drag.CantMoveHere, PlaylistEditErrorKinds.KeyFor(PlaylistMutationFailure.Invalid, PlaylistEditVerb.Reorder));
        Assert.Equal(Strings.Detail.Edit.Failed, PlaylistEditErrorKinds.KeyFor(PlaylistMutationFailure.NoOp, PlaylistEditVerb.Add));
        Assert.Equal(Strings.Detail.Edit.Failed, PlaylistEditErrorKinds.KeyFor(PlaylistMutationFailure.Invalid));
    }

    [Theory]
    [InlineData("rootlist changes failed (409)")]
    [InlineData("permission base failed (403)")]
    [InlineData("Object reference not set to an instance of an object.")]
    public void Unknown_NeverYieldsTheExceptionMessage(string message)
    {
        var ex = new InvalidOperationException(message);
        Assert.Equal(PlaylistMutationFailure.Unknown, PlaylistEditErrorKinds.KindOf(ex));
        foreach (PlaylistEditVerb verb in Enum.GetValues<PlaylistEditVerb>())
            Assert.NotEqual(message, PlaylistEditErrorKinds.KeyFor(PlaylistMutationFailure.Unknown, verb));
    }

    [Fact]
    public void NotSupported_IsClassifiedFromTheBclType()
        => Assert.Equal(PlaylistMutationFailure.NotSupported,
                        PlaylistEditErrorKinds.KindOf(new NotSupportedException("Playlist editing is not available.")));

    [Fact]
    public void ATypedFailure_SurvivesBeingWrapped()
    {
        var inner = new PlaylistMutationException(PlaylistMutationFailure.Forbidden, "403");
        Assert.Equal(PlaylistMutationFailure.Forbidden, PlaylistEditErrorKinds.KindOf(new InvalidOperationException("wrapped", inner)));
        Assert.Equal(PlaylistMutationFailure.Forbidden, PlaylistEditErrorKinds.KindOf(new AggregateException(inner)));
    }

    [Fact]
    public void NullIsUnknown_NotACrash() => Assert.Equal(PlaylistMutationFailure.Unknown, PlaylistEditErrorKinds.KindOf(null));

    [Fact]
    public void OnlyTheKeptOutcomes_AreInformational()
    {
        Assert.True(PlaylistEditErrorKinds.IsInformational(PlaylistMutationFailure.Offline));
        Assert.True(PlaylistEditErrorKinds.IsInformational(PlaylistMutationFailure.Pending));
        Assert.True(PlaylistEditErrorKinds.IsInformational(PlaylistMutationFailure.NoOp));
        Assert.True(PlaylistEditErrorKinds.IsInformational(PlaylistMutationFailure.Invalid));
        foreach (var kind in new[] { PlaylistMutationFailure.Unknown, PlaylistMutationFailure.Conflict,
                                     PlaylistMutationFailure.Forbidden, PlaylistMutationFailure.Deleted,
                                     PlaylistMutationFailure.NotSupported })
            Assert.False(PlaylistEditErrorKinds.IsInformational(kind), $"{kind} is a lost edit");
    }

    /// <summary>0.3's transport half: a write's HTTP status → the kind. A transport failure (0) is a LOST edit in 0.3 —
    /// there is no offline outbox — so it must never read as the reassuring "will sync when you're back".</summary>
    [Theory]
    [InlineData(409, PlaylistMutationFailure.Conflict)]
    [InlineData(403, PlaylistMutationFailure.Forbidden)]
    [InlineData(401, PlaylistMutationFailure.Forbidden)]
    [InlineData(404, PlaylistMutationFailure.Deleted)]
    [InlineData(410, PlaylistMutationFailure.Deleted)]
    [InlineData(0, PlaylistMutationFailure.Unknown)]
    [InlineData(500, PlaylistMutationFailure.Unknown)]
    public void KindOfStatus_MapsTheWriteAnswer(int status, PlaylistMutationFailure expected)
    {
        Assert.Equal(expected, PlaylistEditErrorKinds.KindOfStatus(status));
        Assert.NotEqual(PlaylistMutationFailure.Offline, PlaylistEditErrorKinds.KindOfStatus(status));
    }
}
