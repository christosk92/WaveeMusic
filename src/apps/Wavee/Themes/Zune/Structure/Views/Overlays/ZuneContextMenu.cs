// ── Themes/Zune/Structure/Views/Overlays/ZuneContextMenu.cs — Zune's `Menu.Context` and the context menu's presenters (themes plan 6.3, P5 Z8a) ──────
//
// Board Z-Overlays ("track menu · a header, a strip of round verbs, then words"). The menu itself is the engine's `ContextMenu` over `MenuFlyout`: Zune's control table
// (`ZuneControls.TrackMenu`) restyles the plate and the rows and hands the menu two presenter parts, both drawn here:
//
//   header   a 40 tile (the `Menu.Context` key: a square with the cover's 2, a circle for an artist), the title (15/20) over the subtitle (13/18, secondary), 16 in;
//            no divider under it.
//   strip    the model's primary commands as round verbs, 18 apart: the first (play) the accent disc, the others a 2-DIP ring; a toggle that is on wears the accent ring
//            and its filled icon. 32 across, 40 for a touch menu (a long press). A press runs the command and closes the menu (`ContextMenuStrip.Invoke`).
//
// The rows under them are the menu style's: 36 high, inset 16, a hairline where the model has a separator, the chevron of a sub-menu. The Compact sheet (`ZuneContextSheet`)
// draws the same header and the same round verbs.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;

namespace Wavee.Themes.Zune;

/// <summary>Zune's context menu parts.</summary>
internal static class ZuneContextMenu
{
    /// <summary>The registered template of <see cref="ViewKeys.MenuContext"/>: the header's identity tile.</summary>
    public static Element Tile(ContextMenuTileProps props, in ViewContext ctx)
        => Controls.Artwork(props.ArtUrl, ZuneOverlayRules.MenuTile, ZuneOverlayRules.MenuTile,
            props.Circular ? ZuneOverlayRules.MenuTileCircleRadius : R.Shape.Cover, decodePx: ZuneOverlayRules.MenuTileDecodePx);

    /// <summary>The control table's header presenter (<c>ContextMenu.Style.Header</c>): the identity strip above the commands.</summary>
    public static Element Header(ContextMenuHeader header, bool touch)
    {
        var words = new List<Element>(2)
        {
            R.Type.TrackTitle(header.Title) with { Color = Tok.TextPrimary, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
        };
        if (header.Subtitle is { Length: > 0 } subtitle)
            words.Add(R.Type.DenseMeta(subtitle) with { Color = Tok.TextSecondary, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis });

        var kids = new List<Element>(2);
        if (header.Leading is { } leading)
            kids.Add(new BoxEl
            {
                Width = ZuneOverlayRules.MenuTile, Height = ZuneOverlayRules.MenuTile, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                HitTestVisible = false, Children = [leading],
            });
        kids.Add(new BoxEl
        {
            Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Justify = FlexJustify.Center, HitTestVisible = false, Children = words.ToArray(),
        });
        return new BoxEl
        {
            Key = ZuneOverlayRules.MenuHeaderKey, Direction = 0, Gap = ZuneOverlayRules.MenuHeaderGap, AlignItems = FlexAlign.Center, MinWidth = 0f,
            Padding = ZuneOverlayRules.MenuHeaderPadding, HitTestVisible = false, Children = kids.ToArray(),
        };
    }

    /// <summary>The control table's primary-verb presenter (<c>ContextMenu.Style.PrimaryStrip</c>): a row of round verbs between the header and the rows.</summary>
    public static Element Strip(ContextMenuStrip strip) => Verbs(strip.Commands, strip.Touch, strip.Invoke);

    /// <summary>The round verbs of <paramref name="commands"/>: <paramref name="run"/> receives the command's index in the list (the menu's strip runs it and closes; the
    /// sheet closes first, then runs it).</summary>
    public static Element Verbs(IReadOnlyList<AppBarCommand> commands, bool touch, Action<int> run)
    {
        float size = touch ? ZuneOverlayRules.StripDiscTouch : ZuneOverlayRules.StripDisc;
        float glyph = touch ? ZuneOverlayRules.StripGlyphTouch : ZuneOverlayRules.StripGlyph;
        var verbs = new List<Element>(commands.Count);
        for (int i = 0; i < commands.Count; i++)
        {
            var command = commands[i];
            if (command.Kind == AppBarCommandKind.Separator) continue;
            int at = i;
            // The first verb is the play disc when it is a plain command (the track, album and queue menus all lead with it).
            bool disc = verbs.Count == 0 && command.Kind == AppBarCommandKind.Button;
            verbs.Add(ZuneOverlayParts.RoundVerb(ZuneOverlayRules.StripVerbKeyPrefix + i, command, disc, size, glyph, () => run(at)));
        }
        return new BoxEl
        {
            Key = ZuneOverlayRules.StripKey, Direction = 0, Gap = ZuneOverlayRules.StripGap, AlignItems = FlexAlign.Center, MinWidth = 0f,
            Padding = ZuneOverlayRules.StripPadding, Children = verbs.ToArray(),
        };
    }
}
