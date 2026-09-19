// ── Screens/Settings.cs ────────────────────────────────────────────────────────────────────────────────────────────
// SettingsCatalog, the tab model, and the page's pure decisions: the equalizer gain vector + presets, the quality and
// video ladders, the metered status line, the storage formatting and budget ladders, the notification-block banner
// decision, the app-language options, the About receipts' formatting, the Storage/About hero and meter rules
//
// Role: CORE
// Owner: R
// Wave: 6
// Budget: 700 lines
// Spec: ch 27 §9.3, §8 (the rules to port verbatim), §0 N3; tray plan §8 (the Notification-area rows); D7 (the lossless
// rung is offered only when a key deriver is installed)
//
// ENGINE-FREE apart from two plain WindowsApi VALUE types the decisions are phrased in (`NetworkCost`,
// `ToastDeliverySetting`) — no component, no signal, no token. Glyphs are stored as NAMES (the engine's glyphs.json
// keys) and resolved to `Icons.*` by the UI file, so `SettingsCatalogTests` asserts the no-repeat invariant on plain data.
//
// The About receipts' formatters (`Receipts`) are ch 27 §8's `GpuSummary`/`ClassifySharedIgpu`/`FormatBytes`/
// `FormatUptime`, which plan §2 files under `Screens/Diagnostics.cs` (owner S). They sit here because the About tab is
// this owner's surface and S's file is in flight; the orchestrator keeps ONE copy if both land.

using System.Globalization;
using System.Text;
using FluentGpu.WindowsApi.Network;
using FluentGpu.WindowsApi.Notifications;

namespace Wavee;

public static partial class Settings
{
    // ══ 1. THE TAB MODEL ════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The seven tabs, in strip order (ch 27 parity 2). The slug is the scroll key's suffix.</summary>
    public enum Tab : byte { General, Appearance, Playback, Notifications, Storage, Logs, About }

    /// <summary>Must stay 1:1 with <see cref="Tab"/> — a missing slug made About IndexOutOfRange in 0.2.9.</summary>
    public static readonly string[] TabSlugs = ["general", "appearance", "playback", "notifications", "storage", "logs", "about"];

    public static string SlugOf(Tab tab) => (uint)tab < (uint)TabSlugs.Length ? TabSlugs[(int)tab] : TabSlugs[0];

    /// <summary>A deep link's tab name → the tab, General when unknown (a bad arg lands on the page, never nowhere).</summary>
    public static Tab TabFromSlug(string? slug)
    {
        if (slug is { Length: > 0 })
            for (int i = 0; i < TabSlugs.Length; i++)
                if (string.Equals(TabSlugs[i], slug, StringComparison.OrdinalIgnoreCase)) return (Tab)i;
        return Tab.General;
    }

    // ══ 2. THE CATALOG (0.2.9 `App/SettingsCatalog.cs`, verbatim + the tray group) ═════════════════════════════════
    //
    // EVERY ROW CARRIES ITS OWN GLYPH, AND A SECTION'S GLYPH IS NEVER REUSED BY ONE OF ITS OWN ROWS — per SECTION, not
    // global (ch 27 §0 N3 lists the 17 legitimate cross-section repeats; do not "de-duplicate" them). SCOPE: General,
    // Appearance, Playback, Storage. Notifications enumerates its rows from `Notify.Prefs.AllTopics`; About and Logs have
    // no table rows.

    public static class Catalog
    {
        public readonly record struct Section(Tab Tab, string Title, string Glyph);

        /// <summary><paramref name="RowId"/> is unique within its <paramref name="Tab"/>, not merely its section.</summary>
        public readonly record struct Row(Tab Tab, string Section, string RowId, string Glyph);

        public static readonly Section[] Sections =
        [
            new(Tab.General, "Language & region", "Globe"),
            new(Tab.General, "Links", "Link"),
            // tray plan §8: between Links and Graphics
            new(Tab.General, "Notification area", "ThisPc"),
            new(Tab.General, "Graphics", "Devices"),
            new(Tab.General, "Developer", "Code"),

            new(Tab.Appearance, "Theme", "Brush"),
            new(Tab.Appearance, "Lists", "List"),
            new(Tab.Appearance, "Sidebar", "DockLeft"),
            new(Tab.Appearance, "Lyrics", "Microphone"),
            new(Tab.Appearance, "Now playing", "Album"),

            new(Tab.Playback, "Audio", "MusicNote"),
            new(Tab.Playback, "Sound", "Speakers"),
            new(Tab.Playback, "Video", "Movie"),
            new(Tab.Playback, "Player bar", "Pin"),

            new(Tab.Storage, "On this PC", "ThisPc"),
            new(Tab.Storage, "Playback cache", "Download"),
            new(Tab.Storage, "Metadata cache", "Document"),
            new(Tab.Storage, "Reset", "Delete"),
        ];

        public static readonly Row[] Rows =
        [
            new(Tab.General, "Language & region", "language", "LocaleLanguage"),
            // "OpenInNewWindow", not "Link": the section header already owns Link.
            new(Tab.General, "Links", "spotifyLinks", "OpenInNewWindow"),
            new(Tab.General, "Notification area", "trayIcon", "Pin"),
            new(Tab.General, "Notification area", "closeToTray", "ChromeClose"),
            new(Tab.General, "Notification area", "minimizeToTray", "ChromeMinimize"),
            new(Tab.General, "Notification area", "startHidden", "RevealPassword"),
            new(Tab.General, "Notification area", "startOnLogin", "Contact"),
            // "Device" (singular), not "Devices": the section owns the plural.
            new(Tab.General, "Graphics", "preferredGpu", "Device"),
            new(Tab.General, "Developer", "developerMode", "Settings"),
            new(Tab.General, "Developer", "fpsOverlay", "Clock"),
            new(Tab.General, "Developer", "dealerArchive", "Document"),
            new(Tab.General, "Developer", "simulateUpdate", "Refresh"),

            new(Tab.Appearance, "Theme", "theme", "Sun"),
            new(Tab.Appearance, "Theme", "zoom", "Zoom"),
            new(Tab.Appearance, "Theme", "marquee", "Font"),
            new(Tab.Appearance, "Theme", "colorWashes", "Design"),
            // "Movie", not "Design": `colorWashes` above already took Design, and a glyph may not repeat inside a
            // section (SettingsCatalogTests.NoGlyph_RepeatsWithinASection) — two identical icons in one list are two
            // rows the eye cannot tell apart at a glance, which is the whole point of the rule.
            new(Tab.Appearance, "Theme", "pageMotion", "Movie"),
            new(Tab.Appearance, "Lists", "rowDensity", "RowSize"),
            new(Tab.Appearance, "Lists", "hideTrackArtwork", "Picture"),
            new(Tab.Appearance, "Lists", "trackListStyle", "ViewList"),
            new(Tab.Appearance, "Lists", "pageLayout", "DockLeft"),
            new(Tab.Appearance, "Lists", "railUniform", "Pin"),
            new(Tab.Appearance, "Lists", "railReset", "Delete"),
            new(Tab.Appearance, "Sidebar", "sidebarDesign", "SplitView"),
            new(Tab.Appearance, "Sidebar", "sidebarCustomize", "Edit"),
            new(Tab.Appearance, "Lyrics", "lyricsSecondary", "Globe"),
            new(Tab.Appearance, "Lyrics", "lyricsBackdrop", "RefineSparkle"),
            new(Tab.Appearance, "Lyrics", "lyricsBlur", "Filter"),
            new(Tab.Appearance, "Now playing", "npvPresentation", "Picture"),
            new(Tab.Appearance, "Now playing", "npvStyle", "Settings"),

            new(Tab.Playback, "Audio", "audioQuality", "Headphones"),
            new(Tab.Playback, "Audio", "meteredQuality", "RadioTower"),
            new(Tab.Playback, "Audio", "rememberVolume", "Volume"),
            new(Tab.Playback, "Audio", "autoplay", "Play"),
            // G-132: the normalization switch 0.2.9 persisted but never showed.
            new(Tab.Playback, "Audio", "normalization", "Equalizer"),
            new(Tab.Playback, "Sound", "equalizer", "Equalizer"),
            new(Tab.Playback, "Sound", "crossfade", "Audio"),
            new(Tab.Playback, "Video", "videoQuality", "TvMonitor"),
            new(Tab.Playback, "Video", "videoMetered", "RadioTower"),
            new(Tab.Playback, "Video", "videoPrepareAhead", "Download"),
            new(Tab.Playback, "Video", "videoOverrides", "Edit"),
            new(Tab.Playback, "Player bar", "playerBarRemaining", "Clock"),

            new(Tab.Storage, "On this PC", "library", "Album"),
            new(Tab.Storage, "On this PC", "runtime", "Code"),
            new(Tab.Storage, "On this PC", "logs", "Document"),
            new(Tab.Storage, "On this PC", "localStore", "Folder"),
            new(Tab.Storage, "On this PC", "imageCache", "Picture"),
            new(Tab.Storage, "Playback cache", "cacheAudio", "Audio"),
            // "Document", not "Tag": License keys keeps Tag; the two would collide within this one section.
            new(Tab.Storage, "Playback cache", "cacheKeys", "Document"),
            new(Tab.Storage, "Playback cache", "budget", "RowSize"),
            new(Tab.Storage, "Playback cache", "cacheLocation", "FolderOpen"),
            new(Tab.Storage, "Playback cache", "audioBodies", "MusicNote"),
            new(Tab.Storage, "Playback cache", "licenseKeys", "Tag"),
            new(Tab.Storage, "Metadata cache", "metadataBudget", "RowSize"),
            new(Tab.Storage, "Metadata cache", "clearMetadata", "Delete"),
            // "Attention", not "Delete": the section owns Delete.
            new(Tab.Storage, "Reset", "factoryReset", "Attention"),
        ];

        public static string SectionGlyph(Tab tab, string title)
        {
            foreach (var s in Sections)
                if (s.Tab == tab && string.Equals(s.Title, title, StringComparison.Ordinal))
                    return s.Glyph;
            throw new InvalidOperationException("SettingsCatalog: no section '" + title + "' on tab " + tab + ".");
        }

        public static string RowGlyph(Tab tab, string rowId)
        {
            foreach (var r in Rows)
                if (r.Tab == tab && string.Equals(r.RowId, rowId, StringComparison.Ordinal))
                    return r.Glyph;
            throw new InvalidOperationException("SettingsCatalog: no row '" + rowId + "' on tab " + tab + ".");
        }
    }

    // ══ 3. THE EQUALIZER (0.2.9 `App/EqualizerSettings.cs` + the preset table of `SettingsPage.Playback.cs:20-29`) ══

    public static class Eq
    {
        public const int BandCount = 10;
        public const float MinGainDb = -12f;
        public const float MaxGainDb = 12f;
        /// <summary>Gains snap to half a decibel (ch 27 §0 N10).</summary>
        public const float SnapDb = 0.5f;

        public static readonly string[] PresetIds = ["flat", "bass", "treble", "vocal", "radio", "proof"];

        public static readonly float[][] PresetGains =
        [
            [0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
            [6, 5, 4, 2, 0, 0, 0, 0, 0, 0],
            [0, 0, 0, 0, 0, 1, 2, 3, 4, 5],
            [-2, -1, 0, 2, 4, 4, 2, 0, -1, -2],
            [0, 2, -2, 0, 0, 2, 4, 2, 2, 2],
            [12, -12, 12, -12, 12, -12, 12, -12, 12, -12],
        ];

        /// <summary>Parse the persisted comma-separated vector into <see cref="BandCount"/> bands clamped to ±12 dB. A
        /// missing store, a blank value, too few entries or a non-numeric entry default the band to 0 dB — garbage never
        /// throws.</summary>
        public static float[] ReadGains(IAppSettings? settings)
            => ParseGains(settings?.Get(Platform.Keys.EqualizerGains) ?? Platform.Keys.EqualizerGains.Default);

        public static float[] ParseGains(string? raw)
        {
            var gains = new float[BandCount];
            var parts = (raw ?? "").Split(',', StringSplitOptions.TrimEntries);
            for (int i = 0; i < gains.Length && i < parts.Length; i++)
                if (float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
                    gains[i] = Math.Clamp(v, MinGainDb, MaxGainDb);
            return gains;
        }

        /// <summary>The invariant, comma-separated form <see cref="ParseGains"/> reads; a short vector pads with 0 dB, a
        /// long one truncates.</summary>
        public static string SerializeGains(ReadOnlySpan<float> gains)
        {
            var sb = new StringBuilder(40);
            for (int i = 0; i < BandCount; i++)
            {
                if (i > 0) sb.Append(',');
                float gain = i < gains.Length ? Math.Clamp(gains[i], MinGainDb, MaxGainDb) : 0f;
                sb.Append(gain.ToString("0.#", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        /// <summary>A stored preset id → its combo index; blank or unknown → 0 (Flat), case-insensitive.</summary>
        public static int PresetIndex(string? id)
        {
            if (string.IsNullOrWhiteSpace(id)) return 0;
            for (int i = 0; i < PresetIds.Length; i++)
                if (string.Equals(id, PresetIds[i], StringComparison.OrdinalIgnoreCase)) return i;
            return 0;
        }

        /// <summary>One band edit: snap to <see cref="SnapDb"/> and clamp to ±12.</summary>
        public static float SnapGain(float db) => Math.Clamp(MathF.Round(db / SnapDb) * SnapDb, MinGainDb, MaxGainDb);
    }

    // ══ 4. THE PLAYBACK LADDERS ═════════════════════════════════════════════════════════════════════════════════════

    public static class Quality
    {
        /// <summary>The persisted rung of <c>playback.quality</c> that means Lossless.</summary>
        public const int LosslessRung = 3;

        /// <summary>How many audio rungs the combo offers. 0.2.9 offered three (Lossless was "an advert", ch 27 W7);
        /// decision D7 offers the fourth ONLY when a key deriver is installed — a lossless stream cannot play without one.</summary>
        public static int AudioRungCount(bool canDerive) => canDerive ? 4 : 3;

        /// <summary>The combo index for a stored rung: in range as offered, else the top offered rung (a stored Lossless
        /// on a build that cannot derive shows Very High, which is what actually plays — D7's 320 fallback).</summary>
        public static int AudioIndex(int stored, bool canDerive)
        {
            int count = AudioRungCount(canDerive);
            return stored < 0 ? 0 : stored >= count ? count - 1 : stored;
        }

        /// <summary>Whether a picked index may be written (the 0.2.9 explicit guards, widened by D7).</summary>
        public static bool IsWritableAudio(int index, bool canDerive) => index >= 0 && index < AudioRungCount(canDerive);

        /// <summary>The metered cap offers the SAME ladder as the streaming combo (flac plan §5.4: "the same four items",
        /// stored 0..3 like <c>Platform.Network.QualityMax</c>), gated by the same D7 deriver check.</summary>
        public static int MeteredRungCount(bool canDerive) => AudioRungCount(canDerive);

        public static readonly int[] VideoHeights = [0, 180, 240, 320, 480, 720, 1080];
        public static readonly int[] MeteredVideoHeights = [0, 480, 720, 1080];

        public static int VideoIndex(int height)
        {
            for (int i = 0; i < VideoHeights.Length; i++) if (VideoHeights[i] == height) return i;
            return 0;
        }

        /// <summary>A stored metered height that matches nothing reads as 480p (index 1), 0.2.9's default.</summary>
        public static int MeteredVideoIndex(int height)
        {
            for (int i = 0; i < MeteredVideoHeights.Length; i++) if (MeteredVideoHeights[i] == height) return i;
            return 1;
        }

        public const double CrossfadeMaxSeconds = 12.0;

        /// <summary>Both crossfade controls commit through this: <c>round(clamp(s, 0, 12) × 1000)</c> ms.</summary>
        public static int CrossfadeMs(double seconds)
            => (int)Math.Round(Math.Clamp(double.IsFinite(seconds) ? seconds : 0.0, 0.0, CrossfadeMaxSeconds) * 1000.0);

        public static double CrossfadeSeconds(int storedMs) => Math.Clamp(storedMs, 0, 12_000) / 1000.0;
    }

    // ══ 5. THE METERED STATUS LINE (0.2.9 `App/MeteredStatusLine.cs`, verbatim) ═════════════════════════════════════

    public enum MeteredStatusKind : byte { Unknown, NotMetered, Metered, MeteredWithinCap }

    /// <summary>The "On metered connections" card's description: a headline plus the plan-status suffixes NLM exposes.
    /// Every state says something — a silent card once hid a detector that only spoke when metered.</summary>
    public readonly record struct MeteredStatusLine(MeteredStatusKind Kind, bool OverDataLimit, bool Roaming)
    {
        public const string Separator = " · ";

        /// <param name="cost">The connection-cost snapshot.</param>
        /// <param name="capInEffect">The metered cap is lower than the chosen quality, so it actually bites.</param>
        public static MeteredStatusLine For(NetworkCost cost, bool capInEffect)
        {
            MeteredStatusKind kind = cost.Kind switch
            {
                NetworkCostKind.Fixed or NetworkCostKind.Variable => capInEffect ? MeteredStatusKind.Metered : MeteredStatusKind.MeteredWithinCap,
                NetworkCostKind.Unrestricted => MeteredStatusKind.NotMetered,
                _ => MeteredStatusKind.Unknown,
            };
            return new MeteredStatusLine(kind, cost.OverDataLimit, cost.Roaming);
        }

        public string HeadlineKey => Kind switch
        {
            MeteredStatusKind.Metered => Strings.Settings.Playback.MeteredStatus.Metered,
            MeteredStatusKind.MeteredWithinCap => Strings.Settings.Playback.MeteredStatus.MeteredWithinCap,
            MeteredStatusKind.NotMetered => Strings.Settings.Playback.MeteredStatus.NotMetered,
            _ => Strings.Settings.Playback.MeteredStatus.Unknown,
        };

        /// <summary>Headline, then the over-limit and roaming suffixes in that fixed order.</summary>
        public string Render(Func<string, string> loc)
        {
            ArgumentNullException.ThrowIfNull(loc);
            if (!OverDataLimit && !Roaming) return loc(HeadlineKey);
            var sb = new StringBuilder(64);
            sb.Append(loc(HeadlineKey));
            if (OverDataLimit) sb.Append(Separator).Append(loc(Strings.Settings.Playback.MeteredStatus.OverLimit));
            if (Roaming) sb.Append(Separator).Append(loc(Strings.Settings.Playback.MeteredStatus.Roaming));
            return sb.ToString();
        }
    }

    // ══ 6. STORAGE FORMATTING AND LADDERS (0.2.9 `SettingsPage.Storage.cs:27-28, 50-51, 206-212, 245-255`) ═════════

    /// <summary>The census of the data folder. Sizes are bytes; <see cref="LogFiles"/> counts every log file, the live
    /// one included.</summary>
    public sealed record StorageSnapshot(long LibraryDb, long Runtime, long Logs, int LogFiles, long Store,
        long AudioBody, long LicenseDb, long ImageCache, long Total);

    public enum StorageLoadPhase : byte { NotStarted, Loading, Ready, Failed }

    public static class StorageFormat
    {
        public static readonly string[] BodyBudgetLabels = ["1 GB", "2 GB", "4 GB", "8 GB", "16 GB", "32 GB", "64 GB", "128 GB", "256 GB", "512 GB", "1 TB", "Custom"];
        public static readonly long[] BodyBudgetBytes = [1L << 30, 2L << 30, 4L << 30, 8L << 30, 16L << 30, 32L << 30, 64L << 30, 128L << 30, 256L << 30, 512L << 30, 1L << 40];

        /// <summary>The "Custom" item of <see cref="BodyBudgetLabels"/>.</summary>
        public static int CustomBudgetIndex => BodyBudgetBytes.Length;

        public static readonly long[] MetaBudgetBytes = [32L << 20, 64L << 20, 128L << 20, 256L << 20];
        public static readonly string[] MetaBudgetLabels = ["32 MB", "64 MB", "128 MB", "256 MB"];

        /// <summary>Binary divisors, invariant culture: ≥ 1 GiB "0.0 GB", ≥ 1 MiB "0.0 MB", ≥ 1 KiB "0 KB", else "N B".</summary>
        public static string Bytes(long bytes) => bytes switch
        {
            >= 1024L * 1024 * 1024 => (bytes / (1024.0 * 1024 * 1024)).ToString("0.0", CultureInfo.InvariantCulture) + " GB",
            >= 1024L * 1024 => (bytes / (1024.0 * 1024)).ToString("0.0", CultureInfo.InvariantCulture) + " MB",
            >= 1024L => (bytes / 1024.0).ToString("0", CultureInfo.InvariantCulture) + " KB",
            _ => bytes.ToString(CultureInfo.InvariantCulture) + " B",
        };

        /// <summary>The nearest preset to a stored byte budget (ties keep the earlier rung).</summary>
        public static int BodyBudgetIndex(long bytes)
        {
            int best = 5;
            long bestDiff = long.MaxValue;
            for (int i = 0; i < BodyBudgetBytes.Length; i++)
            {
                long diff = Math.Abs(BodyBudgetBytes[i] - bytes);
                if (diff < bestDiff) { bestDiff = diff; best = i; }
            }
            return best;
        }

        /// <summary>An exact preset match, else <see cref="CustomBudgetIndex"/> (typing a value snaps the combo to Custom).</summary>
        public static int BodyBudgetExactIndex(long bytes)
        {
            for (int i = 0; i < BodyBudgetBytes.Length; i++) if (BodyBudgetBytes[i] == bytes) return i;
            return CustomBudgetIndex;
        }

        /// <summary>A stored metadata budget → its combo index; an unmatched value reads as 64 MB (index 1).</summary>
        public static int MetaBudgetIndex(long bytes)
        {
            for (int i = 0; i < MetaBudgetBytes.Length; i++) if (MetaBudgetBytes[i] == bytes) return i;
            return 1;
        }

        /// <summary>A category's share of the total, rounded half away from zero; 0 when either side is ≤ 0.</summary>
        public static int Percent(long part, long total)
            => total <= 0 || part <= 0 ? 0 : (int)Math.Round(part * 100.0 / total, MidpointRounding.AwayFromZero);

        /// <summary>A GiB NumberBox value → bytes, floored at 1/16 GiB (the box's minimum).</summary>
        public static long GiBToBytes(double gib)
            => (long)(Math.Max(double.IsFinite(gib) ? gib : 0.0625, 0.0625) * (1L << 30));
    }

    // ══ 7. THE NOTIFICATIONS TAB'S OS-BLOCK BANNER (0.2.9 `SettingsPage.Notifications.cs:242-263`, now tested) ═════

    public enum BlockedBanner : byte { None, App, User, Policy }

    public static class NotifyRules
    {
        /// <summary>Which OS-block banner to show. <c>Enabled</c> and an unreadable (<c>Unknown</c>) notifier show NONE —
        /// an unregistered notifier is not evidence of a problem.</summary>
        public static BlockedBanner Blocked(ToastDeliverySetting setting) => setting switch
        {
            ToastDeliverySetting.DisabledForApplication => BlockedBanner.App,
            ToastDeliverySetting.DisabledForUser => BlockedBanner.User,
            ToastDeliverySetting.DisabledByGroupPolicy => BlockedBanner.Policy,
            _ => BlockedBanner.None,
        };

        /// <summary>Group policy is not something the user can click their way out of: no "Open Windows settings".</summary>
        public static bool OffersOpenSettings(BlockedBanner banner) => banner is BlockedBanner.App or BlockedBanner.User;

        /// <summary>A simulated outcome's clock, "HH:mm" local, or the literal "--:--" when the pipeline gave no time.</summary>
        public static string Clock(DateTimeOffset? at, CultureInfo culture)
            => at?.ToLocalTime().ToString("HH:mm", culture) ?? "--:--";

        /// <summary>00:00 … 23:00 from the invariant clock: the value IS the hour index.</summary>
        public static string[] HourLabels()
        {
            var labels = new string[24];
            for (int h = 0; h < 24; h++) labels[h] = h.ToString("00", CultureInfo.InvariantCulture) + ":00";
            return labels;
        }

        /// <summary>The toast sentence's loc key for a "Send a test event" outcome (ch 27 W14). The two clocked outcomes
        /// (<see cref="TestEventOutcome.BannerQuietDeferred"/>, <see cref="TestEventOutcome.Scheduled"/>) carry a
        /// <c>{0}</c> the caller fills with <see cref="Clock"/>; <see cref="TestEventOutcome.Unavailable"/> is the
        /// no-simulator answer.</summary>
        public static string OutcomeKey(TestEventOutcome outcome) => outcome switch
        {
            TestEventOutcome.Dropped => Strings.Settings.Notify.OutcomeDropped,
            TestEventOutcome.RecordedInApp => Strings.Settings.Notify.OutcomeInApp,
            TestEventOutcome.Banner => Strings.Settings.Notify.OutcomeBanner,
            TestEventOutcome.NeverBanners => Strings.Settings.Notify.OutcomeNeverBanners,
            TestEventOutcome.BannerQuietDeferred => "settings.notify.outcomeQuiet",
            TestEventOutcome.Scheduled => "settings.notify.outcomeScheduled",
            _ => Strings.Settings.Notify.OutcomeUnavailable,
        };

        /// <summary>Whether the outcome's sentence carries the release clock.</summary>
        public static bool OutcomeHasClock(TestEventOutcome outcome)
            => outcome is TestEventOutcome.BannerQuietDeferred or TestEventOutcome.Scheduled;

        /// <summary>Dropped and a quiet-deferred banner are Informational, the unavailable answer a Warning, every
        /// delivered outcome a Success (0.2.9 `Describe`). 0 Informational · 1 Success · 2 Warning.</summary>
        public static int OutcomeSeverity(TestEventOutcome outcome) => outcome switch
        {
            TestEventOutcome.Dropped or TestEventOutcome.BannerQuietDeferred => 0,
            TestEventOutcome.Unavailable => 2,
            _ => 1,
        };
    }

    /// <summary>What the notification pipeline did with a simulated event (0.2.9 `NotificationSimulator.SimOutcome`).</summary>
    public enum TestEventOutcome : byte { Unavailable, Dropped, RecordedInApp, Banner, NeverBanners, BannerQuietDeferred, Scheduled }

    /// <summary>A simulated event's result; <see cref="At"/> is the release time for the two clocked outcomes.</summary>
    public readonly record struct TestEventResult(TestEventOutcome Outcome, DateTimeOffset? At);

    // ══ 8. APP LANGUAGE (0.2.9 `SettingsPage.General.cs:32-51`) ════════════════════════════════════════════════════

    public static class Language
    {
        public static readonly string[] Codes = ["system", "en-US", "nl", "ko-KR"];

        /// <summary>nl / ko-KR are shown but DISABLED until their tables are complete (ch 27 parity 77).</summary>
        public static readonly bool[] Enabled = [true, true, false, false];

        public static int IndexOf(string? culture)
        {
            for (int i = 0; i < Codes.Length; i++)
                if (string.Equals(Codes[i], culture, StringComparison.OrdinalIgnoreCase)) return i;
            return 0;
        }

        /// <summary>The belt-and-braces reject behind the combo's own disabled items.</summary>
        public static bool CanPick(int index) => (uint)index < (uint)Codes.Length && Enabled[index];
    }

    // ══ 9. THE ABOUT RECEIPTS' FORMATTING (0.2.9 `SettingsPage.About.cs:265-274, 362-392`, now tested) ═════════════

    public enum GpuTier : byte { Unknown, Weak, Strong }

    public static class Receipts
    {
        public const string Dash = "—";

        /// <summary>Megabytes with one decimal, invariant.</summary>
        public static string Mb(long bytes) => (bytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB";

        /// <summary>"Nd Nh" · "Nh Nm" · "Nm Ns" · "Ns".</summary>
        public static string Uptime(TimeSpan t)
        {
            var c = CultureInfo.InvariantCulture;
            if (t.TotalDays >= 1) return ((int)t.TotalDays).ToString(c) + "d " + t.Hours.ToString(c) + "h";
            if (t.TotalHours >= 1) return ((int)t.TotalHours).ToString(c) + "h " + t.Minutes.ToString(c) + "m";
            if (t.TotalMinutes >= 1) return ((int)t.TotalMinutes).ToString(c) + "m " + t.Seconds.ToString(c) + "s";
            return Math.Max(0, (int)t.TotalSeconds).ToString(c) + "s";
        }

        /// <summary>The adapter line: "name  (Tier)" or "name  (Tier · software)"; a blank name reads as a dash.</summary>
        public static string GpuLine(string? adapterName, GpuTier tier, bool software)
        {
            string name = adapterName is { Length: > 0 } n ? n : Dash;
            string t = tier switch { GpuTier.Weak => "Weak", GpuTier.Strong => "Strong", _ => "Unknown" };
            return software ? name + "  (" + t + " · software)" : name + "  (" + t + ")";
        }

        /// <summary>Shared/iGPU vs discrete: a weak GPU is shared, a strong one discrete; unknown → the DXGI heuristic
        /// (the non-local segment carries the bulk of the usage ⇒ shared).</summary>
        public static bool IsSharedIgpu(GpuTier tier, ulong localUsage, ulong nonLocalUsage)
        {
            if (tier == GpuTier.Weak) return true;
            if (tier == GpuTier.Strong) return false;
            return nonLocalUsage >= localUsage && nonLocalUsage > 0;
        }

        /// <summary>App memory excluding GPU assets ≈ working set − the segment that overlaps system memory, floored at 0.</summary>
        public static long AppExclusive(long workingSet, bool sharedIgpu, ulong localUsage, ulong nonLocalUsage)
        {
            long excl = workingSet - (long)(sharedIgpu ? localUsage : nonLocalUsage);
            return excl < 0 ? 0 : excl;
        }

        public static string Fps(double fps) => fps > 0 ? fps.ToString("0.0", CultureInfo.InvariantCulture) : Dash;
    }

    // ══ 10. THE STORAGE AND ABOUT DECISIONS (0.2.9 `SettingsPage.Storage.cs` / `.About.cs`; pinned by SettingsStorageTests) ═

    public static class StorageRules
    {
        /// <summary>The usage bar's seven categories, in the ONE order the seven hues are indexed by (ch 27 §4.1):
        /// library · runtime · logs · local store · audio bodies · license keys · image cache.</summary>
        public static long[] Parts(StorageSnapshot s)
            => [s.LibraryDb, s.Runtime, s.Logs, s.Store, s.AudioBody, s.LicenseDb, s.ImageCache];

        /// <summary>W16's zero-total rule: a category at ≤ 0 bytes leaves the bar and the legend (its row stays); when
        /// EVERY category is zero there is no bar at all — not an empty track.</summary>
        public static bool ShowsBar(StorageSnapshot s)
        {
            foreach (long bytes in Parts(s)) if (bytes > 0) return true;
            return false;
        }

        /// <summary>W17's 300-DIP meter: the used share of a budget (0 when unlimited or unknown), clamped to [0, 1], and
        /// the Error state whenever the cache holds more than the budget (0.2.9 `BudgetControl`, verbatim).</summary>
        public static (float Fraction, bool Over) BudgetMeter(long usedBytes, long? budgetBytes)
        {
            float fraction = budgetBytes is > 0 ? Math.Clamp((float)(usedBytes / (double)budgetBytes.Value), 0f, 1f) : 0f;
            return (fraction, budgetBytes is { } b && usedBytes > b);
        }
    }

    public static class AboutRules
    {
        public enum HeroTone : byte { Standard, Accent, Disabled }

        /// <summary>What the hero's primary button does: nothing (disabled), a user feed check, or an update verb.</summary>
        public enum HeroVerb : byte { None, Check, UpdateNow, Retry }

        /// <summary>One row of W20's state matrix: the pill (loc key + accent) and the primary button (loc key, tone, verb).</summary>
        public readonly record struct HeroShape(string PillKey, bool PillAccent, string ButtonKey, HeroTone Tone, HeroVerb Verb);

        /// <summary>W20 — every state has a pill AND a button, never a blank card. A dev build that is not walking a
        /// simulation is inert; a Store build names what it cannot know ("not checked yet", the provenance line's own copy,
        /// so the two cannot contradict) and opens the Store listing. Both branches come BEFORE the state switch.</summary>
        public static HeroShape Hero(AppUpdateState state, bool inert, bool isStore)
        {
            if (inert) return new(Strings.Settings.About.DevBuild, false, Strings.Update.Action.Check, HeroTone.Disabled, HeroVerb.None);
            if (isStore) return new(Strings.Update.About.NeverChecked, false, Strings.Update.Store.Open, HeroTone.Standard, HeroVerb.UpdateNow);
            return state switch
            {
                AppUpdateState.Checking => new(Strings.Update.About.PillChecking, false, Strings.Update.State.Checking, HeroTone.Disabled, HeroVerb.None),
                AppUpdateState.Available => new(Strings.Update.About.PillAvailable, true, Strings.Update.Action.UpdateNow, HeroTone.Accent, HeroVerb.UpdateNow),
                AppUpdateState.Snoozed => new(Strings.Update.About.PillSnoozed, false, Strings.Update.Action.UpdateNow, HeroTone.Accent, HeroVerb.UpdateNow),
                AppUpdateState.Downloading => new(Strings.Update.About.PillDownloading, true, Strings.Update.State.Installing, HeroTone.Disabled, HeroVerb.None),
                AppUpdateState.Installing => new(Strings.Update.About.PillInstalling, true, Strings.Update.State.Installing, HeroTone.Disabled, HeroVerb.None),
                AppUpdateState.Completed => new(Strings.Update.About.PillUpdated, false, Strings.Update.Action.Check, HeroTone.Standard, HeroVerb.Check),
                AppUpdateState.Failed => new(Strings.Update.About.PillFailed, false, Strings.Update.Action.Retry, HeroTone.Accent, HeroVerb.Retry),
                _ => new(Strings.Update.About.PillUpToDate, false, Strings.Update.Action.Check, HeroTone.Standard, HeroVerb.Check),
            };
        }

        /// <summary>W19's provenance line, three shapes: "Built D  ·  last checked T", "Built D  ·  not checked yet", or —
        /// on an unstamped build — the check phrase alone, never a leading separator.</summary>
        public static string Provenance(string? buildDate, long lastCheckedMs, Func<string, string> built,
                                        Func<string, string> lastChecked, string neverChecked)
        {
            string check = lastCheckedMs > 0 ? lastChecked(Stamp(lastCheckedMs)) : neverChecked;
            return buildDate is { Length: > 0 } date ? built(date) + "  ·  " + check : check;
        }

        /// <summary>"Last checked" as a short local stamp, "g" in the invariant culture (the app runs invariant-globalized).</summary>
        public static string Stamp(long unixMs)
            => DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToLocalTime().ToString("g", CultureInfo.InvariantCulture);
    }

    /// <summary>The library.db cache-tier census 0.2.9 read from `CachedStore.GetCacheStats()` (ch 27 G3). The budget
    /// governs the EVICTABLE slice only, so the sentence shows it against that, never against the gross cache bytes.</summary>
    public readonly record struct MetadataCacheSnapshot(long DbBytes, long CacheBytes, long PinnedBytes, int EntityRows,
        int PinnedRows, int OverviewRows, int ExtensionRows, long EvictableBytes, long BudgetBytes);
}
