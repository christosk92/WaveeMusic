// ── Themes/Zune/Structure/Views/Player/ZunePlayerBar.cs — Zune's `Player.Bar` (P5 Z4a) ──────────────────────────────────────────────
//
// Boards Z-PlayBar, Z-Part-PlayBar, Z-PlayBar-Small / -Huge / -Compact and Z-Light-PlayBar: a flat, square bar (the style's player-bar
// layer) in two rows,
//
//   SEEK ROW        the 2-px seek line along the bar's top edge, full width (`Player.SeekRail`, its 12 hit box reaching 5 either side of the
//                   line); with the `seekTimes` parameter at AtSliderEnds (Panorama) the row is 16 tall with the times at its two ends. A load
//                   longer than the core's sweep delay turns the line into a sweep.
//   TRANSPORT ROW   the identity column (art · "playing on …" in the accent · title · artists · the heart), the transport centred in what is
//                   left (previous · the round accent DISC · next, or −10 · disc · +10 · the speed word for an episode; the times under it at
//                   the narrow and medium levels, beside it from full: elapsed 16 left of the first button, remaining 16 right of the last,
//                   each in a 40 slot, the disc centred in the bar),
//                   and the tools as words and glyphs: shuffle · repeat · mute · the volume line · lyrics · video ▾ · queue · devices ·
//                   full screen · "…", as the level carries them (`Tiers.Player.cs`). What a level drops is in "…" (the core's overflow).
//
// THE MINI BAR (Compact, Z-PlayBar-Compact): 64 at the foot of the reserved 72, the 2-px progress line, the identity as ONE door to now
// playing (art 44 · title · artists), then the disc and next (+10 for an episode).
//
// PER TIER (ZT2, `Tiers.Player.cs`): the mini bar at Compact; narrow (640) and medium (760) at Small; medium and full (1240) at Standard;
// wide (1920) at Huge in the 72; big (3200) at Huge 4K in an 88 slot. The slot's and the bar's heights ease through real layout on a crossing.
//
// One element tree for every level and both forms: the identity, the art, the disc and each tool keep their keys, so a level crossing FLIPs
// them (MoveMotion) and a tool that lights or dims fades in place (FaceMotion); a line that comes or goes (the "playing on" line, a time)
// enters and leaves with a fade. No key names a level or a form; the transport stands in the same "centre" column in both forms. The one swap
// is the title's tree (the desktop bar's marquee, the mini bar's trimmed run with its tip): one keyed host per tree, cross-faded in place.
//
// THE CORE HALF RUNS FIRST: `props.Host.Use(ctx.Hooks)` runs the bar's bookkeeping hooks and answers the decided `PlayerBarView`; every hook
// below runs in every form, in the same order. The bar root is the layout firewall (`IsolateLayout`). The seek rail and the time labels are
// the core hosts `props.SeekBar` / `props.TimeText` (they mount this lane's `Player.SeekRail` / `Player.TimeLabel`, `ZuneTransport.cs`).
//
// Casing: the tool words, "playing on" and the menus' words are cased by the style's roles (Zune: lowercase); titles and names keep their own.

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;

using L = Wavee.Themes.Zune.ZunePlayerLayout;
using T = Wavee.Themes.Zune.ZuneTransportLayout;

namespace Wavee.Themes.Zune;

/// <summary>Zune's play bar.</summary>
internal static partial class ZunePlayerBar
{
    /// <summary>The node keys the mounted tests find: the bar inside its slot, its seek row, the seek line's box, the transport row, the
    /// disc, the identity column and the tools.</summary>
    public const string BarKey = L.BarKey, SeekRowKey = L.SeekRowKey, SeekKey = L.SeekKey, RowKey = L.RowKey, DiscKey = L.DiscKey,
        LeftKey = L.LeftKey, RightKey = L.RightKey;

    /// <summary>The registered template of <see cref="ViewKeys.PlayerBar"/>.</summary>
    public static Element Template(PlayerBarProps props, in ViewContext ctx)
    {
        var rc = ctx.Hooks;
        var verbs = props.Verbs;

        // ── the core half: its hooks, then the decided view ──
        var v = props.Host.Use(rc);
        var m = v.Metrics;
        var facts = ctx.Tier();
        var tier = ZunePlayerTiers.BarOf(facts);
        var look = ZunePlayerTiers.LookOf(m, tier.Tier);
        bool mini = tier.Form == ZuneBarForm.Mini;
        bool ends = !mini && ZuneOptions.Times() == SeekTimes.AtSliderEnds;
        float seekRowH = ends ? ZunePlayerTiers.SeekEndsRowH : ZunePlayerTiers.SeekLineH;
        // The row's insets: the frame's gutter on the left, the last tool's glyph on the gutter on the right (ZT2's margin audit).
        var insets = ZunePlayerTiers.RowInsets(tier.Tier, look);
        bool reduced = ctx.Reduced;

        // ── hooks (positional, stable order, every form) ──
        var titleLinkHover = rc.UseSignal(false);
        var likeNode = rc.UseRef<NodeHandle>(default);
        var likePrevious = rc.UseRef((0, false));
        var metaNode = rc.UseRef<NodeHandle>(default);
        var facePrevious = rc.UseRef<EntityRef>(default);
        var videoAnchor = rc.UseRef<NodeHandle>(default);
        var videoMenu = rc.UseRef<OverlayHandle?>(null);
        var overlay = rc.UseContext(Overlay.Service);
        var input = rc.UseContext(InputHooks.Current);
        var begin = rc.UseContext(SharedTransition.Begin);
        var barNode = rc.UseRef<NodeHandle>(default);
        float artScale = rc.UseContext(Viewport.Scale);
        var marquees = rc.UseRef<Marquee.Group?>(null);   // the title and the artists: one line moves at a time, the title first
        marquees.Value ??= new Marquee.Group();

        // The heart pops on the same playable's save edge only, on the captured node (a keyed remount would reset its hover mid-toggle).
        int playingSlot = v.Face.IsNone ? 0 : v.Face.Slot;
        rc.UseLayoutEffect(() =>
        {
            var (previousSlot, previouslyLiked) = likePrevious.Value;
            likePrevious.Value = (playingSlot, v.Liked);
            if (props.LikePops(previousSlot, previouslyLiked, playingSlot, v.Liked) && v.LikeLit
                && !likeNode.Value.IsNull && rc.Anim is { } anim)
                anim.IconSwapIn(likeNode.Value);
        }, DepKey.From(playingSlot, v.Liked ? 1 : 0));

        // The face's one authored change: the text cross-fades when the identity it states swaps (never into or out of a hold).
        var faceRef = v.Face;
        rc.UseLayoutEffect(() =>
        {
            var was = facePrevious.Value;
            facePrevious.Value = faceRef;
            if (was.IsNone || faceRef.IsNone || was.Equals(faceRef)) return;
            if (metaNode.Value.IsNull || rc.Anim is not { } anim) return;
            anim.Animate(metaNode.Value, AnimChannel.Opacity, 0f, 1f, L.FaceSwap.EffectiveDurationMs(AnimChannel.Opacity), Easing.FluentDecelerate);
        }, DepKey.From((int)faceRef.Kind, faceRef.Slot));

        // ── the identity ──────────────────────────────────────────────────────────────────────────────────────────────
        var meta = MetaColumn(props, v, look, mini, titleLinkHover, metaNode, marquees.Value);
        var art = new BoxEl
        {
            Key = "art", Width = look.Art, Height = look.Art, Shrink = 0f, Animate = L.MoveMotion,
            Cursor = !mini && v.ArtNav ? CursorId.Hand : null,
            OnClick = !mini && v.ArtNav ? props.NavigateArt : null,
            Role = !mini && v.ArtNav ? AutomationRole.Hyperlink : AutomationRole.None,
            Focusable = !mini && v.ArtNav,
            HitTestVisible = !mini,
            Children = [Controls.Artwork(v.ArtUrl, look.Art, look.Art, R.Shape.For(ShapeRole.Cover), morphKey: props.ArtMorphKey, scale: artScale, keepWhileHidden: true)],
        };

        var leftKids = new List<Element>(3) { art };
        if (v.ShowTitleBlock) leftKids.Add(meta);
        if (!mini && v.ShowLikeSlot) leftKids.Add(HeartSlot(props, v, look, likeNode));
        BoxEl left = mini
            ? new BoxEl
            {
                Key = LeftKey, Grow = 1f, Shrink = 1f, MinWidth = 0f, Direction = 0, AlignItems = FlexAlign.Center, Gap = L.IdGap,
                Animate = L.MoveMotion, ClipToBounds = true,
                Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, Cursor = CursorId.Hand,
                OnClick = props.OpenNowPlaying,
                Draggable = v.Draggable ? Drag.Source(props.DragPayload) : null,
                Children = leftKids.ToArray(),
            }
            : new BoxEl
            {
                Key = LeftKey, Width = look.IdW, Shrink = 0f, MinWidth = 0f, Direction = 0, AlignItems = FlexAlign.Center, Gap = L.IdGap,
                Animate = L.MoveMotion, ClipToBounds = true,
                Draggable = v.Draggable ? Drag.Source(props.DragPayload) : null,
                Children = leftKids.ToArray(),
            };
        // Right-click / Menu key / long-press: the track menu over the now-playing target (the stage shares the seam).
        left = left.WithContextMenu(overlay, () => verbs.NowPlayingMenu());

        // ── the transport ─────────────────────────────────────────────────────────────────────────────────────────────
        var transport = new List<Element>(7);
        if (v.OwnsTransport)
        {
            if (v.Episode && !mini) transport.Add(StepWord(props, -L.SeekStepMs, look, v.SeekStepEnabled) with { Key = "seek-back-10" });
            if (!v.Episode && v.ShowPrevNext && !mini)
                transport.Add(ToolTip.Wrap(Glyph(Icons.Previous, () => verbs.Previous(), v.PrevEnabled, Tok.TextPrimary, look.Button, look.Glyph), Loc.Get(Strings.Player.Previous)) with { Key = "prev" });
            transport.Add(Disc(props, v, look) with { Key = DiscKey });
            if (!v.Episode && v.ShowPrevNext)
                transport.Add(ToolTip.Wrap(Glyph(Icons.Next, () => verbs.Next(), v.NextEnabled, Tok.TextPrimary, look.Button, look.Glyph), Loc.Get(Strings.Player.Next)) with { Key = "next" });
            if (v.Episode) transport.Add(StepWord(props, L.SeekStepMs, look, v.SeekStepEnabled) with { Key = "seek-forward-10" });
            if (v.Episode && !mini) transport.Add(Embed.Comp(new SpeedProps(props, look.WordSize, look.Button), static () => new ZuneEpisodeSpeed()) with { Key = "episode-speed" });
        }
        // The row is the disc's height (36 narrow and medium: the 40 targets overhang it by 2 each way, so the stacked cluster is 36 + 4 + 16
        // with 8 / 8 of air in the 72; 40 / 48 alone and centred from full).
        var transportRow = new BoxEl
        {
            Key = "transport", Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Gap = look.TransportGap, Shrink = 0f,
            Height = look.Disc, Animate = L.MoveMotion, Children = transport.ToArray(),
        };

        // The transport stands in the "centre" column in BOTH forms (the mini bar's hugs it at the row's end, the desktop bar's fills the room
        // between the identity and the tools), so the 640 crossing keeps the disc's and next's nodes: a crossing never re-parents them.
        var centreKids = new List<Element>(2) { transportRow };
        if (!mini && !ends && v.OwnsTransport && (v.ShowTimesElapsed || v.ShowTimesRemaining)) centreKids.Add(TimesRow(props, v, look));
        var centre = new BoxEl
        {
            // No gap: the times row under the transport carries its own (its top padding), so it eases out whole on a level change.
            Key = "centre", Grow = mini ? 0f : 1f, Shrink = mini ? 0f : 1f, MinWidth = 0f, Direction = 1, AlignItems = FlexAlign.Center,
            Justify = FlexJustify.Center, Animate = L.MoveMotion,
            // A drop on the transport is PLAY NEXT: a front-insert, never an immediate playback change.
            DropTarget = Drop.Target<DragPayload>(Drag.Resource,
                accepts: static p => p.CanCopyTracks,
                onDrop: (p, session) => verbs.DropPlayNext(p),
                caption: static _ => Loc.Get(Strings.Drag.PlayNext),
                refusalCaption: static p => Loc.Get(p.Kind == DragKind.Artist ? Strings.Drag.CantAddArtist : Strings.Drag.NothingToAdd),
                visualPolicy: DropTargetVisualPolicy.Spotlight),
            Children = centreKids.ToArray(),
        };

        Element row;
        if (mini)
        {
            row = new BoxEl
            {
                Key = RowKey, Direction = 0, Grow = 1f, AlignItems = FlexAlign.Center, Gap = look.RowGap,
                Animate = L.MoveMotion,
                Children = [left, centre],
            };
        }
        else
        {

            var tools = new List<Element>(10);
            for (int bit = 1; bit <= (int)RightSlot.More; bit <<= 1)
            {
                var slot = (RightSlot)bit;
                if ((v.Slots & slot) != 0) tools.Add(Tool(props, v, look, slot, begin, overlay, videoAnchor, videoMenu));
            }
            var right = new BoxEl
            {
                Key = RightKey, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.End, Gap = L.ToolGap,
                Animate = L.MoveMotion, Children = tools.ToArray(),
            };
            row = new BoxEl
            {
                Key = RowKey, Direction = 0, Grow = 1f, AlignItems = FlexAlign.Center, Gap = look.RowGap,
                Animate = L.MoveMotion,
                Children = [left, centre, right],
            };
        }

        // The insets are boxes either side of the row, not its padding: a crossing that changes the gutter (24 -> 48 at 1008, 48 -> 64 at
        // 3200) eases their widths through real layout and the whole row rides them (a size channel; padding would snap the row's contents).
        row = new BoxEl
        {
            Key = "row-frame", Direction = 0, Grow = 1f,
            Children = [Inset("inset-left", insets.Left, reduced), row, Inset("inset-right", insets.Right, reduced)],
        };

        // ── the seek row (a layer over the bar's top edge) ────────────────────────────────────────────────────────────
        Element line = mini
            ? new BoxEl
            {
                Key = "progress", Grow = 1f, Height = T.LineH, Direction = 0,
                Children = [props.ProgressLine is { } progress ? progress() : new BoxEl { Grow = 1f, Fill = Tok.TextPrimary with { A = T.RailA } }],
            }
            : new BoxEl
            {
                Key = SeekKey, Grow = 1f, Shrink = 1f, MinWidth = 0f, Height = seekRowH, Direction = 1, Opacity = v.TopEdgeSweeps ? 0f : 1f,
                Animate = L.FaceMotion, HitTestPassThrough = true,
                Children = v.OwnsTransport
                    ? [new BoxEl { Height = T.HitH, Shrink = 0f, Direction = 0, Margin = T.RailMargin(seekRowH), Children = [props.SeekBar()] }]
                    : [],
            };
        var seekKids = new List<Element>(3);
        if (ends && v.OwnsTransport && v.ShowTimesElapsed) seekKids.Add(new BoxEl { Key = "elapsed", Animate = L.ItemMotion, Children = [props.TimeText(false)] });
        seekKids.Add(line);
        if (ends && v.OwnsTransport && v.ShowTimesRemaining) seekKids.Add(new BoxEl { Key = "remaining", Animate = L.ItemMotion, Children = [props.TimeText(true)] });
        var seekRow = new BoxEl
        {
            Key = SeekRowKey, Height = seekRowH, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Start, HitTestPassThrough = true,
            Padding = ends ? insets : default,
            Gap = ends ? look.TransportGap : 0f, Animate = L.MoveMotion,
            Children = seekKids.ToArray(),
        };

        // The 2-px seek line lies ON the bar's top edge (the board's absolute line), so the transport row is the whole bar and its cluster is
        // centred with even air above and below (6 / 6 in the 72, 9 / 9 in the 88); the 16-tall row with the times at its ends (AtSliderEnds)
        // takes its own room instead.
        Element body = ends
            ? new BoxEl
            {
                Key = "body", Grow = 1f, Direction = 1,
                Children = [new BoxEl { Key = "seek-room", Height = seekRowH, Shrink = 0f, Animate = L.MoveMotion, HitTestVisible = false }, row],
            }
            : new BoxEl { Key = "body", Grow = 1f, Direction = 1, Children = [row] };
        var layers = new List<Element>(3)
        {
            body,
            new BoxEl { Key = "seek-layer", Grow = 1f, Direction = 1, HitTestPassThrough = true, Children = [seekRow] },
        };
        // A load past the core's sweep delay: the line becomes a sweep (the rail fades under it, keeping its scrub state).
        if (v.TopEdgeSweeps)
            layers.Add(new BoxEl
            {
                Key = "sweep", Grow = 1f, Direction = 1, HitTestVisible = false, Animate = L.ItemMotion,
                Children = [new BoxEl { Height = T.LineH, Shrink = 0f, ClipToBounds = true, Direction = 0, Children = [ProgressBar.Indeterminate(m.TopEdgeWidth)] }],
            });

        // The slot (72, 88 at Huge 4K) and the bar in it (the mini 64 at Compact) change height on a crossing: both ease through real layout (a
        // size channel), so the page above yields frame by frame and the bar's top edge rises with no position slide (ZT2; the engine's
        // reflow-shove rule snaps a position slide while the title bar reflows on the same crossing). The slot is NOT the view's root: a component's
        // root lands its size at once on a crossing (its anchor is laid out at the final size), so the frame's column snapped by 16 at 3200 while the
        // bar eased inside; under a plain root the slot's reflow drives the column frame by frame.
        return new BoxEl
        {
            Direction = 1, Shrink = 0f,
            Children = [Slot(tier, reduced, layers, barNode, input, props)],
        };
    }

    static BoxEl Slot(ZunePlayerBarTier tier, bool reduced, List<Element> layers, Ref<NodeHandle> barNode, InputHooks input, PlayerBarProps props)
        => new()
        {
            Direction = 1, Justify = FlexJustify.End, Height = tier.SlotH, Shrink = 0f, IsolateLayout = true,
            Animate = reduced ? null : ZunePlayerTiers.SlotMotion,
            Focusable = true, FocusVisualMargin = Edges4.All(Spacing.XXS),
            OnRealized = node => barNode.Value = node,
            OnClick = () => input.FocusNode?.Invoke(barNode.Value, true),
            OnKeyDown = e => props.OnKey(e, input.GetFocus?.Invoke() == barNode.Value),
            Children =
            [
                new BoxEl
                {
                    Key = BarKey, Height = tier.BarH, Shrink = 0f, ZStack = true, Fill = R.Layer.PlayerBar,
                    Animate = reduced ? null : ZunePlayerTiers.BarMotion,
                    Children = layers.ToArray(),
                },
            ],
        };

    /// <summary>One of the row's insets: a fixed width that eases through real layout when the tier changes it.</summary>
    static Element Inset(string key, float width, bool reduced) => new BoxEl
    {
        Key = key, Width = width, Shrink = 0f, HitTestVisible = false, Animate = reduced ? null : ZunePlayerTiers.InsetMotion,
    };

    /// <summary>"playing on …" (the accent, above the title), the title (a link to the album or the show) and the artists.</summary>
    /// <summary>The title's host per tree (the marquee, the trimmed run): the swap's family.</summary>
    const string TitleHostKey = "np-title-host:";

    static BoxEl MetaColumn(PlayerBarProps props, PlayerBarView v, ZuneBarLook look, bool mini, Signal<bool> titleLinkHover, Ref<NodeHandle> metaNode,
                           Marquee.Group marquees)
    {
        const string titleKey = "np-title", artistsKey = "np-artists";
        Prop<ColorF> titleInk = Prop.Of(() => titleLinkHover.Value && props.TitleHasRoute() ? Tok.AccentTextPrimary : InkOf(props.TitleInk()));
        var titleRun = new TextEl(Prop.Of<string>(props.TitleText))
        {
            Size = look.TitleSize, LineHeight = look.TitleLine, Weight = L.TitleWeight, Color = titleInk,
            Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
        };
        bool marquee = v.Marquee && !mini;
        long face = ((long)v.Face.Kind << 32) | (uint)v.Face.Slot;   // the stated playable: a new one restarts the lines' passes
        BoxEl title = marquee
            ? Marquee.Of(Prop.Of<string>(props.TitleText), new Marquee.Style
            {
                FontSize = look.TitleSize, Weight = L.TitleWeight, Foreground = titleInk, Speed = L.MarqueeSpeed, FadeBand = L.MarqueeFade,
            }, marquees, order: 0, identity: face)
            : new BoxEl { ClipToBounds = true, MinWidth = 0f, Children = [titleRun] };
        title = title with { Key = titleKey };
        if (v.TitleNav && !mini)
            title = title with
            {
                Cursor = CursorId.Hand, OnClick = props.NavigateTitle, Role = AutomationRole.Hyperlink, Focusable = true,
                OnHoverMove = _ => { if (!titleLinkHover.Peek()) titleLinkHover.Value = true; },
                OnPointerExit = () => { if (titleLinkHover.Peek()) titleLinkHover.Value = false; },
            };

        var kids = new List<Element>(3);
        if (v.RemoteLine)
            kids.Add(new BoxEl
            {
                Key = "remote-device-line", Animate = L.ItemMotion, MinWidth = 0f,
                Children = [Embed.Comp(new RemoteProps(props, look.FromSize, look.FromLine), static () => new ZuneRemoteLine())],
            });
        // The marquee (the desktop bar's, by the core's preference) and the trimmed title with its tip (the mini bar's) are two different trees: a
        // crossing between them cross-fades one keyed host per tree in place, the shared swap fade (never a bare remount of the title).
        kids.Add(new BoxEl
        {
            Key = marquee ? TitleHostKey + "marquee" : TitleHostKey + "trim", Direction = 1, MinWidth = 0f,
            Enter = ZuneMotion.SwapFade, Exit = ZuneMotion.SwapFade,
            Children = [marquee ? title : Controls.TrimTip(title, props.TitleText(), titleRun) with { Key = titleKey }],
        });
        if (v.ShowArtistLine)
        {
            var credits = new CreditsProps(props, look.SubSize, look.SubLine, Linked: !mini);
            kids.Add(marquee
                ? Marquee.Content(Embed.Comp(credits, static () => new ZuneCredits()), Prop.Of(() => props.Credits().Line(L.CreditSeparator)),
                    new Marquee.Style { FontSize = look.SubSize, Speed = L.MarqueeSpeed, FadeBand = L.MarqueeFade }, marquees, order: 1, identity: face)
                    with { Key = artistsKey }
                : new BoxEl { Key = artistsKey, ClipToBounds = true, MinWidth = 0f, Children = [Embed.Comp(credits, static () => new ZuneCredits())] });
        }

        return new BoxEl
        {
            Key = "meta", Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Shrink = 1f, Justify = FlexJustify.Center, ClipToBounds = true,
            Animate = L.MoveMotion, OnRealized = h => metaNode.Value = h,
            Children = kids.ToArray(),
        };
    }

    static ColorF InkOf(NowPlayingInk ink) => ink switch
    {
        NowPlayingInk.Secondary => Tok.TextSecondary,
        NowPlayingInk.Critical => Tok.SystemFillCritical,
        _ => Tok.TextPrimary,
    };

    /// <summary>The times under the transport (narrow, medium): elapsed, " · " and the remaining time, as the level carries them (each enters
    /// and leaves with a fade, so the row never jumps). The row carries the gap above it as its top padding, and enters and leaves with a fade
    /// and an eased height (a level change to full moves the times beside the transport, `SideTime`).</summary>
    static Element TimesRow(PlayerBarProps props, PlayerBarView v, ZuneBarLook look)
    {
        var kids = new List<Element>(3);
        if (v.ShowTimesElapsed) kids.Add(new BoxEl { Key = "elapsed", Animate = L.ItemMotion, Children = [props.TimeText(false)] });
        if (v.ShowTimesElapsed && v.ShowTimesRemaining)
            kids.Add(new BoxEl
            {
                Key = "times-sep", Animate = L.ItemMotion,
                Children = [new TextEl(" · ") { Size = T.TimeSize, LineHeight = T.TimeLine, Color = Tok.TextSecondary, Wrap = TextWrap.NoWrap }],
            });
        if (v.ShowTimesRemaining) kids.Add(new BoxEl { Key = "remaining", Animate = L.ItemMotion, Children = [props.TimeText(true)] });
        return new BoxEl
        {
            Key = "times", Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Height = T.TimeLine + look.TimesGap, Shrink = 0f,
            Padding = T.TimesRowPadding(look.TimesGap), Animate = L.ItemMotion, Children = kids.ToArray(),
        };
    }
}
