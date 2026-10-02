// ── Wavee.Tests/StageLayoutTests.cs — the fullscreen stage's allocator, mode rules, tone arithmetic and entry rule ─────
//
// Replaces `StageTests.cs` (the wide⇄compact allocator, its height fold ladder, the scrim ladder and `Stage.Pane`, all
// deleted with the old `Stage.cs`). `Stage.Layout` is now the four-ASPECT allocator of fullscreen-flagship-implementation.md
// §2.2 / §4.7: Desktop · Ultrawide · Portrait · Compact, every DIP a fraction of the viewport, ratio hysteresis on each edge.
// The numbers pinned here are the prototype's (Main.dc.html:46 `--hw:min(28cqw, 82cqh − 340px)`, the container queries at
// :142-170, Flagship.dc.html's 1920×1080 board) — written down once so a retune is a deliberate edit, not a drift.
//
// All pure: `Stage.*` is System-only (no Element, no signal, no entity read), so these drive the real arithmetic.

using Wavee;
using Xunit;

using A = Wavee.Stage.Aspect;
using L = Wavee.Stage.Layout;
using M = Wavee.Stage.Mode;

namespace Wavee.Tests;

public class StageLayoutTests
{
    // ── the four classes ────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1920f, 1080f, Stage.Aspect.Desktop)]
    [InlineData(3440f, 1440f, Stage.Aspect.Ultrawide)]
    [InlineData(900f, 1600f, Stage.Aspect.Portrait)]
    [InlineData(1100f, 440f, Stage.Aspect.Compact)]
    [InlineData(599f, 1000f, Stage.Aspect.Compact)]     // under WideEnterW whatever the height
    [InlineData(1200f, 500f, Stage.Aspect.Ultrawide)]   // ratio 2.4 and H > 460 (V-U24)
    [InlineData(1500f, 900f, Stage.Aspect.Desktop)]
    public void The_four_classes_are_the_prototypes_queries(float w, float h, Stage.Aspect expected)
        => Assert.Equal(expected, L.Seed(w, h).Aspect);

    [Fact]
    public void Class_edges_are_hysteretic()
    {
        // Ultrawide: entered AT ratio 2.00, kept down to 1.95, left below it. (H = 1000 keeps the ratio readable.)
        Assert.Equal(A.Ultrawide, L.ClassOf(2000f, 1000f, A.Desktop));
        Assert.Equal(A.Desktop, L.ClassOf(1970f, 1000f, A.Desktop));      // the richer class is entered LATE …
        Assert.Equal(A.Ultrawide, L.ClassOf(1970f, 1000f, A.Ultrawide));  // … and left only past the reserve
        Assert.Equal(A.Desktop, L.ClassOf(1940f, 1000f, A.Ultrawide));

        // Compact: entered at H ≤ 460, kept below 484 (= 460 + FoldHysteresisH 24). W = 900 keeps ratio ≥ 1 and < 2.
        Assert.Equal(A.Compact, L.ClassOf(900f, 460f, A.Desktop));
        Assert.Equal(A.Desktop, L.ClassOf(900f, 470f, A.Desktop));
        Assert.Equal(A.Compact, L.ClassOf(900f, 470f, A.Compact));
        Assert.Equal(A.Compact, L.ClassOf(900f, 483f, A.Compact));
        Assert.Equal(A.Desktop, L.ClassOf(900f, 484f, A.Compact));

        // Portrait: entered AT ratio 0.80, kept below 0.85, left from there.
        Assert.Equal(A.Portrait, L.ClassOf(800f, 1000f, A.Desktop));
        Assert.Equal(A.Desktop, L.ClassOf(830f, 1000f, A.Desktop));
        Assert.Equal(A.Portrait, L.ClassOf(830f, 1000f, A.Portrait));
        Assert.Equal(A.Desktop, L.ClassOf(860f, 1000f, A.Portrait));
    }

    [Fact]
    public void A_width_sweep_flips_each_edge_exactly_once_in_both_directions()
    {
        // H = 1000: Compact (W < 600) → Portrait (≤ 0.80, held to 0.85) → Desktop → Ultrawide (2.00, held to 1.95).
        var cur = L.Seed(1f, 1000f);
        int flips = 0;
        for (float w = 1f; w <= 3000f; w += 1f)
        {
            var next = L.Resolve(w, 1000f, cur);
            if (next.Aspect != cur.Aspect) flips++;
            cur = next;
        }
        Assert.Equal(3, flips);
        Assert.Equal(A.Ultrawide, cur.Aspect);

        cur = L.Seed(3000f, 1000f);
        flips = 0;
        for (float w = 3000f; w >= 1f; w -= 1f)
        {
            var next = L.Resolve(w, 1000f, cur);
            if (next.Aspect != cur.Aspect) flips++;
            cur = next;
        }
        Assert.Equal(3, flips);
        Assert.Equal(A.Compact, cur.Aspect);
    }

    [Fact]
    public void A_height_sweep_enters_compact_at_460_and_leaves_it_at_484()
    {
        // W = 900 (ratio 1.86–2.0 across the fold): the only class change on the way is Desktop ⇄ Compact.
        var cur = L.Seed(900f, 1000f);
        float enteredAt = -1f;
        for (float h = 1000f; h >= 1f; h -= 1f)
        {
            cur = L.Resolve(900f, h, cur);
            if (cur.Aspect == A.Compact) { enteredAt = h; break; }
        }
        Assert.Equal(L.CompactEnterH, enteredAt);

        cur = L.Seed(900f, 1f);
        float leftAt = -1f;
        for (float h = 1f; h <= 1000f; h += 1f)
        {
            cur = L.Resolve(900f, h, cur);
            if (cur.Aspect != A.Compact) { leftAt = h; break; }
        }
        Assert.Equal(L.CompactLeaveH, leftAt);
        Assert.Equal(A.Desktop, cur.Aspect);
    }

    [Fact]
    public void A_degenerate_size_keeps_the_previous_class()
    {
        Assert.Equal(A.Portrait, L.ClassOf(0f, 0f, A.Portrait));
        Assert.Equal(A.Ultrawide, L.ClassOf(-5f, 100f, A.Ultrawide));
        Assert.Equal(A.Compact, L.ClassOf(900f, 0f, A.Compact));
        Assert.Equal(A.Desktop, L.ClassOf(0f, 0f, null));   // nothing to keep: the board's class

        var kept = L.Resolve(0f, 0f, L.Seed(900f, 1600f));
        Assert.Equal(A.Portrait, kept.Aspect);
        Assert.True(float.IsFinite(kept.HeroArt) && float.IsFinite(kept.PaneW) && float.IsFinite(kept.PaneH));
        Assert.Equal(L.Seed(0f, 0f), L.Resolve(-10f, -10f, null));   // negatives clamp to the same seed
    }

    // ── the numbers ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_hero_is_the_prototypes_formula_quantised_and_clamped()
    {
        Assert.Equal(536f, L.Seed(1920f, 1080f).HeroArt);            // min(0.28·1920 = 537.6, 0.82·1080 − 340 = 545.6) → 536
        Assert.Equal(640f, L.Seed(3440f, 1440f).HeroArt);            // Ultrawide 0.21·3440 = 722.4 → capped at HeroMax
        Assert.Equal(248f, L.Seed(1280f, 720f).HeroArt);             // min(358.4, 0.82·720 − 340 = 250.4) = 250.4 → 248
        Assert.Equal(L.HeroMin, L.Seed(800f, 500f).HeroArt);         // a very short window → the floor (168)
        Assert.Equal(224f, L.Seed(900f, 1600f).HeroArt);             // Portrait: min(0.14·1600, 0.26·900) = 224
        Assert.Equal(272f, L.Seed(1100f, 440f).HeroArt);             // Compact: min(0.62·440, 1100 − 420) = 272
    }

    [Fact]
    public void The_hero_is_always_on_the_quantum_and_inside_its_class_clamp()
    {
        for (float w = 0f; w <= 4000f; w += 53f)
            for (float h = 0f; h <= 2400f; h += 47f)
            {
                var l = L.Seed(w, h);
                bool small = l.Aspect is A.Portrait or A.Compact;
                float min = small ? L.SmallHeroMin : L.HeroMin, max = small ? L.SmallHeroMax : L.HeroMax;
                Assert.InRange(l.HeroArt, min, max);
                Assert.Equal(0f, l.HeroArt % L.ArtQuantum);
            }
    }

    [Fact]
    public void Desktop_1080p_lands_the_prototypes_numbers()
    {
        var l = L.Seed(1920f, 1080f);
        Assert.Equal(A.Desktop, l.Aspect);
        Assert.Equal(108f, l.PadX);              // Q4(0.058·1920) — the prototype's .cv 112 quantised (V-U44)
        Assert.Equal(128f, l.IdentityTop);       // Q4(0.122·1080) — the prototype's 132 quantised
        Assert.Equal(688f, l.TitleY(M.Lyrics));  // IdentityTop + hero 536 + Pad 24
        Assert.Equal(688f, l.TitleY(M.Queue));
        Assert.Equal(688f, l.TitleY(M.Artist));
        Assert.Equal(108f, l.TitleX(M.Lyrics));  // the title sits UNDER the hero, on its left edge
        Assert.Equal(740f, l.PaneX);             // PadX + hero + Q4(0.05·1920)
        Assert.Equal(96f, l.PaneRight);
        Assert.Equal(88f, l.PaneTop);
        Assert.Equal(168f, l.PaneBottom);
        Assert.Equal(L.GalleryDesktopW, l.GalleryW);
        Assert.Equal(L.TransportDesktopH, l.TransportH);
        Assert.Equal(1920f - 740f - 96f, l.PaneW);
        Assert.Equal(1080f - 88f - 168f, l.PaneH);
    }

    [Fact]
    public void Small_classes_lay_the_identity_out_as_a_row()
    {
        foreach (var l in new[] { L.Seed(900f, 1600f), L.Seed(1100f, 440f) })
        {
            Assert.True(l.IdentityIsRow);
            Assert.Equal(l.PadX + l.HeroArt + L.Pad, l.TitleX(M.Lyrics));   // right of the art …
            Assert.Equal(l.IdentityTop, l.TitleY(M.Lyrics));                // … on its top line (V-U23)
        }
        Assert.Equal(A.Compact, L.Seed(1100f, 440f).Aspect);
        var compact = L.Seed(1100f, 440f);
        Assert.Equal(compact.PadX + compact.HeroArt + L.Pad, compact.TransportLeft);   // the card sits RIGHT of the art

        foreach (var l in new[] { L.Seed(1920f, 1080f), L.Seed(3440f, 1440f), L.Seed(900f, 1600f) })
            Assert.Equal(L.Pad, l.TransportLeft);                                       // everywhere else it spans the width

        var desktop = L.Seed(1920f, 1080f);
        Assert.False(desktop.IdentityIsRow);
        Assert.Equal(desktop.IdentityTop + desktop.HeroArt + L.Pad, desktop.TitleY(M.Lyrics));
    }

    [Fact]
    public void The_visualizer_thumb_sits_in_the_now_playing_card_on_every_class()
    {
        foreach (var l in new[] { L.Seed(1920f, 1080f), L.Seed(3440f, 1440f), L.Seed(900f, 1600f), L.Seed(1100f, 440f) })
        {
            Assert.Equal(L.NowPlayingCardX + 20f, l.CoverX(M.Visualizer));
            Assert.Equal(L.NowPlayingCardY + 20f, l.CoverY(M.Visualizer));
            Assert.Equal(l.CoverX(M.Visualizer) + l.ThumbArt + 16f, l.TitleX(M.Visualizer));
            Assert.Equal(L.NowPlayingCardY + 18f, l.TitleY(M.Visualizer));
            Assert.Equal(L.NowPlayingCardW - (l.ThumbArt + 56f), l.TitleW(M.Visualizer));
            Assert.Equal(l.PadX, l.CoverX(M.Lyrics));
            Assert.Equal(l.IdentityTop, l.CoverY(M.Lyrics));
        }
    }

    [Fact]
    public void FaceRight_is_this_layouts_gallery_plus_two_gutters()
    {
        var desktop = L.Seed(1920f, 1080f);
        Assert.Equal(444f + 48f, desktop.FaceRight(true));
        Assert.Equal(L.GalleryInset, desktop.FaceRight(true));

        var ultra = L.Seed(3440f, 1440f);                    // the docked column is 19 % of W, not 444 (V-U45)
        Assert.Equal(ultra.GalleryW + 48f, ultra.FaceRight(true));
        Assert.NotEqual(492f, ultra.FaceRight(true));

        Assert.Equal(0f, L.Seed(900f, 1600f).FaceRight(true));   // Portrait: the gallery is a bottom sheet, the face is not inset
        Assert.Equal(0f, L.Seed(1100f, 440f).FaceRight(true));   // Compact: no gallery
        Assert.Equal(0f, desktop.FaceRight(false));              // closed
        Assert.Equal(0f, ultra.FaceRight(false));
    }

    // ── what each class carries ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Narrowing_never_adds()
    {
        // Richness is the five affordance flags + the transport height; the HERO is excluded on purpose (it is a per-class
        // formula and legitimately steps down across the Ultrawide promotion — V-U24). Sweeps start at 1: a 0 axis is the
        // degenerate seed, which is Desktop by design.
        foreach (float h in new[] { 1080f, 900f, 700f })
        {
            int prev = int.MinValue;
            for (float w = 1f; w <= 3000f; w += 1f)
            {
                int r = L.Seed(w, h).Richness;
                Assert.True(r >= prev, $"richness went DOWN as the window widened, at w={w}, h={h}");
                prev = r;
            }
        }
        // Height sweeps stop at 900: past ratio 0.80 a taller window at a fixed width is a NARROWER aspect and promotes to
        // Portrait on purpose (icon-only selector, bottom-sheet gallery), which is not "taking something away".
        foreach (float w in new[] { 1920f, 1200f, 800f })
        {
            int prev = int.MinValue;
            for (float h = 1f; h <= 900f; h += 1f)
            {
                int r = L.Seed(w, h).Richness;
                Assert.True(r >= prev, $"richness went DOWN as the window grew taller, at w={w}, h={h}");
                prev = r;
            }
        }
    }

    [Fact]
    public void Compact_hides_the_pane_chips_gallery_and_volume()
    {
        var l = L.Seed(1100f, 440f);
        Assert.Equal(A.Compact, l.Aspect);
        Assert.False(l.ShowPane);
        Assert.False(l.ShowChips);
        Assert.False(l.ShowGallery);
        Assert.False(l.ShowVolume);
        Assert.True(l.IconOnlySelector);
        Assert.Equal(L.TransportCompactH, l.TransportH);
        Assert.Equal(0f, l.GalleryW);
        Assert.Equal(0f, l.GalleryH);
        Assert.Equal(0, l.Richness);
    }

    [Fact]
    public void Portrait_is_icon_only_and_sheets_the_gallery()
    {
        var l = L.Seed(900f, 1600f);
        Assert.Equal(A.Portrait, l.Aspect);
        Assert.True(l.IconOnlySelector);
        Assert.True(l.ShowPane);
        Assert.True(l.ShowChips);
        Assert.True(l.ShowGallery);
        Assert.True(l.ShowVolume);
        Assert.Equal(900f, l.GalleryW);                // full width …
        Assert.Equal(880f, l.GalleryH);                // … 55 % of the height, on the quantum
        Assert.Equal(0f, l.FaceRight(true));
        Assert.False(L.Seed(650f, 1200f).ShowVolume);  // the volume slider needs 700 DIP of width
        Assert.Equal(A.Portrait, L.Seed(650f, 1200f).Aspect);
    }

    [Fact]
    public void Desktop_and_Ultrawide_carry_everything()
    {
        foreach (var l in new[] { L.Seed(1920f, 1080f), L.Seed(3440f, 1440f) })
        {
            Assert.True(l.ShowPane && l.ShowChips && l.ShowGallery && l.ShowVolume);
            Assert.False(l.IconOnlySelector);
            Assert.Equal(L.TransportDesktopH, l.TransportH);
            Assert.Equal(6, l.Richness);
        }
    }

    [Fact]
    public void The_cover_has_exactly_two_sizes_per_layout()
    {
        foreach (var l in new[] { L.Seed(1920f, 1080f), L.Seed(3440f, 1440f), L.Seed(900f, 1600f), L.Seed(1100f, 440f), L.Seed(0f, 0f) })
        {
            Assert.Equal(l.ThumbArt, l.CoverSize(M.Visualizer));
            Assert.Equal(l.HeroArt, l.CoverSize(M.Lyrics));
            Assert.Equal(l.HeroArt, l.CoverSize(M.Queue));
            Assert.Equal(l.HeroArt, l.CoverSize(M.Artist));
            Assert.True(l.ThumbArt < l.HeroArt, "the thumb is always the smaller of the two, or the morph would grow into the card");
        }
    }

    [Fact]
    public void The_pane_never_goes_negative()
    {
        for (float w = 0f; w <= 4000f; w += 53f)
            for (float h = 0f; h <= 2400f; h += 47f)
            {
                var l = L.Seed(w, h);
                Assert.True(l.PaneW >= 0f && l.PaneH >= 0f, $"a negative pane at {w}×{h}");
                Assert.True(l.GalleryH >= 0f, $"a negative gallery at {w}×{h}");
                Assert.True(float.IsFinite(l.PaneW) && float.IsFinite(l.PaneH) && float.IsFinite(l.TitleW(M.Lyrics)));
            }
    }

    [Fact]
    public void Title_type_steps_down_by_class_and_mode()
    {
        var desktop = L.Seed(1920f, 1080f);
        var ultra = L.Seed(3440f, 1440f);
        var portrait = L.Seed(900f, 1600f);
        var compact = L.Seed(1100f, 440f);

        Assert.Equal((40f, 52f), desktop.TitleFont(M.Lyrics));
        Assert.Equal((40f, 52f), ultra.TitleFont(M.Queue));
        Assert.Equal((28f, 36f), portrait.TitleFont(M.Lyrics));
        Assert.Equal((24f, 30f), compact.TitleFont(M.Lyrics));
        foreach (var l in new[] { desktop, ultra, portrait, compact })
            Assert.Equal((20f, 28f), l.TitleFont(M.Visualizer));      // the thumb always reads Subtitle 20/28

        Assert.Equal((18f, 24f), desktop.MetaFont(M.Lyrics));
        Assert.Equal((18f, 24f), ultra.MetaFont(M.Artist));
        Assert.Equal((14f, 20f), desktop.MetaFont(M.Visualizer));
        Assert.Equal((14f, 20f), portrait.MetaFont(M.Lyrics));
        Assert.Equal((14f, 20f), compact.MetaFont(M.Lyrics));
    }

    [Fact]
    public void Seed_is_Resolve_without_a_previous_class()
    {
        Assert.Equal(L.Resolve(1920f, 1080f, null), L.Seed(1920f, 1080f));
        Assert.Equal(48f, L.TopBarH);
        Assert.Equal(112f, L.TransportDesktopH);
        Assert.Equal(96f, L.TransportCompactH);
        Assert.Equal(600f, L.WideEnterW);
    }

    // ── the identity block: the title's width and line budget ──────────────────────────────────────────────────────

    [Fact]
    public void Under_the_hero_the_title_runs_to_the_pane_gutter_not_the_cover_edge()
    {
        var desktop = L.Seed(1920f, 1080f);
        Assert.Equal(740f - L.Pad - 108f, desktop.TitleW(M.Lyrics));     // PaneX 740 − the 24 gutter − PadX 108 = 608 …
        Assert.True(desktop.TitleW(M.Lyrics) > desktop.HeroArt);           // … wider than the 536 cover it used to be cut at
        Assert.Equal(desktop.TitleW(M.Lyrics), desktop.TitleW(M.Queue));
        Assert.Equal(desktop.TitleW(M.Lyrics), desktop.TitleW(M.Artist));

        // Every stacked layout: never narrower than the art, never into the pane.
        for (float w = 600f; w <= 4000f; w += 37f)
            for (float h = 470f; h <= 2400f; h += 41f)
            {
                var l = L.Seed(w, h);
                if (l.IdentityIsRow) continue;
                Assert.True(l.TitleW(M.Lyrics) >= l.HeroArt, $"the title column is narrower than the hero at {w}×{h}");
                Assert.True(l.TitleX(M.Lyrics) + l.TitleW(M.Lyrics) <= l.PaneX - L.Pad + 0.01f, $"the title runs into the pane at {w}×{h}");
            }
    }

    [Fact]
    public void The_hero_title_takes_two_lines_where_the_block_clears_the_transport_and_the_card_title_one()
    {
        // The owner's window (≈2000×1320, Desktop): hero 560, top 160 → the title at 744; two lines + meta + chips end at 912,
        // well above the transport card's top (1320 − 24 − 112 = 1184) less its gutter.
        var owner = L.Seed(2000f, 1320f);
        Assert.Equal(A.Desktop, owner.Aspect);
        Assert.Equal(744f, owner.TitleY(M.Lyrics));
        Assert.Equal(2 * 52f + L.TitleGap + 24f + L.TitleGap + L.ChipsTop + L.ChipH, owner.TitleBlockH(M.Lyrics, 2));
        Assert.Equal(1184f, owner.TransportTop);
        Assert.Equal(2, owner.TitleMaxLines(M.Lyrics));

        foreach (var l in new[] { L.Seed(1920f, 1080f), L.Seed(3440f, 1440f), L.Seed(900f, 1600f), L.Seed(1100f, 440f), owner })
        {
            Assert.Equal(2, l.TitleMaxLines(M.Lyrics));
            Assert.Equal(2, l.TitleMaxLines(M.Queue));
            Assert.Equal(2, l.TitleMaxLines(M.Artist));
            Assert.Equal(1, l.TitleMaxLines(M.Visualizer));               // the now-playing card is a fixed 136 DIP
        }

        // A short desktop has no room for a second line: the 168 hero puts the title at 252, and two lines would end at 420,
        // past the transport card's top (500 − 136 = 364) less its gutter.
        var shortDesk = L.Seed(800f, 500f);
        Assert.Equal(A.Desktop, shortDesk.Aspect);
        Assert.Equal(1, shortDesk.TitleMaxLines(M.Lyrics));

        // Wherever two lines are granted, the whole block clears the transport card.
        for (float w = 600f; w <= 4000f; w += 53f)
            for (float h = 300f; h <= 2400f; h += 47f)
            {
                var l = L.Seed(w, h);
                if (l.TitleMaxLines(M.Lyrics) == 2)
                    Assert.True(l.TitleY(M.Lyrics) + l.TitleBlockH(M.Lyrics, 2) <= l.TransportTop - L.Pad, $"a two-line title reaches the transport at {w}×{h}");
            }
    }

    // ── the top bar ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_exit_button_carries_its_label_only_where_its_column_holds_it()
    {
        var owner = L.Seed(2000f, 1320f);
        Assert.Equal(4f * L.SelectorItemW + 3f * L.SelectorGap, owner.SelectorW);
        Assert.Equal((2000f - L.TopBarPadL - L.TopBarPadR - owner.SelectorW - 2f * L.TopBarGap) * 0.5f, owner.TopBarSideW);
        Assert.True(owner.ExitLabelShown);
        Assert.True(L.Seed(1920f, 1080f).ExitLabelShown);
        Assert.True(L.Seed(3440f, 1440f).ExitLabelShown);

        Assert.False(L.Seed(900f, 1600f).ExitLabelShown);                  // the icon-only classes show the glyph (§3.5)
        Assert.False(L.Seed(1100f, 440f).ExitLabelShown);
        var narrow = L.Seed(1000f, 800f);                                   // Desktop, but the column is 218 wide
        Assert.Equal(A.Desktop, narrow.Aspect);
        Assert.False(narrow.ExitLabelShown);

        // Widening never takes the label away: one false → true edge across the labelled classes (H = 1080: Desktop from 865).
        int flips = 0;
        bool prev = false;
        for (float w = 865f; w <= 4000f; w += 1f)
        {
            bool shown = L.Seed(w, 1080f).ExitLabelShown;
            if (shown != prev) { flips++; Assert.True(shown, $"the exit label disappeared while widening, at w={w}"); }
            prev = shown;
            if (shown) Assert.True(L.Seed(w, 1080f).TopBarSideW >= L.BarButtonW + L.BarButtonGap + L.ExitLabelW);
        }
        Assert.Equal(1, flips);
    }

    // ── the transport card ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_transport_card_spans_the_window_inside_its_gutters()
    {
        for (float w = 600f; w <= 4000f; w += 53f)
            for (float h = 300f; h <= 2400f; h += 47f)
            {
                var l = L.Seed(w, h);
                Assert.Equal(l.W, l.TransportLeft + l.TransportW + L.Pad, 3);           // never past the right edge
                Assert.Equal(l.H, l.TransportTop + l.TransportH + L.Pad, 3);            // the BOTTOM edge, not the top
                Assert.True(l.TransportLeft >= L.Pad);
            }
    }

    [Fact]
    public void The_volume_slider_shows_only_where_the_right_cluster_still_fits_the_card()
    {
        var owner = L.Seed(2000f, 1320f);
        Assert.Equal(2000f - 2f * L.Pad, owner.TransportW);
        Assert.Equal((owner.TransportW - 2f * L.TransportPadX - L.TransportCentreW) * 0.5f, owner.TransportSideW);
        Assert.True(owner.VolumeFits);
        Assert.True(L.Seed(1920f, 1080f).VolumeFits);
        Assert.True(L.Seed(3440f, 1440f).VolumeFits);

        var portrait = L.Seed(900f, 1600f);
        Assert.True(portrait.ShowVolume);                                   // the class offers it …
        Assert.False(portrait.VolumeFits);                                  // … but 282 DIP beside the cluster cannot hold it
        Assert.False(L.Seed(1100f, 440f).VolumeFits);                       // Compact never (mute glyph only)

        // The boards' minimal right cluster (heart · mute · device · more) always fits its half of the card.
        foreach (var l in new[] { L.Seed(1920f, 1080f), L.Seed(3440f, 1440f), portrait, L.Seed(1100f, 440f), owner })
            Assert.True(l.TransportSideW >= L.TransportRightMinW, $"the right cluster overflows the card at {l.W}×{l.H}");

        for (float w = 600f; w <= 4000f; w += 53f)
            for (float h = 300f; h <= 2400f; h += 47f)
            {
                var l = L.Seed(w, h);
                if (l.VolumeFits) Assert.True(l.ShowVolume && l.TransportSideW >= L.TransportRightMinW + L.TransportRightGap + L.VolumeSliderW);
            }
    }

    // ── the lyric caption ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_caption_is_centred_in_the_faces_region_at_72_percent()
    {
        var desktop = L.Seed(1920f, 1080f);
        Assert.Equal(1380f, desktop.CaptionW(M.Visualizer, galleryOpen: false));   // Q4(0.72 · 1920)
        Assert.Equal(270f, desktop.CaptionX(M.Visualizer, galleryOpen: false));
        Assert.Equal(960f, desktop.CaptionX(M.Visualizer, false) + desktop.CaptionW(M.Visualizer, false) * 0.5f);   // horizontally CENTRED

        // The gallery open: centred over the FACE (1920 − 492), never sliding under the pane.
        float region = 1920f - desktop.FaceRight(true);
        Assert.Equal(1028f, desktop.CaptionW(M.Visualizer, galleryOpen: true));    // Q4(0.72 · 1428)
        Assert.Equal(region * 0.5f, desktop.CaptionX(M.Visualizer, true) + desktop.CaptionW(M.Visualizer, true) * 0.5f);
        Assert.True(desktop.CaptionX(M.Visualizer, true) + desktop.CaptionW(M.Visualizer, true) <= region);

        // Compact Lyrics mode: the strip right of the art, flush with the transport card.
        var compact = L.Seed(1100f, 440f);
        Assert.Equal(compact.TransportLeft, compact.CaptionX(M.Lyrics, false));
        Assert.Equal(compact.TransportW, compact.CaptionW(M.Lyrics, false));

        // Inside the stage at every size, mode and gallery state.
        foreach (var mode in new[] { M.Lyrics, M.Visualizer })
            foreach (bool open in new[] { false, true })
                for (float w = 600f; w <= 4000f; w += 61f)
                    for (float h = 300f; h <= 2400f; h += 59f)
                    {
                        var l = L.Seed(w, h);
                        float x = l.CaptionX(mode, open), cw = l.CaptionW(mode, open);
                        Assert.True(x >= 0f && cw >= 0f && x + cw <= l.W + 0.01f, $"the caption leaves the stage at {w}×{h} ({mode}, gallery {open})");
                    }
    }

    [Fact]
    public void The_caption_sits_in_the_lower_third_above_the_transport_and_below_the_card()
    {
        foreach (var l in new[] { L.Seed(1920f, 1080f), L.Seed(2000f, 1320f), L.Seed(3440f, 1440f), L.Seed(900f, 1600f) })
        {
            Assert.Equal(l.TransportH + L.Pad + L.CaptionAboveTransport, l.CaptionBottom);   // above the controls and the hairline
            Assert.True(l.H - l.CaptionBottom >= l.H * 2f / 3f, $"the caption's foot is above the lower third at {l.W}×{l.H}");
        }

        // At its tallest (two active lines + both context lines) the block never climbs into the now-playing card.
        for (float w = 600f; w <= 4000f; w += 53f)
            for (float h = 461f; h <= 2400f; h += 37f)
            {
                var l = L.Seed(w, h);
                if (l.Aspect == A.Compact) continue;
                Assert.True(l.CaptionTopMin >= L.NowPlayingCardY + L.NowPlayingCardH, $"the caption reaches the now-playing card at {w}×{h}");
            }
    }

    [Fact]
    public void The_caption_type_is_the_hero_scale_by_height_with_small_context_lines()
    {
        Assert.Equal((44f, 52f), L.Seed(1920f, 1080f).CaptionFont);      // 0.04 · 1080 = 43.2 → the even 44
        Assert.Equal((52f, 60f), L.Seed(2000f, 1320f).CaptionFont);      // the owner's window
        Assert.Equal((56f, 64f), L.Seed(3440f, 1440f).CaptionFont);      // capped
        Assert.Equal((40f, 48f), L.Seed(1280f, 720f).CaptionFont);       // floored
        Assert.Equal((44f, 52f), L.Seed(900f, 1600f).CaptionFont);       // Portrait: width-capped (0.05 · 900 = 45)
        Assert.Equal((24f, 30f), L.Seed(1100f, 440f).CaptionFont);       // Compact: its title size
        Assert.Equal((20f, 26f), L.Seed(1920f, 1080f).CaptionContextFont);

        for (float w = 600f; w <= 4000f; w += 53f)
            for (float h = 300f; h <= 2400f; h += 47f)
            {
                var l = L.Seed(w, h);
                var (s, line) = l.CaptionFont;
                if (l.Aspect is A.Desktop or A.Ultrawide) Assert.InRange(s, L.CaptionActiveMin, L.CaptionActiveMax);
                if (l.Aspect == A.Portrait) Assert.InRange(s, L.CaptionActivePortraitMin, L.CaptionActiveMax);
                Assert.True(line > s);
                Assert.True(l.CaptionContextFont.Size < s, "a context line is smaller than the active line");
                Assert.Equal(l.Aspect != A.Compact && h >= L.CaptionContextMinH, l.CaptionShowsContext);
            }
    }

    [Fact]
    public void The_caption_clock_view_hands_off_from_the_dots_to_the_next_line_with_nothing_between()
    {
        const long lead = 140L, gap = 5000L;
        // A long intro (8 s): the dots breathe toward line 0 until line 0 resolves on the same lead.
        Assert.Equal((-1, true), Stage.Caption.View(-1, 0L, 0L, 8000L, 0L, lead, gap));
        Assert.Equal((-1, true), Stage.Caption.View(-1, 0L, 0L, 8000L, 7859L, lead, gap));
        Assert.Equal((-1, false), Stage.Caption.View(-1, 0L, 0L, 8000L, 7860L, lead, gap));
        Assert.Equal((-1, false), Stage.Caption.View(-1, 0L, 0L, 3000L, 0L, lead, gap));     // a short intro: no dots

        // A real break after line 4 (sung out at 20 s, line 5 at 30 s): dots from the sung-out point to the next line's lead.
        Assert.Equal((4, false), Stage.Caption.View(4, 20_000L, 30_000L, 1000L, 19_999L, lead, gap));
        Assert.Equal((4, true), Stage.Caption.View(4, 20_000L, 30_000L, 1000L, 20_000L, lead, gap));
        Assert.Equal((4, true), Stage.Caption.View(4, 20_000L, 30_000L, 1000L, 29_859L, lead, gap));
        Assert.Equal((4, false), Stage.Caption.View(4, 20_000L, 30_000L, 1000L, 29_860L, lead, gap));
        Assert.Equal((4, false), Stage.Caption.View(4, 0L, 0L, 1000L, 25_000L, lead, gap));    // no break reported: the line stays

        for (int a = -1; a <= 40; a++)
            foreach (bool dots in new[] { false, true })
            {
                int packed = Stage.Caption.Pack(a, dots);
                Assert.True(packed >= 0);
                Assert.Equal(a, Stage.Caption.AnchorOf(packed));
                Assert.Equal(dots, Stage.Caption.DotsOf(packed));
            }
    }

    [Fact]
    public void The_caption_slots_are_previous_active_next_and_the_dots_take_the_middle()
    {
        Assert.Equal((3, 4, 5), Stage.Caption.Slots(4, dots: false, context: true, count: 10));
        Assert.Equal((-1, 0, 1), Stage.Caption.Slots(0, false, true, 10));      // the first line has nothing above it
        Assert.Equal((8, 9, -1), Stage.Caption.Slots(9, false, true, 10));      // the last has nothing below
        Assert.Equal((4, -1, 5), Stage.Caption.Slots(4, dots: true, context: true, count: 10));   // a break: the line just sung · dots · the next
        Assert.Equal((-1, -1, 0), Stage.Caption.Slots(-1, true, true, 10));     // the intro's dots, line 0 waiting below
        Assert.Equal((-1, -1, 0), Stage.Caption.Slots(-1, false, true, 10));    // a short intro: line 0 waiting below
        Assert.Equal((-1, 4, -1), Stage.Caption.Slots(4, false, context: false, count: 10));      // Compact / a short window
        Assert.Equal((-1, -1, -1), Stage.Caption.Slots(4, true, false, 10));
        Assert.Equal((-1, -1, -1), Stage.Caption.Slots(0, false, true, 0));     // an empty document shows nothing
    }

    // ── modes ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ModeRules_coerce_toggle_and_caption()
    {
        Assert.Equal(4, Stage.ModeRules.Count);
        Assert.Equal(0, Stage.ModeRules.Coerce(-1));
        Assert.Equal(0, Stage.ModeRules.Coerce(4));
        Assert.Equal(0, Stage.ModeRules.Coerce(int.MinValue));
        Assert.Equal(0, Stage.ModeRules.Coerce(int.MaxValue));
        Assert.Equal(2, Stage.ModeRules.Coerce(2));
        Assert.Equal(3, Stage.ModeRules.Coerce(3));
        Assert.Equal(0, (int)M.Lyrics);        // the persisted ints are append-only
        Assert.Equal(1, (int)M.Visualizer);
        Assert.Equal(2, (int)M.Queue);
        Assert.Equal(3, (int)M.Artist);

        Assert.True(Stage.ModeRules.ShowsPane(M.Lyrics));
        Assert.True(Stage.ModeRules.ShowsPane(M.Queue));
        Assert.True(Stage.ModeRules.ShowsPane(M.Artist));
        Assert.False(Stage.ModeRules.ShowsPane(M.Visualizer));

        // The gallery toggle from another mode first switches to Visualizer and opens; from Visualizer it flips.
        var (m1, open1) = Stage.ModeRules.ToggleGallery(M.Lyrics, false);
        Assert.Equal(M.Visualizer, m1);
        Assert.True(open1);
        var (m2, open2) = Stage.ModeRules.ToggleGallery(M.Visualizer, true);
        Assert.Equal(M.Visualizer, m2);
        Assert.False(open2);
        var (m3, open3) = Stage.ModeRules.ToggleGallery(M.Visualizer, false);
        Assert.Equal(M.Visualizer, m3);
        Assert.True(open3);
        var (m4, open4) = Stage.ModeRules.ToggleGallery(M.Queue, true);   // an already-open flag is not a reason to stay put
        Assert.Equal(M.Visualizer, m4);
        Assert.True(open4);

        // ShowsCaption: over the face when asked for, in Lyrics mode when there is no pane — only with a timed line.
        foreach (var mode in new[] { M.Lyrics, M.Visualizer, M.Queue, M.Artist })
            foreach (bool overlay in new[] { false, true })
                foreach (bool timed in new[] { false, true })
                    foreach (bool pane in new[] { false, true })
                        Assert.Equal(timed && ((mode == M.Visualizer && overlay) || (mode == M.Lyrics && !pane)),
                                     Stage.ModeRules.ShowsCaption(mode, overlay, timed, pane));
    }

    // ── entry ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Entry_rules()
    {
        Assert.False(Stage.Entry.CanEnter(videoFullscreen: true));   // F11 owns a fullscreen video
        Assert.True(Stage.Entry.CanEnter(videoFullscreen: false));   // an EMPTY stage is allowed, as the rail ⛶ opens today
        Assert.Equal("stage:art", Stage.Entry.MorphKey);
    }

    // ── tone ────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Tone_progress_is_linear_and_lands_at_CrossFadeMs()
    {
        Assert.Equal(600f, Stage.Tone.CrossFadeMs);
        Assert.Equal(0f, Stage.Tone.Progress(0f));
        Assert.Equal(0.5f, Stage.Tone.Progress(300f));
        Assert.Equal(1f, Stage.Tone.Progress(600f));
        Assert.Equal(1f, Stage.Tone.Progress(900f));
        Assert.Equal(0f, Stage.Tone.Progress(-1f));

        float prev = 0f;
        for (float ms = 0f; ms <= 700f; ms += 5f)
        {
            float p = Stage.Tone.Progress(ms);
            Assert.True(p >= prev && p <= 1f, $"the cross-fade left [0,1] or went backwards at {ms} ms");
            prev = p;
        }
    }

    [Fact]
    public void Tone_scrim_is_deep_under_a_pane_and_light_under_a_face()
    {
        Assert.True(Stage.Tone.ScrimA > Stage.Tone.ScrimVisualizerA);
        Assert.True(Stage.Tone.BaseFieldVisualizerA > Stage.Tone.BaseFieldA);
        Assert.True(Stage.Tone.ScrimA < 1f && Stage.Tone.ScrimVisualizerA > 0f);
    }
}
