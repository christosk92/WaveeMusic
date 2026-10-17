// ── Themes/Zune/Structure/Views/Playlist/ZunePlaylistCover.cs — Zune's playlist cover: the face and the editable cover (themes plan 6.3; P5 Z6b1) ──
//
// Boards Z-Playlist-Owned-Followed (+ -Small, -Compact), Z-State-Empty-Playlist (+ -Compact). The page keeps the model reads (which tiles), the decode
// bucket and every verb (`Playlist.CoverEditor`: picking, encoding, the optimistic swap and the write are `PlaylistCover`'s); these draw what they are
// handed.
//
//   Playlist.CoverArt     the face: the server mosaic or a mosaic of member covers (2 x 2), else the art, else the neutral tile, at the Cover shape.
//                         No saturation boost on any arm (Zune's art is shown as it is), so the editable <-> read-only flip is structural only.
//   Playlist.CoverEditor  the art under a strip along its foot ("change cover"; "drop image to change cover" while a file is dragged anywhere or over
//                         the cover; the status while a write is out) that is ALWAYS there (a touch window has no hover). An empty playlist's cover is
//                         the dashed "choose a cover" tile of the empty board. The whole cover is the file-drop target and the click-to-pick.
//
// Numbers: `Rules\ZunePlaylistRules.cs`.

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using Z = Wavee.Themes.Zune.ZunePlaylistRules;

namespace Wavee.Themes.Zune;

/// <summary>Zune's playlist views.</summary>
internal static partial class ZunePlaylist
{
    // ══ 1. THE COVER FACE ═══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The registered template of <see cref="ViewKeys.PlaylistCoverArt"/>.</summary>
    public static Element CoverArtTemplate(PlaylistCoverArtProps p, in ViewContext ctx) => CoverArt(p);

    static Element CoverArt(PlaylistCoverArtProps p)
        => p.Mosaic is { } tiles
            ? Controls.Mosaic(tiles, p.Size, p.Size, R.Shape.For(ShapeRole.Cover))
            : Controls.Artwork(p.Single, p.Size, p.Size, R.Shape.For(ShapeRole.Cover), decodePx: p.DecodePx);

    // ══ 2. THE EDITABLE COVER ═══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The registered template of <see cref="ViewKeys.PlaylistCoverEditor"/>. The frame's framing box keeps the playlist drag source.</summary>
    public static Element CoverEditorTemplate(PlaylistCoverEditorProps p, in ViewContext ctx)
    {
        var hooks = ctx.Hooks;
        var dropOver = hooks.UseSignal(false);
        var drag = hooks.UseDragState();
        var lay = ZunePlaylistTiers.Of(ctx.Tier());
        bool fileDrag = drag.Active && drag.Payload is FileDropData;
        bool saving = p.Busy(), over = dropOver.Value || fileDrag;
        float size = p.Art.Size;
        var apply = p.Apply;
        bool empty = p.Art.Mosaic is null && string.IsNullOrEmpty(p.Art.Single);

        string word = Loc.Get(over ? Strings.Detail.Edit.DropCover : empty ? Strings.Theme.Zune.Detail.ChooseCover : Strings.Detail.Edit.ChangeCover);
        TextEl label = R.Type.DenseTitle(word) with
        {
            Size = Z.StripWordSize, Color = empty ? Tok.TextSecondary : R.OnMedia.Ink, Case = R.Case.For(LabelKind.Command),
            MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f,
        };

        Element face = empty
            ? new BoxEl
            {
                Key = "cover:empty", Direction = 1, Width = size, Height = size, Shrink = 0f, Gap = Z.EmptyGap, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Fill = R.Layer.RowHover, Corners = R.Shape.All(ShapeRole.Cover),
                BorderWidth = Z.EmptyStroke, BorderColor = Tok.StrokeControlSecondary, BorderDashOn = Z.EmptyDash, BorderDashOff = Z.EmptyDash,
                Children = saving ? [Status(saved: false)] : [Ui.Icon(Icons.Add, Z.EmptyGlyph, Tok.TextTertiary), label],
            }
            : CoverArt(p.Art);

        var kids = new List<Element>(2) { face };
        if (!empty)
            kids.Add(new BoxEl
            {
                Key = Z.CoverStripKey, Direction = 0, Width = size, Height = lay.CoverStrip, Shrink = 0f, AlignSelf = FlexAlign.End,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Gap = Z.StatusGap, MinWidth = 0f,
                Fill = R.OnMedia.CoverScrim, HitTestVisible = false,
                Children = saving ? [Status(saved: false)] : [label],
            });

        BoxEl cover = new()
        {
            Key = Z.CoverEditorKey, ZStack = true, Width = size, Height = size, Shrink = 0f, ClipToBounds = true, Corners = R.Shape.All(ShapeRole.Cover),
            Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, Cursor = CursorId.Hand, OnClick = p.Pick,
            DropTarget = new DropTargetSpec([DropKinds.Files],
                OnEnter: _ => dropOver.Value = true,
                OnLeave: _ => dropOver.Value = false,
                OnDrop: session =>
                {
                    dropOver.Value = false;
                    if (session.Payload is FileDropData files && files.Count > 0) apply(files.Paths[0]);
                }),
            Children = kids.ToArray(),
        };
        return Controls.Named(cover, Loc.Get(Strings.Detail.Edit.PickCover));
    }
}
