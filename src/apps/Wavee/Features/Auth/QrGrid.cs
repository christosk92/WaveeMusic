using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// ── A BoxEl renderer for the QR module matrix (the encoder lives in Qr.cs) ────────────────────────────────────────────
// Renders Qr.Encode()'s matrix as a COALESCED BoxEl grid (consecutive dark modules in a row merge into one box → a few
// hundred nodes, not version²), on a clean white quiet-zone plate. The production pairing QR deliberately has no
// centre logo: uninterrupted modules match the setup prototype and maximize contrast/scan reliability.
sealed class QrGrid : Component
{
    readonly string _text;
    readonly float _size;
    public QrGrid(string text, float size = 132f) { _text = text; _size = size; }

    public override Element Render()
    {
        bool[,] m;
        try { m = Qr.Encode(_text, Qr.Ecc.M); }
        catch { return Fallback(); }   // unencodable (e.g. text too long for v1–10) → graceful white plate

        int n = m.GetLength(0);
        const int quiet = 4;                              // ISO/IEC 18004 mandates a 4-module quiet zone
        int total = n + quiet * 2;
        int cell = Math.Max(3, (int)(_size / total));     // INTEGER cell → pixel-crisp modules; a sub-pixel cell blurs the grid → no scan
        float plate = total * cell;

        // Each row = a horizontal flex of alternating transparent/dark spans (coalesced runs → a few hundred nodes total,
        // not version²). Light spans are transparent so the white plate shows through.
        var rows = new Element[n];
        for (int y = 0; y < n; y++)
        {
            var spans = new List<Element>();
            int x = 0;
            while (x < n)
            {
                bool dark = m[x, y];
                int s = x;
                while (x < n && m[x, y] == dark) x++;
                spans.Add(new BoxEl { Width = (x - s) * cell, Height = cell, Fill = dark ? QrInk : ColorF.Transparent });
            }
            rows[y] = new BoxEl { Direction = 0, Width = n * cell, Height = cell, Children = spans.ToArray() };
        }

        var modules = new BoxEl
        {
            Direction = 1, Width = plate, Height = plate, Padding = Edges4.All(quiet * cell),
            Children = rows,
        };

        // White plate + uninterrupted modules, matching the clean square QR used by the onboarding prototype.
        return new BoxEl
        {
            Width = plate, Height = plate, AlignSelf = FlexAlign.Center,
            Corners = CornerRadius4.All(Radii.Card), Fill = ColorF.FromRgba(0xFF, 0xFF, 0xFF), ClipToBounds = true,
            Children = [modules],
        };
    }

    static readonly ColorF QrInk = ColorF.FromRgba(0x00, 0x00, 0x00);   // PURE black modules (max contrast on white → reliable binarization)

    Element Fallback() => new BoxEl
    {
        Width = _size, Height = _size, AlignSelf = FlexAlign.Center,
        Corners = CornerRadius4.All(Radii.Card), Fill = ColorF.FromRgba(0xFF, 0xFF, 0xFF),
        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Children = [new TextEl(Icons.MusicNote) { Size = 28f, FontFamily = Theme.IconFont, Color = ColorF.FromRgba(0x1D, 0xB9, 0x54) }],
    };
}
