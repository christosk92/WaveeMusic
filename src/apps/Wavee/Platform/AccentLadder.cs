// ── Platform/AccentLadder.cs ───────────────────────────────────────────────────────────────────────────────────────
// The page accent ladder: Graded → Payload → Held → Default.
//
// Role: UI (pure)
// Owner: L
//
// One pure decision every accent site shares. Graded is the cover's chrome grading; Payload is the wire colour the
// entity row carries (0 = none); Held is the last remembered answer from another page, used only while the current
// page's own answer is still pending (`!Definite`); Default is the semantic accent. Held never becomes a remembered
// answer itself, so a stale colour cannot chain across more than one pending page.

using System;
using FluentGpu.Foundation;

namespace Wavee;

public static class AccentLadder
{
    public enum Rung : byte { Graded, Payload, Held, Default }

    /// <summary><paramref name="Definite"/>: the page already knows nothing better will arrive (no payload and a url
    /// that cannot be graded, or a fresh negative), so a held colour must not stand in.</summary>
    public readonly record struct Input(ColorF? Graded, uint Payload, bool Definite);

    public readonly record struct Result(ColorF Color, Rung Rung)
    {
        public bool Remember => Rung is Rung.Graded or Rung.Payload;
    }

    public static Result Resolve(in Input now, ColorF? held, ColorF fallback, Func<uint, ColorF> lift)
    {
        if (now.Graded is { } graded) return new Result(graded, Rung.Graded);
        if (now.Payload != 0) return new Result(lift(now.Payload), Rung.Payload);
        if (!now.Definite && held is { } h) return new Result(h, Rung.Held);
        return new Result(fallback, Rung.Default);
    }
}

/// <summary>The last remembered accent, process-wide. UI-thread state; only Graded/Payload answers are kept.</summary>
public static class AccentHold
{
    public static ColorF? Last { get; private set; }

    public static void Remember(in AccentLadder.Result r)
    {
        if (r.Remember) Last = r.Color;
    }

    public static void Reset() => Last = null;
}
