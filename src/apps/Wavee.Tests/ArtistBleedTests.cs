// ── Wavee.Tests/ArtistBleedTests.cs — the EXPERIMENTAL artist bleed's pure rules and its material protocol ───────────────
//
// `ArtistBleed` (Entities/Artist.Bleed.cs) is the artist hero photo drawn from the window top through the shell material
// channel. Everything it decides is pure: when it applies, how long the photo holds full strength (the hero's own expanded
// fade), where the solid ground's top edge sits (the hero's presented bottom), the photo's parallax, the scrim and the span.
// The protocol half pins that a backdrop rides the SAME ownership outcome as the tint: a successor's claim, a neutral write
// or a stray publish can never leave a stale photo behind.

using FluentGpu.Foundation;
using FluentGpu.Render;
using FluentGpu.Signals;
using Xunit;

namespace Wavee.Tests;

public class ArtistBleedGateTests
{
    [Fact]
    public void It_applies_with_the_switch_on_tinted_surfaces_and_a_header_photo()
    {
        Assert.True(ArtistBleed.Applies(true, WashLevel.Subtle, "u"));
        Assert.True(ArtistBleed.Applies(true, WashLevel.Rich, "u"));
    }

    [Fact]
    public void It_does_not_apply_without_the_switch_the_tint_or_a_photo()
    {
        Assert.False(ArtistBleed.Applies(false, WashLevel.Rich, "u"));
        Assert.False(ArtistBleed.Applies(true, WashLevel.Off, "u"));
        Assert.False(ArtistBleed.Applies(true, WashLevel.Rich, null));
        Assert.False(ArtistBleed.Applies(true, WashLevel.Rich, ""));
    }
}

public class ArtistBleedScrollCurveTests
{
    [Fact]
    public void The_photo_holds_full_strength_until_the_heros_own_fade_starts()
    {
        const float collapse = 300f;
        Assert.Equal(1f, ArtistBleed.HeroVisible(0, collapse));
        Assert.Equal(1f, ArtistBleed.HeroVisible(ArtistHeroLayout.ExpandedFadeStart(collapse), collapse));
        Assert.Equal(1f, ArtistBleed.HeroVisible(-40, collapse));   // an overscroll
    }

    [Fact]
    public void The_photo_is_gone_at_the_collapse_and_never_increases_on_the_way()
    {
        const float collapse = 300f;
        Assert.Equal(0f, ArtistBleed.HeroVisible(collapse, collapse));
        Assert.Equal(0f, ArtistBleed.HeroVisible(collapse + 500, collapse));
        float prev = 1f;
        for (int o = 0; o <= 300; o++)
        {
            float v = ArtistBleed.HeroVisible(o, collapse);
            Assert.InRange(v, 0f, 1f);
            Assert.True(v <= prev + 1e-6f, $"HeroVisible rose at {o}");
            prev = v;
        }
    }

    [Fact]
    public void A_collapse_shorter_than_the_fade_distance_still_resolves()
    {
        // ExpandedFadeStart(60) is 0: the whole collapse is the fade.
        Assert.Equal(1f, ArtistBleed.HeroVisible(0, 60f));
        Assert.Equal(0f, ArtistBleed.HeroVisible(60, 60f));
        Assert.InRange(ArtistBleed.HeroVisible(30, 60f), 0.01f, 0.99f);
    }

    [Fact]
    public void The_grounds_top_edge_rides_the_heros_presented_bottom_down_to_the_floor()
    {
        Assert.Equal(400f, ArtistBleed.HeroBottom(0, 400f, 0f));
        Assert.Equal(250f, ArtistBleed.HeroBottom(150, 400f, 0f));
        Assert.Equal(0f, ArtistBleed.HeroBottom(900, 400f, 0f));
        Assert.Equal(56f, ArtistBleed.HeroBottom(900, 400f, 56f));
    }
}

public class ArtistBleedGeometryTests
{
    [Fact]
    public void The_photo_nets_the_hero_scroll_less_the_media_parallax()
    {
        Assert.Equal(0f, ArtistBleed.ParallaxY(0));
        Assert.Equal(-100f * (1f - ArtistHeroLayout.PhotoParallaxFraction), ArtistBleed.ParallaxY(100), 3);
    }

    [Fact]
    public void A_top_overpan_never_slides_the_photo_down()
    {
        Assert.Equal(0f, ArtistBleed.ParallaxY(-40));
    }

    [Theory]
    [InlineData(0.0, 1f)]
    [InlineData(37.3, 2f)]
    [InlineData(150.0, 1.25f)]
    [InlineData(900.0, 1.5f)]
    public void The_strip_and_the_riser_tile_the_clip_at_one_line_for_any_scroll(double offset, float scale)
    {
        const float photoH = 440f, floor = 56f;
        float line = ArtistBleed.SnapToPixel(ArtistBleed.RiserTop(offset, photoH, floor, photoH), scale);
        // The strip is a clip-tall box shifted up so its bottom lands on the line; the riser starts on it.
        Assert.Equal(line, ArtistBleed.StripShift(line, photoH) + photoH, 3);
        Assert.InRange(line, floor, photoH);
        // A device pixel boundary.
        Assert.Equal(line * scale, MathF.Round(line * scale), 3);
    }

    [Fact]
    public void The_ground_line_never_leaves_the_photos_clip()
    {
        Assert.Equal(300f, ArtistBleed.RiserTop(0, 400f, 0f, 300f));   // a hero taller than the photo: the ground below already stands
        Assert.Equal(0f, ArtistBleed.RiserTop(900, 400f, 0f, 400f));
        Assert.Equal(440f, ArtistBleed.RiserTop(-40, 440f, 56f, 440f)); // an overscroll keeps the line at rest
    }

    [Fact]
    public void The_scrim_is_one_partial_veil_in_both_themes_and_as_tall_as_the_chrome()
    {
        // The field is dark in both themes, so the scrim is a single constant, not a per-theme pair.
        Assert.Equal(ArtistBleed.ScrimTop, ArtistBleed.ScrimTopAlpha());
        Assert.InRange(ArtistBleed.ScrimTopAlpha(), 0.01f, 0.99f);
        Assert.Equal(132f, ArtistBleed.ScrimHeight(132f));
        Assert.Equal(0f, ArtistBleed.ScrimHeight(-4f));
    }
}

public class ArtistBleedFrameTests
{
    static readonly float[] Aspects = [1.5f, 2.4f, 3.6f];
    static readonly float[] CardWidths = [600f, 1200f, 1900f];
    static readonly float[] PhotoHeights = [320f, 440f];
    static readonly float[] Tops = [40f, 92f, 124f];

    [Fact]
    public void The_cards_photo_box_translated_by_the_pose_is_the_shells_box_and_crops_the_same_pixels()
    {
        foreach (float aspect in Aspects)
        foreach (float cardW in CardWidths)
        foreach (float photoH in PhotoHeights)
        foreach (float rectY in Tops)
        foreach (float poseY in Tops)   // equal to rectY and not
        {
            var f = ArtistBleed.FrameFor(cardW, poseY, rectY, photoH);

            // The shell's inner photo box, in window coordinates: top 0, the card's final width, the taller chrome plus the photo.
            var shell = new RectF(0f, 0f, cardW, MathF.Max(rectY, poseY) + photoH);
            // The card's box, in card-local coordinates, translated by the card's presented top (poseY).
            var card = new RectF(0f, f.Top + poseY, f.Width, f.Height);
            Assert.Equal(shell, card);

            int srcH = 1000;
            int srcW = (int)MathF.Round(srcH * aspect);
            var (shellDraw, shellUv) = SceneRecorder.ImageContentFit(ImageFit.Cover, new RectF(0f, 0f, f.Width, f.Height), srcW, srcH,
                                                                     ArtistHeroLayout.PhotoFocusX, ArtistBleed.PhotoFocusY);
            var (cardDraw, cardUv) = SceneRecorder.ImageContentFit(ImageFit.Cover, new RectF(0f, 0f, card.W, card.H), srcW, srcH,
                                                                   ArtistHeroLayout.PhotoFocusX, ArtistBleed.PhotoFocusY);
            Assert.Equal(shellDraw, cardDraw);
            Assert.Equal(shellUv, cardUv);
        }
    }

    [Theory]
    [InlineData(1200f, 92f, 92f, 440f)]
    [InlineData(1200f, 40f, 124f, 320f)]
    [InlineData(600f, 124f, 40f, 440f)]
    public void The_frame_is_the_taller_chrome_plus_the_photo_and_rises_by_the_pose(float cardW, float poseY, float rectY, float photoH)
    {
        var f = ArtistBleed.FrameFor(cardW, poseY, rectY, photoH);
        Assert.Equal(MathF.Max(rectY, poseY) + photoH, f.Height);
        Assert.Equal(-poseY, f.Top);
        Assert.Equal(cardW, f.Width);
    }

    [Fact]
    public void The_photo_is_unscaled_and_focused_on_the_top()
    {
        Assert.Equal(1f, ArtistBleed.PhotoScale);
        Assert.Equal(0f, ArtistBleed.PhotoFocusY);
    }
}

public class ArtistBleedHandOverTests
{
    [Theory]
    [InlineData(0f)]
    [InlineData(0.5f)]
    [InlineData(0.998f)]
    public void The_cards_layers_stay_until_the_shells_are_fully_present(float presence)
        => Assert.Equal(1f, ArtistBleed.CardLayerOpacity(true, presence));

    [Fact]
    public void The_cards_layers_yield_in_one_step_at_full_presence()
    {
        Assert.Equal(0f, ArtistBleed.CardLayerOpacity(true, 1f));
        Assert.Equal(0f, ArtistBleed.CardLayerOpacity(true, 0.999f));
    }

    [Fact]
    public void The_hand_over_eases_the_cards_layers_out_only_once_the_shell_is_fully_present()
    {
        // No hand-over yet: nothing yields. The hand-over never starts before presence 1, however far it has run.
        Assert.Equal(1f, ArtistBleed.CardLayerOpacity(true, 1f, 0f));
        Assert.Equal(1f, ArtistBleed.CardLayerOpacity(true, 0.9f, 1f));
        float prev = 1f;
        for (float h = 0f; h <= 1f; h += 0.1f)
        {
            float o = ArtistBleed.CardLayerOpacity(true, 1f, h);
            Assert.InRange(o, 0f, prev);   // monotone, never a step back up
            prev = o;
        }
        Assert.Equal(0f, ArtistBleed.CardLayerOpacity(true, 1f, 1f));
        Assert.Equal(0.5f, ArtistBleed.CardLayerOpacity(true, 1f, 0.5f), 5);
        Assert.Equal(1f, ArtistBleed.CardLayerOpacity(false, 1f, 1f));
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.7f)]
    [InlineData(1f)]
    public void Nothing_yields_when_the_shell_does_not_draw_the_photo(float presence)
        => Assert.Equal(1f, ArtistBleed.CardLayerOpacity(false, presence));

    [Fact]
    public void The_chrome_ink_mix_is_the_presence_times_the_hero_still_showing()
    {
        Assert.Equal(0f, ArtistBleed.ChromeInkMix(0f, 1f));
        Assert.Equal(1f, ArtistBleed.ChromeInkMix(1f, 1f));
        Assert.Equal(0f, ArtistBleed.ChromeInkMix(1f, 0f));
        Assert.Equal(0.25f, ArtistBleed.ChromeInkMix(0.5f, 0.5f), 5);
        Assert.Equal(1f, ArtistBleed.ChromeInkMix(3f, 2f));   // clamped both ways
        Assert.Equal(0f, ArtistBleed.ChromeInkMix(-1f, 1f));
        float prev = 0f;
        for (int i = 0; i <= 10; i++)
        {
            float v = ArtistBleed.ChromeInkMix(i / 10f, 0.8f);
            Assert.True(v >= prev - 1e-6f);
            prev = v;
        }
        prev = 0f;
        for (int i = 0; i <= 10; i++)
        {
            float v = ArtistBleed.ChromeInkMix(0.8f, i / 10f);
            Assert.True(v >= prev - 1e-6f);
            prev = v;
        }
    }

    [Fact]
    public void The_ink_is_the_themes_at_zero_and_the_medias_at_one()
    {
        var a = ColorF.FromRgba(10, 20, 30, 255);
        var b = ColorF.FromRgba(240, 230, 220, 255);
        Assert.Equal(a, ArtistBleed.Ink(a, b, 0f));
        Assert.Equal(b, ArtistBleed.Ink(a, b, 1f));
        Assert.Equal(a, ArtistBleed.Ink(a, b, -3f));
        Assert.Equal(b, ArtistBleed.Ink(a, b, 3f));
    }
}

public class ArtistBleedMaterialProtocolTests
{
    static readonly ColorF Tint = ColorF.FromRgba(40, 60, 90, 255);

    static ShellBackdrop Backdrop(string key) => new("u:" + key, 440f, 440f, 0f, new Signal<double>(0.0), 344f, 1280, 560, key);

    static Signal<ShellMaterialState> Slot() => new(default);

    [Fact]
    public void A_claim_with_a_backdrop_stores_it()
    {
        var slot = Slot();
        var owner = new object();
        var bd = Backdrop("a");
        ShellMaterial.Publish(slot, owner, isClaim: true, definite: false, Tint, wash: null, bd);
        Assert.Same(bd, slot.Peek().Backdrop);
        Assert.Same(owner, slot.Peek().Owner);
    }

    [Fact]
    public void A_second_owners_claim_without_a_backdrop_clears_it()
    {
        var slot = Slot();
        var a = new object();
        var b = new object();
        ShellMaterial.Publish(slot, a, isClaim: true, definite: false, Tint, wash: null, Backdrop("a"));
        // The successor knows its colour: a known-colour write that carries no backdrop.
        ShellMaterial.Publish(slot, b, isClaim: true, definite: false, Tint, wash: null);
        Assert.Null(slot.Peek().Backdrop);
        Assert.Same(b, slot.Peek().Owner);
    }

    [Fact]
    public void A_successor_that_does_not_know_its_colour_yet_holds_the_tint_but_drops_the_backdrop()
    {
        var slot = Slot();
        var a = new object();
        var b = new object();
        ShellMaterial.Publish(slot, a, isClaim: true, definite: false, Tint, wash: null, Backdrop("a"));
        ShellMaterial.Publish(slot, b, isClaim: true, definite: false, tint: null, wash: null);
        Assert.Equal(Tint, slot.Peek().Tint);
        Assert.Null(slot.Peek().Backdrop);
    }

    [Fact]
    public void A_neutral_write_clears_the_backdrop()
    {
        var slot = Slot();
        var a = new object();
        ShellMaterial.Publish(slot, a, isClaim: true, definite: false, Tint, wash: null, Backdrop("a"));
        ShellMaterial.Publish(slot, a, isClaim: false, definite: true, tint: null, wash: null);
        Assert.Null(slot.Peek().Backdrop);
        Assert.Null(slot.Peek().Tint);
    }

    [Fact]
    public void An_owner_refresh_without_a_colour_still_carries_its_backdrop()
    {
        var slot = Slot();
        var a = new object();
        // The claim lands before the palette has graded: nothing to show yet, no backdrop either.
        ShellMaterial.Publish(slot, a, isClaim: true, definite: false, tint: null, wash: null);
        Assert.Null(slot.Peek().Backdrop);
        // The body comes up with its backdrop while the colour is still unknown: the refresh must store it.
        var bd = Backdrop("a");
        ShellMaterial.Publish(slot, a, isClaim: false, definite: false, tint: null, wash: null, bd);
        Assert.Same(bd, slot.Peek().Backdrop);
        Assert.Same(a, slot.Peek().Owner);
        // A later geometry change (a tier switch) updates it while the colour is still unknown.
        var bd2 = Backdrop("a") with { PhotoHeight = 320f };
        ShellMaterial.Publish(slot, a, isClaim: false, definite: false, tint: null, wash: null, bd2);
        Assert.Same(bd2, slot.Peek().Backdrop);
    }

    [Fact]
    public void A_stray_publish_from_a_non_owner_writes_nothing()
    {
        var slot = Slot();
        var a = new object();
        var stray = new object();
        var bd = Backdrop("a");
        ShellMaterial.Publish(slot, a, isClaim: true, definite: false, Tint, wash: null, bd);
        ShellMaterial.Publish(slot, stray, isClaim: false, definite: false, Tint, wash: null, Backdrop("stray"));
        Assert.Same(bd, slot.Peek().Backdrop);
        Assert.Same(a, slot.Peek().Owner);
    }
}
