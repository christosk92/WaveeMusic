using System;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Input;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Core;
using Wavee.Features.Video;

namespace Wavee;

// The WaveeMusic-style right rail container: a header (panel title + close) over the active panel's content. Mounted by
// WaveeShell as the third child of the sidebar+content row; the
// shell animates the rail's width (and thus visibility). Reads ShellUi for the active mode.
sealed class RightRail : Component
{
    bool _motionSeeded;

    public override Element Render()
    {
        var ui = UseContext(ShellUi.Slot);
        // Unconditional (before the null-guard below): a hook taken after an early return would shift the hook order the
        // frame the shell context arrives. Only the lyrics header reads it. The bridge joins it for the same reason —
        // RailMode.Video's header glyphs (pop-out/fullscreen) need it too.
        var svc = UseContext(Services.Slot);
        var b = UseContext(PlaybackBridge.Slot);
        if (ui is null) return new BoxEl();
        bool open = ui.RailOpen.Value;
        float railWidth = ui.RailWidth.Value;
        var mode = ui.Mode.Value;   // subscribe → swap the panel on a mode change
        bool floating = !ui.RailFits.Value;

        // ── the rail YIELDS to the module watch page's in-page stage ─────────────────────────────────────────────────
        // There is exactly ONE video surface per player, and two docked hosts now want it. The host is DERIVED
        // (DockedVideoHosting.HostFor) — never claimed, never stored — because a page that is navigating away or parked
        // in the keep-alive cache cannot re-render to hand a claim back, so a claim/release protocol would strand the
        // surface on a dead page. Here that derivation costs two reads and splits into two independent answers:
        //
        //   dockedVideo — should the RAIL reserve the cap's height + splitter strip? Only when the rail is the host;
        //                 while the stage hosts, the rail reserves nothing at all and the body gets the whole column.
        //   railBody    — which body renders (RailVideoCoupling.BodyModeFor). A RENDER-time substitution, never a write
        //                 to ShellUi.Mode: the user's chosen mode is untouched, so the instant the stage yields the
        //                 rail is back exactly where it was, with nothing to restore and no ordering to get wrong.
        var resolved = b?.VideoPlacementNow() ?? SurfacePlacement.None;
        bool stageHosts = DockedVideoHosting.HostFor(resolved, ui.ActiveStagePlayable.Value, b?.CurrentTrack.Value?.Uri)
                          == DockedVideoHost.PageStage;
        bool dockedVideo = resolved == SurfacePlacement.Docked && !stageHosts;
        var railBody = RailVideoCoupling.BodyModeFor(mode, stageHosts);
        bool nowPlaying = railBody == RailMode.Details;

        // The shell keeps this panel at its final layout width. Animate the component host itself so open AND close retain
        // the fully-laid-out subtree while it slides through the shell's fixed clip; no width/layout writes occur per tick.
        // WAVEE_RAIL_BASELINE preserves the old wrapper-width Reflow path for the probe's same-build A/B.
        bool baseline = Diag.EnvFlag("WAVEE_RAIL_BASELINE");
        UseLayoutEffect(() =>
        {
            if (baseline || Context.Anim is not { } anim || Context.HostNode.IsNull) return;
            float x = open ? 0f : railWidth;
            if (!_motionSeeded)
            {
                _motionSeeded = true;
                // Seed closed off-canvas (or open in-place) without a startup fly-in.
                anim.Animate(Context.HostNode, AnimChannel.TranslateX, x, x, 1f, Easing.Linear);
            }
            else
            {
                // Same retained clip+translation choreography as WinUI SplitView. There is deliberately no opacity
                // track: a fading full-height panel is the source of the faint "ghost rail" during unrelated motion.
                // Sample the actually presented transform so a rapid reversal continues from the visible position.
                float from = Context.Scene is { } scene
                    ? scene.Paint(Context.HostNode).LocalTransform.Dx
                    : (open ? railWidth : 0f);
                anim.Animate(Context.HostNode, AnimChannel.TranslateX, from, x, 300f,
                    EasingSpec.CubicBezier(0f, 0.35f, 0.15f, 1f));
            }
        }, DepKey.From(HashCode.Combine(open, railWidth, baseline)));
        // Flat CONTENT-LAYER surface, TOP-LEFT rounded like the page's own silhouette — the rail band and the page are
        // ONE RUNG, the same stock LayerFillColorDefault smoke over live Mica. Exactly ONE coat paints that rung:
        // DOCKED the rail is Transparent and the shell spacer's underlay band paints the single FileArea coat (three
        // coats → one — the band and the panel used to paint two different materials in the same 340-DIP strip, so the
        // seam between them reappeared the moment the wallpaper was not neutral); FLOATING the rail paints its own
        // FileArea over the shell's FloatingChrome backing, completing the same ladder. Bound Fill, not a branch: the
        // dock/float flip repaints without re-rendering the panel subtree.
        var corners = new CornerRadius4(Radii.Card, 0f, 0f, 0f);

        Element surface = new BoxEl
        {
            Grow = 1f, Corners = corners, ClipToBounds = true,
            // RailOpen is part of the key: on CLOSE the shell spacer snaps to 0 in the commit frame (RailSpacerAnim is
            // null on the projected path) and the underlay's coat vanishes instantly, while this panel spends 300ms
            // translating out — without its own coat that slide is raw text over the expanding page. Open stays
            // single-coat: RailOpen flips true while the panel is still off-clip, so the hand-off never double-paints.
            Fill = Prop.Of(() => ui.RailFits.Value && ui.RailOpen.Value ? ColorF.Transparent : WaveeColors.FileArea),
        };
        // The stock edge, docked: 1px StrokeCardDefault on LEFT + TOP only (the same left+top-only mechanism the shell's
        // content region uses — WaveeShell.StrokeOverhang documents it), drawn TOPMOST so panel content cannot cover it.
        // This is the SINGLE stroke owner for the docked band — the shell's underlay deliberately paints none, or the
        // two would double-draw along the same hairline.
        // FLOATING keeps the uniform ring instead: a panel hovering over the page is a flyout-class surface and wants a
        // closed outline, which is also the only case that keeps a shadow.
        Element edge = new BoxEl
        {
            Margin = new Edges4(0f, 0f, -1f, -1f), HitTestVisible = false,
            BorderWidth = floating ? 0f : 1f,
            BorderColor = Prop.Of(() => Tok.StrokeCardDefault),
            Corners = corners,
        };

        // Lyrics only: promote the panel to the fullscreen immersive surface (WaveeShell mounts it off this signal).
        // The rail is left exactly as it is underneath — the surface covers the shell rather than replacing the panel.
        Element[] headerKids = railBody switch
        {
            RailMode.Lyrics => LyricsHeaderKids(ui, svc?.Settings),
            RailMode.Video => VideoHeaderKids(ui, b),
            _ => [TitleText(railBody), CloseButton(() => ui.RailOpen.Value = false)],
        };

        var header = new BoxEl
        {
            // 44, not 36: the header title is now WaveeType.RailHeader (Subtitle 20/28), and a 28-DIP line box needs a
            // band that can hold it with a hairline of breathing room. 44 is also the app's NavItemH rung.
            Direction = 0, Height = WaveeSize.NavItemH, AlignItems = FlexAlign.Center, Gap = 4f,
            Padding = new Edges4(Spacing.M, 0f, Spacing.S, 0f),
            Children = headerKids,
        };

        Element body = railBody switch
        {
            // PARK the rail's lyrics engine while the immersive surface is up: it is fully occluded, and two live
            // LyricsView documents would each run a 16 ms ticker, a DoF ramp and a handoff cascade for nothing. The
            // visibility gate is the same one the immersive surface uses, so exactly one of them is ever ticking.
            RailMode.Lyrics => Embed.Comp(() => new LyricsView(
                visible: () => ui.RailOpen.Value && !ui.ImmersiveLyrics.Value)),
            RailMode.Queue => Embed.Comp(() => new QueuePanel()),
            RailMode.Friends => Embed.Comp(() => new FriendsPanel()),
            RailMode.Video => Embed.Comp(() => new VideoRailPanel()),
            _ => Embed.Comp(() => new NowPlayingPanel()),
        };

        // Now-playing: no title chrome; the inset artwork and sections fill the rounded rail surface.
        if (nowPlaying)
        {
            return new BoxEl
            {
                Grow = 1f, MinHeight = 0f, ClipToBounds = true, ZStack = true, HitTestVisible = baseline || open,
                Corners = corners,
                BorderColor = floating ? Tok.StrokeCardDefault : ColorF.Transparent,
                BorderWidth = floating ? 1f : 0f,
                // NO shadow docked (stock): the docked rail is a content-layer sibling of the page, not an elevated
                // card — and its shadow used to stack against the shell reservation band's own Elevation.Card under it.
                Shadow = floating ? Elevation.Flyout : null,
                Children =
                [
                    surface,
                    new BoxEl
                    {
                        // Wrapped in a column (was a bare Grow=1f box): the pinned hero sits ABOVE the scrolled
                        // sections here, Shrink=0f, the SAME "Shrink=0f pinned, Grow=1f scrolls" shape the non-Details
                        // arm below uses. The two arms now agree on the hero slot's contents too: PinnedHero hands back
                        // the very same DockedCap card the other arm mounts whenever the video is docked here, and the
                        // cover-art tile (NowPlayingPanel.NowPlayingHeroTile, hoisted OUT of that panel's own
                        // ScrollView) with the Art|Video toggle over its top-right corner otherwise.
                        Direction = 1, Grow = 1f, MinHeight = 0f, ClipToBounds = true,
                        Children =
                        [
                            PinnedHero(ui, b, stageHosts, svc?.Settings, railWidth),
                            new BoxEl { Grow = 1f, MinHeight = 0f, ClipToBounds = true, Children = [body] },
                        ],
                    },
                    edge,
                ],
            };
        }

        return new BoxEl
        {
            Direction = 1, Grow = 1f, MinHeight = 0f, ClipToBounds = true, ZStack = true, HitTestVisible = baseline || open,
            Corners = corners,
            BorderColor = floating ? Tok.StrokeCardDefault : ColorF.Transparent,
            BorderWidth = floating ? 1f : 0f,
            Shadow = floating ? Elevation.Flyout : null,   // see the now-playing arm above: no elevation docked (stock)
            Children =
            [
                surface,
                new BoxEl
                {
                    Direction = 1, Grow = 1f, MinHeight = 0f, ClipToBounds = true,
                    // The docked video card is a Shrink=0f sibling of header, pinned above it — never inside it. It
                    // mounts UNCONDITIONALLY here (both the Cap face — Lyrics/Queue/Friends — and the Takeover face —
                    // Video — share this exact slot per the docked-video design's §1): DockedVideoSurface's OWN mount
                    // gate (VideoPlacementNow() != Docked ⇒ an empty, Shrink=0f BoxEl) is what makes it disappear with
                    // no reflow the instant the video is anywhere else. The wrapper Height bind collapses to 0 when
                    // not docked so the vertical splitter cannot reserve a strip. Reasons this must stay pinned rather
                    // than scrolled live in that design's §1 (AutoEdgeFade erasing the hole, ScrollLeaseCapture
                    // disqualifying the fling lease, rect-only ancestor clipping against the rail's rounded silhouette).
                    Children = [DockedCap(ui, dockedVideo, svc?.Settings, railWidth), header, new BoxEl { Grow = 1f, MinHeight = 0f, ClipToBounds = true, Children = [body] }],
                },
                edge,
            ],
        };
    }

    // Full-bleed cap + a vertical Splitter overlaid on its bottom 16 DIP (HitTestPassThrough so chrome/clicks on the
    // video keep working). Height is the SAME FloatSignal the splitter writes — a stable bind, not a new Prop.Of
    // thunk each render (that left LayoutInput.Height as NaN and the ZStack collapsed to the 16-DIP strip).
    // Floor = 16:9 of the live rail width (drag only grows); the lyrics/queue body remains the Grow=1 remainder.
    // At REST the height is not the rail's business at all: DockedVideoSurface fits it to the playing content's own
    // aspect (ShellResponsiveLayout.FitDockedVideoHeight), and this splitter is the user's override of that fit.
    //
    // This is the rail's ONE card, reached from BOTH arms — the Details arm through PinnedHero, every other body
    // through the cap slot below the header — which is what makes the video the same shape and the same width no
    // matter which body is showing. The single-surface invariant is unaffected: the two arms are mutually exclusive
    // (RightRail.Render returns from one or the other), so only ever one of them is mounted, and DockedVideoSurface's
    // own gate (DockedVideoHosting.ShouldMount) is a VALUE that does not mention the rail body at all.
    static Element DockedCap(ShellUi ui, bool docked, IAppSettings? settings, float railWidth)
    {
        void Commit()
        {
            float h = ShellResponsiveLayout.ClampDockedVideoHeight(ui.DockedVideoHeight.Peek(), ui.RailWidth.Peek());
            ui.DockedVideoHeight.Value = h;
            // A COMMITTED drag pins the height against the content fit for as long as this source plays (the surface
            // clears the pin at the next source). Set after the write, so the fit effect can never race it back.
            ui.DockedVideoHeightPinned.Value = true;
            settings?.Set(WaveeSettings.ShellDockedVideoHeight, h);
        }

        Element video = Embed.Comp(() => new DockedVideoSurface());
        if (!docked) return video;

        return new BoxEl
        {
            Direction = 1, ZStack = true, Shrink = 0f, ClipToBounds = true,
            Height = ui.DockedVideoHeight,
            Fill = Tok.MediaLetterbox,
            Children =
            [
                video,
                new BoxEl
                {
                    Grow = 1f, Direction = 1, Justify = FlexJustify.End, HitTestPassThrough = true,
                    Children =
                    [
                        Splitter.Create(ui.DockedVideoHeight, Commit, new()
                        {
                            Min = ShellResponsiveLayout.DockedVideoNaturalH(railWidth),
                            Max = ShellResponsiveLayout.DockedVideoMaxH,
                            Axis = SplitterAxis.Vertical,
                            ShowIndicator = false,
                        }),
                    ],
                },
            ],
        };
    }

    // Video mode's header: title + pop-out + fullscreen + the standard close (which closes the RAIL, not the video —
    // WaveeShell's RailVideoCoupling.OnRailClosed is what demotes a docked video to Floating on that edge, matching
    // every other mode's close). Falls back to a bare title+close when the bridge is not yet attached (mirrors every
    // other arm's null-tolerance elsewhere in this file).
    static Element[] VideoHeaderKids(ShellUi ui, PlaybackBridge? b) => b is null
        ? [TitleText(RailMode.Video), CloseButton(() => ui.RailOpen.Value = false)]
        :
        [
            TitleText(RailMode.Video),
            HeaderButton(Icons.BackToWindow, Loc.Get(Strings.Player.VideoMiniPlayer), () =>
            {
                Announcer.Say(Loc.Get(Strings.Player.VideoMiniPlayer));
                b.ShowVideoAt(SurfacePlacement.Floating);
            }),
            HeaderButton(Icons.FullScreen, Loc.Get(Strings.Player.VideoFullScreen), () =>
            {
                Announcer.Say(Loc.Get(Strings.Player.VideoFullScreen));
                b.ShowVideoAt(SurfacePlacement.Fullscreen);
            }),
            CloseButton(() => ui.RailOpen.Value = false),
        ];

    // The Details arm's pinned hero — the ONE slot, two possible occupants, never both:
    //
    //   docked  ⇒ THE DOCKED CARD, byte for byte the DockedCap the non-Details arm mounts (same splitter, same
    //             content-fit height, same rail width). This is the whole point of the change: the video follows its
    //             own aspect, full-bleed at the rail's width, in EVERY body. It used to be squeezed into a fixed
    //             square inset 8 DIP per side here — so a 16:9 stream that filled the card in Queue/Lyrics/Friends/
    //             Video sat in fat letterbox bars the moment the user switched to Details, silently changing both its
    //             shape and its width with the body.
    //   otherwise ⇒ the cover-art tile, with the 2-state Art|Video toggle laid over its top-right corner — but ONLY
    //             while video is dockable here. That reuses PlacementCore.Allows against the ONE resolved
    //             availability set, the same gate the video menu and PlayerBar's split button already read, so a
    //             track with no video (Available carries no Docked bit at all, VideoUpgradeGate.AvailabilityFor) or a
    //             window too narrow to dock leaves the tile bare.
    //
    // While the watch page's stage hosts the one surface, the rail hosts nothing at all: the bare art tile, exactly as
    // it looks for a track with no video. (This arm is only reachable when the SUBSTITUTED body is Details, so that is
    // belt-and-braces against the hero ever outliving the card it stands in for.)
    static Element PinnedHero(ShellUi ui, PlaybackBridge? b, bool stageHosts,
                              IAppSettings? settings, float railWidth)
    {
        Element art = Embed.Comp(() => new NowPlayingHeroTile());
        if (b is null || stageHosts) return art;
        var state = b.VideoSurface.Value;   // subscribe: follows availability + placement
        bool docked = PlacementCore.Resolve(state) == SurfacePlacement.Docked;
        if (docked) return DockedCap(ui, docked: true, settings, railWidth);
        if (!PlacementCore.Allows(state.Available, SurfacePlacement.Docked)) return art;
        // ZStack, not a Margin trick on the tile itself: NowPlayingHeroTile's own layout (its S-inset padding) is
        // untouched by having a sibling layer overlaid on top of it.
        return new BoxEl { ZStack = true, Children = [art, ArtVideoToggle(b)] };
    }

    // Rendered over the ART only — never over the docked card. While the video is docked here, the card's own
    // hover glyph strip already owns the way out (Icons.Cancel → NotifyVideoSurfaceClosed(Docked), the same sticky-off
    // path this toggle's Art half takes) and it occupies the very same top-right corner, so a second control stacked
    // there would be two overlapping affordances for one action. That is also why there is no `docked` parameter: the
    // Art half is always the unlit one here, because this control only exists in the un-docked state.
    //
    // Each half's tooltip is the action IT performs, unconditionally — the same "name of record" idiom
    // DockedVideoSurface's own glyph strip uses. "Art" is the sticky-off path (NotifyVideoSurfaceClosed, never
    // TurnVideoOff — see that method's own doc for why the stale-close identity guard matters), "Video" docks it;
    // both are scoped to THIS surface only.
    static Element ArtVideoToggle(PlaybackBridge b) => new BoxEl
    {
        Height = 24f, Shrink = 0f,
        AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.End,
        Margin = new Edges4(0f, Spacing.XS, Spacing.XS, 0f),
        Direction = 0, Corners = CornerRadius4.All(Radii.Control), ClipToBounds = true,
        Fill = WaveeOnMedia.GlassHover,
        Children =
        [
            ToggleHalf(Icons.Picture, selected: true, Loc.Get(Strings.Player.SwitchToAudio),
                () => b.NotifyVideoSurfaceClosed(SurfacePlacement.Docked)),
            ToggleHalf(Icons.Movie, selected: false, Loc.Get(Strings.Player.SwitchToVideo),
                () => b.ShowVideoAt(SurfacePlacement.Docked)),
        ],
    };

    static Element ToggleHalf(string glyph, bool selected, string tip, Action onClick) => ToolTip.Wrap(new BoxEl
    {
        Width = 24f, Height = 24f, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Corners = CornerRadius4.All(Radii.Control),
        Fill = selected ? Tok.OnMediaPrimary with { A = 0.16f } : ColorF.Transparent,
        Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false,
        Cursor = CursorId.Hand, OnClick = onClick,
        Children =
        [
            new TextEl(glyph)
            {
                Size = 11f, FontFamily = Theme.IconFont,
                Color = selected ? Tok.OnMediaPrimary : Tok.OnMediaSecondary,
            },
        ],
    }, tip);

    // The lyrics header: title · (secondary-line toggle) · (inspect) · expand · close.
    //
    // The inspector is DEVELOPER-ONLY and is its own component (LyricsInspectorButton) rather than a HeaderButton call
    // here, because it needs the overlay service and the playing track id — reading either from this header would
    // subscribe the WHOLE rail to track identity, re-rendering the panel chrome on every song change for a button that
    // only needs the id at click time.
    //
    // The secondary-line toggle is rendered ONLY when the document on screen actually carries a translation or a
    // romanization (LyricsPrefs.Available, published once per doc by LyricsView) — a permanently-present control that
    // does nothing on nine tracks out of ten is noise. Both signal reads are SUBSCRIPTIONS: Available so the button
    // appears the moment a document with the data lands, Epoch so the glyph's on/off state re-reads after a write from
    // here, from the immersive surface, or from the Settings picker.
    static Element[] LyricsHeaderKids(ShellUi ui, IAppSettings? settings)
    {
        int available = LyricsPrefs.Available.Value;
        _ = LyricsPrefs.Epoch.Value;
        int secondary = LyricsPrefs.Clamp(settings?.Get(WaveeSettings.LyricsSecondaryLine) ?? LyricsPrefs.None);

        // The inspector is DEVELOPER SURFACE (Settings ▸ Diagnostics ▸ Developer mode): a listener has no use for a
        // "which source won, and what did it carry" dump. The `.Value` read subscribes the header, so the button
        // appears and disappears with the switch; null means it is not composed at all, not composed-and-hidden.
        Element? inspect = DeveloperMode.Enabled.Value ? Embed.Comp(() => new LyricsInspectorButton()) : null;
        Element expand = HeaderButton(Icons.FullScreen, Loc.Get(Strings.Player.ExpandLyrics),
            () => ui.ImmersiveLyrics.Value = true);
        Element close = CloseButton(() => ui.RailOpen.Value = false);
        if (available == 0)
        {
            if (inspect is null) return [TitleText(RailMode.Lyrics), expand, close];
            return [TitleText(RailMode.Lyrics), inspect, expand, close];
        }

        // `active` is "a second line is actually on screen", not merely "the mode is non-zero": a persisted
        // romanization preference over a document that only carries a translation renders nothing, and an accented
        // glyph claiming otherwise would be the misleading half of the state.
        Element secondaryToggle = HeaderButton(Icons.Globe, LyricsPrefs.Tooltip(secondary),
            () => LyricsPrefs.Set(settings, LyricsPrefs.Next(secondary, available)),
            active: (available & LyricsPrefs.BitFor(secondary)) != 0);

        if (inspect is null) return [TitleText(RailMode.Lyrics), secondaryToggle, expand, close];
        return [TitleText(RailMode.Lyrics), secondaryToggle, inspect, expand, close];
    }

    // The rail's own title takes the shared rail-header alias (Subtitle 20/28/600) — the same run NowPlayingPanel's
    // section headers use, so the panel no longer has a 14/700 title sitting above 14/700 section headers.
    static Element TitleText(RailMode mode) => WaveeType.RailHeader(Title(mode)) with
    {
        Grow = 1f, MinWidth = 0f,
        Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis,
    };

    // A glyph button in the panel header — the CloseButton shape, with a tooltip because its glyph is not universal.
    // `active` is the STATEFUL variant (the secondary-line toggle): the accent tint is the only affordance a 32 DIP
    // glyph has to say "this is currently on", and the tooltip carries which layer it is.
    //
    // GEOMETRY: row 1 of WaveeCta's icon-button table — 32 × 32, Radii.Control, 16-DIP glyph. The glyph used to be 12,
    // which is not a rung of anything: it made a full-size button look like a shrunken one, and it disagreed with the
    // immersive lyrics surface's twin (36 box / 14 glyph) even though the two are the same control on two surfaces.
    internal static Element HeaderButton(string glyph, string tip, Action onClick, bool active = false) => ToolTip.Wrap(new BoxEl
    {
        Width = WaveeCta.IconButtonSize, Height = WaveeCta.IconButtonSize,
        Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Corners = CornerRadius4.All(Radii.Control),
        Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false,
        Cursor = CursorId.Hand, OnClick = onClick,
        Children =
        [
            new TextEl(glyph)
            {
                Size = HeaderGlyph, FontFamily = Theme.IconFont,
                Color = active ? Tok.AccentTextPrimary : Tok.TextSecondary,
                HoverColor = active ? Tok.AccentTextPrimary : Tok.TextPrimary,
            },
        ],
    }.Interactive(Interaction.Subtle), tip);

    static string Title(RailMode m) => m switch
    {
        RailMode.Lyrics => Loc.Get(Strings.Player.Lyrics),
        RailMode.Queue => Loc.Get(Strings.Player.Queue),
        RailMode.Friends => Loc.Get(Strings.Friends.Title),
        RailMode.Video => Loc.Get(Strings.Player.Video),
        _ => Loc.Get(Strings.Player.NowPlaying),
    };

    /// <summary>The shared glyph size for this header's buttons and the immersive surface's twin — WaveeCta's icon
    /// table pairs the 32-square rung with a 16-DIP glyph.</summary>
    internal const float HeaderGlyph = 16f;

    static Element CloseButton(Action onClick) => new BoxEl
    {
        Width = WaveeCta.IconButtonSize, Height = WaveeCta.IconButtonSize,
        Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Corners = CornerRadius4.All(Radii.Control),
        Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false,
        Cursor = CursorId.Hand, OnClick = onClick,
        Children = [new TextEl(Icons.ChromeClose) { Size = HeaderGlyph, FontFamily = Theme.IconFont, Color = Tok.TextSecondary, HoverColor = Tok.TextPrimary }],
    }.Interactive(Interaction.Subtle);
}
