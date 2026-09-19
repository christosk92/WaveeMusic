// ── Wavee.Tests/ArtworkAndAccentTests.cs — Controls.Artwork's decode/layout split, and the one accent ladder ───────
//
// Two regressions pinned together because both are "the last mile disagrees with the rest of the pipe":
//
//   · Controls.Artwork's unscaled branch fed a BUCKETED size into the ImageEl's LAYOUT extent
//     (`Ui.Image(url, dw, dh, …)`) instead of into its DECODE hint, so a slot whose edge was not already a multiple
//     of 8 (e.g. a 28-DIP tile bucketing to 32) laid out past its box and the wrapping BoxEl's clip silently cropped
//     the difference. `Controls.ArtworkDecode` is the pure decision behind the fix.
//
//   · Three of the four accent ladders (`Playlist.UI.AccentOf`, `Album.Page.AccentNow`, `Artist.Page.AccentFor`)
//     hand-rolled their own payload-hex → ColorF conversion instead of routing it through `Detail.AccentFor`'s
//     `Design.Palette.ChromeFromPayload`, so a coverless page's payload accent skipped the same `Lift` every graded
//     cover gets. `Detail.AccentFor` is now the one ladder; the others delegate.
//
// `Controls.ArtworkDecode` and `Playlist.AccentOf` are PUBLIC, not internal: this assembly has no
// `InternalsVisibleTo` (Spotify.Connect.cs, Palette.Host.cs), so a pure decision extracted for a fact here has to be
// reachable from a public surface — the same reason `Detail.AccentFor` and `VerticalLayout.ArtworkDecodePx` already
// are. `Album.Page.AccentNow` / `Artist.Page.AccentFor` stay private (nested instance methods) — their only payload
// rung is a hardcoded 0, pinned below via `Detail.AccentFor(url, 0)` directly, which is exactly what they now call.

using FluentGpu.Dsl;          // Tok — the semantic brush accessors the accent ladder falls back to
using FluentGpu.Foundation;
using Wavee;
using Xunit;

namespace Wavee.Tests;

// ══ FIX 1 — the unscaled decode branch: bucketed for the DECODE, never for the LAYOUT ═══════════════════════════════
//
// Pure statics only: no Entities.Boot, no engine, no window (D17) — same footing as DesignTests.cs.
public class ArtworkDecodeTests
{
    [Fact]
    public void A_28_DIP_slot_decodes_at_32_while_the_layout_extent_stays_28()
    {
        // Controls.Artwork's wrapping BoxEl always takes the CALLER's real width/height (28, unbucketed) — this pure
        // helper only ever returns the DECODE size, which is the one thing that changed.
        var (dw, dh, aspect) = Controls.ArtworkDecode(28f, 28f, 0);
        Assert.Equal(32, dw);
        Assert.Equal(32, dh);
        Assert.Equal(1f, aspect);
    }

    [Theory]
    [InlineData(1f, 8)]      // a tiny edge still costs one whole bucket, never a fraction of one
    [InlineData(8f, 8)]      // already on the grid: no rounding past it
    [InlineData(9f, 16)]     // one px past a bucket boundary still costs a whole bucket
    [InlineData(200f, 200)]  // already a clean multiple of 8: untouched
    [InlineData(44f, 48)]    // User.UI.cs:453 / Concert.Page.cs:1587 / Shell.UI.cs:1702 shape
    [InlineData(36f, 40)]    // User.UI.cs:476 shape
    public void The_decode_edge_is_bucketed_up_to_8_never_down(float edge, int expectedDw)
        => Assert.Equal(expectedDw, Controls.ArtworkDecode(edge, edge, 0).DecodeW);

    [Fact]
    public void A_non_square_slot_keeps_its_own_aspect_and_derives_height_from_it()
    {
        // Album.Page.cs:948's release-panel art: 200×116.
        var (dw, dh, aspect) = Controls.ArtworkDecode(200f, 116f, 0);
        Assert.Equal(200, dw);
        Assert.Equal(200f / 116f, aspect, 4);
        // dh is NOT an independently-bucketed 116 (which would round to 120) — it is derived from (dw, aspect)
        // with the EXACT arithmetic the engine's Reconciler.ImageDecodeTarget performs once dw is handed to the
        // fluid Ui.Image(ImageFit, aspect, decodePx, …) overload as a decode hint (h = round(w / aspect)). Shimmer
        // and the real image must agree on this or they fork onto separate decode cache handles.
        Assert.Equal((int)MathF.Round(dw / aspect), dh);
    }

    [Fact]
    public void An_extreme_edge_clamps_at_the_device_pixel_ceiling()
    {
        // Design.ImageDecodeScale.Bucket alone has no ceiling clamp (only .For does) — ArtworkDecode adds one at
        // this call site instead of editing Design.cs, which another wave owns.
        var (dw, dh, _) = Controls.ArtworkDecode(5000f, 5000f, 0);
        Assert.Equal(Design.ImageDecodeScale.Ceiling, dw);
        Assert.Equal(Design.ImageDecodeScale.Ceiling, dh);
    }

    [Fact]
    public void An_explicit_decodePx_keeps_its_exact_literal_as_a_square()
    {
        // The hand-off path: a card and its detail cover asking for the SAME literal must resolve to one cache
        // handle, so an explicit decodePx is never bucketed, and it always cover-fits a SQUARE regardless of the
        // slot's own shape.
        var (dw, dh, aspect) = Controls.ArtworkDecode(28f, 44f, 300);
        Assert.Equal(300, dw);
        Assert.Equal(300, dh);
        Assert.Equal(1f, aspect);
    }

    [Theory]
    [InlineData(0f, 44f)]     // a first layout pass, before the parent has measured
    [InlineData(0f, 0f)]      // a fully unmeasured slot
    [InlineData(-4f, 44f)]    // a degenerate negative extent
    public void A_zero_width_slot_never_derives_a_nonsense_decode_height(float w, float h)
    {
        // `dh = dw / aspect` with a zero-width slot divides by zero: +Infinity, whose float->int conversion is
        // UNDEFINED and lands on int.MinValue — a negative decode edge handed straight to the image cache as part of
        // its (source, W, H) key. The engine's own `Reconciler.ImageDecodeTarget` guards this by falling back to the
        // hint when the aspect is not positive, and this helper must agree with it or the shimmer and the real image
        // fork onto two decode handles in exactly the frame where a slot is still being measured.
        var (dw, dh, _) = Controls.ArtworkDecode(w, h, 0);
        Assert.True(dw > 0, "decode width must stay positive");
        Assert.True(dh > 0, "decode height must stay positive, not int.MinValue");
        Assert.Equal(dw, dh);   // the engine's degenerate arm: h falls back to the hint itself
    }
}

// ══ FIX 2 — the one accent ladder ════════════════════════════════════════════════════════════════════════════════
//
// Needs a live Entities scope (a Playlist row) and the process-wide Palette table, so it joins EntitiesCollection
// like PlaylistTests.cs and PaletteTests.cs.
[Collection(EntitiesCollection.Name)]
public class AccentLadderTests
{
    // A believable Spotify image id (Palette's own shape: a 16-char size/kind marker + a 24-char artwork-identity
    // tail — Entities/Palette.cs's ArtIdentityOf keys on the tail alone). Controls.ArtUrl prefixes it with the CDN
    // host since it starts with neither "http" nor "file:" and carries no "\\".
    const string GradedImageId = "ab67616d0000b273accecc0000000000000f00d";

    static Playlist MakePlaylist(string uriSuffix, uint accent, string? imageId)
    {
        var s = Staging.Rent();
        var id = EntityId.Parse($"spotify:playlist:{uriSuffix}".AsSpan());
        ref var row = ref s.Playlists.RowFor(new StagedId(id), Authority.Full,
            (uint)(PlaylistFields.Identity | PlaylistFields.Accent));
        row.Title = s.Text("ArtworkAndAccentTests/" + uriSuffix);
        row.Accent = accent;
        if (imageId is not null) row.Image = s.Text(imageId);
        TestScope.CommitAndPublish(s);
        return Entities.Playlist(id);
    }

    [Fact]
    public void Coverless_playlist_chrome_and_rich_text_accent_agree_and_are_lifted()
    {
        TestScope.Fresh();
        var p = MakePlaylist("coverless-agree", 0xFF6A1B9Au, imageId: null);

        // "Chrome" is what Playlist.Page.cs's FrameSpec feeds Detail.ComputeAccent: AccentFor(id.PaletteUrl ??
        // id.CoverUrl, id.CardAccent), i.e. AccentFor(Controls.ArtUrl(p.ImageId), p.Accent) for this row. "Rich
        // text" is Playlist.UI.AccentOf(p) — the description's RichTextFlex accent (Playlist.UI.cs). Both now read
        // the SAME url (null, coverless) and the SAME payload (p.Accent) through the one ladder.
        var chrome = Detail.AccentFor(Controls.ArtUrl(p.ImageId), p.Accent);
        var richText = Playlist.AccentOf(p);
        Assert.Equal(chrome, richText);

        // The regression this pins: before the fix, AccentOf hand-rolled
        // `ColorF.FromRgba((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb, 255)` with no Lift. It must now be the
        // LIFTED payload colour (Design.Palette.ChromeFromPayload), not the raw one.
        Assert.Equal(Design.Palette.ChromeFromPayload(0xFF6A1B9Au), richText);
        Assert.NotEqual(ColorF.FromRgba(0x6A, 0x1B, 0x9A, 255), richText);
    }

    [Fact]
    public void A_zero_payload_on_a_coverless_playlist_is_the_system_accent()
    {
        TestScope.Fresh();
        var p = MakePlaylist("coverless-zero", 0u, imageId: null);
        Assert.Equal(Tok.AccentDefault, Playlist.AccentOf(p));

        // Album.Page.AccentNow and Artist.Page.AccentFor carry no payload rung at all (their model has no Accent
        // field) — they always call Detail.AccentFor(url, 0), so this is the exact fallback they now share.
        Assert.Equal(Tok.AccentDefault, Detail.AccentFor(null, 0));
    }

    [Fact]
    public void A_graded_cover_still_wins_over_the_payload_hex()
    {
        TestScope.Fresh();
        // A strongly saturated grading in BOTH halves, so the fact does not depend on the ambient Tok.Theme.
        var scheme = new Scheme(0xFF191414, 0xFF1DB954, 0xFFFFFFFF, 0xFFB3B3B3, 0xFFFFFFFF);
        Palette.SetGraded(GradedImageId.AsSpan(), dark: scheme, light: scheme, hasLight: true, bestFitIsLight: false);

        var p = MakePlaylist("graded-wins", 0xFFC2185Bu, GradedImageId);

        var graded = Design.Palette.ChromeAccent(scheme);
        Assert.Equal(graded, Playlist.AccentOf(p));
        Assert.NotEqual(Design.Palette.ChromeFromPayload(0xFFC2185Bu), Playlist.AccentOf(p));
    }
}
