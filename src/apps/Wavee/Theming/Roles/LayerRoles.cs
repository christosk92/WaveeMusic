// ── Theming/Roles/LayerRoles.cs — the surfaces and row ladders of a style (themes plan 5.4, T2b) ─────────────────────────
//
// What was `Design.Colors`: the chrome ground and the content step, the translucent MUX rungs, the list-row ladder (zebra,
// hover, press), the selection ladder and the art placeholder. A layer role is a COLOUR a view paints a surface or a state with;
// `R.Layer.RowHover` evaluates it for the active theme kind where the element is built. Classic's recipes are
// `Themes\Classic\Style\ClassicRoles.cs`; the maths they share is `Platform\ColorScience.cs`.
//
//   THE ROW LADDER'S ORDERING LAW (it travels with the roles, not with a theme). A stripe MUST be quieter than the hover that
//   lands on it, or a striped row has no hover; the selection ladder (rest, hover, pressed) only ever goes UP; the press
//   direction inverts by polarity and BOTH arms are correct (light pressed is above hover, dark pressed is below it).

using FluentGpu.Foundation;

namespace Wavee.Theming.Roles;

/// <summary>The layer (surface and state) roles of a style. Every style defines all of them (themes plan S2). Equality is by value.</summary>
/// <param name="Toolbar">The translucent MUX rung under the title row. The authenticated shell no longer PAINTS the chrome rungs (every
/// band is a paint-site omission over the live window material); they stay published because the palette contrast gates measure
/// the tint through them.</param>
/// <param name="Sidebar">The translucent MUX rung under the nav pane.</param>
/// <param name="PlayerBar">The translucent MUX rung under the player dock.</param>
/// <param name="FileArea">The CONTENT layer: the stock layer fill, translucent, paired with a 1-px card stroke on its left and top edges.</param>
/// <param name="Content">The content rung.</param>
/// <param name="ContentAlt">The alternate content rung.</param>
/// <param name="ShellGround">The chrome GROUND: the full-bleed opaque rect the authenticated shell sits on (also the flatten base of every opaque floating surface).</param>
/// <param name="ContentSurface">The OPAQUE content rung, one step above the ground: the stand-in every FLOATING content surface takes.</param>
/// <param name="ContentLayer">The content step as a translucent white LAYER; over the bare ground it composites to <paramref name="ContentSurface"/>.</param>
/// <param name="FloatingChrome">The ground as a value, for a floating pane that stands in for a docked chrome band.</param>
/// <param name="FloatingPane">The content surface as a value, for a floating surface that stands in for a docked content band.</param>
/// <param name="RowZebra">THE zebra stripe: the resting plate on an odd row of a LONG tracklist, and nothing else.</param>
/// <param name="RowHover">A row's hover fill.</param>
/// <param name="RowPressed">A row's pressed fill.</param>
/// <param name="RowHoverZebra">Hover ON a striped row: the hover SOURCE-OVER the stripe, collapsed to ONE translucent fill.</param>
/// <param name="RowPressedZebra">Pressed ON a striped row, collapsed the same way.</param>
/// <param name="SelectedRest">A nav row's "you are here" plate (accent role 2).</param>
/// <param name="SelectedHover">The hover veil composed OVER the selected plate, so hovered-selected is strictly stronger.</param>
/// <param name="SelectedPressed">The quieter press veil over the same plate: above rest, below hover.</param>
/// <param name="ChromeHover">A chrome control's hover fill.</param>
/// <param name="ChromePressed">A chrome control's pressed fill.</param>
/// <param name="Badge">A count badge's fill.</param>
/// <param name="EventPlate">The base fill of a concert tile and card: a fixed near-black where the style is dark, the card's secondary fill where it is light.</param>
/// <param name="EventHeroPlate">The base fill of the concert split hero (one step off <paramref name="EventPlate"/>).</param>
/// <param name="ArtPlaceholder">The OPAQUE neutral tile of an art slot before its bitmap lands. Theme card brushes are translucent, and a
/// small thumb sits over an unpainted chrome band, so the placeholder is an explicit opaque neutral.</param>
public sealed record LayerRoles(
    ColorRole Toolbar, ColorRole Sidebar, ColorRole PlayerBar, ColorRole FileArea, ColorRole Content, ColorRole ContentAlt,
    ColorRole ShellGround, ColorRole ContentSurface, ColorRole ContentLayer, ColorRole FloatingChrome, ColorRole FloatingPane,
    ColorRole RowZebra, ColorRole RowHover, ColorRole RowPressed, ColorRole RowHoverZebra, ColorRole RowPressedZebra,
    ColorRole SelectedRest, ColorRole SelectedHover, ColorRole SelectedPressed,
    ColorRole ChromeHover, ColorRole ChromePressed, ColorRole Badge, ColorRole EventPlate, ColorRole EventHeroPlate,
    ColorRole ArtPlaceholder);

/// <summary>The layer roles as the call syntax a view paints with (<c>Fill = R.Layer.RowHover</c>). Every member evaluates its role for
/// the ACTIVE theme kind at the call: nothing is cached, so keep it out of a <c>const</c> or a <c>static readonly</c> field.</summary>
public readonly struct LayerReads
{
    private readonly LayerRoles _roles;

    internal LayerReads(LayerRoles roles) => _roles = roles;

    /// <summary>The role table behind these reads, for a pure per-kind evaluation (<c>Roles.RowZebra.Resolve(kind)</c>).</summary>
    public LayerRoles Roles => _roles;

    /// <inheritdoc cref="LayerRoles.Toolbar"/>
    public ColorF Toolbar => _roles.Toolbar.Now;
    /// <inheritdoc cref="LayerRoles.Sidebar"/>
    public ColorF Sidebar => _roles.Sidebar.Now;
    /// <inheritdoc cref="LayerRoles.PlayerBar"/>
    public ColorF PlayerBar => _roles.PlayerBar.Now;
    /// <inheritdoc cref="LayerRoles.FileArea"/>
    public ColorF FileArea => _roles.FileArea.Now;
    /// <inheritdoc cref="LayerRoles.Content"/>
    public ColorF Content => _roles.Content.Now;
    /// <inheritdoc cref="LayerRoles.ContentAlt"/>
    public ColorF ContentAlt => _roles.ContentAlt.Now;
    /// <inheritdoc cref="LayerRoles.ShellGround"/>
    public ColorF ShellGround => _roles.ShellGround.Now;
    /// <inheritdoc cref="LayerRoles.ContentSurface"/>
    public ColorF ContentSurface => _roles.ContentSurface.Now;
    /// <inheritdoc cref="LayerRoles.ContentLayer"/>
    public ColorF ContentLayer => _roles.ContentLayer.Now;
    /// <inheritdoc cref="LayerRoles.FloatingChrome"/>
    public ColorF FloatingChrome => _roles.FloatingChrome.Now;
    /// <inheritdoc cref="LayerRoles.FloatingPane"/>
    public ColorF FloatingPane => _roles.FloatingPane.Now;
    /// <inheritdoc cref="LayerRoles.RowZebra"/>
    public ColorF RowZebra => _roles.RowZebra.Now;
    /// <inheritdoc cref="LayerRoles.RowHover"/>
    public ColorF RowHover => _roles.RowHover.Now;
    /// <inheritdoc cref="LayerRoles.RowPressed"/>
    public ColorF RowPressed => _roles.RowPressed.Now;
    /// <inheritdoc cref="LayerRoles.RowHoverZebra"/>
    public ColorF RowHoverZebra => _roles.RowHoverZebra.Now;
    /// <inheritdoc cref="LayerRoles.RowPressedZebra"/>
    public ColorF RowPressedZebra => _roles.RowPressedZebra.Now;
    /// <inheritdoc cref="LayerRoles.SelectedRest"/>
    public ColorF SelectedRest => _roles.SelectedRest.Now;
    /// <inheritdoc cref="LayerRoles.SelectedHover"/>
    public ColorF SelectedHover => _roles.SelectedHover.Now;
    /// <inheritdoc cref="LayerRoles.SelectedPressed"/>
    public ColorF SelectedPressed => _roles.SelectedPressed.Now;
    /// <inheritdoc cref="LayerRoles.ChromeHover"/>
    public ColorF ChromeHover => _roles.ChromeHover.Now;
    /// <inheritdoc cref="LayerRoles.ChromePressed"/>
    public ColorF ChromePressed => _roles.ChromePressed.Now;
    /// <inheritdoc cref="LayerRoles.Badge"/>
    public ColorF Badge => _roles.Badge.Now;
    /// <inheritdoc cref="LayerRoles.EventPlate"/>
    public ColorF EventPlate => _roles.EventPlate.Now;
    /// <inheritdoc cref="LayerRoles.EventHeroPlate"/>
    public ColorF EventHeroPlate => _roles.EventHeroPlate.Now;
    /// <inheritdoc cref="LayerRoles.ArtPlaceholder"/>
    public ColorF ArtPlaceholder => _roles.ArtPlaceholder.Now;
}
