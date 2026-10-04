// ── AiLyrics/AiLyrics.Host.cs ────────────────────────────────────────────────────────────────────────────────────────
// Current, Track, Install, Shutdown, the verbs, Driver, Worker
//
// Role: HOST (plan §2: the state machine, the threads, the store integration)
//
//   UI thread        the two signals (Current, Track) and every verb. Nothing here reads a file, waits on a task or
//                    touches the NPU: work goes to a Task (boot scan, download, remove) or to the worker, and every
//                    result comes back through the UI post.
//   download task    Pack.InstallAsync; progress is posted at most 10 times a second (Rules.SpeedMeter).
//   worker thread    "wavee-ai-lyrics", BelowNormal: owns LoadedModels (the NPU sessions) and runs one TrackJob at a
//                    time. The UI cancels a job's token before it queues the next command, so the worker never blocks
//                    on a command it cannot reach (a job checks its token between NPU calls, every ~250 ms).
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

    /// <summary>Set by the Settings card while it is on screen: a toast is redundant then (Rules.ToastOnReady).</summary>
    public static bool SettingsCardVisible { get; set; }

    static Action<Action> s_post = static a => a();
    static IReadOnlyList<ComputeAdapterInfo> s_npus = Array.Empty<ComputeAdapterInfo>();
    static CancellationTokenSource? s_downloadCts;
    static bool s_pauseRequested;
    static Worker? s_worker;
    static string s_jobTrack = "";
    static long s_lastJobEndMs;
    static readonly Dictionary<string, Lyrics.Doc> s_lastPublished = new(StringComparer.Ordinal);

    // the playhead for just-in-time commits, written by the Driver (UI thread), read by the worker
    static int s_posMs, s_playing;
    static long s_posAtTick;

    static readonly string[] s_languageOrder = ["en", "es"];

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
        if (phase == SetupPhase.Ready) LoadModels();
    }

    /// <summary>App exit: stop the download (partial files stay, the next start resumes them) and the worker.</summary>
    public static void Shutdown()
    {
        try { s_downloadCts?.Cancel(); } catch (ObjectDisposedException) { }
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
            s_worker?.Unload();
            Current.Value = st with { Enabled = false, Phase = SetupPhase.Off, Error = SetupError.None, ErrorDetail = "" };
            Track.Value = default;
            return;
        }
        Current.Value = st with { Enabled = true };
        RefreshInstalled(afterwards: ready =>
        {
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
                    p => s_post(() => { if (Current.Peek().Phase == SetupPhase.Downloading) Current.Value = Current.Peek() with { Download = p }; }),
                    cts.Token, already).ConfigureAwait(false);
                long used = Pack.DiskUsed();
                Log.Info("ai-lyrics", $"ai.download.done bytes={total}");
                s_post(() =>
                {
                    s_downloadCts = null;
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
                    s_downloadCts = null;
                    var now = Current.Peek();
                    if (!now.Enabled) return;
                    Current.Value = now with { Phase = s_pauseRequested ? SetupPhase.Paused : SetupPhase.NeedsSetup };
                });
            }
            catch (SetupException ex)
            {
                Log.Warn("ai-lyrics", $"ai.download.failed kind={ex.Kind} detail={ex.Message}");
                s_post(() => { s_downloadCts = null; Fail(ex.Kind, ex.Message); });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn("ai-lyrics", "ai.download.failed kind=io", ex);
                s_post(() => { s_downloadCts = null; Fail(SetupError.Unknown, ex.Message); });
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
        if (st.Error is SetupError.RuntimeLoad or SetupError.NoNpuDevice or SetupError.Compile) LoadModels(reload: true);
        else StartDownload();
    }

    /// <summary>Delete every downloaded file and result. Loaded runtime DLLs are cleared on the next start.</summary>
    public static void RemoveFiles(Action<bool>? deferred = null)
    {
        CancelJob();
        CancelDownloadInternal(pause: false);
        s_worker?.Unload();
        _ = Task.Run(() =>
        {
            // give the worker a moment to dispose the sessions before the files go
            Thread.Sleep(300);
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

    /// <summary>Remove one aligner language (never the last one).</summary>
    public static void RemoveLanguage(string language)
    {
        var langs = new List<string>(LanguagesSetting());
        if (langs.Count <= 1 || !langs.Remove(language)) return;
        CancelJob();
        Prefs.AiLyrics.SetLanguages(langs);
        s_worker?.Unload();
        _ = Task.Run(() =>
        {
            Thread.Sleep(300);
            if (PackManifest.Embedded.Languages.TryGetValue(language, out var files))
                foreach (var f in files)
                {
                    string path = Path.Combine(ModelsDir, f.Name);
                    LoadedModels.DeleteCompiled(path);
                    try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            long used = Pack.DiskUsed();
            s_post(() =>
            {
                Current.Value = Current.Peek() with { InstalledBytes = used, Languages = LanguagesSetting() };
                LoadModels(reload: true);
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

    static void Fail(SetupError kind, string detail)
    {
        var st = Current.Peek();
        Current.Value = st with { Phase = SetupPhase.Error, Error = kind, ErrorDetail = detail };
        if (Rules.ToastOnError(SettingsCardVisible, kind))
            Notify.Say(Strings.Settings.Lyrics.Ai.Toast.Failed(Loc.Get(Rules.ErrorKey(kind))), InfoBarSeverity.Error,
                actionLabel: Loc.Get(Strings.Settings.Lyrics.Ai.OpenSettings), onAction: static () => Settings.Open(Settings.Tab.Appearance),
                dedupeKey: "ai-lyrics-error");
    }

    static void LoadModels(bool reload = false)
    {
        s_worker ??= new Worker();
        var langs = LanguagesSetting();
        s_worker.Load(langs, reload);
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
        s_jobTrack = "";
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

    /// <summary>The driver's idle check (<c>UseInterval</c>, once a minute): unload the NPU sessions after 10 minutes
    /// without a job; the next song reloads them from the compiled cache in about 1.5 s.</summary>
    public static void DriverIdleTick()
    {
        if (s_worker is { } w && w.Loaded && s_jobTrack.Length == 0 && Rules.UnloadAfterIdle(s_lastJobEndMs, Environment.TickCount64))
            w.Unload();
    }

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

        if (!st.Enabled || st.Phase != SetupPhase.Ready || !id.IsValid)
        {
            if (s_jobTrack.Length > 0) CancelJob();
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
        if (s_jobTrack.Length > 0 && s_jobTrack != trackId) CancelJob();
        if (trackId.Length == 0)
        {
            Track.Value = new TrackStatus("", TrackPhase.Skipped, SkipReason.Podcast, "", 0, 0, 0, false);
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
            if (s_jobTrack != trackId && (Track.Peek().TrackId != trackId || Track.Peek().Phase != TrackPhase.Done))
                Track.Value = new TrackStatus(trackId, TrackPhase.Done, SkipReason.None, doc.Language ?? "", doc.Lines.Count, doc.Lines.Count, 0, true);
            return;
        }
        if (s_lastPublished.TryGetValue(trackId, out var last) && doc is not null && last.Lines.Count == doc.Lines.Count)
        {
            Lyrics.Store.Upgrade(last);                                            // a provider pass overwrote ours: re-layer
            return;
        }
        if (s_jobTrack == trackId) return;                                         // already working on it

        var reason = Rules.Eligibility(doc, id.Kind, track.Uri.Text.StartsWith("spotify:track:", StringComparison.Ordinal),
            track.DurationMs, Platform.Settings.Get(Platform.Keys.AiLyricsWordSync), Platform.Settings.Get(Platform.Keys.AiLyricsPlainText),
            st.Languages, saver, Platform.Settings.Get(Platform.Keys.AiLyricsOnBatterySaver), st.Phase);
        if (reason != SkipReason.None)
        {
            Track.Value = new TrackStatus(trackId, TrackPhase.Skipped, reason, doc is null ? "" : Rules.LanguageOf(doc), 0, doc?.Lines.Count ?? 0, 0, false);
            return;
        }
        string language = Rules.LanguageOf(doc!);
        s_jobTrack = trackId;
        Track.Value = new TrackStatus(trackId, TrackPhase.Working, SkipReason.None, language, 0, doc!.Lines.Count, 0, false);
        s_worker ??= new Worker();
        s_worker.Start(new JobRequest(trackId, track.Uri.Text, track.DurationMs, doc, language));
    }

    // ══ the worker thread ═════════════════════════════════════════════════════════════════════════════════════════

    sealed record JobRequest(string TrackId, string Uri, long DurationMs, Lyrics.Doc Source, string Language);

    sealed class Worker
    {
        readonly BlockingCollection<Action> _queue = new();
        readonly Thread _thread;
        LoadedModels? _models;
        string _loadedLanguages = "";
        CancellationTokenSource? _jobCts;
        volatile bool _loaded;

        public bool Loaded => _loaded;

        public Worker()
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "wavee-ai-lyrics", Priority = ThreadPriority.BelowNormal };
            _thread.Start();
        }

        void Run()
        {
            foreach (var cmd in _queue.GetConsumingEnumerable())
            {
                try { cmd(); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { Log.Warn("ai-lyrics", "worker command failed", ex); }
            }
            _models?.Dispose();
        }

        public void Load(IReadOnlyList<string> languages, bool reload)
        {
            string key = string.Join(",", languages);
            _queue.Add(() => DoLoad(languages, key, reload));
        }

        void DoLoad(IReadOnlyList<string> languages, string key, bool reload)
        {
            if (_models is not null && !reload && key == _loadedLanguages) { s_post(() => SetReady()); return; }
            _models?.Dispose(); _models = null; _loaded = false;
            var graphs = new List<string> { Path.Combine(ModelsDir, "separator.onnx") };
            foreach (var l in languages) foreach (var stg in Aligner.StageNames) graphs.Add(Path.Combine(ModelsDir, $"align-{l}.{stg}.onnx"));
            var weights = Rules.PrepareWeights(graphs, static p => File.Exists(p) ? new FileInfo(p).Length : 0);
            long weightTotal = 0; foreach (var w in weights) weightTotal += w;
            bool anyUncached = graphs.Exists(g => !File.Exists(LoadedModels.CtxPath(g)));
            if (anyUncached) s_post(() => Current.Value = Current.Peek() with { Phase = SetupPhase.Preparing, Prepare = new PrepareProgress(0, graphs.Count, 0, weightTotal, false, false) });
            var sw = Stopwatch.StartNew();
            try
            {
                _models = LoadedModels.Load(RuntimeDir, ModelsDir, languages, (done, total, cached) =>
                {
                    if (!anyUncached) return;
                    long wd = 0; for (int i = 0; i < done && i < weights.Length; i++) wd += weights[i];
                    s_post(() =>
                    {
                        if (Current.Peek().Phase == SetupPhase.Preparing)
                            Current.Value = Current.Peek() with { Prepare = new PrepareProgress(done, total, wd, weightTotal, false, false) };
                    });
                }, CancellationToken.None);
                _loadedLanguages = key;
                _loaded = true;
                Log.Info("ai-lyrics", $"ai.models.loaded ms={sw.ElapsedMilliseconds} graphs={graphs.Count} ort={_models.Ort.Version} compiled={anyUncached}");
                s_post(() =>
                {
                    bool wasPreparing = Current.Peek().Phase == SetupPhase.Preparing;
                    SetReady();
                    if (wasPreparing && Rules.ToastOnReady(SettingsCardVisible))
                        Notify.Say(Loc.Get(Strings.Settings.Lyrics.Ai.Toast.Ready), InfoBarSeverity.Success, dedupeKey: "ai-lyrics-ready");
                });
            }
            catch (NoNpuException ex)
            {
                Log.Warn("ai-lyrics", "ai.models.failed kind=NoNpuDevice " + ex.Message);
                s_post(() => Fail(SetupError.NoNpuDevice, ex.Message));
            }
            catch (OrtException ex)
            {
                var kind = ex.Message.Contains("onnxruntime.dll", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("ONNX Runtime", StringComparison.Ordinal)
                    ? SetupError.RuntimeLoad : SetupError.Compile;
                Log.Warn("ai-lyrics", $"ai.models.failed kind={kind} {ex.Message}");
                s_post(() => Fail(kind, ex.Message));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DllNotFoundException or BadImageFormatException)
            {
                Log.Warn("ai-lyrics", "ai.models.failed kind=RuntimeLoad", ex);
                s_post(() => Fail(SetupError.RuntimeLoad, ex.Message));
            }
        }

        static void SetReady()
        {
            var st = Current.Peek();
            if (!st.Enabled) return;
            Current.Value = st with { Phase = SetupPhase.Ready, Error = SetupError.None, ErrorDetail = "" };
        }

        public void Unload() => _queue.Add(() =>
        {
            _models?.Dispose(); _models = null; _loaded = false; _loadedLanguages = "";
            Log.Info("ai-lyrics", "ai.models.unloaded");
        });

        public void CancelJob()
        {
            try { _jobCts?.Cancel(); } catch (ObjectDisposedException) { }
        }

        public void Start(JobRequest req)
        {
            CancelJob();
            var cts = new CancellationTokenSource();
            _jobCts = cts;
            _queue.Add(() => RunJob(req, cts.Token));
        }

        void RunJob(JobRequest req, CancellationToken ct)
        {
            if (ct.IsCancellationRequested) return;
            ulong hash = Rules.SourceHash(req.Source.Lines);
            // 1. the results cache: a replay needs no NPU at all
            string resultPath = Path.Combine(ResultsDir, Results.FileName(req.TrackId, PackVersion));
            try
            {
                if (File.Exists(resultPath) && Results.TryDecode(File.ReadAllText(resultPath), out var cached)
                    && Results.Overlay(cached, req.Source, hash) is { } overlay)
                {
                    Log.Info("ai-lyrics", $"ai.results.hit track={req.TrackId}");
                    Publish(req, overlay, new JobProgress(req.Source.Lines.Count, req.Source.Lines.Count, 0, true), fromCache: true);
                    return;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warn("ai-lyrics", "results read failed", ex); }

            if (_models is null || !_models.Aligners.TryGetValue(req.Language, out var aligner))
            {
                s_post(() => Skip(req.TrackId, SkipReason.LanguageNotInstalled));
                return;
            }
            var sw = Stopwatch.StartNew();
            using var pcm = SpotifyPcm.Open(req.Uri, req.DurationMs, ct, out var fault);
            if (pcm is null)
            {
                Log.Info("ai-lyrics", $"ai.job.skip track={req.TrackId} reason=audio fault={fault}");
                s_post(() => Skip(req.TrackId, SkipReason.AudioUnavailable));
                return;
            }
            Log.Info("ai-lyrics", $"ai.job.start track={req.TrackId} language={req.Language} lines={req.Source.Lines.Count}");
            try
            {
                var job = new TrackJob(req.Source, req.Language, _models.Separator, aligner, pcm, PlayheadSeconds);
                var final = job.Run((doc, p) => Publish(req, doc, p, fromCache: false), ct);
                Directory.CreateDirectory(ResultsDir);
                string tmp = resultPath + ".tmp";
                File.WriteAllText(tmp, Results.Encode(final, hash, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
                File.Move(tmp, resultPath, overwrite: true);
                Log.Info("ai-lyrics", $"ai.job.done track={req.TrackId} ms={sw.ElapsedMilliseconds} separate={job.SeparateSeconds:0.0}s align={job.AlignSeconds:0.0}s");
            }
            catch (OperationCanceledException) { Log.Info("ai-lyrics", $"ai.job.cancel track={req.TrackId}"); }
            catch (Exception ex)
            {
                Log.Warn("ai-lyrics", $"ai.job.failed track={req.TrackId}", ex);
                s_post(() => Skip(req.TrackId, SkipReason.Failed));
            }
        }

        static void Publish(JobRequest req, Lyrics.Doc doc, JobProgress p, bool fromCache)
        {
            Lyrics.Store.Upgrade(doc);
            s_post(() =>
            {
                s_lastPublished[req.TrackId] = doc;
                if (s_lastPublished.Count > 32) s_lastPublished.Remove(s_lastPublished.Keys.First());
                if (Track.Peek().TrackId != req.TrackId && Track.Peek().TrackId.Length > 0) return;
                var phase = p.Final ? TrackPhase.Done : TrackPhase.Working;
                Track.Value = new TrackStatus(req.TrackId, phase, SkipReason.None, req.Language, p.LinesReady, p.LineCount, p.ProcessedSeconds, fromCache);
                if (p.Final && s_jobTrack == req.TrackId) { s_jobTrack = ""; s_lastJobEndMs = Environment.TickCount64; }
            });
        }

        static void Skip(string trackId, SkipReason reason)
        {
            if (s_jobTrack == trackId) { s_jobTrack = ""; s_lastJobEndMs = Environment.TickCount64; }
            if (Track.Peek().TrackId == trackId)
                Track.Value = Track.Peek() with { Phase = TrackPhase.Skipped, Reason = reason };
        }

        public void Shutdown()
        {
            CancelJob();
            _queue.CompleteAdding();
            if (!_thread.Join(2000)) Log.Warn("ai-lyrics", "the worker did not stop within 2 s");
        }
    }
}
