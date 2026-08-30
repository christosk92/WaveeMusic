using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Render;

namespace Wavee;

/// <summary>The Liked Songs heart as VECTOR GEOMETRY — the one contour the dynamic cover draws at cover scale (Tone's
/// frosted heart, and the Lens window's clip silhouette and rim).
///
/// <para>It is the APP'S OWN heart, lifted verbatim from <c>ThemedIconData</c>'s <c>HeartFill</c> (the same 24-unit
/// view-box every <c>Icons.HeartFill</c> glyph in the app already paints), not the prototype's Material heart: a cover
/// is the biggest place this shape appears, and it would be the one heart in Wavee that did not match the save button
/// three inches below it.</para>
///
/// <para>Parsed EXACTLY ONCE, through <see cref="PathGeometryTable"/> (the <c>HeroMotion.Geo</c> idiom) rather than a
/// bare <c>PathDataParser.Parse</c>: the table mints one content epoch per distinct registration and hands back the
/// same <see cref="PathData"/> instance forever, which is what keeps the tessellation-realization cache warm. A
/// per-render parse would mint a fresh epoch and re-tessellate the heart on every frame the cover re-renders.</para>
///
/// <para>Chips and badges keep using the <c>Icons.HeartFill</c> GLYPH — at 16 DIP the font atlas is cheaper and
/// crisper than a tessellated path. This file is for the big shape only.</para></summary>
static class LikedHeart
{
    /// <summary>The authored view-box edge. Callers pass it to <c>PathEl.ViewBoxW/H</c>, which bakes a uniform fit
    /// scale into the draw's world transform — so stroke widths below are in VIEW-BOX units, not DIP.</summary>
    public const float ViewBox = 24f;

    // ThemedIconData.g.cs's "HeartFill" base layer, character for character. Do not "tidy" it: the string IS the
    // interning key, so a whitespace edit here silently forks a second geometry id off the shared one.
    const string Contour =
        "M12 20 C12 20 4 14.5 4 8.75 C4 6 6.1 4 8.5 4 C10.1 4 11.4 4.95 12 6.3 C12.6 4.95 13.9 4 15.5 4 "
      + "C17.9 4 20 6 20 8.75 C20 14.5 12 20 12 20 Z";

    /// <summary>The interned heart. First touch happens on the UI thread inside a render (the single-writer side of
    /// <see cref="PathGeometryTable"/>'s seam discipline), exactly like the setup-wizard heroes' static geometries.</summary>
    public static readonly PathData Data = Intern();

    static PathData Intern()
    {
        int id = PathGeometryTable.Shared.Register(Contour, ViewBox, ViewBox, FillRule.NonZero);
        PathGeometryTable.Shared.TryGet(id, out var data);
        return data;
    }

    /// <summary>The solid heart at <paramref name="edge"/> DIP square.</summary>
    public static PathEl Fill(float edge, ColorF fill) => new()
    {
        Width = edge, Height = edge, ViewBoxW = ViewBox, ViewBoxH = ViewBox,
        Geometry = Data, Fill = fill, Rule = FillRule.NonZero,
    };

    /// <summary>The heart's outline only. <paramref name="width"/> is in VIEW-BOX units (the recorder folds the
    /// view-box fit into the world transform before realizing the stroke), so the prototype's <c>stroke-width:.3</c>
    /// transfers as the literal 0.3 and stays proportional at every cover size.</summary>
    public static PathEl Rim(float edge, ColorF stroke, float width = 0.3f) => new()
    {
        Width = edge, Height = edge, ViewBoxW = ViewBox, ViewBoxH = ViewBox,
        Geometry = Data, StrokeColor = stroke, Stroke = new StrokeStyle(width, LineCap.Round, LineJoin.Round),
    };
}
