// ── Themes/Classic/Structure/Views/Detail/ClassicDetailKit.cs — the pieces Classic's detail views share (themes plan 6.1, T9a / T9b / T9c) ──────────
//
// The page-facing builders T9a left in `Entities\Detail.UI.cs` as the seam, the ones the structure's detail views build from: the keyed rail rows (a
// position-only FLIP, and the fade-up that lets a row arrive late), the eyebrow run, and the billed-artist line box (the rail's default line and the
// album's face pile are this ONE look). Moved verbatim with their numbers (`Rules\ClassicDetailKitLayout.cs`); what DECIDES stays core
// (`Detail.RailArtistLine`: the width ladder and its hysteresis, the reserved heights, the "+N" arithmetic).
//
// The class is PARTIAL: the rail satellites only the show and episode pages use (T9c) are in `ClassicDetailKit.Satellites.cs`.

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;
using RailArtistLine = Wavee.Detail.RailArtistLine;
using RailArtistMode = Wavee.Detail.RailArtistMode;
using Skeleton = Wavee.Detail.Skeleton;

namespace Wavee.Themes.Classic;

/// <summary>The shared pictures of Classic's detail views.</summary>
internal static partial class ClassicDetailKit
{
    // ══ 1. THE RAIL ROWS ════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>A late row's entrance: it fades up when it arrives after the frame.</summary>
    public static readonly EnterExit FadeUp = new(Opacity: 0f, Active: true);

    /// <summary>The position-only FLIP a keyed row takes when a row above it arrives or leaves.</summary>
    public static readonly LayoutTransition Shove = new(TransitionChannels.Position,
        TransitionDynamics.Tween(Expressive.Fast, Easing.SmoothOut));

    /// <summary>A keyed row that exists in BOTH models: it only ever gets pushed, so it takes the position-only FLIP.</summary>
    public static BoxEl Row(string key, Element child) => new() { Key = key, Direction = 1, Layout = Shove, Children = [child] };

    /// <summary>Release facts arrive after the album frame. Keep that late panel from participating in the rail's position transition; its own content
    /// fades in while the rest of the page keeps its settled geometry.</summary>
    public static BoxEl ReleaseRow(string key, Element child) => new() { Key = key, Direction = 1, Children = [child] };

    /// <summary>A keyed row that can arrive LATE: fades up on insert, FLIPs on later shoves (ch 03 §0.11).</summary>
    public static BoxEl LateRow(string key, Element child) => new()
    {
        Key = key, Direction = 1, Layout = Shove, Enter = FadeUp, Children = [child],
    };

    /// <summary>The eyebrow: tertiary, one line.</summary>
    public static TextEl EyebrowRun(string text) => R.Type.Eyebrow(text) with
    {
        Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
    };

    // ══ 2. THE BILLED-ARTIST LINE ═══════════════════════════════════════════════════════════════════════════════════════

    const string LocRailMoreArtists = "detail.row.moreArtists";

    /// <summary>The line box's height ease: real layout, so the rows under it reflow with it.</summary>
    static readonly LayoutTransition ArtistLineReflow = new(TransitionChannels.Size, MotionTok.ContentResize.ToDynamics(),
        Size: SizeMode.Reflow, Anchor: SizeAnchor.Leading, Axes: SizeAxes.Height);
    static readonly EnterExit ArtistFaceFade = new(Opacity: 0f, Active: true);

    /// <summary>The billed-artist line in one mode: the ONE look both the default line and the album's own (<c>Album.FacePileHost</c>) paint. The NAME is
    /// what survives: <see cref="RailArtistLine"/> drops the faces first (Wide: pile + names, Medium: the lead face + names on two lines + "+N", Narrow:
    /// names only), and the chevron follows the text in every mode. Nothing under the pointer moves on hover (the plate is a fill); a mode change never
    /// snaps: the line box eases its height, the face slots are keyed per mode and cross-fade in place, and the names glide to their new position
    /// (<see cref="Shove"/>).</summary>
    public static Element ArtistLineBox(RailArtistMode mode, ArtistLineSpec s)
    {
        var kids = new List<Element>(4);
        if (s.Faces.Count > 0)
        {
            if (mode == RailArtistMode.Wide)
                kids.Add(FaceSlot("faces:pile", Controls.FacePile(s.Faces, RailArtistLine.WideFaces, s.Overflow)));
            else if (mode == RailArtistMode.Medium)
            {
                var lead = s.Faces[0];
                kids.Add(FaceSlot("faces:lead", PersonPicture.Create("", Skeleton.OwnerAvatar, displayName: lead.Name, imageSourcePath: lead.ImageUrl)));
            }
        }

        bool wide = mode == RailArtistMode.Wide;
        kids.Add(new BoxEl
        {
            Key = "names", Direction = 0, Shrink = 1f, MinWidth = 0f, Layout = Shove,
            Children =
            [
                new TextEl(s.Names)
                {
                    Size = ClassicDetailKitLayout.ArtistNamesSize, LineHeight = RailArtistLine.TextLine, Weight = 600, Color = Tok.AccentTextPrimary,
                    MinWidth = 0f, Shrink = 1f,
                    MaxLines = wide ? 1 : 2, Wrap = wide ? TextWrap.NoWrap : TextWrap.WrapWholeWords,
                    Trim = TextTrim.CharacterEllipsis,
                },
            ],
        });
        int plus = mode == RailArtistMode.Medium ? RailArtistLine.PlusN(s.Count, 1) : 0;
        if (plus > 0)
            kids.Add(new TextEl(Loc.Format(LocRailMoreArtists, ("count", plus)))
            {
                Key = "plus", Size = ClassicDetailKitLayout.ArtistPlusSize, LineHeight = ClassicDetailKitLayout.ArtistPlusLine, Weight = 600,
                Color = Tok.TextTertiary, Shrink = 0f, MaxLines = 1,
            });
        if (s.Menu) kids.Add(new BoxEl
        {
            Key = "chevron", Shrink = 0f, AlignItems = FlexAlign.Center, Layout = Shove,
            Children = [Icon(Icons.ChevronDownSmall, 8f, Tok.TextTertiary)],
        });

        bool live = s.Click is not null;
        Element trigger = new BoxEl
        {
            Key = "trigger",
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, Shrink = 1f, MinWidth = 0f, MaxWidth = s.Width,
            Padding = new Edges4(ClassicDetailKitLayout.ArtistTriggerPadX, RailArtistLine.PadY, ClassicDetailKitLayout.ArtistTriggerPadX, RailArtistLine.PadY),
            Corners = CornerRadius4.All(Radii.Card),
            Fill = ColorF.Transparent,
            HoverFill = live ? Tok.FillCardDefault : ColorF.Transparent, PressedFill = live ? Tok.FillSubtleTertiary : ColorF.Transparent,
            Role = !live ? AutomationRole.None : s.Menu ? AutomationRole.Button : AutomationRole.Hyperlink,
            Focusable = live, Cursor = live ? CursorId.Hand : (CursorId?)null,
            FocusVisualMargin = Design.FocusInsetRow,
            OnClick = s.Click, OnKeyDown = s.OnKey, OnRealized = s.OnRealized,
            Children = kids.ToArray(),
        };
        // The tooltip wrapper shrink-wraps to the trigger, whose MaxWidth is the line's measure: it can never outgrow the row.
        if (s.Menu) trigger = ToolTip.Wrap(trigger, s.Tip);

        return new BoxEl
        {
            Key = "artist-line", Direction = 0, AlignItems = FlexAlign.Center, MinWidth = 0f, MaxWidth = s.Width,
            MinHeight = RailArtistLine.HeightOf(mode), ClipToBounds = true, Animate = ArtistLineReflow,
            Children = [trigger],
        };
    }

    /// <summary>One mode's face slot: keyed per look, so a width step cross-fades the faces in place.</summary>
    static Element FaceSlot(string key, Element faces) => new BoxEl
    {
        Key = key, Direction = 0, AlignItems = FlexAlign.Center, Shrink = 0f, Children = [faces],
        Enter = ArtistFaceFade, Exit = ArtistFaceFade, Transition = MotionTok.ControlFaster,   // Design.Motion.Faster (83 ms)
    };
}
