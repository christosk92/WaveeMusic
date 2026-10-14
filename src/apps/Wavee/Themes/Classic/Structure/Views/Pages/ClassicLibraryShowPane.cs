// ── Themes/Classic/Structure/Views/Pages/ClassicLibraryShowPane.cs — Classic's `Library.ShowPane` (themes plan 6.1, T10c1) ───────────────────────
//
// From `Entities\Show.Pane.cs` (`Render`, `Body`, the swap transition and the two arm keys) and `ClassicShowRail.Pane` (T9c's column): the library pane's right column for a show or an
// audiobook — the sibling of the album pane. The header never skeletons past the show's title (Album's own rule), the body below it is the SAME reader the route page mounts, so a click into the
// library and the standalone show page render byte-identical rows.
//
// THE PANE RE-SKINS IN PLACE (reports 2c / 2d). A selection change must NOT remount the pane, so the keys on the two arms are the ARM's ("showpane:body" / "showpane:skel"), never the slot's: the
// header, the commands and the reader are re-skinned by their own props while the component, its reader model memos and the reader's scroll position all stay alive. The swap transition stays for
// the ONE swap that is a real shape change — the header skeleton giving way to the body. The READER keeps a key of its own, on the slot, which the host puts on the element it hands in: a different
// show is a different list.
//
// The column is the header, the command row (the primary, the heart while it is not the primary, the ⋯) and the reader. The header and its loading twin are the album pane's (`ClassicLibraryPaneKit`).

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's library show pane.</summary>
internal static class ClassicLibraryShowPane
{
    /// <summary>The two arms' keys are the ARM's, not the show's — the pane re-skins in place (see the file header). Only the reader is keyed per show.</summary>
    const string BodyKey = "showpane:body", SkelKey = "showpane:skel";

    /// <summary>The registered template of <see cref="ViewKeys.LibraryShowPane"/>.</summary>
    public static Element Template(LibraryShowPaneProps props, in ViewContext ctx)
    {
        BoxEl child = props.HeaderReady ? Body(props) : ClassicLibraryPaneKit.HeaderSkeleton();
        return new BoxEl
        {
            ZStack = true, Grow = 1f, Basis = 0f, MinHeight = 0f, ClipToBounds = true,
            Children = [child with { Key = props.HeaderReady ? BodyKey : SkelKey, Animate = ClassicLibraryPaneKit.PaneSwap }],
        };
    }

    /// <summary>The library pane's column: the header, the command row (the primary, the heart while it is not the primary, the ⋯) and the SAME reader the route page mounts. The scroll key
    /// scopes its position per library slot (the host's).</summary>
    static BoxEl Body(LibraryShowPaneProps p)
    {
        Element header = ClassicLibraryPaneKit.PaneHeader(p.CoverUrl, p.Eyebrow, p.Title, p.Open,
            R.Type.DenseMeta(p.Publisher) with { Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
            p.Meta);
        Element commands = new BoxEl
        {
            Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, Padding = new Edges4(Spacing.XL, Spacing.S, Spacing.XL, Spacing.M),
            Children =
            [
                p.Primary!(static () => Tok.AccentDefault),
                new BoxEl { Visible = p.HeartShown, Children = [ClassicDetailKit.RailSatelliteSave(p.Uri, p.Title)] },
                ClassicDetailKit.RailSatelliteMore(p.More),
            ],
        };
        return new BoxEl
        {
            Direction = 1, Grow = 1f, ClipToBounds = true,
            Children = [header, commands, new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, MinHeight = 0f, ClipToBounds = true, Children = [p.Reader!] }],
        };
    }
}
