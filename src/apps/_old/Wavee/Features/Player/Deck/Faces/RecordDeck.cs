using System;
using System.Collections.Generic;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using Wavee.Core;
using Wavee.Features.Player.Deck.Model;

namespace Wavee;

/// <summary>
/// The RECORD family's pixels — four skins over one geometry (<see cref="RecordVariant"/>): the bare record, the
/// wood-plinth turntable, the Zune's type-and-bar poster and the picture disc. The physics behind all four is the
/// same <c>TonearmMachine</c>; the variant only decides what gets drawn and where.
///
/// <para><b>Built exactly once per mount.</b> A face is a plain function, not a component: everything that moves is
/// a <c>Transform</c>/<c>Opacity</c> BIND over the <see cref="DeckSignals"/> slab captured here, so the 30 Hz path
/// writes signals and nothing re-renders (<see cref="DeckFaces"/> states the contract). The three things that
/// actually change SHAPE — the sleeve art, the label and the Zune type block — are tiny components at the leaves
/// keyed on <c>CoverGen</c>, so a new album re-renders three nodes instead of a deck.</para>
///
/// <para><b>Layout is absolute, in fractions of <c>side</c>.</b> A deck-sized <see cref="Canvas"/> holds the ground,
/// the sleeve, the platter group and the arm; the rotating disc is a ZStack rather than a canvas because its
/// shadow must escape its own box.</para>
/// </summary>
static class RecordDeck
{
    // ── the palette. A deck is a physical object, not a themed surface: these are the mockup's literals ──────────
    const uint ArmInk = 0xCFD4DD, ArmDarkInk = 0x9AA1AD;
    const uint FeltIn = 0x3A3D44, FeltOut = 0x2B2E34, FeltRingDark = 0x1F2126, FeltRingLight = 0x6D7078;
    const uint WoodTop = 0x6B4A2E, WoodBottom = 0x4A301B;
    const uint StrobeBoxInk = 0x1A1C20, StrobeDotInk = 0xFF5A2A;
    const uint VinylBlack = 0x15171C, SplatterBase = 0xF5F1EA, MarbleBase = 0x6B4FA8, SpindleInk = 0xE6E9EF;
    const uint AlbumFallback = 0x1C4F9A, ZuneGrey = 0x9AA1AD;

    /// <summary>300 ms opacity entrance on a cover leaf: a new album's art fades in over the slot the old one held.
    /// The keyed remount IS the cross-fade — there is no second copy to keep alive.</summary>
    static readonly LayoutTransition CoverFade = new(
        TransitionChannels.Opacity,
        TransitionDynamics.Tween(300f, Easing.SmoothOut),
        Enter: new EnterExit(Opacity: 0f, Active: true));

    public static Element Build(in NpvPlayerCatalog.Preset preset, IAppSettings? settings, float side,
                                DeckSignals sig, PlaybackBridge bridge, DeckHost host, RecordVariant variant)
    {
        // `preset` is an `in` parameter: it cannot be captured by any lambda below, so every option is read into a
        // plain local HERE, before the first bind thunk exists.
        string finish = NpvPlayerPrefs.ChoiceSlug(settings, preset, "finish");
        string sizeSlug = NpvPlayerPrefs.ChoiceSlug(settings, preset, "size");
        string sleeveSlug = NpvPlayerPrefs.ChoiceSlug(settings, preset, "sleeve");
        string accentSlug = NpvPlayerPrefs.ChoiceSlug(settings, preset, "accent");

        float s = side > 0f ? side : NpvDeck.DefaultSide;
        bool zune = variant == RecordVariant.Zune;
        bool turntable = variant == RecordVariant.Turntable;
        bool small = !zune && sizeSlug == "7";
        bool showSleeve = !zune && sleeveSlug != "off";

        // The deck's ground — and therefore what a 7" record's big centre hole shows THROUGH itself. Bound on the
        // record/picture decks so a re-theme repaints the hole with the card it sits on.
        Prop<ColorF> deckBg = zune ? Hex(0x000000)
                            : turntable ? Hex(WoodBottom)
                            : Prop.Of(static () => Tok.FillCardSecondary);

        // ── platter geometry ─────────────────────────────────────────────────────────────────────────────────────
        float d = zune ? 0.62f * s : small ? 0.52f * s : 0.70f * s;
        float px = zune ? 0.08f * s : small ? 0.33f * s : 0.24f * s;
        float py = zune ? 0.22f * s : small ? 0.25f * s : 0.16f * s;
        if (!zune && !showSleeve) px = 0.15f * s;   // nothing to slide out of: the record sits where the sleeve was

        var kids = new List<CanvasChild>(12);

        // ── the plinth (Turntable only) ──────────────────────────────────────────────────────────────────────────
        if (turntable)
        {
            // The grain PNG is an ALPHA texture, so it is tinted white and the brown rides on top at partial alpha:
            // the grain reads through the stain instead of being painted over by it.
            kids.Add(new CanvasChild(0f, 0f, DeckArt.Texture("wood-grain-1024.png", s, s, CornerRadius4.All(Radii.Card), White(1f))));
            kids.Add(new CanvasChild(0f, 0f, new BoxEl
            {
                Width = s,
                Height = s,
                Shrink = 0f,
                Corners = CornerRadius4.All(Radii.Card),
                Gradient = Ui.GradientDown(new GradientStop(0f, Hex(WoodTop, 0.88f)), new GradientStop(1f, Hex(WoodBottom, 0.96f))),
            }));

            float mat = 0.77f * s, matX = 0.205f * s, matY = 0.125f * s;
            kids.Add(new CanvasChild(matX, matY, new BoxEl
            {
                Width = mat,
                Height = mat,
                Shrink = 0f,
                Corners = Radii.Circle(mat),
                Gradient = Ui.RadialGradient(new GradientStop(0f, Hex(FeltIn)), new GradientStop(1f, Hex(FeltOut))),
            }));
            kids.Add(new CanvasChild(matX, matY, DeckArt.Ring(mat, 4f, Hex(FeltRingDark))));
            kids.Add(new CanvasChild(matX + 8f, matY + 8f, DeckArt.Ring(mat - 16f, 2f, Hex(FeltRingLight))));

            // The strobe window: the pitch light that stands still when the platter is on speed.
            float strobeW = 0.16f * s, strobeH = 0.05f * s, dot = strobeH * 0.5f;
            kids.Add(new CanvasChild(0.08f * s, 0.86f * s, Canvas.Create(strobeW, strobeH,
            [
                new CanvasChild(0f, 0f, new BoxEl
                {
                    Width = strobeW, Height = strobeH, Shrink = 0f,
                    Corners = CornerRadius4.All(2f),
                    Fill = Hex(StrobeBoxInk),
                }),
                new CanvasChild(strobeW - dot * 2f, (strobeH - dot) * 0.5f, new BoxEl
                {
                    Width = dot, Height = dot, Shrink = 0f,
                    Corners = Radii.Circle(dot),
                    Fill = Hex(StrobeDotInk),
                    Shadow = new ShadowSpec(10f, 0f, 0f, Hex(StrobeDotInk, 0.9f)),
                }),
            ])));
        }

        // ── the sleeve the record slides out of ──────────────────────────────────────────────────────────────────
        if (showSleeve)
        {
            float sleeve = 0.64f * s;
            kids.Add(new CanvasChild(0.06f * s, 0.12f * s, new BoxEl
            {
                Width = sleeve,
                Height = sleeve,
                Shrink = 0f,
                ZStack = true,
                Corners = CornerRadius4.All(4f),
                ClipToBounds = true,
                Rotation = turntable ? -4f : -2.5f,
                Shadow = new ShadowSpec(30f, 10f, 0f, Black(0.25f)),
                Fill = Black(0.35f),           // a sleeve with no art is still a sleeve, not a hole
                Children = [CoverLeaf(sig, sleeve, round: false, key: "sleeve-art")],
            }));
        }

        // ── the record: the slide-out wrapper, the rotating disc, the needle-drop puff ──────────────────────────
        kids.Add(new CanvasChild(px, py, Platter(d, finish, variant, sig, bridge, host, deckBg, small)));

        if (!zune)
        {
            // ── tonearm + rest post ─────────────────────────────────────────────────────────────────────────────
            float armW = 0.22f * s, armH = 0.92f * s, armX = 0.75f * s, armY = 0.02f * s;
            kids.Add(new CanvasChild(armX, armY, Tonearm(d, px, py, armW, armH, armX, armY, turntable, sig, bridge, host)));
            kids.Add(new CanvasChild(0.845f * s, 0.06f * s, new BoxEl
            {
                Width = 0.03f * s,
                Height = 0.09f * s,
                Shrink = 0f,
                Corners = CornerRadius4.All(3f),
                Fill = turntable ? Hex(0xC9CED8) : Hex(ArmDarkInk, 0.6f),
            }));
        }
        else
        {
            // ── the Zune's type block and its hairline progress rail ────────────────────────────────────────────
            var accent = ZuneAccent(accentSlug);
            float typeW = 0.34f * s;
            kids.Add(new CanvasChild(0.62f * s, 0.08f * s,
                Embed.Comp(() => new ZuneType { Sig = sig, Side = s, BlockW = typeW, Accent = accent })
                // The accent is a FROZEN factory field, so an option flip must remount rather than re-push.
                with { Key = "zune-type:" + accentSlug }));

            float barW = 0.86f * s;
            var frac = sig.Frac;
            kids.Add(new CanvasChild(0.08f * s, 0.88f * s, new BoxEl
            {
                Width = barW,
                Height = 3f,
                Shrink = 0f,
                ZStack = true,
                Fill = Hex(0x333333),
                Children =
                [
                    // SCALED, not resized: a width write at tick rate would be a layout write; a transform write
                    // never leaves the compositor.
                    new BoxEl
                    {
                        Width = barW,
                        Height = 3f,
                        Shrink = 0f,
                        Fill = accent,
                        TransformOriginX = 0f,
                        TransformOriginY = 0.5f,
                        Transform = Prop.Of(() => Affine2D.Scale(frac.Value, 1f)),
                    },
                ],
            }));
        }

        return Canvas.Create(s, s, kids) with
        {
            Fill = deckBg,
            Corners = zune ? default : CornerRadius4.All(Radii.Card),
        };
    }

    // ── the platter group ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The record leaving its sleeve: one wrapper that translates and scales the whole disc, plus the dust
    /// puff that fires on the needle drop. Two bound channels for the entire load animation.</summary>
    static Element Platter(float d, string finish, RecordVariant variant, DeckSignals sig, PlaybackBridge bridge,
                           DeckHost host, Prop<ColorF> deckBg, bool small)
    {
        var slide = sig.Slide;
        float dd = d;                       // captured by value: the thunk reads no field and allocates nothing
        float thump = 0.08f * d;

        return new BoxEl
        {
            Width = d,
            Height = d,
            Shrink = 0f,
            ZStack = true,
            TransformOriginX = 0.5f,
            TransformOriginY = 0.5f,
            // A third of a diameter back into the sleeve, growing the last 4% as it clears it.
            Transform = Prop.Of(() =>
            {
                float e = DeckEase.SlideOut(slide.Value);
                float k = 0.96f + 0.04f * e;
                return Affine2D.Translation(-0.34f * dd * (1f - e), 0f).Multiply(Affine2D.Scale(k, k));
            }),
            // Still inside the sleeve = not drawn; the 1.6 factor has it fully opaque well before it stops moving.
            Opacity = Prop.Of(() => DeckEase.Clamp01(slide.Value * 1.6f)),
            Children =
            [
                Disc(d, finish, variant, sig, bridge, deckBg, small),
                Offset((d - thump) * 0.5f, (d - thump) * 0.5f,
                       Embed.Comp(() => new RecordThump { Host = host, Size = thump }) with { Key = "thump" }),
            ],
        };
    }

    /// <summary>The ONE rotating node. Everything printed on the record is a child of it, so the platter costs a
    /// single transform write per tick however many layers the finish has.</summary>
    static Element Disc(float d, string finish, RecordVariant variant, DeckSignals sig, PlaybackBridge bridge,
                        Prop<ColorF> deckBg, bool small)
    {
        bool picture = variant == RecordVariant.Picture;
        bool zune = variant == RecordVariant.Zune;
        var angle = sig.Angle0;
        var layers = new List<Element>(14);

        if (picture)
        {
            // A picture disc IS the artwork: no body fill and no label — just the cover, cut round, with the
            // faintest groove wash over it so it still reads as a pressing.
            layers.Add(CoverLeaf(sig, d, round: true, key: "picture-art"));
            layers.Add(new BoxEl
            {
                Width = d,
                Height = d,
                Shrink = 0f,
                Corners = Radii.Circle(d),
                Fill = Black(0.10f),
                BorderWidth = 1f,
                BorderColor = White(0.06f),
                Shadow = new ShadowSpec(34f, 14f, 0f, Black(0.45f)),
            });
        }
        else
        {
            var body = new BoxEl
            {
                Width = d,
                Height = d,
                Shrink = 0f,
                Corners = Radii.Circle(d),
                BorderWidth = 1f,
                BorderColor = White(0.06f),
                Shadow = new ShadowSpec(34f, 14f, 0f, Black(0.45f)),
            };
            layers.Add(finish switch
            {
                // The cover's own accent, deepened — BOUND, because the grading lands after the face is built.
                "album" => body with { Fill = AlbumVinyl(bridge) },
                // Translucent, not Acrylic: this node rotates 30 times a second, and a per-node backdrop layer
                // would resample and re-blur the plinth on every one of those frames for a tint a fill already
                // gives. Same colour as the mockup's rgba(40,44,54,.55).
                "clear" => body with { Fill = Rgba(40, 44, 54, 0.55f) },
                "splatter" => body with { Fill = Hex(SplatterBase) },
                "marble" => body with { Fill = Hex(MarbleBase) },
                _ => body with { Fill = Hex(VinylBlack) },
            });

            if (finish == "splatter")
            {
                float dot = 0.04f * d;
                AddDot(layers, d, dot, 0.30f, 0.25f, 0xFFD166);
                AddDot(layers, d, dot, 0.70f, 0.40f, 0xFFD166);
                AddDot(layers, d, dot, 0.60f, 0.78f, 0xEF476F);
                AddDot(layers, d, dot, 0.24f, 0.66f, 0xEF476F);
                AddDot(layers, d, dot, 0.82f, 0.70f, 0x06D6A0);
                AddDot(layers, d, dot, 0.45f, 0.12f, 0x06D6A0);
            }

            // The veining is the marble PNG's alpha, tinted white over the stone-coloured body.
            if (finish == "marble")
                layers.Add(DeckArt.Texture("marble-1024.png", d, d, Radii.Circle(d), White(0.9f)));

            // Grooves: one alpha texture, tinted to whatever reads as a groove on THIS finish — light ink on dark
            // vinyl, dark ink on a pale pressing.
            layers.Add(DeckArt.Texture("grooves-1024.png", d, d, Radii.Circle(d), finish switch
            {
                "clear" => White(0.12f),
                "album" => White(0.08f),
                "splatter" => Black(0.08f),
                "marble" => Black(0.18f),
                _ => White(0.035f),
            }));
        }

        // The sheen: one off-centre highlight that turns WITH the record, which is what sells it as a solid object
        // rather than a spinning picture.
        layers.Add(new BoxEl
        {
            Width = d,
            Height = d,
            Shrink = 0f,
            Corners = Radii.Circle(d),
            Gradient = Ui.RadialGradient(new Point2(0.30f, 0.25f), new Point2(0.9f, 0.9f),
                new GradientStop(0f, White(0.10f)),
                new GradientStop(0.55f, ColorF.Transparent),
                new GradientStop(1f, White(0.06f))),
        });

        if (!picture)
        {
            float label = TonearmGeometry.LabelRadiusFrac * d, lx = (d - label) * 0.5f;
            layers.Add(Offset(lx, lx, CoverLeaf(sig, label, round: true, key: "label-art")));
            layers.Add(Offset(lx, lx, DeckArt.Ring(label, zune ? 3f : 2f, zune ? White(0.9f) : Black(0.6f))));
        }

        // The spindle — or, on a 7", the big hole the deck itself shows through.
        float hole = small ? 0.20f * d : 0.05f * d;
        float hx = (d - hole) * 0.5f;
        layers.Add(Offset(hx, hx, small
            ? new BoxEl { Width = hole, Height = hole, Shrink = 0f, Corners = Radii.Circle(hole), Fill = deckBg }
            : new BoxEl
            {
                Width = hole,
                Height = hole,
                Shrink = 0f,
                Corners = Radii.Circle(hole),
                Fill = picture ? White(1f) : Hex(SpindleInk),
                BorderWidth = 1f,
                BorderColor = Black(0.5f),
            }));

        return new BoxEl
        {
            Width = d,
            Height = d,
            Shrink = 0f,
            ZStack = true,
            TransformOriginX = 0.5f,
            TransformOriginY = 0.5f,
            Transform = Prop.Of(() => Affine2D.Rotation(angle.Value * DeckArt.Deg2Rad)),
            Children = layers.ToArray(),
        };
    }

    static void AddDot(List<Element> into, float d, float dot, float fx, float fy, uint rgb)
        => into.Add(Offset(fx * d - dot * 0.5f, fy * d - dot * 0.5f, new BoxEl
        {
            Width = dot,
            Height = dot,
            Shrink = 0f,
            Corners = Radii.Circle(dot),
            Gradient = Ui.RadialGradient(new GradientStop(0f, Hex(rgb)), new GradientStop(1f, Hex(rgb, 0.75f))),
        }));

    /// <summary>The album-colour vinyl. The url is resolved INSIDE the thunk on purpose: reading the track at build
    /// time would subscribe <c>DeckHost</c> and rebuild the whole face on every track change, when the only thing
    /// that has to change is one fill.</summary>
    static Prop<ColorF> AlbumVinyl(PlaybackBridge bridge) => Prop.Of(() =>
    {
        var track = bridge.CurrentTrack.Value;
        string? url = track is null ? null : DeckArt.CoverUrl(track);
        // Watch INSIDE the thunk: this subscribes the bind, not the face's render.
        if (url is { Length: > 0 }) _ = SpotifyLive.CoverColorPlane.Current.Watch(url).Value;
        var scheme = Surfaces.SchemeFor(url);
        var c = scheme is { } sc ? WaveePalette.Accent(sc) : Hex(AlbumFallback);
        return new ColorF(c.R * 0.55f, c.G * 0.55f, c.B * 0.55f, 1f);   // deepened: dyed vinyl, not paint
    });

    // ── the tonearm ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Where the headshell was last dragged to, so the release can commit it without a pointer it no
    /// longer has. Allocated ONCE, at build — a gesture must not allocate per move.</summary>
    sealed class ArmGrip { public long LastMs; }

    static Element Tonearm(float d, float platterX, float platterY, float armW, float armH, float armX, float armY,
                           bool turntable, DeckSignals sig, PlaybackBridge bridge, DeckHost host)
    {
        var angle1 = sig.Angle1;
        var lift = sig.Lift;

        var arm = turntable ? Hex(0xF2F4F7) : Hex(ArmInk);
        var armDark = turntable ? Hex(0x5D6470) : Hex(ArmDarkInk);

        float tubeW = turntable ? 0.07f * armW : 0.09f * armW;
        float tubeH = 0.62f * armH, tubeX = 0.455f * armW, tubeY = 0.09f * armH;
        var tubeGradient = turntable
            ? Ui.LinearGradient(0f,
                new GradientStop(0f, Hex(0x5D6470)), new GradientStop(0.4f, Hex(0xF2F4F7)),
                new GradientStop(0.6f, Hex(0xC3C8D1)), new GradientStop(1f, Hex(0x5D6470)))
            : Ui.LinearGradient(0f,
                new GradientStop(0f, armDark), new GradientStop(0.5f, arm), new GradientStop(1f, armDark));

        float pivotD = 0.44f * armW;
        float headW = 0.22f * armW, headH = 0.12f * armH;
        float hitW = 0.44f * armW, hitH = 0.20f * armH, hitX = 0.28f * armW, hitY = 0.64f * armH;

        // ── the drag: a point on the headshell → a point on the deck → a groove fraction → milliseconds ─────────
        var grip = new ArmGrip();
        var gesture = host.Gesture;
        float cx = platterX + d * 0.5f, cy = platterY + d * 0.5f;
        long MsAt(Point2 p)
        {
            // Peek, never Value: these run from pointer callbacks, where a subscription would attach the face's
            // bind graph to the gesture.
            var (dx, dy) = TonearmGeometry.ArmLocalToDeck(p.X + hitX, p.Y + hitY, angle1.Peek(), armX, armY, armW, armH);
            float frac = TonearmGeometry.FracFromDeckPoint(dx, dy, cx, cy, d);
            return (long)(frac * bridge.DurationMs.Peek());
        }

        var lifted = new BoxEl
        {
            Width = armW,
            Height = armH,
            Shrink = 0f,
            ZStack = true,
            TransformOriginX = 0.5f,
            TransformOriginY = 0.08f,
            // The cue lever: the arm rises a hair and grows a hair — the parallax that reads as "off the record".
            // The buffering bob overshoots 1, so the travel is clamped at 1.4 rather than at 1.
            Transform = Prop.Of(() =>
            {
                float l = MathF.Min(lift.Value, 1.4f);
                float k = 1f + 0.015f * l;
                return Affine2D.Translation(0f, -0.015f * armH * l).Multiply(Affine2D.Scale(k, k));
            }),
            Children =
            [
                // A shadow-only twin of the tube. ShadowSpec is not a bindable channel, so the shadow's STRENGTH is
                // this node's opacity instead: the arm casts a shadow only while it is up.
                Offset(tubeX, tubeY, new BoxEl
                {
                    Width = tubeW,
                    Height = tubeH,
                    Shrink = 0f,
                    Corners = CornerRadius4.All(4f),
                    Fill = ColorF.Transparent,
                    Shadow = new ShadowSpec(10f, 10f, 0f, Black(0.45f)),
                    Opacity = Prop.Of(() => 0.45f * DeckEase.Clamp01(lift.Value)),
                }),
                Offset(tubeX, tubeY, new BoxEl
                {
                    Width = tubeW,
                    Height = tubeH,
                    Shrink = 0f,
                    Corners = CornerRadius4.All(4f),
                    Gradient = tubeGradient,
                }),
                // The pivot sits exactly on the rotation origin (.5, .08) — that is what makes the swing read as a
                // bearing rather than a slide.
                Offset(armW * 0.5f - pivotD * 0.5f, armH * 0.08f - pivotD * 0.5f, new BoxEl
                {
                    Width = pivotD,
                    Height = pivotD,
                    Shrink = 0f,
                    Corners = Radii.Circle(pivotD),
                    Gradient = Ui.RadialGradient(new GradientStop(0f, arm), new GradientStop(1f, armDark)),
                    Shadow = new ShadowSpec(8f, 3f, 0f, Black(0.35f)),
                }),
                Offset(0.39f * armW, 0.69f * armH, new BoxEl
                {
                    Width = headW,
                    Height = headH,
                    Shrink = 0f,
                    ZStack = true,
                    Corners = CornerRadius4.All(3f),
                    Rotation = -18f,
                    Fill = turntable ? Hex(0x2B2E34) : arm,
                    BorderWidth = turntable ? 1f : 0f,
                    BorderColor = turntable ? Hex(0x7A808A) : ColorF.Transparent,
                    Children =
                    [
                        Offset((headW - 2f) * 0.5f, headH - 8f,
                               new BoxEl { Width = 2f, Height = 8f, Shrink = 0f, Fill = Hex(0xDD3333) }),
                    ],
                }),
                // The grip: transparent, and deliberately larger than the cartridge it covers — a 5-DIP stylus is
                // not a pointer target.
                Offset(hitX, hitY, new BoxEl
                {
                    Width = hitW,
                    Height = hitH,
                    Shrink = 0f,
                    Fill = ColorF.Transparent,
                    Cursor = CursorId.Hand,
                    OnPointerDown = p => { long ms = MsAt(p); grip.LastMs = ms; gesture.Begin(ms); },
                    OnDrag = p => { long ms = MsAt(p); grip.LastMs = ms; gesture.Move(ms); },
                    OnPointerReleased = _ => gesture.Commit(grip.LastMs),
                }),
            ],
        };

        return new BoxEl
        {
            Width = armW,
            Height = armH,
            Shrink = 0f,
            ZStack = true,
            TransformOriginX = 0.5f,
            TransformOriginY = 0.08f,
            Transform = Prop.Of(() => Affine2D.Rotation(angle1.Value * DeckArt.Deg2Rad)),
            Children = [lifted],
        };
    }

    // ── shared helpers ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Absolute placement inside a ZStack — the same offset wrapper <see cref="Canvas.Create"/> uses,
    /// without the canvas's <c>ClipToBounds</c> (a record's shadow has to leave its own box).</summary>
    static Element Offset(float x, float y, Element child)
        => new BoxEl { OffsetX = x, OffsetY = y, Children = [child] };

    static Element CoverLeaf(DeckSignals sig, float size, bool round, string key)
        => Embed.Comp(() => new SleeveArt { Sig = sig, Size = size, Round = round }) with { Key = key };

    static ColorF Hex(uint rgb, float a = 1f)
        => new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, a);

    static ColorF Rgba(byte r, byte g, byte b, float a) => new(r / 255f, g / 255f, b / 255f, a);
    static ColorF White(float a) => new(1f, 1f, 1f, a);
    static ColorF Black(float a) => new(0f, 0f, 0f, a);

    static ColorF ZuneAccent(string slug) => slug switch
    {
        "orange" => Hex(0xFF7A1A),
        "green" => Hex(0x8CBF26),
        "blue" => Hex(0x1BA1E2),
        _ => Hex(0xF0568C),
    };

    // ── the three leaves that re-render ──────────────────────────────────────────────────────────────────────────

    /// <summary>The cover, wherever this family shows one: the sleeve behind the disc, the label on it and the
    /// picture disc's whole face. Keyed on <c>CoverGen</c> rather than on the track, so the art changes when the
    /// MECHANISM says so (mid-sleeve) and a same-album advance leaves it alone.</summary>
    sealed class SleeveArt : Component
    {
        public required DeckSignals Sig;
        public required float Size;
        public required bool Round;

        public override Element Render()
        {
            int gen = Sig.CoverGen.Value;          // the swap instant, authored by the deck's model
            var track = UseContext(PlaybackBridge.Slot)?.CurrentTrack.Value;
            string? url = track is null ? null : DeckArt.CoverUrl(track);
            float size = Size;
            var corners = Round ? Radii.Circle(size) : default;

            // The placeholder is a VALUE, not DeckArt.CoverWash: ImageEl.Placeholder is a plain ColorF, and this
            // leaf re-renders on the swap anyway, so the wash is resolved exactly when it is needed.
            var art = DeckArt.Cover(url, size, corners, NowPlayingPanel.HeroWashColor(url), track?.Image?.BlurHash);

            // The generation IS the key: a new album remounts this leaf, and the remount is the cross-fade.
            return new BoxEl
            {
                Key = gen.ToString(),
                Width = size,
                Height = size,
                Shrink = 0f,
                ZStack = true,
                Corners = corners,
                Animate = CoverFade,
                Children = [art],
            };
        }
    }

    /// <summary>The needle drop: a dust puff that expands and fades ONCE per thump. It owns no timer and no signal
    /// — the ring's node handle is captured at realize and driven straight from the animation scheduler, so a
    /// thump costs three one-shot tracks and zero re-renders.</summary>
    sealed class RecordThump : Component
    {
        public required DeckHost Host;
        public required float Size;

        // Static arrays: a one-shot must not allocate its own keyframes on the edge that fires it.
        static readonly Keyframe[] FadeOut = [new(0f, 0.9f, Easing.Linear), new(1f, 0f, Easing.SmoothOut)];
        static readonly Keyframe[] Expand = [new(0f, 0.2f, Easing.SmoothOut), new(1f, 1.6f, Easing.SmoothOut)];
        const float PuffMs = 500f;

        public override Element Render()
        {
            var nodeRef = UseRef<NodeHandle>(default);
            var host = Host;
            float size = Size;

            UseLayoutEffect(() =>
            {
                var anim = Context.Anim;
                var scene = Context.Scene;
                if (anim is null || scene is null) return null;

                void OnThump()
                {
                    var node = nodeRef.Value;
                    if (node.IsNull || !scene.IsLive(node)) return;
                    anim.Keyframes(node, AnimChannel.Opacity, FadeOut, PuffMs);
                    anim.Keyframes(node, AnimChannel.ScaleX, Expand, PuffMs);
                    anim.Keyframes(node, AnimChannel.ScaleY, Expand, PuffMs);
                }

                host.ThumpRequested += OnThump;
                return () => host.ThumpRequested -= OnThump;
            }, DepKey.From(0));

            return new BoxEl
            {
                Width = size,
                Height = size,
                Shrink = 0f,
                Corners = Radii.Circle(size),
                BorderWidth = MathF.Max(1f, size * 0.08f),
                BorderColor = White(0.7f),
                Opacity = 0f,                      // at rest the puff is not there at all
                TransformOriginX = 0.5f,
                TransformOriginY = 0.5f,
                OnRealized = h => nodeRef.Value = h,
            };
        }
    }

    /// <summary>The Zune's poster: a lowercase title whose tail carries the accent, one grey line of artist and
    /// clock, and nothing else. The clock's TEXT is a bound channel in a fixed-width slot — it is the only thing on
    /// this deck that relayouts, and the fixed slot keeps that relayout inside the deck's firewall.</summary>
    sealed class ZuneType : Component
    {
        public required DeckSignals Sig;
        public required float Side;
        public required float BlockW;
        public required ColorF Accent;

        const string Display = "Segoe UI Variable Display";

        public override Element Render()
        {
            _ = Sig.CoverGen.Value;                // the lettering swaps WITH the artwork, not with the track
            var bridge = UseContext(PlaybackBridge.Slot);
            var track = bridge?.CurrentTrack.Value;

            float s = Side;
            string title = (track?.Title ?? "").ToLowerInvariant();
            string artist = track is null ? "" : DetailFormat.ArtistNames(track.Artists);
            float titleSize = 0.11f * s, lineSize = 0.036f * s;

            // The accent lands on the last third of the title. Two runs rather than one span: a span would reshape
            // the whole line to move the boundary, and this boundary only moves when the title does.
            int cut = title.Length >= 3 ? title.Length - title.Length / 3 : title.Length;

            return new BoxEl
            {
                Width = BlockW,
                Direction = 1,
                Gap = 0.02f * s,
                ClipToBounds = true,
                Children =
                [
                    new BoxEl
                    {
                        Direction = 0,
                        Children =
                        [
                            new TextEl(title[..cut])
                            {
                                Size = titleSize, Weight = 300, FontFamily = Display,
                                Color = White(1f), MaxLines = 1, Trim = TextTrim.Clip,
                            },
                            new TextEl(title[cut..])
                            {
                                Size = titleSize, Weight = 300, FontFamily = Display,
                                Color = Accent, MaxLines = 1, Trim = TextTrim.Clip,
                            },
                        ],
                    },
                    new BoxEl
                    {
                        Direction = 0,
                        AlignItems = FlexAlign.Center,
                        Children =
                        [
                            new TextEl(artist.Length > 0 ? artist + " · " : "")
                            {
                                Size = lineSize, Color = Hex(ZuneGrey), MaxLines = 1, Trim = TextTrim.Clip,
                            },
                            // A fixed slot, so a 0:59 → 1:00 tick cannot move the line beside it.
                            new BoxEl
                            {
                                Width = 0.10f * s,
                                Shrink = 0f,
                                Children = [bridge is null ? new TextEl("0:00") { Size = lineSize, Color = Hex(ZuneGrey) } : Clock(bridge, lineSize)],
                            },
                        ],
                    },
                ],
            };
        }

        /// <summary>The playhead read INSIDE the thunk: a component that read it would rebuild this whole block at
        /// tick rate for a string that changes once a second.</summary>
        static Element Clock(PlaybackBridge bridge, float size)
            => new TextEl(Prop.Of(() => PlayerBarContent.Fmt(bridge.PositionMs.Value)))
            {
                Size = size,
                Color = Hex(ZuneGrey),
                MaxLines = 1,
            };
    }
}
