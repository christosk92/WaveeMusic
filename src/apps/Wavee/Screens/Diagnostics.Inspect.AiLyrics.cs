// ── Screens/Diagnostics.Inspect.AiLyrics.cs ─────────────────────────────────────────────────────────────────────────────
// `state kind=ai ai=<case> error=<kind> lang=<iso>`: the AI lyrics settings section in each of its states, without the module
//
// Role: SHELL (UI thread; writes AiLyrics.Current through AiLyrics.Stage)
//
// THE PATH. The headless server never starts the AI lyrics module (no `AiLyrics.Install`), so the card only ever showed "not started"
// (`Status.Unknown`: the switch off and disabled). This stages the card's ONE status model, `AiLyrics.Current`, with a made-up status for
// each phase the card draws, through `AiLyrics.Stage`, which also makes every module verb inert while it stands: a click on Try again,
// Remove or the switch downloads nothing, loads nothing and never touches the profile's `ai\lyrics` folder. The details are what the
// real download writes for each failure kind (`AiLyrics.Pack.cs`: the URL, the file, the drive). The next `mount` (or `reset`) puts
// "not started" back.

namespace Wavee;

public static partial class Diagnostics
{
    public sealed partial class InspectSession
    {
        /// <summary>The cases `state kind=ai ai=` stages, in the order `help` lists them.</summary>
        internal static readonly string[] AiCases =
            ["off", "unavailable", "setup", "downloading", "paused", "preparing", "ready", "lang-failed", "setup-failed", "error", "none"];

        /// <summary>The failure kinds `error=` takes (with `ai=lang-failed|setup-failed|error`).</summary>
        internal static readonly string[] AiErrors = ["notfound", "offline", "network", "hash", "diskfull", "unknown", "runtime", "nonpu", "compile"];

        /// <summary>True while a `state kind=ai` status stands: the next mount puts "not started" back.</summary>
        bool _aiStaged;

        /// <summary>`state kind=ai ai=<case> error=<kind> lang=<iso>`: stage the AI lyrics card (<see cref="AiStatus"/>).</summary>
        string AiState(int id, string name, Inspect.Args a)
        {
            string c = a.Has("ai") ? a.Str("ai").ToLowerInvariant() : "ready";
            string e = a.Has("error") ? a.Str("error").ToLowerInvariant() : "notfound";
            string lang = a.Has("lang") ? a.Str("lang").ToLowerInvariant() : "ja";
            if (Array.IndexOf(AiCases, c) < 0) return Inspect.Reply.Fail(id, name, "state kind=ai wants ai=" + string.Join("|", AiCases) + ", not '" + c + "'");
            if (AiErrorOf(e) is not { } error) return Inspect.Reply.Fail(id, name, "state kind=ai wants error=" + string.Join("|", AiErrors) + ", not '" + e + "'");
            if (!AiLyrics.PackManifest.Embedded.Languages.ContainsKey(lang))
                return Inspect.Reply.Fail(id, name, "state kind=ai wants lang= a pack language (" + string.Join("|", AiLyrics.PackManifest.Embedded.Languages.Keys) + "), not '" + lang + "'");
            AiLyrics.Stage(c == "none" ? null : AiStatus(c, error, lang));
            _aiStaged = c != "none";
            if (!a.Has("trace") && a.Bool("settle", true)) RunFrames(30);
            return Settled(id, name, a, w =>
            {
                w.WriteString("kind", "ai");
                w.WriteString("ai", c);
                var st = AiLyrics.Current.Peek();
                w.WriteString("phase", st.Phase.ToString());
                if (st.Error != AiLyrics.SetupError.None) w.WriteString("error", st.Error.ToString());
                if (st.LanguageFailure.IsSet) w.WriteString("failedLanguage", st.LanguageFailure.Language);
            });
        }

        /// <summary>A mount puts the module's "not started" back after a staged status.</summary>
        void RestoreAiLyrics()
        {
            if (!_aiStaged) return;
            _aiStaged = false;
            AiLyrics.Stage(null);
        }

        static AiLyrics.SetupError? AiErrorOf(string e) => e switch
        {
            "notfound" => AiLyrics.SetupError.NotFound,
            "offline" => AiLyrics.SetupError.Offline,
            "network" => AiLyrics.SetupError.Network,
            "hash" => AiLyrics.SetupError.HashMismatch,
            "diskfull" => AiLyrics.SetupError.DiskFull,
            "unknown" => AiLyrics.SetupError.Unknown,
            "runtime" => AiLyrics.SetupError.RuntimeLoad,
            "nonpu" => AiLyrics.SetupError.NoNpuDevice,
            "compile" => AiLyrics.SetupError.Compile,
            _ => null,
        };

        /// <summary>The status for one case. Installed: English and Spanish (two languages, so a language row reads as installed beside
        /// one that is not). <paramref name="lang"/> is the language a language failure names (and the one a first setup stopped on).</summary>
        static AiLyrics.Status AiStatus(string c, AiLyrics.SetupError error, string lang)
        {
            var manifest = AiLyrics.PackManifest.Embedded;
            IReadOnlyList<string> installed = ["en", "es"];
            long Bytes(IEnumerable<string> langs) => AiLyrics.Pack.DownloadBytes(manifest.FilesFor(langs));
            long all = Bytes(installed);
            var ready = AiLyrics.Status.Unknown with
            {
                Phase = AiLyrics.SetupPhase.Ready, Availability = AiLyrics.Availability.Available, Enabled = true,
                NpuName = "Qualcomm(R) Hexagon(TM) NPU", NpuDriver = "31.0.210.5", InstalledBytes = all, Languages = installed,
            };
            string detail = AiDetail(error, manifest, lang);
            return c switch
            {
                "off" => ready with { Phase = AiLyrics.SetupPhase.Off, Enabled = false },
                "unavailable" => AiLyrics.Status.Unknown with { Phase = AiLyrics.SetupPhase.Unavailable, Availability = AiLyrics.Availability.NotArm64 },
                "setup" => ready with { Phase = AiLyrics.SetupPhase.NeedsSetup, InstalledBytes = 0, PendingDownloadBytes = all },
                "downloading" => ready with
                {
                    Phase = AiLyrics.SetupPhase.Downloading, InstalledBytes = 0, PendingDownloadBytes = all,
                    Download = new AiLyrics.DownloadProgress(AiLyrics.DownloadPhase.Downloading, all * 2 / 5, all, 6.5e6, "separator.onnx"),
                },
                "paused" => ready with
                {
                    Phase = AiLyrics.SetupPhase.Paused, InstalledBytes = 0, PendingDownloadBytes = all,
                    Download = new AiLyrics.DownloadProgress(AiLyrics.DownloadPhase.Downloading, all * 2 / 5, all, 0, ""),
                },
                "preparing" => ready with
                {
                    Phase = AiLyrics.SetupPhase.Preparing, Prepare = new AiLyrics.PrepareProgress(3, 8, 3, 8, false, false),
                },
                "ready" => ready,
                // a language added to a ready feature failed: the card stays Ready, that language's own bar names it
                "lang-failed" => ready with { LanguageFailure = new AiLyrics.LanguageFailure(lang, error, detail) },
                // a first setup that stopped on a language's file: the whole card is the error, naming the language
                "setup-failed" => ready with
                {
                    Phase = AiLyrics.SetupPhase.Error, InstalledBytes = 0, PendingDownloadBytes = all, Error = error, ErrorDetail = detail,
                    LanguageFailure = new AiLyrics.LanguageFailure(lang, error, detail),
                },
                // the whole feature: a shared file, the runtime, the NPU or the compile
                _ => ready with { Phase = AiLyrics.SetupPhase.Error, Error = error, ErrorDetail = AiDetail(error, manifest, "") },
            };
        }

        /// <summary>What the real download writes as the detail of each failure kind (AiLyrics.Pack.cs): a long URL for the ones that name
        /// one, so Details is judged on the longest raw text it shows.</summary>
        static string AiDetail(AiLyrics.SetupError error, AiLyrics.PackManifest manifest, string lang)
        {
            string file = lang.Length > 0 && manifest.Languages.TryGetValue(lang, out var files) && files.Count > 0 ? files[^1].Name : "separator.onnx";
            string url = manifest.Base + file;
            return error switch
            {
                AiLyrics.SetupError.NotFound => url,
                AiLyrics.SetupError.Offline => "no network",
                AiLyrics.SetupError.Network => "no data for 30 s: " + url,
                AiLyrics.SetupError.HashMismatch => file + " @" + 16777216,
                AiLyrics.SetupError.DiskFull => AiLyrics.Rules.FormatBytes(2_400_000_000) + "|C:\\",
                AiLyrics.SetupError.RuntimeLoad => "onnxruntime.dll: The specified module could not be found. (0x8007007E)",
                AiLyrics.SetupError.NoNpuDevice => "QNN HTP backend: no device",
                AiLyrics.SetupError.Compile => "align-" + (lang.Length > 0 ? lang : "en") + ".conv1.onnx: context binary generation failed (QNN error 1002)",
                _ => "The process cannot access the file '" + file + ".partial' because it is being used by another process.",
            };
        }
    }
}
