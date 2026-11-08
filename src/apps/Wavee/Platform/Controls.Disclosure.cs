// ── Platform/Controls.Disclosure.cs — the disclosure button: a small chevron that opens and closes something inline ────────
//
// The discography card's "Show tracks" control (the inline album drawer). The card itself opens the album; this button, after
// the card's meta line, is the expand control and its own tab stop. It reads as a button: a real hit target (the box plus its
// slop reaches the lint's target floor), the theme's hover and pressed plates, a focus ring, and a name ("Show tracks" /
// "Hide tracks") that rides the tooltip (an engine box has no accessible-name property: `Named`). The chevron turns on the
// reveal spring the drawer opens on (MotionTokenId.Reveal), from where it stands, so the glyph and the drawer's edge land
// together; the first mount takes its resting angle with no motion. A theme draws it through a `DisclosureSkin` (its sizes
// from its own Rules, its colours as live token reads).

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>How a theme draws a <see cref="Controls.Disclosure"/>: the plate's edge, the glyph's size, the hit slop each side
/// (the plate plus twice the slop is the target), the plate's corner and the inks as live token reads (static lambdas, read in
/// the button's render, so a theme switch repaints them). <see cref="Bleed"/> pulls the plate out of its line's layout on the
/// vertical axis (a negative margin each side), so a plate taller than the meta line it ends never grows that line.</summary>
public sealed record DisclosureSkin(float Plate, float Glyph, float Slop, float Corner, float Bleed,
                                    Func<ColorF> Ink, Func<ColorF> Hover, Func<ColorF> Pressed);

public static partial class Controls
{
    /// <summary>The disclosure button (see the file header). <paramref name="toggle"/> must be a stable delegate (a field built
    /// once): the props compare it by reference, so a fresh closure would re-render the card that carries the button.</summary>
    public static Element Disclosure(bool expanded, Action toggle, DisclosureSkin skin)
        => Embed.Comp(new DisclosureProps(expanded, toggle, skin), static () => new DisclosureHost()) with { Key = "disclosure" };

    sealed record DisclosureProps(bool Expanded, Action Toggle, DisclosureSkin Skin);

    /// <summary>The chevron's angle: down while shut, up while open.</summary>
    const float DisclosureOpenDeg = 180f;

    sealed class DisclosureHost : Component
    {
        public override Element Render()
        {
            var p = UseProps<DisclosureProps>();
            var node = UseRef(NodeHandle.Null);
            var seeded = UseRef(false);
            bool expanded = p.Expanded;
            UseEffect(() =>
            {
                var anim = Context.Anim;
                var scene = Context.Scene;
                if (anim is null || scene is null || node.Value.IsNull || !scene.IsLive(node.Value)) return;
                float to = expanded ? DisclosureOpenDeg : 0f;
                if (!seeded.Value)
                {
                    seeded.Value = true;
                    anim.SeedValue(node.Value, AnimChannel.Rotation, to, MotionTokenId.Reveal, from: to);
                    return;
                }
                anim.SeedValue(node.Value, AnimChannel.Rotation, to, MotionTokenId.Reveal);
            }, expanded);

            var s = p.Skin;
            string name = Loc.Get(expanded ? Strings.Detail.Discography.HideTracks : Strings.Detail.Discography.ShowTracks);
            var button = new BoxEl
            {
                Width = s.Plate, Height = s.Plate, Shrink = 0f,
                Margin = new Edges4(0f, -s.Bleed, 0f, -s.Bleed),
                HitSlop = new Edges4(s.Slop, s.Slop, s.Slop, s.Slop),
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Corners = CornerRadius4.All(s.Corner),
                Fill = ColorF.Transparent, HoverFill = s.Hover(), PressedFill = s.Pressed(),
                Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
                // A press on the chevron is the chevron's: it never arms the card's drag, and the click is its own.
                BlocksDragArm = true,
                OnClick = p.Toggle,
                OnRealized = h => node.Value = h,
                Children = [Icon(Icons.ChevronDown, s.Glyph, s.Ink())],
            };
            return Named(button, name);
        }
    }
}
