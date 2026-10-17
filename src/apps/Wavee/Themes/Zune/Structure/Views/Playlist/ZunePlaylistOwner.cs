// ── Themes/Zune/Structure/Views/Playlist/ZunePlaylistOwner.cs — Zune's playlist owner block (themes plan 6.3; P5 Z6b1) ───────────────────────────
//
// Boards Z-Playlist-Owned-Followed (+ -Small, -Compact), Z-Playlist-Followed (+ -Small, -Huge). The frame's Attribution slot, ONE composition for both arms:
// the collaborator pile (the frame's own predicate) or the owner run (avatar and name: the whole run is the link to the owner's profile), each with the
// self-gating invite ring at the row's end. `Playlist.OwnerBlock` keeps the gate, the collaborator set, the permission decision and the two popups with
// their anchors; this draws what it was handed.
//
//   · the OWNER run is the avatar and the name in the artist voice (18/24), a link in the primary ink, the accent on hover (the board: a person link);
//   · the PILE is the faces, the chevron and the label in the same voice (the whole run opens the collaborator flyout);
//   · the INVITE ring is the detail kit's ring (36; 40 at Compact and 4K) with a plus: the board draws "invite" among the rings, but the ring row is the
//     frame's (it carries the play, save, share and "..." rings), so the owner row carries this one.
//
// THE RESERVED ROW. Its two arms differ in height (the owner run is the artist line, the pile a 32 framed face) and the ring is taller than both, so the row is
// one height for both, and the leading arm is a grown box: flipping "collaborative" moves neither the ring (the access flyout is anchored to it) nor what is
// below. The collaborator flyout is the album's every-artist flyout (`ZuneAlbumRelease.FlyoutTemplate`: the same rows, the same props).
//
// Numbers: `Rules\ZunePlaylistRules.cs` and the detail record.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using K = Wavee.Themes.Zune.ZuneDetailKit;
using Z = Wavee.Themes.Zune.ZunePlaylistRules;

namespace Wavee.Themes.Zune;

internal static partial class ZunePlaylist
{
    /// <summary>The registered template of <see cref="ViewKeys.PlaylistOwner"/>.</summary>
    public static Element OwnerTemplate(PlaylistOwnerProps p, in ViewContext ctx)
    {
        var lay = ZunePlaylistTiers.Of(ctx.Tier()).Detail;
        // The leading arm lives in its own GROWN box so the ring after it never moves sideways when "collaborative" flips.
        Element lead = p.Pile ? PileButton(p, lay) : OwnerRun(p, lay);
        var kids = new List<Element>(2)
        {
            new BoxEl { Key = "owner:lead", Direction = 0, AlignItems = FlexAlign.Center, Grow = 1f, Basis = 0f, MinWidth = 0f, Children = [lead] },
        };
        if (p.Invite) kids.Add(InviteRing(p, lay));
        return new BoxEl
        {
            Key = Z.OwnerKey, Direction = 0, Gap = Z.OwnerGap, AlignItems = FlexAlign.Center, MinWidth = 0f,
            Height = MathF.Max(lay.RingSize, MathF.Max(lay.ArtistLine, Controls.FaceOuter)),   // RESERVED: the tallest arm, always
            MaxWidth = float.IsFinite(p.Width) && p.Width > 0f ? p.Width : float.NaN,
            Children = kids.ToArray(),
        };
    }

    /// <summary>The owner opens their profile: the whole run is the link (avatar and name). A row that cannot route stays the plain, inert run.</summary>
    static Element OwnerRun(PlaylistOwnerProps p, ZuneDetailLayout lay)
    {
        bool go = p.OpenOwner is not null;
        return new BoxEl
        {
            // Grow / Basis 0 is load-bearing: the name is Basis 0, and a row with a finite width measures a Basis-0 child at exactly 0.
            Direction = 0, Gap = Z.OwnerGap, AlignItems = FlexAlign.Center, Grow = 1f, Basis = 0f, MinWidth = 0f,
            Role = go ? AutomationRole.Hyperlink : AutomationRole.None, Focusable = go, AllowFocusOnInteraction = false,
            Cursor = go ? CursorId.Hand : null, OnClick = p.OpenOwner,
            Children =
            [
                PersonPicture.Create("", Z.OwnerAvatar, displayName: p.OwnerName, imageSourcePath: p.OwnerImageUrl),
                K.ArtistText(p.OwnerName, float.NaN, lay) with { Grow = 1f, Basis = 0f, HoverColor = go ? Tok.AccentTextPrimary : default },
            ],
        };
    }

    static Element PileButton(PlaylistOwnerProps p, ZuneDetailLayout lay)
    {
        var openMembers = p.OpenMembers;
        return ToolTip.Wrap(new BoxEl
        {
            Direction = 0, Gap = Z.PileGap, AlignItems = FlexAlign.Center, MinWidth = 0f, Shrink = 1f,
            Role = AutomationRole.Button, Cursor = CursorId.Hand, Focusable = true, AllowFocusOnInteraction = false, OnClick = openMembers,
            OnRealized = p.PileRealized,
            OnKeyDown = e => { if (e.KeyCode is Keys.Down or Keys.F4) { openMembers(); e.Handled = true; } },
            Children =
            [
                Controls.FacePile(p.Faces, maxVisible: p.Faces.Count, overflow: p.Overflow),
                Ui.Icon(Icons.ChevronDownSmall, Z.PileChevron, Tok.TextTertiary),
                K.ArtistText(p.Label, float.NaN, lay) with { HoverColor = Tok.AccentTextPrimary },
            ],
        }, Loc.Get(Strings.Detail.CollabView));
    }

    /// <summary>The invite ring (the kit's outline disc with a plus), the anchor of the access panel.</summary>
    static Element InviteRing(PlaylistOwnerProps p, ZuneDetailLayout lay)
    {
        string label = Loc.Get(Strings.Detail.Edit.InviteCollaborators);
        BoxEl ring = K.Disc(Icons.Add, lay.RingSize, lay.RingGlyph, null) with
        {
            Key = "owner:invite", Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, Cursor = CursorId.Hand, BlocksDragArm = true,
            OnClick = p.OpenAccess, OnRealized = p.InviteRealized,
        };
        return Controls.Named(ring, label);
    }

    /// <summary>The registered template of <see cref="ViewKeys.PlaylistMembersFlyout"/>: the album's every-artist flyout (44 rows, the 32 portrait and the name).</summary>
    public static Element MembersFlyoutTemplate(PersonFlyoutProps p, in ViewContext ctx) => ZuneAlbumRelease.FlyoutTemplate(p, in ctx);
}
