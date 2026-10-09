// ── Wavee.Tests/ArtistBleedTests.cs — the EXPERIMENTAL artist bleed's pure rules and its material protocol ───────────────
//
// `ArtistBleed` (Entities/Artist.Bleed.cs) is the artist hero photo drawn from the window top through the shell material
// channel. Everything it decides is pure: when it applies, how long the photo holds full strength (the hero's own expanded
// fade), where the solid ground's top edge sits (the hero's presented bottom), the photo's parallax, the scrim and the span.
// The protocol half pins that a backdrop rides the SAME ownership outcome as the tint: a successor's claim, a neutral write
// or a stray publish can never leave a stale photo behind.

using FluentGpu.Foundation;
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
    public void The_scrim_is_a_partial_veil_in_both_themes_and_as_tall_as_the_chrome()
    {
        Assert.InRange(ArtistBleed.ScrimTopAlpha(light: true), 0.01f, 0.99f);
        Assert.InRange(ArtistBleed.ScrimTopAlpha(light: false), 0.01f, 0.99f);
        Assert.Equal(132f, ArtistBleed.ScrimHeight(132f));
        Assert.Equal(0f, ArtistBleed.ScrimHeight(-4f));
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
