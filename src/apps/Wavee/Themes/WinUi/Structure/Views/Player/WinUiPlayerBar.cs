// ── Themes/WinUi/Structure/Views/Player/WinUiPlayerBar.cs — WinUI's `Player.Bar` (P6 W4a) ─────────────────────────────────────────────
//
// Boards Shell-Player, W-PlayerLight and the player table of W-Tiers. On the window's Mica (the style's player-bar layer) under a 1-px divider:
//
//   THE BAR (Compact 641 up, 72 tall): three columns in a 16 inset,
//     identity   the 48 cover (a link to the context; a film badge for a video) · the title (Body Strong, a link to the album or show) over the
//                artists (Caption, secondary) and, from Medium, "Playing on <device>" for a remote output · the 40 heart
//     centre    the level's seek column (300 / 420 / 520 / 720), the transport CENTRED over the seek row:
//                  transport 36   shuffle · previous · the 36 accent play circle · next · repeat (an episode: previous · -10 · play · +10 · next)
//                  seek 24        elapsed · the rail · remaining (40 slots either side)
//     tools      flush right, 4 apart: the episode speed · lyrics · queue · devices ("This PC" with its name at Huge) · mute · the volume slider
//                (96, 128 at Huge) · the video split · full screen · ⋯. A pane on show plates its button (the subtle fill).
//   At Compact the columns are `1fr 300 auto` 16 apart; from Medium the centre is centred (`1fr | column | 1fr`, the tools keeping their room).
//
//   THE MINI BAR (Compact window, 64 tall): the 2-px accent progress edge along the top, then ONE door to the now-playing sheet (the 40 cover
//   and the two lines; a remote output says "Playing on <device>" in the accent instead of the artists) · the heart · play · next (+10 for an
//   episode) · ⋯, which lists what the bar no longer shows (previous, shuffle, repeat, lyrics, queue, devices, mute, full screen).
//
// THE TIERS (`Rules\Tiers.Player.cs`). The level is the core's (the window width through the policy's levels, clamped to the window tier); the
// look is found from it. One element tree for every level: each control keeps its key, so a crossing fades the ones that come or go and FLIPs
// the rest over 167 ms; the bar's height changes only on the 641 crossing (64 ↔ 72), easing through real layout so the page above follows.
//
// THE CORE HALF RUNS FIRST: `props.Host.Use(ctx.Hooks)` runs the bar's bookkeeping hooks and answers the decided `PlayerBarView`; every hook below
// runs in every form, in the same order. The bar root is the layout firewall (`IsolateLayout`). The seek rail and the time labels are the core
// hosts `props.SeekBar` / `props.TimeText` (they mount `Player.SeekRail` / `Player.TimeLabel`, `WinUiTransport.cs`).

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

using PL = Wavee.Themes.WinUi.WinUiPlayerLayout;

namespace Wavee.Themes.WinUi;

/// <summary>WinUI's play bar.</summary>
internal static partial class WinUiPlayerBar
{
    /// <summary>The registered template of <see cref="ViewKeys.PlayerBar"/>.</summary>
    public static Element Template(PlayerBarProps props, in ViewContext ctx)
    {
        var rc = ctx.Hooks;
        var verbs = props.Verbs;

        // ── the core half: its hooks, then the decided view ──
        var v = props.Host.Use(rc);
        var m = v.Metrics;
        var facts = ctx.Tier();
        var window = facts.Policy is null ? WidthTier.Standard : facts.Window;
        var look = WinUiPlayerTiers.LookOf(m, window);
        bool mini = look.Mini;
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

        // The heart pops on the same playable's save edge only, on the captured node.
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
            anim.Animate(metaNode.Value, AnimChannel.Opacity, 0f, 1f, PL.FaceSwap.EffectiveDurationMs(AnimChannel.Opacity), Easing.FluentDecelerate);
        }, DepKey.From((int)faceRef.Kind, faceRef.Slot));

        // ── the identity ──────────────────────────────────────────────────────────────────────────────────────────────
        var meta = MetaColumn(props, v, mini, titleLinkHover, metaNode, marquees.Value);
        var art = new BoxEl
        {
            Key = "art", Width = look.Art, Height = look.Art, Shrink = 0f, ZStack = true, Animate = PL.MoveMotion,
            Cursor = !mini && v.ArtNav ? CursorId.Hand : null,
            OnClick = !mini && v.ArtNav ? props.NavigateArt : null,
            Role = !mini && v.ArtNav ? AutomationRole.Hyperlink : AutomationRole.None,
            Focusable = !mini && v.ArtNav,
            HitTestVisible = !mini,
            Children = v.HasVideo
                ? [Controls.Artwork(v.ArtUrl, look.Art, look.Art, PL.ArtCorner, morphKey: props.ArtMorphKey, scale: artScale, keepWhileHidden: true), FilmBadge()]
                : [Controls.Artwork(v.ArtUrl, look.Art, look.Art, PL.ArtCorner, morphKey: props.ArtMorphKey, scale: artScale, keepWhileHidden: true)],
        };

        Element heart = HeartSlot(props, v, likeNode);

        // The bar's slot: its height (the Mini's 64 at Compact, 72 elsewhere; it eases through real layout on the 641 crossing, anchored at the
        // foot), the layout firewall, the bar's focus and key map, the Mica layer and the 1-px divider on top.
        Element Bar(Element body) => new BoxEl
        {
            // The slot changes height on the 641 crossing (64 <-> 72) through real layout (a size channel, anchored at the foot): the page above
            // yields frame by frame and the bar, fixed to the foot, is revealed from the bottom up (as Zune's slot, ZT2).
            Direction = 1, Justify = FlexJustify.End, Height = WinUiPlayerTiers.HeightOf(window), Shrink = 0f, IsolateLayout = true,
            ClipToBounds = true, Animate = reduced ? null : WinUiPlayerTiers.SlotMotion,
            Children =
            [
                new BoxEl
                {
                    Key = PL.BarKey, Direction = 1, ZStack = true, Height = WinUiPlayerTiers.HeightOf(window), Shrink = 0f, Fill = R.Layer.PlayerBar,
                    Focusable = true, FocusVisualMargin = Edges4.All(Spacing.XXS),
                    OnRealized = node => barNode.Value = node,
                    // A pointer click takes the bar's key map without drawing its focus ring round the whole bar (the board's bar has only
                    // its top divider); Tab still lands on it with the keyboard focus visual.
                    OnClick = () => input.FocusNode?.Invoke(barNode.Value, false),
                    OnKeyDown = e => props.OnKey(e, input.GetFocus?.Invoke() == barNode.Value),
                    Children =
                    [
                        new BoxEl { Key = "body", Grow = 1f, Direction = 0, MinWidth = 0f, Children = [body] },
                        new BoxEl
                        {
                            Key = "divider", Grow = 1f, Direction = 1, HitTestVisible = false,
                            Children = [new BoxEl { Height = PL.DividerH, Shrink = 0f, Fill = Tok.StrokeDividerDefault }],
                        },
                    ],
                },
            ],
        };

        if (mini) return Bar(MiniRow(props, v, art, meta, heart, overlay));

        var leftKids = new List<Element>(3) { art };
        if (v.ShowTitleBlock) leftKids.Add(meta);
        if (v.ShowLikeSlot) leftKids.Add(heart);
        BoxEl left = new BoxEl
        {
            Key = PL.LeftKey, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, Direction = 0, AlignItems = FlexAlign.Center, Gap = PL.IdGap,
            Animate = PL.MoveMotion, ClipToBounds = true,
            Draggable = v.Draggable ? Drag.Source(props.DragPayload) : null,
            Children = leftKids.ToArray(),
        };
        // Right-click / Menu key / long-press: the track menu over the now-playing target (the stage shares the seam).
        left = left.WithContextMenu(overlay, () => verbs.NowPlayingMenu());

        // ── the centre: the transport over the seek row ──────────────────────────────────────────────────────────────
        var transport = new List<Element>(7);
        if (v.OwnsTransport)
        {
            if (!v.Episode && (v.Slots & RightSlot.Shuffle) != 0)
                transport.Add(Lit(PL.ShuffleKey, (v.Lit & RightSlot.Shuffle) != 0, ToolTip.Wrap(
                    Button(Icons.Shuffle, () => verbs.ToggleShuffle(), v.CanTransport, v.Shuffle, PL.TransportW, PL.TransportH, PL.Glyph),
                    Loc.Get(Strings.Player.Shuffle))));
            if (v.ShowPrevNext)
                transport.Add(Item(PL.PrevKey, ToolTip.Wrap(
                    Button(Icons.Previous, () => verbs.Previous(), v.PrevEnabled, false, PL.TransportW, PL.TransportH, PL.Glyph), Loc.Get(Strings.Player.Previous))));
            if (v.Episode) transport.Add(Item("seek-back-10", StepButton(props, -PL.SeekStepMs, v.SeekStepEnabled, PL.TransportH)));
            transport.Add(PlayCircle(props, v) with { Key = PL.PlayKey });
            if (v.Episode) transport.Add(Item("seek-forward-10", StepButton(props, PL.SeekStepMs, v.SeekStepEnabled, PL.TransportH)));
            if (v.ShowPrevNext)
                transport.Add(Item(PL.NextKey, ToolTip.Wrap(
                    Button(Icons.Next, () => verbs.Next(), v.NextEnabled, false, PL.TransportW, PL.TransportH, PL.Glyph), Loc.Get(Strings.Player.Next))));
            if (!v.Episode && (v.Slots & RightSlot.Repeat) != 0)
                transport.Add(Lit(PL.RepeatKey, (v.Lit & RightSlot.Repeat) != 0, ToolTip.Wrap(
                    Button(v.RepeatGlyph, () => verbs.CycleRepeat(), v.CanTransport, v.RepeatOn, PL.TransportW, PL.TransportH, PL.Glyph),
                    Loc.Get(Strings.Player.Repeat))));
        }
        var seekKids = new List<Element>(3);
        if (v.OwnsTransport)
        {
            if (v.ShowTimesElapsed) seekKids.Add(new BoxEl { Key = "elapsed", Shrink = 0f, Animate = PL.ItemMotion, Children = [props.TimeText(false)] });
            seekKids.Add(new BoxEl
            {
                Key = "seek", Direction = 0, Grow = 1f, Shrink = 1f, MinWidth = 0f, Animate = PL.MoveMotion,
                Opacity = v.TopEdgeSweeps ? 0f : 1f, Children = [props.SeekBar()],
            });
            if (v.ShowTimesRemaining) seekKids.Add(new BoxEl { Key = "remaining", Shrink = 0f, Animate = PL.ItemMotion, Children = [props.TimeText(true)] });
            if (v.TopEdgeSweeps) seekKids.Add(new BoxEl { Key = "sweep", Grow = 1f, HitTestVisible = false });
        }
        Element seekRow = v.TopEdgeSweeps
            ? new BoxEl
            {
                // A load past the core's sweep delay: the rail fades and the indeterminate sweep runs over its row (keeping the scrub state).
                Key = PL.SeekRowKey, Height = PL.SeekRowH, Shrink = 0f, ZStack = true, Animate = PL.MoveMotion,
                Children =
                [
                    new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = PL.SeekGap, Children = seekKids.ToArray() },
                    new BoxEl
                    {
                        Direction = 0, AlignItems = FlexAlign.Center, HitTestVisible = false,
                        Children = [new BoxEl { Grow = 1f, ClipToBounds = true, Direction = 0, Children = [ProgressBar.Indeterminate(look.SeekW)] }],
                    },
                ],
            }
            : new BoxEl
            {
                Key = PL.SeekRowKey, Height = PL.SeekRowH, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center, Gap = PL.SeekGap,
                Animate = PL.MoveMotion, Children = seekKids.ToArray(),
            };
        var centre = new BoxEl
        {
            Key = PL.CentreKey, Width = look.SeekW, Shrink = 0f, Direction = 1, Justify = FlexJustify.Center, Animate = PL.MoveMotion,
            // A drop on the transport is PLAY NEXT: a front-insert, never an immediate playback change.
            DropTarget = Drop.Target<DragPayload>(Drag.Resource,
                accepts: static p => p.CanCopyTracks,
                onDrop: (p, session) => verbs.DropPlayNext(p),
                caption: static _ => Loc.Get(Strings.Drag.PlayNext),
                refusalCaption: static p => Loc.Get(p.Kind == DragKind.Artist ? Strings.Drag.CantAddArtist : Strings.Drag.NothingToAdd),
                visualPolicy: DropTargetVisualPolicy.Spotlight),
            Children =
            [
                new BoxEl
                {
                    Key = PL.TransportKey, Height = PL.TransportRowH, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Gap = PL.TransportGap, Animate = PL.MoveMotion, Children = transport.ToArray(),
                },
                seekRow,
            ],
        };

        // ── the tools ─────────────────────────────────────────────────────────────────────────────────────────────────
        var tools = new List<Element>(10);
        float toolsW = 0f;
        void AddTool(Element tool, float width)
        {
            if (tools.Count > 0) toolsW += PL.ToolGap;
            tools.Add(tool);
            toolsW += width;
        }
        if (v.Episode && look.Speed && v.OwnsTransport)
            AddTool(Item(PL.SpeedKey, Embed.Comp(props, static () => new WinUiEpisodeSpeed())), PL.SpeedW);
        foreach (var slot in s_toolOrder)
        {
            if ((v.Slots & slot) == 0) continue;
            switch (slot)
            {
                case RightSlot.Devices:
                    AddTool(Lit(PL.DevicesKey, (v.Lit & slot) != 0, Embed.Comp(new DevicesProps(props, look.DeviceLabel), static () => new WinUiDevicesButton())
                        with { Key = look.DeviceLabel ? "devices-label" : "devices-icon" }), look.DeviceLabel ? WinUiDevicesButton.LabelledW : PL.ToolBox);
                    break;
                case RightSlot.VolumeSlider:
                    AddTool(Lit(PL.VolumeSliderKey, (v.Lit & slot) != 0, new BoxEl
                    {
                        Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center, Padding = new Edges4(PL.VolumeLead, 0f, 0f, 0f),
                        Children = [Slider.Create(props.Volume, props.SetVolume, new Slider.SliderOptions { ThumbToolTipValueConverter = props.VolumeText },
                            length: look.VolumeW, thickness: PL.VolumeThickness)],
                    }), look.VolumeW + PL.VolumeLead);
                    break;
                case RightSlot.Video:
                    AddTool(Lit(PL.VideoKey, (v.Lit & slot) != 0, VideoSplit(props, v, (v.Lit & slot) != 0, overlay, videoAnchor, videoMenu)),
                        PL.SplitMainW + 1f + PL.SplitChevronW + 2f);
                    break;
                case RightSlot.More:
                    AddTool(Lit(PL.MoreKey, (v.Lit & slot) != 0, MoreMenu(props, v, mini: false)), PL.ToolBox);
                    break;
                default:
                    AddTool(Tool(props, v, slot, begin), PL.ToolBox);
                    break;
            }
        }
        var right = new BoxEl
        {
            Key = PL.RightKey, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.End, Gap = PL.ToolGap,
            Animate = PL.MoveMotion, Children = tools.ToArray(),
        };
        // From Medium the identity and the tools share the room either side of the centred column, the tools keeping theirs (the identity
        // shrinks first); at Compact the tools are `auto` and the identity takes the rest.
        Element rightColumn = look.Level == WinUiBarLevel.Compact
            ? right
            : new BoxEl
            {
                Key = "right-column", Grow = 1f, Shrink = 0f, Basis = toolsW, MinWidth = toolsW, Direction = 0, AlignItems = FlexAlign.Center,
                Justify = FlexJustify.End, Animate = PL.MoveMotion, Children = [right],
            };

        // The two flexible columns start from the tools' width (the identity shrinks first, below it) so the free room splits evenly and the
        // centre stays centred in the window.
        if (look.Level != WinUiBarLevel.Compact) left = left with { Basis = toolsW };
        var row = new BoxEl
        {
            Key = PL.RowKey, Direction = 0, Grow = 1f, Shrink = 1f, MinWidth = 0f, AlignItems = FlexAlign.Center, Gap = look.ColumnGap, Padding = PL.RowPadding,
            Children = [left, centre, rightColumn],
        };
        return Bar(row);
    }

    /// <summary>The tools in the board's order (the core's slot bits are the presence; shuffle and repeat ride the transport).</summary>
    private static readonly RightSlot[] s_toolOrder =
    [
        RightSlot.Lyrics, RightSlot.Queue, RightSlot.Devices, RightSlot.Volume, RightSlot.VolumeSlider, RightSlot.Video, RightSlot.Expand, RightSlot.More,
    ];

    /// <summary>The Mini bar's row: the door (cover and lines; it opens the sheet), the heart, play, next (+10 for an episode), ⋯; over it, the
    /// 2-px accent progress edge.</summary>
    private static Element MiniRow(PlayerBarProps props, PlayerBarView v, Element art, Element meta, Element heart, IOverlayService overlay)
    {
        var verbs = props.Verbs;
        var doorKids = new List<Element>(2) { art };
        if (v.ShowTitleBlock) doorKids.Add(meta);
        var door = new BoxEl
        {
            Key = PL.DoorKey, Grow = 1f, Shrink = 1f, MinWidth = 0f, Height = PL.DoorH, Direction = 0, AlignItems = FlexAlign.Center, Gap = PL.IdGap,
            Padding = new Edges4(0f, 0f, PL.DoorPadRight, 0f), Corners = CornerRadius4.All(PL.ButtonCorner), ClipToBounds = true,
            HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary, BrushTransitionMs = Design.Motion.Faster,
            Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, Cursor = CursorId.Hand,
            OnClick = props.OpenNowPlaying,
            Draggable = v.Draggable ? Drag.Source(props.DragPayload) : null,
            Animate = PL.MoveMotion,
            Children = doorKids.ToArray(),
        }.WithContextMenu(overlay, () => verbs.NowPlayingMenu());

        var kids = new List<Element>(6) { door };
        if (v.ShowLikeSlot) kids.Add(heart);
        if (v.OwnsTransport)
        {
            // The touch tier: the Mini bar's circle is the 40 floor (the board's 36 grows, owner rule).
            kids.Add(PlayCircle(props, v, PL.ToolBox) with { Key = PL.PlayKey });
            kids.Add(v.Episode
                ? Item("seek-forward-10", StepButton(props, PL.SeekStepMs, v.SeekStepEnabled, PL.ToolBox))
                : Item(PL.NextKey, ToolTip.Wrap(Button(Icons.Next, () => verbs.Next(), v.NextEnabled, false, PL.ToolBox, PL.ToolBox, PL.Glyph),
                    Loc.Get(Strings.Player.Next))));
        }
        kids.Add(Item(PL.MoreKey, MoreMenu(props, v, mini: true)));
        var row = new BoxEl
        {
            Key = PL.RowKey, Direction = 0, Grow = 1f, AlignItems = FlexAlign.Center, Gap = PL.MiniGap, Padding = PL.MiniPadding,
            Children = kids.ToArray(),
        };
        // The progress edge lies over the bar's top edge (on the divider), the whole width.
        return new BoxEl
        {
            Key = "mini", Grow = 1f, ZStack = true,
            Children =
            [
                row,
                new BoxEl
                {
                    Key = PL.EdgeKey, Direction = 1, Grow = 1f, HitTestVisible = false,
                    Children =
                    [
                        new BoxEl
                        {
                            Height = WinUiTransportLayout.MiniProgressH, Shrink = 0f, Direction = 0, ClipToBounds = true,
                            Children = v.TopEdgeSweeps
                                ? [ProgressBar.Indeterminate(v.Metrics.TopEdgeWidth)]
                                : [props.ProgressLine is { } progress ? progress() : new BoxEl { Grow = 1f }],
                        },
                    ],
                },
            ],
        };
    }

    /// <summary>The title (a link to the album or the show), the artists (or, on a remote output, "Playing on …"; from Medium a third line).
    /// Each line is one slot of the column's width: a line that does not fit ends in an ellipsis with its full text in a tooltip, or, with
    /// the app's marquee setting on, the title and the artists are marquees of one group (the start at rest, a pass now and then, one line moving
    /// at a time).</summary>
    private static BoxEl MetaColumn(PlayerBarProps props, PlayerBarView v, bool mini, Signal<bool> titleLinkHover, Ref<NodeHandle> metaNode,
                                    Marquee.Group marquees)
    {
        const string titleKey = "np-title", artistsKey = "np-artists";
        Prop<ColorF> titleInk = Prop.Of(() => !mini && titleLinkHover.Value && props.TitleHasRoute() ? Tok.AccentTextPrimary : InkOf(props.TitleInk()));
        var titleRun = new TextEl(Prop.Of<string>(props.TitleText))
        {
            Size = PL.TitleSize, LineHeight = PL.TitleLine, Weight = PL.TitleWeight, Color = titleInk,
            Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
        };
        // A column of the slot's width (never wider: Shrink), so the run is cut by its own ellipsis, not by the column's clip.
        BoxEl title = v.Marquee
            ? new BoxEl
            {
                Key = titleKey, Height = PL.TitleLine, Direction = 0, AlignItems = FlexAlign.Center, Shrink = 1f, MinWidth = 0f,
                Children = [Marquee.Of(Prop.Of<string>(props.TitleText), MarqueeStyle(PL.TitleSize, PL.TitleWeight, titleInk), marquees, order: 0)],
            }
            : new BoxEl { Key = titleKey, Direction = 1, Shrink = 1f, MinWidth = 0f, ClipToBounds = true, Children = [titleRun] };
        if (v.TitleNav && !mini)
            title = title with
            {
                Cursor = CursorId.Hand, OnClick = props.NavigateTitle, Role = AutomationRole.Hyperlink, Focusable = true,
                OnHoverMove = _ => { if (!titleLinkHover.Peek()) titleLinkHover.Value = true; },
                OnPointerExit = () => { if (titleLinkHover.Peek()) titleLinkHover.Value = false; },
            };

        // A scrolling title shows itself in full; a plain one carries its full text in a tooltip while it is cut.
        var kids = new List<Element>(3) { v.Marquee ? title : Controls.TrimTip(title, props.TitleText(), titleRun) with { Key = titleKey } };
        // The Mini bar's second line is the artists or, while the output plays on another device, "Playing on" in their place: the two cross-fade
        // in the one slot (PL.LineSwap), never a cut.
        if (mini && props.DeviceIsRemote())
            kids.Add(new BoxEl
            {
                Key = "remote-device-line", Direction = 1, MinWidth = 0f, Enter = PL.LineSwap, Exit = PL.LineSwap, Transition = PL.LineSwapMotion,
                Children = [Embed.Comp(new RemoteProps(props, Accent: true), static () => new WinUiRemoteLine())],
            });
        else if (v.ShowArtistLine)
        {
            var credits = new CreditsProps(props, Linked: !mini);
            BoxEl line = v.Marquee
                ? new BoxEl
                {
                    Key = artistsKey, Height = PL.SubLine, Direction = 0, AlignItems = FlexAlign.Center, MinWidth = 0f,
                    Children =
                    [
                        Marquee.Content(Embed.Comp(credits, static () => new WinUiCreditsRun()), Prop.Of(() => props.Credits().Line(PL.CreditSeparator)),
                                        MarqueeStyle(PL.SubSize, PL.SubWeight, Prop.Of(static () => Tok.TextSecondary)), marquees, order: 1),
                    ],
                }
                : new BoxEl { Key = artistsKey, Direction = 1, ClipToBounds = true, MinWidth = 0f, Children = [Embed.Comp(credits, static () => new WinUiCredits())] };
            // The swap rides an outer box of no set height: the marquee's line has one, and the layout reads an exit with a set height as one that
            // still holds its space (a reflow's), so the meta column would grow for the fade and its lines drift.
            kids.Add(mini
                ? new BoxEl
                {
                    Key = artistsKey, Direction = 1, MinWidth = 0f, Enter = PL.LineSwap, Exit = PL.LineSwap, Transition = PL.LineSwapMotion,
                    Children = [line with { Key = null }],
                }
                : line);
        }
        if (!mini && v.RemoteLine)
            kids.Add(new BoxEl
            {
                Key = "remote-device-line", Direction = 1, Animate = PL.ItemMotion, MinWidth = 0f,
                Children = [Embed.Comp(new RemoteProps(props, Accent: false), static () => new WinUiRemoteLine())],
            });

        return new BoxEl
        {
            Key = "meta", Direction = 1, Grow = mini ? 1f : 0f, Shrink = 1f, MinWidth = 0f, Justify = FlexJustify.Center, ClipToBounds = true,
            Animate = PL.MoveMotion, OnRealized = h => metaNode.Value = h,
            Children = kids.ToArray(),
        };
    }

    /// <summary>The bar's marquee look at WinUI's type: its pace and its cut edge's fade (the behaviour is the engine control's).</summary>
    static Marquee.Style MarqueeStyle(float size, ushort weight, Prop<ColorF> ink) => new()
    {
        FontSize = size, Weight = weight, Foreground = ink, Speed = PL.MarqueeSpeed, FadeBand = PL.MarqueeFade,
    };

    static ColorF InkOf(NowPlayingInk ink) => ink switch
    {
        NowPlayingInk.Secondary => Tok.TextSecondary,
        NowPlayingInk.Critical => Tok.SystemFillCritical,
        _ => Tok.TextPrimary,
    };

    /// <summary>The film badge over a video's cover (the bottom-right corner).</summary>
    private static Element FilmBadge() => new BoxEl
    {
        Direction = 0, Justify = FlexJustify.End, AlignItems = FlexAlign.End, HitTestVisible = false, Padding = Edges4.All(PL.FilmInset),
        Children =
        [
            new BoxEl
            {
                Width = PL.FilmBadge, Height = PL.FilmBadge, Shrink = 0f, Corners = CornerRadius4.All(PL.FilmCorner), Fill = PL.FilmFill,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Children = [new TextEl(Icons.Movie) { Size = PL.FilmGlyph, FontFamily = Tok.Definition.Fonts.Icon, Color = PL.FilmInk }],
            },
        ],
    };
}
