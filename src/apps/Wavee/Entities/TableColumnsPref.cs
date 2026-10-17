// ── Entities/TableColumnsPref.cs ───────────────────────────────────────────────────────────────────────────────────────
// the track table's two opt-in columns, Tempo and Plays, as a typed pref with verbs
//
// Role: CORE
// Spec: docs/themes/plan/technical-plan.md §5.1 C16 (X40); ch 03 items 41-50 (the column opt-ins)
//
// Before this file the table read `Platform.Keys.TempoColumn` / `PlaysColumn` by hand in three places (the snapshot's
// epoch-gated reads in `Track.Table.cs`, the sort menu's Plays field and the "…" menu's two toggles in
// `Track.Table.Chrome.cs`) and wrote them through the appearance writer. The reads and the writes are here now, so a
// table in any theme asks one door and the menu verbs are one-liners.
//
// Both columns are APPEARANCE prefs: a write persists, then bumps `Prefs.Appearance.Epoch` exactly once (the shape of
// `Platform/Prefs.cs`), so every mounted and KeepAlive-parked table re-reads on the same frame. Whether a column is
// OFFERED at all is the page's config (`Detail.Config.ShowTempo`, `PlaysColumnOptIn`), not a pref.

namespace Wavee;

public static class TableColumnsPref
{
    // ── reads ───────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Reactive read: is the Tempo column switched on? Subscribes <see cref="Prefs.Appearance.Epoch"/>, then
    /// re-reads the store (the store is the truth).</summary>
    public static bool Tempo()
    {
        _ = Prefs.Appearance.Epoch.Value;
        return Platform.Settings.Get(Platform.Keys.TempoColumn);
    }

    /// <summary>Reactive read: is the Plays column switched on?</summary>
    public static bool Plays()
    {
        _ = Prefs.Appearance.Epoch.Value;
        return Platform.Settings.Get(Platform.Keys.PlaysColumn);
    }

    /// <summary>The store's answer without joining the epoch — for a menu builder or an event handler, which is not a
    /// render and must not subscribe.</summary>
    public static bool TempoNow() => Platform.Settings.Get(Platform.Keys.TempoColumn);

    /// <summary>The Plays column's store answer, without joining the epoch (see <see cref="TempoNow"/>).</summary>
    public static bool PlaysNow() => Platform.Settings.Get(Platform.Keys.PlaysColumn);

    /// <summary>The Plays column's presence rule, in one place: a page that always shows it (<paramref name="configShowsPlays"/>),
    /// or one that offers it as an opt-in (<paramref name="optIn"/>) and the user switched it on
    /// (<paramref name="prefOn"/>).</summary>
    public static bool PlaysVisible(bool configShowsPlays, bool optIn, bool prefOn) => configShowsPlays || (optIn && prefOn);

    // ── verbs ───────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Switch the Tempo column on or off: persist, then ONE epoch bump.</summary>
    public static void SetTempo(bool on) => Prefs.Appearance.Set(Platform.Keys.TempoColumn, on);

    /// <summary>Switch the Plays column on or off: persist, then ONE epoch bump.</summary>
    public static void SetPlays(bool on) => Prefs.Appearance.Set(Platform.Keys.PlaysColumn, on);
}
