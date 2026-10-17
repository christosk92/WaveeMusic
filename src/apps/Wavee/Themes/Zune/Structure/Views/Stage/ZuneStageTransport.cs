// ── Themes/Zune/Structure/Views/Stage/ZuneStageTransport.cs — Zune's `Stage.Transport` (P5 Z4b, tiers ZT3) ──────────────────────────
//
// ONE view on a full-bleed chrome layer that places its own parts for the arrangement the stage is in (`ZuneStageGeometry.Arrange`):
//
//   PLATE     (Z-NowPlaying, Z-Lyrics, Z-Queue-Page at every tier) the flat plate across the foot: the stage veil (0.85 over a face,
//             opaque under a pane), the tier's gutter each side, the tier's seek row, then the row in the tier's kind:
//               Full       "playing from …" left, shuffle · previous · the DISC · next · repeat centred, heart · mute · volume · device · "…"
//               HeartLead  (Small) heart left, the centred cluster, device · "…" right
//               Stack      (Compact) the cluster spread across, then heart · device · "…" under it
//               Stacked    where the kind does not fit: heart · previous · disc · next · "…" spread across
//   CARD      (Z-NowPlaying-Card, -Huge) inside the card, in its tier's voice; mute · volume · device at the bottom-right gutter.
//   COLUMN    (Z-NowPlaying-LargeArt, -Small) in the column: "playing from …" at its top; at its foot the seek row, the row (… heart) and
//             the volume row (Small: the aside under the block).
//   CENTERED  (Z-NowPlaying-Centered) the seek row and the centred cluster under the titles; the gradient FOOT: "playing from …" left,
//             heart · volume · device · "…" right.
//   STACK     (Z-NowPlaying-Compact) "playing from …" under the titles; at the foot the seek row, the cluster spread across (the 64
//             disc), and heart · device · the face's word · the layout's word.
//
// Each arrangement is a keyed child of the view's layer, so a layout switch or a tier crossing cross-fades one into the next while the
// cover springs between them: the transport's slot (and the stage) never remount. What is playback state arrives as
// `StageTransportFacts`, read in this render; the mute glyph, the volume line and the device word keep their own components. The "…" menu
// is `Stage.NowPlayingMenu` and holds the chrome while it is open. The seek line itself is the player family's `Player.SeekRail`.

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
using static FluentGpu.Dsl.Ui;

using G = Wavee.Themes.Zune.ZuneStageGeometry;
using Ink = Wavee.Theming.Roles.StageInk;

namespace Wavee.Themes.Zune;

/// <summary>Zune's stage transport.</summary>
internal static class ZuneStageTransport
{
    /// <summary>The plate yields its width to a gallery that runs to the foot on the face's re-centre tween.</summary>
    static readonly LayoutTransition s_yield = new(TransitionChannels.Bounds, TransitionDynamics.Tween(G.GalleryYieldMs, Easing.FluentDecelerate), SizeMode.Relayout);

    /// <summary>The registered template of <see cref="ViewKeys.StageTransport"/>.</summary>
    public static Element Template(StageTransportProps props, in ViewContext ctx)
    {
        var st = props.Stage;
        var L = st.Layout.Value;
        var k = st.Look.Value;
        var t = ZuneStageTiers.StageOf(ctx.Tier());
        bool gallery = k.Mode == Stage.Mode.Visualizer && st.GalleryShown.Value;
        var a = G.Arrange(in L, in k, t, gallery);
        var facts = props.Facts();
        var set = st.Accent.Value;
        var overlay = ctx.Hooks.UseContext(Overlay.Service);
        var fade = ZuneMotion.SwapFade;
        Element body;
        if (a.Voice is { } v)
        {
            var kit = new Kit(props, facts, set, overlay, Sizes.Of(v, t));
            body = a.Form switch
            {
                ZuneStageForm.Card => Card(kit, in a) with { Key = "stage:transport:card", Enter = fade, Exit = fade, Transition = MotionTok.ControlNormal },
                ZuneStageForm.Column => Column(kit, in a, t) with { Key = "stage:transport:column", Enter = fade, Exit = fade, Transition = MotionTok.ControlNormal },
                ZuneStageForm.Centered => Centered(kit, in a, t) with { Key = "stage:transport:centered", Enter = fade, Exit = fade, Transition = MotionTok.ControlNormal },
                _ => Stack(kit, in a, st) with { Key = "stage:transport:stack", Enter = fade, Exit = fade, Transition = MotionTok.ControlNormal },
            };
        }
        else
        {
            var kit = new Kit(props, facts, set, overlay, Sizes.Of(t));
            body = Plate(kit, in L, t, k.Mode, gallery) with { Key = "stage:transport:plate", Enter = fade, Exit = fade, Transition = MotionTok.ControlNormal };
        }
        return Stage.Layer with { Children = [body] };
    }

    // ── the plate ────────────────────────────────────────────────────────────────────────────────────────────────────────

    static BoxEl Plate(Kit kit, in Stage.Layout L, ZuneStageLayout t, Stage.Mode mode, bool gallery)
    {
        var (form, volume) = G.PlateFit(in L, t);
        float rowH = G.PlateRowFor(in L, t, form);
        float disc = G.DiscFor(t, rowH);
        var rows = new List<Element>(3) { kit.SeekRow() };
        switch (form)
        {
            case ZunePlateForm.Stack:
                rows.Add(Spread(rowH, kit.Shuffle(), kit.Previous(), kit.Play(disc), kit.Next(), kit.Repeat()));
                rows.Add(new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Height = t.PlateFootH, Gap = t.RightGap,
                    Children = [kit.Heart(), new BoxEl { Grow = 1f, MinWidth = 0f }, kit.Device(), kit.More()],
                });
                break;
            case ZunePlateForm.Stacked:
                rows.Add(Spread(rowH, kit.Heart(), kit.Previous(), kit.Play(disc), kit.Next(), kit.More()));
                break;
            case ZunePlateForm.HeartLead:
                rows.Add(Three(rowH, t.ClusterGap, [kit.Heart()], kit.Centre(disc), [kit.Device(), kit.More()], t.RightGap));
                break;
            default:
            {
                var right = new List<Element>(5) { kit.Heart(), kit.Mute() };
                if (volume) right.Add(kit.Volume());
                right.Add(kit.Device());
                right.Add(kit.More());
                rows.Add(Three(rowH, t.ClusterGap, [kit.Source()], kit.Centre(disc), right.ToArray(), t.RightGap));
                break;
            }
        }
        return new BoxEl
        {
            AlignSelf = FlexAlign.End, JustifySelf = FlexAlign.Stretch, Height = L.TransportH, Direction = 1,
            Margin = new Edges4(L.TransportLeft, 0f, L.TransportRight(gallery), L.TransportInset), Layout = s_yield,
            Padding = new Edges4(t.Gutter, 0f, t.Gutter, 0f), Fill = Ink.Veil with { A = G.PlateAlpha(mode) },
            OnHoverMove = _ => kit.Over(true), OnPointerExit = () => kit.Over(false),
            Children = rows.ToArray(),
        };
    }

    /// <summary>A row of parts spread across (the Compact cluster, the stacked row).</summary>
    static BoxEl Spread(float h, params Element[] parts) => new()
    {
        Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.SpaceBetween, Height = h, Children = parts,
    };

    /// <summary>Three clusters: the side columns split what the centre leaves (Grow 1, Basis 0), so the centre stays centred.</summary>
    static BoxEl Three(float h, float gap, Element[] left, Element centre, Element[] right, float rightGap) => new()
    {
        Direction = 0, AlignItems = FlexAlign.Center, Height = h, Gap = gap,
        Children =
        [
            new BoxEl { Grow = 1f, Basis = 0f, MinWidth = 0f, Direction = 0, AlignItems = FlexAlign.Center, ClipToBounds = true, Gap = rightGap, Children = left },
            centre,
            new BoxEl { Grow = 1f, Basis = 0f, MinWidth = 0f, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.End, Gap = rightGap, Children = right },
        ],
    };

    // ── the arrangements ─────────────────────────────────────────────────────────────────────────────────────────────────

    static BoxEl Card(Kit kit, in ZuneStageArrangement a)
    {
        var s = kit.Size;
        float disc = MathF.Min(s.Disc, s.RowH);
        var controls = new BoxEl
        {
            Direction = 1,
            Children =
            [
                kit.SeekRow(),
                new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Height = s.RowH, Gap = s.Gap,
                    Children = [kit.Shuffle(), kit.Previous(), kit.Play(disc), kit.Next(), kit.Repeat(), new BoxEl { Grow = 1f, MinWidth = 0f }, kit.Heart(), kit.More()],
                },
            ],
        };
        var aside = new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.End, Height = s.AsideH, Gap = s.RightGap,
            Children = [kit.Mute(), kit.Volume(), kit.Device()],
        };
        return Stage.Layer with
        {
            Children =
            [
                Placed(kit, a.Source, kit.Source(), "stage:transport:source"),
                Placed(kit, a.Controls, controls, "stage:transport:controls"),
                Placed(kit, a.Aside, aside, "stage:transport:aside"),
            ],
        };
    }

    static BoxEl Column(Kit kit, in ZuneStageArrangement a, ZuneStageLayout t)
    {
        var s = kit.Size;
        float disc = MathF.Min(s.Disc, s.RowH);
        bool volumeRow = a.Aside.IsEmpty;
        float seekTop = a.SeekTop;
        var rows = new List<Element>(3)
        {
            kit.SeekRow(),
            new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Height = s.RowH, Gap = s.Gap,
                Children = [kit.Shuffle(), kit.Previous(), kit.Play(disc), kit.Next(), kit.Repeat(), new BoxEl { Grow = 1f, MinWidth = 0f }, kit.Heart()],
            },
        };
        if (volumeRow)
            rows.Add(new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Height = s.AsideH, Gap = s.RightGap, Margin = new Edges4(0f, a.VolumeTop, 0f, 0f),
                Children = [kit.Mute(), kit.Volume(), new BoxEl { Grow = 1f, MinWidth = 0f }, kit.Device(), kit.More()],
            });
        var controls = new BoxEl { Direction = 1, Padding = new Edges4(0f, seekTop, 0f, 0f), Children = rows.ToArray() };
        var kids = new List<Element>(3)
        {
            Placed(kit, a.Source, kit.Source(), "stage:transport:source"),
            Placed(kit, a.Controls, controls, "stage:transport:controls"),
        };
        if (!volumeRow)
            kids.Add(Placed(kit, a.Aside, new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.End, Height = s.AsideH, Gap = s.RightGap,
                Children = [kit.Mute(), kit.Volume(), kit.Device(), kit.More()],
            }, "stage:transport:aside"));
        return Stage.Layer with { Children = kids.ToArray() };
    }

    static BoxEl Centered(Kit kit, in ZuneStageArrangement a, ZuneStageLayout t)
    {
        var s = kit.Size;
        float disc = MathF.Min(s.Disc, s.RowH);
        var controls = new BoxEl
        {
            Direction = 1,
            Children =
            [
                kit.SeekRow(),
                new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Height = s.RowH, Gap = s.Gap,
                    Children = [kit.Shuffle(), kit.Previous(), kit.Play(disc), kit.Next(), kit.Repeat()],
                },
            ],
        };
        // the foot: the gradient across the stage's bottom, "playing from …" at the left gutter, heart · volume · device · "…" at the right
        var foot = new BoxEl
        {
            Key = "stage:transport:foot", AlignSelf = FlexAlign.End, JustifySelf = FlexAlign.Stretch, Height = a.Foot.H, HitTestVisible = false,
            Gradient = GradientDown(new GradientStop(0f, Ink.Veil with { A = 0f }), new GradientStop(1f, Ink.Veil with { A = G.FootVeilA })),
        };
        var right = new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.End, Grow = 1f, Gap = s.RightGap,
            Children = [kit.Heart(), kit.Volume(), kit.Device(), kit.More()],
        };
        var left = new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Grow = 1f, MinWidth = 0f, Children = [kit.Source()] };
        return Stage.Layer with
        {
            Children =
            [
                foot,
                Placed(kit, a.Controls, controls, "stage:transport:controls"),
                Placed(kit, a.Source, left, "stage:transport:source"),
                Placed(kit, a.Aside, right, "stage:transport:aside"),
            ],
        };
    }

    static BoxEl Stack(Kit kit, in ZuneStageArrangement a, Stage.StageCtx st)
    {
        var s = kit.Size;
        float disc = MathF.Min(s.Disc, s.RowH);
        string face = Loc.Get(Visualizer.NameKey(st.Kind.Value));
        string layout = Loc.Get(ZuneStageTopBar.LayoutName(st.LayoutPref.Value));
        var controls = new BoxEl
        {
            Direction = 1,
            Children =
            [
                kit.SeekRow(),
                Spread(s.RowH, kit.Shuffle(), kit.Previous(), kit.Play(disc), kit.Next(), kit.Repeat()),
                new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Height = s.AsideH, Gap = s.RightGap,
                    Children =
                    [
                        kit.Heart(), kit.Device(), new BoxEl { Grow = 1f, MinWidth = 0f },
                        kit.Word(Strings.Theme.Zune.Stage.FaceWord(face), st.ToggleGallery, "stage:transport:face"),
                        kit.Word(layout, st.OpenOptions, "stage:transport:layout"),
                    ],
                },
            ],
        };
        return Stage.Layer with
        {
            Children =
            [
                Placed(kit, a.Source, kit.Source(), "stage:transport:source"),
                Placed(kit, a.Controls, controls, "stage:transport:controls"),
            ],
        };
    }

    /// <summary>A part placed at an arrangement's rect (top-left in the layer, its rect's size); it holds the chrome while the pointer is on it.</summary>
    static BoxEl Placed(Kit kit, in ZuneRect r, Element child, string key) => new()
    {
        Key = key, AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start, Width = r.W, Height = r.H, Margin = new Edges4(r.X, r.Y, 0f, 0f),
        Direction = 1, MinWidth = 0f, ClipToBounds = true, Justify = FlexJustify.Center,
        OnHoverMove = _ => kit.Over(true), OnPointerExit = () => kit.Over(false),
        Children = [child],
    };

    // ── the parts ────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The sizes one arrangement draws its parts at: the tier's plate, or the arrangement's voice.</summary>
    internal readonly record struct Sizes(float Box, float Glyph, float PlayGlyph, float Disc, float VolumeW, float Gap, float RightGap,
        float SourceSize, float SourceLine, float SeekH, float RowH, float AsideH, float DeviceH, float WordSize, float WordLine)
    {
        public static Sizes Of(ZuneStageLayout t) => new(t.GlyphBox, t.Glyph, t.PlayGlyph, t.Disc, t.VolumeW, t.ClusterGap, t.RightGap,
            t.SourceSize, t.SourceLine, t.SeekRowH, t.PlateRowH, t.PlateFootH, t.DeviceH, t.WordSize, t.WordLine);

        public static Sizes Of(ZuneStageVoice v, ZuneStageLayout t) => new(MathF.Max(v.Box, t.TargetH), v.Glyph, v.PlayGlyph, v.Disc, v.VolumeW, v.RowGap, t.RightGap,
            v.SourceSize, v.SourceLine, v.SeekH, v.RowH, v.AsideH, MathF.Max(v.AsideH, t.TargetH), t.WordSize, t.WordLine);
    }

    /// <summary>The pieces every arrangement draws, over one render's facts.</summary>
    sealed class Kit(StageTransportProps props, StageTransportFacts facts, AccentSet set, IOverlayService overlay, Sizes size)
    {
        public Sizes Size => size;

        public void Over(bool over) => props.Stage.OverControls(over);

        /// <summary>The seek row: elapsed · the bar's own seek (the 2-px line) · remaining. Its scrub edge holds the chrome.</summary>
        public Element SeekRow() => new BoxEl
        {
            Key = "stage:transport:seek", Height = size.SeekH, Direction = 0, AlignItems = FlexAlign.Center, Gap = G.SeekGap, Shrink = 0f,
            Children =
            [
                props.TimeText(false, Ink.InkSecondary),
                new BoxEl { Grow = 1f, Shrink = 1f, MinWidth = 0f, Children = [props.SeekBar(props.Stage.Scrubbing)] },
                props.TimeText(true, Ink.InkSecondary),
            ],
        };

        /// <summary>"playing from playlist · late night drive": the label in the tertiary ink (label case), the source's name in the ink.
        /// Nothing without a source.</summary>
        public Element Source()
        {
            if (facts.PlayingFrom.Length == 0) return new BoxEl { HitTestVisible = false };
            return new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, MinWidth = 0f, Shrink = 1f, HitTestVisible = false,
                Children =
                [
                    new TextEl(facts.PlayingFrom) { Size = size.SourceSize, LineHeight = size.SourceLine, Color = Ink.InkTertiary, Case = R.Case.For(LabelKind.Label), MaxLines = 1, Shrink = 0f },
                    new TextEl(facts.Source.Length > 0 ? " · " : "") { Size = size.SourceSize, LineHeight = size.SourceLine, Color = Ink.InkTertiary, Shrink = 0f },
                    new TextEl(facts.Source) { Size = size.SourceSize, LineHeight = size.SourceLine, Color = Ink.Ink, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f },
                ],
            };
        }

        /// <summary>shuffle · previous · the disc · next · repeat, the gap apart.</summary>
        public Element Centre(float disc) => new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = size.Gap, Shrink = 0f,
            Children = [Shuffle(), Previous(), Play(disc), Next(), Repeat()],
        };

        public Element Shuffle() => ToolTip.Wrap(Latch(Icons.Shuffle, props.Verbs.ToggleShuffle, facts.CanTransport, facts.Shuffle), Loc.Get(Strings.Player.Shuffle));

        public Element Repeat() => ToolTip.Wrap(Latch(facts.RepeatOne ? Icons.RepeatOne : Icons.RepeatAll, props.Verbs.CycleRepeat, facts.CanTransport, facts.RepeatOn), Loc.Get(Strings.Player.Repeat));

        public Element Previous() => ToolTip.Wrap(Glyph(Icons.Previous, props.Verbs.Previous, facts.PrevEnabled, Ink.Ink), Loc.Get(Strings.Player.Previous));

        public Element Next() => ToolTip.Wrap(Glyph(Icons.Next, props.Verbs.Next, facts.NextEnabled, Ink.Ink), Loc.Get(Strings.Player.Next));

        /// <summary>The accent disc, round: the one round control of the plate.</summary>
        public Element Play(float box)
        {
            bool enabled = facts.PrimaryEnabled;
            var verbs = props.Verbs;
            return new BoxEl
            {
                Key = "stage:transport:disc", Width = box, Height = box, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Corners = Radii.Circle(box),
                Fill = enabled ? set.Fill : Ink.ScrimRest, HoverFill = enabled ? set.FillSecondary : Ink.ScrimRest, PressedFill = enabled ? set.FillTertiary : Ink.ScrimRest,
                BrushTransitionMs = Design.Motion.Faster,
                HoverScale = R.Motion.ScaleEmphatic.HoverIf(enabled), PressScale = R.Motion.ScaleEmphatic.PressIf(enabled),
                Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, IsEnabled = enabled,
                OnClick = () => verbs.TogglePlay("stage.button"), Cursor = enabled ? CursorId.Hand : (CursorId?)null,
                Children = [new TextEl(facts.Playing ? Icons.Pause : Icons.Play) { Size = size.PlayGlyph, FontFamily = Tok.Definition.Fonts.Icon, Color = enabled ? set.Ink : Ink.InkTertiary }],
            };
        }

        public Element Heart()
        {
            var like = facts.Like;
            bool saved = facts.Saved;
            return new BoxEl
            {
                Width = size.Box, Height = size.Box, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Role = AutomationRole.Button, Focusable = like is not null, AllowFocusOnInteraction = false, OnClick = like, Cursor = like is null ? (CursorId?)null : CursorId.Hand, BlocksDragArm = true,
                Children = [new BoxEl { Key = saved ? "sh:on" : "sh:off", Children = [new TextEl(saved ? Icons.HeartFill : Icons.Heart) { Size = size.Glyph, FontFamily = Tok.Definition.Fonts.Icon, Color = saved ? set.Text : Ink.Ink, HoverColor = saved ? set.Text : Ink.Ink }] }],
            };
        }

        public Element More()
        {
            var menuOpen = props.Stage.MenuOpen;
            return new BoxEl
            {
                Width = size.Box, Height = size.Box, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Role = AutomationRole.Button, Cursor = CursorId.Hand, Focusable = true, AllowFocusOnInteraction = false, ClickRequestsContext = true,
                Children = [new TextEl(Icons.More) { Size = size.Glyph, FontFamily = Tok.Definition.Fonts.Icon, Color = Ink.InkSecondary, HoverColor = Ink.Ink }],
            }.WithContextMenu(overlay,
                () => { var m = Stage.NowPlayingMenu?.Invoke(); if (m is { } model && ContextMenu.HasAnyEnabled(model)) menuOpen(true); return m; },   // the "…" menu holds the chrome; an empty one never opens
                new ContextMenuOptions { OnClosed = _ => menuOpen(false) });
        }

        public Element Mute() => Embed.Comp(new ZuneStageMute.Props(props, size.Box, size.Glyph), static () => new ZuneStageMute());

        public Element Volume() => size.VolumeW > 0f
            ? Embed.Comp(new ZuneStageVolume.Props(size.VolumeW, props), static () => new ZuneStageVolume())
            : new BoxEl { Width = 0f, Height = 0f, HitTestVisible = false };

        public Element Device() => Embed.Comp(new ZuneStageDevice.Props(props, size.DeviceH, size.SourceSize, size.SourceLine), static () => new ZuneStageDevice());

        /// <summary>A word action at the foot of the Compact stack (the face's word, the layout's word): the secondary ink, a full target.</summary>
        public Element Word(string text, Action onClick, string key) => new BoxEl
        {
            Key = key, Height = size.AsideH, MinWidth = size.AsideH, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Shrink = 1f,
            Padding = new Edges4(G.DevicePadX, 0f, G.DevicePadX, 0f),
            Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, Cursor = CursorId.Hand, OnClick = onClick,
            Children = [new TextEl(text) { Size = size.WordSize, LineHeight = size.WordLine, Color = Ink.InkSecondary, HoverColor = Ink.Ink, Case = R.Case.For(LabelKind.Command), MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f }],
        };

        /// <summary>A plateless glyph: the ink lifts on hover, the tertiary ink when disabled.</summary>
        BoxEl Glyph(string glyph, Action? onClick, bool enabled, ColorF ink) => new()
        {
            Width = size.Box, Height = size.Box, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            PressScale = R.Motion.ScaleSubtle.PressIf(enabled),
            Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, IsEnabled = enabled, OnClick = enabled ? onClick : null,
            Cursor = enabled ? CursorId.Hand : (CursorId?)null,
            Children = [new TextEl(glyph) { Size = size.Glyph, FontFamily = Tok.Definition.Fonts.Icon, Color = enabled ? ink : Ink.InkTertiary, HoverColor = enabled ? Ink.Ink : Ink.InkTertiary }],
        };

        /// <summary>Shuffle / repeat: the accent INK while latched, never a plate.</summary>
        BoxEl Latch(string glyph, Action onClick, bool enabled, bool latched) => Glyph(glyph, onClick, enabled, latched ? set.Text : Ink.InkSecondary);
    }
}

/// <summary>The mute glyph: its own component, so a mute re-renders it alone.</summary>
internal sealed class ZuneStageMute : Component
{
    public sealed record Props(StageTransportProps T, float Box, float Glyph);

    public override Element Render()
    {
        var p = UseProps<Props>();
        bool muted = p.T.Muted();
        var glyph = new BoxEl
        {
            Width = p.Box, Height = p.Box, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, OnClick = p.T.ToggleMute, Cursor = CursorId.Hand,
            Children = [new TextEl(muted ? Icons.Mute : Icons.Volume) { Size = p.Glyph, FontFamily = Tok.Definition.Fonts.Icon, Color = Ink.Ink, HoverColor = Ink.Ink }],
        };
        return ToolTip.Wrap(glyph, Loc.Get(muted ? Strings.Player.Unmute : Strings.Player.Mute));
    }
}

/// <summary>The volume LINE (Z-NowPlaying: a 2-px track, the ink as its value, a small square thumb) at the tier's length. Its own
/// component, with a mirror cell, so a drag re-renders it alone.</summary>
internal sealed class ZuneStageVolume : Component
{
    public sealed record Props(float Length, StageTransportProps T);

    static readonly FormatCache<int> s_percent = FormatCache.Create<int>();

    static readonly Slider.SliderOptions s_options = new()
    {
        ThumbToolTipValueConverter = static v => s_percent.Get(Math.Clamp((int)MathF.Round(v * ZuneStageGeometry.PercentScale), 0, ZuneStageGeometry.PercentScaleMax), static p => p + "%"),
    };

    readonly FloatSignal _level = new(0f);

    public override Element Render()
    {
        var p = UseProps<Props>();
        var t = p.T;
        UseSignalEffect(() => _level.SetIfChanged(t.Volume()));
        var style = Slider.DefaultStyle with
        {
            TrackHeight = ZuneStageGeometry.VolumeTrack, TrackCornerRadius = 0f,
            RailFill = Ink.Ink with { A = ZuneStageGeometry.RailA }, RailFillDisabled = Ink.Ink with { A = ZuneStageGeometry.RailDisabledA },
            ValueFill = Ink.Ink, ValueFillPointerOver = Ink.Ink, ValueFillPressed = Ink.InkSecondary, ValueFillDisabled = Ink.Ink with { A = ZuneStageGeometry.ThumbDisabledA },
            ThumbRingDiameter = ZuneStageGeometry.VolumeThumb, InnerThumbDiameter = ZuneStageGeometry.VolumeThumbInner, ThumbCornerRadius = 0f,
            ThumbRing = Ink.Ink, ThumbFill = Ink.Ink, ThumbFillPointerOver = Ink.Ink, ThumbFillPressed = Ink.InkSecondary, ThumbFillDisabled = Ink.Ink with { A = ZuneStageGeometry.ThumbDisabledA },
            ThumbBorder = null, ThumbBorderWidth = 0f,
        };
        return Slider.Create(_level, t.SetVolume, s_options, length: p.Length, thickness: ZuneStageGeometry.VolumeThickness, style: style);
    }
}

/// <summary>The device word ("this computer ▾"): the device's name (content: its own casing) and a chevron, no plate; it opens the
/// two-section device picker over the same items the player bar builds, and holds the chrome while it is open. The tier's height (a 40
/// target at Compact) and type.</summary>
internal sealed class ZuneStageDevice : Component
{
    public sealed record Props(StageTransportProps T, float Height, float Size, float Line);

    public override Element Render()
    {
        var p = UseProps<Props>();
        var t = p.T;
        var st = t.Stage;
        var anchor = UseRef<NodeHandle>(default);
        var handle = UseRef<OverlayHandle?>(null);
        var overlay = UseContext(Overlay.Service);
        var device = t.Device();
        void Toggle()
        {
            if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
            t.OpenDevicePicker();
            var opened = overlay.Open(() => anchor.Value, () => MenuFlyout.Create(t.DeviceItems(), () => handle.Value?.Close(), size: OverlayFit.MenuSize),
                FlyoutPlacement.TopEdgeAlignedRight, new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss) { ConstrainToRootBounds = true });
            st.MenuOpen(true);
            opened.ClosedAction = () => { handle.Value = null; st.MenuOpen(false); };
            handle.Value = opened;
        }
        return new BoxEl
        {
            Height = p.Height, Direction = 0, AlignItems = FlexAlign.Center, Gap = ZuneStageGeometry.RightGap,
            Padding = new Edges4(ZuneStageGeometry.DevicePadX, 0f, ZuneStageGeometry.DevicePadX, 0f),
            Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, Cursor = CursorId.Hand, OnClick = Toggle, OnRealized = h => anchor.Value = h,
            MaxWidth = ZuneStageGeometry.DeviceMaxW, Shrink = 1f, MinWidth = MathF.Max(ZuneStageGeometry.DeviceMinW, p.Height),
            Children =
            [
                new TextEl(device.Name) { Size = p.Size, LineHeight = p.Line, Color = Ink.InkSecondary, HoverColor = Ink.Ink, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f },
                new TextEl(Icons.ChevronDown) { Size = ZuneStageGeometry.ChevronGlyph, FontFamily = Tok.Definition.Fonts.Icon, Color = Ink.InkSecondary },
            ],
        };
    }
}
