// ── Themes/WinUi/Structure/Views/Panel/WinUiPanel.cs — the frame of WinUI's `Shell.Panel` (P6 W4a) ────────────────────────────────────
//
// Boards W-Queue, W-PaneLyrics, W-PaneNowPlaying (docked at Standard), W-Shell-Small 2 (the overlay), W-Shell-Compact 3 (the sheet), W-Shell-Huge
// (two panes) and the "Side panes" block of W-Tiers. The core keeps what decides (`PanelHost`: open, mode, committed width, docked, floating or a
// sheet; the rail <-> docked-video coupling; the tier's placement and dock line, `Rules\Tiers.Player.cs`) and hands this template one decided
// `PanelView` per render. The frame (`WinUiFrame`) places the host; this template draws the pane itself:
//
//   surface   DOCKED in the content layer beside the page (the frame's reservation paints the layer; the pane adds its 1-px left divider);
//             as an OVERLAY (below the dock line) the acrylic flyout surface with its stroke and the flyout shadow, at the same width; as a
//             SHEET (Compact) 85 % of the window body's height from the bottom, the acrylic with r8 top corners and the 32 x 4 grabber.
//   head      the tabs (Body 14, the selected one semibold in the primary ink with the 3-px accent pill under it, the rest secondary), 48
//             tall, then the close (32; 40 at Compact). The primary pane's tabs are Queue · Lyrics · Now playing · Friends (Friends where the
//             capabilities offer it; Video while the video body is up) less what a linked pane beside it shows (`PanelView.Beside`); a pane of
//             Huge's arrangement shows its own tab group (`PanelView.Tabs`: Queue + Now playing) and switches it with `ShowPaneTab`. Opening
//             another body switches the tab, so the content's width never changes twice (W-Tiers).
//   bodies    Queue (the core host of `Queue.Panel`), Lyrics (`Panel.LyricsHeader` over the core host of `Lyrics.View`), Now playing (the
//             hero `Player.NowPlayingHero`, or the docked video in its place; in the sheet, the transport the sheet covers the bar for; then
//             "Next up" over `Panel.NextUp`), Friends (`Panel.Friends`), Video (the playing track's title and line, then "Next up").
//   motion    ONE retained translate track on the host node: X docked and as an overlay, Y as a sheet; 250 ms on the decelerate curve; seeded
//             at rest on mount and on a placement change. A dock <-> overlay change CROSSFADES the pane in over 250 ms (the frame FLIPs the
//             host; the page's reservation moves once, on the line, never mid-gesture: the dock line holds 24).
//   keys      Escape closes an overlay or a sheet; the grabber closes the sheet (the scrim above it too).
//
// The sheet remembers its tab: the core keeps the mode across a close and a reopen.

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Input;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

using B = Wavee.Themes.WinUi.WinUiPanelBodies;
using F = Wavee.Themes.WinUi.WinUiPanelLayout;

namespace Wavee.Themes.WinUi;

/// <summary>WinUI's side pane.</summary>
internal static class WinUiPanel
{
    /// <summary>The registered template of <see cref="ViewKeys.ShellPanel"/>.</summary>
    public static Element Template(PanelProps props, in ViewContext ctx) => Embed.Comp(props, static () => new WinUiPanelFrame());

    /// <summary>The tabs in their fixed order (W-Queue: Queue · Lyrics · Now playing · Friends).</summary>
    internal static readonly PanelMode[] TabOrder = [PanelMode.Queue, PanelMode.Lyrics, PanelMode.NowPlaying, PanelMode.Friends];

    internal static string Word(PanelMode mode) => mode switch
    {
        PanelMode.Queue => Loc.Get(Strings.Player.Queue),
        PanelMode.Lyrics => Loc.Get(Strings.Player.Lyrics),
        PanelMode.Friends => Loc.Get(Strings.Theme.Winui.Player.Friends),
        PanelMode.Video => Loc.Get(Strings.Player.Video),
        _ => Loc.Get(Strings.Theme.Winui.Player.NowPlaying),
    };

    /// <summary>The head: the tabs, then the close.</summary>
    internal static Element Head(PanelProps props, PanelView view, PanelMode body, bool friends, bool compact)
    {
        var tabs = new List<Element>(5);
        if (!view.Tabs.IsEmpty && props.ShowPaneTab is { } showPane)
        {
            // A pane of Huge's arrangement: its own tab group.
            foreach (var mode in TabOrder)
                if (view.Tabs.Contains(mode) || mode == body) tabs.Add(Tab(mode, mode == body, showPane));
        }
        else if (props.ShowMode is { } show)
        {
            foreach (var mode in TabOrder)
            {
                if (mode == PanelMode.Friends && !friends) continue;
                if (mode != body && view.Beside.Contains(mode)) continue;   // on screen in its own docked pane
                tabs.Add(Tab(mode, mode == body, show));
            }
            if (body == PanelMode.Video) tabs.Add(Tab(PanelMode.Video, current: true, show));
        }
        else tabs.Add(Tab(body, current: true, show: null));

        float close = compact ? F.CloseTouchBox : F.CloseBox;
        return new BoxEl
        {
            Key = F.TabsKey, Direction = 1, Shrink = 0f,
            Children =
            [
                new BoxEl
                {
                    Height = F.TabRowH, Direction = 0, AlignItems = FlexAlign.Center, Padding = F.TabRowPadding,
                    Children =
                    [
                        new ScrollEl
                        {
                            Horizontal = true, Height = F.TabRowH, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, SuppressScrollBar = true, AutoEdgeFade = true,
                            Content = new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = F.TabGap, Height = F.TabRowH, Children = tabs.ToArray() },
                        },
                        ToolTip.Wrap(WinUiPlayerBar.Button(Icons.ChromeClose, props.Close, true, false, close, close, F.CloseGlyph) with { Key = F.CloseKey },
                            Loc.Get(Strings.Theme.Winui.Player.ClosePane)),
                    ],
                },
            ],
        };
    }

    /// <summary>One tab: the word over the pill (reserved on every tab, painted on the selected one), its hit box the row's 48.</summary>
    private static Element Tab(PanelMode mode, bool current, Action<PanelMode>? show)
    {
        var column = new BoxEl
        {
            Direction = 1, AlignItems = FlexAlign.Stretch, Gap = F.PillGap, Shrink = 0f,
            Children =
            [
                new TextEl(Word(mode))
                {
                    Size = F.TabSize, LineHeight = F.TabLine, Weight = current ? F.TabSelectedWeight : F.TabWeight,
                    Color = current ? Tok.TextPrimary : Tok.TextSecondary, HoverColor = Tok.TextPrimary, MaxLines = 1, Wrap = TextWrap.NoWrap,
                },
                new BoxEl { Height = F.PillH, Corners = CornerRadius4.All(F.PillH / 2f), Fill = current ? Tok.AccentDefault : ColorF.Transparent },
            ],
        };
        bool clickable = show is not null && !current;
        return new BoxEl
        {
            Key = F.TabKeyPrefix + mode, Height = F.TabRowH, MinWidth = F.TabMinW, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center, Padding = new Edges4(0f, F.PillH + F.PillGap, 0f, 0f),
            Role = AutomationRole.Tab, Focusable = clickable, AllowFocusOnInteraction = false, Cursor = clickable ? CursorId.Hand : null,
            OnClick = clickable ? () => show!(mode) : null,
            Children = [column],
        };
    }

    /// <summary>The body of <paramref name="body"/>.</summary>
    internal static Element Body(PanelProps props, PanelView view, PanelMode body, bool sheet) => body switch
    {
        PanelMode.Queue => props.QueueBody(),
        PanelMode.Lyrics => new BoxEl
        {
            Direction = 1, Grow = 1f, MinHeight = 0f,
            Children = [Slots.View(ViewKeys.PanelLyricsHeader, props.LyricsHeader), GrowSlot(props.LyricsBody())],
        },
        PanelMode.Friends => Slots.View(ViewKeys.PanelFriends, props.Friends),
        PanelMode.Video => Embed.Comp(props, static () => new WinUiPanelVideoArm()),
        _ => NowPlaying(props, view, sheet),
    };

    private static BoxEl GrowSlot(Element body) => new() { Direction = 1, Grow = 1f, MinHeight = 0f, ClipToBounds = true, Children = [body] };

    /// <summary>The Now playing tab: the pinned slot (the hero, or the docked video in its place), the sheet's transport, then "Next up".</summary>
    private static Element NowPlaying(PanelProps props, PanelView view, bool sheet)
    {
        Element pinned = view.Pinned == PanelPinned.DockedVideo
            ? DockedCap(props, docked: true, view.Width)
            : Slots.View(ViewKeys.NowPlayingHero, props.NowPlaying);
        var kids = new List<Element>(3) { pinned };
        if (sheet && props.Transport is { } transport) kids.Add(Embed.Comp(transport, static () => new WinUiSheetTransport()));
        kids.Add(Embed.Comp(props.NowPlaying, static () => new WinUiNowPlayingArm()));
        return ScrollView(new BoxEl { Direction = 1, Children = kids.ToArray() }) with { Grow = 1f, MinHeight = 0f, AutoEdgeFade = true };
    }

    /// <summary>The docked video card with its splitter: the height cell the splitter writes and the card binds (a stable bind).</summary>
    internal static Element DockedCap(PanelProps props, bool docked, float width)
    {
        Element video = props.DockedCard();
        if (!docked) return video;                                         // the card's own gate: zero size, no reflow
        return new BoxEl
        {
            Direction = 1, ZStack = true, Shrink = 0f, ClipToBounds = true,
            Height = props.DockedVideoHeight, Fill = Tok.MediaLetterbox,
            Children =
            [
                video,
                new BoxEl
                {
                    Grow = 1f, Direction = 1, Justify = FlexJustify.End, HitTestPassThrough = true,
                    Children =
                    [
                        Splitter.Create(props.DockedVideoHeight, props.CommitDockedVideo, new Splitter.SplitterOptions
                        {
                            Min = props.DockedVideoMin(width),
                            Max = props.DockedVideoMax(),
                            Axis = SplitterAxis.Vertical, ShowIndicator = false,
                        }),
                    ],
                },
            ],
        };
    }

    /// <summary>"Next up" (Body Strong) over the shared rows, or the empty queue's vacancy.</summary>
    internal static Element[] UpNext(bool has, NextUpProps rows) =>
    [
        new TextEl(Loc.Get(Strings.Player.NextUp)) { Size = WinUiQueueRules.HeaderSize, LineHeight = WinUiQueueRules.HeaderLine, Weight = WinUiQueueRules.TitleWeight, Color = Tok.TextPrimary },
        has
            ? Slots.View(ViewKeys.PanelNextUp, rows)
            : Controls.Vacancy(Controls.VacancyVoice.Empty, Controls.VacancyScale.Compact, title: Loc.Get(Strings.Player.QueueEmpty), subtitle: ""),
    ];
}

/// <summary>The pane's frame: the surface, the slide, the crossfade, the head and the body on show. A component because the slide targets the
/// rendered root and keeps its seeded-once flag across renders.</summary>
internal sealed class WinUiPanelFrame : Component
{
    bool _motionSeeded;
    PanelPlacement _seededPlacement;
    Action<RectF>? _onBounds;
    Action<KeyEventArgs>? _onKey;
    Action? _close;
    bool _escapes;

    public override Element Render()
    {
        var props = UseProps<PanelProps>();
        var view = props.Read();
        bool open = view.Open;
        var placement = view.Placement;
        bool floating = placement != PanelPlacement.Dock;
        bool sheet = placement == PanelPlacement.Sheet;
        float width = placement == PanelPlacement.Float ? WinUiFrameRules.PanelFloatW : view.Width;
        var body = view.Body;
        var vp = UseContextSignal(Viewport.Size);
        var facts = TierContext.DrawnIn(UseContext(ThemeFactsContext.Signal));
        bool friends = facts.Value.Capabilities.FriendsPanel;
        var tiers = facts.Value.Tiers;
        bool compact = tiers.Policy is not null && tiers.Window == WidthTier.Compact;

        _close = props.Close;
        _escapes = open && floating;
        _onKey ??= e =>
        {
            if (e.Handled || e.KeyCode != Keys.Escape || e.Mods != KeyModifiers.None || !_escapes) return;
            e.Handled = true;
            _close?.Invoke();
        };

        // A pane that unmounts its body when closed (a docked pane beside the primary) keeps it for the slide-out, then lets it go.
        var (lingering, setLingering) = UseState(open);
        UseLayoutEffect(() => { if (open && !lingering) setLingering(true); }, DepKey.From(open ? 1 : 0));
        UseTimeout(() => { if (!open) setLingering(false); }, F.SlideMs, DepKey.From(open ? 1 : 0));
        bool bodyLive = !view.UnmountWhenClosed || open || lingering;

        float offCanvas = sheet ? MathF.Max(1f, vp.Peek().Height) : width;
        UseLayoutEffect(() =>
        {
            if (Context.Anim is not { } anim || Context.HostNode.IsNull) return;
            var channel = sheet ? AnimChannel.TranslateY : AnimChannel.TranslateX;
            float target = open ? 0f : offCanvas;
            if (!_motionSeeded || _seededPlacement != placement)
            {
                // A dock <-> overlay change while open crossfades the pane in (the frame FLIPs the host to its new place).
                bool crossfade = _motionSeeded && open && !Design.Reduced
                    && (_seededPlacement == PanelPlacement.Dock) != (placement == PanelPlacement.Dock) && placement != PanelPlacement.Sheet
                    && _seededPlacement != PanelPlacement.Sheet;
                _motionSeeded = true;
                _seededPlacement = placement;
                anim.Animate(Context.HostNode, sheet ? AnimChannel.TranslateX : AnimChannel.TranslateY, 0f, 0f, 1f, Easing.Linear);
                anim.Animate(Context.HostNode, channel, target, target, 1f, Easing.Linear);
                if (crossfade) anim.Animate(Context.HostNode, AnimChannel.Opacity, 0f, 1f, F.CrossfadeMs, Easing.Linear);
                return;
            }
            float from = Context.Scene is { } scene
                ? (sheet ? scene.Paint(Context.HostNode).LocalTransform.Dy : scene.Paint(Context.HostNode).LocalTransform.Dx)
                : (open ? offCanvas : 0f);
            anim.Animate(Context.HostNode, channel, from, target, Design.Reduced ? 1f : F.SlideMs, F.SlideEasing);
        }, DepKey.From(open ? 1 : 0, (int)MathF.Round(offCanvas) * 4 + (int)placement));

        _onBounds ??= r => props.BodyHeightChanged(r.H);

        Element bodySlot = new BoxEl
        {
            Key = F.BodyKey, Direction = 1, Grow = 1f, MinHeight = 0f, ClipToBounds = true,
            Children = [bodyLive ? WinUiPanel.Body(props, view, body, sheet) : new BoxEl { Grow = 1f, HitTestVisible = false }],
        };
        var column = new List<Element>(4);
        if (sheet) column.Add(Grabber(props.Close, compact));
        // The docked card mounts unconditionally outside the Now playing tab (its own gate hides it); that tab pins it in place of the hero.
        if (body != PanelMode.NowPlaying) column.Add(WinUiPanel.DockedCap(props, view.DockedHere, width));
        column.Add(WinUiPanel.Head(props, view, body, friends, compact));
        column.Add(bodySlot);

        CornerRadius4 corners = sheet ? new CornerRadius4(B.CardRadius, B.CardRadius, 0f, 0f)
            : placement == PanelPlacement.Float ? new CornerRadius4(B.CardRadius, 0f, 0f, B.CardRadius)
            : default;
        var surface = new BoxEl
        {
            Key = sheet ? F.SheetKey : null,
            Direction = 1, Grow = sheet ? F.SheetShare : 1f, Basis = sheet ? 0f : float.NaN, MinHeight = 0f, ClipToBounds = true, ZStack = true,
            Corners = corners, HitTestVisible = open,                       // a closed pane is laid out off-canvas and unhittable
            BlocksBackgroundScroll = open && floating,
            Shadow = floating ? Elevation.Flyout : (ShadowSpec?)null,       // no shadow docked
            OnKeyDown = _onKey,
            Children =
            [
                new BoxEl
                {
                    Grow = 1f, ClipToBounds = true, Corners = corners, OnBoundsChanged = _onBounds,
                    Acrylic = floating ? Tok.AcrylicFlyout : null, Fill = floating ? ColorF.Transparent : ColorF.Transparent,
                    BorderColor = floating ? Tok.StrokeFlyoutDefault : ColorF.Transparent, BorderWidth = floating ? 1f : 0f,
                },
                // Docked: the 1-px divider on the pane's left edge (the content layer runs under both).
                new BoxEl
                {
                    Direction = 0, Grow = 1f, HitTestVisible = false,
                    Children = [new BoxEl { Width = floating ? 0f : F.DividerH, Shrink = 0f, Fill = Tok.StrokeDividerDefault }],
                },
                new BoxEl { Direction = 1, Grow = 1f, MinHeight = 0f, ClipToBounds = true, Children = column.ToArray() },
            ],
        };
        if (!sheet) return surface;
        // The sheet: 85 % of the body's height from the bottom; the room above it lets taps through to the frame's scrim.
        return new BoxEl
        {
            Direction = 1, Grow = 1f, HitTestPassThrough = true,
            Children = [new BoxEl { Grow = 1f - F.SheetShare, Basis = 0f, HitTestVisible = false }, surface],
        };
    }

    /// <summary>The sheet's grabber (32 x 4): its row closes the sheet (40 tall at Compact, the touch floor).</summary>
    static Element Grabber(Action close, bool compact) => new BoxEl
    {
        Key = F.GrabberKey, Height = compact ? F.GrabberTouchH : F.GrabberRowH, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Role = AutomationRole.Button, Cursor = CursorId.Hand, OnClick = close,
        Children =
        [
            ToolTip.Wrap(new BoxEl
            {
                Width = F.GrabberTouchH, Height = compact ? F.GrabberTouchH : F.GrabberRowH, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Children = [new BoxEl { Width = F.GrabberW, Height = F.GrabberH, Corners = CornerRadius4.All(F.GrabberH / 2f), Fill = Tok.FillControlStrong }],
            }, Loc.Get(Strings.Theme.Winui.Player.Grabber)),
        ],
    };
}

/// <summary>The Now playing tab's body under the hero: "Next up" (the hero carries the track itself).</summary>
internal sealed class WinUiNowPlayingArm : Component
{
    public override Element Render()
    {
        var props = UseProps<NowPlayingHeroProps>();
        var track = props.NowTrack();
        if (!track.IsValid)
            return Controls.Vacancy(Controls.VacancyVoice.Empty, Controls.VacancyScale.Compact, title: Loc.Get(Strings.Player.NothingPlaying), subtitle: "");
        props.SubscribeEntities();
        return new BoxEl
        {
            Direction = 1, Gap = B.HeroMetaGap, Padding = B.Inset,
            Children = WinUiPanel.UpNext(props.HasNextUp(), props.NextUp),
        };
    }
}

/// <summary>The sheet's transport (the sheet covers the Mini bar): the seek row (both times) over shuffle · previous · play · next · repeat,
/// the bar's own pieces at the touch floor.</summary>
internal sealed class WinUiSheetTransport : Component
{
    public override Element Render()
    {
        var props = UseProps<PanelTransportProps>();
        var f = props.Facts();
        var verbs = props.Verbs;
        const float box = WinUiPanelBodies.TouchButton;
        return new BoxEl
        {
            Key = "sheet-transport", Direction = 1, Padding = B.Inset, Shrink = 0f,
            Children =
            [
                new BoxEl
                {
                    Height = WinUiPlayerLayout.SeekRowH, Direction = 0, AlignItems = FlexAlign.Center, Gap = WinUiPlayerLayout.SeekGap,
                    Children =
                    [
                        props.TimeText(false),
                        new BoxEl { Direction = 0, Grow = 1f, Shrink = 1f, MinWidth = 0f, Children = [props.SeekBar()] },
                        props.TimeText(true),
                    ],
                },
                new BoxEl
                {
                    Height = box + WinUiPlayerLayout.TransportGap, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Gap = WinUiPlayerLayout.TransportGap,
                    Children =
                    [
                        WinUiPlayerBar.Button(Icons.Shuffle, () => verbs.ToggleShuffle(), f.CanTransport, f.Shuffle, box, box, WinUiPlayerLayout.Glyph),
                        WinUiPlayerBar.Button(Icons.Previous, () => verbs.Previous(), f.PrevEnabled, false, box, box, WinUiPlayerLayout.Glyph),
                        new BoxEl
                        {
                            Width = WinUiPlayerLayout.Play + WinUiPlayerLayout.TransportGap, Height = WinUiPlayerLayout.Play + WinUiPlayerLayout.TransportGap,
                            Corners = Radii.Circle(WinUiPlayerLayout.Play + WinUiPlayerLayout.TransportGap), AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                            Fill = f.PrimaryEnabled ? Tok.AccentDefault : Tok.AccentDisabled, HoverFill = f.PrimaryEnabled ? Tok.AccentSecondary : Tok.AccentDisabled,
                            Role = AutomationRole.Button, Focusable = true, Cursor = f.PrimaryEnabled ? CursorId.Hand : null,
                            OnClick = f.PrimaryEnabled ? () => verbs.TogglePlay("panel.sheet") : null,
                            Children = [new TextEl(f.Playing ? WinUiPlayerLayout.PauseSolid : WinUiPlayerLayout.PlaySolid) { Size = WinUiPlayerLayout.PlayGlyph, FontFamily = Tok.Definition.Fonts.Icon, Color = Tok.TextOnAccentPrimary }],
                        },
                        WinUiPlayerBar.Button(Icons.Next, () => verbs.Next(), f.NextEnabled, false, box, box, WinUiPlayerLayout.Glyph),
                        WinUiPlayerBar.Button(f.RepeatOne ? Icons.RepeatOne : Icons.RepeatAll, () => verbs.CycleRepeat(), f.CanTransport, f.RepeatOn, box, box, WinUiPlayerLayout.Glyph),
                    ],
                },
            ],
        };
    }
}

/// <summary>The video tab: the playing track's title and line under the docked card, then "Next up" (built from WinUI's parts: no board).</summary>
internal sealed class WinUiPanelVideoArm : Component
{
    public override Element Render()
    {
        var props = UseProps<PanelProps>();
        var now = props.NowPlaying;
        var track = now.NowTrack();
        props.SubscribeVideoBody();
        var content = new List<Element>(5);
        if (track.IsValid)
        {
            content.Add(new TextEl(track.Title)
            {
                Size = B.HeroTitleSize, LineHeight = B.HeroTitleLine, Weight = B.HeroTitleWeight, Color = Tok.TextPrimary,
                MaxLines = 2, Wrap = TextWrap.Wrap, Trim = TextTrim.CharacterEllipsis,
            });
            string artists = now.ResolveString(track.ArtistLineId), album = track.Album.IsValid ? track.Album.Title : "";
            string meta = artists.Length == 0 ? album : album.Length == 0 ? artists : artists + " · " + album;
            if (meta.Length > 0)
                content.Add(new TextEl(meta) { Size = B.HeroArtistSize, LineHeight = B.HeroArtistLine, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis });
        }
        content.AddRange(WinUiPanel.UpNext(now.HasNextUp(), now.NextUp));
        return ScrollView(new BoxEl
        {
            Direction = 1, Gap = B.HeroGap, Padding = B.HeroPadding,
            Children = content.ToArray(),
        }) with { Grow = 1f, MinHeight = 0f, AutoEdgeFade = true };
    }
}
