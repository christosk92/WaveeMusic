// ── Wavee.Tests/VideoSurfaceViewRulesTests.cs — the video surfaces' view-side decisions, as pure rules (C6, X16) ──────
//
// What `Video.UI.cs` used to switch over inline: the aspect policy's two vocabularies (`AspectMapping`), the docked cap's
// fit effect (`DockedFit`), the overlay surface's size and offset per placement (`PipOverlay`), and the PiP geometry as
// rules over explicit bounds (`PipBounds`). The shipped numbers (`Pip`, `PipGesture`) are pinned in
// VideoSurfaceRulesTests / VideoSurfaceGestureTests; these facts pin that the bounds overloads agree with them and that
// other bounds move the geometry the way a theme's tier policy will need. (The playback-side video rules live in
// VideoRulesTests.)

using FluentGpu.Media;
using Xunit;

using static Wavee.Video;

namespace Wavee.Tests;

public class VideoAspectMappingTests
{
    [Theory]
    [InlineData(AspectPreference.Fit, VideoAspectMode.Uniform)]
    [InlineData(AspectPreference.Crop, VideoAspectMode.UniformToFill)]
    [InlineData(AspectPreference.Stretch, VideoAspectMode.Fill)]
    [InlineData(AspectPreference.Native, VideoAspectMode.Native)]
    [InlineData(AspectPreference.Custom, VideoAspectMode.Custom)]
    public void Each_preference_maps_to_its_engine_mode_and_back(AspectPreference preference, VideoAspectMode engine)
    {
        Assert.Equal(engine, AspectMapping.ToEngine(preference));
        Assert.Equal(preference, AspectMapping.FromEngine(engine));
    }

    [Fact]
    public void An_unknown_value_on_either_side_reads_as_fit()
    {
        Assert.Equal(VideoAspectMode.Uniform, AspectMapping.ToEngine((AspectPreference)200));
        Assert.Equal(AspectPreference.Fit, AspectMapping.FromEngine((VideoAspectMode)200));
    }
}

public class VideoDockedFitTests
{
    const float RailW = 340f;

    static DockedFit.Input Input(string key = "a", string? fittedFor = "a", bool pinned = false, float pinnedHeight = 150f,
        int decoded = 0, int decodedH = 0, int manifest = 0, int manifestH = 0, int catalogue = 0, int catalogueH = 0, float remaining = 10_000f)
        => new(key, fittedFor, pinned, pinnedHeight, RailW, decoded, decodedH, manifest, manifestH, catalogue, catalogueH, remaining);

    [Fact]
    public void With_no_size_known_the_cap_fits_16_by_9()
    {
        var fit = DockedFit.Decide(Input());

        Assert.Equal("none", fit.DimSource);
        Assert.Equal(0, fit.NaturalW);
        Assert.Equal(Shell.FitDockedVideoHeight(RailW, 0, 0), fit.Height, 3);
        Assert.True(fit.WritesHeight);
        Assert.False(fit.Pinned);
    }

    [Fact]
    public void The_most_recent_fact_wins_decoder_then_manifest_then_catalogue()
    {
        var all = DockedFit.Decide(Input(decoded: 1920, decodedH: 1080, manifest: 1280, manifestH: 720, catalogue: 640, catalogueH: 360));
        Assert.Equal("decoder", all.DimSource);
        Assert.Equal(1920, all.NaturalW);
        Assert.Equal(1080, all.NaturalH);

        var noDecoder = DockedFit.Decide(Input(manifest: 1280, manifestH: 720, catalogue: 640, catalogueH: 360));
        Assert.Equal("manifest", noDecoder.DimSource);

        var catalogueOnly = DockedFit.Decide(Input(catalogue: 640, catalogueH: 360));
        Assert.Equal("track", catalogueOnly.DimSource);
    }

    [Fact]
    public void A_wide_stream_gets_a_wide_cap_not_the_16_by_9_floor()
    {
        var fit = DockedFit.Decide(Input(decoded: 2350, decodedH: 1000));

        Assert.Equal(Shell.FitDockedVideoHeight(RailW, 2350, 1000), fit.Height, 3);
        Assert.True(fit.Height < Shell.FitDockedVideoHeight(RailW, 0, 0));
    }

    [Fact]
    public void The_remaining_rail_height_caps_the_fit()
    {
        var fit = DockedFit.Decide(Input(decoded: 1000, decodedH: 2000, remaining: 200f));

        Assert.Equal(Shell.DockedCapCeiling(200f), fit.Height, 3);
    }

    [Fact]
    public void A_pinned_cap_keeps_its_height_for_the_same_source_and_publishes_nothing()
    {
        var fit = DockedFit.Decide(Input(pinned: true, pinnedHeight: 222f, decoded: 1920, decodedH: 1080));

        Assert.True(fit.Pinned);
        Assert.Equal(222f, fit.Height);
        Assert.False(fit.WritesHeight);
        Assert.False(fit.SourceChanged);
    }

    [Fact]
    public void A_new_source_unpins_the_cap_and_refits()
    {
        var fit = DockedFit.Decide(Input(key: "b", fittedFor: "a", pinned: true, pinnedHeight: 222f, decoded: 1920, decodedH: 1080));

        Assert.True(fit.SourceChanged);
        Assert.False(fit.Pinned);
        Assert.True(fit.WritesHeight);
        Assert.Equal(Shell.FitDockedVideoHeight(RailW, 1920, 1080), fit.Height, 3);
    }

    [Fact]
    public void The_first_fit_counts_as_a_source_change()
    {
        Assert.True(DockedFit.Decide(Input(key: "", fittedFor: null)).SourceChanged);
        Assert.False(DockedFit.Decide(Input(key: "", fittedFor: "")).SourceChanged);
    }
}

public class VideoPipBoundsTests
{
    const float VpW = 1440f, VpH = 900f;

    [Fact]
    public void The_classic_bounds_are_the_shipped_constants()
    {
        var b = PipBounds.Classic;

        Assert.Equal(Pip.MinW, b.MinW);
        Assert.Equal(Pip.MinH, b.MinH);
        Assert.Equal(Pip.ReserveBottom, b.ReserveBottom);
        Assert.Equal(Pip.ReserveTop, b.ReserveTop);
        Assert.Equal(PipGesture.Margin, b.Margin);
    }

    [Fact]
    public void Every_bounds_overload_agrees_with_the_original_signature_at_the_classic_bounds()
    {
        var b = PipBounds.Classic;

        Assert.Equal(Pip.FitHeight(360f, 9f / 16f, 900f), Pip.FitHeight(360f, 9f / 16f, 900f, in b));
        Assert.Equal(Pip.Anchor(360f, 202f, VpW, VpH), Pip.Anchor(360f, 202f, VpW, VpH, in b));
        Assert.Equal(PipGesture.ClampX(5000f, VpW, 360f), PipGesture.ClampX(5000f, VpW, 360f, in b));
        Assert.Equal(PipGesture.ClampY(-40f, VpH, 202f), PipGesture.ClampY(-40f, VpH, 202f, in b));
        Assert.Equal(PipGesture.Reserve(true, false, 202f), PipGesture.Reserve(true, false, 202f, in b));
        Assert.Equal(
            PipGesture.Resize(PipEdge.Left | PipEdge.Top, 1064f, 610f, 360f, 202f, -100f, -50f, VpW, VpH),
            PipGesture.Resize(PipEdge.Left | PipEdge.Top, 1064f, 610f, 360f, 202f, -100f, -50f, VpW, VpH, in b));
    }

    [Fact]
    public void A_bigger_margin_and_player_bar_move_the_anchor_and_the_clamps_with_them()
    {
        var roomy = new PipBounds(MinW: 300f, MinH: 170f, ReserveBottom: 100f, ReserveTop: 32f, Margin: 24f);

        var (x, y) = Pip.Anchor(360f, 202f, VpW, VpH, in roomy);
        Assert.Equal(VpW - 360f - 24f, x);
        Assert.Equal(VpH - 202f - 100f - 24f, y);

        Assert.Equal(24f, PipGesture.ClampX(0f, VpW, 360f, in roomy));
        Assert.Equal(VpH - 202f - 100f - 24f, PipGesture.ClampY(5000f, VpH, 202f, in roomy));
        Assert.Equal(202f + 24f, PipGesture.Reserve(true, false, 202f, in roomy));
    }

    [Fact]
    public void The_resize_floors_and_the_fit_floor_are_the_bounds_not_the_constants()
    {
        var tight = new PipBounds(MinW: 400f, MinH: 220f, ReserveBottom: 72f, ReserveTop: 32f, Margin: 16f);

        var r = PipGesture.Resize(PipEdge.Right | PipEdge.Bottom, 1064f, 400f, 500f, 300f, -5000f, -5000f, VpW, VpH, in tight);
        Assert.Equal(400f, r.W);
        Assert.Equal(220f, r.H);

        Assert.Equal(220f, Pip.FitHeight(100f, 9f / 16f, 900f, in tight));        // the floor wins over a tiny width
    }

    [Fact]
    public void A_short_window_still_gets_the_floor_height_under_wider_reserves()
    {
        var reserved = new PipBounds(MinW: 240f, MinH: 135f, ReserveBottom: 200f, ReserveTop: 100f, Margin: 16f);

        Assert.Equal(135f, Pip.FitHeight(360f, 16f / 9f, 400f, in reserved));
    }
}

public class VideoPipOverlayTests
{
    [Fact]
    public void Fullscreen_fills_the_window_from_the_origin()
    {
        Assert.Equal(1440f, PipOverlay.Width(SurfacePlacement.Fullscreen, 1440f, 360f, 500f));
        Assert.Equal(900f, PipOverlay.Height(SurfacePlacement.Fullscreen, 900f, 202f, 300f));

        var o = PipOverlay.Offset(SurfacePlacement.Fullscreen, 1440f, 900f, placed: true, 10f, 20f, 360f, 202f, 40f, 50f);

        Assert.True(o.Identity);
    }

    [Fact]
    public void Docked_follows_the_reservations_slot()
    {
        Assert.Equal(500f, PipOverlay.Width(SurfacePlacement.Docked, 1440f, 360f, 500f));
        Assert.Equal(300f, PipOverlay.Height(SurfacePlacement.Docked, 900f, 202f, 300f));

        var o = PipOverlay.Offset(SurfacePlacement.Docked, 1440f, 900f, placed: false, 10f, 20f, 360f, 202f, 40f, 50f);

        Assert.False(o.Identity);
        Assert.Equal(40f, o.X);
        Assert.Equal(50f, o.Y);
    }

    [Theory]
    [InlineData(SurfacePlacement.Floating)]
    [InlineData(SurfacePlacement.None)]
    [InlineData(SurfacePlacement.Detached)]
    public void Anything_else_is_the_floating_card(SurfacePlacement resolved)
    {
        Assert.Equal(360f, PipOverlay.Width(resolved, 1440f, 360f, 500f));
        Assert.Equal(202f, PipOverlay.Height(resolved, 900f, 202f, 300f));
    }

    [Fact]
    public void An_unplaced_card_parks_at_the_anchored_corner()
    {
        var o = PipOverlay.Offset(SurfacePlacement.Floating, 1440f, 900f, placed: false, 0f, 0f, 360f, 202f, 0f, 0f);

        var anchor = Pip.Anchor(360f, 202f, 1440f, 900f);
        Assert.False(o.Identity);
        Assert.Equal(PipGesture.ClampX(anchor.X, 1440f, 360f), o.X);
        Assert.Equal(PipGesture.ClampY(anchor.Y, 900f, 202f), o.Y);
    }

    [Fact]
    public void A_placed_card_stays_where_the_user_left_it_clamped_inside_the_viewport()
    {
        var inside = PipOverlay.Offset(SurfacePlacement.Floating, 1440f, 900f, placed: true, 300f, 200f, 360f, 202f, 0f, 0f);
        Assert.Equal(300f, inside.X);
        Assert.Equal(200f, inside.Y);

        var offscreen = PipOverlay.Offset(SurfacePlacement.Floating, 1440f, 900f, placed: true, 5000f, -40f, 360f, 202f, 0f, 0f);
        Assert.Equal(1440f - 360f - 16f, offscreen.X);
        Assert.Equal(16f, offscreen.Y);
    }

    [Fact]
    public void The_floating_offset_honours_explicit_bounds()
    {
        var roomy = new PipBounds(240f, 135f, 72f, 32f, Margin: 40f);

        var o = PipOverlay.Offset(SurfacePlacement.Floating, 1440f, 900f, placed: true, 0f, 0f, 360f, 202f, 0f, 0f, in roomy);

        Assert.Equal(40f, o.X);
        Assert.Equal(40f, o.Y);
    }
}
