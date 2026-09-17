// ── Wavee.Tests/PlaylistPageNoticeRulesTests.cs — the notice ladder (playlist verdicts + the album thinness fact) ──
//
// Ported from _old/Wavee.Tests/Actions/PlaylistPageNoticeRulesTests.cs onto `Detail.NoticeRules` (Entities/Detail.cs).
// The playlist ladder (`Next`/`Cold`) is verbatim. The ALBUM facts change input shape: 0.2.9 built `Wavee.Core.Track`
// records and the rule read `HydrationLevels.TrackUnnamed`; 0.3 hands the rule `Track` HANDLES and the thin-row
// predicate is `Knows(...)` — an unnamed title is a row whose Identity group never committed, a nameless artist ref is a
// `TrackArtists` target whose `ArtistFields.Name` is unknown. Every assertion is kept; the one 0.2.9 spelling that has
// no 0.3 twin — the uri-placeholder title (`title == uri`) — is rewritten over the 0.3 shape of the same bug: a row the
// kind-185 trait pass reached (PlayCount known) whose Identity repair never landed, i.e. Plays beside a blank name.
//
// The class joins EntitiesCollection because the album facts build handles into a fresh scope.

using Xunit;
using NoticeRules = Wavee.Detail.NoticeRules;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class PlaylistPageNoticeRulesTests
{
    const bool Owner = true, NotOwner = false, CanView = true, NoView = false, Known = true, Unknown = false;

    [Fact]
    public void AHealthyReload_ClearsToNone()
        => Assert.Equal(DetailNotice.None,
            NoticeRules.Next(DetailNotice.None, freshIsNull: false, headerDeleted: false, Known, CanView, Owner, isCreatePending: false));

    [Fact]
    public void AVanishedReload_IsADeletion()
        => Assert.Equal(DetailNotice.Deleted,
            NoticeRules.Next(DetailNotice.None, freshIsNull: true, headerDeleted: false, Known, CanView, Owner, isCreatePending: false));

    [Fact]
    public void ATombstonedHeader_IsADeletion()
        => Assert.Equal(DetailNotice.Deleted,
            NoticeRules.Next(DetailNotice.None, freshIsNull: false, headerDeleted: true, Known, CanView, Owner, isCreatePending: false));

    [Fact]
    public void LostViewRights_OnSomeoneElsesPlaylist_IsARevocation()
        => Assert.Equal(DetailNotice.AccessRevoked,
            NoticeRules.Next(DetailNotice.None, freshIsNull: false, headerDeleted: false, Known, NoView, NotOwner, isCreatePending: false));

    /// <summary>An OWNER always retains view rights on their own list — a false CanView there is a capability we failed
    /// to seed, never a revocation.</summary>
    [Fact]
    public void AnOwnerIsNeverRevokedFromTheirOwnList()
        => Assert.Equal(DetailNotice.None,
            NoticeRules.Next(DetailNotice.None, freshIsNull: false, headerDeleted: false, Known, NoView, Owner, isCreatePending: false));

    /// <summary>A header with no capabilities block reads as all-false rights — the shape of a revocation. Unknown holds
    /// whatever the page was saying and never accuses on its own.</summary>
    [Theory]
    [InlineData(DetailNotice.None)]
    [InlineData(DetailNotice.AccessRevoked)]
    public void UnknownCapabilities_NeverRevokes(DetailNotice prev)
        => Assert.Equal(prev,
            NoticeRules.Next(prev, freshIsNull: false, headerDeleted: false, Unknown, NoView, NotOwner, isCreatePending: false));

    /// <summary>…and a thin header still clears a DELETION: the header being back is the fact that matters there.</summary>
    [Fact]
    public void UnknownCapabilities_StillClearsADeletion()
        => Assert.Equal(DetailNotice.None,
            NoticeRules.Next(DetailNotice.Deleted, freshIsNull: false, headerDeleted: false, Unknown, NoView, NotOwner, isCreatePending: false));

    /// <summary>The notice is a live verdict, not a latch.</summary>
    [Fact]
    public void ADeletionClearsWhenThePlaylistComesBack()
        => Assert.Equal(DetailNotice.None,
            NoticeRules.Next(DetailNotice.Deleted, freshIsNull: false, headerDeleted: false, Known, CanView, Owner, isCreatePending: false));

    /// <summary>While an optimistic create rides the outbox, "it is not there" is the EXPECTED state.</summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void AnInFlightCreate_IsNotADeletion(bool freshIsNull, bool headerDeleted)
        => Assert.Equal(DetailNotice.None,
            NoticeRules.Next(DetailNotice.None, freshIsNull, headerDeleted, Known, CanView, Owner, isCreatePending: true));

    /// <summary>CreateFailed is terminal: re-deciding would relabel "couldn't be created" as "was deleted".</summary>
    [Fact]
    public void CreateFailed_IsSticky()
    {
        Assert.Equal(DetailNotice.CreateFailed,
            NoticeRules.Next(DetailNotice.CreateFailed, freshIsNull: true, headerDeleted: false, Known, CanView, Owner, isCreatePending: false));
        Assert.Equal(DetailNotice.CreateFailed,
            NoticeRules.Next(DetailNotice.CreateFailed, freshIsNull: false, headerDeleted: false, Known, CanView, Owner, isCreatePending: false));
    }

    /// <summary>"This was deleted" is the more specific fact when a tombstone also strips view rights.</summary>
    [Fact]
    public void DeletionOutranksRevocation()
        => Assert.Equal(DetailNotice.Deleted,
            NoticeRules.Next(DetailNotice.None, freshIsNull: false, headerDeleted: true, Known, NoView, NotOwner, isCreatePending: false));

    /// <summary>In flight (absence expected) → rejected → every later reload keeps saying so.</summary>
    [Fact]
    public void ACreateLifecycle_RunsPendingThenFailedAndStaysFailed()
    {
        var inFlight = NoticeRules.Next(DetailNotice.None, freshIsNull: true, headerDeleted: false,
            Known, CanView, Owner, isCreatePending: true);
        Assert.Equal(DetailNotice.None, inFlight);

        // The rejection is fed in by the page (the intent journal), not decided here — the rule keeps it once set.
        var settled = NoticeRules.Next(DetailNotice.CreateFailed, freshIsNull: true, headerDeleted: false,
            Known, CanView, Owner, isCreatePending: false);
        Assert.Equal(DetailNotice.CreateFailed, settled);
    }

    [Fact]
    public void ColdOpen_ReadsTheHeaderAlone()
    {
        Assert.Equal(DetailNotice.None, NoticeRules.Cold(headerDeleted: false, Known, CanView, Owner));
        Assert.Equal(DetailNotice.Deleted, NoticeRules.Cold(headerDeleted: true, Known, CanView, Owner));
        Assert.Equal(DetailNotice.AccessRevoked, NoticeRules.Cold(headerDeleted: false, Known, NoView, NotOwner));
    }

    /// <summary>A cold open from a thin header must open as an ordinary page; the tombstone still wins.</summary>
    [Fact]
    public void ColdOpen_ThinHeader_IsNone()
    {
        Assert.Equal(DetailNotice.None, NoticeRules.Cold(headerDeleted: false, Unknown, NoView, NotOwner));
        Assert.Equal(DetailNotice.Deleted, NoticeRules.Cold(headerDeleted: true, Unknown, NoView, NotOwner));
    }

    // ── The ALBUM path's one verdict: MinifiedAlbum (ForAlbum) ───────────────────────────────────────────────────────
    // AlbumV4's disc rows are gid-only: the album's tracklist edge allocates their slots, but no Identity answer has
    // committed onto them until the TrackV4 repair lands — and play counts arrive on a separate trait pass, so a pane can
    // show Plays beside blank names.

    /// <summary>A named row: the Identity group committed (title, duration) plus named artists on its TrackArtists edge.
    /// Requires a fresh scope.</summary>
    static Track Row(string id, string title, int durationMs = 200_000, bool artistNamed = true)
    {
        var s = Staging.Rent();
        ref var trow = ref s.Tracks.Add();
        trow.Id = s.Text("spotify:track:" + id);
        trow.Title = s.Text(title);
        trow.DurationMs = durationMs;
        trow.Known = (uint)TrackFields.Identity;
        trow.Authority = Authority.Full;

        string artistUri = "spotify:artist:" + id;
        if (artistNamed)
        {
            ref var arow = ref s.Artists.Add();
            arow.Id = s.Text(artistUri);
            arow.Name = s.Text("Artist");
            arow.Known = (uint)ArtistFields.Identity;
            arow.Authority = Authority.Full;
        }
        TestScope.CommitAndPublish(s);

        var track = Entities.Track(EntityUri.Parse("spotify:track:" + id));
        // Unnamed ⇒ the ref points somewhere real (a slot) that no answer has named.
        int[] artistSlots = [Entities.Artist(EntityUri.Parse(artistUri)).Slot];
        Entities.Current.Edges.TrackArtists.Replace(track.Slot, artistSlots, [], EdgeState.Complete, artistSlots.Length);
        return track;
    }

    /// <summary>A gid-only row: the slot exists (the album's edge allocated it), no Identity answer ever committed.</summary>
    static Track ThinRow(string id) => Entities.Track(EntityUri.Parse("spotify:track:" + id));

    [Fact]
    public void Album_AllRowsNamed_HasNoNotice()
    {
        TestScope.Fresh();
        ReadOnlySpan<Track> tracks = [Row("one", "One"), Row("two", "Two"), Row("three", "Three")];
        Assert.Equal(DetailNotice.None, NoticeRules.ForAlbum(tracks));
    }

    /// <summary>One blank title anywhere in the list is enough: the user can SEE that row, and it is blank.</summary>
    [Fact]
    public void Album_OneUnnamedRow_IsMinified()
    {
        TestScope.Fresh();
        ReadOnlySpan<Track> tracks = [Row("one", "One"), ThinRow("blank"), Row("three", "Three")];
        Assert.Equal(DetailNotice.MinifiedAlbum, NoticeRules.ForAlbum(tracks));
    }

    /// <summary>0.2.9's second spelling of the same gid-only row (a placeholder title) is, in 0.3, a row that a later
    /// enrichment reached — Plays known — while its Identity repair never landed. Plays beside a blank name is minified.</summary>
    [Fact]
    public void Album_UriPlaceholderTitle_IsMinified()
    {
        TestScope.Fresh();
        var s = Staging.Rent();
        ref var plays = ref s.Tracks.Add();
        plays.Id = s.Text("spotify:track:placeholder");
        plays.PlayCount = 1_000_000;
        plays.Known = (uint)TrackFields.PlayCount;
        plays.Authority = Authority.Thin;
        TestScope.CommitAndPublish(s);

        var track = Entities.Track(EntityUri.Parse("spotify:track:placeholder"));
        Assert.True(track.Knows(TrackFields.PlayCount));
        Assert.Equal(DetailNotice.MinifiedAlbum, NoticeRules.ForAlbum([track]));
    }

    /// <summary>An EMPTY tracklist is not "minified" — it is still loading.</summary>
    [Fact]
    public void Album_EmptyTracklist_HasNoNotice()
        => Assert.Equal(DetailNotice.None, NoticeRules.ForAlbum(ReadOnlySpan<Track>.Empty));

    /// <summary>Zero duration on a NAMED row is not "unnamed" — the predicate asks for a title and named artists only.</summary>
    [Fact]
    public void Album_ZeroDurationButNamed_HasNoNotice()
    {
        TestScope.Fresh();
        Assert.Equal(DetailNotice.None, NoticeRules.ForAlbum([Row("zero", "One", durationMs: 0)]));
    }

    /// <summary>A titled row whose artist ref points somewhere real but carries no name is still thin.</summary>
    [Fact]
    public void Album_NamelessArtistRefs_IsMinified()
    {
        TestScope.Fresh();
        Assert.Equal(DetailNotice.MinifiedAlbum, NoticeRules.ForAlbum([Row("nameless", "One", artistNamed: false)]));
    }
}
