// ── Shell/Deck.Faces.cs ────────────────────────────────────────────────────────────────────────────────────────────
// the eight non-record faces (turntable, Zune, cassette, reel, CD, MD, iPod, VU, Winamp, WMP, canvas)
//
// Role: UI
// Owner: K
// Wave: 4
// Budget: 2400 lines
// Spec: ch 23 §9 (2,300-2,500)
//
// ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// THE OTHER FACES. The record family (Record / Turntable / Zune / Picture) is `Deck.UI.cs`'s; this file holds the
// cassette, the reel-to-reel, the CD/MiniDisc, the iPod, the hi-fi VU, Winamp, the WMP visualizer and Canvas drift.
//
// THE FACE CONTRACT (the same for all of them): a static builder, not a component, run once per host render —
//   static Element Build(in Rail.PlayerCatalog.Preset preset, float side, Slab sig[, Host host])
// Options are read into plain locals before the first thunk exists (an `in` parameter cannot be captured). Every
// dimension is a fraction of `side`. Everything that moves is a bound Transform/Opacity/Text over the slab; the few
// things that change SHAPE (a label's lettering, a disc's art, the iPod's screen) are tiny keyed components at the
// leaves, and the live item is read INSIDE thunks and leaves, never in a face body.
//
// The palettes are literal hex — a deck is a physical object, identical in light and dark (ch 23 §4.1). The only
// localised string a face renders is the WMP preset caption; every other string is part of the device's own look.

using System.Collections.Generic;
using System.Globalization;
using System.Text;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Render;
using FluentGpu.Signals;

namespace Wavee;

public static partial class Deck
{
    // ══ 1. CASSETTE ══════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>A compact cassette whose hubs turn at the speed <see cref="TapeModel"/> says and whose shell ejects and
    /// re-loads on a new album. Six bound channels: the eject slide, the two pack radii, the two hub angles.</summary>
    static class CassetteFace
    {
        public static Element Build(in Rail.PlayerCatalog.Preset preset, float side, Slab sig)
        {
            string shell = Rail.PlayerPrefs.ChoiceSlug(in preset, "shell");
            string labelStyle = Rail.PlayerPrefs.ChoiceSlug(in preset, "label");
            string sideSlug = Rail.PlayerPrefs.ChoiceSlug(in preset, "side");

            float s = side;
            float shellW = 0.86f * s, shellH = 0.56f * s, shellX = 0.07f * s, shellY = 0.21f * s;
            float labelW = 0.90f * shellW, labelH = 0.30f * shellH, labelX = 0.05f * shellW, labelY = 0.06f * shellH;
            float winW = 0.62f * shellW, winH = 0.34f * shellH, winX = 0.19f * shellW, winY = 0.40f * shellH;
            float packD = 0.26f * s, reelD = 0.11f * s, badgeD = 0.10f * labelW;
            var brown = Hex(0x3A2B1C);
            var brownTint = Hex(0x2A1D11, 0.55f);

            var labelSurface = new BoxEl
            {
                Width = labelW, Height = labelH, Shrink = 0f, Corners = CornerRadius4.All(3f),
                Fill = labelStyle switch { "chrome" => ColorF.Transparent, "hand" => Hex(0xFFFDF5), _ => Hex(0xF2E9D8) },
                Gradient = labelStyle == "chrome" ? GradientSpec.Vertical(Hex(0xE8E8EC), Hex(0xB9BCC4)) : null,
                // The style is a FROZEN factory field: an option flip remounts, so the key carries it.
                Children = [Embed.Comp(() => new CassetteLabel { Sig = sig, Style = labelStyle, W = labelW, H = labelH, BadgeD = badgeD })
                    with { Key = "cassette-label:" + labelStyle }],
            };

            // The window is the cutout the tape shows through, so it CLIPS: a full pack is wider than the slot.
            var window = new BoxEl
            {
                Width = winW, Height = winH, Shrink = 0f, Corners = CornerRadius4.All(5f), Fill = Hex(0x14151A),
                BorderWidth = 2f, BorderColor = Hex(0xFFFFFF, 0.08f), ClipToBounds = true,
                Children =
                [
                    Canvas.Create(winW, winH,
                    [
                        .. Hub(sig, 0.31f * shellW - winX, winH * 0.5f, packD, reelD, s, brown, brownTint, left: true),
                        .. Hub(sig, 0.69f * shellW - winX, winH * 0.5f, packD, reelD, s, brown, brownTint, left: false),
                    ]),
                ],
            };

            float screw = 0.012f * s;
            var shellBody = new BoxEl
            {
                Width = shellW, Height = shellH, Shrink = 0f, Corners = CornerRadius4.All(6f),
                Fill = shell switch
                {
                    "clear" => Hex(0xBECDE6, 0.28f),
                    "cream" => Hex(0xE9E2D0),
                    "smoke" => Hex(0x464854, 0.85f),
                    _ => Hex(0x1D1F24),
                },
                // The clear shell is the one that genuinely reads THROUGH to the ground; the others are plates.
                Acrylic = shell == "clear" ? new AcrylicSpec(Hex(0xBECDE6), 0.28f, 18f, 0.02f, 0.92f, Hex(0xBECDE6, 0.28f)) : null,
                BorderWidth = shell == "clear" ? 1f : 0f,
                BorderColor = shell == "clear" ? Hex(0xFFFFFF, 0.35f) : ColorF.Transparent,
                Shadow = new ShadowSpec(30f, 14f, 0f, Black(0.5f)),
                Children =
                [
                    Canvas.Create(shellW, shellH,
                    [
                        new CanvasChild(labelX, labelY, labelSurface),
                        new CanvasChild(labelX + labelW - badgeD - 0.02f * shellW, labelY + (labelH - badgeD) * 0.5f, SideBadge(sideSlug, badgeD)),
                        new CanvasChild(winX, winY, window),
                        new CanvasChild(0.20f * shellW, 0.81f * shellH, new BoxEl { Width = 0.60f * shellW, Height = 0.025f * shellH, Shrink = 0f, Fill = brown }),
                        new CanvasChild(0.26f * shellW, shellH - 0.16f * shellH, Foot(0.48f * shellW, 0.16f * shellH)),
                        new CanvasChild(0.03f * shellW, 0.04f * shellH, Circle(screw, White(0.22f))),
                        new CanvasChild(shellW - 0.03f * shellW - screw, 0.04f * shellH, Circle(screw, White(0.22f))),
                        new CanvasChild(0.03f * shellW, shellH - 0.04f * shellH - screw, Circle(screw, White(0.22f))),
                        new CanvasChild(shellW - 0.03f * shellW - screw, shellH - 0.04f * shellH - screw, Circle(screw, White(0.22f))),
                        new CanvasChild(0.02f * shellW, 0.86f * shellH, new TextEl("wavee · c-60 · type i")
                        {
                            Width = 0.17f * s, Height = 0.030f * s, Size = 0.018f * s, Weight = 500,
                            Color = shell == "cream" ? Black(0.40f) : White(0.35f), MaxLines = 1, Trim = TextTrim.Clip,
                        }),
                    ]),
                ],
            };

            // The eject: the whole shell rises and fades, the label re-letters at the top, and it drops back in.
            var slide = sig.Slide;
            var shellWrapper = new BoxEl
            {
                Width = shellW, Height = shellH, Shrink = 0f,
                Transform = Prop.Of(() => Affine2D.Translation(0f, -0.4f * s * (1f - slide.Value))),
                Opacity = slide,
                Children = [shellBody],
            };

            return Canvas.Create(s, s,
            [
                new CanvasChild(0f, 0f, new BoxEl { Width = s, Height = s, Shrink = 0f, Gradient = GradientSpec.Vertical(Hex(0x2B2F3A), Hex(0x1C1F27)) }),
                new CanvasChild(shellX, shellY, shellWrapper),
            ]);
        }

        /// <summary>One hub: the tape pack (scale-bound to its radius) under the spoked reel. Both hubs share the full
        /// pack width, so a scale of 1 means "full" on either side.</summary>
        static CanvasChild[] Hub(Slab sig, float cx, float cy, float packD, float reelD, float s, ColorF brown, ColorF brownTint, bool left)
        {
            var packScale = left ? sig.Aux0 : sig.Aux1;
            var angle = left ? sig.Angle0 : sig.Angle1;
            var pack = new BoxEl
            {
                Width = packD, Height = packD, Shrink = 0f, Corners = Radii.Circle(packD), Fill = brown,
                TransformOriginX = 0.5f, TransformOriginY = 0.5f,
                Transform = Prop.Of(() => Affine2D.Scale(packScale.Value, packScale.Value)),
                Children = [Texture("grooves-1024.png", packD, packD, Radii.Circle(packD), brownTint)],
            };
            float spokeW = 0.10f * s, spokeH = 0.02f * s, hubD = 0.035f * s;
            float sx = (reelD - spokeW) * 0.5f, sy = (reelD - spokeH) * 0.5f;
            var reel = new BoxEl
            {
                Width = reelD, Height = reelD, Shrink = 0f, Corners = Radii.Circle(reelD),
                BorderWidth = 0.014f * s, BorderColor = Hex(0xFFFFFF, 0.92f),
                TransformOriginX = 0.5f, TransformOriginY = 0.5f,
                Transform = Prop.Of(() => Affine2D.Rotation(angle.Value * Deg2Rad)),
                Children =
                [
                    Canvas.Create(reelD, reelD,
                    [
                        new CanvasChild(sx, sy, Spoke(spokeW, spokeH, 0f)),
                        new CanvasChild(sx, sy, Spoke(spokeW, spokeH, 60f)),
                        new CanvasChild(sx, sy, Spoke(spokeW, spokeH, 120f)),
                        new CanvasChild((reelD - hubD) * 0.5f, (reelD - hubD) * 0.5f, Circle(hubD, White(1f))),
                    ]),
                ],
            };
            return [new CanvasChild(cx - packD * 0.5f, cy - packD * 0.5f, pack), new CanvasChild(cx - reelD * 0.5f, cy - reelD * 0.5f, reel)];
        }

        static Element Spoke(float w, float h, float deg) => new BoxEl
        {
            Width = w, Height = h, Shrink = 0f, Fill = White(1f), Rotation = deg, TransformOriginX = 0.5f, TransformOriginY = 0.5f,
        };

        /// <summary>The recessed transport foot with its four drive holes.</summary>
        static Element Foot(float w, float h)
        {
            float hole = h * 0.34f, gap = (w - 4f * hole) / 5f, y = (h - hole) * 0.5f;
            return new BoxEl
            {
                Width = w, Height = h, Shrink = 0f, Corners = CornerRadius4.All(3f), Fill = Black(0.25f),
                Children =
                [
                    Canvas.Create(w, h,
                    [
                        new CanvasChild(gap, y, Circle(hole, Black(0.45f))),
                        new CanvasChild(gap * 2f + hole, y, Circle(hole, Black(0.45f))),
                        new CanvasChild(gap * 3f + hole * 2f, y, Circle(hole, Black(0.45f))),
                        new CanvasChild(gap * 4f + hole * 3f, y, Circle(hole, Black(0.45f))),
                    ]),
                ],
            };
        }

        static Element SideBadge(string sideSlug, float d) => new BoxEl
        {
            Width = d, Height = d, Shrink = 0f, Corners = Radii.Circle(d), Fill = Hex(0x23262B, 0.10f),
            BorderWidth = 1f, BorderColor = Hex(0x23262B, 0.45f),
            // TextEl has no horizontal alignment: the badge centres its glyph the flex way.
            Justify = FlexJustify.Center, AlignItems = FlexAlign.Center,
            Children = [new TextEl(sideSlug == "b" ? "B" : "A") { Height = d * 0.62f, Size = d * 0.52f, Weight = 700, Color = Hex(0x23262B), MaxLines = 1, Shrink = 0f }],
        };

        /// <summary>The two lines of ink on the label — the only part of the cassette that re-renders. Latched: a new
        /// album re-letters at the top of the eject, a same-album advance re-letters in place, an unknown row never
        /// blanks the label.</summary>
        sealed class CassetteLabel : Component
        {
            public required Slab Sig;
            public required string Style;
            public required float W, H, BadgeD;
            Latch _latch;

            public override Element Render()
            {
                _latch.Update(Sig, image: false);
                string title = TitleOf(_latch.Shown), artist = ArtistsOf(_latch.Shown), album = AlbumNameOf(_latch.Shown);
                string second = album.Length > 0 && artist.Length > 0 ? artist + " · " + album : artist.Length > 0 ? artist : album;
                if (Style == "chrome") { title = title.ToUpperInvariant(); second = second.ToUpperInvariant(); }
                string family = Style switch { "type1" => "Courier New", "hand" => "Segoe Print", _ => "Segoe UI" };
                float pad = W * 0.045f, textW = W - pad * 2f - BadgeD;

                var children = new List<CanvasChild>(6);
                if (Style == "hand")
                    for (int i = 0; i < 3; i++)   // three ruled hairlines, under the ink
                        children.Add(new CanvasChild(pad, H * (0.30f + 0.25f * i), new BoxEl { Width = W - pad * 2f, Height = 1f, Shrink = 0f, Fill = Hex(0x9FC0E8) }));
                children.Add(new CanvasChild(pad, H * 0.16f, new TextEl(title)
                {
                    Width = textW, Height = H * 0.34f, Size = H * 0.26f, Weight = 700, FontFamily = family,
                    Color = Hex(0x23262B), MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                }));
                children.Add(new CanvasChild(pad, H * 0.56f, new TextEl(second)
                {
                    Width = textW, Height = H * 0.30f, Size = H * 0.20f, Weight = 400, FontFamily = family,
                    Color = Hex(0x23262B, 0.72f), MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                }));
                return Canvas.Create(W, H, children);
            }
        }
    }

    // ══ 2. REEL-TO-REEL ══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Two open flanges over their packs, the threaded tape, the head block and a mechanical counter —
    /// <see cref="TapeModel"/> with <see cref="TapeKind.Reel"/>.</summary>
    static class ReelFace
    {
        /// <summary>The threaded tape in a 100-unit view box — static geometry (the reels move, the tape does not),
        /// minted once.</summary>
        static readonly PathData TapeGeometry = PathDataParser.Parse(
            "M26 44 L28 74 L36 80 L64 80 L72 74 L74 44", PathContentEpoch.Mint(), FillRule.NonZero, 100f, 100f);

        /// <summary>One string per whole second, wrapping at 10 000 s like the real counter.</summary>
        static readonly FormatCache<int> CounterCache = FormatCache.Create<int>();

        public static Element Build(in Rail.PlayerCatalog.Preset preset, float side, Slab sig)
        {
            string reelStyle = Rail.PlayerPrefs.ChoiceSlug(in preset, "reel");
            float s = side, reel = 0.40f * s, reelY = 0.07f * s;
            var flangeTint = reelStyle switch { "black" => Hex(0x2A2C33), "clear" => Hex(0xC8D7F0, 0.45f), _ => Hex(0xC9CDD4) };
            float guide = 0.05f * s, guideY = s - 0.24f * s - guide * 0.5f;
            float headW = 0.36f * s, headH = 0.18f * s, counterW = 0.20f * s, counterH = 0.072f * s;

            return Canvas.Create(s, s,
            [
                new CanvasChild(0f, 0f, new BoxEl { Width = s, Height = s, Shrink = 0f, Gradient = GradientSpec.Vertical(Hex(0x2A2D33), Hex(0x1B1D22)) }),
                new CanvasChild(0.06f * s, reelY, Reel(sig, reel, s, flangeTint, left: true)),
                new CanvasChild(s - 0.06f * s - reel, reelY, Reel(sig, reel, s, flangeTint, left: false)),
                new CanvasChild(0.26f * s - guide * 0.5f, guideY, Guide(guide)),
                new CanvasChild(0.74f * s - guide * 0.5f, guideY, Guide(guide)),
                new CanvasChild((s - headW) * 0.5f, s - 0.09f * s - headH, HeadBlock(headW, headH)),
                // Threaded = IN FRONT of the guides and the heads.
                new CanvasChild(0f, 0f, new PathEl
                {
                    Geometry = TapeGeometry, Width = s, Height = s, Shrink = 0f, ViewBoxW = 100f, ViewBoxH = 100f,
                    Fill = ColorF.Transparent, StrokeColor = Hex(0x5A4028), Stroke = new StrokeStyle(1.2f, LineCap.Round, LineJoin.Round),
                }),
                new CanvasChild(s - 0.05f * s - counterW, s - 0.055f * s - counterH, Counter(sig, counterW, counterH)),
            ]);
        }

        static Element Reel(Slab sig, float d, float s, ColorF flangeTint, bool left)
        {
            float packD = 0.92f * d, hubD = 0.22f * d;
            var packScale = left ? sig.Aux0 : sig.Aux1;
            var angle = left ? sig.Angle0 : sig.Angle1;
            return Canvas.Create(d, d,
            [
                new CanvasChild((d - packD) * 0.5f, (d - packD) * 0.5f, new BoxEl
                {
                    Width = packD, Height = packD, Shrink = 0f, Corners = Radii.Circle(packD), Fill = Hex(0x4A3520),
                    TransformOriginX = 0.5f, TransformOriginY = 0.5f,
                    Transform = Prop.Of(() => Affine2D.Scale(packScale.Value, packScale.Value)),
                    Children = [Texture("grooves-1024.png", packD, packD, Radii.Circle(packD), Hex(0x2A1D11, 0.55f))],
                }),
                // The flange turns; its PNG is a static child, so the only per-frame write is this transform.
                new CanvasChild(0f, 0f, new BoxEl
                {
                    Width = d, Height = d, Shrink = 0f, TransformOriginX = 0.5f, TransformOriginY = 0.5f,
                    Transform = Prop.Of(() => Affine2D.Rotation(angle.Value * Deg2Rad)),
                    Children = [Texture("reel-flange-512.png", d, d, Radii.Circle(d), flangeTint)],
                }),
                // Fallback rim: a missing flange still reads as a reel, not a brown disc.
                new CanvasChild(0f, 0f, Ring(d, 0.012f * s, White(0.22f))),
                new CanvasChild((d - hubD) * 0.5f, (d - hubD) * 0.5f, new BoxEl
                {
                    Width = hubD, Height = hubD, Shrink = 0f, Corners = Radii.Circle(hubD),
                    Gradient = Ui.RadialGradient(new GradientStop(0f, Hex(0xE9EBEF)), new GradientStop(1f, Hex(0x8A8F99))),
                }),
            ]);
        }

        static Element Guide(float d) => new BoxEl
        {
            Width = d, Height = d, Shrink = 0f, Corners = Radii.Circle(d), Fill = Hex(0x1E2026), BorderWidth = d * 0.16f, BorderColor = Hex(0xA9AEB8),
        };

        /// <summary>Erase / record / play: three faces in a machined housing.</summary>
        static Element HeadBlock(float w, float h)
        {
            float headW = w * 0.14f, headH = h * 0.52f, y = (h - headH) * 0.5f, gap = (w - headW * 3f) / 4f;
            return new BoxEl
            {
                Width = w, Height = h, Shrink = 0f, Corners = CornerRadius4.All(6f),
                Gradient = GradientSpec.Vertical(Hex(0x3B3F47), Hex(0x23262C)), Shadow = new ShadowSpec(18f, 8f, 0f, Black(0.45f)),
                Children =
                [
                    Canvas.Create(w, h,
                    [
                        new CanvasChild(gap, y, Head(headW, headH)),
                        new CanvasChild(gap * 2f + headW, y, Head(headW, headH)),
                        new CanvasChild(gap * 3f + headW * 2f, y, Head(headW, headH)),
                    ]),
                ],
            };
        }

        static Element Head(float w, float h) => new BoxEl
        {
            Width = w, Height = h, Shrink = 0f, Corners = CornerRadius4.All(2f),
            Gradient = GradientSpec.Vertical(Hex(0xB9BEC8), Hex(0x6B7079)), BorderWidth = 1f, BorderColor = Black(0.35f),
        };

        /// <summary>The four-digit counter: a bound text over the playhead, one cached string per second.</summary>
        static Element Counter(Slab sig, float w, float h) => new BoxEl
        {
            Width = w, Height = h, Shrink = 0f, Corners = CornerRadius4.All(3f), Fill = Hex(0x1A0D00),
            BorderWidth = 1f, BorderColor = Black(0.6f), Justify = FlexJustify.Center, AlignItems = FlexAlign.Center,
            Children =
            [
                new TextEl(Prop.Of(() =>
                {
                    int sec = (int)(Math.Max(0L, PlayheadMs(sig)) / 1000L % 10_000L);
                    return CounterCache.Get(sec, static v => v.ToString("D4", CultureInfo.InvariantCulture));
                }))
                {
                    Height = h * 0.72f, Size = h * 0.56f, Weight = 600, FontFamily = "Consolas", Color = Hex(0xFFB000),
                    CharSpacing = 120f, MaxLines = 1, Shrink = 0f,
                },
            ],
        };
    }

    // ══ 3. CD / MINIDISC ═════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>A CLV spindle (<see cref="DiscModel"/>), a laser sled that walks outward with the playhead and a tray
    /// that ejects on a new album. The <c>format</c> option swaps the housing: a bare disc, or a MiniDisc cartridge whose
    /// window shows a slice of the same disc.</summary>
    static class CdFace
    {
        public static Element Build(in Rail.PlayerCatalog.Preset preset, float side, Slab sig)
        {
            bool md = Rail.PlayerPrefs.ChoiceSlug(in preset, "format") == "md";
            float s = side;
            var kids = new List<CanvasChild>(5)
            {
                new(0f, 0f, new BoxEl { Width = s, Height = s, Shrink = 0f, Gradient = GradientSpec.Vertical(Hex(0x22252C), Hex(0x14161B)) }),
                new(0.06f * s, 0.05f * s, new TextEl(md ? "MD · ATRAC" : "COMPACT DISC · DIGITAL AUDIO")
                {
                    Width = 0.80f * s, Height = 0.034f * s, Size = 0.021f * s, Weight = 600, CharSpacing = 180f,
                    Color = White(0.45f), MaxLines = 1, Trim = TextTrim.Clip, Shrink = 0f,
                }),
            };
            if (md) MiniDisc(kids, sig, s); else CompactDisc(kids, sig, s);
            return Canvas.Create(s, s, kids);
        }

        static void CompactDisc(List<CanvasChild> kids, Slab sig, float s)
        {
            float d = 0.72f * s, dx = 0.14f * s, dy = 0.12f * s, cx = dx + d * 0.5f, cy = dy + d * 0.5f, sled = 0.03f * d;
            var slide = sig.Slide;
            var aux = sig.Aux0;
            kids.Add(new CanvasChild(dx, dy, new BoxEl
            {
                Width = d, Height = d, Shrink = 0f, Corners = Radii.Circle(d),
                Transform = Prop.Of(() => Affine2D.Translation(0f, 0.3f * s * (1f - slide.Value))),
                Opacity = slide,
                Children = [Disc(sig, d)],
            }));
            kids.Add(new CanvasChild(cx - 0.5f, cy, new BoxEl { Width = 1f, Height = 0.47f * d, Shrink = 0f, Fill = White(0.25f) }));
            // Aux0 is already a FRACTION OF D, so the sled's walk is one multiply.
            kids.Add(new CanvasChild(cx - sled * 0.5f, cy - sled * 0.5f, new BoxEl
            {
                Width = sled, Height = sled, Shrink = 0f, Corners = Radii.Circle(sled), Fill = Hex(0xFF3B30),
                Shadow = new ShadowSpec(8f, 0f, 0f, Hex(0xFF3B30, 0.85f)),
                Transform = Prop.Of(() => Affine2D.Translation(0f, aux.Value * d)),
            }));
        }

        static void MiniDisc(List<CanvasChild> kids, Slab sig, float s)
        {
            float shellW = 0.62f * s, shellH = shellW * 72f / 68f;
            float winW = 0.50f * shellW, winH = 0.56f * shellH, discD = winW * 1.12f;
            float stripW = 0.84f * shellW, stripH = 0.14f * shellH;
            var slide = sig.Slide;
            // The window is a rectangular CUTOUT onto a disc bigger than the slot.
            var window = new BoxEl
            {
                Width = winW, Height = winH, Shrink = 0f, Corners = CornerRadius4.All(3f), Fill = Hex(0x0A0F20), ClipToBounds = true,
                Children =
                [
                    Canvas.Create(winW, winH,
                    [
                        new CanvasChild((winW - discD) * 0.5f, (winH - discD) * 0.5f, new BoxEl
                        {
                            Width = discD, Height = discD, Shrink = 0f, Corners = Radii.Circle(discD),
                            Transform = Prop.Of(() => Affine2D.Translation(0f, 0.3f * s * (1f - slide.Value))),
                            Opacity = slide,
                            Children = [Disc(sig, discD)],
                        }),
                    ]),
                ],
            };
            kids.Add(new CanvasChild((s - shellW) * 0.5f, (s - shellH) * 0.5f, new BoxEl
            {
                Width = shellW, Height = shellH, Shrink = 0f, Corners = CornerRadius4.All(5f),
                Gradient = new GradientSpec(GradientShape.Linear, 160f, [new GradientStop(0f, Hex(0x283C78, 0.85f)), new GradientStop(1f, Hex(0x141E46, 0.90f))]),
                Shadow = new ShadowSpec(30f, 14f, 0f, Black(0.45f)),
                Children =
                [
                    Canvas.Create(shellW, shellH,
                    [
                        new CanvasChild(0.14f * shellW, 0.12f * shellH, window),
                        new CanvasChild(0.64f * shellW, 0.08f * shellH, new BoxEl
                        {
                            Width = 0.30f * shellW, Height = 0.64f * shellH, Shrink = 0f, Corners = CornerRadius4.All(3f),
                            Gradient = GradientSpec.Vertical(Hex(0xC9CCD3), Hex(0x8A8F99)), BorderWidth = 1f, BorderColor = Black(0.25f),
                        }),
                        new CanvasChild((shellW - stripW) * 0.5f, shellH - stripH - 0.04f * shellH, new BoxEl
                        {
                            Width = stripW, Height = stripH, Shrink = 0f, Corners = CornerRadius4.All(2f), Fill = Hex(0xF4EFE6),
                            Justify = FlexJustify.Center, AlignItems = FlexAlign.Center,
                            Children = [Embed.Comp(() => new MdLabel { Sig = sig, W = stripW * 0.94f, H = stripH }) with { Key = "md-label" }],
                        }),
                    ]),
                ],
            }));
        }

        /// <summary>The spinning disc: a plate (the shadow caster and the undecoded ground), the art cut round, the
        /// rainbow at 75 %, the inner ring and the clamp hole — ONE rotating node.</summary>
        static Element Disc(Slab sig, float d)
        {
            float holeD = 0.17f * d, innerD = 0.23f * d;
            var angle = sig.Angle0;
            return new BoxEl
            {
                Width = d, Height = d, Shrink = 0f, Corners = Radii.Circle(d), TransformOriginX = 0.5f, TransformOriginY = 0.5f,
                Transform = Prop.Of(() => Affine2D.Rotation(angle.Value * Deg2Rad)),
                Children =
                [
                    Canvas.Create(d, d,
                    [
                        new CanvasChild(0f, 0f, new BoxEl
                        {
                            Width = d, Height = d, Shrink = 0f, Corners = Radii.Circle(d), Fill = Hex(0x101216),
                            Shadow = new ShadowSpec(30f, 14f, 0f, Black(0.4f)),
                        }),
                        new CanvasChild(0f, 0f, CoverLeaf(sig, d, round: true, key: "disc-art")),
                        // ImageEl carries no opacity of its own: the rainbow rides in a box that does.
                        new CanvasChild(0f, 0f, new BoxEl
                        {
                            Width = d, Height = d, Shrink = 0f, Opacity = 0.75f,
                            Children = [Texture("cd-rainbow-512.png", d, d, Radii.Circle(d), ColorF.Transparent)],
                        }),
                        new CanvasChild((d - innerD) * 0.5f, (d - innerD) * 0.5f, Ring(innerD, 0.008f * d, White(0.18f))),
                        new CanvasChild((d - holeD) * 0.5f, (d - holeD) * 0.5f, new BoxEl
                        {
                            Width = holeD, Height = holeD, Shrink = 0f, Corners = Radii.Circle(holeD),
                            Fill = Prop.Of(static () => Tok.FillCardSecondary), BorderWidth = 0.012f * d, BorderColor = White(0.55f),
                        }),
                    ]),
                ],
            };
        }

        /// <summary>The cartridge's paper strip: TITLE · ARTIST, uppercase, one line, latched like the cassette label.</summary>
        sealed class MdLabel : Component
        {
            public required Slab Sig;
            public required float W, H;
            Latch _latch;

            public override Element Render()
            {
                _latch.Update(Sig, image: false);
                string title = TitleOf(_latch.Shown), artist = ArtistsOf(_latch.Shown);
                string text = title.Length > 0 && artist.Length > 0 ? title + " · " + artist : title.Length > 0 ? title : artist;
                return new TextEl(text.ToUpperInvariant())
                {
                    Width = W, Height = H * 0.66f, Size = H * 0.44f, Weight = 600, CharSpacing = 60f, Color = Hex(0x23262B),
                    MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Shrink = 0f,
                };
            }
        }
    }

    // ══ 4. iPOD CLASSIC ══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>A body, a backlit LCD and a click wheel that SEEKS. Nothing moves but progress (<see cref="ProgressModel"/>):
    /// the bar's fill is one bound scale, the thumb one bound translation, the clocks 1 Hz bound text in FIXED slots.</summary>
    static class IpodFace
    {
        public static Element Build(in Rail.PlayerCatalog.Preset preset, float side, Slab sig, Host host)
        {
            string body = Rail.PlayerPrefs.ChoiceSlug(in preset, "body");
            bool green = Rail.PlayerPrefs.ChoiceSlug(in preset, "lcd") == "green";
            bool dark = body != "silver";
            ColorF lcdFill = green ? Hex(0xD6E6C4) : Hex(0xDFE6EA), ink = green ? Hex(0x1C2A14) : Hex(0x1C2230);

            float bodyX = 0.21f * side, bodyY = 0.02f * side, bodyW = 0.58f * side, bodyH = 0.96f * side;
            float lcdW = 0.84f * bodyW, lcdH = 0.43f * bodyH, wheelD = 0.74f * bodyW;

            // The playhead as the SCREEN sees it: a live drag owns the position before any audio has moved.
            Func<float> frac = () => sig.ScrubTargetMs.Value is { } scrub
                ? (Playback.DurationMs.Value is > 0 and var dur ? Clamp01(scrub / (float)dur) : 0f)
                : Clamp01(sig.Frac.Value);

            var gesture = host.Grip;
            return Canvas.Create(side, side,
            [
                new CanvasChild(bodyX, bodyY, new BoxEl
                {
                    Width = bodyW, Height = bodyH, Shrink = 0f, Corners = CornerRadius4.All(side * 0.055f),
                    Gradient = dark ? GradientSpec.Vertical(Hex(0x2A2C31), Hex(0x0F1013)) : GradientSpec.Vertical(Hex(0xF7F7F9), Hex(0xCFD2D8)),
                    Shadow = new ShadowSpec(34f, 16f, 0f, Black(0.5f)),
                }),
                new CanvasChild(bodyX + 0.08f * bodyW, bodyY + 0.05f * bodyH, Lcd(lcdW, lcdH, lcdFill, ink, sig, frac)),
                new CanvasChild(bodyX + (bodyW - wheelD) * 0.5f, bodyY + bodyH * 0.955f - wheelD,
                    Embed.Comp(() => new IpodWheel { Gesture = gesture, Frac = frac, Diameter = wheelD, Body = body })
                        with { Key = "ipod-wheel:" + body }),
            ]) with { Gradient = GradientSpec.Vertical(Hex(0x3A3F4A), Hex(0x22252C)) };
        }

        static Element Lcd(float w, float h, ColorF fill, ColorF ink, Slab sig, Func<float> frac)
        {
            float barH = 0.14f * h;
            float chrome = MathF.Max(6f, w * 0.053f), clock = MathF.Max(6f, w * 0.049f);
            float clockH = MathF.Ceiling(clock * 1.45f), clockW = w * 0.30f;
            float progX = 0.04f * w, progW = 0.92f * w, progH = 0.08f * h, progY = h * 0.91f - progH;
            float thumbW = MathF.Max(4f, progW * 0.06f), thumbH = progH * 1.3f;
            float battW = 0.09f * w, battH = 0.44f * barH;

            return Canvas.Create(w, h,
            [
                new CanvasChild(0f, 0f, new BoxEl
                {
                    Width = w, Height = barH, Shrink = 0f, Gradient = GradientSpec.Vertical(Hex(0xF4F6F8), Hex(0xC7CDD6)),
                    Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    // The device's OWN chrome string — part of its look, so it is not localised.
                    Children = [new TextEl("Now Playing") { Size = chrome, Weight = 700, Color = Hex(0x1C2230), Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.Clip }],
                }),
                new CanvasChild(0f, barH - 1f, new BoxEl { Width = w, Height = 1f, Shrink = 0f, Fill = Hex(0x8B93A3) }),
                new CanvasChild(0.03f * w, barH * 0.22f, new TextEl(Prop.Of(static () => Playback.IsPlaying.Value ? Icons.Play : Icons.Pause))
                {
                    Size = MathF.Max(6f, chrome * 0.92f), FontFamily = Theme.IconFont, Color = Hex(0x1C2230),
                    Width = chrome * 1.4f, AlignSelf = FlexAlign.Start,   // Start: a Canvas child with no height stretches
                }),
                new CanvasChild(w - 0.03f * w - battW, barH * 0.28f, new BoxEl
                {
                    Width = battW, Height = battH, Shrink = 0f, BorderWidth = 1f, BorderColor = Hex(0x333333), Corners = CornerRadius4.All(1f),
                    Children = [new BoxEl { Width = battW * 0.7f, Height = MathF.Max(1f, battH - 2f), Margin = new Edges4(1f, 1f, 0f, 1f), Shrink = 0f, Fill = Hex(0x3AA757) }],
                }),
                new CanvasChild(0f, 0f, Embed.Comp(() => new IpodScreen { Sig = sig, W = w, H = h, Ink = ink }) with { Key = "ipod-screen" }),
                new CanvasChild(progX, progY, new BoxEl
                {
                    Width = progW, Height = progH, Shrink = 0f, ClipToBounds = true, BorderWidth = 1f, BorderColor = Hex(0x2A2F38),
                    Corners = CornerRadius4.All(2f), Fill = Hex(0xF6F9FB),
                    Children =
                    [
                        new BoxEl
                        {
                            Width = progW, Height = progH, Shrink = 0f, Gradient = GradientSpec.Vertical(Hex(0x7DB2EA), Hex(0x2A6BC4)),
                            TransformOriginX = 0f, TransformOriginY = 0.5f, Transform = Prop.Of(() => Affine2D.Scale(frac(), 1f)),
                        },
                    ],
                }),
                // The thumb rides OUTSIDE the trough's clip; its 45° lives on an inner box (a node owns one transform).
                new CanvasChild(progX - thumbW * 0.5f, progY + (progH - thumbH) * 0.5f, new BoxEl
                {
                    Width = thumbW, Height = thumbH, Shrink = 0f, Transform = Prop.Of(() => Affine2D.Translation(frac() * progW, 0f)),
                    Children = [new BoxEl { Width = thumbW, Height = thumbH, Shrink = 0f, Rotation = 45f, Fill = Hex(0xE9EEF4), BorderWidth = 1f, BorderColor = Hex(0x2A2F38) }],
                }),
                new CanvasChild(0.04f * w, h * 0.99f - clockH, ClockSlot(clockW, clockH, clock, ink, sig, remaining: false)),
                new CanvasChild(w - 0.04f * w - clockW, h * 0.99f - clockH, ClockSlot(clockW, clockH, clock, ink, sig, remaining: true)),
            ]) with { Fill = fill, BorderWidth = 2f, BorderColor = Hex(0x2A2F38), Corners = CornerRadius4.All(3f) };
        }

        /// <summary>One clock in a FIXED slot: a bound text is a scoped relayout, and a resizing slot would move the screen.</summary>
        static Element ClockSlot(float w, float h, float size, ColorF ink, Slab sig, bool remaining) => new BoxEl
        {
            Width = w, Height = h, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center,
            Justify = remaining ? FlexJustify.End : FlexJustify.Start,
            Children =
            [
                new TextEl(remaining ? Prop.Of(() => RemainingText(sig, padded: false)) : Prop.Of(() => ElapsedText(sig, padded: false)))
                {
                    Size = size, Color = ink, FontFamily = "Consolas", Wrap = TextWrap.NoWrap, MaxLines = 1,
                },
            ],
        };

        /// <summary>The LCD's art and three lines — the only part of the iPod that changes with the SONG (not the album).</summary>
        sealed class IpodScreen : Component
        {
            public required Slab Sig;
            public required float W, H;
            public required ColorF Ink;
            Latch _latch;

            public override Element Render()
            {
                _latch.Update(Sig, image: false, followTrack: true);
                var shown = _latch.Shown;
                float w = W, h = H, art = 0.34f * w, textW = 0.54f * w;
                float title = MathF.Max(7f, w * 0.059f), line = MathF.Max(6f, w * 0.053f);
                var soft = Ink with { A = 0.78f };
                if (!KnowsImage(shown)) WatchRow(shown);
                string? url = CoverUrlOf(shown);
                return Canvas.Create(w, h,
                [
                    new CanvasChild(0.04f * w, 0.20f * h, Cover(url, art, CornerRadius4.All(2f), WatchedWash(url))),
                    new CanvasChild(0.42f * w, 0.22f * h, new BoxEl
                    {
                        Width = textW, Direction = 1, Gap = MathF.Max(1f, h * 0.012f), Shrink = 0f,
                        Children =
                        [
                            new TextEl(TitleOf(shown)) { Size = title, Weight = 700, Color = Ink, Width = textW, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                            new TextEl(ArtistsOf(shown)) { Size = line, Color = soft, Width = textW, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                            new TextEl(AlbumNameOf(shown)) { Size = line, Color = soft, Width = textW, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                        ],
                    }),
                ]) with { HitTestVisible = false };
            }
        }

        /// <summary>The click wheel: the pointer's polar angle is differenced per move, unwrapped across the ±π seam, and
        /// one full turn moves the playhead a quarter of the track — through the host's shared gesture. The drag state is
        /// instance fields (the wheel is keyed on the body and nothing re-pushes into it, ch 23 §6.4(2)). The centre button
        /// and the three glyph captions are their own hit nodes, so pressing them never arms the scrub.</summary>
        sealed class IpodWheel : Component
        {
            public required Gesture Gesture;
            public required Func<float> Frac;
            public required float Diameter;
            public required string Body;

            float _lastAngle, _dragFrac;
            bool _active, _moved;

            public override Element Render()
            {
                float d = Diameter, centre = 0.36f * d;
                bool dark = Body != "silver", u2 = Body == "u2";
                float cap = MathF.Max(6f, d * 0.058f), capH = MathF.Ceiling(cap * 1.5f), glyph = MathF.Max(7f, d * 0.075f);
                ColorF capInk = dark ? Hex(0xC9CED8, 0.75f) : Hex(0x8D939D);
                var wheelStops = u2
                    ? new[] { new GradientStop(0f, Hex(0xD8262B)), new GradientStop(1f, Hex(0x8F1418)) }
                    : dark
                        ? new[] { new GradientStop(0f, Hex(0x2F3239)), new GradientStop(1f, Hex(0x1A1C21)) }
                        : new[] { new GradientStop(0f, Hex(0xF3F3F5)), new GradientStop(1f, Hex(0xDCDEE3)) };
                var centreStops = dark
                    ? new[] { new GradientStop(0f, Hex(0x3A3D45)), new GradientStop(1f, Hex(0x1F2126)) }
                    : new[] { new GradientStop(0f, Hex(0xFFFFFF)), new GradientStop(1f, Hex(0xE6E8EC)) };

                return Canvas.Create(d, d,
                [
                    // MENU is inert and decorative: no handler, no cursor, no role.
                    new CanvasChild((d - cap * 3.6f) * 0.5f, 0.09f * d, new BoxEl
                    {
                        Width = cap * 3.6f, Height = capH, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                        Children = [new TextEl("MENU") { Size = cap, Weight = 700, Color = capInk, CharSpacing = 80f, Wrap = TextWrap.NoWrap }],
                    }),
                    new CanvasChild(0.08f * d, (d - capH) * 0.5f, GlyphKey(Icons.Previous, capH * 1.4f, capH, glyph, capInk, Playback.Previous)),
                    new CanvasChild(d - 0.08f * d - capH * 1.4f, (d - capH) * 0.5f, GlyphKey(Icons.Next, capH * 1.4f, capH, glyph, capInk, Playback.Next)),
                    new CanvasChild((d - capH * 1.4f) * 0.5f, d - 0.08f * d - capH, GlyphKey(Icons.Play, capH * 1.4f, capH, glyph, capInk, Playback.TogglePlay)),
                    new CanvasChild((d - centre) * 0.5f, (d - centre) * 0.5f, new BoxEl
                    {
                        Width = centre, Height = centre, Shrink = 0f, Corners = Radii.Circle(centre),
                        Gradient = new GradientSpec(GradientShape.Radial, 0f, centreStops), BorderWidth = 1f, BorderColor = Black(0.2f),
                        Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = Playback.TogglePlay,
                    }),
                ]) with
                {
                    Corners = Radii.Circle(d), Gradient = new GradientSpec(GradientShape.Radial, 0f, wheelStops),
                    BorderWidth = 1f, BorderColor = Black(0.15f), Cursor = CursorId.Hand, Role = AutomationRole.Slider,
                    OnPointerDown = Down, OnDrag = Move,
                    OnClick = Release,            // the drag-END edge (the seek bar's contract), not a tap
                    OnDragCanceled = Abort,
                };
            }

            static Element GlyphKey(string glyph, float w, float h, float size, ColorF ink, Action onClick) => new BoxEl
            {
                Width = w, Height = h, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Cursor = CursorId.Hand, Role = AutomationRole.Button, Focusable = true, OnClick = onClick,
                Children = [new TextEl(glyph) { Size = size, FontFamily = Theme.IconFont, Color = ink }],
            };

            void Down(Point2 local)
            {
                long dur = Playback.DurationMs.Peek();
                if (dur <= 0L) return;              // the wheel refuses over an unknown duration (so does the gesture)
                _lastAngle = AngleAt(local);
                _dragFrac = Clamp01(Frac());
                _moved = false;
                _active = true;
                Gesture.Begin(GripRules.MsAt(_dragFrac, dur));
            }

            void Move(Point2 local)
            {
                if (!_active) return;
                long dur = Playback.DurationMs.Peek();
                if (dur <= 0L) return;
                float a = AngleAt(local), delta = GripRules.Unwrap(a - _lastAngle);
                _lastAngle = a;
                if (MathF.Abs(delta) < 1e-4f) return;
                _moved = true;
                _dragFrac = GripRules.WheelAdvance(_dragFrac, delta);
                Gesture.Move(GripRules.MsAt(_dragFrac, dur));
            }

            void Release()
            {
                if (!_active) return;
                _active = false;
                long dur = Playback.DurationMs.Peek();
                // A tap on the rim is not a seek: committing where the finger landed would seek to where the playhead is.
                if (GripRules.ReleaseCommits(_moved, dur)) Gesture.Commit(GripRules.MsAt(_dragFrac, dur));
                else Gesture.Cancel();
            }

            void Abort()
            {
                if (!_active) return;
                _active = false;
                Gesture.Cancel();
            }

            float AngleAt(Point2 local)
            {
                float r = Diameter * 0.5f;
                return MathF.Atan2(local.Y - r, local.X - r);
            }
        }
    }

    // ══ 5. HI-FI VU ══════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Two lit meter faces over an amber LCD. Two writes a tick (the needles); the scale is three interned paths
    /// parsed once per process; the clocks are 1 Hz bound text. <c>ballistics</c> is a MODEL option (the τ), so this face
    /// draws the same pixels either way.</summary>
    static class VuFace
    {
        const float ViewW = 100f, ViewH = 82f;
        static readonly int[] TickValues = [-20, -10, -7, -5, -3, -1, 0, 1, 2, 3];
        static readonly PathData InkScale = Intern(BuildScale(red: false));
        static readonly PathData RedArc = Intern("M68 32 A40 40 0 0 1 86 62");
        static readonly PathData RedTicks = Intern(BuildScale(red: true));

        public static Element Build(in Rail.PlayerCatalog.Preset preset, float side, Slab sig)
        {
            string face = Rail.PlayerPrefs.ChoiceSlug(in preset, "face");
            ColorF plate = face switch { "blue" => Hex(0x0A2A5A), "black" => Hex(0x141519), _ => Hex(0xEFE6CF) };
            ColorF ink = face switch { "blue" => Hex(0x9FD6FF), "black" => Hex(0xF0F0F0), _ => Hex(0x222222) };
            ColorF red = face switch { "blue" => Hex(0xFF6A4A), "black" => Hex(0xFF4A3A), _ => Hex(0xC8321E) };
            ColorF glow = face switch
            {
                "blue" => new ColorF(120f / 255f, 200f / 255f, 1f, 0.25f),
                "black" => new ColorF(1f, 1f, 1f, 0.12f),
                _ => new ColorF(1f, 200f / 255f, 120f / 255f, 0.25f),
            };
            float mw = 0.43f * side, mh = mw * 0.82f, top = 0.12f * side, lcdW = 0.90f * side, lcdH = 0.20f * side, knob = 0.09f * side;

            return Canvas.Create(side, side,
            [
                new CanvasChild(0.05f * side, top, Meter(side, mw, mh, plate, ink, red, glow, sig.Angle0, "LEFT")),
                new CanvasChild(side - 0.05f * side - mw, top, Meter(side, mw, mh, plate, ink, red, glow, sig.Angle1, "RIGHT")),
                new CanvasChild((side - knob) * 0.5f, 0.60f * side, Knob(knob)),
                new CanvasChild(0.05f * side, side * 0.91f - lcdH, Lcd(side, lcdW, lcdH, sig)),
            ]) with { Gradient = GradientSpec.Vertical(Hex(0x1A1B20), Hex(0x0F1013)) };
        }

        static Element Meter(float side, float w, float h, ColorF plate, ColorF ink, ColorF red, ColorF glow, FloatSignal angle, string channel)
        {
            float k = w / ViewW, label = MathF.Max(5f, 5f * k), needleH = 0.78f * h, pivot = 0.08f * w;
            float vu = MathF.Max(8f, side * 0.040f), chan = MathF.Max(6f, side * 0.026f);
            var kids = new List<CanvasChild>(18)
            {
                // The lamp: a radial wash rising from the bottom edge.
                new(0f, 0f, new BoxEl
                {
                    Width = w, Height = h, Shrink = 0f, HitTestVisible = false,
                    Gradient = Ui.RadialGradient(new Point2(0.5f, 1f), new Point2(0.6f, 0.4f), new GradientStop(0f, glow), new GradientStop(1f, glow with { A = 0f })),
                }),
                new(0f, 0f, new PathEl { Width = w, Height = h, ViewBoxW = ViewW, ViewBoxH = ViewH, Geometry = InkScale, StrokeColor = ink, Stroke = new StrokeStyle(1.2f, LineCap.Round, LineJoin.Round) }),
                new(0f, 0f, new PathEl { Width = w, Height = h, ViewBoxW = ViewW, ViewBoxH = ViewH, Geometry = RedArc, StrokeColor = red, Stroke = new StrokeStyle(3f, LineCap.Butt, LineJoin.Round) }),
                new(0f, 0f, new PathEl { Width = w, Height = h, ViewBoxW = ViewW, ViewBoxH = ViewH, Geometry = RedTicks, StrokeColor = red, Stroke = new StrokeStyle(1f, LineCap.Round, LineJoin.Round) }),
            };
            for (int i = 0; i < TickValues.Length; i++)
            {
                int v = TickValues[i];
                string text = v > 0 ? "+" + v.ToString(CultureInfo.InvariantCulture) : v.ToString(CultureInfo.InvariantCulture);
                float a = (-45f + i * 10f) * Deg2Rad;
                float tx = (50f + 30f * MathF.Sin(a)) * k, ty = (62f - 30f * MathF.Cos(a)) * k;
                float halfW = text.Length * label * 0.30f;   // no text-anchor: centre by the estimated advance
                kids.Add(new CanvasChild(tx - halfW, ty - label * 0.62f, new TextEl(text)
                {
                    Size = label, Color = v > 0 ? red : ink, Wrap = TextWrap.NoWrap, AlignSelf = FlexAlign.Start,
                }));
            }
            kids.Add(new CanvasChild(0.06f * w, 0.06f * h, new TextEl(channel)
            {
                Size = chan, Weight = 700, Color = ink with { A = ink.A * 0.7f }, Wrap = TextWrap.NoWrap, AlignSelf = FlexAlign.Start,
            }));
            kids.Add(new CanvasChild((w - vu * 1.9f) * 0.5f, 0.52f * h, new BoxEl
            {
                Width = vu * 1.9f, Height = MathF.Ceiling(vu * 1.4f), Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Children = [new TextEl("VU") { Size = vu, Weight = 700, Color = ink, CharSpacing = 100f, Wrap = TextWrap.NoWrap }],
            }));
            // The needle: pivoted on its own bottom-centre, one bound rotation.
            kids.Add(new CanvasChild(w * 0.5f - 0.75f, h * 0.90f - needleH, new BoxEl
            {
                Width = 1.5f, Height = needleH, Shrink = 0f, Fill = ink, HitTestVisible = false, TransformOriginX = 0.5f, TransformOriginY = 1f,
                Transform = Prop.Of(() => Affine2D.Rotation(angle.Value * Deg2Rad)),
            }));
            kids.Add(new CanvasChild((w - pivot) * 0.5f, h * 0.94f - pivot, new BoxEl
            {
                Width = pivot, Height = pivot, Shrink = 0f, Corners = Radii.Circle(pivot),
                Gradient = Ui.RadialGradient(new GradientStop(0f, Hex(0x555555)), new GradientStop(1f, Hex(0x111111))),
            }));
            return Canvas.Create(w, h, kids) with
            {
                Fill = plate, Corners = new CornerRadius4(6f, 6f, 14f, 14f), BorderWidth = 1f, BorderColor = Black(0.5f),
                Shadow = new ShadowSpec(20f, 8f, 0f, Black(0.5f)),
            };
        }

        static Element Lcd(float side, float w, float h, Slab sig)
        {
            ColorF amber = Hex(0xFFB000), soft = amber with { A = 0.85f };
            float big = MathF.Max(10f, side * 0.055f), small = MathF.Max(6f, side * 0.028f), clockW = w * 0.34f;
            float padX = 0.04f * w, padY = 0.03f * h;
            return new BoxEl
            {
                Width = w, Height = h, Shrink = 0f, ZStack = true, ClipToBounds = true, Fill = Hex(0x1A0D00),
                Corners = CornerRadius4.All(4f), BorderWidth = 1f, BorderColor = Hex(0x3A2000),
                Children =
                [
                    // Text has no shadow channel: the amber bloom is a wash BEHIND the glyphs.
                    new BoxEl
                    {
                        Width = w, Height = h, Shrink = 0f, HitTestVisible = false,
                        Gradient = Ui.RadialGradient(new Point2(0.5f, 0.35f), new Point2(0.7f, 0.8f), new GradientStop(0f, amber with { A = 0.12f }), new GradientStop(1f, amber with { A = 0f })),
                    },
                    new BoxEl
                    {
                        Width = w, Height = h, Shrink = 0f, Direction = 1, Justify = FlexJustify.SpaceBetween, Padding = new Edges4(padX, padY, padX, padY),
                        Children =
                        [
                            new BoxEl
                            {
                                Direction = 0, Justify = FlexJustify.SpaceBetween, AlignItems = FlexAlign.Center,
                                Children = [LcdClock(clockW, big, amber, sig, remaining: false), LcdClock(clockW, big, amber, sig, remaining: true)],
                            },
                            new BoxEl
                            {
                                Direction = 0, Justify = FlexJustify.SpaceBetween, AlignItems = FlexAlign.Center, Gap = 8f,
                                Children =
                                [
                                    new TextEl(Prop.Of(static () => NowLine()))
                                    {
                                        Size = small, Color = soft, CharSpacing = 140f, Grow = 1f, Shrink = 1f, MinWidth = 0f,
                                        Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                                    },
                                    new TextEl(Prop.Of(static () => FormatLine())) { Size = small, Color = soft, CharSpacing = 140f, Shrink = 0f, Wrap = TextWrap.NoWrap, MaxLines = 1 },
                                ],
                            },
                        ],
                    },
                ],
            };
        }

        static Element LcdClock(float w, float size, ColorF amber, Slab sig, bool remaining) => new BoxEl
        {
            Width = w, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center, Justify = remaining ? FlexJustify.End : FlexJustify.Start,
            Children =
            [
                new TextEl(remaining ? Prop.Of(() => RemainingText(sig, padded: true)) : Prop.Of(() => ElapsedText(sig, padded: true)))
                {
                    Size = size, Color = amber, FontFamily = "Consolas", CharSpacing = 60f, Wrap = TextWrap.NoWrap, MaxLines = 1,
                },
            ],
        };

        static Element Knob(float d) => Canvas.Create(d, d,
        [
            new CanvasChild(d * 0.5f - 1f, d * 0.08f, new BoxEl { Width = 2f, Height = d * 0.30f, Shrink = 0f, Fill = White(1f) }),
        ]) with
        {
            Corners = Radii.Circle(d),
            Gradient = Ui.RadialGradient(new Point2(0.4f, 0.35f), new Point2(0.7f, 0.7f), new GradientStop(0f, Hex(0x8A8F99)), new GradientStop(1f, Hex(0x2B2E35))),
            Shadow = new ShadowSpec(8f, 3f, 0f, Black(0.6f)),
        };

        /// <summary>TITLE · FIRST ARTIST, uppercase — the first artist only, never the joined list.</summary>
        static string NowLine()
        {
            var cur = Playback.Current.Value;
            if (!KnowsText(cur)) WatchRow(cur);
            string title = TitleOf(cur), artist = FirstArtistOf(cur);
            return (artist.Length == 0 ? title : title + " · " + artist).ToUpperInvariant();
        }

        /// <summary>The stream's own badge, uppercase, or an empty cell — never an invented "44.1 kHz".</summary>
        static string FormatLine()
        {
            var badge = Playback.StreamFormat.Value;
            return badge.IsEmpty ? "" : Entities.Strings.Resolve(badge);
        }

        /// <summary>The dial arc plus the ticks on one side of 0 dB, in the 100×82 view box — built and interned once.</summary>
        static string BuildScale(bool red)
        {
            var sb = new StringBuilder(red ? 96 : 256);
            if (!red) sb.Append("M14 62 A40 40 0 0 1 86 62");
            for (int i = 0; i < TickValues.Length; i++)
            {
                if (TickValues[i] > 0 != red) continue;
                float a = (-45f + i * 10f) * MathF.PI / 180f, sin = MathF.Sin(a), cos = MathF.Cos(a);
                sb.Append(" M").Append(N(50f + 40f * sin)).Append(' ').Append(N(62f - 40f * cos))
                  .Append(" L").Append(N(50f + 35f * sin)).Append(' ').Append(N(62f - 35f * cos));
            }
            return sb.ToString();
        }

        static string N(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        static PathData Intern(string d)
        {
            int id = PathGeometryTable.Shared.Register(d, ViewW, ViewH, FillRule.NonZero);
            PathGeometryTable.Shared.TryGet(id, out var data);
            return data;
        }
    }

    // ══ 6. WINAMP ════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Two 275:116 windows. The analyser is the only fast channel (19 bars + 19 caps, the EQ window binding the
    /// SAME signals); the time is 1 Hz bound text; the title a marquee that scrolls while the deck plays.</summary>
    static class WinampFace
    {
        const int SpectrumBands = 19, ScopeDots = 24;
        static readonly FormatCache<int> TimeCache = FormatCache.Create<int>();

        public static Element Build(in Rail.PlayerCatalog.Preset preset, float side, Slab sig)
        {
            string skin = Rail.PlayerPrefs.ChoiceSlug(in preset, "skin");
            bool scope = Rail.PlayerPrefs.ChoiceSlug(in preset, "vis") == "scope";
            ColorF wa1 = skin switch { "modern" => Hex(0x4A4F57), "dark" => Hex(0x1A1C22), _ => Hex(0x3B4459) };
            ColorF wa2 = skin switch { "modern" => Hex(0x2B2E34), "dark" => Hex(0x0E0F13), _ => Hex(0x222A3A) };
            ColorF wa3 = skin switch { "modern" => Hex(0x8C9199), "dark" => Hex(0x5B5F6B), _ => Hex(0x6E7A97) };
            ColorF lcd = skin switch { "modern" => Hex(0xE5F3FF), "dark" => Hex(0xFF8A00), _ => Hex(0x00FF00) };
            float w = 0.92f * side, h = w * 116f / 275f, mainY = 0.10f * side;
            return Canvas.Create(side, side,
            [
                new CanvasChild(0.04f * side, mainY, MainWindow(side, w, h, wa1, wa2, wa3, lcd, scope, sig)),
                new CanvasChild(0.04f * side, mainY + h + 0.02f * side, EqWindow(side, w, h, wa1, wa2, wa3, lcd, scope, sig)),
            ]) with { Fill = Hex(0x0F1218) };
        }

        static Element MainWindow(float side, float w, float h, ColorF wa1, ColorF wa2, ColorF wa3, ColorF lcd, bool scope, Slab sig)
        {
            float small = MathF.Max(6f, side * 0.024f), lcdSize = MathF.Max(9f, side * 0.064f), stSize = MathF.Max(6f, side * 0.026f);
            float timeX = 0.14f * w, timeY = 0.20f * h, timeW = 0.32f * w, timeH = 0.30f * h, visX = 0.50f * w, visW = 0.34f * w;
            float stY = 0.54f * h, stW = 0.80f * w, stH = 0.12f * h;
            float posX = 0.06f * w, posY = 0.82f * h, posW = 0.88f * w, posH = 0.06f * h;
            const float ThumbW = 12f;
            float btnW = MathF.Max(6f, side * 0.044f), btnH = MathF.Max(5f, side * 0.030f);
            var frac = sig.Frac;

            var kids = new List<CanvasChild>(12)
            {
                new(0f, 0f, TitleBar(w, 0.12f * h, wa1, wa3, small, "WINAMP")),
                // A fixed-width slot, so a 9:59 → 10:00 roll cannot nudge the analyser beside it.
                new(timeX, timeY, new BoxEl
                {
                    Width = timeW, Height = timeH, Shrink = 0f, Fill = Hex(0x000000), BorderWidth = 1f, BorderColor = Hex(0x000000),
                    Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Children = [new TextEl(Prop.Of(() => ElapsedText(sig, padded: true))) { Size = lcdSize, Weight = 700, Color = lcd, FontFamily = "Consolas", CharSpacing = 60f, Wrap = TextWrap.NoWrap, MaxLines = 1 }],
                }),
                new(visX, timeY, new BoxEl
                {
                    Width = visW, Height = timeH, Shrink = 0f, Fill = Hex(0x000000), BorderWidth = 1f, BorderColor = Hex(0x000000), ClipToBounds = true,
                    Children = [scope ? Scope(visW - 2f, timeH - 2f, sig, lcd) : Bars(visW - 2f, timeH - 2f, sig, lcd, 1f, 2f)],
                }),
                new(timeX, stY, new BoxEl
                {
                    Width = stW, Height = stH, Shrink = 0f, Fill = Hex(0x000000), BorderWidth = 1f, BorderColor = Hex(0x000000), ClipToBounds = true,
                    Direction = 0, AlignItems = FlexAlign.Center, Padding = new Edges4(3f, 0f, 3f, 0f),
                    Children =
                    [
                        // Scrolls while the deck plays; a paused deck owns no looping row (the 2026-09-12 idle rule).
                        Marquee.Of(Prop.Of(static () => ScrollTitle()), new Marquee.Style
                        {
                            FontSize = stSize, Weight = 400, Foreground = lcd, FontFamily = "Arial", Speed = 22f, Gap = 24f,
                            FadeBand = 0f, StartDelayMs = 0f, EndPauseMs = 0f, Mode = Marquee.ScrollMode.Loop, Trigger = Marquee.TriggerMode.Hover,
                        }, scrollWhen: sig.LoopsRun),
                    ],
                }),
                // 0.3 publishes the stream's badge and no bitrate: the kbps cell states the fact it lacks.
                new(timeX, 0.69f * h, Cell("---", small, lcd)),
                new(0.34f * w, 0.69f * h, Cell(Prop.Of(static () => FormatCell()), small, lcd)),
                new(w - 0.08f * w - small * 4.2f, 0.69f * h, new BoxEl
                {
                    Width = small * 4.2f, Height = MathF.Ceiling(small * 1.5f), Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.End,
                    Children = [new TextEl("STEREO") { Size = small, Color = lcd, CharSpacing = 100f, Wrap = TextWrap.NoWrap }],
                }),
                new(posX, posY, new BoxEl { Width = posW, Height = posH, Shrink = 0f, Fill = Hex(0x000000) }),
                new(posX, posY - posH * 0.4f, new BoxEl
                {
                    Width = ThumbW, Height = posH * 1.8f, Shrink = 0f,
                    Gradient = new GradientSpec(GradientShape.Linear, 0f, [new GradientStop(0f, Hex(0x8A92A6)), new GradientStop(1f, Hex(0x4E5568))]),
                    BorderWidth = 1f, BorderColor = Hex(0x000000),
                    Transform = Prop.Of(() => Affine2D.Translation(Clamp01(frac.Value) * MathF.Max(0f, posW - ThumbW), 0f)),
                }),
                new(posX, 0.90f * h, new BoxEl
                {
                    Direction = 0, Gap = 2f, Shrink = 0f, Height = btnH,
                    // Winamp's STOP has no streaming equivalent: it pauses — the honest mapping, not a dead key.
                    Children =
                    [
                        TransportKey(btnW, btnH, Playback.Previous), TransportKey(btnW, btnH, Playback.Resume), TransportKey(btnW, btnH, Playback.Pause),
                        TransportKey(btnW, btnH, Playback.Pause), TransportKey(btnW, btnH, Playback.Next),
                    ],
                }),
            };
            return Window(w, h, wa2, wa3, kids);
        }

        static Element EqWindow(float side, float w, float h, ColorF wa1, ColorF wa2, ColorF wa3, ColorF lcd, bool scope, Slab sig)
        {
            float small = MathF.Max(6f, side * 0.024f), gx = 0.06f * w, gy = 0.18f * h, gw = 0.88f * w, gh = 0.68f * h;
            var kids = new List<CanvasChild>(4)
            {
                new(0f, 0f, TitleBar(w, 0.12f * h, wa1, wa3, small, scope ? "WINAMP OSCILLOSCOPE" : "WINAMP SPECTRUM ANALYZER")),
                new(gx, gy, new BoxEl
                {
                    Width = gw, Height = gh, Shrink = 0f, Fill = Hex(0x000000), BorderWidth = 1f, BorderColor = Hex(0x000000), ClipToBounds = true,
                    Children = [scope ? Scope(gw - 2f, gh - 2f, sig, lcd) : Bars(gw - 2f, gh - 2f, sig, lcd, 2f, 3f)],
                }),
                new(gx, gy, Grid(gw, gh)),   // the 10 hairlines OVER the bars
            };
            return Window(w, h, wa2, wa3, kids);
        }

        /// <summary>The plate: skin fill, hard black outline, and the 1 px light bevel added LAST.</summary>
        static Element Window(float w, float h, ColorF wa2, ColorF wa3, List<CanvasChild> kids)
        {
            kids.Add(new CanvasChild(1f, 1f, new BoxEl
            {
                Width = MathF.Max(0f, w - 2f), Height = MathF.Max(0f, h - 2f), Shrink = 0f, BorderWidth = 1f, BorderColor = wa3 with { A = 0.45f }, HitTestVisible = false,
            }));
            return Canvas.Create(w, h, kids) with { Fill = wa2, BorderWidth = 1f, BorderColor = Hex(0x000000) };
        }

        /// <summary>The striped title bar: a bundled 256×16 alpha stripe tinted with the skin's highlight.</summary>
        static Element TitleBar(float w, float h, ColorF wa1, ColorF wa3, float size, string caption) => new BoxEl
        {
            Width = w, Height = h, Shrink = 0f, ZStack = true, Fill = wa1, ClipToBounds = true,
            Children =
            [
                Texture("winamp-tbar.png", w, h, default, wa3),
                new BoxEl
                {
                    Width = w, Height = h, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Children =
                    [
                        new BoxEl
                        {
                            Height = h, Shrink = 0f, Fill = wa1, Padding = new Edges4(8f, 0f, 8f, 0f), Direction = 0, AlignItems = FlexAlign.Center,
                            Children = [new TextEl(caption) { Size = size, Color = White(1f), CharSpacing = 240f, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.Clip }],
                        },
                    ],
                },
            ],
        };

        /// <summary>19 bars + their 1 px caps; each captures its OWN band signal before the thunk exists.</summary>
        static Element Bars(float w, float h, Slab sig, ColorF lcd, float gap, float pad)
        {
            float inner = MathF.Max(1f, h - pad * 2f), barW = MathF.Max(1f, (w - pad * 2f - gap * (SpectrumBands - 1)) / SpectrumBands);
            var grad = new GradientSpec(GradientShape.Linear, 90f, [new GradientStop(0f, Hex(0xFF4A4A)), new GradientStop(0.45f, Hex(0xFFD24A)), new GradientStop(1f, lcd)]);
            var cols = new Element[SpectrumBands];
            for (int i = 0; i < SpectrumBands; i++)
            {
                var band = sig.Bands[i];
                var peak = sig.Peaks[i];
                cols[i] = new BoxEl
                {
                    Width = barW, Height = inner, Shrink = 0f, ZStack = true,
                    Children =
                    [
                        new BoxEl
                        {
                            Width = barW, Height = inner, Shrink = 0f, Gradient = grad, TransformOriginX = 0.5f, TransformOriginY = 1f,
                            Transform = Prop.Of(() => Affine2D.Scale(1f, MathF.Max(band.Value, 0.02f))),
                        },
                        new BoxEl
                        {
                            Width = barW, Height = 1f, Shrink = 0f, Fill = White(0.9f),
                            Transform = Prop.Of(() => Affine2D.Translation(0f, inner * (1f - Clamp01(peak.Value)))),
                        },
                    ],
                };
            }
            return new BoxEl { Width = w, Height = h, Shrink = 0f, Direction = 0, Gap = gap, Padding = Edges4.All(pad), AlignItems = FlexAlign.End, Children = cols };
        }

        /// <summary>The oscilloscope: 24 one-pixel dots, each translated off the centre line (the synth centres on 0.5).</summary>
        static Element Scope(float w, float h, Slab sig, ColorF lcd)
        {
            const float Dot = 1.5f;
            float span = MathF.Max(1f, w - Dot);
            var kids = new List<CanvasChild>(ScopeDots);
            for (int i = 0; i < ScopeDots; i++)
            {
                var band = sig.Bands[i];
                kids.Add(new CanvasChild(i * span / (ScopeDots - 1), (h - Dot) * 0.5f, new BoxEl
                {
                    Width = Dot, Height = Dot, Shrink = 0f, Fill = lcd,
                    Transform = Prop.Of(() => Affine2D.Translation(0f, (Clamp01(band.Value) - 0.5f) * h)),
                }));
            }
            return Canvas.Create(w, h, kids);
        }

        static Element Grid(float w, float h)
        {
            var lines = new List<CanvasChild>(10);
            for (int i = 0; i < 10; i++)
                lines.Add(new CanvasChild(0f, MathF.Round(h * (i + 1f) / 10f) - 1f, new BoxEl { Width = w, Height = 1f, Shrink = 0f, Fill = White(0.06f) }));
            return Canvas.Create(w, h, lines) with { HitTestVisible = false };
        }

        static Element Cell(Prop<string> text, float size, ColorF lcd) => new BoxEl
        {
            Height = MathF.Ceiling(size * 1.5f), Shrink = 0f, Fill = Hex(0x000000), Padding = new Edges4(4f, 1f, 4f, 1f), Direction = 0, AlignItems = FlexAlign.Center,
            Children = [new TextEl(text) { Size = size, Color = lcd, Wrap = TextWrap.NoWrap, MaxLines = 1 }],
        };

        /// <summary>A 1990s bitmap key: no hover paint, no pressed paint, by design.</summary>
        static Element TransportKey(float w, float h, Action onClick) => new BoxEl
        {
            Width = w, Height = h, Shrink = 0f, Gradient = GradientSpec.Vertical(Hex(0x5A6480), Hex(0x38405A)), BorderWidth = 1f, BorderColor = Hex(0x000000),
            Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = onClick,
        };

        /// <summary>"1. First Artist - Title (m:ss) *** " — the first artist only; "1. Wavee *** " with nothing playing.</summary>
        static string ScrollTitle()
        {
            var cur = Playback.Current.Value;
            if (cur.IsNone) return "1. Wavee *** ";
            if (!KnowsText(cur)) WatchRow(cur);
            int dur = cur.Kind == EntityKind.Track && new Track(cur.Slot).IsValid ? new Track(cur.Slot).DurationMs
                    : cur.Kind == EntityKind.Episode && new Episode(cur.Slot).IsValid ? new Episode(cur.Slot).DurationMs : 0;
            return "1. " + FirstArtistOf(cur) + " - " + TitleOf(cur) + " (" + FormatCache.DurationMmSs(dur) + ") *** ";
        }

        /// <summary>The "kHz" cell: the stream badge the host publishes (a codec, not a sample rate), or "---".</summary>
        static string FormatCell()
        {
            var badge = Playback.StreamFormat.Value;
            return badge.IsEmpty ? "---" : Entities.Strings.Resolve(badge);
        }
    }

    // ══ 7. WMP VISUALIZER ════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>A black plate, three presets over the same 24-band synth, a corner cover and a hairline progress line.
    /// The colour is a bound channel (theme accent, or the live cover accent), so a theme or album change repaints
    /// without a rebuild.</summary>
    static class WmpFace
    {
        const int Bands = 24, WaveDots = 32;

        public static Element Build(in Rail.PlayerCatalog.Preset preset, float side, Slab sig)
        {
            var option = Rail.PlayerCatalog.Option(in preset, "preset");
            int index = Rail.PlayerPrefs.Choice(in preset, in option);
            string style = option.Choices[index].Slug, caption = option.Choices[index].LabelKey;
            bool fromCover = Rail.PlayerPrefs.ChoiceSlug(in preset, "colour") == "cover";
            Func<ColorF> colour;
            if (fromCover) colour = static () => LiveCoverAccent(Tok.AccentDefault);
            else colour = static () => Tok.AccentDefault;

            float cover = 0.22f * side, capSize = MathF.Max(8f, side * 0.034f);
            var frac = sig.Frac;
            var kids = new List<CanvasChild>(6)
            {
                new(0f, 0f, style switch
                {
                    "alchemy" => Alchemy(side, sig, colour),
                    "battery" => Battery(side, sig, colour),
                    _ => BarsAndWaves(side, sig, colour),
                }),
                new(0.05f * side, side * 0.92f - cover, new BoxEl
                {
                    Width = cover, Height = cover, Shrink = 0f, Corners = CornerRadius4.All(3f), Shadow = new ShadowSpec(20f, 8f, 0f, Black(0.6f)),
                    // A bound Source: the corner cover follows the item without the face ever being rebuilt. It opts out of
                    // the wash (a flat plate), the one deck slot that does.
                    Children = [new ImageEl { Source = CornerCover, Width = cover, Height = cover, Fit = ImageFit.Cover, DecodePx = 128f, Corners = CornerRadius4.All(3f), Placeholder = Hex(0x14171F) }],
                }),
                new(side - 0.05f * side - side * 0.42f, side * 0.92f - capSize * 1.4f, new BoxEl
                {
                    Width = side * 0.42f, Height = MathF.Ceiling(capSize * 1.4f), Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.End,
                    Children = [new TextEl(Loc.Bind(caption)) { Size = capSize * 0.72f, Color = White(0.45f), CharSpacing = 140f, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis }],
                }),
                new(0f, side - 2f, new BoxEl
                {
                    Width = side, Height = 2f, Shrink = 0f, Fill = White(0.12f), ClipToBounds = true,
                    Children =
                    [
                        new BoxEl
                        {
                            Width = side, Height = 2f, Shrink = 0f, Fill = Prop.Of(colour), TransformOriginX = 0f, TransformOriginY = 0.5f,
                            Transform = Prop.Of(() => Affine2D.Scale(Clamp01(frac.Value), 1f)),
                        },
                    ],
                }),
            };
            return Canvas.Create(side, side, kids) with { Fill = Hex(0x04060C) };
        }

        static readonly Prop<string> CornerCover = Prop.Of(static () =>
        {
            var cur = Playback.Current.Value;
            string? url = CoverUrlOf(cur);
            if (url is null) WatchRow(cur);
            return url ?? "";
        });

        /// <summary>24 bars on the centre line with a dimmer reflection, and a 32-dot wave off the peak caps (dots 24-31
        /// deliberately repeat peaks 0-7). 80 bound transforms, zero re-renders.</summary>
        static Element BarsAndWaves(float side, Slab sig, Func<ColorF> colour)
        {
            float mid = side * 0.5f, bw = side / Bands, body = MathF.Max(1f, bw - 4f), maxH = side * 0.55f, reflect = maxH * 0.6f;
            var fill = Prop.Of(colour);
            var kids = new List<CanvasChild>(Bands * 2 + WaveDots);
            for (int i = 0; i < Bands; i++)
            {
                var band = sig.Bands[i];
                kids.Add(new CanvasChild(i * bw + 2f, mid - maxH, new BoxEl
                {
                    Width = body, Height = maxH, Shrink = 0f, Fill = fill, Opacity = 0.85f, TransformOriginX = 0.5f, TransformOriginY = 1f,
                    Transform = Prop.Of(() => Affine2D.Scale(1f, Clamp01(band.Value))),
                }));
                kids.Add(new CanvasChild(i * bw + 2f, mid, new BoxEl
                {
                    Width = body, Height = reflect, Shrink = 0f, Fill = fill, Opacity = 0.35f, TransformOriginX = 0.5f, TransformOriginY = 0f,
                    Transform = Prop.Of(() => Affine2D.Scale(1f, Clamp01(band.Value))),
                }));
            }
            const float Dot = 2f;
            float span = MathF.Max(1f, side - Dot);
            for (int i = 0; i < WaveDots; i++)
            {
                var peak = sig.Peaks[i % Bands];
                kids.Add(new CanvasChild(i * span / (WaveDots - 1), mid - Dot * 0.5f, new BoxEl
                {
                    Width = Dot, Height = Dot, Shrink = 0f, Fill = White(0.85f), Corners = Radii.Circle(Dot),
                    Transform = Prop.Of(() => Affine2D.Translation(0f, (Clamp01(peak.Value) - 0.5f) * 0.36f * side)),
                }));
            }
            return Canvas.Create(side, side, kids) with { HitTestVisible = false };
        }

        /// <summary>Three rings × three trails. Rotation follows PROGRESS (6 / 12 / 18 turns a track — scrubbing turns them,
        /// pausing freezes them); ellipticity follows a band. Hollow-SDF ring boxes, so the stroke colour can be bound.</summary>
        static Element Alchemy(float side, Slab sig, Func<ColorF> colour)
        {
            var stroke = Prop.Of(colour);
            var frac = sig.Frac;
            var kids = new List<CanvasChild>(9);
            for (int r = 0; r < 3; r++)
            {
                float d = 2f * side * (0.18f + 0.10f * r), turns = 6f * (r + 1);
                for (int trail = 0; trail < 3; trail++)
                {
                    var band = sig.Bands[(r * 7 + trail) % Slab.BandCount];
                    float lag = trail * 0.15f, opacity = trail == 0 ? 0.9f : trail == 1 ? 0.35f : 0.15f;
                    kids.Add(new CanvasChild((side - d) * 0.5f, (side - d) * 0.5f, new BoxEl
                    {
                        Width = d, Height = d, Shrink = 0f, Corners = Radii.Circle(d), BorderWidth = 2f, BorderColor = stroke, Opacity = opacity,
                        TransformOriginX = 0.5f, TransformOriginY = 0.5f,
                        Transform = Prop.Of(() =>
                        {
                            float sc = 1f + 0.25f * Clamp01(band.Value);
                            return Affine2D.Rotation(Clamp01(frac.Value) * MathF.Tau * turns - lag).Multiply(Affine2D.Scale(sc, 0.8f * sc));
                        }),
                    }));
                }
            }
            return Canvas.Create(side, side, kids) with { HitTestVisible = false };
        }

        /// <summary>A 6×6 grid pulsing in diagonals (<c>band (i·3 + j) mod 24</c>).</summary>
        static Element Battery(float side, Slab sig, Func<ColorF> colour)
        {
            const int Cols = 6, Rows = 6;
            var fill = Prop.Of(colour);
            float cw = side / Cols, ch = side / Rows, cell = MathF.Min(cw, ch);
            var kids = new List<CanvasChild>(Cols * Rows);
            for (int i = 0; i < Cols; i++)
                for (int j = 0; j < Rows; j++)
                {
                    var band = sig.Bands[(i * 3 + j) % Bands];
                    kids.Add(new CanvasChild(i * cw + (cw - cell) * 0.5f, j * ch + (ch - cell) * 0.5f, new BoxEl
                    {
                        Width = cell, Height = cell, Shrink = 0f, Fill = fill, Opacity = 0.7f, TransformOriginX = 0.5f, TransformOriginY = 0.5f,
                        Transform = Prop.Of(() => { float sc = 0.15f + 0.7f * Clamp01(band.Value); return Affine2D.Scale(sc, sc); }),
                    }));
                }
            return Canvas.Create(side, side, kids) with { HitTestVisible = false };
        }
    }

    // ══ 8. CANVAS DRIFT ══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The cover blown out and blurred behind itself, with a slow Ken Burns drift over a framed copy. The drift is
    /// not a tick: it is four looping keyframe tracks the animation slab runs, CANCELLED in place (frozen where it stands)
    /// whenever the deck may not loop (<see cref="Slab.LoopsRun"/>).</summary>
    static class CanvasFace
    {
        public static Element Build(in Rail.PlayerCatalog.Preset preset, float side, Slab sig)
        {
            bool fast = Rail.PlayerPrefs.ChoiceSlug(in preset, "drift") == "fast";
            bool strong = Rail.PlayerPrefs.ChoiceSlug(in preset, "bleed") == "strong";
            float seekW = side * 0.78f;
            var frac = sig.Frac;
            return Canvas.Create(side, side,
            [
                new CanvasChild(0f, 0f, Embed.Comp(() => new CanvasArtwork { Sig = sig, Side = side, Fast = fast, BleedOpacity = strong ? 1f : 0.6f })
                    with { Key = "cv:" + (fast ? "fast" : "slow") + ":" + (strong ? "strong" : "soft") }),
                // Built OUTSIDE the artwork component, so its bind survives every album change above it.
                new CanvasChild(side * 0.11f, side * 0.95f - 2f, new BoxEl
                {
                    Width = seekW, Height = 2f, Shrink = 0f, Fill = White(0.35f), ClipToBounds = true,
                    Children =
                    [
                        new BoxEl
                        {
                            Width = seekW, Height = 2f, Shrink = 0f, Fill = White(1f), TransformOriginX = 0f, TransformOriginY = 0.5f,
                            Transform = Prop.Of(() => Affine2D.Scale(Clamp01(frac.Value), 1f)),
                        },
                    ],
                }),
            ]) with { Fill = Hex(0x05070C) };
        }

        /// <summary>The bleed and the drifting frame: re-renders on the latched generation (a new album remounts the canvas,
        /// which is its 300 ms cross-fade) and on the loop gate, and owns the drift's layout effect.</summary>
        sealed class CanvasArtwork : Component
        {
            public required Slab Sig;
            public required float Side;
            public required bool Fast;
            public required float BleedOpacity;

            NodeHandle _drift;
            Keyframe[]? _tx, _ty, _sx, _sy;
            float _keyedFor = float.NaN;
            Latch _latch;

            public override Element Render()
            {
                _latch.Update(Sig, image: true);
                bool run = Sig.LoopsRun.Value;                 // playing ∧ active ∧ rail open ∧ ¬reduced — a value, not a branch
                int gen = _latch.Gen;
                float s = Side, frame = 0.78f * s, inner = 1.18f * frame, bleed = 1.5f * s;
                string url = CoverUrlOf(_latch.Shown) ?? "";
                var wash = WatchedWash(url.Length > 0 ? url : null);

                UseLayoutEffect(() =>
                {
                    var anim = Context.Anim;
                    var scene = Context.Scene;
                    var node = _drift;
                    if (anim is null || scene is null || node.IsNull || !scene.IsLive(node)) return;
                    if (!run)
                    {
                        // Cancel, never "seed a flat track": a cancelled channel holds its value — the paused freeze.
                        anim.Cancel(node, AnimChannel.TranslateX);
                        anim.Cancel(node, AnimChannel.TranslateY);
                        anim.Cancel(node, AnimChannel.ScaleX);
                        anim.Cancel(node, AnimChannel.ScaleY);
                        return;
                    }
                    EnsureKeys(frame);
                    float ms = (Fast ? DriftPath.FastSeconds : DriftPath.SlowSeconds) * 1000f;
                    anim.Keyframes(node, AnimChannel.TranslateX, _tx!, ms, loop: true);
                    anim.Keyframes(node, AnimChannel.TranslateY, _ty!, ms, loop: true);
                    anim.Keyframes(node, AnimChannel.ScaleX, _sx!, ms, loop: true);
                    anim.Keyframes(node, AnimChannel.ScaleY, _sy!, ms, loop: true);
                }, DepKey.From(gen, run ? 1 : 0, Fast ? 1 : 0, (int)frame));

                return Canvas.Create(s, s,
                [
                    // ONE oversized pre-blurred draw: BakedBlur derives a persistent bitmap once.
                    new CanvasChild(-0.25f * s, -0.25f * s, new BoxEl
                    {
                        Width = bleed, Height = bleed, Shrink = 0f, HitTestVisible = false, Opacity = BleedOpacity,
                        Children = [new ImageEl { Source = url, Width = bleed, Height = bleed, Fit = ImageFit.Cover, DecodePx = 256f, Placeholder = wash, BakedBlur = new BakedBlurSpec(44f, 0.25f), Saturation = 1.5f }],
                    }),
                    // The −9 % inset rides the frame canvas's positioning wrapper, so the 118 % box owns no static transform
                    // (a node owns ONE transform, and this one's belongs to the animation slab).
                    new CanvasChild(0.11f * s, 0.11f * s, Canvas.Create(frame, frame,
                    [
                        new CanvasChild(-0.09f * frame, -0.09f * frame, new BoxEl
                        {
                            Width = inner, Height = inner, Shrink = 0f, OnRealized = h => _drift = h,
                            Children = [new ImageEl { Source = url, Width = inner, Height = inner, Fit = ImageFit.Cover, DecodePx = 512f, Placeholder = wash }],
                        }),
                    ]) with { Corners = CornerRadius4.All(6f), Shadow = new ShadowSpec(60f, 24f, 0f, Black(0.45f)) }),
                ]) with { Animate = CoverFade, HitTestVisible = false, Key = "cv:" + FormatCache.Int(gen) };
            }

            /// <summary>The four looping tracks from <see cref="DriftPath"/>, MIRRORED (0 → ½ → 1 → ½ → 0) so the loop has
            /// no seam. Built once per frame size, so a gate flip re-seeds without allocating.</summary>
            void EnsureKeys(float frame)
            {
                if (_tx is not null && _keyedFor == frame) return;
                var keys = DriftPath.Keys;
                _tx = Mirror(keys.Length, i => keys[i].Tx * frame);
                _ty = Mirror(keys.Length, i => keys[i].Ty * frame);
                _sx = Mirror(keys.Length, i => keys[i].Scale);
                _sy = Mirror(keys.Length, i => keys[i].Scale);
                _keyedFor = frame;
            }

            static Keyframe[] Mirror(int count, Func<int, float> value)
            {
                int total = count * 2 - 1;
                var frames = new Keyframe[total];
                for (int i = 0; i < total; i++)
                    frames[i] = new Keyframe(i / (float)(total - 1), value(i < count ? i : total - 1 - i), Easing.EaseInOut);
                return frames;
            }
        }
    }
}
