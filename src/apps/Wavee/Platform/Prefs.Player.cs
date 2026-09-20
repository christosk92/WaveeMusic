// ── Platform/Prefs.Player.cs ───────────────────────────────────────────────────────────────────────────────────────
// the now-playing presentation / player-style epoch (NpvPlayerPrefs) and the player bar's elapsed/remaining epoch — a
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

    /// <summary>The cross-surface NOW-PLAYING-PRESENTATION epoch. Five surfaces write it — the rail header row, the
    /// style flyout, the artwork context menu, Settings ▸ Theme and the command palette — and the deck faces read it for
    /// their own options, so every writer clamps, persists and bumps ONCE.
    ///
    /// <para>The CATALOG (which presets exist, which options each carries, what a choice means) is owner K's
    /// `Shell/Deck.cs`. This section holds only the persisted CHOICE and the clamps, so the preference plumbing does not
    /// depend on the catalog and the catalog does not depend on the settings store.</para></summary>
    public static class NpvPlayer
    {
        /// <inheritdoc cref="NpvPlayer"/>
        public static readonly Signal<int> Epoch = new(0);
        /// <inheritdoc cref="Appearance.Bump"/>
        public static void Bump() => Epoch.Value = Epoch.Peek() + 1;

        /// <summary>The style flyout's CONTROLLED open state — shared so the artwork context menu opens the SAME popup
        /// the gear owns, rather than a second one stacked on it.</summary>
        public static readonly Signal<bool> StyleFlyoutOpen = new(false);

        /// <summary>The two presentations the hero can take.</summary>
        public const int Cover = 0, Player = 1;

        /// <summary>Anything that is not <see cref="Player"/> is the cover — the safe reading of an unknown value.</summary>
        public static int ClampPresentation(int v) => v == Player ? Player : Cover;

        /// <summary>PURE: which face the hero actually SHOWS. The Cover/‹Player› switch is a DEVELOPER surface — it is
        /// composed only in developer mode — so a normal build shows the <see cref="Cover"/> whatever is stored, and a
        /// value persisted while developer mode was on cannot bring the deck back when it is off. The stored preference
        /// is untouched: turning developer mode back on returns the face the user last chose.</summary>
        public static int ResolvePresentation(int stored, bool developerMode)
            => developerMode ? ClampPresentation(stored) : Cover;

        /// <summary>Reactive read of the STORED hero presentation — what the switch and every writer see. Surfaces that
        /// PAINT the hero read <see cref="ResolvedPresentation"/> instead.</summary>
        public static int Presentation()
        {
            _ = Epoch.Value;
            return ClampPresentation(Platform.Settings.Get(Platform.Keys.NpvPresentation));
        }

        /// <summary>Reactive read of the face the hero paints — <see cref="Presentation"/> resolved against the live
        /// developer-mode signal, so a flip of that switch re-renders every reader without a relaunch.</summary>
        public static int ResolvedPresentation()
            => ResolvePresentation(Presentation(), Platform.Developer.Enabled.Value);

        /// <summary>Reactive read of the chosen player-style id, clamped against the catalog's preset count (owner K
        /// passes it; a downgrade or a hand-edited value then reads as preset 0 rather than as nothing).</summary>
        public static int Style(int presetCount)
        {
            _ = Epoch.Value;
            return Clamp(Platform.Settings.Get(Platform.Keys.NpvPlayerStyle), presetCount);
        }

        /// <summary>Reactive read of one preset OPTION's chosen index, clamped against that option's choice count.</summary>
        public static int Choice(string presetSlug, string optionSlug, int choiceCount)
        {
            _ = Epoch.Value;
            return Clamp(Platform.Settings.Get(Platform.Keys.NpvOption(presetSlug, optionSlug)), choiceCount);
        }

        /// <summary>Persist a presentation and bump.</summary>
        public static void SetPresentation(int v)
        {
            Platform.Settings.Set(Platform.Keys.NpvPresentation, ClampPresentation(v));
            Bump();
        }

        /// <summary>Flip cover ↔ player.</summary>
        public static void TogglePresentation()
            => SetPresentation(Presentation() == Cover ? Player : Cover);

        /// <summary>Persist a style — and SHOW it. A style chosen while the cover was up flips the presentation to
        /// <see cref="Player"/> in the same write, because picking a player and then not seeing it is the report this
        /// answers.</summary>
        public static void SetStyle(int id, int presetCount)
        {
            Platform.Settings.Set(Platform.Keys.NpvPlayerStyle, Clamp(id, presetCount));
            if (ClampPresentation(Platform.Settings.Get(Platform.Keys.NpvPresentation)) != Player)
                Platform.Settings.Set(Platform.Keys.NpvPresentation, Player);
            Bump();
        }

        /// <summary>Step to the next style in catalog order, wrapping.</summary>
        public static void NextStyle(int presetCount)
            => SetStyle(presetCount <= 0 ? 0 : (Style(presetCount) + 1) % presetCount, presetCount);

        /// <summary>Persist one preset option's choice and bump.</summary>
        public static void SetChoice(string presetSlug, string optionSlug, int index, int choiceCount)
        {
            Platform.Settings.Set(Platform.Keys.NpvOption(presetSlug, optionSlug), Clamp(index, choiceCount));
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
