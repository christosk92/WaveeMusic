// ── AiLyrics/AiLyrics.Host.cs ────────────────────────────────────────────────────────────────────────────────────────
// Current, Track, Job, Install, Shutdown, the verbs, Driver, Worker
//
// Role: HOST (plan §2: the state machine, the threads, the store integration)
//
//   UI thread        the signals (Current, Track, Job), the ledger and every verb. Nothing here reads a file, waits on a
//                    task or touches the NPU: work goes to a Task (boot scan, download) or to the worker, and every result
//                    comes back through the UI post carrying the ticket it started with (Ledger). A stale ticket — the job
//                    was cancelled or replaced, the feature went off, the files were removed — is dropped.
//   download task    Pack.InstallAsync; progress is posted at most 10 times a second (Rules.SpeedMeter).
//   worker thread    "wavee-ai-lyrics", BelowNormal: owns LoadedModels (the NPU sessions), runs one TrackJob at a time
//                    and the file deletions that must wait for an unload. Loads and jobs take a token the UI cancels
//                    before it queues the next command (both check it between NPU calls).
//   Driver           signal effects in the shell root: track change, lyrics arrival, battery saver, the preference
//                    epoch and the setup phase decide (Rules.Eligibility) whether the current track gets timed.

using System.Collections.Concurrent;
using System.Diagnostics;
using FluentGpu.Controls;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.WindowsApi.Devices;

namespace Wavee;

public static partial class AiLyrics
{
    /// <summary>The setup state the Settings card and the header button render. Written on the UI thread only.</summary>
    public static readonly Signal<Status> Current = new(Status.Unknown);

    /// <summary>The current track's AI state. Written on the UI thread only.</summary>
    public static readonly Signal<TrackStatus> Track = new(default);

    /// <summary>The current (or last) job for the lyrics inspector and the footer's buffer bar. Written on the UI thread.</summary>
    public static readonly Signal<JobInfo?> Job = new(null);

    /// <summary>What the inspector shows about one track's AI timing.</summary>
    /// <param name="Outcome">working, done, cached, cancelled, stopped (people-made word timing arrived), skipped, failed.</param>
    public sealed record JobInfo(string TrackId, string Language, int LineCount, int LinesReady, double DurationSeconds,
        double ProcessedSeconds, double SeparateSeconds, double AlignSeconds, long ElapsedMs, bool FromCache,
        string ResultPath, string Outcome, string Detail);

    /// <summary>The results file of a track (the inspector shows it and can delete it to time the song again).</summary>
    public static string ResultPathOf(string trackId) => Path.Combine(ResultsDir, Results.FileName(trackId, PackVersion));

    /// <summary>The inspector's "Time again": forget the saved result of <paramref name="trackId"/> and, when it is the
    /// playing track, run the job again. The file goes on the worker, after the cancelled job has finished writing it.</summary>
    public static void Retime(string trackId)
    {
        if (trackId.Length == 0) return;
        if (s_ledger.JobTrack == trackId) CancelJob();
        s_lastPublished.Remove(trackId);
        string path = ResultPathOf(trackId);
        WorkerQueue(() =>
        {
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            s_post(() =>
            {
                if (Track.Peek().TrackId == trackId) Track.Value = default;
                Lyrics.Store.Refetch(trackId);
            });
        });
    }

    // ── per-song preference: AI timing over the provider's people-made word timing ────────────────────────────────

    static readonly HashSet<string> s_preferAi = new(StringComparer.Ordinal);
    const int PreferAiCap = 2000;
    static string PreferAiPath => Path.Combine(Root, "prefer-ai.txt");
    static readonly object s_preferWrite = new();
    static string[] s_preferSnapshot = [];

    /// <summary>Bumped when a song's preference changes, so the header and the inspector re-render.</summary>
    public static readonly Signal<int> PreferEpoch = new(0);

    /// <summary>True when the user chose AI timing for <paramref name="trackId"/> over the provider's word timing.
    /// Reads <see cref="PreferEpoch"/>, so a render that asks re-renders on a change. UI thread.</summary>
    public static bool PrefersAi(string trackId)
    {
        _ = PreferEpoch.Value;
        return trackId.Length > 0 && s_preferAi.Contains(trackId);
    }

    /// <summary>Use AI timing for this song even when a provider has people-made word timing (on), or go back to the
    /// provider's (off: the provider's document is fetched again; the saved AI result stays for next time). Remembered
    /// across restarts in ai\lyrics\prefer-ai.txt. UI thread.</summary>
    public static void SetPreferAi(string trackId, bool on)
    {
        if (trackId.Length == 0 || (on ? !s_preferAi.Add(trackId) : !s_preferAi.Remove(trackId))) return;
        if (on && s_preferAi.Count > PreferAiCap) s_preferAi.Remove(s_preferAi.First());
        Log.Info("ai-lyrics", $"ai.prefer track={trackId} ai={on}");
        SavePreferAi();
        if (!on)
        {
            if (s_ledger.JobTrack == trackId) CancelJob();
            s_lastPublished.Remove(trackId);                                       // else Evaluate re-layers our doc
            if (Track.Peek().TrackId == trackId) Track.Value = default;
            if (Lyrics.Store.Doc(trackId) is { Generated: true }) Lyrics.Store.Refetch(trackId);
        }
        PreferEpoch.Value++;                                                       // re-runs the driver, which starts the job when on
    }

    /// <summary>One writer at a time, and it always writes the newest list: two quick toggles never leave the older one.</summary>
    static void SavePreferAi()
    {
        Volatile.Write(ref s_preferSnapshot, [.. s_preferAi]);
        _ = Task.Run(() =>
        {
            lock (s_preferWrite)
            {
                try { Directory.CreateDirectory(Root); File.WriteAllLines(PreferAiPath, Volatile.Read(ref s_preferSnapshot)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warn("ai-lyrics", "prefer-ai save failed", ex); }
            }
        });
    }

    static void LoadPreferAi()
    {
        try
        {
            if (!File.Exists(PreferAiPath)) return;
            var ids = File.ReadAllLines(PreferAiPath);
            s_post(() => { foreach (var id in ids) if (id.Trim().Length > 0) s_preferAi.Add(id.Trim()); PreferEpoch.Value++; });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warn("ai-lyrics", "prefer-ai read failed", ex); }
    }

    /// <summary>Set by the Settings card while it is on screen: a toast is redundant then (Rules.ToastOnReady).</summary>
    public static bool SettingsCardVisible { get; set; }

    static Action<Action> s_post = static a => a();
    static IReadOnlyList<ComputeAdapterInfo> s_npus = Array.Empty<ComputeAdapterInfo>();
    static CancellationTokenSource? s_downloadCts;
    static CancellationTokenSource? s_loadCts;
    static bool s_pauseRequested;
    static Worker? s_worker;
    static readonly Ledger s_ledger = new();
    static readonly Dictionary<string, Lyrics.Doc> s_lastPublished = new(StringComparer.Ordinal);

    // the playhead for just-in-time commits, written by the Driver (UI thread), read by the worker
    static int s_posMs, s_playing;
    static long s_posAtTick;

    static readonly string[] s_languageOrder = ["en", "es", "nl", "ko"];

    // ══ boot and shutdown ═════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Called once at shell composition with the UI post. Starts the boot scan on a background task.</summary>
    public static void Install(Action<Action> post)
    {
        s_post = post;
        _ = Task.Run(Boot);
    }

    static void Boot()
    {
        try
        {
            Pack.FinishPendingRemoval();
            IReadOnlyList<ComputeAdapterInfo> npus = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)
                ? ComputeAdapters.Enumerate(ComputeAdapterKind.Npu) : Array.Empty<ComputeAdapterInfo>();
            bool arm64 = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64;
            int build = Environment.OSVersion.Version.Build;
            var availability = Rules.Availability(arm64, build, npus);
            var languages = LanguagesSetting();
            var missing = Pack.Missing(PackManifest.Embedded.FilesFor(languages));
            long used = Pack.DiskUsed();
            SweepResults();
            LoadPreferAi();
            var npu = Rules.SupportedNpu(npus);
            Log.Info("ai-lyrics", $"ai.capability arm64={arm64} build={build} npus={npus.Count} availability={availability} " +
                                  $"npu=\"{npu?.Description}\" driver={npu?.DriverVersion} missing={missing.Count}");
            s_post(() => Seed(npus, availability, languages, missing, used));
        }
        catch (Exception ex)
        {
            Log.Warn("ai-lyrics", "boot scan failed", ex);
            s_post(() => Current.Value = Status.Unknown);
        }
    }

    static void Seed(IReadOnlyList<ComputeAdapterInfo> npus, Availability availability, IReadOnlyList<string> languages,
        List<PackFile> missing, long used)
    {
        s_npus = npus;
        var npu = Rules.SupportedNpu(npus);
        bool enabled = Platform.Settings.Get(Platform.Keys.AiLyricsEnabled);
        var phase = Rules.InitialPhase(availability, enabled, missing.Count == 0);
        Current.Value = Status.Unknown with
        {
            Phase = phase, Availability = availability, Enabled = enabled,
            NpuName = npu?.Description ?? "", NpuDriver = npu?.DriverVersion ?? "",
            InstalledBytes = used, Languages = languages, PendingDownloadBytes = Pack.DownloadBytes(missing),
        };
        // Boot only checks that the installed graphs are compiled: the NPU sessions (~0.9 GB of mapped context binaries and
        // ~230 MB of shared NPU buffers) load when the first job that needs them starts — and overlap that job's audio
        // fetch — not for a session that never times a song. Setup, a retry and a download still load at once.
        if (phase == SetupPhase.Ready) LoadModels(deferIfCompiled: true);
    }

    /// <summary>App exit: stop the download (partial files stay, the next start resumes them), a running compile (between
    /// graphs) and the job, then the worker.</summary>
    public static void Shutdown()
    {
        try { s_downloadCts?.Cancel(); } catch (ObjectDisposedException) { }
        CancelLoads();
        s_worker?.Shutdown();
    }

    // ══ the verbs (UI thread) ═════════════════════════════════════════════════════════════════════════════════════

    public static void SetEnabled(bool on)
    {
        var st = Current.Peek();
        if (on && !Rules.CanToggleOn(st.Availability)) return;
        Prefs.AiLyrics.Set(Platform.Keys.AiLyricsEnabled, on);
        if (!on)
        {
            CancelJob();
            CancelDownloadInternal(pause: false);
            CancelLoads();
            s_worker?.Unload();
            Current.Value = st with { Enabled = false, Phase = SetupPhase.Off, Error = SetupError.None, ErrorDetail = "" };
            Track.Value = default;
            return;
        }
        Current.Value = st with { Enabled = true };
        RefreshInstalled(afterwards: ready =>
        {
            if (!Current.Peek().Enabled) return;
            if (ready) LoadModels();
            else Current.Value = Current.Peek() with { Phase = SetupPhase.NeedsSetup };
        });
    }

    /// <summary>Start (or resume) the download of everything the installed languages need.</summary>
    public static void StartDownload() => StartDownload(LanguagesSetting(), addLanguage: null);

    static void StartDownload(IReadOnlyList<string> languages, string? addLanguage)
    {
        if (s_downloadCts is not null) return;                     // one download at a time
        var cts = new CancellationTokenSource();
        s_downloadCts = cts;
        s_pauseRequested = false;
        var st = Current.Peek();
        Current.Value = st with
        {
            Phase = SetupPhase.Downloading, Error = SetupError.None, ErrorDetail = "",
            Download = new DownloadProgress(DownloadPhase.Checking, 0, st.PendingDownloadBytes, 0, ""),
        };
        _ = Task.Run(async () =>
        {
            try
            {
                var all = PackManifest.Embedded.FilesFor(languages);
                var files = Pack.Missing(all);
                long total = Pack.DownloadBytes(files), already = Pack.DownloadBytes(all) - total;
                Log.Info("ai-lyrics", $"ai.download.start files={files.Count} bytes={total}");
                await Pack.InstallAsync(files, PackManifest.Embedded.Base,
                    p => s_post(() => { if (s_downloadCts == cts && Current.Peek().Phase == SetupPhase.Downloading) Current.Value = Current.Peek() with { Download = p }; }),
                    cts.Token, already).ConfigureAwait(false);
                long used = Pack.DiskUsed();
                Log.Info("ai-lyrics", $"ai.download.done bytes={total}");
                s_post(() =>
                {
                    if (s_downloadCts != cts) return;
                    s_downloadCts = null;
                    cts.Dispose();
                    if (!Current.Peek().Enabled) return;
                    if (addLanguage is not null)
                    {
                        var langs = new List<string>(LanguagesSetting());
                        if (!langs.Contains(addLanguage)) langs.Add(addLanguage);
                        Prefs.AiLyrics.SetLanguages(Ordered(langs));
                    }
                    Current.Value = Current.Peek() with { InstalledBytes = used, PendingDownloadBytes = 0, Languages = LanguagesSetting() };
                    LoadModels(reload: true);
                });
            }
            catch (OperationCanceledException)
            {
                s_post(() =>
                {
                    if (s_downloadCts != cts) return;
                    s_downloadCts = null;
                    cts.Dispose();
                    var now = Current.Peek();
                    if (!now.Enabled) return;
                    Current.Value = now with { Phase = s_pauseRequested ? SetupPhase.Paused : SetupPhase.NeedsSetup };
                });
            }
            catch (Exception ex) when (ex is SetupException or IOException or UnauthorizedAccessException)
            {
                var kind = ex is SetupException se ? se.Kind : SetupError.Unknown;
                Log.Warn("ai-lyrics", $"ai.download.failed kind={kind} detail={ex.Message}");
                s_post(() =>
                {
                    if (s_downloadCts != cts) return;
                    s_downloadCts = null;
                    cts.Dispose();
                    Fail(kind, ex.Message);
                });
            }
        });
    }

    public static void PauseDownload() => CancelDownloadInternal(pause: true);
    public static void ResumeDownload() => StartDownload();
    public static void CancelDownload() => CancelDownloadInternal(pause: false);

    static void CancelDownloadInternal(bool pause)
    {
        if (s_downloadCts is not { } cts) return;
        s_pauseRequested = pause;
        try { cts.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>The error card's "Try again": a download error downloads again (resuming), a runtime or compile error
    /// loads the models again.</summary>
    public static void Retry()
    {
        var st = Current.Peek();
        if (Rules.RetryLoadsModels(st.Error)) LoadModels(reload: true);
        else StartDownload();
    }

    /// <summary>Delete every downloaded file, result and per-song choice. The deletion runs on the worker right after the
    /// unload, so no file is still mapped by a session; a loaded runtime DLL is cleared on the next start.</summary>
    public static void RemoveFiles(Action<bool>? deferred = null)
    {
        CancelJob();
        CancelDownloadInternal(pause: false);
        CancelLoads();
        s_preferAi.Clear();
        PreferEpoch.Value++;
        s_lastPublished.Clear();
        s_worker?.Unload();
        WorkerQueue(() =>
        {
            bool complete = Pack.Remove();
            Log.Info("ai-lyrics", $"ai.remove deferred={!complete}");
            var missing = Pack.Missing(PackManifest.Embedded.FilesFor(LanguagesSetting()));
            s_post(() =>
            {
                var st = Current.Peek();
                Current.Value = st with
                {
                    Phase = st.Enabled ? SetupPhase.NeedsSetup : SetupPhase.Off, InstalledBytes = 0,
                    PendingDownloadBytes = Pack.DownloadBytes(missing), Error = SetupError.None, ErrorDetail = "",
                };
                Track.Value = default;
                deferred?.Invoke(!complete);
            });
        });
    }

    /// <summary>Download one more aligner language, then reload the models with it.</summary>
    public static void InstallLanguage(string language)
    {
        var langs = new List<string>(LanguagesSetting());
        if (!langs.Contains(language)) langs.Add(language);
        StartDownload(langs, addLanguage: language);
    }

    /// <summary>Remove one aligner language (never the last one). Its files go on the worker after the unload.</summary>
    public static void RemoveLanguage(string language)
    {
        var langs = new List<string>(LanguagesSetting());
        if (langs.Count <= 1 || !langs.Remove(language)) return;
        CancelJob();
        CancelLoads();
        Prefs.AiLyrics.SetLanguages(langs);
        s_worker?.Unload();
        WorkerQueue(() =>
        {
            Pack.RemoveLanguage(language);
            long used = Pack.DiskUsed();
            s_post(() =>
            {
                Current.Value = Current.Peek() with { InstalledBytes = used, Languages = LanguagesSetting() };
                if (Current.Peek().Enabled) LoadModels(reload: true);
            });
        });
    }

    /// <summary>Open the AI files folder in Explorer.</summary>
    public static void OpenFolder()
    {
        _ = Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(Root);
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + Root + "\"") { UseShellExecute = true });
            }
            catch (Exception ex) { Log.Warn("ai-lyrics", "open folder failed", ex); }
        });
    }

    // ══ internals (UI thread) ═════════════════════════════════════════════════════════════════════════════════════

    /// <summary>A setup error. Ignored while the feature is off: a failure the user can no longer act on is not news.</summary>
    static void Fail(SetupError kind, string detail)
    {
        var st = Current.Peek();
        if (!st.Enabled) return;
        Current.Value = st with { Phase = SetupPhase.Error, Error = kind, ErrorDetail = detail };
        if (Rules.ToastOnError(SettingsCardVisible, kind))
            Notify.Say(Strings.Settings.Lyrics.Ai.Toast.Failed(Loc.Get(Rules.ErrorKey(kind))), InfoBarSeverity.Error,
                actionLabel: Loc.Get(Strings.Settings.Lyrics.Ai.OpenSettings), onAction: static () => Settings.Open(Settings.Tab.Appearance),
                dedupeKey: "ai-lyrics-error");
    }

    /// <param name="deferIfCompiled">Boot: when every installed graph is already compiled, only record what is installed
    /// and leave the sessions unloaded until a job needs them (<see cref="Worker"/>'s EnsureLanguage loads them). A graph
    /// that still needs compiling is loaded (compiled) now, with the Preparing progress, exactly as before.</param>
    static void LoadModels(bool reload = false, bool deferIfCompiled = false)
    {
        CancelLoads();
        var cts = new CancellationTokenSource();
        s_loadCts = cts;
        int ticket = s_ledger.BeginLoad();
        s_worker ??= new Worker();
        s_worker.Load(LanguagesSetting(), reload, ticket, cts.Token, deferIfCompiled);
    }

    /// <summary>A load in flight stops at its next graph, and whatever it posts afterwards is stale.</summary>
    static void CancelLoads()
    {
        s_ledger.InvalidateLoads();
        if (s_loadCts is { } cts)
        {
            s_loadCts = null;
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    /// <summary>Run <paramref name="work"/> on the worker, after every command already queued (an unload, a cancelled job).</summary>
    static void WorkerQueue(Action work)
    {
        s_worker ??= new Worker();
        s_worker.Queue(work);
    }

    static void RefreshInstalled(Action<bool> afterwards)
    {
        var langs = LanguagesSetting();
        _ = Task.Run(() =>
        {
            var missing = Pack.Missing(PackManifest.Embedded.FilesFor(langs));
            long used = Pack.DiskUsed();
            s_post(() =>
            {
                Current.Value = Current.Peek() with { InstalledBytes = used, PendingDownloadBytes = Pack.DownloadBytes(missing), Languages = langs };
                afterwards(missing.Count == 0);
            });
        });
    }

    static void CancelJob()
    {
        s_worker?.CancelJob();
        if (s_ledger.JobRunning) s_ledger.EndJob();
    }

    static IReadOnlyList<string> LanguagesSetting()
    {
        var list = new List<string>();
        foreach (var part in (Platform.Settings.Get(Platform.Keys.AiLyricsLanguages) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string l = part.ToLowerInvariant();
            if (PackManifest.Embedded.Languages.ContainsKey(l) && !list.Contains(l)) list.Add(l);
        }
        if (list.Count == 0) list.Add("en");
        return Ordered(list);
    }

    static List<string> Ordered(IEnumerable<string> langs)
    {
        var set = new HashSet<string>(langs, StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        foreach (var l in s_languageOrder) if (set.Remove(l)) list.Add(l);
        list.AddRange(set);
        return list;
    }

    static void SweepResults()
    {
        try
        {
            if (!Directory.Exists(ResultsDir)) return;
            var files = new List<(string, long, long)>();
            foreach (var f in new DirectoryInfo(ResultsDir).EnumerateFiles("*.json"))
                files.Add((f.FullName, f.Length, new DateTimeOffset(f.LastWriteTimeUtc).ToUnixTimeMilliseconds()));
            foreach (var victim in Results.SweepVictims(files)) try { File.Delete(victim); } catch (IOException) { }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warn("ai-lyrics", "results sweep failed", ex); }
    }

    // ══ the driver (UI thread; mounted in the shell root) ═════════════════════════════════════════════════════════

    /// <summary>The driver's playhead effect: the shell root's Render passes it to <c>UseSignalEffect</c>. It copies the
    /// sampled position into the fields the worker reads for just-in-time commits.</summary>
    public static void DriverPlayhead()
    {
        s_posMs = Playback.PositionMs.Value;
        s_playing = Playback.IsPlaying.Value ? 1 : 0;
        s_posAtTick = Environment.TickCount64;
    }

    /// <summary>The driver's decision effect (<c>UseSignalEffect</c> in the shell root): every signal it reads re-runs it,
    /// so a track change, a lyrics arrival, battery saver, a preference change or a phase change re-evaluates.</summary>
    public static void DriverEvaluate() => Evaluate();

    static double PlayheadSeconds()
    {
        long since = Volatile.Read(ref s_playing) != 0 ? Environment.TickCount64 - Volatile.Read(ref s_posAtTick) : 0;
        return (Volatile.Read(ref s_posMs) + since) / 1000.0;
    }

    static void Evaluate()
    {
        var st = Current.Value;
        var id = Playback.CurrentId.Value;
        _ = Lyrics.Store.Changed.Value;
        bool saver = Platform.Power.EnergySaver.Value;
        _ = Prefs.AiLyrics.Epoch.Value;
        _ = PreferEpoch.Value;

        if (!st.Enabled || st.Phase != SetupPhase.Ready || !id.IsValid)
        {
            if (s_ledger.JobRunning) CancelJob();
            if (Track.Peek().Phase != TrackPhase.Idle) Track.Value = default;
            return;
        }
        string trackId = "";
        Track track = default;
        if (id.Kind == EntityKind.Track)
        {
            track = Entities.Track(id);
            trackId = Lyrics.Store.IdOf(track);
        }
        if (s_ledger.JobRunning && s_ledger.JobTrack != trackId) CancelJob();
        if (trackId.Length == 0)
        {
            // a podcast episode, or a track whose entity is not published yet (that one re-evaluates when it lands)
            var reason0 = id.Kind == EntityKind.Track ? SkipReason.None : SkipReason.Podcast;
            Track.Value = reason0 == SkipReason.None
                ? new TrackStatus("", TrackPhase.Waiting, SkipReason.None, "", 0, 0, 0, false)
                : new TrackStatus("", TrackPhase.Skipped, SkipReason.Podcast, "", 0, 0, 0, false);
            return;
        }
        if (!Lyrics.Store.Answered(trackId))
        {
            if (Track.Peek().TrackId != trackId || Track.Peek().Phase != TrackPhase.Waiting)
                Track.Value = new TrackStatus(trackId, TrackPhase.Waiting, SkipReason.None, "", 0, 0, 0, false);
            return;
        }
        var doc = Lyrics.Store.Doc(trackId);
        if (doc is { Generated: true })                                            // our own publish is on screen
        {
            if (s_ledger.JobTrack != trackId && (Track.Peek().TrackId != trackId || Track.Peek().Phase != TrackPhase.Done))
                Track.Value = new TrackStatus(trackId, TrackPhase.Done, SkipReason.None, doc.Language ?? "", doc.Lines.Count, doc.Lines.Count, 0, true);
            return;
        }
        bool prefersAi = s_preferAi.Contains(trackId);
        if (doc is { Sync: Lyrics.SyncKind.Syllable } && !prefersAi)
        {
            // people-made word timing arrived (often a background provider pass after the line-synced first answer):
            // it outranks ours, so stop the job and say so instead of claiming AI timing that is not on screen
            if (s_ledger.JobTrack == trackId)
            {
                CancelJob();
                if (Job.Peek() is { } j && j.TrackId == trackId) Job.Value = j with { Outcome = "stopped", Detail = doc.Provider ?? "" };
            }
            if (Track.Peek().TrackId != trackId || Track.Peek().Reason != SkipReason.AlreadyWordByWord)
                Track.Value = new TrackStatus(trackId, TrackPhase.Skipped, SkipReason.AlreadyWordByWord, Rules.LanguageOf(doc), 0, doc.Lines.Count, 0, false);
            return;
        }
        if (s_lastPublished.TryGetValue(trackId, out var last) && doc is not null && last.Lines.Count == doc.Lines.Count)
        {
            Lyrics.Store.Upgrade(last, prefersAi);                                 // a provider pass overwrote ours: re-layer
            return;
        }
        if (s_ledger.JobTrack == trackId) return;                                  // already working on it

        var reason = Rules.Eligibility(doc, id.Kind, track.Uri.Text.StartsWith("spotify:track:", StringComparison.Ordinal),
            track.DurationMs, Platform.Settings.Get(Platform.Keys.AiLyricsWordSync), Platform.Settings.Get(Platform.Keys.AiLyricsPlainText),
            st.Languages, saver, Platform.Settings.Get(Platform.Keys.AiLyricsOnBatterySaver), st.Phase);
        if (reason == SkipReason.AlreadyWordByWord && prefersAi) reason = SkipReason.None;   // the user chose ours
        if (reason != SkipReason.None)
        {
            if (Track.Peek().TrackId != trackId || Track.Peek().Reason != reason)
                Log.Info("ai-lyrics", $"ai.skip track={trackId} reason={reason} provider={doc?.Provider} sync={doc?.Sync} tag={doc?.Language} language={(doc is null ? "" : Rules.LanguageOf(doc))}");
            Track.Value = new TrackStatus(trackId, TrackPhase.Skipped, reason, doc is null ? "" : Rules.LanguageOf(doc), 0, doc?.Lines.Count ?? 0, 0, false);
            return;
        }
        string language = Rules.LanguageOf(doc!);
        int ticket = s_ledger.BeginJob(trackId);
        Track.Value = new TrackStatus(trackId, TrackPhase.Working, SkipReason.None, language, 0, doc!.Lines.Count, 0, false);
        Job.Value = new JobInfo(trackId, language, doc.Lines.Count, 0, track.DurationMs / 1000.0, 0, 0, 0, 0, false, ResultPathOf(trackId), "working", "");
        s_worker ??= new Worker();
        s_worker.Start(new JobRequest(trackId, ticket, track.Uri.Text, track.DurationMs, doc, language, prefersAi, Platform.Network.IsMetered));
    }

    // ── posts from the worker (UI thread) ─────────────────────────────────────────────────────────────────────────

    /// <summary>A job could not run or failed. Dropped when the job is no longer the current one.</summary>
    static void Skip(JobRequest req, SkipReason reason, string detail)
    {
        if (!s_ledger.IsCurrentJob(req.Ticket, req.TrackId)) return;
        s_ledger.EndJob();
        if (Job.Peek() is { } j && j.TrackId == req.TrackId) Job.Value = j with { Outcome = reason == SkipReason.Failed ? "failed" : "skipped", Detail = detail };
        if (Track.Peek().TrackId == req.TrackId)
            Track.Value = Track.Peek() with { Phase = TrackPhase.Skipped, Reason = reason };
    }

    // ══ the worker thread ═════════════════════════════════════════════════════════════════════════════════════════

    sealed record JobRequest(string TrackId, int Ticket, string Uri, long DurationMs, Lyrics.Doc Source, string Language,
        bool OverrideProvider, bool Metered);

    sealed class Worker
    {
        readonly BlockingCollection<Action> _queue = new();
        readonly Thread _thread;
        LoadedModels? _models;
        JobScratch? _scratch;   // the jobs' ~28 MB of working buffers, reused job to job; dropped with the models
        string _installedKey = "";
        IReadOnlyList<string> _installed = [];
        CancellationTokenSource? _jobCts;
        volatile bool _loaded;
        long _lastUseMs;   // the worker thread's own clock: when a command last ran with the models loaded

        public bool Loaded => _loaded;

        public Worker()
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "wavee-ai-lyrics", Priority = ThreadPriority.BelowNormal };
            _thread.Start();
        }

        /// <summary>One command at a time, so nothing runs while it waits: the IDLE UNLOAD is timed here, on this thread,
        /// not by a UI interval. The UI loop parks while the window is minimized and every UseInterval with it, which used
        /// to keep the ~1.1 GB of sessions loaded for as long as Wavee sat in the taskbar.</summary>
        void Run()
        {
            while (true)
            {
                int wait = Rules.IdleWaitMs(_loaded, _lastUseMs, Environment.TickCount64);
                if (!_queue.TryTake(out var cmd, wait))
                {
                    if (_queue.IsCompleted) break;
                    if (_loaded && Rules.IdleWaitMs(true, _lastUseMs, Environment.TickCount64) == 0) UnloadNow();
                    continue;
                }
                try { cmd(); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { Log.Warn("ai-lyrics", "worker command failed", ex); }
                if (_loaded) _lastUseMs = Environment.TickCount64;
            }
            DisposeModels();
        }

        void DisposeModels()
        {
            _models?.Dispose();
            _models = null;
            _scratch = null;
            _loaded = false;
        }

        /// <summary>Queue arbitrary work behind every command already queued. After shutdown it is dropped.</summary>
        public void Queue(Action work)
        {
            try { _queue.Add(work); }
            catch (InvalidOperationException) { }                                  // CompleteAdding: the app is closing
        }

        public void Load(IReadOnlyList<string> languages, bool reload, int ticket, CancellationToken ct, bool deferIfCompiled = false)
            => Queue(() => DoLoad(languages, string.Join(",", languages), reload, ticket, ct, deferIfCompiled));

        /// <summary>Every installed language is compiled once (setup), but only the first stays loaded; a song in another
        /// language swaps the aligner (<see cref="EnsureLanguage"/>). Each loaded language holds its own NPU buffers.
        /// Every post carries <paramref name="ticket"/>: a load the UI no longer wants changes nothing on screen.</summary>
        void DoLoad(IReadOnlyList<string> languages, string key, bool reload, int ticket, CancellationToken ct, bool deferIfCompiled = false)
        {
            if (ct.IsCancellationRequested) return;
            if (_models is not null && !reload && key == _installedKey) { PostLoad(ticket, SetReady); return; }
            DisposeModels();
            _installed = languages;
            _installedKey = key;
            string active = languages.Count > 0 ? languages[0] : "en";
            var others = new List<string>();
            foreach (var l in languages) if (l != active) others.Add(l);
            // the same order LoadedModels.Load works in: the other languages' uncompiled graphs (compiled, then released),
            // then the separator and the active language — so the progress weights line up with what is being done
            var graphs = new List<string>();
            foreach (var l in others) foreach (var stg in Aligner.StageNames)
            {
                string g = Path.Combine(ModelsDir, $"align-{l}.{stg}.onnx");
                if (!File.Exists(LoadedModels.CtxPath(g))) graphs.Add(g);
            }
            graphs.Add(Path.Combine(ModelsDir, "separator.onnx"));
            foreach (var stg in Aligner.StageNames) graphs.Add(Path.Combine(ModelsDir, $"align-{active}.{stg}.onnx"));
            var weights = Rules.PrepareWeights(graphs, static p => File.Exists(p) ? new FileInfo(p).Length : 0);
            long weightTotal = 0; foreach (var w in weights) weightTotal += w;
            bool anyUncached = graphs.Exists(g => !File.Exists(LoadedModels.CtxPath(g)));
            if (deferIfCompiled && !anyUncached)
            {
                Log.Info("ai-lyrics", $"ai.models.deferred active={active} installed={key} (compiled; the first job that needs the NPU loads them)");
                PostLoad(ticket, SetReady);
                return;
            }
            if (anyUncached) PostLoad(ticket, () => Current.Value = Current.Peek() with { Phase = SetupPhase.Preparing, Prepare = new PrepareProgress(0, graphs.Count, 0, weightTotal, false, false) });
            var sw = Stopwatch.StartNew();
            try
            {
                _models = LoadedModels.Load(RuntimeDir, ModelsDir, [active], (done, total, cached) =>
                {
                    if (!anyUncached) return;
                    long wd = 0; for (int i = 0; i < done && i < weights.Length; i++) wd += weights[i];
                    PostLoad(ticket, () =>
                    {
                        if (Current.Peek().Phase == SetupPhase.Preparing)
                            Current.Value = Current.Peek() with { Prepare = new PrepareProgress(done, total, wd, weightTotal, false, false) };
                    });
                }, ct, others);
                _loaded = true;
                Log.Info("ai-lyrics", $"ai.models.loaded ms={sw.ElapsedMilliseconds} active={active} installed={key} graphs={graphs.Count} ort={_models.Ort.Version} compiled={anyUncached}");
                PostLoad(ticket, () =>
                {
                    bool wasPreparing = Current.Peek().Phase == SetupPhase.Preparing;
                    SetReady();
                    if (wasPreparing && Rules.ToastOnReady(SettingsCardVisible))
                        Notify.Say(Loc.Get(Strings.Settings.Lyrics.Ai.Toast.Ready), InfoBarSeverity.Success, dedupeKey: "ai-lyrics-ready");
                });
            }
            catch (OperationCanceledException)
            {
                DisposeModels();
                Log.Info("ai-lyrics", $"ai.models.load.cancel ms={sw.ElapsedMilliseconds}");
            }
            catch (NoNpuException ex)
            {
                Log.Warn("ai-lyrics", "ai.models.failed kind=NoNpuDevice " + ex.Message);
                PostLoad(ticket, () => Fail(SetupError.NoNpuDevice, ex.Message));
            }
            catch (OrtException ex)
            {
                var kind = ex.Message.Contains("onnxruntime.dll", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("ONNX Runtime", StringComparison.Ordinal)
                    ? SetupError.RuntimeLoad : SetupError.Compile;
                Log.Warn("ai-lyrics", $"ai.models.failed kind={kind} {ex.Message}");
                PostLoad(ticket, () => Fail(kind, ex.Message));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DllNotFoundException or BadImageFormatException
                                           or KeyNotFoundException or ArgumentException or System.Text.Json.JsonException)
            {
                Log.Warn("ai-lyrics", "ai.models.failed kind=RuntimeLoad", ex);
                PostLoad(ticket, () => Fail(SetupError.RuntimeLoad, ex.Message));
            }
        }

        static void PostLoad(int ticket, Action onUi) => s_post(() => { if (s_ledger.IsCurrentLoad(ticket)) onUi(); });

        static void SetReady()
        {
            var st = Current.Peek();
            if (!st.Enabled) return;
            Current.Value = st with { Phase = SetupPhase.Ready, Error = SetupError.None, ErrorDetail = "" };
        }

        /// <summary>On the worker: make sure the aligner for <paramref name="language"/> is the loaded one. The ORT
        /// environment, the QNN provider and the separator stay; only the aligner's five sessions are swapped (about 1 s
        /// from the compiled cache). After an idle unload everything loads again. False when it is not installed.</summary>
        bool EnsureLanguage(string language, CancellationToken ct)
        {
            if (_models is not null && _models.Aligners.ContainsKey(language)) return true;
            if (!_installed.Contains(language)) return false;
            var sw = Stopwatch.StartNew();
            bool recompile = Aligner.StageNames.Any(st => !File.Exists(LoadedModels.CtxPath(Path.Combine(ModelsDir, $"align-{language}.{st}.onnx"))));
            if (recompile) Log.Info("ai-lyrics", $"ai.models.recompile language={language}");
            if (_models is null)
            {
                _models = LoadedModels.Load(RuntimeDir, ModelsDir, [language], null, ct);
                _loaded = true;
                Log.Info("ai-lyrics", $"ai.models.loaded ms={sw.ElapsedMilliseconds} active={language} installed={_installedKey} lazy=true ort={_models.Ort.Version}");
            }
            else _models.SwapAligner(ModelsDir, language, null, ct);
            Log.Info("ai-lyrics", $"ai.models.switch active={language} ms={sw.ElapsedMilliseconds} recompiled={recompile}");
            return true;
        }

        public void Unload() => Queue(UnloadNow);

        void UnloadNow()
        {
            if (!_loaded && _models is null) return;
            long before = Environment.WorkingSet;
            DisposeModels();
            _installedKey = "";
            // The jobs' buffers are large-object garbage, and an idle app runs no full GC to reclaim it: the process
            // used to keep ~300 MB of dead LOH committed for hours after the unload. A BACKGROUND gen-2 collection hands
            // the freed regions back without stopping the UI thread: the unload now follows every song's job, and a
            // blocking compaction there would hitch playback and the lyrics view.
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: false);
            Log.Info("ai-lyrics", $"ai.models.unloaded ws={before / (1 << 20)}->{Environment.WorkingSet / (1 << 20)}MB heap={GC.GetGCMemoryInfo().HeapSizeBytes / (1 << 20)}MB");
        }

        public void CancelJob()
        {
            try { _jobCts?.Cancel(); } catch (ObjectDisposedException) { }
        }

        public void Start(JobRequest req)
        {
            CancelJob();
            var cts = new CancellationTokenSource();
            _jobCts = cts;
            Queue(() =>
            {
                try { RunJob(req, cts.Token); }
                finally { cts.Dispose(); }
            });
        }

        /// <summary>One job. Anything it throws ends it as Failed (a job never stays "Working"); a cancel says so in the
        /// inspector; every post is dropped once the job is no longer current.</summary>
        void RunJob(JobRequest req, CancellationToken ct)
        {
            try { RunJobCore(req, ct); }
            catch (OperationCanceledException)
            {
                Log.Info("ai-lyrics", $"ai.job.cancel track={req.TrackId}");
                s_post(() => { if (Job.Peek() is { Outcome: "working" } j && j.TrackId == req.TrackId) Job.Value = j with { Outcome = "cancelled" }; });
            }
            catch (Exception ex)
            {
                Log.Warn("ai-lyrics", $"ai.job.failed track={req.TrackId}", ex);
                s_post(() => Skip(req, SkipReason.Failed, ex.GetType().Name + ": " + ex.Message));
            }
        }

        void RunJobCore(JobRequest req, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            ulong hash = Rules.SourceHash(req.Source.Lines);
            // 1. the results cache: a replay needs no NPU and no audio
            string resultPath = ResultPathOf(req.TrackId);
            try
            {
                if (File.Exists(resultPath) && Results.TryDecode(File.ReadAllText(resultPath), out var cached)
                    && Results.Overlay(cached, req.Source, hash) is { } overlay)
                {
                    Log.Info("ai-lyrics", $"ai.results.hit track={req.TrackId}");
                    Publish(req, overlay, new JobProgress(req.Source.Lines.Count, req.Source.Lines.Count, req.DurationMs / 1000.0, true), fromCache: true, 0, 0, 0, ct);
                    return;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warn("ai-lyrics", "results read failed", ex); }

            // 2. timing a new song fetches its audio once more: not on a metered connection
            if (req.Metered)
            {
                Log.Info("ai-lyrics", $"ai.job.skip track={req.TrackId} reason=Metered");
                s_post(() => Skip(req, SkipReason.Metered, "metered connection"));
                return;
            }
            // Cold sessions (the first job since boot, or since the idle unload): open the audio on the pool while the worker
            // loads the NPU sessions, so the ~1.3 s load overlaps the fetch the job waits on anyway.
            Task<(SpotifyPcm? Pcm, Spotify.Audio.Fault Fault)>? early = null;
            if ((_models is null || !_models.Aligners.ContainsKey(req.Language)) && _installed.Contains(req.Language))
                early = Task.Run(() => (SpotifyPcm.Open(req.Uri, req.DurationMs, ct, out var f), f));
            bool ready;
            try { ready = EnsureLanguage(req.Language, ct); }
            catch { DisposeWhenDone(early); throw; }
            if (!ready || _models is null || !_models.Aligners.TryGetValue(req.Language, out var aligner))
            {
                DisposeWhenDone(early);
                Log.Info("ai-lyrics", $"ai.job.skip track={req.TrackId} reason=LanguageNotInstalled language={req.Language} installed={string.Join(",", _installed)} loaded={_models is not null}");
                s_post(() => Skip(req, SkipReason.LanguageNotInstalled, "language " + req.Language + " not installed"));
                return;
            }
            var sw = Stopwatch.StartNew();
            Spotify.Audio.Fault fault;
            SpotifyPcm? opened;
            if (early is not null) (opened, fault) = early.GetAwaiter().GetResult();
            else opened = SpotifyPcm.Open(req.Uri, req.DurationMs, ct, out fault);
            using var pcm = opened;
            if (pcm is null)
            {
                Log.Info("ai-lyrics", $"ai.job.skip track={req.TrackId} reason=audio fault={fault}");
                s_post(() => Skip(req, SkipReason.AudioUnavailable, "audio: " + fault));
                return;
            }
            Log.Info("ai-lyrics", $"ai.job.start track={req.TrackId} language={req.Language} lines={req.Source.Lines.Count}");
            var job = new TrackJob(req.Source, req.Language, _models.Separator, aligner, pcm, PlayheadSeconds);
            var final = job.Run((doc, p) => Publish(req, doc, p, fromCache: false, job.SeparateSeconds, job.AlignSeconds, sw.ElapsedMilliseconds, ct), ct,
                _scratch ??= new JobScratch());
            Directory.CreateDirectory(ResultsDir);
            string tmp = resultPath + ".tmp";
            File.WriteAllText(tmp, Results.Encode(final, hash, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
            File.Move(tmp, resultPath, overwrite: true);
            Log.Info("ai-lyrics", $"ai.job.done track={req.TrackId} ms={sw.ElapsedMilliseconds} separate={job.SeparateSeconds:0.0}s align={job.AlignSeconds:0.0}s");
        }

        /// <summary>An audio open the job will not use: close it whenever it lands (it may still be fetching).</summary>
        static void DisposeWhenDone(Task<(SpotifyPcm? Pcm, Spotify.Audio.Fault Fault)>? open)
            => open?.ContinueWith(static t => { if (t.IsCompletedSuccessfully) t.Result.Pcm?.Dispose(); }, TaskScheduler.Default);

        static void Publish(JobRequest req, Lyrics.Doc doc, JobProgress p, bool fromCache, double separate, double align, long elapsedMs,
            CancellationToken ct)
        {
            if (ct.IsCancellationRequested) return;
            Lyrics.Store.Upgrade(doc, req.OverrideProvider);
            s_post(() =>
            {
                // A progress post that was already queued when the job stopped (people-made word timing arrived, the
                // track changed, the feature went off) must not bring "Timing words · x of y" back.
                if (!s_ledger.IsCurrentJob(req.Ticket, req.TrackId)) return;
                Job.Value = new JobInfo(req.TrackId, req.Language, p.LineCount, p.LinesReady, req.DurationMs / 1000.0, p.ProcessedSeconds,
                    separate, align, elapsedMs, fromCache, ResultPathOf(req.TrackId),
                    fromCache ? "cached" : p.Final ? "done" : "working", "");
                s_lastPublished[req.TrackId] = doc;
                if (s_lastPublished.Count > 32) s_lastPublished.Remove(s_lastPublished.Keys.First());
                if (Track.Peek().TrackId != req.TrackId && Track.Peek().TrackId.Length > 0) return;
                var phase = p.Final ? TrackPhase.Done : TrackPhase.Working;
                Track.Value = new TrackStatus(req.TrackId, phase, SkipReason.None, req.Language, p.LinesReady, p.LineCount, p.ProcessedSeconds, fromCache);
                if (p.Final) s_ledger.EndJob();
            });
        }

        public void Shutdown()
        {
            CancelJob();
            _queue.CompleteAdding();
            if (!_thread.Join(2000)) Log.Warn("ai-lyrics", "the worker did not stop within 2 s");
        }
    }
}
