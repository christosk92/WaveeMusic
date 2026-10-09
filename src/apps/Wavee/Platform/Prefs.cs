// ── Platform/Prefs.cs ──────────────────────────────────────────────────────────────────────────────────────────────
// the four cross-surface preference epochs: AppearancePrefs, LyricsPrefs, DetailHeroPrefs, NpvPlayerPrefs
//
// Role: CORE
// Owner: L
// Wave: 4
// Budget: ~250 lines (UNVERIFIED)
// Spec: ch 00 §9.5 (proposes the file, states no number)
// Partial: Prefs.Player.cs (the NpvPlayer + PlayerBar epochs) — named because this file passed its UNVERIFIED
//          ~250 by more than 30 %.
//
// ── THE ONE SHAPE, STATED ONCE ───────────────────────────────────────────────────────────────────────────────────────
//
// `Platform.Settings` is the TRUTH and it is not observable: a mounted surface that read it once has no way to learn
// that Settings wrote a new value. So every cross-surface preference gets an EPOCH — a `Signal<int>` a writer bumps
// exactly once — and every reactive read is two lines:
//
//     _ = Epoch.Value;                       // subscribe: this is the update EDGE
//     return Platform.Settings.Get(key);     // re-read: the store is the TRUTH
//
// Never the other way round. Caching the value in the signal would make the signal a second source of truth that a
// restart, a settings import or a second window silently disagrees with.
//
// ── WHY FOUR EPOCHS AND NOT ONE ──────────────────────────────────────────────────────────────────────────────────────
//
// `Platform.SettingsChanged` already bumps on EVERY write, and a surface could subscribe to that instead. It would also
// re-render every mounted lyrics view when the user changed the audio output device. The epochs are the fan-out
// boundary: one per group of surfaces that genuinely move together, so a row-density change re-reads the track lists
// and nothing else. A NEW epoch is a design decision, not a convenience — it asserts that a new independent set of
// surfaces exists.
//
// ── WHY THE CLAMPS ARE HERE AND NOT AT THE CALL SITES ────────────────────────────────────────────────────────────────
//
// Every persisted int is a WIRE VALUE: a hand-edited registry entry, a downgrade from a build that shipped more
// treatments, or a future build's value read by this one. A clamp at the READER turns "7" into the default rather than
// into nothing at all — and a clamp at the WRITER is what keeps the store from ever holding a value this build cannot
// read back. Both ends, in one place.

using FluentGpu.Localization;
using FluentGpu.Signals;

namespace Wavee;

public static partial class Prefs
{
    // ══ 1. APPEARANCE ════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The cross-surface APPEARANCE epoch. Settings writes bump it once so mounted and KeepAlive-parked
    /// player/detail/artist surfaces re-read their persisted appearance flags on the same frame.
    ///
    /// <para>WHAT BUMPS IT: marquee text, the three colour-wash settings, row density, hide-track-artwork, track list style, the animated
    /// lyrics backdrop, the liked-cover treatment, and the daylist clock. WHAT DOES NOT: the THEME (the engine's own epoch re-themes every
    /// mounted render in place) and the ZOOM (the host folds it into the window scale, which relayouts everything
    /// anyway).</para></summary>
    public static class Appearance
    {
        /// <inheritdoc cref="Appearance"/>
        public static readonly Signal<int> Epoch = new(0);

        /// <summary>Announce that an appearance preference changed. Called ONCE per write, by the writer, AFTER the
        /// store has it — a bump before the write makes every reader re-read the old value.</summary>
        public static void Bump() => Epoch.Value = Epoch.Peek() + 1;

        /// <summary>Reactive read of the app-wide track-cell ARTWORK policy (Settings ▸ Lists ▸ "Always hide track
        /// artwork"). Every track surface, the drag insertion preview and the row-density miniature read it.</summary>
        public static bool TrackArtworkHidden()
        {
            _ = Epoch.Value;
            return Platform.Settings.Get(Platform.Keys.HideTrackArtwork);
        }

        /// <summary>Reactive read of the marquee policy: may an overflowing title scroll under the pointer at all?</summary>
        public static bool Marquee()
        {
            _ = Epoch.Value;
            return Platform.Settings.Get(Platform.Keys.MarqueeEnabled);
        }

        /// <summary>Reactive read of the tinted-surfaces level. <see cref="WashLevel.Off"/> means every tone plane, hero
        /// wash, artist blend and shell tint binder takes its <c>disabled</c> arm and paints NOTHING, on the already-open
        /// page, with no restart and no navigation.</summary>
        public static WashLevel SurfaceWash()
        {
            _ = Epoch.Value;
            return ColorWashRules.Level(Platform.Settings.Get(Platform.Keys.WashSurfaces),
                Platform.Settings.Get(Platform.Keys.ColorWashesEnabled));
        }

        /// <summary>Reactive read of "accent from artwork": may a page take its accent colour from the cover art?</summary>
        public static bool AccentFromArtwork()
        {
            _ = Epoch.Value;
            return ColorWashRules.Flag(Platform.Settings.Get(Platform.Keys.WashAccent),
                Platform.Settings.Get(Platform.Keys.ColorWashesEnabled));
        }

        /// <summary>Reactive read of "now-playing colours": may the lyrics pill and the Verse ghosts take the playing
        /// track's colours?</summary>
        public static bool NowPlayingColors()
        {
            _ = Epoch.Value;
            return ColorWashRules.Flag(Platform.Settings.Get(Platform.Keys.WashNowPlaying),
                Platform.Settings.Get(Platform.Keys.ColorWashesEnabled));
        }

        /// <summary>Reactive read of the row-density rung (0 Compact · 1 Default · 2 Cozy · 3 Comfortable), clamped.
        /// The LADDERS themselves are owner M's (`Entities/Track.cs`'s table rules); this is only the persisted
        /// choice.</summary>
        public static int RowDensity()
        {
            _ = Epoch.Value;
            return Clamp(Platform.Settings.Get(Platform.Keys.RowDensity), 4);
        }

        /// <summary>Reactive read of the track-list SKIN (0 Modern · 1 Classic), clamped.</summary>
        public static int TrackRowStyle()
        {
            _ = Epoch.Value;
            return Clamp(Platform.Settings.Get(Platform.Keys.TrackRowStyle), 2);
        }

        /// <summary>Reactive read of the Liked Songs cover TREATMENT, as the raw persisted int, clamped against the
        /// count owner O's liked-cover rules publish.
        /// <para>The clamp is the point: a hand-edited value or a downgrade from a build that shipped more treatments
        /// must read as the STOCK treatment, never as nothing at all. The rules class owns what each rung MEANS; this
        /// owns only that the rung is in range.</para></summary>
        public static int LikedCover(int treatmentCount)
        {
            _ = Epoch.Value;
            return Clamp(Platform.Settings.Get(Platform.Keys.LikedCoverStyle), treatmentCount);
        }

        /// <summary>Reactive read of the page-swap motion STYLE (Settings ▸ Appearance ▸ Page motion), as the raw
        /// persisted int, clamped against <paramref name="styleCount"/>.
        /// <para>The clamp is the point: a hand-edited value or a downgrade from a build that shipped more styles must
        /// read as a real style, never as nothing at all — same contract as <see cref="LikedCover"/>.</para></summary>
        public static int PageMotionStyle(int styleCount)
        {
            _ = Epoch.Value;
            return Clamp(Platform.Settings.Get(Platform.Keys.PageMotionStyle), styleCount);
        }

        /// <summary>Reactive read of what the daylist clock shows (Settings ▸ Appearance ▸ Lists ▸ Daylist clock, #185),
        /// clamped: a hand-edited or downgraded value reads as <see cref="DaylistClockMode.Both"/>, never as an empty
        /// clock. The playlist hero strip and the Home card both read it, so a pick re-renders them on the same frame.</summary>
        public static DaylistClockMode DaylistClock()
        {
            _ = Epoch.Value;
            return DaylistClockShows.FromSetting(Platform.Settings.Get(Platform.Keys.DaylistClock));
        }

        /// <summary>The ONE writer every appearance row goes through: persist, then bump so every mounted surface
        /// re-reads on the same frame.</summary>
        public static void Set<T>(SettingKey<T> key, T value)
        {
            Platform.Settings.Set(key, value);
            Bump();
        }
    }

    // ══ 2. LYRICS ════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The cross-surface LYRICS epoch, plus the SECONDARY-LINE state machine both lyrics surfaces share.
    ///
    /// <para><see cref="Available"/> is published by whichever lyrics surface prepared the document (once per document)
    /// and read by the rail header and the immersive top bar: the toggle is not rendered AT ALL for a document with
    /// neither layer, and it CYCLES only through the layers that document actually has. Both surfaces prepare the SAME
    /// document, so their writes agree and the signal coalesces the second one.</para>
    ///
    /// <para><b>The secondary line is SESSION state, not a setting.</b> Settings ▸ Appearance ▸ Lyrics no longer offers
    /// it and no key persists it, so every launch starts at <see cref="None"/> — the old default — and the two globe
    /// toggles are the only writers. The layers themselves are untouched; only the stored preference is gone.</para></summary>
    public static class Lyrics
    {
        /// <inheritdoc cref="Lyrics"/>
        public static readonly Signal<int> Epoch = new(0);
        /// <inheritdoc cref="Appearance.Bump"/>
        public static void Bump() => Epoch.Value = Epoch.Peek() + 1;

        /// <summary>The three states of the secondary-line preference.</summary>
        public const int None = 0, Translation = 1, Romanization = 2;

        /// <summary>…and the two capability bits of <see cref="Available"/>. Deliberately <c>1 &lt;&lt; (mode − 1)</c>,
        /// so <see cref="BitFor"/> is ARITHMETIC rather than a map that could drift from the modes above.</summary>
        public const int HasTranslation = 1, HasRomanization = 2;

        /// <summary>Which secondary layers the document ON SCREEN carries (a bit set of <see cref="HasTranslation"/> /
        /// <see cref="HasRomanization"/>). 0 ⇒ no toggle is offered at all.</summary>
        public static readonly Signal<int> Available = new(0);

        /// <summary>Coerce a persisted / hand-edited value into a real mode — a stray 7 must show the ORIGINAL line, not
        /// nothing.</summary>
        public static int ClampMode(int mode) => (uint)mode <= Romanization ? mode : None;

        /// <summary>The capability bit a mode needs. <see cref="None"/> needs none, so it is always reachable.</summary>
        public static int BitFor(int mode) => mode is Translation or Romanization ? 1 << (mode - 1) : 0;

        /// <summary>The NEXT state of the cycling header toggle: none → translation → romanization → none, SKIPPING any
        /// layer this document does not have. "None" is always reachable, so the cycle can never trap the user in a
        /// layer they cannot leave.</summary>
        public static int Next(int mode, int available)
        {
            for (int step = 1; step <= 3; step++)
            {
                int next = (ClampMode(mode) + step) % 3;
                if (next == None || (available & BitFor(next)) != 0) return next;
            }
            return None;
        }

        /// <summary>The toggle's tooltip. It names the state the view is in NOW — a cycling control whose tooltip named
        /// its NEXT state would read as a lie the moment the user hovered it after clicking.</summary>
        public static string Tooltip(int mode) => ClampMode(mode) switch
        {
            Translation => Loc.Get(Strings.Player.LyricsSecondaryTranslation),
            Romanization => Loc.Get(Strings.Player.LyricsSecondaryRomanization),
            _ => Loc.Get(Strings.Player.LyricsSecondaryOff),
        };

        /// <summary>The secondary line CHOSEN in this session. Not persisted (see the class summary): a fresh process
        /// starts at <see cref="None"/>, which is exactly what the removed setting defaulted to.</summary>
        static int s_secondaryLine = None;

        /// <summary>Reactive read of the session's secondary-line mode. Still epoch-gated, so the globe toggles and the
        /// lyrics surfaces settle on the same frame they always did.</summary>
        public static int SecondaryLine()
        {
            _ = Epoch.Value;
            return ClampMode(s_secondaryLine);
        }

        /// <summary>Reactive read of the lyrics blur strength. <b>−1 means AUTO</b> and is a real, persisted value — the
        /// surface resolves it against its own backdrop, and 0 (the user turning the blur off entirely) must never be
        /// confused with it.</summary>
        public static int BlurStrength()
        {
            _ = Epoch.Value;
            int v = Platform.Settings.Get(Platform.Keys.LyricsBlurStrength);
            return v < 0 ? -1 : System.Math.Min(v, 100);
        }

        /// <summary>The ONE writer both globe toggles go through: store the clamped mode for this session, then bump so
        /// every mounted lyrics surface re-reads it on the same frame.</summary>
        public static void SetSecondaryLine(int mode)
        {
            s_secondaryLine = ClampMode(mode);
            Bump();
        }

        /// <summary>Persist the blur strength (−1 = Auto, else 0-100) and bump.</summary>
        public static void SetBlurStrength(int value)
        {
            Platform.Settings.Set(Platform.Keys.LyricsBlurStrength, value < 0 ? -1 : System.Math.Min(value, 100));
            Bump();
        }
    }

    /// <summary>The on-device AI lyrics preferences (AiLyrics): one epoch for the Settings card, the lyrics header button
    /// and the host's driver, so a toggle reaches all three on the same frame. Writes go through <see cref="Set"/>.</summary>
    public static class AiLyrics
    {
        /// <inheritdoc cref="AiLyrics"/>
        public static readonly Signal<int> Epoch = new(0);
        /// <inheritdoc cref="Appearance.Bump"/>
        public static void Bump() => Epoch.Value = Epoch.Peek() + 1;

        public static bool Enabled() { _ = Epoch.Value; return Platform.Settings.Get(Platform.Keys.AiLyricsEnabled); }
        public static bool WordSync() { _ = Epoch.Value; return Platform.Settings.Get(Platform.Keys.AiLyricsWordSync); }
        public static bool PlainText() { _ = Epoch.Value; return Platform.Settings.Get(Platform.Keys.AiLyricsPlainText); }
        public static bool OnBatterySaver() { _ = Epoch.Value; return Platform.Settings.Get(Platform.Keys.AiLyricsOnBatterySaver); }

        /// <summary>The installed aligner languages, in order, never empty ("en" when nothing valid is stored).</summary>
        public static IReadOnlyList<string> Languages()
        {
            _ = Epoch.Value;
            var list = new List<string>();
            foreach (var part in (Platform.Settings.Get(Platform.Keys.AiLyricsLanguages) ?? "").Split(',', System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries))
                if (part.Length is >= 2 and <= 3 && !list.Contains(part)) list.Add(part.ToLowerInvariant());
            if (list.Count == 0) list.Add("en");
            return list;
        }

        public static void Set(SettingKey<bool> key, bool value) { Platform.Settings.Set(key, value); Bump(); }

        public static void SetLanguages(IEnumerable<string> languages)
        {
            Platform.Settings.Set(Platform.Keys.AiLyricsLanguages, string.Join(",", languages));
            Bump();
        }
    }

    // ══ 3. THE DETAIL PAGE'S LAYOUT ══════════════════════════════════════════════════════════════════════════════════

    /// <summary>The cross-surface DETAIL-LAYOUT epoch: bumped when Settings ▸ Lists ▸ "Track page layout" (or the rail
    /// uniformity switch, or a rail-width reset) changes, so any mounted — including KeepAlive-parked — detail frame
    /// re-resolves rail-vs-hero LIVE rather than on its next remount.</summary>
    public static class DetailHero
    {
        /// <inheritdoc cref="DetailHero"/>
        public static readonly Signal<int> Epoch = new(0);
        /// <inheritdoc cref="Appearance.Bump"/>
        public static void Bump() => Epoch.Value = Epoch.Peek() + 1;

        /// <summary>The two page layouts.</summary>
        public const int Automatic = 0, Hero = 1;

        /// <summary>Reactive read of the page-layout choice, clamped.</summary>
        public static int PageLayout()
        {
            _ = Epoch.Value;
            return Clamp(Platform.Settings.Get(Platform.Keys.DetailPageLayout), 2);
        }

        /// <summary>Reactive read of "keep the left rail the same size across kinds". Only meaningful in
        /// <see cref="Automatic"/>; the Hero layout has no rail to size.</summary>
        public static bool RailUniform()
        {
            _ = Epoch.Value;
            return Platform.Settings.Get(Platform.Keys.DetailRailUniform);
        }

        /// <summary>The ONE writer for every row on this epoch.</summary>
        public static void Set<T>(SettingKey<T> key, T value)
        {
            Platform.Settings.Set(key, value);
            Bump();
        }
    }

    // ══ 4-5. the now-playing player and the player bar live in the named partial `Prefs.Player.cs` ═════════════════

    // ══ 6. SHARED ════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>THE clamp every persisted enum-ish int goes through: in range, or 0. One function, so a hand-edited
    /// registry value, a downgrade, and a future build's value all resolve the same way everywhere.</summary>
    public static int Clamp(int value, int count) => (uint)value < (uint)count ? value : 0;

    /// <summary>Bump EVERY epoch. For the two moments where the store changed underneath the app rather than through a
    /// writer: a settings import, and a scope switch that re-pointed the store at a different account.</summary>
    public static void BumpAll()
    {
        Appearance.Bump();
        Lyrics.Bump();
        DetailHero.Bump();
        NpvPlayer.Bump();
        PlayerBar.Bump();
        Stage.Bump();
    }
}

/// <summary>How much colour the tinted surfaces paint. Stored as the int 0 / 1 / 2 in <c>appearance.wash.surfaces</c>.</summary>
public enum WashLevel : byte
{
    Off = 0,
    Subtle = 1,
    Rich = 2,
}

/// <summary>The colour-wash settings' migration rule, pure so the tests can pin it without a store.
///
/// <para>The migration happens at READ time, not at boot: the settings store has no "does this key exist" probe, so the
/// three new keys default to <see cref="Unset"/> (-1) and fall back to the legacy <c>appearance.colorWashes.enabled</c>.
/// An explicit write always wins. A stray value outside 0..2 (a hand-edited store, a newer build) also falls back to the
/// legacy answer, never to a crash.</para></summary>
public static class ColorWashRules
{
    /// <summary>The "never written" value of the three wash keys.</summary>
    public const int Unset = -1;

    /// <summary>The surfaces level. An unset or out-of-range value answers from the legacy switch: TRUE is Subtle, FALSE is Off.</summary>
    public static WashLevel Level(int stored, bool legacyEnabled) => stored switch
    {
        0 => WashLevel.Off,
        1 => WashLevel.Subtle,
        2 => WashLevel.Rich,
        _ => legacyEnabled ? WashLevel.Subtle : WashLevel.Off,
    };

    /// <summary>An On/Off wash flag (accent from artwork, now-playing colours). An unset or out-of-range value answers
    /// from the legacy switch.</summary>
    public static bool Flag(int stored, bool legacyEnabled) => stored switch
    {
        0 => false,
        1 => true,
        _ => legacyEnabled,
    };
}
