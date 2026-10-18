// ── Themes/Zune/Structure/Views/Panel/ZunePanelFrame.cs — the frame of Zune's `Shell.Panel` (themes plan 6.3; P5 Z2a) ──────────────────
//
// Boards Z-Part-Panel, Z-Frame-Panel (inline) and Z-Frame-Narrow (floating). The core keeps what decides (`PanelHost`: open, mode, committed
// width, docked, floating or a sheet; the rail <-> docked-video coupling) and hands this template one decided `PanelView` per render. The
// frame places the host (`ZuneFrame`); this template draws the panel itself:
//
//   surface   the panel ground (#1b1b1b; the frame's reservation paints it while the panel is docked and open, so the slide-out never
//             shows a hole); floating it carries the flyout shadow and no ring, docked no shadow. Square everywhere.
//   column    the docked video card (mounted unconditionally; its own gate hides it) · the PIVOT HEAD · the body of the mode on show.
//   head      (round G: the words are 44-tall hit boxes and sit in a horizontal scroller under an edge fade, never clipped mid-word; the current
//             word is brought into view) the pivots as words in the heading voice (26/32/300), 18 apart, lowercase (the style's navigation case): queue · lyrics ·
//             now playing · friends (where the capabilities offer the friends panel) and video while the video body is up; the current one
//             in the primary ink, the rest tertiary. A word shows its body in the same panel (`PanelProps.ShowMode`); a pane that offers none
//             (a docked pane beside the primary: the queue, friends at Huge 4K) draws its one word. The close glyph (40 x 40) ends the row.
//   docked    (ZT2, Z-Part-Panel "docked2" / "docked3") where panes dock beside the primary, the primary's head leaves out the bodies they
//             show (`PanelView.Beside`: lyrics · now playing · friends at Huge, lyrics · now playing at Huge 4K), and a LINKED pane draws no
//             close (it closes with the panel; the board draws none on the docked columns, the primary keeps its one, as inline).
//   bodies    queue (the core host of `Queue.Panel`), lyrics (`Panel.LyricsHeader` over the core host of `Lyrics.View`), now playing (the
//             hero `Player.NowPlayingHero`, or the docked video, then "up next" over `Panel.NextUp`), friends (`Panel.Friends`), video (the
//             playing track's title and line, then "up next"). The bodies are the player lane's views (Z4a): this frame gives them the full
//             width and lets them carry their own insets.
//   motion    ONE retained translate track on the host node: X docked and floating (the panel's width, plus nothing: it is flush right),
//             Y as a sheet (the window's height); 300 ms on the smooth-out curve; seeded at rest on mount and on a placement change (the host's
//             own FLIP carries that move). A rapid reversal continues from where it is drawn.
//   keys      Escape closes a floating panel or a sheet.
//
// The undrawn arms (now playing, video, the sheet) are built from Zune's parts (owner Q18).

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Input;
using FluentGpu.Localization;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.Zune;

/// <summary>Zune's right panel.</summary>
internal static class ZunePanel
{
    /// <summary>The registered template of <see cref="ViewKeys.ShellPanel"/>.</summary>
    public static Element Template(PanelProps props, in ViewContext ctx) => Embed.Comp(props, static () => new ZunePanelFrame());

    /// <summary>The pivots in their fixed order (Z-Part-Panel inline: the words never rotate on the desktop tiers).</summary>
    internal static readonly PanelMode[] PivotOrder = [PanelMode.Queue, PanelMode.Lyrics, PanelMode.NowPlaying, PanelMode.Friends];

    internal static string Word(PanelMode mode) => mode switch
    {
        PanelMode.Queue => Loc.Get(Strings.Player.Queue),
        PanelMode.Lyrics => Loc.Get(Strings.Player.Lyrics),
        PanelMode.Friends => Loc.Get(Strings.Friends.Title),
        PanelMode.Video => Loc.Get(Strings.Player.Video),
        _ => Loc.Get(Strings.Player.NowPlaying),
    };

    /// <summary>The pivot head: the words (but the bodies the linked panes beside it show), then the close glyph (none on a linked pane).</summary>
    internal static Element Head(PanelProps props, PanelView view, PanelMode body, bool friends, PanelHeadNodes? nodes = null)
    {
        var words = new List<Element>(5);
        if (props.ShowMode is { } show)
        {
            foreach (var mode in PivotOrder)
            {
                if (mode == PanelMode.Friends && !friends) continue;
                if (mode != body && view.Beside.Contains(mode)) continue;   // on screen in its own docked pane
                words.Add(Pivot(mode, mode == body, show, nodes));
            }
            if (body == PanelMode.Video) words.Add(Pivot(PanelMode.Video, current: true, show, nodes));
        }
        else words.Add(Pivot(body, current: true, show: null, nodes));

        var row = new List<Element>(2)
        {
            // The words scroll under an edge fade when they outgrow the head (Compact's sheet, the 340 panel with "friend activity"), never clip.
            new ScrollEl
            {
                Key = ZuneFrameRules.PanelPivotsKey, Horizontal = true, Height = ZuneFrameRules.PanelHeadH,
                Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f,
                AutoEdgeFade = true, AutoEdgeFadeBand = ZuneFrameRules.PanelEdgeFade, SuppressScrollBar = true,
                OnRealized = h => { if (nodes is not null) nodes.Viewport = h; },
                Content = new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Gap = ZuneFrameRules.PanelPivotGap, Height = ZuneFrameRules.PanelHeadH,
                    Children = words.ToArray(),
                },
            },
        };
        if (!view.Linked) row.Add(Close(props.Close));
        return new BoxEl
        {
            Key = ZuneFrameRules.PanelHeadKey, Direction = 0, AlignItems = FlexAlign.Center, Shrink = 0f,
            Padding = ZuneFrameRules.PanelHeadPadding,
            Children = row.ToArray(),
        };
    }

    private static Element Pivot(PanelMode mode, bool current, Action<PanelMode>? show, PanelHeadNodes? nodes)
    {
        var text = R.Type.Heading(Word(mode)) with
        {
            Color = current ? Tok.TextPrimary : Tok.TextTertiary, HoverColor = Tok.TextPrimary,
            Case = R.Case.For(LabelKind.Navigation), MaxLines = 1, Shrink = 0f,
        };
        // The word's box is the head's whole height (44): the touch floor of 40 holds at every tier, the word centred in it.
        Action<NodeHandle>? realized = nodes is null ? null : h => nodes.Pivot(mode, h);
        if (show is null || current)
            return new BoxEl
            {
                Key = ZuneFrameRules.PanelPivotKeyPrefix + mode, Shrink = 0f, Height = ZuneFrameRules.PanelHeadH, Direction = 0, AlignItems = FlexAlign.Center,
                OnRealized = realized, Children = [text],
            };
        return new BoxEl
        {
            Key = ZuneFrameRules.PanelPivotKeyPrefix + mode, Shrink = 0f, Height = ZuneFrameRules.PanelHeadH, Direction = 0, AlignItems = FlexAlign.Center,
            Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, Cursor = CursorId.Hand,
            OnClick = () => show(mode), OnRealized = realized,
            Children = [text],
        };
    }

    /// <summary>✕ closes the panel whatever it shows (a docked video is demoted by the coupling).</summary>
    private static Element Close(Action close) => ToolTip.Wrap(new BoxEl
    {
        Key = ZuneFrameRules.PanelCloseKey,
        Width = ZuneFrameRules.Button, Height = ZuneFrameRules.Button, Shrink = 0f,
        Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Corners = R.Shape.All(ShapeRole.Control),
        Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, Cursor = CursorId.Hand,
        HoverFill = R.Layer.ChromeHover, PressedFill = R.Layer.ChromePressed,
        OnClick = close,
        Children =
        [
            new TextEl(Icons.ChromeClose)
            {
                Size = ZuneFrameRules.PanelCloseGlyph, FontFamily = Tok.Definition.Fonts.Icon, Color = Tok.TextSecondary, HoverColor = Tok.TextPrimary,
            },
        ],
    }, Loc.Get(Strings.Theme.Zune.Frame.ClosePanel));

    /// <summary>The body of <paramref name="body"/>.</summary>
    internal static Element Body(PanelProps props, PanelView view, PanelMode body) => body switch
    {
        PanelMode.Queue => props.QueueBody(),
        PanelMode.Lyrics => new BoxEl
        {
            Direction = 1, Grow = 1f, MinHeight = 0f,
            Children = [Slots.View(ViewKeys.PanelLyricsHeader, props.LyricsHeader), GrowSlot(props.LyricsBody())],
        },
        PanelMode.Friends => Slots.View(ViewKeys.PanelFriends, props.Friends),
        PanelMode.Video => Embed.Comp(props, static () => new ZunePanelVideoArm()),
        _ => NowPlaying(props, view),
    };

    private static BoxEl GrowSlot(Element body) => new() { Direction = 1, Grow = 1f, MinHeight = 0f, ClipToBounds = true, Children = [body] };

    /// <summary>The now-playing arm: the pinned slot (the hero, or the docked video in its place) over "up next".</summary>
    private static Element NowPlaying(PanelProps props, PanelView view)
    {
        Element pinned = view.Pinned == PanelPinned.DockedVideo
            ? DockedCap(props, docked: true, view.Width)
            : Slots.View(ViewKeys.NowPlayingHero, props.NowPlaying);
        return ScrollView(new BoxEl
        {
            Direction = 1,
            Children = [pinned, Embed.Comp(props.NowPlaying, static () => new ZuneNowPlayingArm())],
        }) with { Grow = 1f, MinHeight = 0f, AutoEdgeFade = true };
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

    /// <summary>"up next" over the shared rows, or the empty queue's vacancy.</summary>
    internal static Element[] UpNext(bool has, NextUpProps rows) =>
    [
        R.Type.Heading(Loc.Get(Strings.Player.VideoUpNext)) with { Color = Tok.TextSecondary, Case = R.Case.For(LabelKind.Heading) },
        has
            ? Slots.View(ViewKeys.PanelNextUp, rows)
            : Controls.Vacancy(Controls.VacancyVoice.Empty, Controls.VacancyScale.Compact, title: Loc.Get(Strings.Player.QueueEmpty), subtitle: ""),
    ];
}

/// <summary>The head's realized nodes (the pivots' scroller and each word), kept by the frame so the current word can be brought into view.</summary>
internal sealed class PanelHeadNodes
{
    readonly Dictionary<PanelMode, NodeHandle> _pivots = new();

    /// <summary>The scroller's viewport.</summary>
    public NodeHandle Viewport;

    /// <summary>The word for <paramref name="mode"/> was realized.</summary>
    public void Pivot(PanelMode mode, NodeHandle node) => _pivots[mode] = node;

    /// <summary>The word for <paramref name="mode"/>, or null while it is not realized.</summary>
    public NodeHandle PivotOf(PanelMode mode) => _pivots.TryGetValue(mode, out var node) ? node : NodeHandle.Null;
}

/// <summary>The panel's frame: the surface, the slide, the head and the body on show. A component because the slide targets the rendered
/// root and keeps its seeded-once flag across renders.</summary>
internal sealed class ZunePanelFrame : Component
{
    bool _motionSeeded;
    PanelPlacement _seededPlacement;
    Func<ColorF>? _surfaceFill;
    Action<RectF>? _onBounds;
    Action<KeyEventArgs>? _onKey;
    Action? _close;
    bool _escapes;
    readonly PanelHeadNodes _headNodes = new();

    public override Element Render()
    {
        var props = UseProps<PanelProps>();
        var view = props.Read();
        bool open = view.Open;
        var placement = view.Placement;
        bool floating = placement != PanelPlacement.Dock;
        float width = placement == PanelPlacement.Float ? ZuneFrameRules.PanelFloatW : view.Width;
        var body = view.Body;
        var vp = UseContextSignal(Viewport.Size);
        var facts = TierContext.DrawnIn(UseContext(ThemeFactsContext.Signal));
        bool friends = facts.Value.Capabilities.FriendsPanel;
        bool sheet = placement == PanelPlacement.Sheet;

        _close = props.Close;
        _escapes = open && floating;
        _onKey ??= e =>
        {
            if (e.Handled || e.KeyCode != Keys.Escape || e.Mods != KeyModifiers.None || !_escapes) return;
            e.Handled = true;
            _close?.Invoke();
        };

        // A pane that unmounts its body when closed (the second docked pane) keeps it for the slide-out, then lets it go.
        var (lingering, setLingering) = UseState(open);
        UseLayoutEffect(() => { if (open && !lingering) setLingering(true); }, DepKey.From(open ? 1 : 0));
        UseTimeout(() => { if (!open) setLingering(false); }, ZuneFrameRules.PanelSlideMs, DepKey.From(open ? 1 : 0));
        bool bodyLive = !view.UnmountWhenClosed || open || lingering;

        float offCanvas = sheet ? MathF.Max(1f, vp.Peek().Height) : width;
        UseLayoutEffect(() =>
        {
            if (Context.Anim is not { } anim || Context.HostNode.IsNull) return;
            var channel = sheet ? AnimChannel.TranslateY : AnimChannel.TranslateX;
            float target = open ? 0f : offCanvas;
            if (!_motionSeeded || _seededPlacement != placement)
            {
                _motionSeeded = true;
                _seededPlacement = placement;
                anim.Animate(Context.HostNode, sheet ? AnimChannel.TranslateX : AnimChannel.TranslateY, 0f, 0f, 1f, Easing.Linear);
                anim.Animate(Context.HostNode, channel, target, target, 1f, Easing.Linear);
                return;
            }
            float from = Context.Scene is { } scene
                ? (sheet ? scene.Paint(Context.HostNode).LocalTransform.Dy : scene.Paint(Context.HostNode).LocalTransform.Dx)
                : (open ? offCanvas : 0f);
            anim.Animate(Context.HostNode, channel, from, target, Design.Reduced ? 1f : ZuneFrameRules.PanelSlideMs, ZuneFrameRules.PanelSlideEasing);
        }, DepKey.From(open ? 1 : 0, (int)MathF.Round(offCanvas) * 4 + (int)placement));

        // The word of the body on show is brought into view in the head's scroller (nearest edge, clear of the fade), when the body changes or the
        // panel opens.
        UseLayoutEffect(() =>
        {
            if (!open || Context.Scene is not { } scene) return;
            var viewport = _headNodes.Viewport;
            var word = _headNodes.PivotOf(body);
            if (viewport.IsNull || word.IsNull || !scene.IsLive(viewport) || !scene.IsLive(word)) return;
            scene.BringIntoView(viewport, word, align: float.NaN, Design.Reduced ? ScrollMove.Immediate : ScrollMove.Glide, margin: ZuneFrameRules.PanelEdgeFade);
        }, DepKey.From((int)body, open ? 1 : 0));

        _surfaceFill ??= () => props.InlineAndOpen() ? ColorF.Transparent : R.Layer.FloatingPane;
        _onBounds ??= r => props.BodyHeightChanged(r.H);
        var wash = ZunePanelWash.Layers(props.CoverUrl?.Invoke());

        Element bodySlot = new BoxEl
        {
            Direction = 1, Grow = 1f, MinHeight = 0f, ClipToBounds = true, Padding = ZuneFrameRules.PanelBodyPadding,
            Children = [bodyLive ? ZunePanel.Body(props, view, body) : new BoxEl { Grow = 1f, HitTestVisible = false }],
        };
        var column = new List<Element>(3);
        // The docked card mounts unconditionally outside the now-playing arm (its own gate hides it); the now-playing arm pins it in place
        // of the hero instead.
        if (body != PanelMode.NowPlaying) column.Add(ZunePanel.DockedCap(props, view.DockedHere, width));
        column.Add(ZunePanel.Head(props, view, body, friends, _headNodes));
        column.Add(bodySlot);

        return new BoxEl
        {
            Direction = 1, Grow = 1f, MinHeight = 0f, ClipToBounds = true, ZStack = true, Corners = R.Shape.All(ShapeRole.Overlay),
            HitTestVisible = open,                                             // a closed panel is laid out off-canvas and unhittable
            BlocksBackgroundScroll = open && floating,
            Shadow = floating ? Elevation.Flyout : (ShadowSpec?)null,          // no shadow docked
            OnKeyDown = _onKey,
            Children =
            [
                new BoxEl { Grow = 1f, ClipToBounds = true, ZStack = true, Fill = Prop.Of(_surfaceFill), OnBoundsChanged = _onBounds, Children = wash },
                new BoxEl { Direction = 1, Grow = 1f, MinHeight = 0f, ClipToBounds = true, Children = column.ToArray() },
            ],
        };
    }
}

/// <summary>The now-playing arm's body under the pinned hero: "up next" (the hero carries the track itself).</summary>
internal sealed class ZuneNowPlayingArm : Component
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
            Direction = 1, Gap = ZuneFrameRules.PanelArmGap, Padding = ZuneFrameRules.PanelArmPadding,
            Children = ZunePanel.UpNext(props.HasNextUp(), props.NextUp),
        };
    }
}

/// <summary>The video arm: the playing track's title and line under the docked card, then "up next" (Z-Video's panel, from Zune's parts).</summary>
internal sealed class ZunePanelVideoArm : Component
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
            content.Add(R.Type.NowPlayingTitle(track.Title) with { MaxLines = 2, Wrap = TextWrap.Wrap, Trim = TextTrim.CharacterEllipsis });
            string artists = now.ResolveString(track.ArtistLineId), album = track.Album.IsValid ? track.Album.Title : "";
            string meta = artists.Length == 0 ? album : album.Length == 0 ? artists : artists + " · " + album;
            if (meta.Length > 0) content.Add(R.Type.TrackMeta(meta) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis });
        }
        content.AddRange(ZunePanel.UpNext(now.HasNextUp(), now.NextUp));
        return ScrollView(new BoxEl
        {
            Direction = 1, Gap = ZuneFrameRules.PanelArmGap, Padding = ZuneFrameRules.PanelArmPadding,
            Children = content.ToArray(),
        }) with { Grow = 1f, MinHeight = 0f, AutoEdgeFade = true };
    }
}

/// <summary>The sheet's dim (black at 40 %): it fades with the sheet's open state and a press on it closes the panel. Nothing (transparent,
/// never hit) at every other placement.</summary>
internal sealed class ZunePanelScrim : Component
{
    bool _mounted;

    public override Element Render()
    {
        var props = UseProps<PanelProps>();
        var view = props.Read();
        bool show = view.Open && view.Placement == PanelPlacement.Sheet;
        float ms = Design.Reduced ? 0f : ZuneFrameRules.SheetScrimFadeMs;
        float target = show ? 1f : 0f;
        UseTransition(AnimChannel.Opacity, _mounted ? 1f - target : target, target, ms, Easing.Linear, DepKey.From(show));
        _mounted = true;
        return new BoxEl
        {
            Grow = 1f, Opacity = target, HitTestVisible = show, OnClick = show ? props.Close : null, TabStop = false,
            BlocksBackgroundScroll = show,
            Children = [new BoxEl { Grow = 1f, Fill = ZuneFrameRules.SheetScrim, HitTestVisible = false }],
        };
    }
}
