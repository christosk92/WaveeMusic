// ── Wavee.Tests/TestScope.cs — the two lines every entity-core test needs ─────────────────────────────────────────
//
// D17: these tests never start the engine loop, never open a window and never touch a network. They DO need a live
// `Entities.Current`, because a handle is a slot into the current scope's tables and a commit writes into them.
//
// `Entities.Boot(CatalogScope.Fake())` is the documented way to get one: Entities.cs's own summary says "the store and
// the planner are optional (the partial hooks below are erased when their files are absent), so `--fake` and the unit
// tests boot a pure in-memory graph". That contract is load-bearing here — `Entities/Store.cs`'s `StoreBoot` /
// `StoreWarm` hooks must not open sqlite for a fake scope (or must open it lazily), or every fact in this assembly
// pays for a database it never reads.
//
// Booting per test is also the isolation: a fresh `Scope` is a fresh set of tables, so nothing leaks between facts.
// The process-wide statics that a Boot does NOT reset — `Entities.Strings`, `Entities.Publication`, `Entities.Now` and
// `Palette.Table` — are why every class here joins `EntitiesCollection`, which disables parallelization.

using System.Text;
using FluentGpu.Foundation;
using Wavee;

namespace Wavee.Tests;

static class TestScope
{
    /// <summary>A clean, offline graph plus a fixed clock. The clock matters: `Entities.Now` is what every `FetchedAt`
    /// stamp and every palette TTL reads, and core never calls a real one (P7/P10).</summary>
    public static void Fresh(int now = 1_000_000)
    {
        Entities.Boot(CatalogScope.Fake());
        Entities.Now = now;
    }

    /// <summary>Stage some UTF-8. The decoder's own shape: text goes into the staging arena as bytes and the drain
    /// interns it, because the socket thread may not touch the <see cref="StringTable"/> (C1).</summary>
    public static TextRef Text(this Staging s, string value)
        => value.Length == 0 ? default : s.AddText(Encoding.UTF8.GetBytes(value));

    /// <summary>Commit a batch and publish it, the way one UI drain does (C1/C3), then hand the buffer back.</summary>
    public static uint CommitAndPublish(Staging s)
    {
        Entities.Commit(s);
        uint publication = Entities.Publish();
        Staging.Return(s);
        return publication;
    }
}
