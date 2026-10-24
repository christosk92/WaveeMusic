// ── Themes/Classic/Structure/Views/Screens/ClassicReleaseNotes.Viewer.cs — Classic's `ReleaseNotes.Viewer` (themes plan 6.1, T11c) ────────────────
//
// From `Screens\ReleaseNotes.UI.cs` §6: the modal one-highlight-at-a-time viewer. The core opens it (`ReleaseNotes.OpenViewer`: anchor-less, Modal chrome for the
// ContentDialog scale/fade and the focus trap, `ScrimVisual = false` — the view paints its OWN veil so it never stacks a second smoke over the after-update plate —
// and LightDismiss: a veil click is how a viewer closes); this template is the veil, the plate, the band, the pager and the text. The index, the pager's mirror and
// the next slide's direction are the template's own hook cells (the old component's fields): the direction is a plain ref — a motion-only value must never trigger
// a render of its own; the render `index.Value = …` triggers on the next line reads it (ch 28 §9.1 #3).
//
// Navigation (Left/Right step clamped with no wrap, Home/End jump, Escape is the overlay's) is the core's `HighlightPaging`; every recognised key is HANDLED even as a
// no-op (a one-highlight viewer never leaks arrows to the page); an already-handled key came from a focused pip and is left alone.

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
using static FluentGpu.Dsl.Ui;
using static Wavee.ReleaseNotes;
using K = Wavee.Themes.Classic.ClassicReleaseNotesRules;
using VL = Wavee.Themes.Classic.ClassicReleaseNotesRules.Viewer;

namespace Wavee.Themes.Classic;

internal static partial class ClassicReleaseNotes
{
    /// <summary>The registered template of <see cref="ViewKeys.ReleaseNotesViewer"/>.</summary>
    public static Element ViewerTemplate(HighlightViewerProps props, in ViewContext ctx)
    {
        var hooks = ctx.Hooks;
        var items = props.Items;
        int count = items.Count;
        var indexCell = hooks.UseSignal(Math.Clamp(props.Initial, 0, Math.Max(0, count - 1)));
        var pip = hooks.UseSignal(0);   // the pager's mirror of the index — written from an EFFECT, never during render
        var dir = hooks.UseRef(HighlightSlideDirection.None);
        var vp = hooks.UseContext(Viewport.Size);
        int index = count == 0 ? 0 : Math.Clamp(indexCell.Value, 0, count - 1);
        hooks.UseEffect(() => { if (pip.Peek() != index) pip.Value = index; }, index);
        if (count == 0) return new BoxEl();

        // The window's overlay record: the chrome circles, the pager's hit boxes and the actions are 40 at Compact (the viewer sizes against the window, not the page).
        return new ViewerBody(props, indexCell, dir, ClassicReleaseNotesTiers.Overlay(ctx.Tier())).Build(vp.Width, vp.Height, index);
    }

    /// <summary>The viewer's tree for one render: the props, the index cell and the direction ref.</summary>
    sealed class ViewerBody(HighlightViewerProps props, Signal<int> indexCell, Ref<HighlightSlideDirection> dir, ClassicReleaseOverlayLayout ov)
    {
        // The chrome's hover / dimmed media scrim (properties: they read tokens, so they follow a theme switch).
        static ColorF ChromeDimmed => Tok.MediaScrim with { A = K.ChromeDimmedAlpha };
        static ColorF ChromeHover => Tok.MediaScrim with { A = K.ChromeHoverAlpha };

        readonly IReadOnlyList<HighlightItem> _items = props.Items;

        public Element Build(float vpWidth, float vpHeight, int index)
        {
            int count = _items.Count;
            var item = _items[index];
            var h = item.Highlight;
            bool store = HighlightVisibility.IsStore(h);
            string id = SlideId(item, index);
            float w = VL.PlateWidth(vpWidth, vpHeight);
            float imgH = VL.ImageHeight(w);   // W·9/16 ALWAYS, poster or not (issue #89 L4)
            bool hasPager = count >= 2;

            var plateKids = new List<Element>(3) { Band(item, h, id, imgH, index, count, store, IsVideo(h)) };
            if (hasPager) plateKids.Add(PagerRow(count, index));
            plateKids.Add(TextStack(h, id, store, hasPager));

            return new BoxEl
            {
                // Explicit viewport size: the modal host hugs, so without it the veil would cover only the plate (#89 L1).
                Width = vpWidth, Height = vpHeight, Grow = 1f, ZStack = true,
                Justify = FlexJustify.Center, AlignItems = FlexAlign.Center,
                Focusable = true, OnKeyDown = OnKeys,
                Children =
                [
                    // THE light-dismiss surface (the plate paints over it in z-order). Hover/pressed pinned to the resting
                    // fill: a dismiss surface must not tint under the cursor or flash on press like a giant button.
                    new BoxEl
                    {
                        AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch,
                        Fill = K.ViewerVeil, HoverFill = K.ViewerVeil, PressedFill = K.ViewerVeil, OnClick = props.Close,
                    },
                    new BoxEl
                    {
                        Direction = 1, Width = w, MaxHeight = VL.PlateMaxHeight(vpHeight),
                        Fill = Tok.FillSolidBase, Corners = CornerRadius4.All(Radii.Overlay),
                        BorderWidth = 1f, BorderColor = Tok.StrokeSurfaceDefault, ClipToBounds = true,
                        Shadow = K.ViewerPlateShadow,
                        Children = plateKids.ToArray(),
                    },
                ],
            };
        }

        void Go(HighlightStep step)
        {
            if (step.Direction == HighlightSlideDirection.None) return;   // an end, or one item: nothing moves or re-renders
            dir.Value = step.Direction;
            indexCell.Value = step.Index;
        }

        /// <summary>Left/Right step (clamped, no wrap), Home/End jump, Escape is the overlay's. Every recognised key is
        /// HANDLED even as a no-op (a one-highlight viewer never leaks arrows to the page); an already-handled key came
        /// from a focused pip and is left alone.</summary>
        void OnKeys(KeyEventArgs e)
        {
            if (e.Handled) return;
            HighlightNavKey key;
            switch (e.KeyCode)
            {
                case Keys.Left: key = HighlightNavKey.Previous; break;
                case Keys.Right: key = HighlightNavKey.Next; break;
                case Keys.Home: key = HighlightNavKey.First; break;
                case Keys.End: key = HighlightNavKey.Last; break;
                default: return;
            }
            e.Handled = true;
            Go(HighlightPaging.Step(indexCell.Peek(), _items.Count, key));
        }

        /// <summary>A KEYED poster layer under two STABLE chrome rows. The no-poster tint lives on this stable band, never on
        /// the keyed layer — there the outgoing and incoming translucent fills stacked and flashed ~8 levels brighter.</summary>
        Element Band(HighlightItem item, ReleaseHighlight h, string id, float imgH, int index, int count, bool store, bool video) => new BoxEl
        {
            Height = imgH, ZStack = true, ClipToBounds = true,
            Fill = store ? Tok.AccentSubtle : Tok.FillSubtleSecondary,
            BrushTransitionMs = Design.Motion.Standard,
            Children =
            [
                new BoxEl
                {
                    Key = "hv:" + id + ":img", Animate = HighlightViewerMotion.For(dir.Value),
                    ZStack = true, AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch,
                    Children = item.Poster is { Length: > 0 } poster
                        ? [new ImageEl
                          {
                              Source = poster, Fit = ImageFit.Cover, AspectRatio = VL.PosterAspect, DecodePx = K.PosterDecodePx,
                              Corners = new CornerRadius4(Radii.Overlay, Radii.Overlay, 0f, 0f), Placeholder = Tok.FillSubtleSecondary,
                              RevealTransition = ImageTransition.Fade(140f), AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch,
                          }]
                        : [],
                },
                new BoxEl
                {
                    AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Stretch,
                    Direction = 0, Justify = FlexJustify.SpaceBetween, AlignItems = FlexAlign.Start,
                    Children =
                    [
                        new BoxEl { Margin = new Edges4(8f, 8f, 0f, 0f), Children = [KindPill(h.Kind, store)] },
                        ToolTip.Wrap(Circle(Icons.ChromeClose, K.CloseGlyph, enabled: true, props.Close, "hv:close", ov.ChromeCircle) with { Margin = new Edges4(0f, 12f, 12f, 0f) },
                            Loc.Get(Strings.WhatsNew.Viewer.Close)),
                    ],
                },
                new BoxEl
                {
                    AlignSelf = FlexAlign.Center, JustifySelf = FlexAlign.Stretch,
                    Direction = 0, Justify = FlexJustify.SpaceBetween, AlignItems = FlexAlign.Center,
                    Padding = new Edges4(VL.ChromeInset, 0f, VL.ChromeInset, 0f),
                    Children = video
                        ? [Prev(index, count), WatchPill(item.Doc), Next(index, count)]
                        : [Prev(index, count), Next(index, count)],
                },
            ],
        };

        // The glyph-only circles' tooltip is the ONLY name each carries. A dimmed end button is not hit-testable, so it
        // simply never raises one.
        Element Prev(int index, int count) => ToolTip.Wrap(
            Circle(Icons.ChevronLeft, K.ChevronGlyph, index > 0, () => Go(HighlightPaging.Step(indexCell.Peek(), count, HighlightNavKey.Previous)), "hv:prev", ov.ChromeCircle),
            Loc.Get(Strings.WhatsNew.Viewer.Previous));

        Element Next(int index, int count) => ToolTip.Wrap(
            Circle(Icons.ChevronRight, K.ChevronGlyph, index < count - 1, () => Go(HighlightPaging.Step(indexCell.Peek(), count, HighlightNavKey.Next)), "hv:next", ov.ChromeCircle),
            Loc.Get(Strings.WhatsNew.Viewer.Next));

        /// <summary>The SAME node on every slide (a stable caller key): at an end it DIMS IN PLACE over an 83-ms ease and
        /// leaves the tab order, but never the layout — removing it read as the chrome jumping.</summary>
        static BoxEl Circle(string glyph, float glyphSize, bool enabled, Action onClick, string key, float size) => new BoxEl
        {
            Key = key,
            Width = size, Height = size, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = CornerRadius4.All(size / 2f),
            Fill = enabled ? Tok.MediaScrim : ChromeDimmed,
            HoverFill = enabled ? ChromeHover : default,
            BrushTransitionMs = Design.Motion.Faster,
            Opacity = enabled ? 1f : K.DimmedOpacity,
            Transition = MotionTok.ControlFaster,
            HoverScale = R.Motion.ScaleStandard.HoverIf(enabled),
            PressScale = R.Motion.ScaleStandard.PressIf(enabled),
            HitTestVisible = enabled, Focusable = enabled, TabStop = enabled ? null : false,
            Role = AutomationRole.Button, Cursor = enabled ? CursorId.Hand : default,
            OnClick = enabled ? onClick : null,
            Children = [Icon(glyph, glyphSize, Tok.OnMediaPrimary)],
        };

        /// <summary>The labelled on-media pill between the chevrons: opens the release page (where the mp4 lives) and leaves
        /// the viewer open. Its visible label IS its name. Focusable in 0.3 — 0.2.9 left it out of the trap (ch 28 §6.3).</summary>
        Element WatchPill(ReleaseNotesDocument doc)
        {
            string release = doc.Links.Release is { Length: > 0 } r ? r : Links.ReleaseTagUrl(doc.Version);
            var openUrl = props.OpenUrl;
            return new BoxEl
            {
                Direction = 0, Gap = K.WatchGap, AlignItems = FlexAlign.Center, MinHeight = ov.Target,
                Padding = new Edges4(14f, 8f, 16f, 8f), Corners = CornerRadius4.All(Radii.Pill),
                Fill = Tok.MediaScrim, HoverFill = ChromeHover, BrushTransitionMs = Design.Motion.Faster,
                Role = AutomationRole.Hyperlink, Cursor = CursorId.Hand, Focusable = true,
                HoverScale = R.Motion.ScaleStandard.Hover, PressScale = R.Motion.ScaleStandard.Press,
                OnClick = () => openUrl(release),
                Children =
                [
                    Icon(Icons.Play, 14f, Tok.OnMediaPrimary),
                    R.Type.DenseTitle(Loc.Get(Strings.WhatsNew.Viewer.Watch)) with { Color = Tok.OnMediaPrimary },
                ],
            };
        }

        /// <summary>Hand-rolled, not PipsPager: 6-DIP dots and a 14-DIP selected capsule that GROWS on a Reflow width tween
        /// (Width only — a Position channel on top double-moved the pips) and brightens on the same 120-ms ramp. A full-width
        /// strip centres a hugging cluster; the tooltip wraps the cluster, so the dots never drift sideways.</summary>
        Element PagerRow(int count, int index)
        {
            var pips = new Element[count];
            for (int i = 0; i < count; i++)
            {
                int target = i;
                bool on = i == index;
                var dot = new BoxEl
                {
                    Key = ov.Target > 0f ? "pipdot:" + target : "pip:" + target,
                    Animate = new LayoutTransition(TransitionChannels.Size, TransitionDynamics.Tween(K.PipMs, Easing.SmoothOut),
                        Size: SizeMode.Reflow, Axes: SizeAxes.Width),
                    Width = on ? K.PipSelected : K.PipDot, Height = K.PipDot, Shrink = 0f, Corners = CornerRadius4.All(Radii.Full),
                    Fill = on ? Tok.TextPrimary : K.PipOff, HoverFill = on ? Tok.TextPrimary : K.PipOffHover,
                    BrushTransitionMs = K.PipMs,
                };
                // At the hit target each pip is a transparent box of the target's size around its dot: the hit area grows, never the picture.
                pips[i] = ov.Target > 0f
                    ? new BoxEl
                    {
                        Key = "pip:" + target, Shrink = 0f, MinWidth = ov.Target, Height = ov.Target, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                        Cursor = CursorId.Hand, Focusable = true, Role = AutomationRole.Button,
                        OnClick = () => Go(HighlightPaging.StepTo(indexCell.Peek(), target, count)),
                        Children = [dot with { HitTestVisible = false }],
                    }
                    : dot with
                    {
                        Cursor = CursorId.Hand, Focusable = true, Role = AutomationRole.Button,
                        OnClick = () => Go(HighlightPaging.StepTo(indexCell.Peek(), target, count)),
                    };
            }
            return new BoxEl
            {
                AlignSelf = FlexAlign.Stretch, Height = ov.PagerRowH, Margin = new Edges4(0f, K.PagerRowTop, 0f, 0f),
                Direction = 0, Justify = FlexJustify.Center, AlignItems = FlexAlign.Center,
                Children =
                [
                    ToolTip.Wrap(new BoxEl { Height = ov.PagerRowH, Direction = 0, AlignItems = FlexAlign.Center, Gap = ov.Target > 0f ? 0f : K.PipGap, Children = pips },
                        Strings.WhatsNew.Viewer.Position(index + 1, count)),
                ],
            };
        }

        /// <summary>The text half with its box FINAL from frame one: a ZStack of one invisible, inert SIZER per highlight plus
        /// the live keyed slide on top. A flex column here folds the exit orphan's height into the plate for the whole exit
        /// and the centred plate bounces twice per step; MeasureZStack has no orphan fold (ch 28 §9.1 #1).</summary>
        Element TextStack(ReleaseHighlight h, string id, bool store, bool hasPager)
        {
            var layers = new Element[_items.Count + 1];
            for (int i = 0; i < _items.Count; i++)
            {
                var other = _items[i];
                layers[i] = new BoxEl
                {
                    Key = "hv:" + SlideId(other, i) + ":size",
                    Direction = 1, Padding = K.ViewerTextPadding(hasPager),
                    Opacity = 0f, HitTestVisible = false,   // inert: no hit-test, and its button is disabled (never trapped)
                    Children = [TextColumn(other.Highlight, HighlightVisibility.IsStore(other.Highlight), live: false)],
                };
            }
            layers[_items.Count] = new BoxEl   // painted last, over the sizers
            {
                Key = "hv:" + id + ":txt", Animate = HighlightViewerMotion.For(dir.Value),
                Direction = 1, Grow = 1f, Shrink = 1f, MinHeight = 0f,
                Children =
                [
                    // ContentSized (issue #89 L2): the plate's auto height must sum band + pager + text, then SCROLL the text
                    // (never the image) once it would pass vpH − 64.
                    ScrollView(TextColumn(h, store, live: true)) with
                        { Grow = 1f, Shrink = 1f, MinHeight = 0f, ContentSized = true, Padding = K.ViewerTextPadding(hasPager) },
                ],
            };
            return new BoxEl { ZStack = true, Grow = 1f, Shrink = 1f, MinHeight = 0f, Children = layers };
        }

        /// <summary>Title, the WHOLE body, and — mutually exclusive — the store CTA or "Try it" for an in-app deep link.
        /// <paramref name="live"/> = false builds the sizer's twin: same geometry, no live link, selection or action.</summary>
        Element TextColumn(ReleaseHighlight h, bool store, bool live)
        {
            var openUrl = props.OpenUrl;
            var content = new List<Element>(3)
            {
                new TextEl(h.Title) { Size = K.ViewerTitleSize, Weight = 600, LineHeight = K.ViewerTitleLine, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap },
                RichTextBlock.Paragraph(ToSpans(MarkdownLite.Tokenize(h.Body), live ? openUrl : IgnoreLink), isTextSelectionEnabled: live)
                    with { Size = K.ViewerBodySize, LineHeight = K.ViewerBodyLine, Color = Tok.TextSecondary, Margin = new Edges4(0f, 8f, 0f, 0f) },
            };
            Element? action = null;
            if (store)
                action = Btn(Loc.Get(Strings.WhatsNew.StoreCta), live ? props.OpenStoreListing : Inert, ButtonAppearance.Accent, ControlSize.Medium, ov.Target, live);
            else if (props.TryItFor(h) is { } tryIt)
            {
                action = Btn(Loc.Get(Strings.WhatsNew.TryIt), live ? tryIt : Inert, ButtonAppearance.Accent, ControlSize.Medium, ov.Target, live);
            }
            if (action is not null)
                content.Add(new BoxEl { Direction = 0, Gap = K.ViewerActionGap, Margin = new Edges4(0f, 16f, 0f, 0f), Children = [action] });
            return new BoxEl { Direction = 1, MaxWidth = K.ViewerTextMaxWidth, AlignSelf = FlexAlign.Start, Children = content.ToArray() };
        }
    }
}

/// <summary>The viewer's slide: a directional ENTER (±24 DIP + fade, 250 ms SmoothOut — <c>Motion.EntranceOffsetPx</c> and
/// <c>Expressive.Fast</c>) over a direction-FREE exit (a 120-ms fade in place). The exit is direction-free by necessity:
/// the engine seeds an orphan's exit from the spec it MOUNTED with, so a directional exit would replay the PREVIOUS
/// step's direction on the way out. Reduced motion needs no branch — the scheduler skips Enter/Exit tracks.</summary>
public static class HighlightViewerMotion
{
    /// <summary>== <c>Motion.EntranceOffsetPx</c>; a slideshow should visibly move (a page nudge is 8).</summary>
    public const float SlideDistance = 24f;
    public const float ExitMs = 120f;

    public static LayoutTransition SlideForward => new(
        TransitionChannels.Position | TransitionChannels.Opacity,
        TransitionDynamics.Tween(Expressive.Fast, Easing.SmoothOut),
        Enter: new EnterExit(Dx: SlideDistance, Opacity: 0f, Active: true),
        Exit: new EnterExit(Dx: 0f, Opacity: 0f, Active: true),
        ExitDynamics: TransitionDynamics.Tween(ExitMs, Easing.FluentAccelerate));

    public static LayoutTransition SlideBack => SlideForward with { Enter = new EnterExit(Dx: -SlideDistance, Opacity: 0f, Active: true) };

    /// <summary>The FIRST slide: no entrance (the Modal chrome already scaled the plate in), but the same exit.</summary>
    public static LayoutTransition ExitOnly => SlideForward with { Enter = default };

    public static LayoutTransition For(HighlightSlideDirection d) => d switch
    {
        HighlightSlideDirection.Forward => SlideForward,
        HighlightSlideDirection.Back => SlideBack,
        _ => ExitOnly,
    };
}
