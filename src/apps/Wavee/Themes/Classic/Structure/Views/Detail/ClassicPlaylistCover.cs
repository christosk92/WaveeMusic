// ── Themes/Classic/Structure/Views/Detail/ClassicPlaylistCover.cs — Classic's playlist cover: the face and the editable cover (themes plan 6.1, T9b) ─────
//
// From `Entities\Playlist.UI.cs` §1 (W16): the cover face (a provider `spotify:mosaic:` token paints as the server-composed 2 × 2, else the art, else a 2 × 2
// mosaic of member album covers (≥ 4), else the first, else the neutral placeholder; saturation 1.18 on EVERY arm, so the editable ↔ read-only flip is
// structural only) and the editable cover (an always-mounted scrim whose OPACITY binds: hover, a live OS file drag anywhere, saving; the file-drop target and the
// click-to-pick). `Playlist.CoverEditor` keeps the model reads (which tiles), the decode bucket and every verb: picking, encoding, the optimistic swap and the
// write are `PlaylistCover`'s (#155), the click only starts the picker on its own thread and returns. The hover / drop-over cells are the picture's own.
//
// This class is split over the playlist views (`ClassicPlaylistCover.cs`, `ClassicPlaylistEditor.cs`, `ClassicPlaylistOwner.cs`, `ClassicPlaylistPanels.cs`,
// `ClassicPlaylistExtras.cs`).

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.Classic;

/// <summary>Classic's playlist views.</summary>
internal static partial class ClassicPlaylist
{
    // ══ 1. THE COVER FACE ═══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The registered template of <see cref="ViewKeys.PlaylistCoverArt"/>.</summary>
    public static Element CoverArtTemplate(PlaylistCoverArtProps p, in ViewContext ctx) => CoverArt(p);

    static Element CoverArt(PlaylistCoverArtProps p)
        => p.Mosaic is { } tiles
            ? Controls.Mosaic(tiles, p.Size, p.Size, Radii.Card)
            : Controls.Artwork(p.Single, p.Size, p.Size, Radii.Card, decodePx: p.DecodePx, saturation: ClassicPlaylistLayout.CoverSaturation);

    // ══ 2. THE EDITABLE COVER ═══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The registered template of <see cref="ViewKeys.PlaylistCoverEditor"/>: the editable cover. The frame's framing box keeps the playlist drag
    /// source.</summary>
    public static Element CoverEditorTemplate(PlaylistCoverEditorProps p, in ViewContext ctx)
    {
        var hooks = ctx.Hooks;
        var hovered = hooks.UseSignal(false);
        var dropOver = hooks.UseSignal(false);
        var drag = hooks.UseDragState();
        bool fileDrag = drag.Active && drag.Payload is FileDropData;
        var busy = p.Busy;
        bool saving = busy(), over = dropOver.Value;
        float size = p.Art.Size;
        var apply = p.Apply;

        Element overlayBody = saving
            ? StatusChip(saved: false)
            : new BoxEl
            {
                Direction = 1, Gap = ClassicPlaylistLayout.CoverScrimGap, AlignItems = FlexAlign.Center, HitTestVisible = false,
                Children =
                [
                    Icon(Icons.Camera, ClassicPlaylistLayout.CoverCameraGlyph, ClassicPlaylistLayout.OnScrim),
                    new TextEl(Loc.Get(over ? Strings.Detail.Edit.DropCover : Strings.Detail.Edit.ChangeCover))
                    {
                        Size = ClassicPlaylistLayout.CoverScrimTextSize, Weight = 600, Color = ClassicPlaylistLayout.OnScrim,
                        Width = ClassicPlaylistLayout.CoverScrimTextW, MaxLines = 2,
                        Wrap = TextWrap.WrapWholeWords, Trim = TextTrim.CharacterEllipsis,
                    },
                ],
            };
        return new BoxEl
        {
            ZStack = true, Width = size, Height = size, ClipToBounds = true, Corners = CornerRadius4.All(Radii.Card),
            Role = AutomationRole.Button, Cursor = CursorId.Hand, OnClick = p.Pick,
            OnHoverMove = _ => { if (!hovered.Peek()) hovered.Value = true; },
            OnPointerExit = () => hovered.Value = false,
            DropTarget = new DropTargetSpec([DropKinds.Files],
                OnEnter: _ => dropOver.Value = true,
                OnLeave: _ => dropOver.Value = false,
                OnDrop: session =>
                {
                    dropOver.Value = false;
                    if (session.Payload is FileDropData files && files.Count > 0) apply(files.Paths[0]);
                }),
            Children =
            [
                CoverArt(p.Art),
                new BoxEl
                {
                    Width = size, Height = size, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Fill = over ? ClassicPlaylistLayout.ScrimDrop : ClassicPlaylistLayout.ScrimHover, HitTestVisible = false,
                    Opacity = (Func<float>)(() => busy() || hovered.Value || dropOver.Value || fileDrag ? 1f : 0f),
                    Transition = MotionTok.ControlNormal,
                    Children = [overlayBody],
                },
            ],
        };
    }
}
