// ── Wavee.Tests/VisualizerCatalogTests.cs — the face catalog, the Kind migration, the needs/tiers and the demand ───────
//
// Pure: `Visualizer.Catalog` and `Visualizer.Demand` are System-only. Pins the persisted ints (append-only), the legacy →
// successor table (viz-app-plan §3.1), the gallery's order and groups, every shown face's needs, the caption rule and
// the gallery's live-preview budget (§4.1).

using Wavee;
using Xunit;

using K = Wavee.Visualizer.Kind;

namespace Wavee.Tests;

public class VisualizerCatalogTests
{
    // ── the persisted ints and the migration ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Kind_ints_are_append_only()
    {
        Assert.Equal(Visualizer.Catalog.Count, Enum.GetValues<K>().Length);
        K[] order =
        [
            K.Field, K.Halo, K.Horizon, K.Matrix, K.Aurora, K.Spectrum, K.Pulse, K.Tape, K.Verse, K.Bloom, K.Bars, K.Ring, K.Orbit,
            K.Timeline, K.Classic, K.Warp, K.Tunnel, K.Ambience, K.Kaleido, K.Scope, K.Drift, K.Type, K.Mosaic, K.Spotlight, K.Magneto, K.Flow,
        ];
        Assert.Equal(26, order.Length);
        for (int i = 0; i < order.Length; i++) Assert.Equal(i, (int)order[i]);
    }

    [Theory]
    [InlineData(K.Field, K.Bloom)]
    [InlineData(K.Halo, K.Ring)]
    [InlineData(K.Horizon, K.Timeline)]
    [InlineData(K.Matrix, K.Classic)]
    [InlineData(K.Spectrum, K.Bars)]
    [InlineData(K.Pulse, K.Ring)]
    [InlineData(K.Tape, K.Timeline)]
    public void Legacy_faces_coerce_to_their_successors(K legacy, K successor)
    {
        Assert.True(Visualizer.Catalog.IsLegacy(legacy));
        Assert.False(Visualizer.Catalog.IsShown(legacy));
        Assert.Equal((int)successor, Visualizer.Catalog.Coerce((int)legacy));
        Assert.Equal(successor, Visualizer.Catalog.Successor(legacy));
        Assert.True(Visualizer.Catalog.IsShown(successor));
    }

    [Fact]
    public void Every_shown_face_round_trips_and_anything_else_is_Bloom()
    {
        foreach (var k in Visualizer.Catalog.Shown)
        {
            Assert.Equal((int)k, Visualizer.Catalog.Coerce((int)k));
            Assert.False(Visualizer.Catalog.IsLegacy(k));
        }
        Assert.False(Visualizer.Catalog.IsLegacy(K.Aurora));          // 4 was redone in place and is still shown
        Assert.Equal((int)K.Aurora, Visualizer.Catalog.Coerce(4));

        int bloom = (int)K.Bloom;
        Assert.Equal(K.Bloom, Visualizer.Catalog.Default);
        Assert.Equal(bloom, Visualizer.Catalog.Coerce(99));
        Assert.Equal(bloom, Visualizer.Catalog.Coerce(Visualizer.Catalog.Count));
        Assert.Equal(bloom, Visualizer.Catalog.Coerce(-1));
        Assert.Equal(bloom, Visualizer.Catalog.Coerce(int.MinValue));
        Assert.Equal(bloom, Visualizer.Catalog.Coerce(int.MaxValue));

        // coercing is idempotent: every answer is a shown face
        for (int i = -3; i < 40; i++)
        {
            int c = Visualizer.Catalog.Coerce(i);
            Assert.True(Visualizer.Catalog.IsShown((K)c));
            Assert.Equal(c, Visualizer.Catalog.Coerce(c));
        }
    }

    // ── the gallery's order and groups ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Shown_is_the_gallery_order_in_contiguous_groups()
    {
        K[] expected =
        [
            K.Verse, K.Bloom, K.Bars, K.Ring, K.Orbit, K.Aurora, K.Timeline, K.Classic, K.Warp, K.Tunnel, K.Ambience, K.Kaleido, K.Scope,
            K.Drift, K.Type, K.Mosaic, K.Spotlight, K.Magneto, K.Flow,
        ];
        Assert.Equal(expected, Visualizer.Catalog.Shown.ToArray());
        Assert.Equal(19, Visualizer.Catalog.ShownCount);
        Assert.Equal(new[] { Visualizer.Group.Lyrics, Visualizer.Group.Fluent, Visualizer.Group.Classics, Visualizer.Group.Zune, Visualizer.Group.ITunes },
                     Visualizer.Catalog.Groups.ToArray());

        Assert.Equal(1, Visualizer.Catalog.CountIn(Visualizer.Group.Lyrics));
        Assert.Equal(6, Visualizer.Catalog.CountIn(Visualizer.Group.Fluent));
        Assert.Equal(7, Visualizer.Catalog.CountIn(Visualizer.Group.Classics));
        Assert.Equal(3, Visualizer.Catalog.CountIn(Visualizer.Group.Zune));
        Assert.Equal(2, Visualizer.Catalog.CountIn(Visualizer.Group.ITunes));

        // the groups never interleave (the gallery draws one section per group, in order)
        var shown = Visualizer.Catalog.Shown;
        for (int i = 1; i < shown.Length; i++)
            Assert.True(Visualizer.Catalog.GroupOf(shown[i]) >= Visualizer.Catalog.GroupOf(shown[i - 1]));
        Assert.Equal(K.Classic, Visualizer.Catalog.FirstOf(Visualizer.Group.Classics));
        Assert.Equal(-1, Visualizer.Catalog.IndexOf(K.Tape));
        Assert.Equal(0, Visualizer.Catalog.IndexOf(K.Verse));
    }

    [Fact]
    public void Step_and_StepGroup_wrap_over_the_shown_faces()
    {
        Assert.Equal(K.Bars, Visualizer.Catalog.Step(K.Bloom, 1));
        Assert.Equal(K.Verse, Visualizer.Catalog.Step(K.Bloom, -1));
        Assert.Equal(K.Verse, Visualizer.Catalog.Step(K.Flow, 1));       // wraps
        Assert.Equal(K.Flow, Visualizer.Catalog.Step(K.Verse, -1));
        Assert.Equal(K.Ring, Visualizer.Catalog.Step(K.Field, 2));       // a legacy kind steps from its successor (Bloom)

        Assert.Equal(K.Classic, Visualizer.Catalog.StepGroup(K.Ring, 1));
        Assert.Equal(K.Verse, Visualizer.Catalog.StepGroup(K.Ring, -1));
        Assert.Equal(K.Verse, Visualizer.Catalog.StepGroup(K.Flow, 1));   // wraps
        Assert.Equal(K.Magneto, Visualizer.Catalog.StepGroup(K.Verse, -1));
    }

    // ── needs, tiers, captions ──────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(K.Verse, Visualizer.Need.Spectrum | Visualizer.Need.Lyrics, Visualizer.Tier.Spectrum)]
    [InlineData(K.Bloom, Visualizer.Need.Level, Visualizer.Tier.Level)]
    [InlineData(K.Bars, Visualizer.Need.Spectrum, Visualizer.Tier.Spectrum)]
    [InlineData(K.Ring, Visualizer.Need.Spectrum | Visualizer.Need.Beats, Visualizer.Tier.Spectrum)]
    [InlineData(K.Timeline, Visualizer.Need.Precomputed | Visualizer.Need.Beats | Visualizer.Need.Level, Visualizer.Tier.Level)]
    [InlineData(K.Scope, Visualizer.Need.Spectrum | Visualizer.Need.Scope, Visualizer.Tier.Scope)]
    [InlineData(K.Flow, Visualizer.Need.Spectrum | Visualizer.Need.Scope, Visualizer.Tier.Scope)]
    [InlineData(K.Type, Visualizer.Need.Level | Visualizer.Need.Beats, Visualizer.Tier.Level)]
    [InlineData(K.Mosaic, Visualizer.Need.Spectrum | Visualizer.Need.Beats | Visualizer.Need.Covers, Visualizer.Tier.Spectrum)]
    [InlineData(K.Spotlight, Visualizer.Need.Beats | Visualizer.Need.Gallery, Visualizer.Tier.None)]
    [InlineData(K.Magneto, Visualizer.Need.Spectrum | Visualizer.Need.Beats, Visualizer.Tier.Spectrum)]
    public void Needs_and_tiers(K kind, Visualizer.Need need, Visualizer.Tier tier)
    {
        Assert.Equal(need, Visualizer.Catalog.NeedsOf(kind));
        Assert.Equal(tier, Visualizer.Catalog.TierOf(Visualizer.Catalog.NeedsOf(kind)));
    }

    [Fact]
    public void Every_shown_face_needs_something_and_legacy_kinds_answer_for_their_successor()
    {
        foreach (var k in Visualizer.Catalog.Shown) Assert.NotEqual(Visualizer.Need.None, Visualizer.Catalog.NeedsOf(k));
        Assert.Equal(Visualizer.Catalog.NeedsOf(K.Timeline), Visualizer.Catalog.NeedsOf(K.Horizon));
        Assert.Equal(Visualizer.Tier.Spectrum, Visualizer.Catalog.PreviewTier);   // the gallery's previews: capped at spectrum
        Assert.True(Visualizer.Catalog.UsesSeries(K.Aurora) && Visualizer.Catalog.UsesSeries(K.Timeline) && Visualizer.Catalog.UsesSeries(K.Scope));
        Assert.False(Visualizer.Catalog.UsesSeries(K.Bars));
        Assert.Equal(Visualizer.Pace.Lively, Visualizer.Catalog.PaceOf(K.Bars));
        Assert.Equal(Visualizer.Pace.Calm, Visualizer.Catalog.PaceOf(K.Bloom));
    }

    [Fact]
    public void The_caption_never_sits_over_Verse_or_the_faces_that_carry_their_own_text()
    {
        foreach (var k in new[] { K.Verse, K.Timeline, K.Scope, K.Classic, K.Type, K.Mosaic, K.Spotlight, K.Flow })
            Assert.False(Visualizer.Catalog.CaptionFriendly(k), k.ToString());
        foreach (var k in new[] { K.Bloom, K.Bars, K.Ring, K.Orbit, K.Aurora, K.Warp, K.Tunnel, K.Ambience, K.Kaleido, K.Drift, K.Magneto })
            Assert.True(Visualizer.Catalog.CaptionFriendly(k), k.ToString());
    }

    // ── the gallery's live budget ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void LodFor_keeps_the_selected_and_focused_tiles_live_and_the_rest_posters()
    {
        int bloom = Visualizer.Catalog.IndexOf(K.Bloom), warp = Visualizer.Catalog.IndexOf(K.Warp), flow = Visualizer.Catalog.IndexOf(K.Flow);
        Assert.Equal(Visualizer.Lod.Preview, Visualizer.Catalog.LodFor(bloom, bloom, -1, weak: false));
        Assert.Equal(Visualizer.Lod.Preview, Visualizer.Catalog.LodFor(bloom, bloom, -1, weak: true));          // selected: always live
        Assert.Equal(Visualizer.Lod.Preview, Visualizer.Catalog.LodFor(flow, bloom, flow, weak: true));         // hovered: always live
        Assert.Equal(Visualizer.Lod.Preview, Visualizer.Catalog.LodFor(bloom + 2, bloom, -1, weak: false));     // a neighbour
        Assert.Equal(Visualizer.Lod.Poster, Visualizer.Catalog.LodFor(bloom + 2, bloom, -1, weak: true));       // weak: posters
        Assert.Equal(Visualizer.Lod.Poster, Visualizer.Catalog.LodFor(bloom + 3, bloom, -1, weak: false));      // out of reach
        Assert.Equal(Visualizer.Lod.Poster, Visualizer.Catalog.LodFor(warp, warp - 1, -1, weak: false));        // a physics preview
        Assert.Equal(Visualizer.Lod.Preview, Visualizer.Catalog.LodFor(warp, warp, -1, weak: false));           // … unless selected

        // whatever is selected and focused, never more than the budget is live
        int n = Visualizer.Catalog.ShownCount;
        for (int sel = -1; sel < n; sel++)
            for (int focus = -1; focus < n; focus++)
            {
                int live = 0;
                for (int i = 0; i < n; i++) if (Visualizer.Catalog.LodFor(i, sel, focus, weak: false) == Visualizer.Lod.Preview) live++;
                Assert.True(live <= Visualizer.Catalog.MaxLivePreviews, $"sel={sel} focus={focus}: {live} live");
            }
    }

    // ── demand ──────────────────────────────────────────────────────────────────────────────────────────────────────

    static Visualizer.Tier TierFor(K k, bool viz = true, bool gallery = false, bool stageUp = true, bool playing = true, bool owner = true,
        bool supported = true, bool occluded = false, bool reduced = false)
        => Visualizer.Demand.For(k, viz, gallery, stageUp, playing, owner, supported, occluded, reduced);

    [Fact]
    public void Demand_every_closed_gate_holds_no_lease()
    {
        foreach (var k in Enum.GetValues<K>())
            foreach (bool viz in new[] { true, false })
                foreach (bool gallery in new[] { true, false })
                {
                    Assert.Equal(Visualizer.Tier.None, TierFor(k, viz, gallery, stageUp: false));
                    Assert.Equal(Visualizer.Tier.None, TierFor(k, viz, gallery, playing: false));
                    Assert.Equal(Visualizer.Tier.None, TierFor(k, viz, gallery, owner: false));
                    Assert.Equal(Visualizer.Tier.None, TierFor(k, viz, gallery, supported: false));
                    Assert.Equal(Visualizer.Tier.None, TierFor(k, viz, gallery, occluded: true));
                    Assert.Equal(Visualizer.Tier.None, TierFor(k, viz, gallery, reduced: true));
                }
    }

    [Theory]
    [InlineData(K.Bloom, Visualizer.Tier.Level)]
    [InlineData(K.Bars, Visualizer.Tier.Spectrum)]
    [InlineData(K.Scope, Visualizer.Tier.Scope)]
    [InlineData(K.Spotlight, Visualizer.Tier.None)]
    [InlineData(K.Timeline, Visualizer.Tier.Level)]
    [InlineData(K.Field, Visualizer.Tier.Level)]                      // a legacy pick answers as its successor (Bloom)
    public void Demand_follows_the_face_and_the_open_gallery_raises_it_to_the_previews(K kind, Visualizer.Tier face)
    {
        Assert.Equal(face, TierFor(kind));
        var withGallery = face > Visualizer.Catalog.PreviewTier ? face : Visualizer.Catalog.PreviewTier;
        Assert.Equal(withGallery, TierFor(kind, gallery: true));      // a Bars preview is not dead under a Bloom stage
        Assert.Equal(Visualizer.Tier.Level, TierFor(kind, viz: false)); // outside Visualizer mode: the backdrop's breath (V-U55)
        Assert.Equal(Visualizer.Tier.Level, TierFor(kind, viz: false, gallery: true));
    }

    [Fact]
    public void Demand_pulls_the_scope_only_for_a_face_that_reads_it()
    {
        Assert.True(Visualizer.Demand.PullsScope(Visualizer.Tier.Scope, visualizerMode: true, readers: 1));        // the stage Scope face
        Assert.True(Visualizer.Demand.PullsScope(Visualizer.Tier.Spectrum, visualizerMode: true, readers: 1));     // a live Scope tile under a Bloom stage
        Assert.False(Visualizer.Demand.PullsScope(Visualizer.Tier.Spectrum, visualizerMode: true, readers: 0));    // the gallery open alone is no reason
        Assert.False(Visualizer.Demand.PullsScope(Visualizer.Tier.Level, visualizerMode: true, readers: 2));       // no spectrum lease: no tap
        Assert.False(Visualizer.Demand.PullsScope(Visualizer.Tier.Scope, visualizerMode: false, readers: 1));
    }

    [Fact]
    public void Ticks_run_while_settling_even_when_paused_and_stop_when_occluded_or_reduced()
    {
        Assert.True(Visualizer.Demand.Ticks(stageUp: true, playing: true, settled: true, occluded: false, reduced: false));
        Assert.True(Visualizer.Demand.Ticks(stageUp: true, playing: false, settled: false, occluded: false, reduced: false));
        Assert.False(Visualizer.Demand.Ticks(stageUp: true, playing: false, settled: true, occluded: false, reduced: false));
        Assert.False(Visualizer.Demand.Ticks(stageUp: false, playing: true, settled: false, occluded: false, reduced: false));
        Assert.False(Visualizer.Demand.Ticks(stageUp: true, playing: true, settled: false, occluded: true, reduced: false));
        Assert.False(Visualizer.Demand.Ticks(stageUp: true, playing: true, settled: false, occluded: false, reduced: true));
    }
}
