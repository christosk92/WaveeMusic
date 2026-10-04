// ── AiLyrics/AiLyrics.Rules.cs ───────────────────────────────────────────────────────────────────────────────────────
// Availability, SetupPhase, SetupError, PrepareProgress, Status, TrackPhase, SkipReason, TrackStatus, HeaderState, Rules
//
// Role: CORE (pure: no I/O, no clock reads, no engine)
//
// Every decision of the on-device AI lyrics feature (plan §2.1). The host, the Settings card and the lyrics surfaces
// only execute what these functions answer; each one is unit-tested in Wavee.Tests/AiLyricsRulesTests.cs. Loc keys
// are returned as plain strings (plan §5) so the decision stays testable without the loc pack.

using FluentGpu.WindowsApi.Devices;

namespace Wavee;

public static partial class AiLyrics
{
    /// <summary>Whether this PC can run the feature at all. Decided once at boot from the process architecture, the
    /// OS build and the DXCore NPU list.</summary>
    public enum Availability : byte { Available, NotArm64, OsTooOld, NoNpu, NpuNotSupported }

    /// <summary>The setup lifecycle the Settings card renders. One value, written only on the UI thread.</summary>
    public enum SetupPhase : byte { Unavailable, Off, NeedsSetup, Downloading, Paused, Preparing, Ready, Error }

    /// <summary>Why setup failed; each kind maps to one message and one recovery (<see cref="Rules.ErrorKey"/>).</summary>
    public enum SetupError { None, Offline, Network, NotFound, HashMismatch, DiskFull, RuntimeLoad, NoNpuDevice, Compile, Cancelled, Unknown }

    /// <summary>First-run NPU compile progress. <see cref="WeightDone"/>/<see cref="WeightTotal"/> weigh each graph by
    /// <see cref="Rules.PrepareWeights"/>; <see cref="Recompile"/> = a stale cache is being rebuilt (driver/runtime
    /// changed); <see cref="Finishing"/> = cancel was asked and the current graph is still compiling.</summary>
    public readonly record struct PrepareProgress(int Done, int Total, long WeightDone, long WeightTotal, bool Recompile, bool Finishing);

    /// <summary>Everything the card and the header need, published WHOLE (torn reads impossible). UI thread only.</summary>
    /// <param name="NpuName">The NPU's driver description ("Qualcomm(R) Hexagon(TM) NPU").</param>
    /// <param name="NpuDriver">Its driver version ("31.0.210.5").</param>
    /// <param name="Languages">The installed aligner languages (ISO 639-1).</param>
    /// <param name="PendingDownloadBytes">What <see cref="SetupPhase.NeedsSetup"/> would fetch.</param>
    public readonly record struct Status(
        SetupPhase Phase, Availability Availability, bool Enabled,
        string NpuName, string NpuDriver,
        DownloadProgress Download, PrepareProgress Prepare,
        SetupError Error, string ErrorDetail,
        long InstalledBytes, IReadOnlyList<string> Languages,
        long PendingDownloadBytes)
    {
        /// <summary>Before the boot probe answered: nothing is clickable (<see cref="SetupPhase.Unavailable"/>) and the
        /// card can test <c>== Status.Unknown</c> to show no reason yet.</summary>
        public static readonly Status Unknown = new(
            SetupPhase.Unavailable, AiLyrics.Availability.NoNpu, false, "", "",
            new DownloadProgress(DownloadPhase.Checking, 0, 0, 0, ""), default,
            SetupError.None, "", 0, Array.Empty<string>(), 0);
    }

    /// <summary>The current track's AI state, for the header button, the footer strip and the inspector.</summary>
    public enum TrackPhase : byte { Idle, Waiting, Working, Done, Skipped }

    /// <summary>Why the current track is not being timed (<see cref="Rules.Eligibility"/>), or why its job stopped.</summary>
    public enum SkipReason : byte { None, NoLyrics, AlreadyWordByWord, LanguageNotInstalled, PlainTextOff, WordSyncOff, Podcast, NotSpotifyAudio, TooLong, BatterySaver, AudioUnavailable, Failed, NeedsSetup }

    public readonly record struct TrackStatus(string TrackId, TrackPhase Phase, SkipReason Reason, string Language,
        int LinesReady, int LineCount, double ProcessedSeconds, bool FromCache);

    /// <summary>The lyrics rail's sparkle button: whether it shows, whether it is lit, its tooltip key and whether a
    /// click opens Settings (only when Settings can fix what the tooltip says).</summary>
    public readonly record struct HeaderState(bool Visible, bool Active, string TipKey, bool OpensSettings);

    public static partial class Rules
    {
        /// <summary>Windows 11 24H2: the QNN execution provider and DXCore's workload filter need it.</summary>
        public const int MinOsBuild = 26100;

        /// <summary>A download above this asks first on a metered connection.</summary>
        public const long MeteredConfirmBytes = 50L * 1024 * 1024;

        /// <summary>Longer songs are not timed (the job's memory and NPU time grow with the length).</summary>
        public const long MaxDurationMs = 20L * 60 * 1000;

        /// <summary>Models are unloaded (NPU contexts freed) after this long without a job. Reload costs ~1.5 s.</summary>
        public const long IdleUnloadMs = 10L * 60 * 1000;

        /// <summary>The floor of one graph's prepare weight: even a tiny graph costs seconds to compile.</summary>
        public const long PrepareWeightFloor = 16L * 1024 * 1024;

        // Every loc key below is a WHOLE literal: the loc analyzer (FLLOC005) counts a key as used only when a string
        // literal equals it, so a prefix + suffix concatenation would read as an unreferenced key.

        /// <summary>Bytes in the app's one format ("1.2 GB").</summary>
        public static string FormatBytes(long bytes) => Settings.StorageFormat.Bytes(bytes);

        // ── availability and setup ───────────────────────────────────────────────────────────────────────────────────

        /// <summary>NotArm64 → OsTooOld (build &lt; <see cref="MinOsBuild"/>) → NoNpu (no hardware NPU) →
        /// NpuNotSupported (no Qualcomm NPU) → Available.</summary>
        public static Availability Availability(bool arm64, int osBuild, IReadOnlyList<ComputeAdapterInfo> npus)
        {
            if (!arm64) return AiLyrics.Availability.NotArm64;
            if (osBuild < MinOsBuild) return AiLyrics.Availability.OsTooOld;
            bool any = false;
            for (int i = 0; i < npus.Count; i++)
            {
                if (!npus[i].IsHardware) continue;
                any = true;
                if (npus[i].Vendor == AdapterVendor.Qualcomm) return AiLyrics.Availability.Available;
            }
            return any ? AiLyrics.Availability.NpuNotSupported : AiLyrics.Availability.NoNpu;
        }

        /// <summary>The NPU the feature runs on (the first hardware Qualcomm NPU), or null.</summary>
        public static ComputeAdapterInfo? SupportedNpu(IReadOnlyList<ComputeAdapterInfo> npus)
        {
            for (int i = 0; i < npus.Count; i++)
                if (npus[i].IsHardware && npus[i].Vendor == AdapterVendor.Qualcomm) return npus[i];
            return null;
        }

        /// <summary>The Settings card's description when the feature cannot run (plan §5 <c>unavailable.*</c>);
        /// <see cref="AiLyrics.Availability.Available"/> answers the card's ordinary <c>sub</c>.</summary>
        public static string UnavailableKey(Availability a) => a switch
        {
            AiLyrics.Availability.NotArm64 => "settings.lyrics.ai.unavailable.arm64",
            AiLyrics.Availability.OsTooOld => "settings.lyrics.ai.unavailable.os",
            AiLyrics.Availability.NoNpu => "settings.lyrics.ai.unavailable.noNpu",
            AiLyrics.Availability.NpuNotSupported => "settings.lyrics.ai.unavailable.npuVendor",
            _ => "settings.lyrics.ai.sub",
        };

        /// <summary>The phase at boot: Unavailable → Off (switch off) → NeedsSetup (files missing) → Ready.</summary>
        public static SetupPhase InitialPhase(Availability availability, bool enabled, bool installedComplete)
        {
            if (availability != AiLyrics.Availability.Available) return SetupPhase.Unavailable;
            if (!enabled) return SetupPhase.Off;
            return installedComplete ? SetupPhase.Ready : SetupPhase.NeedsSetup;
        }

        /// <summary>The master switch can be turned on only on a PC that can run the feature.</summary>
        public static bool CanToggleOn(Availability availability) => availability == AiLyrics.Availability.Available;

        /// <summary>A metered connection asks before a download larger than <see cref="MeteredConfirmBytes"/>.</summary>
        public static bool NeedsMeteredConfirm(bool metered, long bytes) => metered && bytes > MeteredConfirmBytes;

        // ── per track ────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Whether the current track gets timed, in this order: NeedsSetup → Podcast → NotSpotifyAudio →
        /// NoLyrics → AlreadyWordByWord → PlainTextOff / WordSyncOff → LanguageNotInstalled → TooLong → BatterySaver.
        /// <see cref="SkipReason.None"/> = start the job.</summary>
        /// <param name="durationMs">The track's length; 0 or less = unknown (not refused).</param>
        /// <param name="languages">The installed aligner languages.</param>
        /// <param name="keepOnSaver">The "keep running on battery saver" toggle.</param>
        public static SkipReason Eligibility(Lyrics.Doc? doc, EntityKind kind, bool spotifyUri, long durationMs,
            bool wordSync, bool plainText, IReadOnlyList<string> languages, bool energySaver, bool keepOnSaver, SetupPhase phase)
        {
            if (phase != SetupPhase.Ready) return SkipReason.NeedsSetup;
            if (IsPodcast(kind)) return SkipReason.Podcast;
            if (!spotifyUri) return SkipReason.NotSpotifyAudio;
            if (doc is null || doc.Sync == Lyrics.SyncKind.None || !HasText(doc.Lines)) return SkipReason.NoLyrics;
            if (HasWordTiming(doc.Lines)) return SkipReason.AlreadyWordByWord;
            bool plain = doc.Sync == Lyrics.SyncKind.Unsynced || !doc.IsSynced;
            if (plain ? !plainText : !wordSync) return plain ? SkipReason.PlainTextOff : SkipReason.WordSyncOff;
            if (!Contains(languages, LanguageOf(doc))) return SkipReason.LanguageNotInstalled;
            if (durationMs > MaxDurationMs) return SkipReason.TooLong;
            if (energySaver && !keepOnSaver) return SkipReason.BatterySaver;
            return SkipReason.None;
        }

        static bool IsPodcast(EntityKind kind) => kind is EntityKind.Episode or EntityKind.Show;

        static bool HasText(IReadOnlyList<Lyrics.Line> lines)
        {
            for (int i = 0; i < lines.Count; i++) if (!string.IsNullOrWhiteSpace(lines[i].Text)) return true;
            return false;
        }

        static bool HasWordTiming(IReadOnlyList<Lyrics.Line> lines)
        {
            for (int i = 0; i < lines.Count; i++) if (lines[i].IsWordByWord && lines[i].Syllables.Count > 0) return true;
            return false;
        }

        static bool Contains(IReadOnlyList<string> list, string value)
        {
            for (int i = 0; i < list.Count; i++) if (string.Equals(list[i], value, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>The aligner language for a document: the source's own tag (primary subtag, lower case) when it names
        /// one, else a heuristic between the two aligners: Spanish when the text has ñ/¿/¡ or Spanish function words
        /// make up a real share of it, else "en". (Portuguese also reads as Spanish here; only en/es aligners exist.)</summary>
        public static string LanguageOf(Lyrics.Doc doc)
        {
            string? tag = doc.Language;
            if (!string.IsNullOrWhiteSpace(tag))
            {
                int cut = tag.IndexOfAny(['-', '_']);
                string primary = (cut > 0 ? tag[..cut] : tag).Trim().ToLowerInvariant();
                if (primary.Length > 0 && primary != "und" && primary != "zxx") return primary;
            }
            return LooksSpanish(doc.Lines) ? "es" : "en";
        }

        static readonly HashSet<string> SpanishWords = new(StringComparer.Ordinal)
        {
            "el", "la", "los", "las", "que", "y", "un", "una", "es", "de", "en", "del", "al", "por", "con", "para", "mi",
            "tu", "yo", "se", "lo", "te", "como", "pero", "más", "mas", "muy", "porque", "estoy", "eres", "soy", "está",
            "esta", "todo", "nada", "cuando", "sin", "quiero", "tengo", "contigo", "amor", "corazón",
        };

        static bool LooksSpanish(IReadOnlyList<Lyrics.Line> lines)
        {
            int tokens = 0, hits = 0;
            var distinct = new HashSet<string>(StringComparer.Ordinal);
            for (int li = 0; li < lines.Count; li++)
            {
                string text = lines[li].Text ?? "";
                int start = -1;
                for (int i = 0; i <= text.Length; i++)
                {
                    char c = i < text.Length ? text[i] : ' ';
                    if (c is 'ñ' or 'Ñ' or '¿' or '¡') return true;
                    if (char.IsLetter(c)) { if (start < 0) start = i; continue; }
                    if (start < 0) continue;
                    string word = text[start..i].ToLowerInvariant();
                    start = -1;
                    tokens++;
                    if (SpanishWords.Contains(word)) { hits++; distinct.Add(word); }
                }
            }
            return hits >= 3 && distinct.Count >= 2 && hits * 8 >= tokens;
        }

        /// <summary>The results cache's file-name-safe key for a track: its id with every character outside
        /// [A-Za-z0-9_-] replaced by '_', plus the pack version (a new pack never reads an old result).</summary>
        public static string ResultsKey(string trackId, int packVersion)
        {
            var chars = trackId.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
                if (!(char.IsAsciiLetterOrDigit(chars[i]) || chars[i] is '_' or '-')) chars[i] = '_';
            return new string(chars) + ".v" + packVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>FNV-1a (64-bit) over the line texts, each prefixed by its length (so ["ab","c"] ≠ ["a","bc"]).
        /// Timing is ignored: it protects a cached result against a provider doc whose WORDS changed.</summary>
        public static ulong SourceHash(IReadOnlyList<Lyrics.Line> lines)
        {
            const ulong Offset = 14695981039346656037UL, Prime = 1099511628211UL;
            ulong h = Offset;
            for (int li = 0; li < lines.Count; li++)
            {
                string text = lines[li].Text ?? "";
                int n = text.Length;
                for (int b = 0; b < 4; b++) { h ^= (byte)(n >> (8 * b)); h *= Prime; }
                for (int i = 0; i < n; i++)
                {
                    char c = text[i];
                    h ^= (byte)c; h *= Prime;
                    h ^= (byte)(c >> 8); h *= Prime;
                }
            }
            return h;
        }

        // ── progress and ETA ─────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Seconds left at the current speed; null while the speed or the total is unknown.</summary>
        public static double? EtaSeconds(long done, long total, double bytesPerSecond)
        {
            if (total <= 0 || done < 0 || !(bytesPerSecond > 0) || double.IsInfinity(bytesPerSecond)) return null;
            return Math.Max(0, total - done) / bytesPerSecond;
        }

        /// <summary>The ETA's loc key and its <paramref name="n"/>: whole seconds under a minute, whole minutes under an
        /// hour (rounded up), else rounded hours ("about n h"); unknown when null.</summary>
        public static string EtaKey(double? seconds, out int n)
        {
            if (seconds is not double s || double.IsNaN(s) || double.IsInfinity(s) || s < 0)
            {
                n = 0;
                return "settings.lyrics.ai.eta.unknown";
            }
            double secs = Math.Max(1, Math.Ceiling(s));
            if (secs < 60) { n = (int)secs; return "settings.lyrics.ai.eta.seconds"; }
            double mins = Math.Ceiling(s / 60);
            if (mins < 60) { n = (int)mins; return "settings.lyrics.ai.eta.minutes"; }
            n = (int)Math.Min(int.MaxValue, Math.Max(1, Math.Round(s / 3600, MidpointRounding.AwayFromZero)));
            return "settings.lyrics.ai.eta.hours";
        }

        /// <summary>One compile weight per graph: its size, floored at <see cref="PrepareWeightFloor"/> (the separator
        /// and the transformer halves dominate, but a 17 KB conv still costs ~3 s).</summary>
        public static long[] PrepareWeights(IReadOnlyList<string> graphs, Func<string, long> bytesOf)
        {
            var w = new long[graphs.Count];
            for (int i = 0; i < w.Length; i++) w[i] = Math.Max(bytesOf(graphs[i]), PrepareWeightFloor);
            return w;
        }

        /// <summary>The prepare bar's fill in [0, 1]: by weight when known, else by graph count.</summary>
        public static double PrepareFraction(long weightDone, long weightTotal, int done, int total)
        {
            if (weightTotal > 0) return Math.Clamp((double)weightDone / weightTotal, 0, 1);
            if (total > 0) return Math.Clamp((double)done / total, 0, 1);
            return 0;
        }

        /// <summary>Download speed as an exponentially weighted moving average over ~3 s. <see cref="Sample"/> answers
        /// true at most every 100 ms: the caller reports progress only then.</summary>
        public sealed class SpeedMeter
        {
            long _lastBytes = -1, _lastMs, _reportMs = long.MinValue;
            public double BytesPerSecond { get; private set; }

            public bool Sample(long doneBytes, long nowMs)
            {
                if (_lastBytes < 0) { _lastBytes = doneBytes; _lastMs = nowMs; _reportMs = nowMs; return true; }
                long dt = nowMs - _lastMs;
                if (dt >= 250)
                {
                    double inst = (doneBytes - _lastBytes) * 1000.0 / dt;
                    double alpha = 1 - Math.Exp(-dt / 3000.0);
                    BytesPerSecond = BytesPerSecond <= 0 ? inst : BytesPerSecond + alpha * (inst - BytesPerSecond);
                    _lastBytes = doneBytes; _lastMs = nowMs;
                }
                if (nowMs - _reportMs < 100) return false;
                _reportMs = nowMs;
                return true;
            }
        }

        // ── errors ───────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The InfoBar title for a setup error (plan §5 <c>error.*</c>). None/Cancelled are never shown; they
        /// answer the generic key so a caller can never look up an empty key.</summary>
        public static string ErrorKey(SetupError error) => error switch
        {
            SetupError.Offline => "settings.lyrics.ai.error.offline",
            SetupError.Network => "settings.lyrics.ai.error.network",
            SetupError.NotFound => "settings.lyrics.ai.error.notFound",
            SetupError.HashMismatch => "settings.lyrics.ai.error.hash",
            SetupError.DiskFull => "settings.lyrics.ai.error.diskFull",
            SetupError.RuntimeLoad => "settings.lyrics.ai.error.runtime",
            SetupError.NoNpuDevice => "settings.lyrics.ai.error.noNpuDevice",
            SetupError.Compile => "settings.lyrics.ai.error.compile",
            _ => "settings.lyrics.ai.error.unknown",
        };

        /// <summary>Every real error offers "Try again" (Network resumes from the partial); Cancelled goes back to
        /// NeedsSetup silently.</summary>
        public static bool ErrorOffersRetry(SetupError error) => error is not (SetupError.None or SetupError.Cancelled);

        /// <summary>A runtime that will not start or models that will not compile also offer "Remove and download again".</summary>
        public static bool ErrorOffersRemove(SetupError error) => error is SetupError.RuntimeLoad or SetupError.Compile;

        // ── surfaces ─────────────────────────────────────────────────────────────────────────────────────────────────

        static readonly HeaderState Hidden = new(false, false, "", false);

        /// <summary>The rail's sparkle button. Hidden while the feature is off or cannot run; podcasts say so; before
        /// Ready it asks to finish setup (click opens Settings); when Ready it reflects the track (lit when Done).</summary>
        public static HeaderState Header(Status status, TrackStatus track, EntityKind kind)
        {
            if (!status.Enabled || status.Phase is SetupPhase.Unavailable or SetupPhase.Off) return Hidden;
            if (IsPodcast(kind)) return new(true, false, "lyrics.ai.header.skipped.podcast", false);
            if (status.Phase != SetupPhase.Ready) return new(true, false, "lyrics.ai.header.setup", true);
            return track.Phase switch
            {
                TrackPhase.Working => new(true, false, "lyrics.ai.header.working", false),
                TrackPhase.Done => new(true, true, "lyrics.ai.header.on", false),
                TrackPhase.Skipped => Skipped(track.Reason),
                _ => new(true, false, "lyrics.ai.header.waiting", false),
            };
        }

        static HeaderState Skipped(SkipReason reason) => reason switch
        {
            SkipReason.NoLyrics => new(true, false, "lyrics.ai.header.skipped.noLyrics", false),
            SkipReason.AlreadyWordByWord => new(true, false, "lyrics.ai.header.skipped.alreadyWordByWord", false),
            SkipReason.LanguageNotInstalled => new(true, false, "lyrics.ai.header.skipped.language", true),
            SkipReason.PlainTextOff => new(true, false, "lyrics.ai.header.skipped.plainOff", true),
            SkipReason.WordSyncOff => new(true, false, "lyrics.ai.header.skipped.wordSyncOff", true),
            SkipReason.Podcast => new(true, false, "lyrics.ai.header.skipped.podcast", false),
            SkipReason.NotSpotifyAudio or SkipReason.AudioUnavailable => new(true, false, "lyrics.ai.header.skipped.audio", false),
            SkipReason.TooLong => new(true, false, "lyrics.ai.header.skipped.tooLong", false),
            SkipReason.BatterySaver => new(true, false, "lyrics.ai.header.skipped.batterySaver", true),
            SkipReason.Failed => new(true, false, "lyrics.ai.header.skipped.failed", false),
            SkipReason.NeedsSetup => new(true, false, "lyrics.ai.header.setup", true),
            _ => new(true, false, "lyrics.ai.header.waiting", false),
        };

        /// <summary>The end-of-lyrics disclosure shows while a job runs and once its timing is on screen.</summary>
        public static bool ShowsFooter(TrackStatus track) => track.Phase is TrackPhase.Working or TrackPhase.Done;

        public static string FooterKey(TrackStatus track)
            => track.Phase == TrackPhase.Working ? "lyrics.ai.footer.working" : "lyrics.ai.footer.generated";

        /// <summary>Free the NPU contexts after <see cref="IdleUnloadMs"/> without a job.</summary>
        public static bool UnloadAfterIdle(long lastJobEndMs, long nowMs) => nowMs - lastJobEndMs >= IdleUnloadMs;

        /// <summary>Setup finished: toast only when the card is not on screen (it already says Ready).</summary>
        public static bool ToastOnReady(bool settingsPageVisible) => !settingsPageVisible;

        /// <summary>Setup failed: toast only when the card is not on screen; a cancel is the user's own act.</summary>
        public static bool ToastOnError(bool settingsPageVisible, SetupError error)
            => !settingsPageVisible && error is not (SetupError.None or SetupError.Cancelled);
    }
}
