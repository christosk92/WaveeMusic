// ── Themes/Classic/Structure/Views/Panel/ClassicFriends.cs — Classic's `Panel.Friends` (themes plan 6.1, T6b) ───────────────────
//
// From `Shell/Rail.UI.cs`: the friends panel. Keyed rows in a scroller so every push-driven upsert enters/exits/FLIPs into place.
// Mount == visible (drives the host's watchdog through `props.SetActive`); a 30 s frame-clock interval advances the live window and
// the relative times without a push, and auto-pauses while the window is parked.
//
// The faces (rows win whenever there are any, so stale rows stay visible through a refresh or a transient error): the rows, the
// skeleton (idle / loading), the offline vacancy, the error vacancy with a stock Standard Retry (never an accent one, and only
// when the host can retry), and the empty vacancy. The feed's rows and state, the clock and every navigation come through the
// props; the rows themselves are entity handles the template reads.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's friends body.</summary>
internal static class ClassicFriends
{
    /// <summary>The registered template of <see cref="ViewKeys.PanelFriends"/>.</summary>
    public static Element Template(FriendsProps props, in ViewContext ctx)
        => Embed.Comp(props, static () => new ClassicFriendsBody());

    internal static BoxEl FriendsShell(Element content) => new()
    {
        Direction = 1, Grow = 1f, MinHeight = 0f, ClipToBounds = true,
        Padding = new Edges4(Spacing.M, Spacing.XS, Spacing.M, 0f), Children = [content],
    };

    /// <summary>One friend-activity row. Right-click / Menu key / Shift+F10 open the entity menu of what the friend played:
    /// the track's own menu when the activity names a track (the same one every track row offers), else the container menu
    /// of the context, album or artist the row navigates to (<see cref="Menus.Container"/>).</summary>
    internal static Element FriendRow(FriendsProps props, in FriendEdge f, long now, IOverlayService overlay)
    {
        float avatar = ClassicFriendsLayout.Avatar;
        var user = new User(f.UserSlot);
        string name = props.ResolveString(user.NameId);
        bool live = Rail.Friends.IsLive(now, f.TimestampMs);
        string trackTitle = f.TrackSlot > 0 ? new Track(f.TrackSlot).Title : "";
        string artistName = f.ArtistSlot > 0 ? new Artist(f.ArtistSlot).Name : "";
        string albumTitle = f.AlbumSlot > 0 ? new Album(f.AlbumSlot).Title : "";
        string contextName = f.ContextSlot > 0 ? props.ResolveString(new Playlist(f.ContextSlot).TitleId) : "";
        string label = contextName.Length > 0 ? contextName : albumTitle.Length > 0 ? albumTitle : artistName;
        int contextSlot = f.ContextSlot, albumSlot = f.AlbumSlot, artistSlot = f.ArtistSlot;
        Action? onClick = Rail.Friends.TargetOf(contextSlot, albumSlot, artistSlot) switch
        {
            Rail.Friends.Target.Context => () => props.GoTo(new Playlist(contextSlot).Uri, label),
            Rail.Friends.Target.Album => () => props.GoTo(new Album(albumSlot).Uri, label),
            Rail.Friends.Target.Artist => () => props.GoTo(new Artist(artistSlot).Uri, label),
            _ => null,
        };
        bool nav = onClick is not null;

        var text = new List<Element>(3)
        {
            new TextEl(name) { Size = ClassicFriendsLayout.NameSize, LineHeight = ClassicFriendsLayout.NameLine, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
            new TextEl(artistName.Length > 0 ? trackTitle + "  •  " + artistName : trackTitle)
                { Size = ClassicFriendsLayout.DetailSize, LineHeight = ClassicFriendsLayout.DetailLine, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
        };
        string context = contextName.Length > 0 ? contextName : albumTitle;
        if (context.Length > 0)
            text.Add(new TextEl(context) { Size = ClassicFriendsLayout.DetailSize, LineHeight = ClassicFriendsLayout.DetailLine, Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f });

        Element picture = PersonPicture.Create("", avatar, displayName: name, imageSourcePath: Controls.ArtUrl(user.ImageId));
        if (live)
            picture = new BoxEl
            {
                Width = avatar, Height = avatar, Shrink = 0f, ZStack = true,
                Children =
                [
                    picture,
                    new BoxEl
                    {
                        Width = avatar, Height = avatar, Direction = 1, Justify = FlexJustify.End, AlignItems = FlexAlign.End, HitTestVisible = false,
                        // The ring is the opaque content rung, so the dot reads as a hole docked or floating.
                        Children = [new BoxEl { Width = ClassicFriendsLayout.PresenceDot, Height = ClassicFriendsLayout.PresenceDot, Corners = Radii.Circle(ClassicFriendsLayout.PresenceDot), Fill = Tok.AccentDefault, BorderWidth = ClassicFriendsLayout.PresenceRing, BorderColor = R.Layer.FloatingPane }],
                    },
                ],
            };

        // The avatar and the name open the friend's profile (#161): nested click owners inside the row's own context
        // click (the nearest owner wins), each its own affordance so a press on them never arms a row drag.
        Action? openProfile = props.OpenProfile(user);
        if (openProfile is not null)
        {
            picture = new BoxEl
            {
                Shrink = 0f, Role = AutomationRole.Button, Cursor = CursorId.Hand, BlocksDragArm = true,
                OnClick = openProfile, Children = [picture],
            };
            text[0] = new BoxEl
            {
                Direction = 0, MinWidth = 0f, Role = AutomationRole.Hyperlink, Focusable = true, Cursor = CursorId.Hand,
                BlocksDragArm = true, OnClick = openProfile,
                Children = [((TextEl)text[0]) with { HoverColor = Tok.AccentTextPrimary, BrushTransitionMs = Design.Motion.Faster }],
            };
        }

        int trackSlot = f.TrackSlot;
        Rail.Friends.Target menuTarget = Rail.Friends.TargetOf(contextSlot, albumSlot, artistSlot);
        Func<ContextMenuModel?> menuOf = () =>
        {
            if (trackSlot > 0 && new Track(trackSlot) is { IsValid: true } track)
                return Track.Menu(new[] { track }, new Track.MenuOptions(ShowGoToAlbum: true));
            ActionTarget target;
            switch (menuTarget)
            {
                case Rail.Friends.Target.Context: target = ActionTarget.ForPlaylist(new Playlist(contextSlot).Uri, label); break;
                case Rail.Friends.Target.Album: target = ActionTarget.ForAlbum(new Album(albumSlot).Uri, label); break;
                case Rail.Friends.Target.Artist: target = ActionTarget.ForArtist(new Artist(artistSlot).Uri, label); break;
                default: return null;
            }
            return Menus.Container(in target, null, null);
        };

        var row = new BoxEl
        {
            Key = "fr:" + user.Uri.Text,
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, MinHeight = Design.Size.TrackRowH,
            Padding = new Edges4(Spacing.S, Spacing.XS, Spacing.S, Spacing.XS), Corners = Radii.ControlAll,
            Fill = ColorF.Transparent,                                     // no zebra on a short list
            HoverFill = nav ? R.Layer.RowHover : ColorF.Transparent,
            PressedFill = nav ? R.Layer.RowPressed : ColorF.Transparent,
            PressScale = R.Motion.ScaleSubtle.PressIf(nav),
            Role = nav ? AutomationRole.Button : AutomationRole.None, Cursor = nav ? CursorId.Hand : CursorId.Arrow,
            Focusable = nav, FocusVisualMargin = Design.FocusInsetRow, OnClick = onClick,
            Enter = new EnterExit(Dy: ClassicFriendsLayout.EnterDy, Opacity: 0f, Active: true),
            Exit = new EnterExit(Dy: -ClassicFriendsLayout.ExitDy, Opacity: 0f, Active: true),
            Layout = LayoutTransition.Slide,
            Children =
            [
                picture,
                new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Justify = FlexJustify.Center, Gap = 1f, Children = text.ToArray() },
                new BoxEl
                {
                    Width = ClassicFriendsLayout.TrailW, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Children = [live ? Controls.Equalizer(true, Tok.AccentDefault, ClassicFriendsLayout.EqualizerH) : new TextEl(RelTime(now - f.TimestampMs)) { Size = ClassicFriendsLayout.DetailSize, LineHeight = ClassicFriendsLayout.DetailLine, Weight = 600, Color = Tok.TextTertiary, MaxLines = 1 }],
                },
            ],
        };
        return Controls.IsNullOverlay(overlay) ? row : row.WithContextMenu(overlay, menuOf);
    }

    static string RelTime(long ageMs) => Rail.Friends.Age(ageMs, out long n) switch
    {
        Rail.Friends.AgeUnit.Minutes => Strings.Friends.MinAgo(n),
        Rail.Friends.AgeUnit.Hours => Strings.Friends.HrAgo(n),
        Rail.Friends.AgeUnit.Days => Strings.Friends.DAgo(n),
        _ => Loc.Get(Strings.Friends.Now),
    };

    internal static Element SkeletonRow() => new BoxEl
    {
        Direction = 0, MinHeight = Design.Size.TrackRowH, AlignItems = FlexAlign.Center, Gap = Spacing.M,
        Padding = new Edges4(Spacing.S, Spacing.XS, Spacing.S, Spacing.XS),
        Children =
        [
            new BoxEl { Width = ClassicFriendsLayout.Avatar, Height = ClassicFriendsLayout.Avatar, Shrink = 0f, Corners = Radii.Circle(ClassicFriendsLayout.Avatar), Fill = Tok.FillSubtleSecondary },
            new BoxEl
            {
                Direction = 1, Grow = 1f, Gap = Spacing.XS,
                Children =
                [
                    new BoxEl { Width = ClassicFriendsLayout.SkeletonNameW, Height = ClassicFriendsLayout.SkeletonBarH, Corners = Radii.ControlAll, Fill = Tok.FillSubtleSecondary },
                    new BoxEl { Width = ClassicFriendsLayout.SkeletonDetailW, Height = ClassicFriendsLayout.SkeletonBarH, Corners = Radii.ControlAll, Fill = Tok.FillSubtleSecondary },
                ],
            },
        ],
    };
}

/// <summary>The friends body: the feed's face for the rows and state it reads.</summary>
internal sealed class ClassicFriendsBody : Component
{
    public override Element Render()
    {
        var props = UseProps<FriendsProps>();
        UseEffect(() => { props.SetActive(true); return () => props.SetActive(false); }, DepKey.Empty);
        var tick = UseSignal(0);
        UseInterval(() => tick.Value = tick.Peek() + 1, Rail.Friends.TickMs);
        var overlay = UseContext(Overlay.Service);   // the rows' right-click / Menu-key host
        _ = tick.Value;
        var rows = props.Feed.Read(out var state);

        switch (Rail.Friends.SurfaceFor(rows.Length, state))
        {
            case Rail.Friends.Surface.Rows:
                long now = props.NowMs();                                   // a DATE against a server epoch, not a motion clock
                var kids = new Element[rows.Length];
                for (int i = 0; i < rows.Length; i++) kids[i] = ClassicFriends.FriendRow(props, in rows[i], now, overlay);
                return ClassicFriends.FriendsShell(new ScrollEl
                {
                    Grow = 1f, MinHeight = 0f, AutoEdgeFade = true, ScrollKey = "friendspanel",
                    Content = new BoxEl { Direction = 1, MinHeight = 0f, Padding = new Edges4(0f, 0f, 0f, Spacing.M), Children = kids },
                });
            case Rail.Friends.Surface.Skeleton:
                var bars = new Element[ClassicFriendsLayout.SkeletonRows];
                for (int i = 0; i < bars.Length; i++) bars[i] = ClassicFriends.SkeletonRow();
                return ClassicFriends.FriendsShell(new BoxEl { Direction = 1, MinHeight = 0f, Padding = new Edges4(0f, Spacing.XS, 0f, 0f), Children = bars });
            case Rail.Friends.Surface.Offline:
                return Controls.Vacancy(Controls.VacancyVoice.Offline, Controls.VacancyScale.Compact, title: Loc.Get(Strings.Friends.Offline), subtitle: "");
            case Rail.Friends.Surface.Error:
                // A stock Standard Retry, never an accent one — and only when the host can actually retry.
                return Controls.Vacancy(Controls.VacancyVoice.Error, Controls.VacancyScale.Compact, title: Loc.Get(Strings.Friends.Error), subtitle: "",
                                        actionLabel: Loc.Get(Strings.Friends.Retry), onAction: props.Refresh());
            default:
                return Controls.Vacancy(Controls.VacancyVoice.Empty, Controls.VacancyScale.Compact,
                                        title: Loc.Get(Strings.Friends.Empty), subtitle: Loc.Get(Strings.Friends.EmptyHint));
        }
    }
}
