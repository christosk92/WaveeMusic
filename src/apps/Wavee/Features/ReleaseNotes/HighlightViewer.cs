using System;
using System.Collections.Generic;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Core.ReleaseNotes;
using static FluentGpu.Dsl.Ui;
using VL = Wavee.Core.ReleaseNotes.HighlightViewerLayout;

namespace Wavee;

/// <summary>The modal, one-highlight-at-a-time viewer a highlight card opens: the poster large, the body whole
/// (never the card's 4-line-and-a-fade slot), prev/next chevrons and a pip pager when there is more than one.
/// One component, two hosts (design §B.7) — <c>AfterUpdateDialog</c> passes its own close as <c>closeHost</c> so
/// "Try it" leaves both plates; <c>ReleaseNotesPage</c> passes null.
///
/// <para>Opened the way <c>ArtistPage.OpenGallery</c> opens the photo lightbox: an anchor-less overlay
/// (<c>NodeHandle.Null</c>) with <c>PopupChrome.Modal</c> for the ContentDialog scale/fade and the focus trap, but
/// <c>ScrimVisual = false</c> — the view paints its OWN veil instead of stacking a second smoke on top of the
/// chrome's, and <c>DismissBehavior.LightDismiss</c> (not Modal) because a veil click is exactly how a viewer is
/// meant to close.</para></summary>
static class HighlightViewer
{
    public static OverlayHandle Open(IOverlayService overlay, IReadOnlyList<HighlightItem> items, int initial,
                                     Action<string, string?>? nav, Action? closeHost)
    {
        OverlayHandle? handle = null;                     // the ArtistPage.OpenGallery self-reference idiom
        handle = overlay.Open(
            static () => NodeHandle.Null,
            () => Embed.Comp(() => new HighlightViewerView(items, initial, nav, closeHost, () => handle)),
            FlyoutPlacement.BottomCenter,
            new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Modal)
                { ScrimVisual = false });
        return handle;
    }
}

/// <summary>The viewer's live state: which slide is showing, the pager's controlled mirror of it, and the direction
/// the NEXT step travels (a plain field — see <see cref="_dir"/>).</summary>
sealed class HighlightViewerView : Component
{
    // rgba(0,0,0,.72), a LOCAL literal rather than Tok.MediaScrim (.55): MediaScrim is tuned to read over a photo, and
    // .55 looks thin stacked over the dialog host's own smoke when the viewer opens ON TOP of AfterUpdateDialog.
    static readonly ColorF Veil = ColorF.FromRgba(0, 0, 0, 184);
    // The chevrons' two extra states (design §B.2): dimmed-in-place at an end, and the hover fill on-media circles
    // share. Both ride Tok.MediaScrim's RGB so they read as the same chrome family as the play glyph.
    static readonly ColorF ChromeDimmed = Tok.MediaScrim with { A = 0.40f };
    static readonly ColorF ChromeHover = Tok.MediaScrim with { A = 0.70f };

    readonly IReadOnlyList<HighlightItem> _items;
    readonly Action<string, string?>? _nav;
    readonly Action? _closeHost;
    readonly Func<OverlayHandle?> _handle;

    readonly Signal<int> _index;
    // The pager's CONTROLLED value, mirrored FROM _index by a UseEffect in Render — never written during render (the
    // ArtistPopular.ChartPager rule). PipsPager roves keyboard FOCUS across pips itself; this is only the selection.
    readonly Signal<int> _pip = new(0);
    // Motion-only: which way the NEXT slide travels. A plain FIELD, not a Signal — a motion-only value must never
    // trigger a render of its own, and it only needs to be read by the render Go() already triggers via _index.Value.
    HighlightSlideDirection _dir = HighlightSlideDirection.None;

    public HighlightViewerView(IReadOnlyList<HighlightItem> items, int initial, Action<string, string?>? nav,
                               Action? closeHost, Func<OverlayHandle?> handle)
    {
        _items = items;
        _nav = nav;
        _closeHost = closeHost;
        _handle = handle;
        _index = new(Math.Clamp(initial, 0, Math.Max(0, items.Count - 1)));
    }

    public override Element Render()
    {
        var vp = UseContext(Viewport.Size);
        int count = _items.Count;
        int index = count == 0 ? 0 : Math.Clamp(_index.Value, 0, count - 1);
        // Controlled pager: mirror the index truth into the pip signal from an EFFECT, never a render-time write —
        // the pips write BACK only through onChange, so Go() has already set _dir before the index ever moves.
        UseEffect(() => { if (_pip.Peek() != index) _pip.Value = index; }, index);

        if (count == 0) return new BoxEl();   // defensive: Open() only ever mounts this over a non-empty list

        var item = _items[index];
        var h = item.Highlight;
        bool store = HighlightVisibility.IsStore(h);
        bool video = HighlightCard.IsVideo(h);
        string id = SlideId(item, index);

        // §B.1: W = clamp(320, min(960, vpW-96, (vpH-360)*16/9)); the band is W*9/16 (a flat 120 with no poster to
        // derive from), and the plate never exceeds vpH-64 — past that the TEXT scrolls, the image never does.
        float w = VL.PlateWidth(vp.Width, vp.Height);
        float imgH = VL.ImageHeight(w, item.Poster is { Length: > 0 });
        float maxH = VL.PlateMaxHeight(vp.Height);

        return new BoxEl
        {
            Grow = 1f, ZStack = true,
            Justify = FlexJustify.Center, AlignItems = FlexAlign.Center,   // centers the plate; the veil below overrides both axes to fill instead
            Focusable = true, OnKeyDown = OnKeys,
            Children =
            [
                new BoxEl
                {
                    AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch,
                    // The ONLY light-dismiss surface: the plate paints OVER this in z-order, so a click anywhere on
                    // the plate never reaches this handler — no HitTestPassThrough gymnastics needed.
                    Fill = Veil, OnClick = Close,
                },
                Plate(item, h, id, w, maxH, imgH, index, count, store, video),
            ],
        };
    }

    // ── navigation ───────────────────────────────────────────────────────────────────────────────────────────────

    void Go(HighlightStep step)
    {
        if (step.Direction == HighlightSlideDirection.None) return;
        _dir = step.Direction;          // read by the render the next line triggers
        _index.Value = step.Index;
    }

    /// <summary>Root keys. Left/Right step (clamped), Home/End jump, Escape is the overlay's. Every recognised key is
    /// marked handled even when it is a no-op, so a single-highlight viewer never leaks arrows to the page under the
    /// veil. A key that arrives already handled came from a focused pip — PipsPager roves pip FOCUS on Left/Right and
    /// marks it handled itself, which is the WinUI contract this defers to.</summary>
    void OnKeys(KeyEventArgs e)
    {
        if (e.Handled) return;
        HighlightNavKey key;
        switch (e.KeyCode)                  // int; Keys.* are const ints, so these are constant cases
        {
            case Keys.Left: key = HighlightNavKey.Previous; break;
            case Keys.Right: key = HighlightNavKey.Next; break;
            case Keys.Home: key = HighlightNavKey.First; break;
            case Keys.End: key = HighlightNavKey.Last; break;
            default: return;
        }
        e.Handled = true;
        Go(VL.Step(_index.Peek(), _items.Count, key));
    }

    void Close() => _handle()?.Close();

    /// <summary>Design §B.5 / §E.10 — order is the whole feature: the shell must have navigated before EITHER plate
    /// (this viewer's own, and — over the dialog — the dialog's) starts its exit, or focus lands nowhere.</summary>
    void TryIt(string route, string? arg)
    {
        _nav?.Invoke(route, arg);
        _closeHost?.Invoke();
        Close();
    }

    // ── tree ─────────────────────────────────────────────────────────────────────────────────────────────────────

    Element Plate(HighlightItem item, ReleaseHighlight h, string id, float w, float maxH, float imgH,
                 int index, int count, bool store, bool video)
    {
        bool hasPager = count >= 2;   // one highlight → no dots (design §E.1); the chevrons stay — see Circle
        var children = new List<Element>(3) { Band(item, h, id, imgH, index, count, store, video) };
        if (hasPager) children.Add(PagerRow(count, index));
        children.Add(TextSlide(h, id, store, hasPager));

        return new BoxEl
        {
            Direction = 1, Width = w, MaxHeight = maxH,
            Fill = Tok.FillSolidBase, Corners = CornerRadius4.All(Radii.Overlay),
            BorderWidth = 1f, BorderColor = Tok.StrokeSurfaceDefault, ClipToBounds = true,
            Children = children.ToArray(),
        };
    }

    /// <summary>The 16:9 image band: a KEYED, animated poster/tint layer plus two STABLE chrome rows painted over it
    /// (never remounted, never carrying the slide's key) — so the pager's focused pip and the chevrons' identity
    /// survive a step, and only the poster itself slides.</summary>
    Element Band(HighlightItem item, ReleaseHighlight h, string id, float imgH, int index, int count, bool store, bool video)
    {
        string? poster = item.Poster is { Length: > 0 } p ? p : null;
        return new BoxEl
        {
            Height = imgH, ZStack = true, ClipToBounds = true,
            Children =
            [
                new BoxEl
                {
                    Key = "hv:" + id + ":img",
                    Animate = HighlightViewerMotion.For(_dir),
                    // Forced fill on BOTH axes (not left to the aspect-ratio-derives-width trick the card's own band
                    // relies on): a no-poster slide has no image to derive a size from, and the tint needs the same
                    // full-band fill the poster gets.
                    ZStack = true, AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch,
                    Fill = poster is null ? (store ? Tok.AccentSubtle : Tok.FillSubtleSecondary) : ColorF.Transparent,
                    Children = poster is null ? [] : [Poster(poster)],
                },
                TopRow(h, store),
                CentreRow(item.Doc, video, index, count),
            ],
        };
    }

    static Element Poster(string poster) => new ImageEl
    {
        Source = poster, Fit = ImageFit.Cover,
        AspectRatio = VL.PosterAspect, DecodePx = HighlightCard.PosterDecodePx,
        Corners = new CornerRadius4(Radii.Overlay, Radii.Overlay, 0f, 0f),
        Placeholder = Tok.FillSubtleSecondary,
        RevealTransition = ImageTransition.Fade(140f),
        AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch,
    };

    /// <summary>Kind pill top-left (8 DIP inset, matching the card), close top-right (12 DIP — a different affordance
    /// at a different scale). Rebuilt fresh every render like any ordinary child — "stable" here means it is never
    /// wrapped in the slide's Key, not that its own content is frozen.</summary>
    Element TopRow(ReleaseHighlight h, bool store) => new BoxEl
    {
        AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Stretch,
        Direction = 0, Justify = FlexJustify.SpaceBetween, AlignItems = FlexAlign.Start,
        Children =
        [
            new BoxEl { Margin = new Edges4(8f, 8f, 0f, 0f), Children = [HighlightCard.KindPill(h.Kind, store)] },
            ToolTip.Wrap(
                Circle(Icons.ChromeClose, 12f, enabled: true, Close, "hv:close") with { Margin = new Edges4(0f, 12f, 12f, 0f) },
                Loc.Get(Strings.WhatsNew.Viewer.Close)),
        ],
    };

    /// <summary>The chevrons, 12 DIP from the band's edges, plus (video only) the watch pill between them. Three
    /// SpaceBetween children of equal-width edges centre the middle one exactly, so the pill needs no separate
    /// centring trick.</summary>
    Element CentreRow(ReleaseNotesDocument doc, bool video, int index, int count) => new BoxEl
    {
        AlignSelf = FlexAlign.Center, JustifySelf = FlexAlign.Stretch,
        Direction = 0, Justify = FlexJustify.SpaceBetween, AlignItems = FlexAlign.Center,
        Padding = new Edges4(VL.ChromeInset, 0f, VL.ChromeInset, 0f),
        Children = video
            ? [Prev(index, count), WatchPill(doc), Next(index, count)]
            : [Prev(index, count), Next(index, count)],
    };

    // The three chrome circles are glyph-only, and the engine has no automation-name property yet (design §B.6), so a
    // tooltip is the ONLY name each one carries — for a pointer user and, once a UIA layer exists, for a screen
    // reader. A dimmed end button is not hit-testable, so it simply never raises one.
    Element Prev(int index, int count) => ToolTip.Wrap(
        Circle(Icons.ChevronLeft, 16f, index > 0,
            () => Go(VL.Step(_index.Peek(), count, HighlightNavKey.Previous)), "hv:prev"),
        Loc.Get(Strings.WhatsNew.Viewer.Previous));

    Element Next(int index, int count) => ToolTip.Wrap(
        Circle(Icons.ChevronRight, 16f, index < count - 1,
            () => Go(VL.Step(_index.Peek(), count, HighlightNavKey.Next)), "hv:next"),
        Loc.Get(Strings.WhatsNew.Viewer.Next));

    /// <summary>A 36 DIP on-media circle. The SAME node on every slide (a caller-supplied stable Key): at an end it
    /// dims IN PLACE and never leaves the layout — the first draft removed it instead, and paging read as the chrome
    /// jumping around rather than the slide moving. A control that sits in the same place on every slide is the
    /// whole point of chrome.</summary>
    static BoxEl Circle(string glyph, float glyphSize, bool enabled, Action onClick, string key) => new BoxEl
    {
        Key = key,
        Width = VL.ChromeCircle, Height = VL.ChromeCircle, Shrink = 0f,
        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Corners = CornerRadius4.All(VL.ChromeCircle / 2f),
        Fill = enabled ? Tok.MediaScrim : ChromeDimmed,
        HoverFill = enabled ? ChromeHover : default,
        BrushTransitionMs = WaveeMotion.Faster,
        Opacity = enabled ? 1f : 0.3f,
        Transition = MotionTok.ControlFaster,          // the dim is an 83ms EASE, not a pop
        HitTestVisible = enabled, Focusable = enabled, TabStop = enabled ? null : false,
        Role = AutomationRole.Button, Cursor = enabled ? CursorId.Hand : default,
        OnClick = enabled ? onClick : null,
        Children = [Icon(glyph, glyphSize, Tok.OnMediaPrimary)],
    };

    /// <summary>The labelled on-media pill that stands in for the video-link glyph the design's first draft drew
    /// straight onto the image (§0's engine-gap correction): there is no automation-name property yet, so a bare
    /// glyph link would carry no name at all — this pill's own visible label IS the name. Opens the release page
    /// (where the mp4 lives, never the app); the viewer stays open.</summary>
    static Element WatchPill(ReleaseNotesDocument doc) => new BoxEl
    {
        Direction = 0, Gap = 6f, AlignItems = FlexAlign.Center,
        Padding = new Edges4(14f, 8f, 16f, 8f), Corners = CornerRadius4.All(Radii.Pill),
        Fill = Tok.MediaScrim, HoverFill = ChromeHover, BrushTransitionMs = WaveeMotion.Faster,
        Role = AutomationRole.Hyperlink, Cursor = CursorId.Hand,
        HoverScale = WaveeMotion.ScaleStandard.Hover, PressScale = WaveeMotion.ScaleStandard.Press,
        OnClick = () => ShellOpen.OpenUrl(doc.Links?.Release is { Length: > 0 } r ? r : ReleaseNotesText.ReleaseTagUrl(doc.Version)),
        Children =
        [
            Icon(Icons.Play, 14f, Tok.OnMediaPrimary),
            new TextEl(Loc.Get(Strings.WhatsNew.Viewer.Watch)) { Size = 12.5f, Weight = 600, Color = Tok.OnMediaPrimary },
        ],
    };

    /// <summary>The dots. Three of them ARE the position, so there is no "2 of 3" caption — but the count still has to
    /// be sayable, so it is the row's tooltip rather than a second visible copy of the same fact.</summary>
    Element PagerRow(int count, int index) => ToolTip.Wrap(new BoxEl
    {
        Height = 24f, Margin = new Edges4(0f, 12f, 0f, 0f),
        Direction = 0, Justify = FlexJustify.Center,
        Children = [PipsPager.Create(count, _pip, onChange: i => Go(VL.StepTo(_index.Peek(), i, count)))],
    }, Strings.WhatsNew.Viewer.Position(index + 1, count));

    /// <summary>The keyed text half of the slide: title, the full body (never the card's clipped-and-faded slot),
    /// and — mutually exclusive, design §B.5 — the store CTA or the "Try it" deep link.</summary>
    Element TextSlide(ReleaseHighlight h, string id, bool store, bool hasPager)
    {
        var actions = new List<Element>(1);
        if (store)
            actions.Add(Button.Accent(Loc.Get(Strings.WhatsNew.StoreCta), HighlightCard.OpenStoreListing));
        else if (h.DeepLink is { Length: > 0 } dl && DeepLink.TryParse(dl, out var verb) && verb.Kind == DeepLinkKind.Open)
        {
            string route = verb.Route;
            string? arg = verb.Arg.Length == 0 ? null : verb.Arg;
            actions.Add(Button.Accent(Loc.Get(Strings.WhatsNew.TryIt), () => TryIt(route, arg)));
        }

        var content = new List<Element>(3)
        {
            new TextEl(h.Title) { Size = 20f, Weight = 600, LineHeight = 28f, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap },
            // The SAME markdown-lite spans the card uses (bold/code/links survive); selectable here — the card's own
            // paragraph opts OUT of selection because the card itself is one big click target, and the viewer's body
            // is not.
            RichTextBlock.Paragraph(ReleaseNotesText.ToSpans(MarkdownLite.Tokenize(h.Body), url => ShellOpen.OpenUrl(url)),
                    isTextSelectionEnabled: true)
                with { Size = 14f, LineHeight = 20f, Color = Tok.TextSecondary, Margin = new Edges4(0f, 8f, 0f, 0f) },
        };
        if (actions.Count > 0)
            content.Add(new BoxEl { Direction = 0, Gap = 8f, Margin = new Edges4(0f, 16f, 0f, 0f), Children = actions.ToArray() });

        return new BoxEl
        {
            Key = "hv:" + id + ":txt",
            Animate = HighlightViewerMotion.For(_dir),
            Direction = 1, Grow = 1f, Shrink = 1f, MinHeight = 0f,
            Children =
            [
                // Grow/Shrink/MinHeight repeated on the ScrollView itself (the ReleaseNotesPage.Frame precedent):
                // a flex child's content-driven minimum has to be zeroed at EACH nesting level for shrinking to
                // reach the ScrollView, not just at the keyed wrapper around it.
                // Top padding drops to 8 when the pager already sits above it — its own 12 DIP margin plus 24 height
                // is most of the breathing room the full 16 would otherwise add a second time.
                ScrollView(new BoxEl { Direction = 1, MaxWidth = 720f, AlignSelf = FlexAlign.Start, Children = content.ToArray() })
                    with { Grow = 1f, Shrink = 1f, MinHeight = 0f, Padding = new Edges4(24f, hasPager ? 8f : 16f, 24f, 24f) },
            ],
        };
    }

    /// <summary>Mirrors HighlightStrip's own key recipe (doc version + highlight id, the loop index as a last
    /// resort) so the SAME highlight keeps the SAME slide key across a re-render — Animate's enter/exit diffing
    /// depends on it staying stable.</summary>
    static string SlideId(HighlightItem item, int index)
        => item.Doc.Version + ":" + (item.Highlight.Id is { Length: > 0 } id ? id : index.ToString());
}
