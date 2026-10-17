// ── Screens/Diagnostics.Pages.cs — the diagnostics pages' CORE hosts: the mount points and the documents they hand the view (T11c) ──────
//
// Role: CORE host (the picture — the pinned header over one scroll, the cards, rows, chips, status blocks and buttons, the capture roots and
//   their trees, and the five stateful cards — is the Common `Pages.Diagnostics.*` views: `Themes\Common\CommonDiagnostics*.cs`)
// Owner: S
// Wave: 6
// Spec: ch 27 §9.3; ch 27 W23 (runtime), W24 (Connect); docs/plans/wavee/realtime-capture-implementation.md §5.5 (capture);
//       docs/themes/plan/technical-plan.md §6.5 (T11c)
//
// THE RULE THIS FILE KEEPS: it decides and formats, it never lays out. Each page is a DOCUMENT (`DiagNode`s over strings): what a fact is, how it
// reads (a blank value prints "—", ch 27 W23: an unknown fact is a dash, never an empty column), the invariant number formats and the local-time
// stamps are here; the view draws them. The builders run INSIDE the view's render, so every `.Value` read subscribes the view exactly as the old page
// components' reads did.
//
// The runtime page — "Why is local playback not ready?" — is the provisioner's own computed report, verbatim: the page adds no detection of its own,
// which is why Refresh can re-read it with no side effect. Every section states its own absence in prose; only Locate / Candidates / Verify drop out
// without a report — the status, modules, updates, the buttons and the caption always render. The Connect page — "Who owns playback, and why does the
// bar say what it says" — is a direct read of the ownership fold, the last cluster, the last put-state round trip and the local host; it re-renders on
// the owner, the session phase and the traces' version — never on the position. The capture page — "What did the user do, and everything that
// happened because of it" — reads `Capture.Recent.cs`'s pure classes over `CaptureRecentStore`'s snapshot, NEVER the disk (§3.1's crash-safety story is a
// disk concern; the live view is a memory-only, header-only one, §5.5: "bodies stay opaque to the in-app view by design"); the route itself IS the
// subscription (zero cost when off).
//
// MASTER-DETAIL, NO NAVIGATION: "View tree" expands a root's causal tree IN PLACE, directly beneath its own row — the view owns the expanded id and hands
// it to the builder.

using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Media;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;
using Slots = FluentGpu.Theming.Slots;

namespace Wavee;

public static partial class Diagnostics
{
    // ══ 1. THE MOUNT POINTS ═════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The `playback-diagnostics` page (ch 27 W23).</summary>
    // MOUNT POINT (stage B contract) — registered by InstallPages
    public static Element RuntimePage() => Slots.View(ViewKeys.DiagnosticsPlayback, new DiagnosticsPageProps
    {
        Glyph = Icons.MusicNote, Title = Loc.Get(Strings.Playback.Runtime.DiagnosticsTitle), ScrollKey = "playback-diagnostics", Build = RuntimeNodes,
    });

    /// <summary>The `connect-diagnostics` page (ch 27 W24).</summary>
    // MOUNT POINT (stage B contract) — registered by InstallPages
    public static Element ConnectPage() => Slots.View(ViewKeys.DiagnosticsConnect, new DiagnosticsPageProps
    {
        Glyph = Icons.Devices, Title = Loc.Get(Strings.Nav.ConnectDiagnostics), ScrollKey = "connect-diagnostics", Build = ConnectNodes,
    });

    /// <summary>The `capture-diagnostics` page. Re-renders on its own 750 ms poll (only while capture is actually on — off, the interval is disabled and the
    /// snapshot is a fixed empty array) and on the capture toggle's own signal.</summary>
    // MOUNT POINT (stage B contract) — registered by InstallPages
    public static Element CapturePage() => Slots.View(ViewKeys.DiagnosticsCapture, new DiagnosticsPageProps
    {
        Glyph = Icons.Document, Title = Loc.Get(Strings.Nav.CaptureDiagnostics), ScrollKey = "capture-diagnostics", Build = CaptureNodes,
        PollWhile = RealtimeCaptureHost.Enabled, PollMs = CapturePollMs,
    });

    const float CapturePollMs = 750f, HealthPollMs = 1000f;

    static TimeSpan LocalOffset => TimeZoneInfo.Local.GetUtcOffset(DateTimeOffset.UtcNow);

    /// <summary>Label + value; a blank value prints "—".</summary>
    static DiagRow Fact(string label, string? value, bool wide = false) => new(label, OrDash(value), wide);

    static DiagCard Card(string title, params DiagNode[] items) => new(title, items);

    static DiagCard Card(string title, List<DiagNode> items) => new(title, items);

    // ══ 2. THE RUNTIME DIAGNOSTICS PAGE (ch 27 W23; 0.2.9 `PlaybackRuntimeDiagnosticsPage.cs`) ═════════════════════

    static IReadOnlyList<DiagNode> RuntimeNodes(long refresh)
    {
        var status = Setup.Runtime.Status.Value;
        var update = Notify.Update.Value;
        var diag = RuntimeReportSource?.Invoke();

        var body = new List<DiagNode>(12) { CompiledInCard(diag), StatusSection(status) };
        if (diag is not null)
        {
            body.Add(Card(Loc.Get(Strings.Diagnostics.Runtime.Locate),
                Fact(Loc.Get(Strings.Diagnostics.Runtime.Outcome), diag.LocateOutcome.ToString()),
                Fact(Loc.Get(Strings.Diagnostics.Runtime.Reason), diag.LocateReason)));
            body.Add(CandidatesSection(diag));
            body.Add(VerifySection(diag));
        }
        body.Add(ModulesSection(ModulesReportSource?.Invoke()));
        body.Add(UpdatesSection(update, NotesReportSource?.Invoke()));
        body.Add(StageCard());
        body.Add(new DiagHealth(s_readHealth, HealthPollMs));
        body.Add(WireCheckNode());
        body.Add(new DiagButtons(
        [
            new DiagButton(Loc.Get(Strings.Diagnostics.Runtime.Copy), Accent: true,
                Click: static () => Notify.Say(Loc.Get(Strings.Diagnostics.Runtime.Copied), InfoBarSeverity.Success),
                CopyText: static () => BuildRuntimeReport(RuntimeReportSource?.Invoke(), Setup.Runtime.Status.Peek(), DateTimeOffset.Now, Log.FilePath)),
            new DiagButton(Loc.Get(Strings.Settings.Diagnostics.OpenLogFolder), Click: static () => OpenFolder(Path.GetDirectoryName(Log.FilePath ?? ""))),
            new DiagButton(Loc.Get(Strings.Settings.Diagnostics.Refresh), Refresh: true),
        ]));
        body.Add(new DiagNote(Loc.Get(Strings.Diagnostics.Runtime.Caption)));
        return body;
    }

    /// <summary>THREE headline states: no report (Attention, "no playback session yet"), compiled in (Success), not
    /// compiled in (Critical, with the locate reason as the body).</summary>
    static DiagNode CompiledInCard(RuntimeDiagnostics? diag)
    {
        if (diag is null)
            return new DiagStatus(Icons.StatusInfo, DiagTone.Attention, Loc.Get(Strings.Diagnostics.Runtime.NoSessionTitle), Loc.Get(Strings.Diagnostics.Runtime.NoSessionBody));
        return diag.CompiledIn
            ? new DiagStatus(Icons.StatusSuccess, DiagTone.Success, Loc.Get(Strings.Diagnostics.Runtime.CompiledInTitle), Loc.Get(Strings.Diagnostics.Runtime.CompiledInBody))
            : new DiagStatus(Icons.StatusError, DiagTone.Critical, Loc.Get(Strings.Diagnostics.Runtime.NotCompiledTitle), diag.LocateReason ?? "");
    }

    static DiagNode StatusSection(Setup.RuntimeFacts s) => Card(Loc.Get(Strings.Diagnostics.Runtime.CurrentStatus),
        Fact(Loc.Get(Strings.Diagnostics.Runtime.Outcome), s.IsReady ? "Ready" : s.Issue.ToString()),
        Fact(Loc.Get(Strings.Diagnostics.Runtime.Pack), s.PackId),
        Fact(Loc.Get(Strings.Diagnostics.Runtime.SpotifyVersion), s.Version),
        Fact(Loc.Get(Strings.Diagnostics.Runtime.Architecture), s.Arch),
        Fact(Loc.Get(Strings.Diagnostics.Runtime.RuntimePath), s.Location),
        Fact(Loc.Get(Strings.Diagnostics.Runtime.SignatureTrust), s.Trust.ToString()));

    /// <summary>"Fullscreen &amp; visualizer" (fullscreen-flagship-implementation.md §4.14): the stage's mode/kind, the lease
    /// tier, the FFT cost per publish, the alignment applied to the last window (frames behind the newest rendered sample)
    /// and the endpoint's queue depth (diagnostic only — §2.7), the data source, and the publish count. A snapshot taken at
    /// render: <c>Stage.Diagnostics.Version</c> re-renders the page on every enter/exit/mode/pick/lease/source note, and
    /// Refresh re-reads the FFT figures.</summary>
    static DiagNode StageCard()
    {
        _ = Stage.Diagnostics.Version.Value;
        var (queueFrames, publishes) = Playback.Audio.SpectrumDiagnostics();
        Span<float> scratch = stackalloc float[Visualizer.Bands.Count];
        int n = Playback.Audio.CopySpectrum(scratch, out SpectrumInfo info);
        string delay = n == 0 ? queueFrames.ToString(CultureInfo.InvariantCulture) + " frames queued"
            : info.AlignFrames.ToString(CultureInfo.InvariantCulture) + " frames behind newest · " + queueFrames.ToString(CultureInfo.InvariantCulture) + " queued";
        return Card(Loc.Get(Strings.Stage.Diag.Title),
            Fact(Loc.Get(Strings.Stage.Diag.Mode), Stage.Diagnostics.IsOpen ? Stage.Diagnostics.LastMode + " · " + Stage.Diagnostics.LastKind : null),
            Fact(Loc.Get(Strings.Stage.Diag.Layout), Stage.Diagnostics.IsOpen ? Stage.Diagnostics.LastLayout.ToString() : null),
            Fact(Loc.Get(Strings.Stage.Diag.Leases), Stage.Diagnostics.LastTier.ToString()),
            Fact(Loc.Get(Strings.Stage.Diag.Fft), n == 0 ? null : info.FftMs.ToString("0.000", CultureInfo.InvariantCulture) + " ms"),
            Fact(Loc.Get(Strings.Stage.Diag.Delay), delay),
            Fact(Loc.Get(Strings.Stage.Diag.Source), Stage.Diagnostics.LastSource.ToString()),
            Fact(Loc.Get(Strings.Stage.Diag.Publishes), publishes.ToString(CultureInfo.InvariantCulture)));
    }

    static DiagNode CandidatesSection(RuntimeDiagnostics d)
    {
        string title = Loc.Get(Strings.Diagnostics.Runtime.Candidates);
        if (d.Candidates.Count == 0) return Card(title, new DiagBody(Loc.Get(Strings.Diagnostics.Runtime.NoCandidates)));
        var rows = new List<DiagNode>(d.Candidates.Count * 3);
        for (int i = 0; i < d.Candidates.Count; i++)
        {
            var c = d.Candidates[i];
            if (i > 0) rows.Add(new DiagSeparator(DiagGap.Normal));
            rows.Add(new DiagNamed(c.Source, [new DiagChip("Spotify.dll", c.DllPresent), new DiagChip("playplay-runtime.json", c.ManifestPresent)]));
            rows.Add(new DiagLine(string.IsNullOrWhiteSpace(c.RuntimeDir) ? Loc.Get(Strings.Diagnostics.Runtime.NoPath) : c.RuntimeDir, Primary: false));
        }
        return Card(title, rows);
    }

    static DiagNode VerifySection(RuntimeDiagnostics d)
    {
        string title = Loc.Get(Strings.Diagnostics.Runtime.Verify);
        if (d.VerifyNeverReached) return Card(title, new DiagBody(Loc.Get(Strings.Diagnostics.Runtime.VerifyNeverReached)));
        var rows = new List<DiagNode>(9)
        {
            Fact(Loc.Get(Strings.Diagnostics.Runtime.Outcome), d.VerifyOutcome?.ToString()),
            Fact(Loc.Get(Strings.Diagnostics.Runtime.Detail), d.VerifyDetail),
            Fact(Loc.Get(Strings.Diagnostics.Runtime.SignatureTrust), d.SignatureTrust?.ToString()),
        };
        if (d.Signature is { } sig)
        {
            rows.Add(Fact(Loc.Get(Strings.Diagnostics.Runtime.Publisher), sig.Subject));
            rows.Add(Fact(Loc.Get(Strings.Diagnostics.Runtime.Issuer), sig.Issuer));
            rows.Add(Fact(Loc.Get(Strings.Diagnostics.Runtime.Thumbprint), sig.Thumbprint));
            rows.Add(Fact(Loc.Get(Strings.Diagnostics.Runtime.Reason), sig.Reason));
            rows.Add(Fact(Loc.Get(Strings.Diagnostics.Runtime.Valid), Stamp(sig.ValidFrom.ToLocalTime()) + "  →  " + Stamp(sig.ValidTo.ToLocalTime())));
            rows.Add(Fact(Loc.Get(Strings.Diagnostics.Runtime.File), sig.FilePath));
        }
        return Card(title, rows);
    }

    /// <summary>One block per installed module, plus every refused directory with its reason. Retry for Faulted OR
    /// Crashed; the Requests / Latency / Last error / Status rows only when there is something behind them.</summary>
    static DiagNode ModulesSection(ModulesReport? host)
    {
        string title = Loc.Get(Strings.Diagnostics.Runtime.Modules);
        if (host is null) return Card(title, new DiagBody(Loc.Get(Strings.Diagnostics.Runtime.NoModuleHost)));
        var inv = CultureInfo.InvariantCulture;
        var rows = new List<DiagNode>(16);
        if (host.Installed.Count == 0) rows.Add(new DiagBody(Strings.Diagnostics.Runtime.NoModules(host.BundledRoot, host.UserRoot)));
        for (int i = 0; i < host.Installed.Count; i++)
        {
            var m = host.Installed[i];
            if (i > 0) rows.Add(new DiagSeparator(DiagGap.Normal));
            rows.Add(new DiagNamed(m.DisplayName,
            [
                new DiagChip(Loc.Get(m.Bundled ? Strings.Diagnostics.Runtime.Bundled : Strings.Diagnostics.Runtime.Installed), Present: true),
                new DiagChip(m.ProcessState ?? "Stopped", m.ProcessHealthy),
            ]));
            rows.Add(Fact(Loc.Get(Strings.Diagnostics.Runtime.ModuleId), m.Id));
            rows.Add(Fact(Loc.Get(Strings.Diagnostics.Runtime.ModuleVersion), Strings.Diagnostics.Runtime.ModuleVersionValue(m.Version, m.ProtocolVersion.ToString(inv))));
            rows.Add(Fact(Loc.Get(Strings.Diagnostics.Runtime.Publisher), m.Publisher));
            rows.Add(Fact(Loc.Get(Strings.Diagnostics.Runtime.Directory), m.Directory));
            rows.Add(Fact(Loc.Get(Strings.Diagnostics.Runtime.Process), m.ProcessId is { } pid
                ? Strings.Diagnostics.Runtime.ProcessPid(pid.ToString(inv)) : Loc.Get(Strings.Diagnostics.Runtime.NotRunning)));
            rows.Add(Fact(Loc.Get(Strings.Diagnostics.Runtime.Capabilities), m.Capabilities));
            if (m.HasStats)
            {
                rows.Add(Fact(Loc.Get(Strings.Diagnostics.Runtime.Requests),
                    Strings.Diagnostics.Runtime.RequestsValue(m.Requests.ToString(inv), m.Failures.ToString(inv), m.Restarts.ToString(inv))));
                rows.Add(Fact(Loc.Get(Strings.Diagnostics.Runtime.Latency), Strings.Diagnostics.Runtime.LatencyValue(m.P50Ms.ToString(inv), m.P95Ms.ToString(inv))));
                if (m.LastError is { Length: > 0 } err) rows.Add(Fact(Loc.Get(Strings.Diagnostics.Runtime.LastError), err));
            }
            if (m.StatusCard is { Length: > 0 } card) rows.Add(Fact(Loc.Get(Strings.Diagnostics.Runtime.StatusRow), card));
            if (m.CanRetry && host.Retry is { } retry)
            {
                string id = m.Id;
                rows.Add(new DiagButtons([new DiagButton(Loc.Get(Strings.Diagnostics.Runtime.Retry), Click: () => retry(id))]));
            }
        }
        if (host.Rejections.Count > 0)
        {
            rows.Add(new DiagSeparator(DiagGap.Wide));
            rows.Add(new DiagHeading(Loc.Get(Strings.Diagnostics.Runtime.Refused), Primary: false));
            foreach (var (dir, reason) in host.Rejections)
            {
                rows.Add(new DiagLine(dir, Primary: true));
                rows.Add(new DiagLine(reason, Primary: false));
            }
        }
        return Card(title, rows);
    }

    /// <summary>The update pillar's receipts (it lives here: the same "which feed, which build, what did the OS do"
    /// questions, one thing to copy). "Repair auto-update" downloads the .appinstaller through the browser; the hint
    /// says so, because the link alone looks like it did nothing.</summary>
    static DiagNode UpdatesSection(AppUpdateSnapshot snap, NotesReport? notes)
    {
        var me = Platform.Version;
        string feed = Update.Host.FeedUrl;
        var inv = CultureInfo.InvariantCulture;
        var rows = new List<DiagNode>(14)
        {
            Fact(Loc.Get(Strings.Diagnostics.Updates.Feed), feed),
            Fact(Loc.Get(Strings.Diagnostics.Updates.Channel), me.Channel),
            Fact(Loc.Get(Strings.Diagnostics.Updates.Version), me.Quad.Length > 0 ? me.Quad : me.SemVer),
            Fact(Loc.Get(Strings.Diagnostics.Updates.State), snap.State.ToString()),
            Fact(Loc.Get(Strings.Diagnostics.Updates.Associated), Loc.Get(snap.AutoUpdateAssociated ? Strings.Diagnostics.Updates.AssociatedYes : Strings.Diagnostics.Updates.AssociatedNo)),
            Fact(Loc.Get(Strings.Diagnostics.Updates.LastChecked), snap.LastCheckedMs > 0 ? Stamp(DateTimeOffset.FromUnixTimeMilliseconds(snap.LastCheckedMs).ToLocalTime()) : null),
            Fact(Loc.Get(Strings.Diagnostics.Updates.LastFailure), snap.Failure is { } fail
                ? fail.Kind + "  ·  0x" + fail.HResult.ToString("X8", inv) + (fail.Message.Length > 0 ? "  ·  " + fail.Message : "") : null),
        };
        if (notes is not null)
        {
            rows.Add(Fact(Loc.Get(Strings.Diagnostics.Updates.NotesSource), notes.LastSource));
            rows.Add(Fact(Loc.Get(Strings.Diagnostics.Updates.NotesCache), notes.CacheRoot));
            rows.Add(Fact(Loc.Get(Strings.Diagnostics.Updates.NotesEmbedded), notes.EmbeddedRoot));
            rows.Add(Fact(Loc.Get(Strings.Diagnostics.Updates.NotesFeed), notes.FeedRelease));
            rows.Add(Fact(Loc.Get(Strings.Diagnostics.Updates.NotesLastFetch), notes.LastFetchUtc is { } at
                ? Stamp(at.ToLocalTime()) + (notes.LastFetchUrl.Length > 0 ? "  ·  " + notes.LastFetchUrl : "") + (notes.LastFetchStatus.Length > 0 ? "  ·  " + notes.LastFetchStatus : "")
                : null));
            rows.Add(Fact(Loc.Get(Strings.Diagnostics.Updates.NotesBudget),
                Strings.Diagnostics.Updates.NotesBudgetValue(notes.IssueRequestsThisSession, notes.RateLimitRemaining)));
        }
        if (feed.Length > 0)
            rows.Add(new DiagLink(Loc.Get(Strings.Diagnostics.Updates.Repair), () => Update.Host.OpenUrl(feed), Loc.Get(Strings.Diagnostics.Updates.RepairHint)));
        return Card(Loc.Get(Strings.Diagnostics.Updates.Title), rows);
    }

    // ══ 2a. THE PLAYBACK HEALTH CARD (playback-smoothness-implementation.md §4.14; #167) ══════════════════════════
    //
    // "Is playback healthy right now?" without opening a log: the glitch ledger's verdict, the longest stall, whether the audio threads joined MMCSS, how
    // full the PCM ring is against its target, the device's own buffer and the decode speed. One `Playback.Audio.Metrics.Read()` per second (a
    // lock-free read of primitives, the view's poll), so it costs the page nothing while nothing changes. All text is a loc key (V-PA34): the verdict
    // arrives as a key string and the seek kind as an enum, and both map to the generated `Strings` constants here.

    static readonly Func<DiagCard> s_readHealth = ReadHealth;

    /// <summary>The "Playback health" card on the runtime-diagnostics page. A row whose fact does not exist yet (no live session, no decode yet, no
    /// seek yet) prints the page's usual "—".</summary>
    static DiagCard ReadHealth()
    {
        var m = Playback.Audio.Metrics.Read();
        var inv = CultureInfo.InvariantCulture;
        bool live = m.RingTargetMs > 0;   // a ring target exists only while a session is open
        return new DiagCard(Loc.Get(Strings.Diagnostics.Playback.Health),
        [
            Fact(Loc.Get(Strings.Diagnostics.Playback.VerdictLabel), VerdictText(m.GlitchVerdictKey)),
            Fact(Loc.Get(Strings.Diagnostics.Playback.Incidents), m.GlitchIncidents.ToString(inv)),
            Fact(Loc.Get(Strings.Diagnostics.Playback.LongestStall), Strings.Diagnostics.Playback.Ms(m.LongestStallMs)),
            Fact(Loc.Get(Strings.Diagnostics.Playback.Mmcss), Loc.Get(m.MmcssRegistered ? Strings.Diagnostics.Playback.MmcssOn : Strings.Diagnostics.Playback.MmcssOff)),
            Fact(Loc.Get(Strings.Diagnostics.Playback.RingFill), live ? Strings.Diagnostics.Playback.MsOfMs(m.RingFillMs, m.RingTargetMs) : null),
            Fact(Loc.Get(Strings.Diagnostics.Playback.DeviceBuffer), live ? Strings.Diagnostics.Playback.Ms(m.DevicePaddingMs) : null),
            Fact(Loc.Get(Strings.Diagnostics.Playback.Decode), m.DecodeXRealtime > 0f ? Strings.Diagnostics.Playback.XRealtime(m.DecodeXRealtime.ToString("0.0", inv)) : null),
            Fact(Loc.Get(Strings.Diagnostics.Playback.LastSeek), m.LastSeekLatencyMs >= 0 ? Strings.Diagnostics.Playback.MsKind(m.LastSeekLatencyMs, SeekKindText(m.LastSeekKind)) : null),
        ]);
    }

    /// <summary>The ledger's verdict key → its sentence. An empty key is "no verdict yet" (a dash); a key this card does
    /// not know falls through to a dynamic lookup, which renders loudly as "[key]" rather than hiding a drifted name.</summary>
    static string? VerdictText(string? key) => string.IsNullOrEmpty(key) ? null : key switch
    {
        "clean" => Loc.Get(Strings.Diagnostics.Playback.Verdict.Clean),
        "gcPauses" => Loc.Get(Strings.Diagnostics.Playback.Verdict.GcPauses),
        "byteStarved" => Loc.Get(Strings.Diagnostics.Playback.Verdict.ByteStarved),
        "producerStarved" => Loc.Get(Strings.Diagnostics.Playback.Verdict.ProducerStarved),
        "deviceLate" => Loc.Get(Strings.Diagnostics.Playback.Verdict.DeviceLate),
        _ => Loc.Get("diagnostics.playback.verdict." + key),
    };

    static string SeekKindText(Playback.Audio.SeekKind kind) => Loc.Get(kind switch
    {
        Playback.Audio.SeekKind.Far => Strings.Diagnostics.Playback.SeekKind.Far,
        Playback.Audio.SeekKind.Disk => Strings.Diagnostics.Playback.SeekKind.Disk,
        _ => Strings.Diagnostics.Playback.SeekKind.Ring,
    });

    // ══ 2b. THE PODCAST WIRE CHECK (podcast-show-rework-implementation.md §6.0; owner D1) ═════════════════════════
    //
    // A Diagnostics click that fires the podcast rework's 8 pathfinder ops + herodotus ListCurrentStates + the transcript GET, ONCE each, and lists each
    // one's status/bytes/root-found — the pre-flight the WinUI-era hashes need before wave P5 writes a single decoder line against a guess (a 400 means a
    // rotated hash; nothing else blocks). "Save captures" writes every raw body under Platform.LogFolder\podcast-wire\ — scrubbed of account data, those
    // files become the P5 decoder fixtures. The network walk itself (`Spotify.Api.PodcastQueries.RunWireCheckAsync`) runs on an api worker thread; the
    // view only awaits it.

    static readonly WireCheckHost s_wireHost = new();

    static DiagNode WireCheckNode() => new DiagWireCheck(
        Loc.Get(Strings.Diagnostics.PodcastWire.Title), Loc.Get(Strings.Diagnostics.PodcastWire.Body),
        Loc.Get(Strings.Diagnostics.PodcastWire.ShowUri), Loc.Get(Strings.Diagnostics.PodcastWire.EpisodeUri), Loc.Get(Strings.Diagnostics.PodcastWire.SaveCaptures),
        Loc.Get(Strings.Diagnostics.PodcastWire.Run), Loc.Get(Strings.Diagnostics.PodcastWire.Running),
        Loc.Get(Strings.Diagnostics.PodcastWire.ColOp), Loc.Get(Strings.Diagnostics.PodcastWire.ColStatus),
        Loc.Get(Strings.Diagnostics.PodcastWire.ColBytes), Loc.Get(Strings.Diagnostics.PodcastWire.ColRoot), s_wireHost);

    sealed class WireCheckHost : IDiagWireHost
    {
        public bool Live => Shell.Auth.Value == Shell.AuthState.Live;

        /// <summary>The now-playing episode and its show, if one is playing.</summary>
        public (string ShowUri, string EpisodeUri) Prefill()
        {
            var cur = Playback.Current.Peek();
            if (cur.Kind != EntityKind.Episode || cur.IsNone) return ("", "");
            var ep = new Episode(cur.Slot);
            if (!ep.IsValid) return ("", "");
            return (ep.ShowSlot > 0 ? new Show(ep.ShowSlot).Uri.Text : "", ep.Uri.Text);
        }

        public async Task<IReadOnlyList<DiagWireRow>> Run(string showUri, string episodeUri, bool saveCaptures)
        {
            IReadOnlyList<Spotify.Api.PodcastQueries.WireCheckResult> results;
            try
            {
                results = await Spotify.Api.PodcastQueries.RunWireCheckAsync(showUri, episodeUri, saveCaptures, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error("podcast", "wire check faulted", ex);
                results = [];
            }
            var inv = CultureInfo.InvariantCulture;
            var rows = new DiagWireRow[results.Count];
            for (int i = 0; i < rows.Length; i++)
            {
                var r = results[i];
                string status = r.Status == -1 ? "skip" : r.Status.ToString(inv);
                string root = r.RootFound ? (r.RootKey ?? "yes") : OrDash(r.Error);
                rows[i] = new DiagWireRow(r.Op, status, r.Bytes.ToString(inv), root, r.RootFound);
            }
            return rows;
        }

        public void Saved()
        {
            string dir = Path.Combine(Platform.LogFolder, Spotify.Api.PodcastQueries.CaptureFolderName);
            Notify.Say(Strings.Diagnostics.PodcastWire.Saved(dir), InfoBarSeverity.Success);
        }
    }

    // ══ 3. THE CONNECT DIAGNOSTICS PAGE (ch 27 W24; 0.2.9 `ConnectDiagnosticsPage.cs`) ═════════════════════════════

    /// <summary>Owner / cluster / host state their absence in prose; the two put-state cards never do (six dashed rows each, by design). Re-renders on the
    /// owner, the session phase and the traces' version — never on the position.</summary>
    static IReadOnlyList<DiagNode> ConnectNodes(long refresh)
    {
        _ = Playback.OwnerSignal.Value;
        _ = Playback.PhaseSignal.Value;
        _ = Connect.Version.Value;
        bool online = Spotify.Status.Value == Spotify.SessionPhase.Online;
        var snap = Playback.Snap();
        var offset = LocalOffset;

        return new List<DiagNode>(6)
        {
            online ? OwnerCard(in snap.Own, offset) : Card(Loc.Get(Strings.Diagnostics.Connect.Owner), new DiagBody(Loc.Get(Strings.Diagnostics.Connect.NoSession))),
            Connect.LastCluster is { } cluster ? ClusterCard(in cluster, offset) : Card(Loc.Get(Strings.Diagnostics.Connect.LastCluster), new DiagBody(Loc.Get(Strings.Diagnostics.Connect.NoCluster))),
            PutCard(Connect.LastPut, offset),
            EchoCard(Connect.LastEcho, offset),
            HostCard(in snap),
            new DiagNote(Loc.Get(Strings.Diagnostics.Connect.Caption)),
        };
    }

    static DiagRow Wide(string label, string? value) => Fact(label, value, wide: true);

    static DiagNode OwnerCard(in Playback.OwnerState o, TimeSpan offset)
    {
        var inv = CultureInfo.InvariantCulture;
        string state = o.Kind switch
        {
            Playback.Owner.Foreign => "Foreign · " + Playback.Devices.NameOf(o.Device),
            Playback.Owner.Nobody => "Nobody (" + o.Cause + ")",
            _ => "Us",
        };
        return Card(Loc.Get(Strings.Diagnostics.Connect.Owner),
            Wide(Loc.Get(Strings.Diagnostics.Connect.State), state),
            Wide(Loc.Get(Strings.Diagnostics.Connect.ClaimPhase), o.Claim.ToString()),
            Wide(Loc.Get(Strings.Diagnostics.Connect.ClaimId), o.ClaimId > 0 ? o.ClaimId.ToString(inv) : null),
            Wide(Loc.Get(Strings.Diagnostics.Connect.StartedAt), StampMs(o.ClaimStartedAtMs, offset)),
            Wide(Loc.Get(Strings.Diagnostics.Connect.FenceServerTs), StampMs(o.Fence, offset)),
            Wide(Loc.Get(Strings.Diagnostics.Connect.LastServerTs), StampMs(o.LastServerTs, offset)),
            Wide(Loc.Get(Strings.Diagnostics.Connect.LastSeen), o.LastSeenActive != 0
                ? Playback.Devices.NameOf(o.LastSeenActive) + " @ " + StampMs(o.LastSeenServerTs, offset) : null));
    }

    static DiagNode ClusterCard(in ClusterTrace c, TimeSpan offset)
    {
        var inv = CultureInfo.InvariantCulture;
        return Card(Loc.Get(Strings.Diagnostics.Connect.LastCluster),
            Wide(Loc.Get(Strings.Diagnostics.Connect.ActiveIdRaw), c.ActiveId),
            Wide(Loc.Get(Strings.Diagnostics.Connect.Origin), c.PutMsgId != 0 ? c.Origin + " #" + c.PutMsgId.ToString(inv) : c.Origin),
            Wide(Loc.Get(Strings.Diagnostics.Connect.UpdateReason), c.UpdateReason.ToString(inv)),
            Wide(Loc.Get(Strings.Diagnostics.Connect.ChangedDevices), c.ChangedDevices),
            Wide(Loc.Get(Strings.Diagnostics.Connect.ServerTs), StampMs(c.ServerTsMs, offset)),
            Wide(Loc.Get(Strings.Diagnostics.Connect.TrackUri), c.TrackUri),
            Wide(Loc.Get(Strings.Diagnostics.Connect.PlayingPaused), (c.IsPlaying ? "playing" : "not playing") + " / " + (c.IsPaused ? "paused" : "not paused")),
            Wide(Loc.Get(Strings.Diagnostics.Connect.Position), c.PositionAsOfMs.ToString(inv) + " ms"));
    }

    static DiagNode PutCard(PutTrace p, TimeSpan offset) => Card(Loc.Get(Strings.Diagnostics.Connect.LastPut),
        Wide(Loc.Get(Strings.Diagnostics.Connect.MsgId), p.MsgId == 0 ? null : p.MsgId.ToString(CultureInfo.InvariantCulture)),
        Wide(Loc.Get(Strings.Diagnostics.Connect.Reason), p.Reason),
        Wide("is_active", p.IsActive.ToString()),
        Wide("started_playing_at", StampMs(p.StartedPlayingAtMs, offset)),
        Wide("has_been_playing_for", p.HasBeenPlayingForMs.ToString(CultureInfo.InvariantCulture) + " ms"),
        Wide(Loc.Get(Strings.Diagnostics.Connect.SentAt), StampMs(p.AtMs, offset)));

    static DiagNode EchoCard(EchoTrace e, TimeSpan offset) => Card(Loc.Get(Strings.Diagnostics.Connect.LastEcho),
        Wide(Loc.Get(Strings.Diagnostics.Connect.MsgId), e.MsgId == 0 ? null : e.MsgId.ToString(CultureInfo.InvariantCulture)),
        Wide(Loc.Get(Strings.Diagnostics.Connect.ActiveId), e.ActiveId),
        Wide(Loc.Get(Strings.Diagnostics.Connect.ServerTs), StampMs(e.ServerTs, offset)),
        Wide(Loc.Get(Strings.Diagnostics.Connect.ClusterStartedAt), StampMs(e.ClusterStartedAtMs, offset)),
        Wide(Loc.Get(Strings.Diagnostics.Connect.Adopted), e.Adopted.ToString()),
        Wide(Loc.Get(Strings.Diagnostics.Connect.ReceivedAt), StampMs(e.AtMs, offset)));

    /// <summary>The local host: absent (prose) while a foreign device owns playback or nothing has loaded.</summary>
    static DiagNode HostCard(in Playback.State s)
    {
        string title = Loc.Get(Strings.Diagnostics.Connect.Host);
        if (!s.HasCurrent || !s.RoutesLocal) return Card(title, new DiagBody(Loc.Get(Strings.Diagnostics.Connect.NoHost)));
        return Card(title,
            Wide(Loc.Get(Strings.Diagnostics.Connect.Playing), s.IsPlaying.ToString()),
            Wide(Loc.Get(Strings.Diagnostics.Connect.ClockValid), (s.PosQpc > 0).ToString()),
            Wide(Loc.Get(Strings.Diagnostics.Connect.Position), s.PosMs.ToString(CultureInfo.InvariantCulture) + " ms"));
    }

    // ══ 4. THE REALTIME CAPTURE PAGE (docs/plans/wavee/realtime-capture-implementation.md §5.5, §8 unit 6) ═════════

    const int MaxRoots = 20;
    const int MaxAnomalies = 10;

    /// <summary>The expanded root's id is the view's (it owns the toggle); the builder reads the snapshot and answers the tree for that root only.
    /// THE SCROLL CARD rides at the foot of this page (`Diagnostics.Scroll.cs`): the engine scroll probe's level, the feel profile and the CSV export —
    /// independent of the capture toggle. THE TILES CARD rides right after it (`Diagnostics.Tiles.cs`): the always-on retained-tile census and the GPU
    /// pass timing toggle — also independent of the capture toggle. THE EVIDENCE CARD (`Diagnostics.Evidence.cs`) closes it: the stale-tile /
    /// exposed-missing / scratch invariants, the pixel query over the latest composite, and the evidence bundle export.</summary>
    static IReadOnlyList<DiagNode> CaptureNodes(long expandedRoot)
    {
        bool on = RealtimeCaptureHost.Enabled.Value;
        var snapshot = CaptureRecentStore.Instance.Snapshot();
        var roots = CaptureRootGrouping.RecentRoots(snapshot, MaxRoots);
        var anomalies = CaptureAnomalyScanner.Scan(snapshot);

        return new List<DiagNode>(9)
        {
            CaptureStatus(on),
            AnomaliesCard(anomalies),
            RootsCard(roots, snapshot, expandedRoot),
            new DiagButtons(
            [
                new DiagButton(Loc.Get(Strings.Settings.Privacy.Tools.OpenFolder), Click: static () => OpenFolder(Path.Combine(Platform.LogFolder, "capture"))),
                new DiagButton(Loc.Get(Strings.Settings.Diagnostics.Refresh), Refresh: true),
            ]),
            new DiagNote(Loc.Get(Strings.Diagnostics.Capture.Caption)),
            // The developer page's other always-available instruments: the engine scroll probe (Diagnostics.Scroll.cs), the retained-tile census + GPU
            // pass timing (Diagnostics.Tiles.cs) and the evidence card (Diagnostics.Evidence.cs).
            ScrollNode(),
            TilesNode(),
            EvidenceNode(),
        };
    }

    // ── the status line ──────────────────────────────────────────────────────────────────────────────────────────

    static DiagNode CaptureStatus(bool on) => new DiagStatus(
        on ? Icons.StatusSuccess : Icons.StatusInfo,
        on ? DiagTone.Success : DiagTone.Muted,
        Loc.Get(on ? Strings.Diagnostics.Capture.Recording : Strings.Diagnostics.Capture.Off),
        Loc.Get(on ? Strings.Diagnostics.Capture.RecordingBody : Strings.Diagnostics.Capture.OffBody));

    // ── the Anomalies card (§5.5: capped at 10, newest first) ───────────────────────────────────────────────────

    static DiagNode AnomaliesCard(IReadOnlyList<CaptureAnomaly> anomalies)
    {
        string title = Loc.Get(Strings.Diagnostics.Capture.Anomalies);
        if (anomalies.Count == 0) return Card(title, new DiagBody(Loc.Get(Strings.Diagnostics.Capture.NoAnomalies)));

        int shown = Math.Min(anomalies.Count, MaxAnomalies);
        var rows = new List<DiagNode>(shown * 2);
        for (int i = 0; i < shown; i++)
        {
            if (i > 0) rows.Add(new DiagSeparator(DiagGap.Tight));
            var a = anomalies[i];
            rows.Add(new DiagAnomaly(AnomalyLabel(a.Kind), a.Summary ?? "", "#" + a.RootId.ToString(CultureInfo.InvariantCulture)));
        }
        return Card(title, rows);
    }

    static string AnomalyLabel(CaptureAnomalyKind kind) => Loc.Get(kind switch
    {
        CaptureAnomalyKind.EchoMissing => Strings.Diagnostics.Capture.EchoMissing,
        CaptureAnomalyKind.NonSuccessStatus => Strings.Diagnostics.Capture.NonSuccessStatus,
        CaptureAnomalyKind.DecodeFailed => Strings.Diagnostics.Capture.DecodeFailed,
        _ => Strings.Diagnostics.Capture.FrameIgnored,
    });

    // ── the recent causal roots ("last stories" — §5.5) ─────────────────────────────────────────────────────────

    static DiagNode RootsCard(IReadOnlyList<CaptureRootSummary> roots, CaptureEvent[] snapshot, long expandedRoot)
    {
        string title = Loc.Get(Strings.Diagnostics.Capture.RecentRoots);
        if (roots.Count == 0) return Card(title, new DiagBody(Loc.Get(Strings.Diagnostics.Capture.NoRoots)));

        var rows = new List<DiagNode>(roots.Count * 2);
        foreach (var r in roots)
        {
            if (rows.Count > 0) rows.Add(new DiagSeparator(DiagGap.Tight));
            bool expanded = expandedRoot == r.RootId;
            string label = r.Root.Kind + (string.IsNullOrEmpty(r.Root.Fields.A) ? "" : "  " + r.Root.Fields.A);
            rows.Add(new DiagRoot(r.RootId, RootTone(r.Status), LogView.FormatTime(r.Root.UnixMs, LocalOffset), label,
                Strings.Diagnostics.Capture.EffectCount(r.EffectCount), expanded,
                Loc.Get(expanded ? Strings.Diagnostics.Capture.HideTree : Strings.Diagnostics.Capture.ViewTree),
                expanded ? TreeRows(r.RootId, snapshot) : null, Loc.Get(Strings.Diagnostics.Capture.EmptyTree)));
        }
        return Card(title, rows);
    }

    static DiagTone RootTone(CaptureRootStatus status) => status switch
    {
        CaptureRootStatus.Red => DiagTone.Critical,
        CaptureRootStatus.Amber => DiagTone.Caution,
        _ => DiagTone.Success,
    };

    /// <summary>The expanded tree — input → decisions → requests → echoes → UI outcomes → errors, in causal + time order (§2.5). Header fields only; no
    /// payload is ever read here.</summary>
    static IReadOnlyList<DiagTreeRow> TreeRows(long rootId, CaptureEvent[] snapshot)
    {
        var underRoot = new List<CaptureEvent>();
        foreach (var e in snapshot)
            if (e.RootId == rootId) underRoot.Add(e);

        var tree = CaptureTreeBuilder.Build(rootId, underRoot);
        var rows = new DiagTreeRow[tree.Count];
        for (int i = 0; i < rows.Length; i++)
        {
            var row = tree[i];
            var e = row.Event;
            string suffix = e.Phase == CapturePhase.End ? " ✓" : "";
            string label = e.Kind + suffix + (string.IsNullOrEmpty(e.Fields.A) ? "" : "  " + e.Fields.A)
                + (string.IsNullOrEmpty(e.Fields.B) ? "" : " → " + e.Fields.B);
            rows[i] = new DiagTreeRow(row.Depth, LogView.FormatTime(e.UnixMs, LocalOffset), label);
        }
        return rows;
    }
}
