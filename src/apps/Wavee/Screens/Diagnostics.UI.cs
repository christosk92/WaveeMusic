// ── Screens/Diagnostics.UI.cs ──────────────────────────────────────────────────────────────────────────────────────
// the diagnostics install (the orchestrator's one call), the FPS rule, and the lyrics inspector dialog (≈600, moved here by
// ch 22 §9 (d), A14). The diagnostics pages (runtime, Connect, capture — the API console page DELETED, §9.6 Q7, 2026-09-12)
// are core hosts in `Diagnostics.Pages.cs` over the Common `Pages.Diagnostics.*` views (`Themes\Common\CommonDiagnostics*.cs`,
// T11c); the log viewer is its own route (`LogsPage.Host.cs` / `LogsPage.cs` over the Common `Pages.Logs` view,
// privacy-diagnostics-tab-implementation.md §3.2); `InstallPages` registers them all.
//
// Role: UI
// Owner: S
// Wave: 6
// Budget: 2300 lines
// Spec: ch 27 §9.3 (1,700) + ch 22 §9 (d) (+600) — the 1,700 figure predates Q7 and is not reduced here; ch 27 owns the
//       precise re-split. Wireframes: ch 27 W21-W24, W26, W28; ch 22 W17-W19b. Gaps G-094, G-153, G-197, G-218.
//
// THE RULE THIS FILE KEEPS: it lays out and binds, it never decides. The filter/group/cap of the log view, the level fold,
// the report text, the frame watch and the inspector's anomaly inks are `Diagnostics.cs` / `Platform.Settings.cs` (CORE,
// tested); the disk and the process are `Diagnostics.Host.cs`.
//
// NOTHING HERE READS A CLOCK PER FRAME (ch 27 N12). No surface here carries a context menu, a drag or a shortcut (ch 27 §6).
//
// PROPS FREEZE AT MOUNT: every control whose interesting fields freeze is REMOUNTED through a `Key` that changes exactly
// when its frozen input does, and every toggle that triggers such a remount runs through `UsePost` so the click finishes
// before its node is torn down.

using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Media;
using FluentGpu.Signals;

namespace Wavee;

public static partial class Diagnostics
{
    // ══ 1. THE INSTALL (the orchestrator's one call) ════════════════════════════════════════════════════════════════

    static int s_pagesInstalled, s_servicesInstalled;

    /// <summary>The page half: the diagnostics pages (runtime, Connect, capture), the log viewer's own route (`logs`) and the
    /// seams the Wave-6 screens left for owner S ("Send event", the report dialog's diagnostics text / past sessions). Called
    /// by the composition root after <c>Shell.InstallUi()</c> and <c>Settings.InstallScreens()</c>, in every boot profile
    /// (the headless shell host mounts these pages too). Idempotent.</summary>
    public static void InstallPages()
    {
        if (Interlocked.Exchange(ref s_pagesInstalled, 1) != 0) return;
        Shell.SetPage(Shell.RouteKind.PlaybackDiagnostics, static (in Shell.Route _) => RuntimePage());
        Shell.SetPage(Shell.RouteKind.ConnectDiagnostics, static (in Shell.Route _) => ConnectPage());
        Shell.SetPage(Shell.RouteKind.CaptureDiagnostics, static (in Shell.Route _) => CapturePage());
        Shell.SetPage(Shell.RouteKind.Logs, static (in Shell.Route _) => LogsPage.Page());
        Settings.SendTestEvent = static topic => ToTestEvent(NotificationSimulator.Send(topic));
        Feedback.DiagnosticsText = static () => InfoText();
        Feedback.PastSessionLog = static key => PastSessionLines(key);
    }

    /// <summary>The service half, GUI launches only (never the headless arm or the shell test host). Called ONCE by the
    /// composition root right after <see cref="InstallPages"/>: AFTER <c>Shell.InstallMarshallers()</c> (the network host
    /// posts through <c>Playback.ToUi</c>) and BEFORE <c>Update.Host.Start</c> (<c>Crash.Host.BeginGuiRun</c> reads
    /// <c>app.lastRunVersion</c> before the updater rewrites it) and <c>Shell.Run()</c> (the pre-loop hook must be set
    /// before the window).
    /// <list type="bullet">
    /// <item>the crash pipeline's run marker + boot-failure streak + this-launch bundle latch (<c>Crash.Host.BeginGuiRun</c>);</item>
    /// <item>the frame watch as <c>Shell.RouteNoted</c>'s consumer (G-197);</item>
    /// <item>the NLM cost host; the pre-loop hook (ambient power + the GUI probe arms);</item>
    /// <item>the updater's and the stream's metered reads.</item>
    /// </list></summary>
    public static void InstallServices()
    {
        if (Interlocked.Exchange(ref s_servicesInstalled, 1) != 0) return;
        // crash-diagnostics-implementation.md §I "WP-B": the same versionChanged input the old Diagnostics.BeginGuiRun
        // computed — an update deployment killing every open window is not evidence of a boot-time crash loop.
        bool versionChanged = Array.IndexOf(Platform.Args.Argv, Platform.RelaunchedAfterUpdateFlag) >= 0
            || Platform.Settings.Get(Platform.Keys.LastRunVersion) != Platform.Version.LastRunKey;
        Crash.Host.BeginGuiRun(Platform.Settings, versionChanged);

        Shell.RouteNoted += static route => { NavigationFrameWatch.NoteRoute(route); Crash.Host.NoteContextChanged(); };
        NavigationFrameWatch.Attach();

        Platform.Network.Install(Playback.ToUi);
        Probe.InstallGuiArms(Platform.Args.Argv);

        Update.Host.IsMetered = static () => Platform.Network.IsMetered;
        // The stream sizes its read-ahead down on a metered link (ch 29 W9's invisible effect): a volatile snapshot read,
        // any thread. The headless arm leaves the seam null = unmetered, which is what an unknown cost is.
        Spotify.Audio.MeteredConnection = static () => Platform.Network.IsMetered;
    }

    /// <summary>The exit tail (before <c>Platform.Shutdown()</c>): the last frame/memory lines, the clean run marker, the
    /// cost subscriptions and the power poll.</summary>
    public static void Shutdown()
    {
        NavigationFrameWatch.EndSession();
        MemorySampler.SampleProcessEnd();
        try { RunMarker.End(Platform.Settings); } catch { }
        Platform.Network.Shutdown();
        Platform.DetachAmbientPower();
    }

    static Settings.TestEventResult ToTestEvent(SimResult r) => new(r.Outcome switch
    {
        SimOutcome.Dropped => Settings.TestEventOutcome.Dropped,
        SimOutcome.RecordedInApp => Settings.TestEventOutcome.RecordedInApp,
        SimOutcome.Banner => Settings.TestEventOutcome.Banner,
        SimOutcome.BannerQuietDeferred => Settings.TestEventOutcome.BannerQuietDeferred,
        SimOutcome.Scheduled => Settings.TestEventOutcome.Scheduled,
        SimOutcome.NeverBanners => Settings.TestEventOutcome.NeverBanners,
        _ => Settings.TestEventOutcome.Unavailable,
    }, r.At);

    /// <summary>"Copy diagnostics info" / the report dialog's diagnostics block (0.2.9 <c>SettingsPage.DiagInfoText</c>):
    /// the build stamp, OS, engine, GPU, data folder, feed and the runtime's state. UI thread.</summary>
    public static string InfoText()
    {
        string arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        var tier = GpuProfile.Tier switch { GpuPowerTier.Weak => Settings.GpuTier.Weak, GpuPowerTier.Strong => Settings.GpuTier.Strong, _ => Settings.GpuTier.Unknown };
        var runtime = Setup.Runtime.Status.Peek();
        return Platform.Version.OneLine(arch)
            + "\nOS: " + System.Runtime.InteropServices.RuntimeInformation.OSDescription
            + "\nEngine: FluentGpu · .NET " + Environment.Version
            + "\nGPU: " + Settings.Receipts.GpuLine(GpuProfile.AdapterName, tier, GpuProfile.IsSoftwareAdapter)
            + "\nData folder: " + Platform.LocalFolder
            + "\nFeed: " + OrDash(Update.Host.FeedUrl)
            + "\nPlayback runtime: " + (runtime.IsReady ? "Ready" : runtime.Issue.ToString()) + "\n";
    }

    /// <summary>The report dialog's past-session quote: the session <see cref="WaveeLogSessions.KeyOf"/> named, or the newest
    /// past session for a null key. Off the UI thread.</summary>
    static IReadOnlyList<string>? PastSessionLines(string? key)
    {
        var sessions = WaveeLogSessions.ListPastSessions(Log.BasePath, Environment.ProcessId).Sessions;
        return WaveeLogSessions.Find(sessions, key) is { } info ? WaveeLogSessions.RawLines(info, WaveeLogSessions.ReadSharedLines) : null;
    }

    // ══ 2. THE FPS RULE ═════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Is the FPS HUD (ch 27 W26) on? Developer mode AND the overlay toggle, never in presentation mode (the Store shot).
    /// The HUD itself is the Common <c>Shell.Fps</c> view (T11c, <c>Themes\Common\CommonFps.cs</c>): the shell frame mounts it and binds
    /// it to this rule, so the pill exists only while the rule holds. The reads are tracked (a <c>Flow.Show</c> predicate).</summary>
    public static bool FpsOverlayOn()
        => Platform.Developer.ShowsFpsOverlay(Platform.Developer.Enabled.Value && !StoreShot.Presenting.Value,
            Platform.Developer.FpsOverlay.Value);

    // ══ 3. THE LYRICS SOURCE INSPECTOR (A14 — moved out of the lyrics files by ch 22 §9 (d); ch 22 W17-W19b) ═══════════
    //
    // The answer to "these lyrics are out of sync — is that the provider's timings or our parser?". For the playing track
    // it shows (1) PROVIDERS: every source that ran, whether it was chosen and why not; (2) RAW: each payload exactly as
    // it arrived, copyable per payload; (3) PARSED: each candidate's document and the FINAL one the view got, with the
    // timing anomalies that matter for desync coloured. Everything it reads is `Lyrics.Diag`'s store; the one thing it
    // DOES is "Re-fetch from providers", because a cached answer carries no payload. A developer surface on a theme
    // PLATE (a ContentDialog card): theme text rungs and a monospace face, never the reading surface's ink. No context
    // menu, no drag, no shortcut. The pure half (the text, the inks, the cells) is `LyricsReport` in Diagnostics.cs; the
    // bundle writer is `SaveLyricsBundle` in Diagnostics.Host.cs.

    /// <summary>The inspector's door: <see cref="Open"/> (the dialog). The panel's lyrics header draws the developer-mode <c>&lt;/&gt;</c>
    /// glyph that opens it (`Panel.LyricsHeader`; <c>RailWiring</c> gates it on <see cref="StoreShot.DeveloperSurfaces"/>).</summary>
    public static class LyricsInspector
    {
        /// <summary>The WinUI ContentDialog maximum — a raw payload wants every DIP of it (ch 22 §3).</summary>
        public const float DialogWidth = 548f;

        /// <summary>Open the inspector for <paramref name="trackId"/> (the base62 id <c>Lyrics.Store.IdOf</c> keys the
        /// diagnostics store with; "" = nothing playing). Reports only: Close is the one command and the default.</summary>
        public static void Open(IOverlayService overlay, string trackId) => ContentDialog.Show(overlay, d =>
        {
            d.Title = Loc.Get(Strings.Player.LyricsInspector);
            d.PrimaryText = "";                        // "" hides the primary (null would show the localized OK)
            d.CloseText = Loc.Get(Strings.Common.Close);
            d.DefaultButton = ContentDialog.DefaultBtn.Close;
            d.Content = Embed.Comp(() => new LyricsInspectorBody(trackId));
            OverlayFit.Dialog(d, DialogWidth);
        });

        /// <summary>The playing row's diagnostics key, or "" when nothing (or an episode) is playing. A <c>Peek</c>:
        /// called at click time, never in a render.</summary>
        public static string CurrentTrackId()
        {
            var row = Playback.Current.Peek();
            return row.Kind == EntityKind.Track ? Lyrics.Store.IdOf(new Track(row.Slot)) : "";
        }

        /// <summary>The localized reference line ("Reference: Spotify" / "Reference: consensus of kugou, qq, netease" /
        /// "No reference") for a report summary; "" when the summary names no reference (a disk hit, a no-match
        /// search). The parse is <see cref="LyricsReport.ReferenceOf"/>; the dialog and the in-lyrics debug panel share
        /// this.</summary>
        public static string ReferenceLabel(string? summary)
        {
            var r = LyricsReport.ReferenceOf(summary);
            return r.Kind switch
            {
                LyricsReport.ReferenceKind.Source => Strings.Diagnostics.Inspector.ReferenceSource(LyricsReport.SourceName(r.Sources[0])),
                LyricsReport.ReferenceKind.Consensus => Strings.Diagnostics.Inspector.ReferenceConsensus(LyricsReport.JoinSources(r.Sources)),
                LyricsReport.ReferenceKind.None => Loc.Get(Strings.Diagnostics.Inspector.ReferenceNone),
                _ => "",
            };
        }
    }

    /// <summary>The dialog body (548 card, 492 content). The track id is the dialog's IDENTITY — one open, one track —
    /// so it is a mount seed; everything that changes is a signal on this instance.</summary>
    sealed class LyricsInspectorBody(string trackId) : Component
    {
        const float ContentW = LyricsInspector.DialogWidth - 56f;   // 548 − 2×24 card padding − the scrollbar lane = 492
        const string Mono = "Cascadia Code";

        // The inspector's own severity inks (0.2.9's, theme-invariant on a theme plate — they read on both).
        static readonly ColorF Good = new(0.30f, 0.78f, 0.45f, 1f);
        static readonly ColorF Warn = new(0.92f, 0.70f, 0.25f, 1f);
        static readonly ColorF Bad = new(0.90f, 0.35f, 0.38f, 1f);
        static readonly ColorF Dim = new(0.40f, 0.42f, 0.50f, 1f);
        static readonly ColorF Grey = new(0.55f, 0.57f, 0.62f, 1f);

        static string[]? s_tabs;   // the launch locale is fixed for the process, so the labels resolve once

        readonly string _trackId = trackId;
        readonly Signal<int> _tab = new(0);           // 0 providers · 1 raw · 2 parsed
        readonly Signal<string> _focus = new("");     // the provider the raw/parsed tabs show ("" = the final document)
        readonly Signal<int> _epoch = new(0);         // bump to re-read the static diagnostics store
        readonly Signal<bool> _syllables = new(false);
        readonly Signal<bool> _refetching = new(false);
        readonly Signal<bool> _saving = new(false);
        readonly Signal<string> _status = new("");    // the amber status line; "" = absent
        readonly Signal<long> _aiBytes = new(LyricsReport.AiResultUnchecked);   // the AI card's off-thread stat of the saved result

        public override Element Render()
        {
            var hooks = UseContext(InputHooks.Current);
            var post = UsePost();
            _ = _epoch.Value;   // Refresh and a finished re-fetch bump it: re-read the store

            // W19b NO TRACK: the note is the ONLY child — no toolbar, no tabs.
            if (_trackId.Length == 0) return Body(Note(Loc.Get(Strings.Diagnostics.Inspector.NoTrack)));

            var report = Lyrics.Diag.ForTrack(_trackId);
            var insp = Lyrics.Diag.InspectionFor(_trackId);
            Element tab = _tab.Value switch
            {
                1 => RawTab(hooks, insp),
                2 => ParsedTab(hooks, insp),
                _ => ProvidersTab(report, insp),
            };
            s_tabs ??=
            [
                Loc.Get(Strings.Diagnostics.Inspector.TabProviders),
                Loc.Get(Strings.Diagnostics.Inspector.TabRaw),
                Loc.Get(Strings.Diagnostics.Inspector.TabParsed),
            ];
            return Body(Toolbar(hooks, post, report, insp), SelectorBar.Create(s_tabs, _tab), tab);
        }

        static BoxEl Body(params Element[] kids) => new() { Direction = 1, Gap = 12f, Width = ContentW, Children = kids };

        // ── the toolbar ──────────────────────────────────────────────────────────────────────────────────────────────

        Element Toolbar(InputHooks hooks, Action<Action> post, Lyrics.SearchReport? report, Lyrics.Inspection? insp)
        {
            bool refetching = _refetching.Value, busy = refetching || _saving.Value;
            var kids = new List<Element>(3)
            {
                Row(8f,
                    Standard(Loc.Get(Strings.Diagnostics.Inspector.CopyReport), () =>
                    {
                        hooks.Clipboard?.SetText(LyricsReport.BuildReport(_trackId, report, insp, AiFactsNow()));
                        Notify.Say(Loc.Get(Strings.Lyrics.Inspector.ReportCopied), InfoBarSeverity.Success);
                    }),
                    // The report is what fits in a clipboard; the BUNDLE is what an investigation needs — every payload
                    // byte-for-byte and every parse as a TSV, in a folder that can be diffed or replayed as a fixture.
                    Standard(Loc.Get(busy ? Strings.Diagnostics.Inspector.Working : Strings.Diagnostics.Inspector.SaveBundle),
                        () => SaveBundle(post, report, insp), enabled: !busy),
                    Standard(Loc.Get(Strings.Diagnostics.Inspector.Refresh), () => _epoch.Value = _epoch.Peek() + 1),
                    FluentGpu.Controls.Button.Accent(
                        Loc.Get(refetching ? Strings.Diagnostics.Inspector.Refetching : Strings.Diagnostics.Inspector.Refetch),
                        () => Refetch(post, thenSave: false), isEnabled: !busy)),
                Caption(Loc.Get(Strings.Lyrics.Inspector.RefetchNote)),
            };
            if (_status.Value is { Length: > 0 } status)
                kids.Add(new TextEl(status) { Size = 12f, LineHeight = 16f, Color = Warn, Wrap = TextWrap.Wrap });
            return new BoxEl { Direction = 1, Gap = 6f, Children = kids.ToArray() };
        }

        /// <summary>Save, fetching FIRST when there is nothing to save: a cache hit carries no payload, so a bundle written
        /// from one is the useless artifact this button exists to avoid — chain it, never hand over an empty folder.</summary>
        void SaveBundle(Action<Action> post, Lyrics.SearchReport? report, Lyrics.Inspection? insp)
        {
            if (insp is null || insp.Raw.Count == 0) Refetch(post, thenSave: true);
            else WriteBundle(post, report, insp);
        }

        void WriteBundle(Action<Action> post, Lyrics.SearchReport? report, Lyrics.Inspection? insp)
        {
            if (_saving.Peek()) return;
            _saving.Value = true;
            string trackId = _trackId;
            int payloads = insp?.Raw.Count ?? 0;
            var ai = AiFactsNow();
            _ = Task.Run(() =>
            {
                string? folder = SaveLyricsBundle(trackId, report, insp, out string error, ai);
                post(() => BundleLanded(folder, error, payloads));
            });
        }

        void BundleLanded(string? folder, string error, int payloads)
        {
            _saving.Value = false;
            if (folder is null)
            {
                _status.Value = error.Length > 0
                    ? Strings.Diagnostics.Inspector.SaveFailed(error)
                    : Loc.Get(Strings.Diagnostics.Inspector.NothingToSave);
                return;
            }
            // Say so LOUDLY rather than handing over a folder that silently holds no evidence.
            _status.Value = payloads == 0
                ? Strings.Diagnostics.Inspector.SavedNoPayloads(folder)
                : Strings.Diagnostics.Inspector.SavedPayloads(payloads, folder);
            Notify.Say(Loc.Get(Strings.Lyrics.Inspector.EvidenceSaved), InfoBarSeverity.Success);
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + folder + "\"") { UseShellExecute = true }); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                Log.Warn(Lyrics.Diag.Category, "the lyrics evidence folder could not be opened", ex);
            }
        }

        /// <summary>Drop this track from the aggregator's memory AND disk caches and run the fan-out again, straight on the
        /// provider: the store republishes the inspection, and the lyrics already on screen are left alone. A session
        /// with no provider (offline, signed out, the fake backend) says so in the amber line — never a toast.</summary>
        void Refetch(Action<Action> post, bool thenSave)
        {
            if (_refetching.Peek() || _saving.Peek()) return;
            if (Lyrics.Store.Provider is not { } provider)
            {
                _status.Value = Loc.Get(Strings.Diagnostics.Inspector.NoRefetch);
                return;
            }
            _refetching.Value = true;
            _status.Value = thenSave ? Loc.Get(Strings.Diagnostics.Inspector.RefetchingFirst) : "";
            _ = RefetchAsync(provider, _trackId, post, thenSave);
        }

        async Task RefetchAsync(Lyrics.Aggregator provider, string trackId, Action<Action> post, bool thenSave)
        {
            string? error = null;
            try { await Task.Run(() => provider.RefetchAsync(trackId)).ConfigureAwait(false); }
            catch (Exception e) { error = e.GetType().Name + ": " + e.Message; }   // reported in the status line, not rethrown
            post(() =>
            {
                _refetching.Value = false;
                _epoch.Value = _epoch.Peek() + 1;
                if (error is not null) { _status.Value = Strings.Diagnostics.Inspector.RefetchFailed(error); return; }
                _status.Value = "";
                // Re-read the store rather than the snapshot the click closed over: the fetch is what just filled it.
                if (thenSave) WriteBundle(post, Lyrics.Diag.ForTrack(trackId), Lyrics.Diag.InspectionFor(trackId));
            });
        }

        // ── 1. providers (W17) ───────────────────────────────────────────────────────────────────────────────────────

        Element ProvidersTab(Lyrics.SearchReport? report, Lyrics.Inspection? insp)
        {
            // The AI card subscribes the AI signals itself, so a job's progress re-renders only it.
            var kids = new List<Element>(9) { Embed.Comp(() => new AiCard(_trackId, _aiBytes)) };
            if (report is null)
            {
                kids.Add(Note(Loc.Get(Strings.Diagnostics.Inspector.NoReport)));
                if (insp?.Note is { Length: > 0 } onlyNote) kids.Add(Caption(onlyNote));
                return Column(10f, kids);
            }

            kids.Add(R.Type.DenseTitle(report.Summary) with { Color = Tok.AccentTextPrimary, Wrap = TextWrap.Wrap });
            if (LyricsInspector.ReferenceLabel(report.Summary) is { Length: > 0 } reference)
                kids.Add(new TextEl(reference) { Size = 12f, LineHeight = 16f, Weight = 600, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap });
            kids.Add(new TextEl(Strings.Diagnostics.Inspector.TitleLine(
                    string.IsNullOrWhiteSpace(report.Title) ? Loc.Get(Strings.Diagnostics.Inspector.NoTitle) : report.Title,
                    string.IsNullOrWhiteSpace(report.Artist) ? Loc.Get(Strings.Lyrics.Debug.NoArtist) : report.Artist))
                { Size = 12f, LineHeight = 16f, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap });
            kids.Add(Caption(LyricsReport.IdentityLine(_trackId, report)));
            if (insp?.Note is { Length: > 0 } note) kids.Add(Caption(note));
            kids.Add(Rule());

            double winnerScore = LyricsReport.WinnerScore(report);
            if (report.Sources.Count == 0) kids.Add(Note(Loc.Get(Strings.Diagnostics.Inspector.NoSourceRan)));
            foreach (var t in report.Sources) kids.Add(ProviderCard(t, insp, winnerScore));
            return Column(10f, kids);
        }

        Element ProviderCard(Lyrics.SourceTrace t, Lyrics.Inspection? insp, double winnerScore)
        {
            ColorF dot = t.Outcome switch
            {
                Lyrics.Outcome.Hit => Good,
                Lyrics.Outcome.Timeout => Warn,
                Lyrics.Outcome.Error => Bad,
                Lyrics.Outcome.Skipped => Dim,
                _ => Grey,
            };
            int rawCount = LyricsReport.RawCount(insp, t.SourceId);
            var parsed = LyricsReport.CandidateFor(insp, t.SourceId);
            string id = t.SourceId;
            // v2: the stage-0 match and the rerank facts, each its own WRAPPED line — a 492-DIP card has no room for
            // columns, and a reason is never truncated.
            string match = LyricsReport.MatchText(t, parsed?.Basis);
            string rerank = LyricsReport.RerankText(LyricsReport.FactsOf(t));

            var rows = new List<Element>(8)
            {
                Row(8f,
                    new BoxEl { Width = 8f, Height = 8f, Corners = Radii.Circle(8f), Fill = dot, AlignSelf = FlexAlign.Center, Shrink = 0f },
                    R.Type.DenseTitle(t.SourceId) with { Weight = 700, Color = t.Winner ? Tok.AccentTextPrimary : Tok.TextPrimary },
                    R.Type.MicroMeta(LyricsReport.OutcomeLine(t)) with { Weight = 600, Color = Tok.TextTertiary, Grow = 1f, MinWidth = 0f }),
                new TextEl(LyricsReport.Verdict(t, winnerScore))
                    { Size = 12f, LineHeight = 16f, Wrap = TextWrap.Wrap, Color = t.Winner ? Good : Tok.TextSecondary },
            };
            if (match.Length > 0)
                rows.Add(R.Type.MicroMeta(Strings.Diagnostics.Inspector.MatchLine(match)) with { Color = Tok.TextSecondary, Wrap = TextWrap.Wrap });
            if (rerank.Length > 0)
                rows.Add(R.Type.MicroMeta(Strings.Diagnostics.Inspector.RerankLine(rerank)) with { Color = Tok.TextSecondary, Wrap = TextWrap.Wrap });
            if (t.Detail.Length > 0) rows.Add(Caption(t.Detail));
            if (parsed is not null)
            {
                rows.Add(Caption(LyricsReport.ParsedLine(parsed)));
                string timing = Lyrics.Timing.Describe(parsed.Document);
                rows.Add(R.Type.MicroMeta("timing: " + timing) with
                {
                    Wrap = TextWrap.Wrap,
                    Color = timing.StartsWith("clean", StringComparison.Ordinal) ? Tok.TextTertiary : Warn,
                });
            }
            // Each switches tab AND focus, and is DISABLED (and relabelled) when there is nothing behind it.
            rows.Add(Row(6f,
                Standard(rawCount > 0 ? Strings.Lyrics.Inspector.Raw(rawCount) : Loc.Get(Strings.Lyrics.Inspector.RawNone),
                    () => { _focus.Value = id; _tab.Value = 1; }, enabled: rawCount > 0),
                Standard(Loc.Get(parsed is not null ? Strings.Diagnostics.Inspector.TabParsed : Strings.Diagnostics.Inspector.ParsedNone),
                    () => { _focus.Value = id; _tab.Value = 2; }, enabled: parsed is not null)));

            return new BoxEl
            {
                Direction = 1, Gap = 5f, Padding = Edges4.All(10f), Corners = CornerRadius4.All(6f),
                Fill = Tok.FillSubtleSecondary, BorderWidth = 1f, BorderColor = t.Winner ? Tok.AccentDefault : Tok.StrokeCardDefault,
                Children = rows.ToArray(),
            };
        }

        /// <summary>The AI facts for this track at click time (Peeks: the copy and the bundle, never a render).</summary>
        LyricsReport.AiFacts AiFactsNow()
        {
            var job = AiLyrics.Job.Peek();
            return LyricsReport.AiFactsFor(_trackId, AiLyrics.Current.Peek(), AiLyrics.Track.Peek(), job,
                Lyrics.Store.Doc(_trackId), AiResultPath(_trackId, job), _aiBytes.Peek());
        }

        static string AiResultPath(string trackId, AiLyrics.JobInfo? job)
            => job is not null && job.TrackId == trackId ? job.ResultPath : AiLyrics.ResultPathOf(trackId);

        /// <summary>The Providers tab's first card: what the on-device AI did with this track's timing. Its own component
        /// so ONLY it subscribes the AI signals and the lyrics store (a running job republishes per line). The track id
        /// is the dialog's identity and the size signal is the body's (the copy reads it), so both are mount seeds.</summary>
        sealed class AiCard(string trackId, Signal<long> resultBytes) : Component
        {
            readonly string _trackId = trackId;
            readonly Signal<long> _bytes = resultBytes;

            public override Element Render()
            {
                var post = UsePost();
                var status = AiLyrics.Current.Value;
                var track = AiLyrics.Track.Value;
                var job = AiLyrics.Job.Value;
                uint changed = Lyrics.Store.Changed.Value;
                string path = AiResultPath(_trackId, job);

                // The saved result's size: stat OFF the UI thread whenever the job's outcome, the track phase or the
                // store moves (a finished job writes it, "Time again" deletes it and refetches).
                UseEffect(() =>
                {
                    string p = path;
                    var bytes = _bytes;
                    _ = Task.Run(() =>
                    {
                        long n;
                        try { var fi = new FileInfo(p); n = fi.Exists ? fi.Length : LyricsReport.AiResultMissing; }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                        { n = LyricsReport.AiResultMissing; }
                        post(() => bytes.Value = n);
                    });
                }, DepKey.Combine(DepKey.FromRef(job?.Outcome, job?.TrackId), DepKey.From((int)track.Phase, unchecked((int)changed))));

                var f = LyricsReport.AiFactsFor(_trackId, status, track, job, Lyrics.Store.Doc(_trackId), path, _bytes.Value);
                if (!LyricsReport.ShowsAi(f)) return new BoxEl { Visible = false };

                var line = LyricsReport.AiLineOf(f);
                ColorF dot = LyricsReport.AiInkOf(line) switch
                {
                    LyricsReport.AiInk.Accent => Tok.AccentDefault,
                    LyricsReport.AiInk.Good => Good,
                    LyricsReport.AiInk.Bad => Bad,
                    _ => Grey,
                };
                var progress = LyricsReport.AiProgressOf(f);
                string sentence = line switch
                {
                    LyricsReport.AiLine.Working => Strings.Diagnostics.Inspector.Ai.Working(progress.Ready, progress.Total, progress.Processed, progress.Duration),
                    LyricsReport.AiLine.OnScreen => Loc.Get(Strings.Diagnostics.Inspector.Ai.OnScreen),
                    LyricsReport.AiLine.Cached => Loc.Get(Strings.Diagnostics.Inspector.Ai.Cached),
                    LyricsReport.AiLine.Outranked => Strings.Diagnostics.Inspector.Ai.Outranked(LyricsReport.AiProvider(f)),
                    LyricsReport.AiLine.Replaced => Strings.Diagnostics.Inspector.Ai.Replaced(LyricsReport.AiProvider(f)),
                    LyricsReport.AiLine.Skipped => Strings.Diagnostics.Inspector.Ai.Skipped(LyricsReport.AiSkipReason(f)),
                    LyricsReport.AiLine.Failed => Strings.Diagnostics.Inspector.Ai.Failed(LyricsReport.AiFailure(f)),
                    LyricsReport.AiLine.Cancelled => Loc.Get(Strings.Diagnostics.Inspector.Ai.Cancelled),
                    LyricsReport.AiLine.Waiting => Loc.Get(Strings.Diagnostics.Inspector.Ai.Waiting),
                    LyricsReport.AiLine.Off => Loc.Get(Strings.Diagnostics.Inspector.Ai.Off),
                    LyricsReport.AiLine.NotReady => Loc.Get(Strings.Diagnostics.Inspector.Ai.NotReady),
                    _ => Loc.Get(Strings.Diagnostics.Inspector.Ai.Idle),
                };
                var times = LyricsReport.AiTimesOf(f);
                string pack = AiLyrics.PackVersion.ToString(CultureInfo.InvariantCulture);
                string result = f.ResultBytes switch
                {
                    >= 0 => Strings.Diagnostics.Inspector.Ai.Result(path, LyricsReport.AiResultSize(f.ResultBytes)),
                    LyricsReport.AiResultMissing => Strings.Diagnostics.Inspector.Ai.ResultNone(path),
                    _ => Strings.Diagnostics.Inspector.Ai.ResultChecking(path),
                };
                string id = _trackId;

                return new BoxEl
                {
                    Direction = 1, Gap = 5f, Padding = Edges4.All(10f), Corners = CornerRadius4.All(6f),
                    Fill = Tok.FillSubtleSecondary, BorderWidth = 1f,
                    BorderColor = line is LyricsReport.AiLine.OnScreen or LyricsReport.AiLine.Cached ? Tok.AccentDefault : Tok.StrokeCardDefault,
                    Children =
                    [
                        Row(8f,
                            new BoxEl { Width = 8f, Height = 8f, Corners = Radii.Circle(8f), Fill = dot, AlignSelf = FlexAlign.Center, Shrink = 0f },
                            R.Type.DenseTitle(Loc.Get(Strings.Diagnostics.Inspector.Ai.Title)) with { Weight = 700, Color = Tok.TextPrimary },
                            R.Type.MicroMeta(Strings.Diagnostics.Inspector.Ai.Setup(status.Phase.ToString()))
                                with { Weight = 600, Color = Tok.TextTertiary, Grow = 1f, MinWidth = 0f }),
                        new TextEl(sentence)
                        {
                            Size = 12f, LineHeight = 16f, Wrap = TextWrap.Wrap,
                            Color = line switch
                            {
                                LyricsReport.AiLine.OnScreen or LyricsReport.AiLine.Cached => Good,
                                LyricsReport.AiLine.Failed => Bad,
                                _ => Tok.TextSecondary,
                            },
                        },
                        R.Type.MicroMeta(Strings.Diagnostics.Inspector.Ai.Meta(times.Separate, times.Align, times.Total, times.Language, pack))
                            with { FontFamily = Mono, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap },
                        Caption(Strings.Diagnostics.Inspector.Ai.Npu(OrDash(status.NpuName), OrDash(status.NpuDriver))),
                        Caption(result),
                        Row(6f,
                            Standard(Loc.Get(Strings.Diagnostics.Inspector.Ai.TimeAgain), () => AiLyrics.Retime(id)),
                            Standard(Loc.Get(Strings.Diagnostics.Inspector.Ai.OpenFolder), static () => AiLyrics.OpenFolder())),
                    ],
                };
            }
        }

        // ── 2. raw (W18) ─────────────────────────────────────────────────────────────────────────────────────────────

        Element RawTab(InputHooks hooks, Lyrics.Inspection? insp)
        {
            if (insp is null || insp.Raw.Count == 0)
                return FluentGpu.Dsl.Ui.VStack(10f,
                    Note(Loc.Get(Strings.Diagnostics.Inspector.NoPayload)),
                    Caption(insp?.Note ?? Loc.Get(Strings.Diagnostics.Inspector.NothingRecorded)),
                    Caption(Loc.Get(Strings.Lyrics.Inspector.PayloadsNote)));

            var sources = LyricsReport.RawSources(insp);
            string focus = _focus.Value;
            if (!sources.Contains(focus)) focus = sources[0];   // a focus from another tab may name a source with no payload

            var chips = new Element[sources.Count];
            for (int i = 0; i < sources.Count; i++)
            {
                string id = sources[i];
                chips[i] = FluentGpu.Dsl.Ui.Pill(id + " (" + LyricsReport.RawCount(insp, id).ToString(System.Globalization.CultureInfo.InvariantCulture) + ")",
                    StringComparer.Ordinal.Equals(id, focus), () => _focus.Value = id);
            }
            var kids = new List<Element>(4) { FluentGpu.Dsl.Ui.Wrap(6f, chips) };
            foreach (var payload in insp.Raw)
                if (StringComparer.Ordinal.Equals(payload.SourceId, focus)) kids.Add(PayloadCard(hooks, payload));
            return Column(10f, kids);
        }

        static Element PayloadCard(InputHooks hooks, Lyrics.RawPayload p)
        {
            // The ON-SCREEN cap; Copy always hands over everything that was captured.
            string shown = p.Text.Length <= LyricsReport.OnScreenRawChars ? p.Text : p.Text[..LyricsReport.OnScreenRawChars];
            return new BoxEl
            {
                Direction = 1, Gap = 6f, Padding = Edges4.All(10f), Corners = CornerRadius4.All(6f),
                Fill = Tok.FillControlSecondary, BorderWidth = 1f, BorderColor = Tok.StrokeControlDefault,
                Children =
                [
                    new BoxEl
                    {
                        Direction = 1, Gap = 4f,
                        Children =
                        [
                            R.Type.MicroMeta(p.Label) with { Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, FontFamily = Mono },
                            Row(8f,
                                Caption(LyricsReport.PayloadCaption(p, shown.Length)) with { Grow = 1f, MinWidth = 0f },
                                Standard(Loc.Get(Strings.Diagnostics.Inspector.Copy), () =>
                                {
                                    hooks.Clipboard?.SetText(p.Text);
                                    Notify.Say(Strings.Diagnostics.Inspector.PayloadCopied(p.SourceId), InfoBarSeverity.Success);
                                })),
                        ],
                    },
                    Rule(),
                    R.Type.MicroMeta(shown) with { FontFamily = Mono, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap },
                ],
            };
        }

        // ── 3. parsed (W19) ──────────────────────────────────────────────────────────────────────────────────────────

        Element ParsedTab(InputHooks hooks, Lyrics.Inspection? insp)
        {
            if (insp is null || (insp.Final is null && insp.Candidates.Count == 0))
                return FluentGpu.Dsl.Ui.VStack(10f,
                    Note(Loc.Get(Strings.Diagnostics.Inspector.NoParsed)),
                    Caption(insp?.Note ?? Loc.Get(Strings.Diagnostics.Inspector.NothingRecorded)));

            // A focus carried over from Providers/Raw may name a source that produced no DOCUMENT. Resolve it once so the
            // chip highlight and the body can never disagree about what is on screen.
            string focus = _focus.Value;
            var cand = LyricsReport.CandidateFor(insp, focus);
            Lyrics.Doc? doc = cand?.Document ?? insp.Final;
            string who = cand is null ? "final" : focus;

            var chips = new List<Element>(insp.Candidates.Count + 1);
            if (insp.Final is not null)
                chips.Add(FluentGpu.Dsl.Ui.Pill(Loc.Get(Strings.Diagnostics.Inspector.FinalChip), cand is null, () => _focus.Value = ""));
            foreach (var c in insp.Candidates)
            {
                string id = c.SourceId;
                chips.Add(FluentGpu.Dsl.Ui.Pill(id, cand is not null && StringComparer.Ordinal.Equals(id, focus), () => _focus.Value = id));
            }
            var kids = new List<Element>(8) { FluentGpu.Dsl.Ui.Wrap(6f, chips.ToArray()) };
            if (doc is null)
            {
                kids.Add(Note(Loc.Get(Strings.Diagnostics.Inspector.NoDocument)));
                return Column(10f, kids);
            }

            bool showSyllables = _syllables.Value;
            // Only the FINAL line claims to be what the UI got; a candidate is the parse BEFORE the offset correction.
            kids.Add(new TextEl(cand is null
                    ? Loc.Get(Strings.Diagnostics.Inspector.FinalDescription)
                    : Strings.Diagnostics.Inspector.CandidateDescription(who))
                { Size = 12f, LineHeight = 16f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap });
            kids.Add(Caption(LyricsReport.DocMeta(doc, who)));
            string anomalies = Lyrics.Timing.Describe(doc);
            kids.Add(new TextEl("timing check: " + anomalies)
            {
                Size = 12f, LineHeight = 16f, Wrap = TextWrap.Wrap,
                Color = anomalies.StartsWith("clean", StringComparison.Ordinal) ? Good : Warn,
            });
            Lyrics.Doc d = doc;   // a non-null local the Copy thunk captures (a `var` would re-read as nullable inside it)
            kids.Add(Row(6f,
                Standard(Loc.Get(showSyllables ? Strings.Diagnostics.Inspector.HideSyllables : Strings.Diagnostics.Inspector.ShowSyllables),
                    () => _syllables.Value = !_syllables.Peek()),
                Standard(Loc.Get(Strings.Diagnostics.Inspector.CopyParsed), () =>
                {
                    hooks.Clipboard?.SetText(LyricsReport.BuildParsed(who, d));
                    Notify.Say(Loc.Get(Strings.Lyrics.Inspector.ParsedCopied), InfoBarSeverity.Success);
                })));
            kids.Add(Rule());

            int count = Math.Min(doc.Lines.Count, LyricsReport.OnScreenLines);
            for (int i = 0; i < count; i++) kids.Add(LineRow(doc, i, showSyllables));
            if (doc.Lines.Count > count)
                kids.Add(Caption(Strings.Diagnostics.Inspector.MoreLines(doc.Lines.Count - count)));
            return Column(8f, kids);
        }

        static Element LineRow(Lyrics.Doc doc, int i, bool showSyllables)
        {
            var l = doc.Lines[i];
            var check = LyricsReport.Check(doc, i);
            ColorF timeInk = check.Time switch
            {
                LyricsReport.TimeInk.Bad => Bad,
                LyricsReport.TimeInk.Warn => Warn,
                _ => Tok.TextSecondary,
            };
            bool blank = l.Text.Length == 0;
            var kids = new List<Element>(4)
            {
                Row(8f,
                    R.Type.MicroMeta(i.ToString(System.Globalization.CultureInfo.InvariantCulture)) with { FontFamily = Mono, Color = Tok.TextTertiary, Width = 26f, Shrink = 0f },
                    R.Type.MicroMeta(LyricsReport.TimeCell(l)) with { FontFamily = Mono, Color = timeInk, Width = 130f, Shrink = 0f },
                    // The DURATION spelled out: a squashed line is invisible in two timestamps and obvious as "217ms".
                    R.Type.MicroMeta(LyricsReport.DurationCell(l)) with { FontFamily = Mono, Color = check.Squashed ? Warn : Tok.TextTertiary, Width = 52f, Shrink = 0f },
                    new TextEl(blank ? Loc.Get(Strings.Diagnostics.Inspector.BlankLine) : l.Text)
                    {
                        Size = 12f, LineHeight = 16f, Wrap = TextWrap.Wrap, Grow = 1f, MinWidth = 0f,
                        Color = blank ? Tok.TextTertiary : Tok.TextPrimary,
                    }),
            };
            if (l.Translation is { Length: > 0 } tr) kids.Add(SubLine("[tr] " + tr));
            if (l.Romanization is { Length: > 0 } ro) kids.Add(SubLine("[ro] " + ro));
            if (showSyllables && l.Syllables.Count > 0)
                kids.Add(R.Type.MicroMeta(LyricsReport.SyllableStrip(l)) with
                {
                    FontFamily = Mono, Color = Tok.TextTertiary, Wrap = TextWrap.Wrap,
                    Margin = new Edges4(34f, 0f, 0f, 0f),
                });
            return new BoxEl { Direction = 1, Gap = 2f, Children = kids.ToArray() };
        }

        // ── small helpers ────────────────────────────────────────────────────────────────────────────────────────────

        static BoxEl Row(float gap, params Element[] kids) => FluentGpu.Dsl.Ui.HStack(gap, kids);

        static BoxEl Column(float gap, List<Element> kids) => FluentGpu.Dsl.Ui.VStack(gap, kids.ToArray());

        static BoxEl Standard(string label, Action onClick, bool enabled = true)
            => FluentGpu.Controls.Button.Standard(label, onClick, isEnabled: enabled);

        static BoxEl Rule() => new() { Height = 1f, Fill = Tok.StrokeCardDefault };

        static TextEl SubLine(string text) => R.Type.MicroMeta(text) with
        {
            Color = Tok.TextTertiary, Wrap = TextWrap.Wrap, Margin = new Edges4(34f, 0f, 0f, 0f),
        };

        static TextEl Note(string text) => new(text) { Size = 12f, LineHeight = 17f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap };

        static TextEl Caption(string text) => R.Type.MicroMeta(text) with { Color = Tok.TextTertiary, Wrap = TextWrap.Wrap };
    }
}
