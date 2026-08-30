using System;
using System.Collections.Generic;
using System.Threading;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// The right-rail FRIENDS panel — the Spotify friend-activity feed (what friends are listening to), modelled on the same
// keyed-rows-in-a-ScrollEl shape as QueuePanel so the reconciler's Enter/Exit/FLIP animates every push-driven upsert
// into place. Rows are keyed by user uri; a friend who starts playing flips their row live (accent presence dot +
// equalizer) the frame the dealer push lands. No virtualization (a friends feed is a few dozen rows), no context menu
// in this cut. Reads FriendsBridge for the reactive snapshot; a mount effect marks the feed visible + drives a 30-second
// tick so relative times / the live window advance without a push.
sealed class FriendsPanel : Component
{
    const float Avatar = 40f;
    const long LiveWindowMs = 120_000;   // "listening now" — age ≤ 2 min shows the equalizer + presence dot
    const int TickMs = 30_000;           // relative-time / live-window refresh cadence while the panel is visible

    public override Element Render()
    {
        var fb = UseContext(FriendsBridge.Slot);
        var go = UseContext(HistoryStore.NavCtx);

        // Mount == visible (RightRail only embeds this in Friends mode): mark the feed active (starts the watchdog +
        // lazy-seed); cleanup on unmount stops it.
        UseSignalEffect(() =>
        {
            if (fb is null) return;
            fb.SetActive(true);
            Reactive.OnCleanup(() => fb.SetActive(false));
        });
        // Relative-time / live-window refresh: a 30-s frame-clock interval that AUTO-PAUSES while parked/minimized (idle
        // quiesce) — replaces the old System.Threading.Timer + post marshal. Bumps NowTick so the times + live window recompute.
        UseInterval(() => { if (fb is not null) fb.NowTick.Value = fb.NowTick.Peek() + 1; }, TickMs, enabled: fb is not null);

        if (fb is null) return new BoxEl();

        var items = fb.Items.Value;      // subscribe → re-render on any push/seed
        var state = fb.State.Value;      // subscribe → swap the surface (skeleton / rows / empty / offline / error)
        _ = fb.NowTick.Value;            // subscribe → relative times + the live window advance on the 30-s tick
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var accent = Tok.AccentDefault;

        // Rows win whenever we have them (keep stale rows visible during a refresh / transient error).
        if (items.Count > 0) return RowsView(items, now, accent, go);

        return state switch
        {
            FriendFeedState.Offline => Message(Loc.Get(Strings.Friends.Offline)),
            // A stock Standard button, not the old hand-rolled pill: a retry is a utility action on a flat panel,
            // which is row 1 of the button grammar - see WaveeCta's geometry table.
            FriendFeedState.Error => Message(Loc.Get(Strings.Friends.Error),
                actionLabel: Loc.Get(Strings.Friends.Retry), onAction: fb.Refresh),
            FriendFeedState.Loading or FriendFeedState.Idle => SkeletonView(),
            _ => Message(Loc.Get(Strings.Friends.Empty), Loc.Get(Strings.Friends.EmptyHint)),
        };
    }

    // ── the scrolling list of keyed friend rows ─────────────────────────────────────────────────────────────────────
    static Element RowsView(IReadOnlyList<FriendActivity> items, long now, ColorF accent, Action<string, string?>? go)
    {
        var rows = new List<Element>(items.Count);
        for (int i = 0; i < items.Count; i++) rows.Add(Row(items[i], now, accent, go));
        return Shell(new ScrollEl
        {
            Grow = 1f, MinHeight = 0f, AutoEdgeFade = true, ScrollKey = "friendspanel",
            Content = new BoxEl
            {
                Direction = 1, MinHeight = 0f, Padding = new Edges4(0f, 0f, 0f, Spacing.M),
                Children = rows.ToArray(),
            },
        });
    }

    static Element Row(FriendActivity fa, long now, ColorF accent, Action<string, string?>? go)
    {
        bool live = fa.TimestampMs > 0 && now - fa.TimestampMs <= LiveWindowMs;

        string? route = RichText.RouteForUri(fa.ContextUri) ?? RichText.RouteForUri(fa.AlbumUri) ?? RichText.RouteForUri(fa.ArtistUri);
        string? routeName = fa.ContextName ?? fa.AlbumName ?? fa.ArtistName;
        Action? onClick = route is not null && go is not null ? () => go(route, routeName) : null;

        // "Track • Artist"
        string trackLine = fa.ArtistName is { Length: > 0 } an ? fa.TrackName + "  •  " + an : fa.TrackName;
        string? context = fa.ContextName is { Length: > 0 } cn ? cn : fa.AlbumName is { Length: > 0 } al ? al : null;

        var textKids = new List<Element>(3)
        {
            new TextEl(fa.UserName) { Size = 14f, LineHeight = 20f, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
            new TextEl(trackLine) { Size = 12f, LineHeight = 16f, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
        };
        if (context is not null)
            textKids.Add(new TextEl(context) { Size = 12f, LineHeight = 16f, Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f });

        return new BoxEl
        {
            Key = "fr:" + fa.UserUri,
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, MinHeight = WaveeSize.TrackRowH,
            Padding = new Edges4(Spacing.S, Spacing.XS, Spacing.S, Spacing.XS),
            Corners = Radii.ControlAll,
            // NO ZEBRA: a friends rail is a short list in a 340-DIP panel, so the stripe was texture rather than a
            // scanning aid. Plain rows + the standard hover, which is also the only state a non-clickable row has.
            Fill = ColorF.Transparent,
            HoverFill = onClick is not null ? WaveeColors.RowHover : ColorF.Transparent,
            PressedFill = onClick is not null ? WaveeColors.RowPressed : ColorF.Transparent,
            PressScale = WaveeMotion.ScaleSubtle.PressIf(onClick is not null),
            Role = onClick is not null ? AutomationRole.Button : AutomationRole.None,
            Cursor = onClick is not null ? CursorId.Hand : CursorId.Arrow,
            Focusable = onClick is not null,
            OnClick = onClick,
            Enter = new EnterExit(Dy: 6f, Opacity: 0f, Active: true),
            Exit = new EnterExit(Dy: -4f, Opacity: 0f, Active: true),
            Layout = LayoutTransition.Slide,
            Children =
            [
                AvatarWithPresence(fa, live, accent),
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Justify = FlexJustify.Center, Gap = 1f,
                    Children = textKids.ToArray(),
                },
                new BoxEl
                {
                    Width = 44f, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Children =
                    [
                        live
                            ? WaveeEqualizer.Of(true, static () => Tok.AccentDefault, 14f)
                            : new TextEl(RelTime(now - fa.TimestampMs)) { Size = 12f, LineHeight = 16f, Weight = 600, Color = Tok.TextTertiary, MaxLines = 1 },
                    ],
                },
            ],
        };
    }

    // 40px PersonPicture with a small accent presence dot overlaid bottom-right when the friend is listening now.
    static Element AvatarWithPresence(FriendActivity fa, bool live, ColorF accent)
    {
        var pic = PersonPicture.Create("", Avatar, displayName: fa.UserName, imageSourcePath: fa.UserImageUrl);
        if (!live) return pic;
        return new BoxEl
        {
            Width = Avatar, Height = Avatar, Shrink = 0f, ZStack = true,
            Children =
            [
                pic,
                new BoxEl
                {
                    Width = Avatar, Height = Avatar, Direction = 1, Justify = FlexJustify.End, AlignItems = FlexAlign.End, HitTestVisible = false,
                    Children =
                    [
                        new BoxEl
                        {
                            Width = 12f, Height = 12f, Corners = Radii.Circle(12f),
                            // The cut-out ring must match what the badge actually sits ON: FloatingPane IS the opaque
                            // ContentSurface, the one rung the rail paints whether it is docked or floating, so the
                            // ring reads as a hole either way.
                            Fill = accent, BorderWidth = 2f, BorderColor = WaveeColors.FloatingPane,
                        },
                    ],
                },
            ],
        };
    }

    static string RelTime(long ageMs)
    {
        if (ageMs < 0) ageMs = 0;
        long min = ageMs / 60_000;
        if (min < 1) return Loc.Get(Strings.Friends.Now);
        if (min < 60) return Strings.Friends.MinAgo(min);
        long hr = min / 60;
        if (hr < 24) return Strings.Friends.HrAgo(hr);
        return Strings.Friends.DAgo(hr / 24);
    }

    // ── empty / offline / error surfaces ────────────────────────────────────────────────────────────────────────────
    static Element SkeletonView()
    {
        var kids = new Element[5];
        for (int i = 0; i < kids.Length; i++) kids[i] = SkeletonRow();
        return Shell(new BoxEl
        {
            Direction = 1, MinHeight = 0f, Padding = new Edges4(0f, Spacing.XS, 0f, 0f),
            Children = kids,
        });
    }

    static Element SkeletonRow() => new BoxEl
    {
        Direction = 0, MinHeight = WaveeSize.TrackRowH, AlignItems = FlexAlign.Center, Gap = Spacing.M, Padding = new Edges4(Spacing.S, Spacing.XS, Spacing.S, Spacing.XS),
        Children =
        [
            new BoxEl { Width = Avatar, Height = Avatar, Shrink = 0f, Corners = CornerRadius4.All(Avatar / 2f), Fill = Tok.FillSubtleSecondary },
            new BoxEl
            {
                Direction = 1, Grow = 1f, Gap = Spacing.XS,
                Children =
                [
                    new BoxEl { Width = 120f, Height = 12f, Corners = CornerRadius4.All(Radii.Control), Fill = Tok.FillSubtleSecondary },
                    new BoxEl { Width = 160f, Height = 12f, Corners = CornerRadius4.All(Radii.Control), Fill = Tok.FillSubtleSecondary },
                ],
            },
        ],
    };

    // The shared empty-state grammar at RAIL scale (this panel is ~340 DIP). Was a hand-rolled stack with its own
    // 14/600-secondary heading, its own 12/16 hint and a hand-rolled pill for the action - three parts, three
    // divergences from the component that already owns all three.
    static Element Message(string title, string? hint = null, string? actionLabel = null, Action? onAction = null)
        => EmptyState.Compact(title, hint, actionLabel, onAction);

    // The panel frame: a full-height clipped column with the shared rail padding (matches QueuePanel).
    static Element Shell(Element content) => new BoxEl
    {
        Direction = 1, Grow = 1f, MinHeight = 0f, ClipToBounds = true,
        Padding = new Edges4(Spacing.M, Spacing.XS, Spacing.M, 0f),
        Children = [content],
    };
}
