using System;

namespace Wavee;

// Pure progression math for the detail track list's progressive-reveal ramp (see DetailTracks.cs). Extracted BCL-only so
// it is unit-testable in isolation — no engine, no component harness — the same pattern as DetailLayoutBreakpoints /
// ArtistHeroLayout. Measured: the cold shimmer→content swap mounted the whole visible band in one ~80ms UI frame (694
// spans re-recorded, record=72.4ms, gen2 GC). The ramp reveals REAL rows Chunk-at-a-time over a few frames instead.
internal static class DetailRevealRamp
{
    public const int Chunk = 12;      // real rows swapped shimmer→real per frame — one ≈20ms record slice of the measured 72ms
    public const int Cap = 60;        // the ramp only needs to cover the realized viewport band (~44 rows measured); past it, snap to all-real
    public const int Done = int.MaxValue;   // sentinel: ramp finished — every row is real (rows scrolled in later never re-shimmer)

    // The next reveal count, given the current count and the visible track count. Returns Done once a chunk reaches or
    // exceeds the realized band (min(visible, Cap)). The per-frame reveal clock calls this, then writes _reveal.
    public static int Next(int reveal, int visible)
    {
        int target = Math.Min(visible, Cap);
        int next = reveal + Chunk;
        return next >= target ? Done : next;
    }

    // Progressive-reveal gate: is the row at this display position a REAL row yet (vs a shimmer placeholder)? True once
    // the ramp count passes it; always true at Done (int.MaxValue), so a fully-revealed list pays nothing.
    public static bool Revealed(int displayIndex, int reveal) => displayIndex < reveal;
}
