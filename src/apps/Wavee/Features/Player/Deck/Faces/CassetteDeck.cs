using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using Wavee.Core;

namespace Wavee;

/// <summary>
/// The CASSETTE face: a compact-cassette shell whose two hubs turn at the speed the tape physics
/// (<c>TapeModel</c>, <c>TapeKind.Cassette</c>) says they turn, and which ejects and re-loads on a new album.
///
/// <para><b>What moves, and how.</b> Exactly six bound channels, all of them <c>Transform</c>/<c>Opacity</c> on a
/// <c>BoxEl</c>: the shell's eject slide (<c>Slide</c>), the two tape-pack radii (<c>Aux0</c>/<c>Aux1</c>, scaled
/// about the pack centre) and the two hub angles (<c>Angle0</c>/<c>Angle1</c>). Nothing here re-renders at tick
/// rate — the face is built ONCE (see <see cref="DeckFaces"/>) and every thunk below closes over the signal slab
/// and allocates nothing when it runs.</para>
///
/// <para><b>Layout.</b> Absolute, in fractions of <c>side</c>: a deck-sized <see cref="Canvas"/> holding the
/// gradient ground and the shell, and a shell-sized canvas inside it for the label, the window, the tape strip and
/// the foot. The hubs live INSIDE the window's canvas (the window is a cutout, and clipping there is what keeps a
/// growing take-up pack from spilling onto the shell body).</para>
/// </summary>
static class CassetteDeck
{
    public static Element Build(in NpvPlayerCatalog.Preset preset, IAppSettings? settings, float side,
                                DeckSignals sig, PlaybackBridge bridge, DeckHost host)
    {
        // `preset` is an `in` parameter: it cannot be captured by any lambda below, so every option is read into a
        // plain local HERE, before the first bind thunk exists.
        string shell = NpvPlayerPrefs.ChoiceSlug(settings, preset, "shell");
        string labelStyle = NpvPlayerPrefs.ChoiceSlug(settings, preset, "label");
        string sideSlug = NpvPlayerPrefs.ChoiceSlug(settings, preset, "side");

        float s = side;
        float shellW = 0.86f * s, shellH = 0.56f * s;
        float shellX = 0.07f * s, shellY = 0.21f * s;

        // ── the shell's own furniture (fractions of the SHELL, per the mockup's geometry table) ──────────────────
        float labelW = 0.90f * shellW, labelH = 0.30f * shellH;
        float labelX = 0.05f * shellW, labelY = 0.06f * shellH;
        float winW = 0.62f * shellW, winH = 0.34f * shellH;
        float winX = 0.19f * shellW, winY = 0.40f * shellH;
        float packD = 0.26f * s;      // the take-up pack at FULL wind — both hubs share it, so the scale bind is honest
        float reelD = 0.11f * s;
        float badgeD = 0.10f * labelW;

        var brown = Hex(0x3A2B1C);
        var brownTint = Hex(0x2A1D11, 0.55f);
        var white = Hex(0xFFFFFF);

        var labelSurface = new BoxEl
        {
            Width = labelW,
            Height = labelH,
            Shrink = 0f,
            Corners = CornerRadius4.All(3f),
            Fill = labelStyle switch
            {
                "chrome" => ColorF.Transparent,
                "hand" => Hex(0xFFFDF5),
                _ => Hex(0xF2E9D8),
            },
            Gradient = labelStyle == "chrome" ? GradientSpec.Vertical(Hex(0xE8E8EC), Hex(0xB9BCC4)) : null,
            Children = [LabelBody(sig, labelStyle, labelW, labelH, badgeD)],
        };

        // The window is the cutout the tape shows through, so it CLIPS: a pack at full wind is wider than the slot.
        var window = new BoxEl
        {
            Width = winW,
            Height = winH,
            Shrink = 0f,
            Corners = CornerRadius4.All(5f),
            Fill = Hex(0x14151A),
            BorderWidth = 2f,
            BorderColor = Hex(0xFFFFFF, 0.08f),
            ClipToBounds = true,
            Children =
            [
                Canvas.Create(winW, winH,
                [
                    // Hub centres: 31% / 69% of the shell, 57% down — i.e. the window's own two thirds, vertically centred.
                    .. Hub(sig, 0.31f * shellW - winX, winH * 0.5f, packD, reelD, s, brown, brownTint, white, left: true),
                    .. Hub(sig, 0.69f * shellW - winX, winH * 0.5f, packD, reelD, s, brown, brownTint, white, left: false),
                ]),
            ],
        };

        var shellBody = new BoxEl
        {
            Width = shellW,
            Height = shellH,
            Shrink = 0f,
            Corners = CornerRadius4.All(6f),
            Fill = shell switch
            {
                "clear" => Hex(0xBECDE6, 0.28f),
                "cream" => Hex(0xE9E2D0),
                "smoke" => Hex(0x464854, 0.85f),
                _ => Hex(0x1D1F24),
            },
            // The clear shell is the one that genuinely reads THROUGH to the deck ground; the others are plates.
            Acrylic = shell == "clear"
                ? new AcrylicSpec(Hex(0xBECDE6), 0.28f, 18f, 0.02f, 0.92f, Hex(0xBECDE6, 0.28f))
                : null,
            BorderWidth = shell == "clear" ? 1f : 0f,
            BorderColor = shell == "clear" ? Hex(0xFFFFFF, 0.35f) : ColorF.Transparent,
            Shadow = new ShadowSpec(30f, 14f, 0f, Hex(0x000000, 0.5f)),
            Children =
            [
                Canvas.Create(shellW, shellH,
                [
                    new CanvasChild(labelX, labelY, labelSurface),
                    new CanvasChild(labelX + labelW - badgeD - 0.02f * shellW, labelY + (labelH - badgeD) * 0.5f,
                        SideBadge(sideSlug, badgeD)),
                    new CanvasChild(winX, winY, window),
                    // Tape strip: the exposed span between the two hubs, along the shell's open edge.
                    new CanvasChild(0.20f * shellW, 0.81f * shellH, Plate(0.60f * shellW, 0.025f * shellH, brown, 0f)),
                    new CanvasChild(0.26f * shellW, shellH - 0.16f * shellH, Foot(0.48f * shellW, 0.16f * shellH)),
                    new CanvasChild(0.03f * shellW, 0.04f * shellH, Screw(s)),
                    new CanvasChild(shellW - 0.03f * shellW - 0.012f * s, 0.04f * shellH, Screw(s)),
                    new CanvasChild(0.03f * shellW, shellH - 0.04f * shellH - 0.012f * s, Screw(s)),
                    new CanvasChild(shellW - 0.03f * shellW - 0.012f * s, shellH - 0.04f * shellH - 0.012f * s, Screw(s)),
                    new CanvasChild(0.02f * shellW, 0.86f * shellH, new TextEl("wavee · c-60 · type i")
                    {
                        Width = 0.17f * s,
                        Height = 0.030f * s,
                        Size = 0.018f * s,
                        Weight = 500,
                        Color = shell is "cream" ? Hex(0x000000, 0.40f) : Hex(0xFFFFFF, 0.35f),
                        MaxLines = 1,
                        Trim = TextTrim.Clip,
                    }),
                ]),
            ],
        };

        // The eject: the whole shell rises out of the slot and fades, the artwork swaps at the top of the arc
        // (CoverGen, which the label component keys on), and it drops back in. Two writes, no re-render.
        var shellWrapper = new BoxEl
        {
            Width = shellW,
            Height = shellH,
            Shrink = 0f,
            Transform = Prop.Of(() => Affine2D.Translation(0f, -0.4f * s * (1f - sig.Slide.Value))),
            Opacity = Prop.Of(() => sig.Slide.Value),
            Children = [shellBody],
        };

        return Canvas.Create(s, s,
        [
            new CanvasChild(0f, 0f, new BoxEl
            {
                Width = s,
                Height = s,
                Shrink = 0f,
                Gradient = GradientSpec.Vertical(Hex(0x2B2F3A), Hex(0x1C1F27)),
            }),
            new CanvasChild(shellX, shellY, shellWrapper),
        ]);
    }

    /// <summary>One hub: the tape PACK (scale-bound to its radius signal) with the spoked reel turning on top of it.
    /// Both hubs are built from the same max width so that a scale of 1 means "full pack" on either side.</summary>
    static CanvasChild[] Hub(DeckSignals sig, float cx, float cy, float packD, float reelD, float s,
                             ColorF brown, ColorF brownTint, ColorF white, bool left)
    {
        var pack = new BoxEl
        {
            Width = packD,
            Height = packD,
            Shrink = 0f,
            Corners = Radii.Circle(packD),
            Fill = brown,
            TransformOriginX = 0.5f,
            TransformOriginY = 0.5f,
            Transform = left
                ? Prop.Of(() => Affine2D.Scale(sig.Aux0.Value, sig.Aux0.Value))
                : Prop.Of(() => Affine2D.Scale(sig.Aux1.Value, sig.Aux1.Value)),
            Children = [DeckArt.Texture("grooves-1024.png", packD, packD, Radii.Circle(packD), brownTint)],
        };

        float spokeW = 0.10f * s, spokeH = 0.02f * s, hubD = 0.035f * s;
        var reel = new BoxEl
        {
            Width = reelD,
            Height = reelD,
            Shrink = 0f,
            Corners = Radii.Circle(reelD),
            BorderWidth = 0.014f * s,
            BorderColor = Hex(0xFFFFFF, 0.92f),
            TransformOriginX = 0.5f,
            TransformOriginY = 0.5f,
            Transform = left
                ? Prop.Of(() => Affine2D.Rotation(sig.Angle0.Value * DeckArt.Deg2Rad))
                : Prop.Of(() => Affine2D.Rotation(sig.Angle1.Value * DeckArt.Deg2Rad)),
            Children =
            [
                Canvas.Create(reelD, reelD,
                [
                    new CanvasChild((reelD - spokeW) * 0.5f, (reelD - spokeH) * 0.5f, Spoke(spokeW, spokeH, 0f, white)),
                    new CanvasChild((reelD - spokeW) * 0.5f, (reelD - spokeH) * 0.5f, Spoke(spokeW, spokeH, 60f, white)),
                    new CanvasChild((reelD - spokeW) * 0.5f, (reelD - spokeH) * 0.5f, Spoke(spokeW, spokeH, 120f, white)),
                    new CanvasChild((reelD - hubD) * 0.5f, (reelD - hubD) * 0.5f, DeckArt.Circle(hubD, white)),
                ]),
            ],
        };

        return
        [
            new CanvasChild(cx - packD * 0.5f, cy - packD * 0.5f, pack),
            new CanvasChild(cx - reelD * 0.5f, cy - reelD * 0.5f, reel),
        ];
    }

    static Element Spoke(float w, float h, float deg, ColorF c) => new BoxEl
    {
        Width = w,
        Height = h,
        Shrink = 0f,
        Fill = c,
        Rotation = deg,
        TransformOriginX = 0.5f,
        TransformOriginY = 0.5f,
    };

    static Element Plate(float w, float h, ColorF fill, float corners) => new BoxEl
    {
        Width = w,
        Height = h,
        Shrink = 0f,
        Fill = fill,
        Corners = CornerRadius4.All(corners),
    };

    /// <summary>The transport foot: the recessed strip along the cassette's open edge with its four drive holes.</summary>
    static Element Foot(float w, float h)
    {
        float hole = h * 0.34f, gap = (w - 4f * hole) / 5f, y = (h - hole) * 0.5f;
        return new BoxEl
        {
            Width = w,
            Height = h,
            Shrink = 0f,
            Corners = CornerRadius4.All(3f),
            Fill = Hex(0x000000, 0.25f),
            Children =
            [
                Canvas.Create(w, h,
                [
                    new CanvasChild(gap, y, DeckArt.Circle(hole, Hex(0x000000, 0.45f))),
                    new CanvasChild(gap * 2f + hole, y, DeckArt.Circle(hole, Hex(0x000000, 0.45f))),
                    new CanvasChild(gap * 3f + hole * 2f, y, DeckArt.Circle(hole, Hex(0x000000, 0.45f))),
                    new CanvasChild(gap * 4f + hole * 3f, y, DeckArt.Circle(hole, Hex(0x000000, 0.45f))),
                ]),
            ],
        };
    }

    static Element Screw(float s) => DeckArt.Circle(0.012f * s, Hex(0xFFFFFF, 0.22f));

    static Element SideBadge(string sideSlug, float d) => new BoxEl
    {
        Width = d,
        Height = d,
        Shrink = 0f,
        Corners = Radii.Circle(d),
        Fill = Hex(0x23262B, 0.10f),
        BorderWidth = 1f,
        BorderColor = Hex(0x23262B, 0.45f),
        // TextEl has no horizontal alignment of its own — the badge centres its glyph the flex way.
        Justify = FlexJustify.Center,
        AlignItems = FlexAlign.Center,
        Children =
        [
            new TextEl(sideSlug == "b" ? "B" : "A")
            {
                Height = d * 0.62f,
                Size = d * 0.52f,
                Weight = 700,
                Color = Hex(0x23262B),
                MaxLines = 1,
                Shrink = 0f,
            },
        ],
    };

    static Element LabelBody(DeckSignals sig, string style, float w, float h, float badgeD)
        => Embed.Comp(() => new CassetteLabel { Sig = sig, Style = style, W = w, H = h, BadgeD = badgeD })
           // The style is a FROZEN factory field, so an option flip must remount rather than re-push: the key carries it.
           with { Key = "cassette-label:" + style };

    static ColorF Hex(uint rgb, float a = 1f)
        => new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, a);

    /// <summary>The only part of the cassette that re-renders: the two lines of ink on the label. Keyed on
    /// <c>CoverGen</c> rather than on the track, so a same-album advance re-letters the label in place while the
    /// eject's mid-arc swap is what changes it during a load.</summary>
    sealed class CassetteLabel : Component
    {
        public required DeckSignals Sig;
        public required string Style;
        public required float W, H, BadgeD;

        public override Element Render()
        {
            _ = Sig.CoverGen.Value;              // the artwork/lettering generation — bumps mid-eject
            var track = UseContext(PlaybackBridge.Slot)?.CurrentTrack.Value;
            string title = track?.Title ?? "";
            string artist = track is null ? "" : DetailFormat.ArtistNames(track.Artists);
            string album = track?.Album.Name ?? "";
            string second = album.Length > 0 && artist.Length > 0 ? artist + " · " + album
                          : artist.Length > 0 ? artist : album;
            if (Style == "chrome")
            {
                title = title.ToUpperInvariant();
                second = second.ToUpperInvariant();
            }

            string? family = Style switch
            {
                "type1" => "Courier New",
                "hand" => "Segoe Print",
                _ => "Segoe UI",
            };
            var ink = Hex(0x23262B);
            float pad = W * 0.045f, textW = W - pad * 2f - BadgeD;

            var children = new List<CanvasChild>(6);
            if (Style == "hand")
            {
                // Three ruled hairlines, as on a blank hand-written insert — under the ink, not over it.
                for (int i = 0; i < 3; i++)
                    children.Add(new CanvasChild(pad, H * (0.30f + 0.25f * i), new BoxEl
                    {
                        Width = W - pad * 2f,
                        Height = 1f,
                        Shrink = 0f,
                        Fill = Hex(0x9FC0E8),
                    }));
            }
            children.Add(new CanvasChild(pad, H * 0.16f, new TextEl(title)
            {
                Width = textW,
                Height = H * 0.34f,
                Size = H * 0.26f,
                Weight = 700,
                FontFamily = family,
                Color = ink,
                MaxLines = 1,
                Trim = TextTrim.CharacterEllipsis,
            }));
            children.Add(new CanvasChild(pad, H * 0.56f, new TextEl(second)
            {
                Width = textW,
                Height = H * 0.30f,
                Size = H * 0.20f,
                Weight = 400,
                FontFamily = family,
                Color = Hex(0x23262B, 0.72f),
                MaxLines = 1,
                Trim = TextTrim.CharacterEllipsis,
            }));
            return Canvas.Create(W, H, children);
        }
    }
}
