// ── Themes/Zune/Structure/Views/Panel/ZuneFriends.cs — Zune's `Panel.Friends` (P5 Z4a) ─────────────────────────────────────────────────
//
// Board Z-Part-Panel (pivot "friends"): keyed rows in a scroller, each a 52 column with the 40 round avatar, the friend's name (15/20), what
// they play (13/18: the track · the artist, in the accent while it is live) and where from (12/16, tertiary), the time at the right (12/16,
// tertiary) or, live, the accent equalizer. No plates, no hover fill but the row's: Zune draws content on the ground.
//
// The feed's rows and state, the clock and every navigation come through the props (as Classic's); the faces are the same: the rows (stale rows
// stay through a refresh or a transient error), the skeleton (bars), the offline and error vacancies (Retry only when the host can retry),
// the empty vacancy. Every face stands on the panel's gutter (the rows' inset), the vacancies in the panel's body voice (`ZuneVacancy`). Mount drives the feed's watchdog (`SetActive`), and a frame-clock interval moves the relative times on without a push.
// A row's menu is the track's own menu when the activity names a track, else the container menu of what the row opens.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;

using P = Wavee.Themes.Zune.ZunePanelBodies;

namespace Wavee.Themes.Zune;

/// <summary>Zune's friends body.</summary>
internal static class ZuneFriends
{
    /// <summary>The registered template of <see cref="ViewKeys.PanelFriends"/>.</summary>
    public static Element Template(FriendsProps props, in ViewContext ctx) => Embed.Comp(props, static () => new ZuneFriendsBody());

    internal static Element Row(FriendsProps props, in FriendEdge f, long now, IOverlayService overlay)
    {
        var user = new User(f.UserSlot);
        string name = props.ResolveString(user.NameId);
        bool live = Rail.Friends.IsLive(now, f.TimestampMs);
        string trackTitle = f.TrackSlot > 0 ? new Track(f.TrackSlot).Title : "";
        string artistName = f.ArtistSlot > 0 ? new Artist(f.ArtistSlot).Name : "";
        string albumTitle = f.AlbumSlot > 0 ? new Album(f.AlbumSlot).Title : "";
        string contextName = f.ContextSlot > 0 ? props.ResolveString(new Playlist(f.ContextSlot).TitleId) : "";
        string label = contextName.Length > 0 ? contextName : albumTitle.Length > 0 ? albumTitle : artistName;
        int contextSlot = f.ContextSlot, albumSlot = f.AlbumSlot, artistSlot = f.ArtistSlot, trackSlot = f.TrackSlot;
        var target = Rail.Friends.TargetOf(contextSlot, albumSlot, artistSlot);
        Action? onClick = target switch
        {
            Rail.Friends.Target.Context => () => props.GoTo(new Playlist(contextSlot).Uri, label),
            Rail.Friends.Target.Album => () => props.GoTo(new Album(albumSlot).Uri, label),
            Rail.Friends.Target.Artist => () => props.GoTo(new Artist(artistSlot).Uri, label),
            _ => null,
        };
        bool nav = onClick is not null;

        Element nameText = new TextEl(name)
        {
            Size = P.FriendNameSize, LineHeight = P.FriendNameLine, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
        };
        Element picture = PersonPicture.Create("", P.Avatar, displayName: name, imageSourcePath: Controls.ArtUrl(user.ImageId));
        Action? openProfile = props.OpenProfile(user);
        if (openProfile is not null)
        {
            picture = new BoxEl { Shrink = 0f, Role = AutomationRole.Button, Cursor = CursorId.Hand, BlocksDragArm = true, OnClick = openProfile, Children = [picture] };
            nameText = new BoxEl
            {
                Direction = 0, MinWidth = 0f, Role = AutomationRole.Hyperlink, Focusable = true, Cursor = CursorId.Hand, BlocksDragArm = true, OnClick = openProfile,
                Children = [((TextEl)nameText) with { HoverColor = Tok.AccentTextPrimary }],
            };
        }

        var lines = new List<Element>(3)
        {
            nameText,
            new TextEl(artistName.Length > 0 ? trackTitle + " · " + artistName : trackTitle)
            {
                Size = P.FriendTrackSize, LineHeight = P.FriendTrackLine, Color = live ? Tok.AccentTextPrimary : Tok.TextSecondary,
                MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
            },
        };
        string context = contextName.Length > 0 ? contextName : albumTitle;
        if (context.Length > 0)
            lines.Add(new TextEl(context) { Size = P.FriendSmallSize, LineHeight = P.FriendSmallLine, Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f });

        Func<ContextMenuModel?> menuOf = () =>
        {
            if (trackSlot > 0 && new Track(trackSlot) is { IsValid: true } track)
                return Track.Menu(new[] { track }, new Track.MenuOptions(ShowGoToAlbum: true));
            ActionTarget at;
            switch (target)
            {
                case Rail.Friends.Target.Context: at = ActionTarget.ForPlaylist(new Playlist(contextSlot).Uri, label); break;
                case Rail.Friends.Target.Album: at = ActionTarget.ForAlbum(new Album(albumSlot).Uri, label); break;
                case Rail.Friends.Target.Artist: at = ActionTarget.ForArtist(new Artist(artistSlot).Uri, label); break;
                default: return null;
            }
            return Menus.Container(in at, null, null);
        };

        var row = new BoxEl
        {
            Key = "fr:" + user.Uri.Text,
            Direction = 0, AlignItems = FlexAlign.Center, MinHeight = P.FriendRowH,
            HoverFill = nav ? R.Layer.RowHover : ColorF.Transparent, PressedFill = nav ? R.Layer.RowPressed : ColorF.Transparent,
            Role = nav ? AutomationRole.Button : AutomationRole.None, Cursor = nav ? CursorId.Hand : CursorId.Arrow,
            Focusable = nav, FocusVisualMargin = Design.FocusInsetRow, OnClick = onClick,
            Enter = new EnterExit(Dy: P.FriendEnterDy, Opacity: 0f, Active: true),
            Exit = new EnterExit(Dy: -P.FriendExitDy, Opacity: 0f, Active: true),
            Layout = LayoutTransition.Slide,
            Children =
            [
                new BoxEl { Width = P.AvatarCol, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center, Children = [picture] },
                new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Justify = FlexJustify.Center, Gap = P.FriendLinesGap, Children = lines.ToArray() },
                new BoxEl
                {
                    Shrink = 0f, Padding = new Edges4(P.FriendWhenPad, 0f, 0f, 0f), AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Children =
                    [
                        live
                            ? Controls.Equalizer(true, Tok.AccentDefault, P.FriendEq)
                            : new TextEl(Ago(now - f.TimestampMs)) { Size = P.FriendSmallSize, LineHeight = P.FriendSmallLine, Color = Tok.TextTertiary, MaxLines = 1 },
                    ],
                },
            ],
        };
        return Controls.IsNullOverlay(overlay) ? row : row.WithContextMenu(overlay, menuOf);
    }

    static string Ago(long ageMs) => Rail.Friends.Age(ageMs, out long n) switch
    {
        Rail.Friends.AgeUnit.Minutes => Strings.Friends.MinAgo(n),
        Rail.Friends.AgeUnit.Hours => Strings.Friends.HrAgo(n),
        Rail.Friends.AgeUnit.Days => Strings.Friends.DAgo(n),
        _ => Loc.Get(Strings.Friends.Now),
    };

    internal static Element SkeletonRow() => new BoxEl
    {
        Direction = 0, MinHeight = P.FriendRowH, AlignItems = FlexAlign.Center,
        Children =
        [
            new BoxEl
            {
                Width = P.AvatarCol, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center,
                Children = [new BoxEl { Width = P.Avatar, Height = P.Avatar, Corners = Radii.Circle(P.Avatar), Fill = Tok.FillSubtleSecondary }],
            },
            new BoxEl
            {
                Direction = 1, Grow = 1f, Gap = P.SkeletonGap,
                Children =
                [
                    new BoxEl { Width = P.SkeletonNameW, Height = P.SkeletonBarH, Fill = Tok.FillSubtleSecondary },
                    new BoxEl { Width = P.SkeletonDetailW, Height = P.SkeletonBarH, Fill = Tok.FillSubtleSecondary },
                ],
            },
        ],
    };
}

/// <summary>The friends body: the feed's face for the rows and state it reads.</summary>
internal sealed class ZuneFriendsBody : Component
{
    public override Element Render()
    {
        var props = UseProps<FriendsProps>();
        UseEffect(() => { props.SetActive(true); return () => props.SetActive(false); }, DepKey.Empty);
        var tick = UseSignal(0);
        UseInterval(() => tick.Value = tick.Peek() + 1, Rail.Friends.TickMs);
        var overlay = UseContext(Overlay.Service);
        _ = tick.Value;
        var rows = props.Feed.Read(out var state);

        switch (Rail.Friends.SurfaceFor(rows.Length, state))
        {
            case Rail.Friends.Surface.Rows:
                long now = props.NowMs();
                var kids = new Element[rows.Length];
                for (int i = 0; i < rows.Length; i++) kids[i] = ZuneFriends.Row(props, in rows[i], now, overlay);
                return new ScrollEl
                {
                    Grow = 1f, MinHeight = 0f, AutoEdgeFade = true, ScrollKey = "friendspanel",
                    Content = new BoxEl { Direction = 1, MinHeight = 0f, Padding = P.Inset, Children = kids },
                };
            case Rail.Friends.Surface.Skeleton:
                var bars = new Element[P.SkeletonRows];
                for (int i = 0; i < bars.Length; i++) bars[i] = ZuneFriends.SkeletonRow();
                return new BoxEl { Direction = 1, MinHeight = 0f, Padding = P.Inset, Children = bars };
            case Rail.Friends.Surface.Offline:
                return P.OnGutter(Controls.Vacancy(Controls.VacancyVoice.Offline, Controls.VacancyScale.Compact, title: Loc.Get(Strings.Friends.Offline), subtitle: ""));
            case Rail.Friends.Surface.Error:
                return P.OnGutter(Controls.Vacancy(Controls.VacancyVoice.Error, Controls.VacancyScale.Compact, title: Loc.Get(Strings.Friends.Error), subtitle: "",
                                                   actionLabel: Loc.Get(Strings.Friends.Retry), onAction: props.Refresh()));
            default:
                return P.OnGutter(Controls.Vacancy(Controls.VacancyVoice.Empty, Controls.VacancyScale.Compact,
                                                   title: Loc.Get(Strings.Friends.Empty), subtitle: Loc.Get(Strings.Friends.EmptyHint)));
        }
    }
}
