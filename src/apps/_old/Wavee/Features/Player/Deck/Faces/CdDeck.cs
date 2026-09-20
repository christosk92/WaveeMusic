using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using Wavee.Core;

namespace Wavee;

/// <summary>
/// The CD / MiniDisc face: a constant-linear-velocity spindle (<c>DiscModel</c> — fastest at the inner edge, slowest
/// at the rim), a laser sled that walks outward with the playhead, and a tray that ejects on a new album.
///
/// <para><b>What moves.</b> Three bound channels on <c>BoxEl</c>s: the disc's <c>Angle0</c> rotation, the tray's
/// <c>Slide</c> (translate + fade, the artwork swapping at the bottom of the arc where <c>CoverGen</c> bumps) and
/// the sled's <c>Aux0</c>, which the model already publishes as a FRACTION OF THE DISC DIAMETER — so the bind is a
/// plain <c>Translation(0, Aux0 * D)</c> down the rail and nothing here needs to know the sled's units.</para>
///
/// <para><b>Two formats, one physics.</b> The <c>format</c> option swaps the housing: a bare disc on the tray, or a
/// MiniDisc cartridge whose window shows a slice of the same rotating disc through a rectangular clip.</para>
/// </summary>
static class CdDeck
{
    public static Element Build(in NpvPlayerCatalog.Preset preset, IAppSettings? settings, float side,
                                DeckSignals sig, PlaybackBridge bridge, DeckHost host)
    {
        // `preset` is an `in` parameter — it cannot be captured by a thunk, so the option becomes a local first.
        string format = NpvPlayerPrefs.ChoiceSlug(settings, preset, "format");
        float s = side;
        bool md = format == "md";

        var ground = new BoxEl
        {
            Width = s,
            Height = s,
            Shrink = 0f,
            Gradient = GradientSpec.Vertical(Hex(0x22252C), Hex(0x14161B)),
        };

        var caption = new TextEl(md ? "MD · ATRAC" : "COMPACT DISC · DIGITAL AUDIO")
        {
            Width = 0.80f * s,
            Height = 0.034f * s,
            Size = 0.021f * s,
            Weight = 600,
            CharSpacing = 180f,
            Color = Hex(0xFFFFFF, 0.45f),
            MaxLines = 1,
            Trim = TextTrim.Clip,
            Shrink = 0f,
        };

        return md
            ? Canvas.Create(s, s,
            [
                new CanvasChild(0f, 0f, ground),
                new CanvasChild(0.06f * s, 0.05f * s, caption),
                .. MiniDisc(sig, s),
            ])
            : Canvas.Create(s, s,
            [
                new CanvasChild(0f, 0f, ground),
                new CanvasChild(0.06f * s, 0.05f * s, caption),
                .. CompactDisc(sig, s),
            ]);
    }

    // ── CD ──────────────────────────────────────────────────────────────────────────────────────────────────────

    static CanvasChild[] CompactDisc(DeckSignals sig, float s)
    {
        float d = 0.72f * s;
        float dx = 0.14f * s, dy = 0.12f * s;
        float cx = dx + d * 0.5f, cy = dy + d * 0.5f;
        float sled = 0.03f * d;

        // The tray: out and down, fade, swap (CoverGen), back in. Two writes, no re-render.
        var tray = new BoxEl
        {
            Width = d,
            Height = d,
            Shrink = 0f,
            Corners = Radii.Circle(d),
            Transform = Prop.Of(() => Affine2D.Translation(0f, 0.3f * s * (1f - sig.Slide.Value))),
            Opacity = Prop.Of(() => sig.Slide.Value),
            Children = [Canvas.Create(d, d, [new CanvasChild(0f, 0f, Disc(sig, d, hole: true))])],
        };

        return
        [
            new CanvasChild(dx, dy, tray),
            // The sled rail runs from the spindle straight down; the sled itself is parked at the centre and the
            // bind walks it outward — Aux0 is a fraction of D, so the arithmetic is one multiply.
            new CanvasChild(cx - 0.5f, cy, new BoxEl
            {
                Width = 1f,
                Height = 0.47f * d,
                Shrink = 0f,
                Fill = Hex(0xFFFFFF, 0.25f),
            }),
            new CanvasChild(cx - sled * 0.5f, cy - sled * 0.5f, new BoxEl
            {
                Width = sled,
                Height = sled,
                Shrink = 0f,
                Corners = Radii.Circle(sled),
                Fill = Hex(0xFF3B30),
                Shadow = new ShadowSpec(8f, 0f, 0f, Hex(0xFF3B30, 0.85f)),
                Transform = Prop.Of(() => Affine2D.Translation(0f, sig.Aux0.Value * d)),
            }),
        ];
    }

    // ── MiniDisc ────────────────────────────────────────────────────────────────────────────────────────────────

    static CanvasChild[] MiniDisc(DeckSignals sig, float s)
    {
        float shellW = 0.62f * s, shellH = shellW * 72f / 68f;
        float shellX = (s - shellW) * 0.5f, shellY = (s - shellH) * 0.5f;
        float winW = 0.50f * shellW, winH = 0.56f * shellH;
        float winX = 0.14f * shellW, winY = 0.12f * shellH;
        float discD = winW * 1.12f;
        float stripW = 0.84f * shellW, stripH = 0.14f * shellH;

        // The cartridge window is a rectangular CUTOUT onto the disc: the disc is bigger than the slot and the
        // window clips it, which is exactly what a MiniDisc shows.
        var window = new BoxEl
        {
            Width = winW,
            Height = winH,
            Shrink = 0f,
            Corners = CornerRadius4.All(3f),
            Fill = Hex(0x0A0F20),
            ClipToBounds = true,
            Children =
            [
                Canvas.Create(winW, winH,
                [
                    new CanvasChild((winW - discD) * 0.5f, (winH - discD) * 0.5f, new BoxEl
                    {
                        Width = discD,
                        Height = discD,
                        Shrink = 0f,
                        Corners = Radii.Circle(discD),
                        Transform = Prop.Of(() => Affine2D.Translation(0f, 0.3f * s * (1f - sig.Slide.Value))),
                        Opacity = Prop.Of(() => sig.Slide.Value),
                        Children = [Disc(sig, discD, hole: true)],
                    }),
                ]),
            ],
        };

        var shell = new BoxEl
        {
            Width = shellW,
            Height = shellH,
            Shrink = 0f,
            Corners = CornerRadius4.All(5f),
            Gradient = new GradientSpec(GradientShape.Linear, 160f,
            [
                new GradientStop(0f, Hex(0x283C78, 0.85f)),
                new GradientStop(1f, Hex(0x141E46, 0.90f)),
            ]),
            Shadow = new ShadowSpec(30f, 14f, 0f, Hex(0x000000, 0.45f)),
            Children =
            [
                Canvas.Create(shellW, shellH,
                [
                    new CanvasChild(winX, winY, window),
                    new CanvasChild(0.64f * shellW, 0.08f * shellH, new BoxEl
                    {
                        Width = 0.30f * shellW,
                        Height = 0.64f * shellH,
                        Shrink = 0f,
                        Corners = CornerRadius4.All(3f),
                        Gradient = GradientSpec.Vertical(Hex(0xC9CCD3), Hex(0x8A8F99)),
                        BorderWidth = 1f,
                        BorderColor = Hex(0x000000, 0.25f),
                    }),
                    new CanvasChild((shellW - stripW) * 0.5f, shellH - stripH - 0.04f * shellH, new BoxEl
                    {
                        Width = stripW,
                        Height = stripH,
                        Shrink = 0f,
                        Corners = CornerRadius4.All(2f),
                        Fill = Hex(0xF4EFE6),
                        Justify = FlexJustify.Center,
                        AlignItems = FlexAlign.Center,
                        Children =
                        [
                            Embed.Comp(() => new MdLabel { Sig = sig, W = stripW * 0.94f, H = stripH })
                                with { Key = "md-label" },
                        ],
                    }),
                ]),
            ],
        };

        return [new CanvasChild(shellX, shellY, shell)];
    }

    // ── shared disc ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The spinning disc: a base plate (also the shadow caster and the "cover has not decoded" ground), the
    /// cover art clipped to a circle, the rainbow diffraction texture, and the clamp hole.</summary>
    static Element Disc(DeckSignals sig, float d, bool hole)
    {
        float holeD = 0.17f * d, innerD = 0.23f * d;
        var children = new List<CanvasChild>(5)
        {
            new(0f, 0f, new BoxEl
            {
                Width = d,
                Height = d,
                Shrink = 0f,
                Corners = Radii.Circle(d),
                Fill = Hex(0x101216),
                Shadow = new ShadowSpec(30f, 14f, 0f, Hex(0x000000, 0.4f)),
            }),
            new(0f, 0f, Embed.Comp(() => new DiscArt { Sig = sig, D = d }) with { Key = "disc-art" }),
            // ImageEl carries no opacity of its own, so the rainbow rides in a box that does.
            new(0f, 0f, new BoxEl
            {
                Width = d,
                Height = d,
                Shrink = 0f,
                Opacity = 0.75f,
                Children = [DeckArt.Texture("cd-rainbow-512.png", d, d, Radii.Circle(d), ColorF.Transparent)],
            }),
        };
        if (hole)
        {
            children.Add(new CanvasChild((d - innerD) * 0.5f, (d - innerD) * 0.5f,
                DeckArt.Ring(innerD, 0.008f * d, Hex(0xFFFFFF, 0.18f))));
            children.Add(new CanvasChild((d - holeD) * 0.5f, (d - holeD) * 0.5f, new BoxEl
            {
                Width = holeD,
                Height = holeD,
                Shrink = 0f,
                Corners = Radii.Circle(holeD),
                Fill = Tok.FillCardSecondary,
                BorderWidth = 0.012f * d,
                BorderColor = Hex(0xFFFFFF, 0.55f),
            }));
        }

        // ONE rotating node for the whole disc: the cover, the rainbow and the hole are its static children.
        return new BoxEl
        {
            Width = d,
            Height = d,
            Shrink = 0f,
            Corners = Radii.Circle(d),
            TransformOriginX = 0.5f,
            TransformOriginY = 0.5f,
            Transform = Prop.Of(() => Affine2D.Rotation(sig.Angle0.Value * DeckArt.Deg2Rad)),
            Children = [Canvas.Create(d, d, children)],
        };
    }

    static ColorF Hex(uint rgb, float a = 1f)
        => new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, a);

    /// <summary>The disc's artwork. Keyed on <c>CoverGen</c> — the eject bumps it at the bottom of the tray's arc,
    /// which is the ONE instant the picture is allowed to change.</summary>
    sealed class DiscArt : Component
    {
        public required DeckSignals Sig;
        public required float D;

        public override Element Render()
        {
            _ = Sig.CoverGen.Value;
            var track = UseContext(PlaybackBridge.Slot)?.CurrentTrack.Value;
            string? url = track is null ? null : DeckArt.CoverUrl(track);
            return DeckArt.Cover(url, D, Radii.Circle(D), NowPlayingPanel.HeroWashColor(url), track?.Image?.BlurHash);
        }
    }

    /// <summary>The cartridge's paper label: TITLE · ARTIST, uppercase, one line.</summary>
    sealed class MdLabel : Component
    {
        public required DeckSignals Sig;
        public required float W, H;

        public override Element Render()
        {
            _ = Sig.CoverGen.Value;
            var track = UseContext(PlaybackBridge.Slot)?.CurrentTrack.Value;
            string title = track?.Title ?? "";
            string artist = track is null ? "" : DetailFormat.ArtistNames(track.Artists);
            string text = title.Length > 0 && artist.Length > 0 ? title + " · " + artist
                        : title.Length > 0 ? title : artist;
            return new TextEl(text.ToUpperInvariant())
            {
                Width = W,
                Height = H * 0.66f,
                Size = H * 0.44f,
                Weight = 600,
                CharSpacing = 60f,
                Color = Hex(0x23262B),
                MaxLines = 1,
                Trim = TextTrim.CharacterEllipsis,
                Shrink = 0f,
            };
        }
    }
}
