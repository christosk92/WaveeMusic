using Wavee;
using Xunit;

namespace Wavee.Tests.Actions;

/// <summary>
/// The playlist page's notice rule (plan P1.9). The page used to have exactly one answer for "the playlist you are
/// reading is gone": nothing at all — the reload failed, the previous model stayed, and the edit affordances kept
/// offering edits the server could only refuse.
/// </summary>
public class PlaylistPageNoticeRulesTests
{
    const bool Owner = true, NotOwner = false, CanView = true, NoView = false, Known = true, Unknown = false;

    [Fact]
    public void AHealthyReload_ClearsToNone()
        => Assert.Equal(DetailNotice.None,
            PlaylistPageNoticeRules.Next(DetailNotice.None, freshIsNull: false, headerDeleted: false, Known, CanView, Owner, isCreatePending: false));

    [Fact]
    public void AVanishedReload_IsADeletion()
        => Assert.Equal(DetailNotice.Deleted,
            PlaylistPageNoticeRules.Next(DetailNotice.None, freshIsNull: true, headerDeleted: false, Known, CanView, Owner, isCreatePending: false));

    [Fact]
    public void ATombstonedHeader_IsADeletion()
        => Assert.Equal(DetailNotice.Deleted,
            PlaylistPageNoticeRules.Next(DetailNotice.None, freshIsNull: false, headerDeleted: true, Known, CanView, Owner, isCreatePending: false));

    [Fact]
    public void LostViewRights_OnSomeoneElsesPlaylist_IsARevocation()
        => Assert.Equal(DetailNotice.AccessRevoked,
            PlaylistPageNoticeRules.Next(DetailNotice.None, freshIsNull: false, headerDeleted: false, Known, NoView, NotOwner, isCreatePending: false));

    /// <summary>An OWNER always retains view rights on their own list, so a false CanView there is a capability we
    /// failed to seed — not a revocation. Accusing the owner of losing access to their own playlist is the worse error.</summary>
    [Fact]
    public void AnOwnerIsNeverRevokedFromTheirOwnList()
        => Assert.Equal(DetailNotice.None,
            PlaylistPageNoticeRules.Next(DetailNotice.None, freshIsNull: false, headerDeleted: false, Known, NoView, Owner, isCreatePending: false));

    /// <summary>A header that has not carried a capabilities block yet (a rootlist-seeded thin row, a decorate reply
    /// without it) reads as all-false rights — which is exactly the "!CanView && !IsOwner" shape of a revocation. It is
    /// nothing of the kind: unknown holds whatever the page was already saying, and never accuses on its own.</summary>
    [Theory]
    [InlineData(DetailNotice.None)]
    [InlineData(DetailNotice.AccessRevoked)]
    public void UnknownCapabilities_NeverRevokes(DetailNotice prev)
        => Assert.Equal(prev,
            PlaylistPageNoticeRules.Next(prev, freshIsNull: false, headerDeleted: false, Unknown, NoView, NotOwner, isCreatePending: false));

    /// <summary>…and a thin header still clears a DELETION: the header being back at all is the fact that matters
    /// there, and it does not depend on the rights block.</summary>
    [Fact]
    public void UnknownCapabilities_StillClearsADeletion()
        => Assert.Equal(DetailNotice.None,
            PlaylistPageNoticeRules.Next(DetailNotice.Deleted, freshIsNull: false, headerDeleted: false, Unknown, NoView, NotOwner, isCreatePending: false));

    /// <summary>A deletion clears when the playlist comes back (an undelete, a re-share, or a transient bad read):
    /// the notice is a live verdict, not a latch.</summary>
    [Fact]
    public void ADeletionClearsWhenThePlaylistComesBack()
        => Assert.Equal(DetailNotice.None,
            PlaylistPageNoticeRules.Next(DetailNotice.Deleted, freshIsNull: false, headerDeleted: false, Known, CanView, Owner, isCreatePending: false));

    /// <summary>While an optimistic create is still riding the outbox the server has never heard of this playlist, so
    /// "it is not there" is the EXPECTED state — reporting it as a deletion would make every offline create look like
    /// someone deleted the thing the user just made.</summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void AnInFlightCreate_IsNotADeletion(bool freshIsNull, bool headerDeleted)
        => Assert.Equal(DetailNotice.None,
            PlaylistPageNoticeRules.Next(DetailNotice.None, freshIsNull, headerDeleted, Known, CanView, Owner, isCreatePending: true));

    /// <summary>CreateFailed is terminal: the follow-up reload also finds nothing, and re-deciding would relabel
    /// "couldn't be created" as "was deleted" — a different, and wrong, story about the same page.</summary>
    [Fact]
    public void CreateFailed_IsSticky()
    {
        Assert.Equal(DetailNotice.CreateFailed,
            PlaylistPageNoticeRules.Next(DetailNotice.CreateFailed, freshIsNull: true, headerDeleted: false, Known, CanView, Owner, isCreatePending: false));
        Assert.Equal(DetailNotice.CreateFailed,
            PlaylistPageNoticeRules.Next(DetailNotice.CreateFailed, freshIsNull: false, headerDeleted: false, Known, CanView, Owner, isCreatePending: false));
    }

    /// <summary>A deletion outranks a revocation: "this was deleted" is the more specific and more useful fact when a
    /// tombstone also strips view rights.</summary>
    [Fact]
    public void DeletionOutranksRevocation()
        => Assert.Equal(DetailNotice.Deleted,
            PlaylistPageNoticeRules.Next(DetailNotice.None, freshIsNull: false, headerDeleted: true, Known, NoView, NotOwner, isCreatePending: false));

    /// <summary>The whole create lifecycle, in the order a real one runs it: in flight (absence is expected) → rejected
    /// (the page says so) → every later reload (it keeps saying so, and never relabels itself a deletion).</summary>
    [Fact]
    public void ACreateLifecycle_RunsPendingThenFailedAndStaysFailed()
    {
        var inFlight = PlaylistPageNoticeRules.Next(DetailNotice.None, freshIsNull: true, headerDeleted: false,
            Known, CanView, Owner, isCreatePending: true);
        Assert.Equal(DetailNotice.None, inFlight);

        // The rejection is fed in by DetailPage (LibraryBridge.IsCreateFailed), not decided here — the rule's job is
        // to keep it once it is set.
        var settled = PlaylistPageNoticeRules.Next(DetailNotice.CreateFailed, freshIsNull: true, headerDeleted: false,
            Known, CanView, Owner, isCreatePending: false);
        Assert.Equal(DetailNotice.CreateFailed, settled);
    }

    [Fact]
    public void ColdOpen_ReadsTheHeaderAlone()
    {
        Assert.Equal(DetailNotice.None, PlaylistPageNoticeRules.Cold(headerDeleted: false, Known, CanView, Owner));
        Assert.Equal(DetailNotice.Deleted, PlaylistPageNoticeRules.Cold(headerDeleted: true, Known, CanView, Owner));
        Assert.Equal(DetailNotice.AccessRevoked, PlaylistPageNoticeRules.Cold(headerDeleted: false, Known, NoView, NotOwner));
    }

    /// <summary>The cold-open flash: a deep link / a fresh navigation whose first model is built from a thin header
    /// (all-false placeholder rights) must open as an ordinary page, not with "You no longer have access" for the beat
    /// before the decorate reply lands. The tombstone still wins — it is a header fact, not a rights fact.</summary>
    [Fact]
    public void ColdOpen_ThinHeader_IsNone()
    {
        Assert.Equal(DetailNotice.None, PlaylistPageNoticeRules.Cold(headerDeleted: false, Unknown, NoView, NotOwner));
        Assert.Equal(DetailNotice.Deleted, PlaylistPageNoticeRules.Cold(headerDeleted: true, Unknown, NoView, NotOwner));
    }
}
