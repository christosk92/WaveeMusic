// ── Theming/Roles/OnMediaLadder.cs — everything that paints ON TOP of artwork or video (themes plan 5.4, T2b) ──────────
//
// What was `Design.OnMedia`: the scrim plates behind a floating "..."/FAB/chip, the hairline that rings them, the ink on them, the
// dim under a blurred stand-in backdrop, the hover spotlight and the light-theme on-media button ramp. ONE ladder: the card layer
// alone once hand-rolled 36 colour literals for this job, including THREE near-duplicate scrim ladders that disagreed by 10-50
// alpha steps for visually identical affordances. They collapsed into ONE ladder built from the middle of the three, except
// that the REST rung is the engine's own `Tok.MediaScrim`.
//
// THEME INVARIANCE IS THE POINT, NOT AN OVERSIGHT. Ink over artwork is theme-invariant, so Classic's plates stay dark and its ink
// stays white in light mode too. The ladder is nevertheless a ROLE TABLE: a style that wants another answer over artwork gives its
// own. The one surface a THEME changes is the immersive stage, and that is `StageArmTable`'s (it is the only surface with a
// polarity).

using FluentGpu.Foundation;

namespace Wavee.Theming.Roles;

/// <summary>The on-media ladder of a style. Every style defines all of it (themes plan S2). Equality is by value.</summary>
/// <param name="ScrimRest">The resting plate of a small dark surface floated over art (Classic: the engine's <c>Tok.MediaScrim</c>, black at 0.55).</param>
/// <param name="ScrimHover">Hover (Classic: black at 190/255; BLACK, not the old (20,20,20), which a 0.75 alpha made invisible over artwork).</param>
/// <param name="ScrimPressed">Pressed (Classic: black at 220/255, the middle of 245, 220 and 218).</param>
/// <param name="CoverScrim">The full-cover hover veil: a whole square of artwork dimmed so a centred FAB reads on it. A different role from <paramref name="ScrimRest"/> (a small plate), so it keeps its own, lighter value.</param>
/// <param name="Stroke">The hairline that rings every on-media plate. Doubles as the faint on-media RULE (the countdown ring's track).</param>
/// <param name="Ink">Primary on-media ink: titles, chip labels, glyphs, the swept countdown arc.</param>
/// <param name="InkSecondary">Secondary on-media ink: eyebrows and subtitles over art.</param>
/// <param name="InkTertiary">Tertiary on-media ink: captions and meta over art.</param>
/// <param name="GlassRest">The on-media INTERACTION ramp for a control carrying NO resting plate: nothing at rest.</param>
/// <param name="GlassHover">A breath of the on-media ink on hover: the lightest fill that still reads as a surface over artwork.</param>
/// <param name="GlassPressed">One rung more on press (the ratio the subtle ladder keeps between its two rungs).</param>
/// <param name="GlassPlate">The resting ground of an ink-plated on-media control (the immersive stage's way out): a REST state, so above <paramref name="GlassHover"/>.</param>
/// <param name="GlassPlateHover">The ink plate's hover.</param>
/// <param name="GlassPlatePressed">The ink plate's press.</param>
/// <param name="BackdropDim">The dim laid over a BAKED-BLUR derivative so it reads as a backdrop rather than as a stretched square.</param>
/// <param name="SpotlightInner">The hover spotlight's inner stop: a soft white bloom under the pointer on an editorial cover.</param>
/// <param name="SpotlightMid">The hover spotlight's mid stop (it falls to transparent at the edge).</param>
/// <param name="LightButton">Rest fill of a light-on-media button: the on-media ink itself.</param>
/// <param name="LightButtonHover">Hover fill of the light-on-media button.</param>
/// <param name="LightButtonPressed">Pressed fill of the light-on-media button.</param>
/// <param name="LightButtonInk">The glyph ON a light on-media button: the engine's opaque media stage.</param>
/// <param name="LightHoverDrop">The hover step of the button ramp as a mix fraction (the stage's play disc mirrors it toward the other end).</param>
/// <param name="LightPressedDrop">The press step of the button ramp as a mix fraction.</param>
public sealed record OnMediaLadder(
    ColorRole ScrimRest, ColorRole ScrimHover, ColorRole ScrimPressed, ColorRole CoverScrim, ColorRole Stroke,
    ColorRole Ink, ColorRole InkSecondary, ColorRole InkTertiary,
    ColorRole GlassRest, ColorRole GlassHover, ColorRole GlassPressed,
    ColorRole GlassPlate, ColorRole GlassPlateHover, ColorRole GlassPlatePressed,
    ColorRole BackdropDim, ColorRole SpotlightInner, ColorRole SpotlightMid,
    ColorRole LightButton, ColorRole LightButtonHover, ColorRole LightButtonPressed, ColorRole LightButtonInk,
    float LightHoverDrop, float LightPressedDrop);

/// <summary>The on-media roles as the call syntax a view paints with (<c>Fill = R.OnMedia.ScrimRest</c>). Every colour member
/// evaluates its role for the ACTIVE theme kind at the call: nothing is cached.</summary>
public readonly struct OnMediaReads
{
    private readonly OnMediaLadder _roles;

    internal OnMediaReads(OnMediaLadder roles) => _roles = roles;

    /// <summary>The role table behind these reads.</summary>
    public OnMediaLadder Roles => _roles;

    /// <inheritdoc cref="OnMediaLadder.ScrimRest"/>
    public ColorF ScrimRest => _roles.ScrimRest.Now;
    /// <inheritdoc cref="OnMediaLadder.ScrimHover"/>
    public ColorF ScrimHover => _roles.ScrimHover.Now;
    /// <inheritdoc cref="OnMediaLadder.ScrimPressed"/>
    public ColorF ScrimPressed => _roles.ScrimPressed.Now;
    /// <inheritdoc cref="OnMediaLadder.CoverScrim"/>
    public ColorF CoverScrim => _roles.CoverScrim.Now;
    /// <inheritdoc cref="OnMediaLadder.Stroke"/>
    public ColorF Stroke => _roles.Stroke.Now;
    /// <inheritdoc cref="OnMediaLadder.Ink"/>
    public ColorF Ink => _roles.Ink.Now;
    /// <inheritdoc cref="OnMediaLadder.InkSecondary"/>
    public ColorF InkSecondary => _roles.InkSecondary.Now;
    /// <inheritdoc cref="OnMediaLadder.InkTertiary"/>
    public ColorF InkTertiary => _roles.InkTertiary.Now;
    /// <inheritdoc cref="OnMediaLadder.GlassRest"/>
    public ColorF GlassRest => _roles.GlassRest.Now;
    /// <inheritdoc cref="OnMediaLadder.GlassHover"/>
    public ColorF GlassHover => _roles.GlassHover.Now;
    /// <inheritdoc cref="OnMediaLadder.GlassPressed"/>
    public ColorF GlassPressed => _roles.GlassPressed.Now;
    /// <inheritdoc cref="OnMediaLadder.GlassPlate"/>
    public ColorF GlassPlate => _roles.GlassPlate.Now;
    /// <inheritdoc cref="OnMediaLadder.GlassPlateHover"/>
    public ColorF GlassPlateHover => _roles.GlassPlateHover.Now;
    /// <inheritdoc cref="OnMediaLadder.GlassPlatePressed"/>
    public ColorF GlassPlatePressed => _roles.GlassPlatePressed.Now;
    /// <inheritdoc cref="OnMediaLadder.BackdropDim"/>
    public ColorF BackdropDim => _roles.BackdropDim.Now;
    /// <inheritdoc cref="OnMediaLadder.SpotlightInner"/>
    public ColorF SpotlightInner => _roles.SpotlightInner.Now;
    /// <inheritdoc cref="OnMediaLadder.SpotlightMid"/>
    public ColorF SpotlightMid => _roles.SpotlightMid.Now;
    /// <inheritdoc cref="OnMediaLadder.LightButton"/>
    public ColorF LightButton => _roles.LightButton.Now;
    /// <inheritdoc cref="OnMediaLadder.LightButtonHover"/>
    public ColorF LightButtonHover => _roles.LightButtonHover.Now;
    /// <inheritdoc cref="OnMediaLadder.LightButtonPressed"/>
    public ColorF LightButtonPressed => _roles.LightButtonPressed.Now;
    /// <inheritdoc cref="OnMediaLadder.LightButtonInk"/>
    public ColorF LightButtonInk => _roles.LightButtonInk.Now;
    /// <inheritdoc cref="OnMediaLadder.LightHoverDrop"/>
    public float LightHoverDrop => _roles.LightHoverDrop;
    /// <inheritdoc cref="OnMediaLadder.LightPressedDrop"/>
    public float LightPressedDrop => _roles.LightPressedDrop;
}
