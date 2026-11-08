// ── Platform/Prefs.Player.cs ───────────────────────────────────────────────────────────────────────────────────────
// the now-playing player-style epoch (the style and its finish) and the player bar's elapsed/remaining epoch — a
// named partial of `Prefs.cs`, split on the plan's 30 %-over rule (§5)
//
// Role: CORE
// Owner: L
// Wave: 4
// Budget: part of Prefs.cs's ~250 (UNVERIFIED); this partial ≈ 130
// Spec: ch 00 §9.5, ch 30 §9.4
//
// The shape is `Prefs.cs`'s and is not restated here: the store is the TRUTH, the epoch is the update EDGE, every
// writer clamps, persists and bumps ONCE.

using FluentGpu.Signals;

namespace Wavee;

public static partial class Prefs
{
    // ══ 4. THE NOW-PLAYING VIEW'S PLAYER ═════════════════════════════════════════════════════════════════════════════

    /// <summary>The cross-surface PLAYER-STYLE epoch. Three surfaces write it (the style chip's picker, Settings ▸ Now playing and the
    /// command palette) and the hero and the deck faces read it, so every writer clamps, persists and bumps ONCE.
    ///
    /// <para>The CATALOG (which styles exist, which carry a finish) is owner K's `Shell/Rail.cs` (`Rail.PlayerCatalog`). This section
    /// holds only the persisted CHOICES and the clamps, so the preference plumbing does not depend on the catalog and the catalog does not
    /// depend on the settings store. The style is a normal user setting: no developer gate (the old Cover/‹Player› switch is gone; Cover
    /// is a style, the default).</para></summary>
    public static class NpvPlayer
    {
        /// <inheritdoc cref="NpvPlayer"/>
        public static readonly Signal<int> Epoch = new(0);
        /// <inheritdoc cref="Appearance.Bump"/>
        public static void Bump() => Epoch.Value = Epoch.Peek() + 1;

        /// <summary>Reactive read of the stored style id (unclamped: owner K clamps against its catalog).</summary>
        public static int Style()
        {
            _ = Epoch.Value;
            return Platform.Settings.Get(Platform.Keys.NpvStyle);
        }

        /// <summary>The stored style id, peeked.</summary>
        public static int StylePeek() => Platform.Settings.Get(Platform.Keys.NpvStyle);

        /// <summary>Reactive read of one style's finish index, clamped against its finish count.</summary>
        public static int Finish(string styleSlug, int finishCount)
        {
            _ = Epoch.Value;
            return FinishPeek(styleSlug, finishCount);
        }

        /// <summary>One style's finish index, peeked and clamped.</summary>
        public static int FinishPeek(string styleSlug, int finishCount)
            => Clamp(Platform.Settings.Get(Platform.Keys.NpvFinish(styleSlug)), finishCount);

        /// <summary>Persist a style and bump.</summary>
        public static void SetStyle(int id)
        {
            Platform.Settings.Set(Platform.Keys.NpvStyle, id);
            Bump();
        }

        /// <summary>Persist one style's finish and bump.</summary>
        public static void SetFinish(string styleSlug, int index)
        {
            Platform.Settings.Set(Platform.Keys.NpvFinish(styleSlug), index);
            Bump();
        }
    }

    // ══ 5. THE PLAYER BAR ════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>A FIFTH epoch, named here deliberately rather than left for owner I to invent. 0.2.9 carries it (a
    /// five-line class embedded in the player bar) and the plan's §2 row names only four — so it is RECORDED as the one
    /// addition rather than smuggled in: the elapsed/remaining toggle is a preference the player bar AND the context
    /// menu that flips it both read, which is the definition of cross-surface.</summary>
    public static class PlayerBar
    {
        /// <inheritdoc cref="PlayerBar"/>
        public static readonly Signal<int> Epoch = new(0);
        /// <inheritdoc cref="Appearance.Bump"/>
        public static void Bump() => Epoch.Value = Epoch.Peek() + 1;

        /// <summary>Reactive read: does the duration lane show the REMAINING time (true) or the track's total?</summary>
        public static bool ShowRemaining()
        {
            _ = Epoch.Value;
            return Platform.Settings.Get(Platform.Keys.PlayerBarShowRemaining);
        }

        /// <summary>Flip it.</summary>
        public static void ToggleRemaining()
        {
            Platform.Settings.Set(Platform.Keys.PlayerBarShowRemaining, !ShowRemaining());
            Bump();
        }
    }
}
