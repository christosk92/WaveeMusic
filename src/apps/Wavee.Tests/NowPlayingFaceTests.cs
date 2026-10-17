// ── Wavee.Tests/NowPlayingFaceTests.cs — the now-playing face's HOLD as a core model (themes plan C2, X4) ──────────────
// (Shell/NowPlayingFace.cs; the rule it applies is `Shell.PlayerBarRules.FaceOf`.) Pure: a fake clock, rows named by slot and
// a title callback stand in for the catalog; nothing here reads a signal of the playback model.
//
//   A SKIP BETWEEN TWO ROWS THE CLIENT ALREADY HOLDS changes nothing but the content: the OUTGOING identity stays on screen
//   for 450 ms while the incoming one resolves; a load that outlives the hold was never a skip and falls back to the
//   placeholder.

using Xunit;

namespace Wavee.Tests;

public class NowPlayingFaceTests
{
    const float Hold = Shell.PlayerBarRules.FaceHoldMs;

    static EntityRef Row(int slot, EntityKind kind = EntityKind.Track) => new(kind, slot);

    static Func<EntityRef, bool> Known(params int[] slots) => r => Array.IndexOf(slots, r.Slot) >= 0;

    static readonly EntityId PlaylistContext = EntityId.Parse("spotify:playlist:37i9dQZF1DXcBWIGoYBM5M");
    static readonly EntityId AlbumContext = EntityId.Parse("spotify:album:4aawyAB9vmqN3uQ7FjRGTy");

    [Fact]
    public void The_hold_is_the_450_ms_the_rule_names()
        => Assert.Equal(450f, Hold);

    [Fact]
    public void A_resolved_title_is_the_live_face()
    {
        var face = new Shell.NowPlayingFace();
        var a = Row(1);
        face.Observe(a, titleKnown: true, PlaylistContext, nowMs: 1_000);
        var (arm, shown) = face.Resolve(Shell.PlayerState.Active, a, Known(1), 1_010);
        Assert.Equal(Shell.FaceArm.Live, arm);
        Assert.Equal(a, shown);
    }

    [Fact]
    public void A_skip_holds_the_outgoing_face_while_the_incoming_title_resolves()
    {
        var face = new Shell.NowPlayingFace();
        var a = Row(1);
        var b = Row(2);
        face.Observe(a, true, PlaylistContext, 1_000);

        // The deck steps to B in THIS render (its identity stamp follows it): B has no title yet, A is held.
        var (landing, landingRef) = face.Resolve(Shell.PlayerState.Loading, b, Known(1), 5_000);
        Assert.Equal(Shell.FaceArm.Hold, landing);
        Assert.Equal(a, landingRef);

        face.Observe(b, false, PlaylistContext, 5_000);        // the layout effect commits after the render
        var (arm, shown) = face.Resolve(Shell.PlayerState.Loading, b, Known(1), 5_000 + 100);
        Assert.Equal(Shell.FaceArm.Hold, arm);
        Assert.Equal(a, shown);
        Assert.Equal(a, face.Held);                            // an unresolved incoming row never replaces the held one
    }

    [Fact]
    public void The_hold_ends_exactly_at_450_ms_and_falls_back_to_the_placeholder()
    {
        var face = new Shell.NowPlayingFace();
        var a = Row(1);
        var b = Row(2);
        face.Observe(a, true, PlaylistContext, 1_000);
        face.Observe(b, false, PlaylistContext, 5_000);

        Assert.Equal(Shell.FaceArm.Hold, face.Resolve(Shell.PlayerState.Loading, b, Known(1), 5_000 + 449).Arm);
        var (arm, shown) = face.Resolve(Shell.PlayerState.Loading, b, Known(1), 5_000 + 450);
        Assert.Equal(Shell.FaceArm.Placeholder, arm);
        Assert.Equal(b, shown);
    }

    [Fact]
    public void The_incoming_row_becomes_the_face_the_moment_its_title_lands()
    {
        var face = new Shell.NowPlayingFace();
        var a = Row(1);
        var b = Row(2);
        face.Observe(a, true, PlaylistContext, 1_000);
        face.Observe(b, false, PlaylistContext, 5_000);

        var (arm, shown) = face.Resolve(Shell.PlayerState.Active, b, Known(1, 2), 5_200);
        Assert.Equal(Shell.FaceArm.Live, arm);
        Assert.Equal(b, shown);

        face.Observe(b, true, AlbumContext, 5_200);
        Assert.Equal(b, face.Held);
        Assert.Equal(AlbumContext, face.HeldContext);
    }

    [Fact]
    public void The_held_art_click_keeps_the_context_the_held_row_was_resolved_under()
    {
        var face = new Shell.NowPlayingFace();
        face.Observe(Row(1), true, PlaylistContext, 1_000);
        face.Observe(Row(2), false, AlbumContext, 5_000);      // the NEW context arrives with the unresolved row
        Assert.Equal(PlaylistContext, face.HeldContext);
    }

    [Fact]
    public void A_held_row_whose_own_title_was_evicted_is_not_a_face_worth_showing()
    {
        var face = new Shell.NowPlayingFace();
        var a = Row(1);
        var b = Row(2);
        face.Observe(a, true, PlaylistContext, 1_000);
        face.Observe(b, false, PlaylistContext, 5_000);
        Assert.Equal(Shell.FaceArm.Placeholder, face.Resolve(Shell.PlayerState.Loading, b, Known(), 5_050).Arm);
    }

    [Fact]
    public void The_hold_never_masks_the_states_that_author_their_own_copy()
    {
        var face = new Shell.NowPlayingFace();
        var a = Row(1);
        var b = Row(2);
        face.Observe(a, true, PlaylistContext, 1_000);
        face.Observe(b, false, PlaylistContext, 5_000);
        Assert.Equal(Shell.FaceArm.Live, face.Resolve(Shell.PlayerState.Error, b, Known(1), 5_050).Arm);
        Assert.Equal(Shell.FaceArm.Live, face.Resolve(Shell.PlayerState.NoTrack, default, Known(1), 5_050).Arm);
    }

    [Fact]
    public void An_empty_deck_clears_the_held_face_so_play_from_idle_never_flashes_the_previous_session()
    {
        var face = new Shell.NowPlayingFace();
        face.Observe(Row(1), true, PlaylistContext, 1_000);
        face.Observe(default, false, default, 2_000);          // "Nothing playing"
        Assert.True(face.Held.IsNone);
        Assert.True(face.HeldContext.IsEmpty);

        var b = Row(2);
        face.Observe(b, false, PlaylistContext, 3_000);        // press play from idle
        Assert.Equal(Shell.FaceArm.Placeholder, face.Resolve(Shell.PlayerState.Loading, b, Known(1), 3_010).Arm);
    }

    [Fact]
    public void The_held_title_is_asked_only_when_there_is_a_held_face_to_ask_about()
    {
        var face = new Shell.NowPlayingFace();
        var asked = new List<int>();
        bool Ask(EntityRef r) { asked.Add(r.Slot); return true; }

        var a = Row(1);
        face.Resolve(Shell.PlayerState.Active, a, Ask, 1_000);
        Assert.Equal(new[] { 1 }, asked);                      // nothing held yet: only the current row

        face.Observe(a, true, PlaylistContext, 1_000);
        asked.Clear();
        face.Resolve(Shell.PlayerState.Active, a, Ask, 1_010);
        Assert.Equal(new[] { 1 }, asked);                      // the held row IS the current row: no second read

        asked.Clear();
        face.Resolve(Shell.PlayerState.Active, Row(2), Ask, 1_020);
        Assert.Equal(new[] { 1, 2 }, asked);                   // a different current row: the held one is checked too
    }

    [Fact]
    public void The_sweep_grace_measures_from_the_stamp_the_layout_effect_commits()
    {
        var face = new Shell.NowPlayingFace();
        Assert.Equal(0f, face.MsInLoading(true, 1_000));       // the render that OPENS the window reads 0
        face.ObserveLoading(true, 1_000);
        Assert.Equal(300f, face.MsInLoading(true, 1_300));
        Assert.Equal(0f, face.MsInLoading(false, 1_300));
        face.ObserveLoading(false, 1_400);
        Assert.Equal(0f, face.MsInLoading(true, 1_500));
    }

    [Fact]
    public void A_load_keeps_the_last_settled_play_state_so_the_primary_never_flickers()
    {
        var face = new Shell.NowPlayingFace();
        face.ObserveSettled(Shell.PlayerState.Active, true);
        face.ObserveSettled(Shell.PlayerState.Loading, false);     // IsPlaying drops for the 150 ms a skip takes
        Assert.True(face.PlayingBeforeLoad);
        Assert.True(Shell.PlayerBarRules.ShowsPauseGlyph(Shell.PlayerState.Loading, false, face.PlayingBeforeLoad));

        face.ObserveSettled(Shell.PlayerState.Active, false);      // paused: the answer settles on play
        Assert.False(face.PlayingBeforeLoad);
    }

    [Fact]
    public void A_deadline_wakes_readers_through_the_one_signal()
    {
        var face = new Shell.NowPlayingFace();
        int before = face.Wake.Peek();
        face.Bump();
        face.Bump();
        Assert.Equal(before + 2, face.Wake.Peek());
    }

    [Fact]
    public void The_shared_face_is_one_instance()
        => Assert.Same(Shell.NowPlayingFace.Shared, Shell.NowPlayingFace.Shared);
}
