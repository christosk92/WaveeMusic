using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Signals;
using Wavee.Core;

namespace Wavee;

/// <summary>
/// The stage's LEFT region: identity + transport, optically CENTRED in a fixed 352-DIP column on the wide stage and
/// folded into a single header ROW below <see cref="StageLayout.WideEnterW"/>.
///
/// <para><b>One structure, one reflow flag.</b> Both shapes are this one component reading
/// <see cref="StageLayout.Wide"/> — the detail hero's rule. Nothing here re-derives a breakpoint; every size comes off
/// the layout struct, and the folded-control set is <see cref="StageLayout.Folded"/> (shuffle, repeat, volume and the
/// output-device line move into the compact header's "…" — they are never lost).</para>
///
/// <para><b>Everything is a reuse.</b> The seek is the bar's own <c>SeekBar</c> (its Slider derivation is untouched —
/// the stage does not fork it), the times are the bar's <c>TimeText</c> in on-media ink, the volume is
/// <c>Slider.Create</c> bound to the same signal, the device line reads the same
/// <c>PlayerBarContent.RemoteDevice</c>/<c>LocalOutputs</c> pair <c>RemoteDeviceLine</c> does and opens the same
/// <c>DevicePickerMenu</c>, and every transport intent is the bridge command the bar already calls.</para>
///
/// <para><b>The quality badge names the PLAYING STREAM</b> — see <see cref="StageQualityBadge"/>. It was absent for a
/// long time on the grounds that nothing published the value; the pipeline always computed it (<c>PlaybackEvent</c>
/// carries <c>SelectedBitrateKbps</c> + <c>AudioFormatName</c>) but only Spotify telemetry subscribed. It is published
/// now, through <c>NowPlayingProjection</c> to <c>PlaybackBridge.StreamFormat</c>, with a clear-on-remote rule — so the
/// badge can be filled without claiming anything it cannot know. What it still refuses to be is unchanged: not the
/// user's quality PREFERENCE, and not the track's available format LADDER.</para>
/// </summary>
sealed class StageIdentity : Component
{
    readonly IReadSignal<StageLayout> _layout;

    /// <summary>The stage title's rung: 22 / 28 in the DISPLAY face at 650. It is a masthead, not a UI label — the same
    /// argument <c>WaveeType</c> makes for its three display-face 700s — and it is authored here rather than as a
    /// <c>WaveeType</c> alias because exactly one surface in the app speaks it.</summary>
    const float TitleSize = 22f, TitleLine = 28f;
    const ushort TitleWeight = 650;
    const string DisplayFace = "Segoe UI Variable Display";

    const float CompactTitleSize = 16f, CompactTitleLine = 22f;

    /// <summary>The column's internal gutter — <see cref="StageLayout.ColumnPadX"/>, which is where
    /// <see cref="StageLayout.ColumnContentW"/> (304) comes from.</summary>
    const float ColumnPadX = StageLayout.ColumnPadX;
    /// <summary>The column's vertical gutter, applied SYMMETRICALLY (top == bottom). The cluster is optically centred
    /// in its band, and an optical centre with a 28-DIP floor and a 0-DIP ceiling is not a centre — the free space
    /// above and below has to be the same before <c>FlexJustify.Center</c> can mean anything.</summary>
    const float ColumnPadY = 28f;
    const float StackGap = 18f;
    const float TransportGap = 6f;
    const float VolumeThickness = 20f;
    const float VolumeGlyph = 15f;

    /// <summary><c>Slider.Create</c> takes a track LENGTH, not a stretch — a NaN length is not "fill the row", it is a
    /// NaN width on every part of the slider template, which is what collapsed the volume rail to a thumb-sized dash.
    /// So the track is DERIVED: the column's content span, less the mute glyph and the gap beside it.</summary>
    static readonly float VolumeTrackW = StageLayout.ColumnContentW - WaveeCta.IconButtonSize - Spacing.S;

    /// <summary>The identity region's context-menu SHIELD (see <see cref="ContextShield"/>).</summary>
    const string ContextShieldKey = "stage:context-shield";

    public StageIdentity(IReadSignal<StageLayout> layout) => _layout = layout;

    public override Element Render()
    {
        var L = _layout.Value;                       // coarse band signal — not a per-pixel viewport subscription
        var b = UseContext(PlaybackBridge.Slot);
        var lib = UseContext(LibraryBridge.Slot);
        var go = UseContext(HistoryStore.NavCtx);
        var acts = UseContext(ActionServices.Slot);
        var menuOverlay = UseContext(Overlay.Service);

        if (b is null) return new BoxEl { Width = L.LayoutWidth, HitTestVisible = false };

        var track = b.CurrentTrack.Value;
        bool playing = b.IsPlaying.Value;
        bool loading = b.IsLoading.Value;
        string? err = b.Error.Value;
        bool shuffle = b.IsShuffle.Value;
        var repeat = b.Repeat.Value;
        bool saved = track is not null && (lib?.IsSaved(track.Uri) ?? false);
        bool canTransport = track is not null && err is null;
        bool primaryEnabled = track is not null && !loading;
        var accent = StageChrome.AccentFor(track);

        Action? like = track is { Uri.Length: > 0 } lt && lib is not null
            ? () => lib.ToggleSaved(lt.Uri, lt.Title)
            : null;

        Element body = L.Wide
            ? WideColumn(L, b, track, playing, canTransport, primaryEnabled, shuffle, repeat, saved, like, accent, go)
            : CompactHeader(L, b, track, playing, canTransport, primaryEnabled, shuffle, repeat, saved, like, accent, go);

        // Right-click / Menu key anywhere on the identity region — and the "⋯" button's ClickRequestsContext — raise the
        // SAME now-playing menu the player bar's cluster does. The factory Peeks at open time so it never serves the
        // render-time track capture.
        if (acts is { } a && menuOverlay is { } svc && body is BoxEl box)
            body = ContextScope(box, svc, () =>
                b.CurrentTrack.Peek() is { } now ? Menus.NowPlaying(a, now) : (ContextMenuModel?)null);

        return body;
    }

    // ── the hover/press SCOPE (the container trap, and why the menu is not attached to the column) ────────────────────
    //
    // THE DEFECT. Attaching the context menu straight to the column box made the whole cluster ONE hover family:
    // hovering the gap between two transport buttons lit the heart, and pressing anywhere lit EVERY button's plate at
    // once. Nothing in this file was wrong on its own — the mechanism is the engine's, and it is worth writing down:
    //   1. `OnContextRequested` sets InteractionInfo.ContextBit, and ContextBit is in InputDispatcher's `hitAnywhere`
    //      mask — an element with a context flyout is a hit-test target in its own right (the WinUI rule). So a pointer
    //      over the column's own BACKGROUND (a gap between controls; the row padding) resolved the COLUMN as the hit.
    //   2. `AnimScheduler.SetHover` then runs SetHoverDescendants from that node: a descendant that carries a
    //      reveal/scale affordance follows its CONTAINER's hover — and every StageChrome button carries HoverScale.
    //   3. `SetPress` used to be worse: SetPressDescendants recursed UNCONDITIONALLY, with no interactive boundary at
    //      all, so one press on the container drove PressTarget on every descendant with an interact row.
    //
    // (3) IS NOW FIXED IN THE ENGINE. SetPressDescendants carries the same nested-interactive boundary hover has: a
    // container press drives a boundary child (a button that owns an interact row still lights) and STOPS beneath it,
    // and the interact-row gate is the reach filter for everything else. `AnimSuite` gate 58c pins it. What that does
    // NOT fix is (1): the container is still the HIT, so it still owns the press/hover for every gap between controls,
    // and its own non-boundary reveal descendants still follow it. The shield below is therefore still load-bearing —
    // it is the answer to hit OWNERSHIP and to the hover cascade, not just to the press one.
    //
    // THE FIX, without losing right-click-anywhere. The menu goes on a SHELL (a ZStack) plus a childless full-bleed
    // SHIELD layer beneath the content:
    //   • the SHIELD always wins the hit over the shell's own box (InputDispatcher.Hit self-hits only when no child
    //     matched), and it has NO CHILDREN — so the hover/press cascade from it reaches exactly nothing;
    //   • the shell keeps ContextBit as an ANCESTOR, which is what the "⋯" button's ClickRequestsContext needs (the
    //     context funnel walks self-or-ancestors) and what carries a right-click on the art / title / a button up to the
    //     menu. An ancestor is safe: HoverWithin is published only for Pointer/Click/Pressed bits, never ContextBit, and
    //     the press target is the deepest HIT node, which the shield now is.
    //
    // THE CONTRACT, for anything added here later: NO stage container that has several interactive descendants may own a
    // pointer/click/press handler. Handlers live on the leaf controls; a container that must own one contains only its
    // own reveal affordance. The shield is the one exception and it is allowed precisely because it is childless.
    static BoxEl ContextScope(BoxEl content, IOverlayService svc, Func<ContextMenuModel?> factory) => new BoxEl
    {
        ZStack = true, MinHeight = 0f,
        // The shell inherits the content's participation in the parent flex; the content becomes a stretched layer.
        Width = content.Width, Grow = content.Grow, Shrink = content.Shrink,
        Children = [ContextShield(svc, factory), content with { Width = float.NaN, Grow = 0f, Shrink = 0f }],
    }.WithContextMenu(svc, factory);

    /// <summary>The shield: ONE childless, full-bleed layer that takes the hit anywhere the content does not, so the
    /// container above it is never the hover/press target. It must stay childless — see the note on
    /// <see cref="ContextScope"/>; <c>StageLayoutTests</c> pins the literal construction below.</summary>
    static BoxEl ContextShield(IOverlayService svc, Func<ContextMenuModel?> factory) =>
        new BoxEl { Key = ContextShieldKey }.WithContextMenu(svc, factory);

    // ── WIDE: the fixed, bottom-anchored column ──────────────────────────────────────────────────────────────────────

    BoxEl WideColumn(in StageLayout L, PlaybackBridge b, Track? track, bool playing, bool canTransport,
                     bool primaryEnabled, bool shuffle, RepeatMode repeat, bool saved, Action? like, ColorF accent,
                     Action<string, string?>? go)
    {
        // The cascade index. The COVER is deliberately not on it: a 300-square that rises and fades reads as the whole
        // surface arriving late, where the type and the controls arriving in sequence reads as the surface composing
        // itself. (WaveeEntrance.Row is capped and reduced-motion-safe by construction.)
        int rung = 0;
        var kids = new List<Element>(7)
        {
            new BoxEl
            {
                Key = "stage:art",
                Width = L.ArtSize, Height = L.ArtSize, Shrink = 0f,
                Corners = Radii.CardAll,
                Shadow = Elevation.Dialog,
                Margin = new Edges4(0f, 0f, 0f, StackGap),
                Children = [Surfaces.Artwork(track?.Image, SeedOf(track), L.ArtSize, L.ArtSize, Radii.Card, decodePx: 512)],
            },
            // Every wrapper below is Direction = 1 ON PURPOSE. A BoxEl defaults to a ROW, and a row's single child takes
            // its INTRINSIC main-axis size — which is what made the seek bar a ~120-DIP stub, the volume rail a dash and
            // the "0:15  3:20" pair collapse into "0:15-3:20" (its Grow spacer had no space to spread). As columns, the
            // wrapper's cross axis is horizontal and the default AlignItems = Stretch gives each row the full 304.
            new BoxEl
            {
                Key = "stage:identity-row", Direction = 1, Animate = WaveeEntrance.Row(rung++),
                Children = [IdentityRow(track, saved, like, accent, go, wide: true)],
            },
            new BoxEl
            {
                Key = "stage:seek", Direction = 1, Animate = WaveeEntrance.Row(rung++),
                Margin = new Edges4(0f, StackGap, 0f, 0f),
                Children = [SeekBlock(b)],
            },
            new BoxEl
            {
                Key = "stage:transport", Direction = 1, Animate = WaveeEntrance.Row(rung++),
                Margin = new Edges4(0f, Spacing.S, 0f, 0f),
                Children = [TransportRow(in L, b, playing, canTransport, primaryEnabled, shuffle, repeat, accent)],
            },
        };
        if (L.ShowVolume)
            kids.Add(new BoxEl
            {
                Key = "stage:volume", Direction = 1, Animate = WaveeEntrance.Row(rung++),
                Margin = new Edges4(0f, StackGap, 0f, 0f),
                Children = [VolumeRow(b)],
            });
        if (L.ShowDeviceLine)
            kids.Add(new BoxEl
            {
                Key = "stage:device", Direction = 1, Animate = WaveeEntrance.Row(rung),
                Margin = new Edges4(0f, Spacing.S, 0f, 0f),
                Children = [Embed.Comp(() => new StageDeviceLine(b))],
            });

        return new BoxEl
        {
            // The BOX is the designed column plus the gutter that keeps the pane off its type; the padding puts the
            // content back inside the designed 352. (The dark SHADE under the column is not here any more — it is a
            // full-bleed layer in the backdrop stack, so it can feather more than twice as far for free.)
            Width = L.LayoutWidth, Shrink = 0f,
            // CENTRED, and Grow is what makes that true. A component's element is mounted UNDER a host node whose own
            // layout is the scene default (a COLUMN, Grow 0), so this box was taking its MEASURED height and sitting at
            // the top of a full-height host: Justify had no free space to distribute and was silently inert, which is
            // why the cover read as pinned top-left with the window's whole lower half empty. Growing into the host's
            // height gives Justify something to spend.
            //
            // The Grow is VERTICAL and only vertical: the surface's band owns this region's horizontal participation
            // (see the wrapper note in ImmersiveLyricsSurface.StageBody), because the anchor mirrors this element's
            // FlexGrow and the wide band is a ROW, where the same number would have read as "and half the free width".
            //
            // ANCHORING: the cluster sits in the OPTICAL centre of its column rather than on the floor. Bottom-anchored
            // put the cover's top edge under the caption band on a short window and left a 300-square of dead scrim
            // above it on a tall one; the padding is symmetric (ColumnPadY both ends) so Center is a real centre.
            Grow = 1f, MinHeight = 0f,
            Direction = 1, Justify = FlexJustify.Center,
            // SYMMETRIC on both axes now. The right side used to carry `L.ColumnFalloff + ColumnPadX` — 120 DIP of
            // padding whose only job was to cancel a 120-DIP falloff the box added to itself. Both are gone; the air
            // beside the column is StageLayout.RegionGapW, spent by the band as a real Gap. ColumnContentW is still
            // 304, so nothing inside the column moves.
            Padding = new Edges4(ColumnPadX, ColumnPadY, ColumnPadX, ColumnPadY),
            Children = kids.ToArray(),
        };
    }

    // ── COMPACT: the header row + a full-width seek under it ─────────────────────────────────────────────────────────
    // The seek is NOT on the fold list (StageLayout.CompactFold folds shuffle, repeat, volume and the device line, and
    // nothing else): a now-playing surface you cannot scrub is not a now-playing surface.
    BoxEl CompactHeader(in StageLayout L, PlaybackBridge b, Track? track, bool playing, bool canTransport,
                        bool primaryEnabled, bool shuffle, RepeatMode repeat, bool saved, Action? like, ColorF accent,
                        Action<string, string?>? go)
    {
        var row = new List<Element>(7)
        {
            new BoxEl
            {
                Key = "stage:art",
                Width = L.ArtSize, Height = L.ArtSize, Shrink = 0f,
                Corners = Radii.ControlAll, Shadow = Elevation.Card,
                Children = [Surfaces.Artwork(track?.Image, SeedOf(track), L.ArtSize, L.ArtSize, Radii.Control, decodePx: 192)],
            },
            IdentityRow(track, saved, like, accent, go, wide: false) with { Key = "stage:identity-row" },
            StageChrome.Glyph(Icons.Previous, () => { _ = b.Player.PreviousAsync(); }, L.StepBox, 15f, canTransport)
                with { Key = "stage:prev" },
            StageChrome.Play(playing ? Icons.Pause : Icons.Play, () => PlayerBarContent.TogglePlayPause(b),
                primaryEnabled, L.PlayBox, 17f) with { Key = "stage:play" },
            StageChrome.Glyph(Icons.Next, () => { _ = b.Player.NextAsync(); }, L.StepBox, 15f, canTransport)
                with { Key = "stage:next" },
        };
        if (L.ShowOverflow)
            row.Add(Embed.Comp(() => new StageOverflowButton(b, _layout)) with { Key = "stage:overflow" });

        return new BoxEl
        {
            // No veil of its own: the scrim's top deepening is what this header sits on, and it resolves across a
            // feather many times the header's height instead of ending at a boxed edge.
            Direction = 1, Shrink = 0f,
            Padding = new Edges4(Spacing.L, Spacing.S, Spacing.L, Spacing.S),
            Children =
            [
                new BoxEl
                {
                    Key = "stage:header-row", Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M,
                    Animate = WaveeEntrance.Row(0),
                    Children = row.ToArray(),
                },
                new BoxEl
                {
                    // Direction = 1 for the same reason the wide column's wrappers are — see the note there. A row
                    // wrapper would hand the seek block its intrinsic width instead of the header's.
                    Key = "stage:seek", Direction = 1, Animate = WaveeEntrance.Row(1),
                    Margin = new Edges4(0f, Spacing.XS, 0f, 0f),
                    Children = [SeekBlock(b)],
                },
            ],
        };
    }

    // ── the identity row: title · artist — album · heart · "…" ───────────────────────────────────────────────────────

    Element IdentityRow(Track? track, bool saved, Action? like, ColorF accent, Action<string, string?>? go, bool wide)
    {
        string title = track is { Title.Length: > 0 } t && t.Title != t.Uri ? t.Title : Loc.Get(Strings.Player.NothingPlaying);

        var meta = new BoxEl
        {
            Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Justify = FlexJustify.Center, Gap = Spacing.XXS,
            ClipToBounds = true,
            Children =
            [
                new TextEl(title)
                {
                    Size = wide ? TitleSize : CompactTitleSize,
                    LineHeight = wide ? TitleLine : CompactTitleLine,
                    Weight = TitleWeight, FontFamily = DisplayFace,
                    Color = StageInk.Ink,
                    Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                },
                Embed.Comp(() => new StageMetaLink(go)),
            ],
        };

        var kids = new List<Element>(3) { meta, StageChrome.Heart(saved, like, accent) };
        if (wide)
            kids.Add(new BoxEl
            {
                Width = WaveeCta.IconButtonSize, Height = WaveeCta.IconButtonSize, Shrink = 0f,
                Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Corners = Radii.ControlAll,
                Fill = StageInk.GlassRest, HoverFill = StageInk.GlassHover, PressedFill = StageInk.GlassPressed,
                BrushTransitionMs = WaveeMotion.Faster,
                Role = AutomationRole.Button, Cursor = CursorId.Hand, Focusable = true, AllowFocusOnInteraction = false,
                // The engine's declarative re-entry into the context funnel: the ancestor's WithContextMenu opens
                // byte-identically to a right-click (no node capture, no second menu model).
                ClickRequestsContext = true,
                Children = [new TextEl(Icons.More) { Size = 16f, FontFamily = Theme.IconFont, Color = StageInk.InkSecondary, HoverColor = StageInk.Ink }],
            });

        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, MinWidth = 0f,
            Grow = wide ? 0f : 1f, Basis = wide ? float.NaN : 0f, Shrink = 1f,
            Children = kids.ToArray(),
        };
    }

    // ── the seek block: the bar's own SeekBar + the elapsed / QUALITY / remaining row, in on-media ink ────────────────

    static Element SeekBlock(PlaybackBridge b) => new BoxEl
    {
        Direction = 1, Gap = Spacing.XXS, MinWidth = 0f,
        Children =
        [
            Embed.Comp(() => new SeekBar(b)),
            new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, MinWidth = 0f,
                Children =
                [
                    Embed.Comp(() => new TimeText(b, remaining: false, ink: StageInk.InkTertiary)),
                    new BoxEl { Grow = 1f, MinWidth = 0f, HitTestVisible = false },
                    Embed.Comp(() => new StageQualityBadge(b)),
                    new BoxEl { Grow = 1f, MinWidth = 0f, HitTestVisible = false },
                    Embed.Comp(() => new TimeText(b, remaining: true, ink: StageInk.InkTertiary)),
                ],
            },
        ],
    };

    // ── the transport row: 32 · 40 · 56 · 40 · 32, centred ───────────────────────────────────────────────────────────

    static Element TransportRow(in StageLayout L, PlaybackBridge b, bool playing, bool canTransport, bool primaryEnabled,
                                bool shuffle, RepeatMode repeat, ColorF accent)
    {
        var kids = new List<Element>(5);
        if (L.ShowSatellites)
            kids.Add(ToolTip.Wrap(
                StageChrome.Satellite(Icons.Shuffle, () => PlayerBarContent.ToggleShuffle(b), canTransport, shuffle,
                    accent, L.SatelliteBox, 14f),
                Loc.Get(Strings.Player.Shuffle)) with { Key = "tp:shuffle" });
        kids.Add(ToolTip.Wrap(
            StageChrome.Glyph(Icons.Previous, () => { _ = b.Player.PreviousAsync(); }, L.StepBox, 17f, canTransport),
            Loc.Get(Strings.Player.Previous)) with { Key = "tp:prev" });
        kids.Add(StageChrome.Play(playing ? Icons.Pause : Icons.Play, () => PlayerBarContent.TogglePlayPause(b),
            primaryEnabled, L.PlayBox, 22f) with { Key = "tp:play" });
        kids.Add(ToolTip.Wrap(
            StageChrome.Glyph(Icons.Next, () => { _ = b.Player.NextAsync(); }, L.StepBox, 17f, canTransport),
            Loc.Get(Strings.Player.Next)) with { Key = "tp:next" });
        if (L.ShowSatellites)
            kids.Add(ToolTip.Wrap(
                StageChrome.Satellite(repeat == RepeatMode.Track ? Icons.RepeatOne : Icons.RepeatAll,
                    () => PlayerBarContent.CycleRepeat(b), canTransport, repeat != RepeatMode.Off, accent,
                    L.SatelliteBox, 14f),
                Loc.Get(Strings.Player.Repeat)) with { Key = "tp:repeat" });

        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Gap = TransportGap,
            Children = kids.ToArray(),
        };
    }

    // ── the volume row: the STOCK slider, recoloured onto the on-media ladder ────────────────────────────────────────

    static Element VolumeRow(PlaybackBridge b) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, MinWidth = 0f,
        Children =
        [
            Embed.Comp(() => new StageVolumeGlyph(b)),
            // The track is an authored LENGTH (see VolumeTrackW): Slider.Create has no stretch mode, and the NaN that
            // sat here propagated into every Width in its template — which is the whole of the "volume is a tiny dash"
            // report. Grow still lets the row absorb any rounding, but the rail's size is the derived number.
            new BoxEl
            {
                Grow = 1f, Shrink = 1f, MinWidth = 0f,
                Children =
                [
                    Slider.Create(b.Volume, v => { _ = b.Player.SetVolumeAsync(v); },
                        options: VolumeOptions, length: VolumeTrackW, thickness: VolumeThickness, style: OnMediaSlider()),
                ],
            },
        ],
    };

    static readonly Slider.SliderOptions VolumeOptions = new()
    {
        ThumbToolTipValueConverter = static value => $"{Math.Clamp((int)MathF.Round(value * 100f), 0, 100)}%",
    };

    /// <summary>The stock <see cref="Slider.DefaultStyle"/> with its ink moved onto the on-media ladder — a RECOLOUR,
    /// not a fork: every metric (track height, thumb ring, the inner-dot storyboard scales) stays exactly where WinUI
    /// put it, and the seek bar's own derivation is untouched.</summary>
    static Slider.Style OnMediaSlider()
    {
        var s = Slider.DefaultStyle;
        ColorF ink = StageInk.Ink;
        return s with
        {
            RailFill = ink with { A = 0.26f },
            RailFillDisabled = ink with { A = 0.14f },
            ValueFill = ink,
            ValueFillPointerOver = ink with { A = 0.90f },
            ValueFillPressed = ink with { A = 0.80f },
            ValueFillDisabled = ink with { A = 0.32f },
            ThumbRing = ink,
            ThumbFill = ink,
            ThumbFillPointerOver = ink with { A = 0.90f },
            ThumbFillPressed = ink with { A = 0.80f },
            ThumbFillDisabled = ink with { A = 0.32f },
            ThumbBorder = GradientSpec.Solid(StageInk.Stroke),
        };
    }

    static int SeedOf(Track? t) => t is null ? 11 : Math.Abs((t.Uri ?? t.Id).Length * 7 + t.Title.Length);

    /// <summary>The mute glyph beside the volume rail. Its OWN component so the 0-crossing glyph swap does not
    /// re-render the whole column during a volume drag (the rail beside it is compositor-only).</summary>
    sealed class StageVolumeGlyph : Component
    {
        readonly PlaybackBridge _b;
        public StageVolumeGlyph(PlaybackBridge b) => _b = b;

        public override Element Render()
        {
            float v = _b.Volume.Value;
            bool muted = _b.OutputMuted.Value || v <= 0.001f;
            return ToolTip.Wrap(
                StageChrome.Glyph(muted ? Icons.Mute : Icons.Volume, () => VolumeButton.ToggleMute(_b),
                    WaveeCta.IconButtonSize, VolumeGlyph),
                Loc.Get(muted ? Strings.Player.Unmute : Strings.Player.Mute));
        }
    }

    /// <summary>"Artist — Album" on one line: a hover-UNDERLINED link to the artist page. Its own component because the
    /// hover has to scope to this word rather than to the identity row (the hover-container trap), and because the
    /// underline is a per-render <c>TextEl.Underline</c> rather than a bound channel.</summary>
    sealed class StageMetaLink : Component
    {
        readonly Action<string, string?>? _go;
        public StageMetaLink(Action<string, string?>? go) => _go = go;

        public override Element Render()
        {
            var b = UseContext(PlaybackBridge.Slot);
            var hover = UseSignal(false);
            var track = b?.CurrentTrack.Value;
            if (track is null) return new BoxEl { Height = 0f, HitTestVisible = false };

            string artists = track.Artists.Count > 0 ? DetailFormat.ArtistNames(track.Artists) : "";
            string album = track.Album is { Name.Length: > 0 } al ? al.Name : "";
            string line = artists.Length > 0 && album.Length > 0 ? artists + " — " + album
                        : artists.Length > 0 ? artists
                        : album;
            if (line.Length == 0) return new BoxEl { Height = 0f, HitTestVisible = false };

            // Resolve the route at INVOKE time: this node outlives every track change. WHERE it goes is
            // PlayableLinks' answer — a module playable's subtitle names a channel/station, not a credited artist, and
            // the old `Artists[0].Uri.Length > 0` gate left every one of them dead.
            bool enabled = PlayableLinks.RouteFor(track, LinkSlot.Artist) is { Length: > 0 };
            void Nav()
            {
                var now = b?.CurrentTrack.Peek();
                if (PlayableLinks.RouteFor(now, LinkSlot.Artist) is { } route)
                    _go?.Invoke(route, PlayableLinks.LabelFor(now, LinkSlot.Artist));
            }

            return new BoxEl
            {
                MinWidth = 0f, Shrink = 1f, ClipToBounds = true,
                Cursor = enabled ? CursorId.Hand : (CursorId?)null,
                OnClick = enabled ? Nav : null,
                OnHoverMove = enabled ? _ => { if (!hover.Peek()) hover.Value = true; } : null,
                OnPointerExit = enabled ? () => { if (hover.Peek()) hover.Value = false; } : null,
                Role = enabled ? AutomationRole.Hyperlink : AutomationRole.Text,
                Focusable = enabled, AllowFocusOnInteraction = false,
                Children =
                [
                    new TextEl(line)
                    {
                        Size = 14f, LineHeight = 20f, Weight = 400,
                        Color = enabled && hover.Value ? StageInk.Ink : StageInk.InkSecondary,
                        Underline = enabled && hover.Value,
                        Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                    },
                ],
            };
        }
    }

    /// <summary>The times row's centre slot: what the PLAYING STREAM actually is ("FLAC", "Vorbis 320 kbps").
    ///
    /// <para><b>This slot was empty for a reason, and the reason is now gone.</b> The pipeline always computed the
    /// value — <c>PlaybackController</c> resolves bitrate + format at the load chokepoint and folds both into
    /// <c>PlaybackEvent</c> — but nothing PUBLISHED it, so the only honest thing to mount was nothing. It is published
    /// now (<c>NowPlayingProjection</c> → <see cref="PlaybackBridge.StreamFormat"/>), so the slot can be filled without
    /// the badge saying anything it cannot know.</para>
    ///
    /// <para><b>The two things it still refuses to be.</b> Not the user's quality PREFERENCE (that is what was asked
    /// for, not what arrived) and not the track's available format LADDER (that is what exists, not what is decoding).
    /// Either would be a plausible lie, which is worse than an empty slot.</para>
    ///
    /// <para><b>It renders NOTHING rather than guessing.</b> No format published, or another Connect device is active
    /// ⇒ no badge. Remote playback is resolved on that device and this machine cannot describe its stream; the
    /// projection clears the value on the same fold that sets the active device, and this is the second half of that
    /// rule. Silence is the correct answer, not a fallback.</para></summary>
    sealed class StageQualityBadge : Component
    {
        readonly PlaybackBridge _b;
        public StageQualityBadge(PlaybackBridge b) => _b = b;

        public override Element Render()
        {
            string? fmt = _b.StreamFormat.Value;
            string? remote = _b.ActiveDeviceId.Value;
            if (fmt is not { Length: > 0 } || remote is { Length: > 0 }) return new BoxEl { HitTestVisible = false };

            return new TextEl(fmt)
            {
                Size = 11.5f, LineHeight = 16f, Weight = 600,
                Color = StageInk.InkTertiary,
                // The stage has no caps ROLE — the case is authored in the loc catalogue / the provider's own label,
                // never produced by a transform here (NoStageFile_CapsTransformsItsText).
                Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Shrink = 0f,
                Margin = new Edges4(Spacing.S, 0f, Spacing.S, 0f),
            };
        }
    }

    /// <summary>The output-device line: a speaker glyph plus the name of whatever is actually playing this — a Connect
    /// device when playback is remote (the SAME <c>PlayerBarContent.RemoteDevice</c> source <c>RemoteDeviceLine</c>
    /// reads, so the two can never disagree), otherwise the selected local endpoint or the system default. Clicking it
    /// opens the SAME two-section <c>DevicePickerMenu</c> the bar's Devices button opens.</summary>
    sealed class StageDeviceLine : Component
    {
        readonly PlaybackBridge _b;
        public StageDeviceLine(PlaybackBridge b) => _b = b;

        public override Element Render()
        {
            var anchor = UseRef<NodeHandle>(default);
            var handle = UseRef<OverlayHandle?>(null);
            var svc = UseContext(Overlay.Service);

            var remote = PlayerBarContent.RemoteDevice(_b);           // subscribes to Devices + ActiveDeviceId
            string? selectedLocal = _b.LocalOutputs?.SelectedOutputId.Value;
            var localRoster = _b.LocalOutputs?.Devices.Value;

            string name;
            string glyph;
            if (remote is not null)
            {
                name = Strings.Player.PlayingOn(remote.Name);
                glyph = Icons.Devices;
            }
            else
            {
                LocalAudioDevice? sel = null;
                if (selectedLocal is { Length: > 0 } id && localRoster is not null)
                    foreach (var d in localRoster) { if (d.Id == id) { sel = d; break; } }
                name = sel?.Name ?? Loc.Get(Strings.Player.SystemDefault);
                glyph = sel?.Kind switch
                {
                    LocalAudioDeviceKind.Headphones or LocalAudioDeviceKind.Headset => Icons.Headphones,
                    LocalAudioDeviceKind.Hdmi => Icons.TvMonitor,
                    _ => Icons.Speakers,
                };
            }

            void Toggle()
            {
                if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
                handle.Value = svc.Open(
                    () => anchor.Value,
                    () => Embed.Comp(() => new DevicePickerMenu(_b, () => handle.Value?.Close())),
                    FlyoutPlacement.TopEdgeAlignedLeft,
                    new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss) { ConstrainToRootBounds = false });
                handle.Value.ClosedAction = () => handle.Value = null;
            }

            return new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, MinHeight = 24f, MinWidth = 0f,
                Padding = new Edges4(Spacing.XS, 0f, Spacing.S, 0f),
                Corners = Radii.ControlAll,
                Fill = StageInk.GlassRest, HoverFill = StageInk.GlassHover, PressedFill = StageInk.GlassPressed,
                BrushTransitionMs = WaveeMotion.Faster,
                ClipToBounds = true,
                Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false,
                Cursor = CursorId.Hand, OnClick = Toggle, OnRealized = h => anchor.Value = h,
                Children =
                [
                    new TextEl(glyph)
                    {
                        Size = 12f, FontFamily = Theme.IconFont,
                        Color = remote is null ? StageInk.InkTertiary : StageInk.InkSecondary,
                        HoverColor = StageInk.Ink,
                    },
                    new TextEl(name)
                    {
                        Size = 12f, LineHeight = 16f, Weight = 400,
                        Color = remote is null ? StageInk.InkTertiary : StageInk.InkSecondary,
                        HoverColor = StageInk.Ink,
                        MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f,
                    },
                ],
            };
        }
    }

    /// <summary>The compact header's "…": the folded controls, live. It is not a decoration — shuffle, repeat, mute and
    /// the whole device picker are all here, so <see cref="StageLayout.CompactFold"/> means "moved address", not "lost".</summary>
    sealed class StageOverflowButton : Component
    {
        readonly PlaybackBridge _b;
        readonly IReadSignal<StageLayout> _layout;
        public StageOverflowButton(PlaybackBridge b, IReadSignal<StageLayout> layout) { _b = b; _layout = layout; }

        public override Element Render()
        {
            var anchor = UseRef<NodeHandle>(default);
            var handle = UseRef<OverlayHandle?>(null);
            var svc = UseContext(Overlay.Service);
            var L = _layout.Value;

            void Toggle()
            {
                if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
                // Built at OPEN time so the rows read the live state without subscribing the header to hot signals.
                var items = new List<MenuFlyoutItem>(8);
                if (!L.Shows(StageControl.Shuffle))
                    items.Add(MenuFlyoutItem.Toggle(Loc.Get(Strings.Player.Shuffle), _b.IsShuffle.Peek(),
                        () => PlayerBarContent.ToggleShuffle(_b), Icons.Shuffle));
                if (!L.Shows(StageControl.Repeat))
                {
                    var r = _b.Repeat.Peek();
                    items.Add(MenuFlyoutItem.Toggle(Loc.Get(Strings.Player.Repeat), r != RepeatMode.Off,
                        () => PlayerBarContent.CycleRepeat(_b),
                        r == RepeatMode.Track ? Icons.RepeatOne : Icons.RepeatAll));
                }
                if (!L.Shows(StageControl.Volume))
                {
                    bool muted = _b.OutputMuted.Peek() || _b.Volume.Peek() <= 0.001f;
                    items.Add(new MenuFlyoutItem(Loc.Get(muted ? Strings.Player.Unmute : Strings.Player.Mute),
                        muted ? Icons.Volume : Icons.Mute, true, () => VolumeButton.ToggleMute(_b)));
                }
                if (!L.Shows(StageControl.OutputDevice))
                {
                    if (items.Count > 0) items.Add(MenuFlyoutItem.Separator);
                    items.AddRange(PlayerBarContent.DevicePickerItems(_b));
                }
                if (items.Count == 0) return;

                handle.Value = svc.Open(
                    () => anchor.Value,
                    () => MenuFlyout.Create(items, () => handle.Value?.Close()),
                    FlyoutPlacement.BottomEdgeAlignedRight,
                    new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss) { ConstrainToRootBounds = false });
                handle.Value.ClosedAction = () => handle.Value = null;
            }

            return ToolTip.Wrap(
                StageChrome.Glyph(Icons.More, Toggle, WaveeCta.IconButtonSize, 16f, onRealized: h => anchor.Value = h),
                Loc.Get(Strings.Player.NowPlaying));
        }
    }
}
