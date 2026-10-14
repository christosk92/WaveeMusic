// ── Themes/Classic/Structure/Views/Detail/ClassicPlaylistOwner.cs — Classic's playlist owner block and collaborator flyout (themes plan 6.1, T9b) ───────
//
// From `Entities\Playlist.UI.cs` §4 (W25, W29): the frame's Attribution slot, ONE composition for both arms — the collaborator pile (the frame's own predicate,
// `Detail.Text.ShowCollaborators`) or the owner row (avatar and name: the whole run is the link to the owner's profile, #161), each with the self-gating invite
// affordance. `Playlist.OwnerBlock` keeps the gate (the stamp over the playlist row, the owner and collaborator users and the session's edit-path verdict), the
// collaborator set, the permission decision (`IsOwner`, `CanAdministratePermissions`, `Live`), and the open / close of the two popups with their anchors.
//
// THE RESERVED OWNER ROW (W17 / W29). Its two arms are different sizes — the owner run is a 24 DIP portrait, the collaborator pile is a 32 DIP framed face
// (`Controls.FaceOuter`) — and the invite pill between them is 28. Flipping "Collaborative" swaps the arms, so an unreserved row changed height AND width
// mid-interaction: the Play pill stepped down, the invite button stepped sideways, and the access flyout anchored to that button moved under the cursor. One
// height for both arms, and a grown leading arm so the invite pill keeps its x.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.Classic;

internal static partial class ClassicPlaylist
{
    // ══ 1. THE OWNER BLOCK ══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The registered template of <see cref="ViewKeys.PlaylistOwner"/>.</summary>
    public static Element OwnerTemplate(PlaylistOwnerProps p, in ViewContext ctx)
    {
        // THE LEADING ARM lives in its own GROWN box, so the pile and the owner run measure the same and the invite pill after them never moves sideways when
        // "Collaborative" flips (the flyout is anchored to that pill).
        Element lead = p.Pile ? PileButton(p) : OwnerRun(p);
        var kids = new List<Element>(2)
        {
            new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Grow = 1f, Basis = 0f, MinWidth = 0f,
                Children = [lead],
            },
        };
        if (p.Invite) kids.Add(InviteButton(p));
        return new BoxEl
        {
            Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, MinWidth = 0f,
            Height = ClassicPlaylistLayout.OwnerRowHeight,      // RESERVED: the taller arm's height, always (see the header)
            MaxWidth = float.IsFinite(p.Width) && p.Width > 0f ? p.Width : float.NaN,
            Children = kids.ToArray(),
        };
    }

    /// <summary>The owner opens their profile (#161): the whole run is the link (avatar + name), a hyperlink-looking name on hover. An owner row that cannot
    /// route (an invalid handle) stays the plain, inert run it was.</summary>
    static Element OwnerRun(PlaylistOwnerProps p)
    {
        bool go = p.OpenOwner is not null;
        return new BoxEl
        {
            // Grow/Basis 0 is load-bearing: the name below is Basis 0, and a row with a finite width measures a Basis-0 child at exactly 0 (FlexLayout's
            // intrinsic-suppression rule). Without its own grow this run was measured 24 + gap + 0 and arranged at that width — the avatar painted, the name
            // got 0 DIP and ellipsised to nothing. The pile arm stays content-sized, so the invite pill still keeps its x.
            Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, Grow = 1f, Basis = 0f, MinWidth = 0f,
            Role = go ? AutomationRole.Button : AutomationRole.None, Focusable = go,
            Cursor = go ? CursorId.Hand : null, OnClick = p.OpenOwner,
            Children =
            [
                PersonPicture.Create("", ClassicPlaylistLayout.OwnerAvatar, displayName: p.OwnerName, imageSourcePath: p.OwnerImageUrl),
                R.Type.TrackTitle(p.OwnerName) with
                {
                    Grow = 1f, Basis = 0f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                    HoverColor = go ? Tok.AccentTextPrimary : default, BrushTransitionMs = Design.Motion.Faster,
                },
            ],
        };
    }

    static Element PileButton(PlaylistOwnerProps p)
    {
        var openMembers = p.OpenMembers;
        return ToolTip.Wrap(new BoxEl
        {
            // No VERTICAL padding: the framed face is already OwnerRowHeight tall and the reserved row is measured on it — 4 DIP more here would make the
            // collaborative arm the taller one all over again.
            Direction = 0, Gap = ClassicPlaylistLayout.PileGap, AlignItems = FlexAlign.Center, MinWidth = 0f, Shrink = 1f,
            Padding = new Edges4(ClassicPlaylistLayout.PilePadX, 0f, ClassicPlaylistLayout.PilePadX, 0f),
            Corners = CornerRadius4.All(ClassicPlaylistLayout.PileRadius),
            HoverFill = Tok.FillCardDefault, PressedFill = Tok.FillSubtleTertiary, BrushTransitionMs = Design.Motion.Faster,
            Role = AutomationRole.Button, Cursor = CursorId.Hand, Focusable = true, OnClick = openMembers,
            OnRealized = p.PileRealized,
            OnKeyDown = e => { if (e.KeyCode is Keys.Down or Keys.F4) { openMembers(); e.Handled = true; } },
            Children =
            [
                Controls.FacePile(p.Faces, maxVisible: p.Faces.Count, overflow: p.Overflow),
                Icon(Icons.ChevronDownSmall, ClassicPlaylistLayout.PileChevron, Tok.TextTertiary),
                new TextEl(p.Label)
                {
                    Size = ClassicPlaylistLayout.PileLabelSize, Weight = 700, Color = Tok.AccentTextPrimary, MinWidth = 0f, MaxLines = 1,
                    Trim = TextTrim.CharacterEllipsis,
                },
            ],
        }, Loc.Get(Strings.Detail.CollabView));
    }

    /// <summary>The ROUND 28 arm under 260 DIP (glyph 14), the labelled pill above it (glyph 12). Both inks are TextPrimary in 0.3 (ch 06 §9: the two arms used to
    /// disagree).</summary>
    static Element InviteButton(PlaylistOwnerProps p)
    {
        bool wide = ClassicPlaylistLayout.InviteIsWide(p.Width);
        string label = Loc.Get(Strings.Detail.Edit.InviteCollaborators);
        var box = new BoxEl
        {
            Direction = 0, Gap = ClassicPlaylistLayout.InviteGap, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Shrink = 0f,
            Height = ClassicPlaylistLayout.InviteRoundEdge, Width = wide ? float.NaN : ClassicPlaylistLayout.InviteRoundEdge,
            Padding = wide ? new Edges4(ClassicPlaylistLayout.InvitePadL, 0f, ClassicPlaylistLayout.InvitePadR, 0f) : default,
            Corners = CornerRadius4.All(ClassicPlaylistLayout.InviteRadius),
            BorderWidth = 1f, BorderColor = Tok.StrokeControlDefault, HoverFill = Tok.FillSubtleSecondary,
            PressedFill = Tok.FillSubtleTertiary, BrushTransitionMs = Design.Motion.Faster,
            HoverScale = R.Motion.ScaleSubtle.Hover, PressScale = R.Motion.ScaleSubtle.Press,
            Role = AutomationRole.Button, Cursor = CursorId.Hand, Focusable = true, OnClick = p.OpenAccess,
            OnRealized = p.InviteRealized,
            Children = wide
                ? [Icon(Icons.Friends, ClassicPlaylistLayout.InviteGlyphWide, Tok.TextPrimary),
                   new TextEl(label) { Size = ClassicPlaylistLayout.InviteTextSize, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1 }]
                : [Icon(Icons.Friends, ClassicPlaylistLayout.InviteGlyphRound, Tok.TextPrimary)],
        };
        return wide ? box : ToolTip.Wrap(box, label);
    }

    // ══ 2. THE COLLABORATOR FLYOUT ══════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The registered template of <see cref="ViewKeys.PlaylistMembersFlyout"/>: 44-DIP rows (portrait and name) in a scrolling list. A member opens
    /// their profile (#161); the verb is built at click time so a name that landed while the flyout was open rides on it.</summary>
    public static Element MembersFlyoutTemplate(PersonFlyoutProps p, in ViewContext ctx)
    {
        var rows = new Element[p.Rows.Count];
        for (int i = 0; i < rows.Length; i++)
        {
            var row = p.Rows[i];
            rows[i] = new BoxEl
            {
                Direction = 0, Gap = ClassicPlaylistLayout.MemberGap, Height = ClassicPlaylistLayout.MemberRowH, AlignItems = FlexAlign.Center,
                Padding = new Edges4(ClassicPlaylistLayout.MemberPadX, 0f, ClassicPlaylistLayout.MemberPadX, 0f),
                Corners = CornerRadius4.All(ClassicPlaylistLayout.MemberRadius), HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
                Role = AutomationRole.MenuItem, Cursor = CursorId.Hand,
                OnClick = row.Go,
                Children =
                [
                    PersonPicture.Create("", ClassicPlaylistLayout.MemberPortrait, displayName: row.Name, imageSourcePath: row.ImageUrl),
                    new TextEl(row.Name)
                    {
                        Size = ClassicPlaylistLayout.MemberNameSize, Weight = 600, Color = Tok.TextPrimary, Grow = 1f, MinWidth = 0f, MaxLines = 1,
                        Trim = TextTrim.CharacterEllipsis,
                    },
                ],
            };
        }
        var list = new BoxEl { Direction = 1, Gap = ClassicPlaylistLayout.MemberListGap, Width = R.Space.FacePileListW, Children = rows };
        return new BoxEl
        {
            Direction = 1, Width = R.Space.FacePileFlyoutW, MaxHeight = R.Space.FacePileFlyoutH, Padding = Edges4.All(Spacing.S),
            Children = [ScrollView(list) with { Width = R.Space.FacePileListW, MaxHeight = R.Space.FacePileListH, ContentSized = true, AutoEdgeFade = true }],
        };
    }
}
