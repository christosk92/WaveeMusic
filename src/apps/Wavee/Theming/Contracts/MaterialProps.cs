// ── Theming/Contracts/MaterialProps.cs — the props of `Shell.Material` and its four cover leaves (themes plan 6.0, T4c) ─────
//
// THE MATERIAL IS CORE DATA AND A THEME PICTURE. The core owns the channel (`Platform/ShellMaterial.cs`): the published
// `ShellMaterialState` (owner token, flat tint, Home's three washes), the hand-over rule (`TintOwnership`: a claim, a refresh
// that lands only for the current owner, never a clear), and the pages' mounts. A theme draws it:
//
//   Shell.Material                the layer between live Mica and the chrome column (a flat page tint or Home's radial washes)
//   Shell.Material.PageTone       the detail page's flat art-derived plane (a page-root sibling)
//   Shell.Material.ArtistBlend    the artist page's blend wash
//   Shell.Material.ArtistVeil     the artist hero's photography veil (also the Home daylist card's)
//   Shell.Material.TintBinder     the 0 x 0 leaf that watches one cover and PUBLISHES the shell tint
//
// A leaf is a Component the structure registers (the cover-colour watch must sit in a LEAF, never in a page's Render), mounted
// by the helpers in `Entities/Palette.Host.cs` with the page's own `Key`. Their props change while they stay mounted (a grading
// arrives, a hero resizes), so they are re-pushed records: the template mounts the leaf with `Embed.Comp(props, ...)`.
//
// WHETHER A WASH IS DRAWN AT ALL IS THE THEME'S DECISION, not a prop: the core hosts publish data and mount the leaves; Classic's
// leaves and its material layer read Classic's Surfaces option themselves (M6).

using FluentGpu.Signals;

namespace Wavee.Theming.Contracts;

/// <summary>The props of <see cref="ViewKeys.ShellMaterialLayer"/>.</summary>
public sealed record MaterialProps
{
    /// <summary>The published material: read at NAVIGATION rate (the layer re-renders per hand-over, never per resize).</summary>
    public required IReadSignal<ShellMaterialState> State { get; init; }
}

/// <summary>The props of <see cref="ViewKeys.ShellCoverTone"/>: the detail page's flat art-derived ground.</summary>
/// <param name="Url">The cover the tone is graded from.</param>
/// <param name="FallbackUrl">A second cover to try when <paramref name="Url"/> has no grading.</param>
/// <param name="BackdropBand">The hero band's own height in DIP (hero-only mode), supplied by the caller's layout.</param>
/// <param name="PageHeight">The page's height in DIP (hero-only mode).</param>
/// <param name="HeroOnly">Paint only the hero band and fade out below it.</param>
/// <param name="PayloadAccent">The header row's raw accent (the ladder's payload rung); 0 when the caller has none.</param>
public sealed record CoverToneProps(string? Url, string? FallbackUrl, float BackdropBand, float PageHeight,
                                    bool HeroOnly, uint PayloadAccent = 0);

/// <summary>The props of <see cref="ViewKeys.ShellCoverBlend"/>: the artist page's blend wash.</summary>
/// <param name="Url">The cover (the artist's photograph) the wash is graded from.</param>
/// <param name="Height">The wash's height, from the caller's hero layout.</param>
/// <param name="Boundary">Where the gradient's middle stop sits (0..1), from the caller's hero layout.</param>
/// <param name="PayloadAccent">The header row's raw accent; 0 when the caller has none.</param>
public sealed record CoverBlendProps(string? Url, float Height, float Boundary, uint PayloadAccent = 0);

/// <summary>The props of <see cref="ViewKeys.ShellCoverVeil"/>: the photography veil.
/// A finite <paramref name="Width"/>/<paramref name="Height"/> is the sized arm (the artist hero's banner); <c>float.NaN</c> for
/// both is the stretch arm (a surface whose height is its content's, the daylist card).</summary>
/// <param name="Url">The cover the veil is graded from.</param>
/// <param name="Vertical">The veil fades along the vertical axis.</param>
/// <param name="Width">The veil's width, or NaN.</param>
/// <param name="Height">The veil's height, or NaN.</param>
/// <param name="PayloadAccent">The header row's raw accent; 0 when the caller has none.</param>
/// <param name="Hold">Where a horizontal veil's hold ends, as a fraction of its width (the caller's copy edge); NaN keeps the
/// theme's default. Appended in contract 1.6.</param>
public sealed record CoverVeilProps(string? Url, bool Vertical, float Width, float Height, uint PayloadAccent = 0,
                                    float Hold = float.NaN);

/// <summary>The props of <see cref="ViewKeys.ShellCoverTint"/>: the leaf that watches one cover and publishes the shell tint
/// (flat arm only; Home publishes its washes itself).</summary>
/// <param name="Url">The cover the tint is graded from.</param>
/// <param name="FallbackUrl">A second cover to try when <paramref name="Url"/> has no grading.</param>
/// <param name="Ready">The page's cover is usable (a page still loading holds the current colour).</param>
/// <param name="Apply">This layout applies a tint at all (false is a DEFINITE "no colour"; so is Classic's Surfaces option set to Off, which the leaf reads itself).</param>
/// <param name="Owner">The publishing page's identity token (reference identity only).</param>
/// <param name="Port">The write side of the shell material the page read from its context.</param>
/// <param name="PayloadAccent">The header row's raw accent; 0 when the caller has none.</param>
public sealed record CoverTintProps(string? Url, string? FallbackUrl, bool Ready, bool Apply, object Owner,
                                    MaterialPort Port, uint PayloadAccent = 0);
