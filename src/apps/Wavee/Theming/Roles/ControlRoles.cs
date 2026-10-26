// -- Theming/Roles/ControlRoles.cs -- the control roles a style publishes (themes plan EP15 / E14, lane WR) ----------------------
//
// The engine's control style table (`ThemeDefinition.Controls`, keys in `FluentGpu.Controls.ControlStyles`) restyles a CONTROL KIND:
// every Accent button, every ToggleButton. A control ROLE says which control a Wavee surface wants for a purpose: the one primary
// action on a page, its secondary, the transport's round play, the follow toggle, the plate a list row paints. The role is the app's
// word for it; the control table is how a style draws the control; the role table (this file) is the bridge, and it is DATA:
//
//   role  -->  (appearance, size, round)  -->  Button.Style  through the active definition's control table
//
// so a role never copies a colour or a radius out of the control table: the style that restyles Accent buttons restyles the
// PrimaryAction with it, and the role table only says WHICH appearance and size the role is. That is why this file holds numbers only
// where a role is not a control kind (the list backplate's inset and pill), and why the lookups take the control table as an argument:
// the live read is `R.Controls` (the active definition, read at render, never captured), the pure read is a table in a test.
//
// `CommandButtonStyle.LabelMode` is the same kind of fact for a command (a play-bar or toolbar button): Words, IconLabel or Icon,
// chosen per width tier. It lives here, app-side, because the engine's Button is neither: a view asks `Parts(mode, ...)` which of the
// glyph and the label to build.
//
// Classic's and Zune's values are what those styles draw today (Themes\Classic\Style\ClassicControlRoles.cs, Themes\Zune\Style\
// ZuneControlRoles.cs), so adding the table moves no pixel; Panorama (P7) reads PrimaryAction, SecondaryAction and ListBackplate from the
// WinUI style under the Zune structure (plan 6.4).

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Theming.Roles;

/// <summary>The controls a Wavee surface asks a style for by purpose (themes plan EP15, section 6.4's role map).</summary>
public enum ControlRole : byte
{
    /// <summary>The one primary call to action of a surface (Play, Resume, Save): an Accent button.</summary>
    PrimaryAction = 0,

    /// <summary>A secondary action beside the primary (Shuffle, Not now, Cancel): a Standard button.</summary>
    SecondaryAction = 1,

    /// <summary>The transport's play and pause: the accent disc the player bar and the stage draw.</summary>
    Transport = 2,

    /// <summary>The follow toggle (artist, profile, show).</summary>
    Follow = 3,

    /// <summary>The plate a list row paints for hover, press and selection.</summary>
    ListBackplate = 4,
}

/// <summary>How a command button writes itself (themes plan E14): as words, as an icon with its label, or as the icon alone.</summary>
public enum LabelMode : byte
{
    /// <summary>The label only, even when the caller has a glyph (Zune's lowercase words).</summary>
    Words = 0,

    /// <summary>The glyph and the label (the glyph where the caller has one): the full command.</summary>
    IconLabel = 1,

    /// <summary>The glyph only (the label drops, and rides the tooltip); a command without a glyph keeps its label.</summary>
    Icon = 2,
}

/// <summary>Which parts of a command button to build for a <see cref="LabelMode"/>.</summary>
/// <param name="Glyph">Build the glyph.</param>
/// <param name="Label">Build the label.</param>
public readonly record struct CommandParts(bool Glyph, bool Label);

/// <summary>The <see cref="LabelMode"/> of a style's command buttons at each width tier. Equality is by value.</summary>
/// <param name="Compact">The mode below the Small tier.</param>
/// <param name="Small">The mode at the Small tier.</param>
/// <param name="Standard">The mode at the Standard tier.</param>
/// <param name="Huge">The mode at the Huge tier.</param>
/// <param name="Huge4K">The mode at the Huge 4K tier.</param>
public sealed record CommandButtonStyle(LabelMode Compact, LabelMode Small, LabelMode Standard, LabelMode Huge, LabelMode Huge4K)
{
    /// <summary>The same mode at every tier.</summary>
    public static CommandButtonStyle Uniform(LabelMode mode) => new(mode, mode, mode, mode, mode);

    /// <summary><paramref name="below"/> in every tier under <paramref name="from"/> and <paramref name="mode"/> from it up (the WinUI
    /// shape: icon and label from the Standard tier, the icon alone below).</summary>
    public static CommandButtonStyle FromTier(WidthTier from, LabelMode mode, LabelMode below) => new(
        WidthTier.Compact >= from ? mode : below, WidthTier.Small >= from ? mode : below, WidthTier.Standard >= from ? mode : below,
        WidthTier.Huge >= from ? mode : below, WidthTier.Huge4K >= from ? mode : below);

    /// <summary>The mode at <paramref name="tier"/>.</summary>
    public LabelMode For(WidthTier tier) => tier switch
    {
        WidthTier.Compact => Compact,
        WidthTier.Small => Small,
        WidthTier.Standard => Standard,
        WidthTier.Huge => Huge,
        _ => Huge4K,
    };

    /// <summary>The parts of a command to build for <paramref name="mode"/>. A command with no glyph always keeps its label (an icon-only
    /// command with nothing to draw would be an empty button); a command with no label never builds one.</summary>
    public static CommandParts Parts(LabelMode mode, bool hasGlyph, bool hasLabel = true)
    {
        if (!hasGlyph) return new CommandParts(false, hasLabel);
        if (!hasLabel) return new CommandParts(true, false);
        return mode switch
        {
            LabelMode.Words => new CommandParts(false, true),
            LabelMode.Icon => new CommandParts(true, false),
            _ => new CommandParts(true, true),
        };
    }

    /// <summary><see cref="Parts(LabelMode, bool, bool)"/> at <paramref name="tier"/>.</summary>
    public CommandParts PartsAt(WidthTier tier, bool hasGlyph, bool hasLabel = true) => Parts(For(tier), hasGlyph, hasLabel);
}

/// <summary>Which button a role is: an appearance at a size, round or not. The styling (colour, radius, padding) is the control table's.</summary>
/// <param name="Appearance">The button appearance the role draws.</param>
/// <param name="Size">The control size the role draws at when the call site names none.</param>
/// <param name="Round">The role is a disc or a capsule: the corner is the full round whatever the control table's radius for the appearance.</param>
public readonly record struct ButtonRoleSpec(ButtonAppearance Appearance, ControlSize Size, bool Round = false);

/// <summary>The follow toggle's size. Its ON arm is the accent tint of <c>ButtonRules.FollowStyle</c>, built from the page accent.</summary>
/// <param name="Size">The control size the toggle draws at when the call site names none.</param>
public readonly record struct FollowRoleSpec(ControlSize Size);

/// <summary>The "you are here" mark on a selected row (a rounded bar before the row's content).</summary>
/// <param name="Width">The bar's width.</param>
/// <param name="Height">The bar's height.</param>
/// <param name="Radius">The bar's corner radius.</param>
/// <param name="Lead">The gap between the row's leading edge and the bar.</param>
public readonly record struct SelectionPill(float Width, float Height, float Radius, float Lead);

/// <summary>The geometry of the plate a list row paints. The colours are the style's layer roles (<see cref="LayerRoles"/>), so a row's
/// hover and selection fills stay where every other surface reads them from.</summary>
/// <param name="Radius">The plate's corner radius.</param>
/// <param name="Inset">The plate's inset from the list's edges (both sides).</param>
/// <param name="Pill">The selection pill, or null when the style marks the selected row by its fill alone.</param>
public sealed record ListBackplateSpec(float Radius, float Inset, SelectionPill? Pill);

/// <summary>A list row's plate for the active theme kind: the fills and the geometry.</summary>
/// <param name="Hover">The hover fill.</param>
/// <param name="Pressed">The pressed fill.</param>
/// <param name="SelectedRest">The selected row's fill.</param>
/// <param name="SelectedHover">The selected row under the pointer.</param>
/// <param name="SelectedPressed">The selected row pressed.</param>
/// <param name="Radius">The plate's corner radius.</param>
/// <param name="Inset">The plate's inset from the list's edges.</param>
/// <param name="Pill">The selection pill, or null.</param>
public readonly record struct ListBackplate(
    ColorF Hover, ColorF Pressed, ColorF SelectedRest, ColorF SelectedHover, ColorF SelectedPressed,
    float Radius, float Inset, SelectionPill? Pill);

/// <summary>The control roles of one style (a member of <see cref="WaveeStyleRoles"/>). Data only, so equality is by value and
/// <c>WaveeStyleRoles.DiffersOnlyInColours</c> sees a changed role as the geometry change it is.</summary>
/// <param name="PrimaryAction">The primary action's button.</param>
/// <param name="SecondaryAction">The secondary action's button.</param>
/// <param name="Transport">The transport's play disc.</param>
/// <param name="Follow">The follow toggle.</param>
/// <param name="ListBackplate">The list row plate's geometry.</param>
/// <param name="Command">How command buttons write themselves, per width tier.</param>
public sealed record ControlRoleTable(
    ButtonRoleSpec PrimaryAction, ButtonRoleSpec SecondaryAction, ButtonRoleSpec Transport,
    FollowRoleSpec Follow, ListBackplateSpec ListBackplate, CommandButtonStyle Command)
{
    /// <summary>A fully round corner (clamped to half the box when it is recorded): the engine's own 999.</summary>
    public const float RoundCorner = 999f;

    /// <summary>The role table of a style that publishes none: the stock Fluent grammar (an Accent, a Standard, an accent disc, the
    /// medium follow toggle, the WinUI row plate on a 4-DIP inset with the 3 x 16 pill, icon and label commands).</summary>
    public static ControlRoleTable Fluent { get; } = new(
        PrimaryAction: new ButtonRoleSpec(ButtonAppearance.Accent, ControlSize.Medium),
        SecondaryAction: new ButtonRoleSpec(ButtonAppearance.Standard, ControlSize.Medium),
        Transport: new ButtonRoleSpec(ButtonAppearance.Accent, ControlSize.Large, Round: true),
        Follow: new FollowRoleSpec(ControlSize.Medium),
        ListBackplate: new ListBackplateSpec(Radius: 4f, Inset: 4f, Pill: new SelectionPill(3f, 16f, 1.5f, 2f)),
        Command: CommandButtonStyle.Uniform(LabelMode.IconLabel));

    /// <summary>The spec of a button role. Throws for the roles that are not a button (<see cref="ControlRole.Follow"/>,
    /// <see cref="ControlRole.ListBackplate"/>).</summary>
    public ButtonRoleSpec ButtonSpec(ControlRole role) => role switch
    {
        ControlRole.PrimaryAction => PrimaryAction,
        ControlRole.SecondaryAction => SecondaryAction,
        ControlRole.Transport => Transport,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "not a button role"),
    };

    /// <summary>The button style of a role through <paramref name="controls"/> (the definition's control table): the table's
    /// <see cref="ControlStyles.ButtonStyle"/> provider for the role's appearance and size when it has one and returns a style, else the
    /// built-in Fluent button; a round role then takes the full round corner. <paramref name="size"/> overrides the role's size.</summary>
    public Button.Style ButtonStyle(ControlRole role, ControlStyleTable controls, ControlSize? size = null)
    {
        var spec = ButtonSpec(role);
        var s = size ?? spec.Size;
        Button.Style? styled = controls.TryGet(ControlStyles.ButtonStyle, out var provider) ? provider(spec.Appearance, s) : null;
        var style = styled ?? Button.FluentStyle(spec.Appearance, s);
        return spec.Round ? style with { CornerRadius = RoundCorner } : style;
    }

    /// <summary>The follow toggle's style for the page accent <paramref name="accent"/> (the app's follow tint over the active
    /// definition's toggle button).</summary>
    public ToggleButton.Style FollowStyle(ColorF accent, bool light) => ButtonRules.FollowStyle(accent, light);

    /// <summary>The row plate for <paramref name="kind"/> from the style's layer roles.</summary>
    public ListBackplate Backplate(LayerRoles layer, ThemeKind kind) => new(
        Hover: layer.RowHover.Resolve(kind), Pressed: layer.RowPressed.Resolve(kind),
        SelectedRest: layer.SelectedRest.Resolve(kind), SelectedHover: layer.SelectedHover.Resolve(kind),
        SelectedPressed: layer.SelectedPressed.Resolve(kind),
        Radius: ListBackplate.Radius, Inset: ListBackplate.Inset, Pill: ListBackplate.Pill);
}

/// <summary>The control roles of the active style as the call syntax a view builds with (<c>R.Controls.ButtonStyle(ControlRole.PrimaryAction)</c>).
/// Every member reads the active definition now: nothing is cached, so keep it out of a <c>const</c> or a <c>static readonly</c> field.</summary>
public readonly struct ControlReads
{
    private readonly WaveeStyleRoles _roles;

    internal ControlReads(WaveeStyleRoles roles) => _roles = roles;

    /// <summary>The role table behind these reads, for a pure evaluation against another definition's control table.</summary>
    public ControlRoleTable Table => _roles.Controls;

    /// <summary>The button style of <paramref name="role"/> (PrimaryAction, SecondaryAction or Transport) under the active definition.</summary>
    public Button.Style ButtonStyle(ControlRole role, ControlSize? size = null)
        => _roles.Controls.ButtonStyle(role, Tok.Definition.Controls, size);

    /// <summary>The size a button <paramref name="role"/> draws at.</summary>
    public ControlSize SizeOf(ControlRole role) => _roles.Controls.ButtonSpec(role).Size;

    /// <summary>The follow toggle's style for the page accent <paramref name="accent"/> in the active theme kind.</summary>
    public ToggleButton.Style FollowStyle(ColorF accent) => _roles.Controls.FollowStyle(accent, Tok.Theme == ThemeKind.Light);

    /// <summary>The list row plate for the active theme kind.</summary>
    public ListBackplate ListBackplate => _roles.Controls.Backplate(_roles.Layer, Tok.Theme);

    /// <summary>The command buttons' label modes per width tier.</summary>
    public CommandButtonStyle Command => _roles.Controls.Command;

    /// <summary>The label mode of a command at <paramref name="tier"/>.</summary>
    public LabelMode LabelModeAt(WidthTier tier) => _roles.Controls.Command.For(tier);
}
