// ── Themes/Zune/Structure/Views/Library/ZuneLibraryShowPane.cs — Zune's `Library.ShowPane` (themes plan 6.3; P5 Z7) ───────────────────────────
//
// Boards Z-Library-Podcasts, -Audiobooks: the pane's column for a show or an audiobook, the sibling of the album pane: the title (22 light, a link), the publisher, the meta
// line, the command row (the host's primary, the heart while it is not the primary, the "…"), then the SAME reader the route page mounts, so a click into the library and the
// standalone show page render byte-identical rows.
//
// THE PANE RE-SKINS IN PLACE. A selection change must NOT remount the pane, so the keys on the two arms are the ARM's ("showpane:body" / "showpane:skel"), never the slot's: the
// header, the commands and the reader are re-skinned by their own props while the component, its reader model memos and the reader's scroll position stay alive. The swap
// transition stays for the ONE swap that is a real shape change: the header skeleton giving way to the body. The READER keeps a key of its own, which the host puts on the
// element it hands in: a different show is a different list.
//
// At a Compact content tier (the drill-in's leaf) the command row is 40 tall: the primary asks the host for its Large size, the heart and the "…" take the 40 box.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using K = Wavee.Themes.Zune.ZuneLibraryKit;
using LR = Wavee.Themes.Zune.ZuneLibraryRules;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.Zune;

/// <summary>Zune's library show pane.</summary>
internal static class ZuneLibraryShowPane
{
    /// <summary>The two arms' keys are the ARM's, not the show's: the pane re-skins in place (see the file header). Only the reader is keyed per show.</summary>
    const string BodyKey = "showpane:body", SkelKey = "showpane:skel";

    /// <summary>The registered template of <see cref="ViewKeys.LibraryShowPane"/>.</summary>
    public static Element Template(LibraryShowPaneProps props, in ViewContext ctx)
    {
        // The Compact pane (the drill-in's leaf) draws its commands 40 tall: the targets of a one-column page.
        bool compact = ctx.Tier().Content == WidthTier.Compact;
        BoxEl child = props.HeaderReady ? Body(props, compact) : K.HeaderSkeleton();
        return new BoxEl
        {
            ZStack = true, Grow = 1f, Basis = 0f, MinHeight = 0f, ClipToBounds = true,
            Children = [child with { Key = props.HeaderReady ? BodyKey : SkelKey, Animate = K.PaneSwap }],
        };
    }

    /// <summary>The pane's column: the header, the command row and the SAME reader the route page mounts. The scroll key scopes its position per library slot (the host's).</summary>
    static BoxEl Body(LibraryShowPaneProps p, bool compact)
    {
        float box = compact ? LR.CompactCommandBox : LR.CommandBox;
        Element header = K.PaneHeader(p.Title, p.Open, R.Type.DenseMeta(p.Publisher) with { Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis }, p.Meta);
        Element commands = new BoxEl
        {
            Direction = 0, Gap = LR.PaneCommandsGap, AlignItems = FlexAlign.Center, MinWidth = 0f, Shrink = 0f,
            Padding = new Edges4(0f, LR.PaneCommandsTop, 0f, LR.PaneCommandsBottom),
            Children =
            [
                p.Primary!(static () => Tok.AccentDefault, compact ? ControlSize.Large : ControlSize.Medium),
                new BoxEl { Visible = p.HeartShown, Children = [K.Save(p.Uri, p.Title, box)] },
                K.MoreButton(p.More, box),
            ],
        };
        return new BoxEl
        {
            Direction = 1, Grow = 1f, ClipToBounds = true,
            Children = [header, commands, new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, MinHeight = 0f, ClipToBounds = true, Children = [p.Reader!] }],
        };
    }
}
