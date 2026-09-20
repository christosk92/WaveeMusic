using System;
using System.Collections.Generic;
using Wavee;

namespace Wavee.Backend.Wiring;

// ── The go-live install ledger (hydration-facade-design.md §2.6) ─────────────────────────────────────────────────────
// WHY this type exists: the go-live block used to be a long list of one-way writes — `svc.X.SetInner(live)`, `svc.Y = live`,
// `SomeGlobal.Hook = live` — and `Services.GoOffline()` was a HAND-MAINTAINED list of their inverses. The two lists drifted
// (metadata-entry-points-inventory.md §8.2 #18: AlbumEnrichment, the video hooks, the cover-colour filler and the whole
// StoreLibrarySource hook set were installed at go-live and never torn down), because nothing in the compiler or the tests
// tied an install to its undo.
//
// LiveWiring makes the pairing STRUCTURAL: you cannot install through it without handing it the inverse in the same call,
// `Uninstall()` replays those inverses in reverse order, and `AssertCovers(Services.LiveSeams)` fails the go-live path when
// a seam on the required list was never registered. "No install without a teardown" becomes a build/run gate instead of a
// review convention (wiring-discipline.md).
public sealed class LiveWiring
{
    readonly object _gate = new();
    readonly List<(string Name, Action Uninstall)> _entries = new();
    readonly HashSet<string> _names = new(StringComparer.Ordinal);
    readonly WaveeLogger _log;
    /// <summary>Set by <see cref="Uninstall"/> and never cleared: this ledger is SPENT. See <see cref="Set"/>.</summary>
    bool _spent;

    /// <param name="log">Teardown diagnostics. Uninstall NEVER throws (a logout that half-fails must still reach the
    /// offline state), so this log is the only trace a failing inverse leaves — pass a real sink.</param>
    public LiveWiring(WaveeLogger log) => _log = log;

    /// <summary>The seam names installed through this wiring, in install order. Empty after <see cref="Uninstall"/>.</summary>
    public IReadOnlyList<string> Installed
    {
        get
        {
            lock (_gate)
            {
                var names = new string[_entries.Count];
                for (int i = 0; i < _entries.Count; i++) names[i] = _entries[i].Name;
                return names;
            }
        }
    }

    /// <summary>Install a live seam NOW and record its inverse. <paramref name="uninstall"/> is what
    /// <see cref="Uninstall"/> runs on logout — it must restore the OFFLINE value (a named offline impl, never null-as-
    /// "unwired"; see wiring-discipline.md), not merely drop the live one.
    ///
    /// The inverse is recorded BEFORE <paramref name="install"/> runs on purpose: an install that throws half-way still
    /// has its teardown on the stack, so the caller's `catch` → `Uninstall()` undoes the partial state instead of leaking it.
    ///
    /// A DUPLICATE NAME IS REJECTED (throws). Replace-with-teardown was the alternative and is worse here: two installs
    /// of one seam in a single go-live means two owners racing it, and silently keeping the last would also silently move
    /// that seam's position in the reverse-order teardown. Loud is correct — this is a composition-root bug, not a runtime
    /// condition.</summary>
    public void Set(string name, Action install, Action uninstall)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(install);
        ArgumentNullException.ThrowIfNull(uninstall);
        bool spent = false;
        lock (_gate)
        {
            // ONCE UNINSTALLED, THIS LEDGER IS SPENT. Without this a `Set` that races the teardown — the go-live block
            // keeps installing until the very end, and `svc.GoLive` makes the logout menu reachable long before it — was
            // recorded into a ledger that had already been replayed and cleared, so the seam went live AFTER logout with
            // nothing left to undo it (and the cleared `_names` even let a duplicate through). Running the inverse
            // immediately lands the seam on its OFFLINE value, which is the correct end state for a session that is
            // already gone; nothing is recorded, because there is nothing left to undo.
            if (_spent) spent = true;
            else
            {
                if (!_names.Add(name))
                    throw new InvalidOperationException(
                        $"LiveWiring: seam '{name}' is already installed. One seam has exactly one owner in the go-live block.");
                _entries.Add((name, uninstall));
            }
        }
        if (spent)
        {
            _log.Info("live seam '" + name + "' arrived after teardown — held at its offline value, not installed");
            Run(name, uninstall);   // outside the lock: an inverse is arbitrary caller code
            return;
        }
        install();
    }

    /// <summary>The common shape: point a Switchable* at its live inner, and record "point it back at a FRESH offline
    /// inner" as the inverse. <paramref name="offline"/> is a FACTORY, not a value: it runs at teardown, so a stateful
    /// offline stand-in (a fresh session, a fresh transport stub) is built clean on logout rather than being carried
    /// across the whole live session.</summary>
    public void Swap<T>(string name, Action<T> setInner, T live, Func<T> offline)
    {
        ArgumentNullException.ThrowIfNull(setInner);
        ArgumentNullException.ThrowIfNull(offline);
        Set(name, () => setInner(live), () => setInner(offline()));
    }

    /// <summary>Run every recorded inverse in REVERSE install order (a seam built on an earlier one is torn down first),
    /// then forget them — so a second call is a no-op and GoOffline/DisposeAsync can both call it.
    ///
    /// Each inverse is guarded: a throwing teardown is logged and the rest still run. A logout that cannot complete must
    /// not strand the app half-live.</summary>
    public void Uninstall()
    {
        (string Name, Action Uninstall)[] entries;
        lock (_gate)
        {
            _spent = true;   // set even for an empty ledger — a bootstrap that failed before its first install is still over
            if (_entries.Count == 0) return;
            entries = _entries.ToArray();
            _entries.Clear();
            _names.Clear();
        }
        for (int i = entries.Length - 1; i >= 0; i--) Run(entries[i].Name, entries[i].Uninstall);
        _log.Info("live wiring uninstalled (" + entries.Length + " seams back to their offline values)");
    }

    /// <summary>Run one inverse, guarded: a throwing teardown is logged and never propagates — a logout that cannot
    /// complete must not strand the app half-live.</summary>
    void Run(string name, Action uninstall)
    {
        try { uninstall(); }
        catch (Exception ex) { _log.Error("live seam teardown failed: " + name, ex); }
    }

    /// <summary>The go-live gate: every name in <paramref name="required"/> must have been installed through THIS wiring
    /// (i.e. must have a recorded teardown). Throws naming the missing seams — that is the failure mode this whole type
    /// exists to make impossible to ship.</summary>
    public void AssertCovers(IEnumerable<string> required)
    {
        ArgumentNullException.ThrowIfNull(required);
        List<string>? missing = null;
        lock (_gate)
            foreach (var name in required)
                if (!_names.Contains(name))
                    (missing ??= new List<string>()).Add(name);
        if (missing is null) return;
        throw new InvalidOperationException(
            "LiveWiring: go-live installed no teardown for " + missing.Count + " required seam(s): "
            + string.Join(", ", missing)
            + ". Every live install must go through LiveWiring.Set/Swap so GoOffline can undo it.");
    }
}
