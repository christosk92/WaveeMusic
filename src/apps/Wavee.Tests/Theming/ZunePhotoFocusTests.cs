// ── Wavee.Tests/Theming/ZunePhotoFocusTests.cs — the artist photo's crop keeps the face in view (P5 Zune fidelity round 3) ──────────────────────
//
// The bleed is the one photo behind Zune's title bar, band and hero: a wide, short box (the window's width, 420 tall) over a portrait or square header
// image, cover-fitted. Cropped around the middle it left only the nose and mouth under the chrome. The focal point is a rule (`ZuneArtistRules.PhotoFocusY`,
// 28 % from the top, the engine's `ImageEl.FocusY`). A marker in the photo's top third (where a face sits) must land inside the visible slice, and the
// middle crop must not (the bug), at the window widths the owner saw (a 1984 wide window and a phone-narrow one).

using FluentGpu.Foundation;
using FluentGpu.Render;
using Wavee.Themes.Zune;
using Xunit;

namespace Wavee.Tests.Theming;

public sealed class ZunePhotoFocusTests
{
    const float BleedH = 420f;

    /// <summary>The photo's visible slice as fractions of its height (0..1) in a box <paramref name="w"/> x <see cref="BleedH"/>.</summary>
    static (float Top, float Bottom) Visible(float w, int srcW, int srcH, float focusY)
    {
        var (_, uv) = SceneRecorder.ImageContentFit(ImageFit.Cover, new RectF(0f, 0f, w, BleedH), srcW, srcH, 0.5f, focusY);
        return (uv.Y, uv.Y + uv.H);
    }

    [Fact]
    public void The_focal_point_is_in_the_upper_third()
        => Assert.InRange(ZuneArtistRules.PhotoFocusY, 0.2f, 0.35f);

    [Theory]
    [InlineData(1984f, 640, 640)]      // a square avatar-sized header
    [InlineData(1984f, 1000, 1500)]    // a portrait
    [InlineData(1440f, 1280, 720)]     // a landscape header
    public void A_marker_in_the_top_third_lands_in_the_visible_slice(float w, int srcW, int srcH)
    {
        const float marker = 0.30f;   // the eyes: about a third down a head-and-shoulders header (the face sits in the upper third)
        var rule = Visible(w, srcW, srcH, ZuneArtistRules.PhotoFocusY);
        Assert.InRange(marker, rule.Top, rule.Bottom);
        var middle = Visible(w, srcW, srcH, 0.5f);
        if (srcH >= srcW) Assert.False(marker >= middle.Top, "the middle crop loses the top third (the bug this rule fixes)");
    }
}
