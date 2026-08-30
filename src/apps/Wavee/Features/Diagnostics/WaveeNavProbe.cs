using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using FluentGpu.Foundation;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Windows;
using FluentGpu.Rhi;
using FluentGpu.Rhi.D3D12;
using Wavee.Core;

namespace Wavee;

// WAVEE_NAV_PROBE=1: a scripted navigation / scroll / theme / back-forward stress harness. Installed via
// FluentApp.DiagnosticRun, it takes over the run loop and drives REAL app navigation through WaveeShell.Probe* hooks,
// recording the per-frame PRODUCTION time of every painted frame. To measure the engine's true throughput each frame
// suppresses the swapchain frame-latency wait AND vsync (WAVEE_PROBE_VSYNC=1 keeps them — proves a present artifact).
//
// "Butter smooth" target on a 120 Hz panel = every frame's WORK under one vblank (8.33 ms). A frame over budget misses
// a vblank in real (vsync-paced) use → a visible stutter. So the headline metric is "frames over 8.33 ms" — and each is
// split into WORK spikes vs GC spikes (a Gen0+ collection fired on that frame), because the two need different fixes.
internal static class WaveeNavProbe
{
    static readonly WaveeLogger Log = new(WaveeLog.Instance, "probe");

    const double BudgetMs = 1000.0 / 120.0;   // one 120 Hz vblank — the smoothness bar

    [System.Runtime.InteropServices.DllImport("user32", ExactSpelling = true)]
    static extern nint SendMessageW(nint hWnd, uint msg, nint wParam, nint lParam);

    // Heavy pages: HomePage (image cards), Liked (big track list), real playlists (40 tracks + cover). Cheap: placeholders.
    static readonly (string Key, string? Arg)[] HeavyRoutes =
    [
        ("home", null),
        ("liked", null),
        ("pl:spotify:playlist:pl0", "Playlist 0"),
        ("pl:spotify:playlist:pl1", "Playlist 1"),
        ("pl:spotify:playlist:pl2", "Playlist 2"),
        ("pl:spotify:playlist:pl3", "Playlist 3"),
        ("pl:spotify:playlist:pl4", "Playlist 4"),
        ("pl:spotify:playlist:pl5", "Playlist 5"),
    ];
    static readonly (string Key, string? Arg)[] CheapRoutes =
    [
        ("albums", null), ("artists", null), ("podcasts", null), ("local", null), ("browse", null),
    ];

    /// <summary>Every env flag that hands this probe the run loop. ONE list, read by both <see cref="TryRun"/> and the
    /// single-instance gate in <c>Program</c> — a probe must never contend with the user's own running Wavee (the gate
    /// would silently exit the probe process with code 0, which reads exactly like "the probe ran and found nothing").</summary>
    static readonly string[] ProbeFlags =
    [
        "WAVEE_NAV_PROBE", "WAVEE_CONN_STRESS", "WAVEE_TRACKLIST_SHOT", "WAVEE_HERO_SHOT", "WAVEE_SHELF_SHOT",
        "WAVEE_RAIL_SHOT", "WAVEE_RAIL_PROBE", "WAVEE_HOME_SCROLL_PROBE", "WAVEE_LYRICS_PROBE",
        "WAVEE_LIVE_LYRICS_SCROLL_PROBE", "WAVEE_LYRICS_ADVANCE_PROBE", "WAVEE_SIDEBAR_MODE_SHOT",
        "WAVEE_SIDEBAR_V3_SHOT", "WAVEE_SIDEBAR_VISUAL_SHOT",
    ];

    /// <inheritdoc cref="ProbeFlags"/>
    internal static bool Requested
    {
        get
        {
            foreach (string f in ProbeFlags) if (Diag.EnvFlag(f)) return true;
            return false;
        }
    }

    public static bool TryRun(AppHost host, IPlatformWindow window, IGpuDevice device)
    {
        if (!Requested) return false;
        bool connStress = Diag.EnvFlag("WAVEE_CONN_STRESS");
        bool trackShot = Diag.EnvFlag("WAVEE_TRACKLIST_SHOT");
        bool heroShot = Diag.EnvFlag("WAVEE_HERO_SHOT");
        bool shelfShot = Diag.EnvFlag("WAVEE_SHELF_SHOT");
        bool railShot = Diag.EnvFlag("WAVEE_RAIL_SHOT");
        bool railProbe = Diag.EnvFlag("WAVEE_RAIL_PROBE");
        bool homeScroll = Diag.EnvFlag("WAVEE_HOME_SCROLL_PROBE");
        bool lyricsProbe = Diag.EnvFlag("WAVEE_LYRICS_PROBE");
        bool liveLyricsScroll = Diag.EnvFlag("WAVEE_LIVE_LYRICS_SCROLL_PROBE");
        bool advanceProbe = Diag.EnvFlag("WAVEE_LYRICS_ADVANCE_PROBE");
        bool sidebarModeShot = Diag.EnvFlag("WAVEE_SIDEBAR_MODE_SHOT");
        bool sidebarV3Shot = Diag.EnvFlag("WAVEE_SIDEBAR_V3_SHOT");
        bool sidebarVisualShot = Diag.EnvFlag("WAVEE_SIDEBAR_VISUAL_SHOT");
        WaveeLog.Instance.SetEcho(Console.Error.WriteLine);   // env-gated run only: mirror probe progress to the terminal
        if (window is not Win32Window w || device is not D3D12Device gpu)
        {
            Log.Warn("[wavee-nav-probe] unavailable: requires Win32Window + D3D12Device");
            return true;
        }
        // DiagnosticRun fires straight after window.Show(), BEFORE the first frame — so WaveeShell hasn't mounted yet
        // and ProbeNav is still null. Pump warmup frames until the shell wires its nav hook (fixes the mount race that
        // made earlier shot runs flakily report "nav hook not wired").
        // The sidebar shots render the LIBRARY projection (playlists / albums / artists / podcasts), so like the rail and
        // hero shots they need the authenticated first sync to have landed — not just the shell to have mounted.
        bool needsAuth = liveLyricsScroll || advanceProbe || heroShot || shelfShot || railShot || sidebarModeShot || sidebarV3Shot || sidebarVisualShot;
        int hookFrames = needsAuth ? Math.Max(240, EnvInt("WAVEE_PROBE_AUTH_FRAMES", 7200, 240, 36000)) : 240;
        for (int i = 0; i < hookFrames && WaveeShell.ProbeNav is null && !w.IsClosed; i++)
        {
            if (needsAuth)
            {
                host.RunFrame();
                w.WaitForWork(Math.Min(host.RecommendedWaitMs(), 16));
            }
            else
            {
                gpu.SuppressLatencyWaitOnce();
                gpu.SuppressVsyncOnce();
                host.RunFrame();
            }
        }
        if (WaveeShell.ProbeNav is null)
        {
            Log.Warn("[wavee-nav-probe] nav hook not wired (WaveeShell not mounted?)");
            return true;
        }
        if (heroShot) RunHeroCollapseShot(host, w, gpu);
        else if (sidebarVisualShot) RunSidebarVisualShot(host, w, gpu);
        else if (sidebarModeShot) RunSidebarModeShot(host, w, gpu);
        else if (sidebarV3Shot) RunSidebarV3Shot(host, w, gpu);
        else if (railProbe) RunRailProbe(host, w, gpu);
        else if (railShot) RunRailShot(host, w, gpu);
        else if (shelfShot) RunShelfFadeShot(host, w, gpu);
        else if (trackShot) RunTrackListShot(host, w, gpu);
        else if (connStress) RunConnStress(host, w, gpu);
        else if (homeScroll) RunHomeScrollProbe(host, w, gpu);
        else if (liveLyricsScroll) RunLiveLyricsScrollProbe(host, w, gpu);
        else if (advanceProbe) RunLyricsAdvanceProbe(host, w, gpu);
        else if (lyricsProbe) RunLyricsProbe(host, w, gpu);
        else Run(host, w, gpu);
        return true;
    }

    // WAVEE_SIDEBAR_VISUAL_SHOT=1: the two user-reported failure viewports plus the wide customizer canvas. This is the
    // mandatory visual-remediation loop: Curated must not regress into billboard empty states/rule noise, the customizer
    // command bar must keep Done fully visible, and the docked pane must settle at 56 DIP or its valid expanded width.
    // Output -> sidebar_visual_curated_577x987.png, sidebar_visual_customizer_1395x1107.png,
    //           sidebar_visual_customizer_canvas_1776x1107.png. Set WAVEE_SIDEBAR_VISUAL_STAGE to
    //           curated/customizer/canvas for one capture, or expanded for sidebar_visual_curated_expanded_1395x1107.png.
    static void RunSidebarVisualShot(AppHost host, Win32Window window, D3D12Device gpu)
    {
        try { Directory.CreateDirectory(ProbeArtifacts.Dir); } catch { }
        string stage = (Environment.GetEnvironmentVariable("WAVEE_SIDEBAR_VISUAL_STAGE") ?? "")
            .Trim().ToLowerInvariant();
        void Frame() { if (!window.IsClosed) { gpu.SuppressLatencyWaitOnce(); gpu.SuppressVsyncOnce(); host.RunFrame(); } }
        void Settle(int n) { for (int i = 0; i < n && !window.IsClosed; i++) Frame(); }
        void Shot(string name)
        {
            var px = gpu.CaptureBgra(out int cw, out int ch);
            PngWriter.WriteBgra(ProbeArtifacts.PathFor(name + ".png"), px, cw, ch);
            Log.Info($"[sidebar-visual-shot] wrote {name}.png ({cw}x{ch})");
        }

        if (WaveeShell.ProbeSidebarDesign is null || WaveeShell.ProbeSidebarCompact is null
            || WaveeShell.ProbeSidebarDrawer is null || WaveeShell.ProbeSidebarPaneFrame is null)
        {
            Log.Warn("[sidebar-visual-shot] sidebar probe hooks not wired - aborting");
            return;
        }

        void AssertPane(string stage)
        {
            var snapshot = WaveeShell.ProbeSidebarPaneFrame!();
            var fault = SidebarPaneInvariant.Inspect(in snapshot);
            if (fault == SidebarPaneInvariantFault.None) return;
            WaveeLog.Instance.Event(WaveeLogLevel.Error, "sidebar", "sidebar.pane.invariant_failed",
                "Sidebar pane did not settle in a valid terminal state",
                fields:
                [
                    WaveeLogField.Of("stage", stage),
                    WaveeLogField.Of("design", snapshot.Design.ToString()),
                    WaveeLogField.Of("presentedCompact", snapshot.PresentedCompact),
                    WaveeLogField.Of("preferredWidth", snapshot.PreferredExpandedWidth),
                    WaveeLogField.Of("renderedWidth", snapshot.RenderedPaneWidth),
                    WaveeLogField.Of("fault", SidebarPaneInvariant.FaultName(fault)),
                ]);
            throw new InvalidOperationException($"sidebar pane invariant failed at {stage}: {fault}");
        }

        WaveeShell.ProbeNav!("home", null);
        WaveeShell.ProbeSidebarDesign!(2 /* Curated */);
        WaveeShell.ProbeSidebarCompact!(false);
        Settle(90); System.Threading.Thread.Sleep(900); Settle(90);
        SendMessageW(window.Handle.Value, 0x0006 /*WM_ACTIVATE*/, 1 /*WA_ACTIVE*/, 0);

        if (stage.Length == 0 || stage == "curated")
        {
            window.SetClientSize(577, 987);
            Settle(90); System.Threading.Thread.Sleep(500); Settle(60);
            AssertPane("curated_577x987_docked");
            WaveeShell.ProbeSidebarDrawer!(true);
            Settle(60); System.Threading.Thread.Sleep(300); Settle(45);
            Shot("sidebar_visual_curated_577x987");
            WaveeShell.ProbeSidebarDrawer!(false);
            if (stage == "curated") { Log.Info("[sidebar-visual-shot] done (curated)"); return; }
        }

        if (stage == "expanded")
        {
            window.SetClientSize(1395, 1107);
            WaveeShell.ProbeSidebarDrawer!(false);
            WaveeShell.ProbeSidebarCompact!(false);
            Settle(120); System.Threading.Thread.Sleep(500); Settle(60);
            AssertPane("curated_expanded_1395x1107");
            Shot("sidebar_visual_curated_expanded_1395x1107");
            Log.Info("[sidebar-visual-shot] done (expanded)");
            return;
        }

        if (stage.Length == 0 || stage == "customizer")
        {
            window.SetClientSize(1395, 1107);
            WaveeShell.ProbeNav!("sidebar-customize", null);
            Settle(120); System.Threading.Thread.Sleep(700); Settle(90);
            AssertPane("customizer_1395x1107");
            Shot("sidebar_visual_customizer_1395x1107");
            if (stage == "customizer") { Log.Info("[sidebar-visual-shot] done (customizer)"); return; }
        }

        if (stage.Length == 0 || stage == "canvas")
        {
            window.SetClientSize(1776, 1107);
            WaveeShell.ProbeNav!("sidebar-customize", null);
            Settle(120); System.Threading.Thread.Sleep(500); Settle(60);
            AssertPane("customizer_canvas_1776x1107");
            Shot("sidebar_visual_customizer_canvas_1776x1107");
        }
        Log.Info("[sidebar-visual-shot] done");
    }

    // WAVEE_TRACKLIST_SHOT=1: navigate to each surface that shows tracks and capture a PNG, so the unified track row (the
    // shared TrackRow cell) can be eyeballed for 1:1 parity — a detail playlist, the Library albums pane, an artist page
    // ("Popular"), and search results ("Songs"). Output → <logs>\artifacts\tl_*.png.
    static void RunTrackListShot(AppHost host, Win32Window window, D3D12Device gpu)
    {
        try { System.IO.Directory.CreateDirectory(ProbeArtifacts.Dir); } catch { }
        void Frame() { if (!window.IsClosed) { gpu.SuppressLatencyWaitOnce(); gpu.SuppressVsyncOnce(); host.RunFrame(); } }
        void Nav(string k, string? a) => WaveeShell.ProbeNav!(k, a);
        void Settle(int n) { for (int i = 0; i < n && !window.IsClosed; i++) Frame(); }
        void Shot(string name)
        {
            var px = gpu.CaptureBgra(out int cw, out int ch);
            PngWriter.WriteBgra(ProbeArtifacts.PathFor($"tl_{name}.png"), px, cw, ch);
            Log.Info($"[tracklist-shot] wrote tl_{name}.png ({cw}x{ch})");
        }

        Nav("home", null); Settle(40); System.Threading.Thread.Sleep(700); Settle(20);   // home live + card covers decode

        // 1) Detail playlist (the canonical full list — must be unchanged by the cell extraction).
        Nav("pl:spotify:playlist:pl0", "Playlist 0"); Settle(40); System.Threading.Thread.Sleep(700); Settle(60);
        var detailVp = FindLargestScrollViewport(host.Scene);
        if (!detailVp.IsNull && host.Scene.TryGetScroll(detailVp, out var detailBefore))
        {
            var rr = host.Scene.AbsoluteRect(detailVp);
            var pt = new Point2(rr.X + rr.W * 0.5f, rr.Y + rr.H * 0.5f);
            var routed = host.Input.ScrollableUnderForAxis(pt, wantHorizontal: false);
            var hit = host.Input.DiagHitTest(pt);
            window.QueueInput(new InputEvent(InputKind.PointerMove, pt, 0, 0));
            window.QueueInput(new InputEvent(InputKind.Wheel, pt, 0, 0, 240f));
            Settle(30);
            host.Scene.TryGetScroll(detailVp, out var detailAfter);
            Log.Info($"[tracklist-shot] detail-scroll expected=n#{detailVp.Raw.Index} hit=n#{hit.Raw.Index} routed=n#{routed.Raw.Index} " +
                $"rect=({rr.X:0},{rr.Y:0} {rr.W:0}x{rr.H:0}) view={detailBefore.ViewportH:0} content={detailBefore.ContentH:0} " +
                $"offset={detailBefore.OffsetY:0}->{detailAfter.OffsetY:0} activity={detailAfter.Activity} flags={detailAfter.ActivityFlags} " +
                $"result={(MathF.Abs(detailAfter.OffsetY - detailBefore.OffsetY) > 1f ? "PASS" : "FAIL")}");
            if (routed.IsNull)
            {
                Log.Info("[tracklist-shot]   expected-chain:");
                for (var n = detailVp; !n.IsNull; n = host.Scene.Parent(n))
                {
                    var nr = host.Scene.AbsoluteRect(n);
                    var nf = host.Scene.Flags(n);
                    Log.Info($"[tracklist-shot]     n#{n.Raw.Index} type={host.Scene.ElementTypeId(n)} " +
                        $"rect=({nr.X:0},{nr.Y:0} {nr.W:0}x{nr.H:0}) flags={nf} scroll={host.Scene.HasScroll(n)}");
                }
                Log.Info("[tracklist-shot]   hit-chain:");
                for (var n = hit; !n.IsNull; n = host.Scene.Parent(n))
                {
                    var nr = host.Scene.AbsoluteRect(n);
                    var nf = host.Scene.Flags(n);
                    Log.Info($"[tracklist-shot]   hit-chain n#{n.Raw.Index} type={host.Scene.ElementTypeId(n)} " +
                        $"rect=({nr.X:0},{nr.Y:0} {nr.W:0}x{nr.H:0}) flags={nf} scroll={host.Scene.HasScroll(n)}");
                }
            }
        }
        else Log.Info("[tracklist-shot] detail-scroll MISSING");
        Shot("detail");

        // 2) Album detail uses an OUTER scroll viewport around its eager track rows + trailing shelves. Probe the exact
        // wheel route twice (including focus loss/reactivation): an inactive full-bleed selection overlay used to win
        // HitTestAny here even though ordinary row clicks still worked.
        var probeAlbum = Wavee.Core.FakeData.Album(120);
        Nav("album:" + probeAlbum.Uri, probeAlbum.Name); Settle(50); System.Threading.Thread.Sleep(800); Settle(80);
        var albumVp = FindLargestScrollViewport(host.Scene);
        if (!albumVp.IsNull && host.Scene.TryGetScroll(albumVp, out var albumBefore))
        {
            var rr = host.Scene.AbsoluteRect(albumVp);
            var pt = new Point2(rr.X + rr.W * 0.5f, rr.Y + MathF.Min(rr.H, albumBefore.ViewportH) * 0.5f);
            var routed = host.Input.ScrollableUnderForAxis(pt, wantHorizontal: false);
            window.QueueInput(new InputEvent(InputKind.PointerMove, pt, 0, 0));
            window.QueueInput(new InputEvent(InputKind.Wheel, pt, 0, 0, 240f));
            Settle(30);
            host.Scene.TryGetScroll(albumVp, out var albumAfter);

            window.QueueInput(new InputEvent(InputKind.WindowBlur, default, 0, 0)); Settle(2);
            window.QueueInput(new InputEvent(InputKind.WindowFocus, default, 0, 0));
            window.QueueInput(new InputEvent(InputKind.PointerMove, pt, 0, 0));
            float beforeFocusWheel = albumAfter.OffsetY;
            window.QueueInput(new InputEvent(InputKind.Wheel, pt, 0, 0, 240f));
            Settle(30);
            host.Scene.TryGetScroll(albumVp, out var albumFocused);
            var routedFocused = host.Input.ScrollableUnderForAxis(pt, wantHorizontal: false);
            Log.Info($"[tracklist-shot] album-scroll expected=n#{albumVp.Raw.Index} routed=n#{routed.Raw.Index}/n#{routedFocused.Raw.Index} " +
                $"rect=({rr.X:0},{rr.Y:0} {rr.W:0}x{rr.H:0}) view={albumBefore.ViewportH:0} content={albumBefore.ContentH:0} " +
                $"offset={albumBefore.OffsetY:0}->{albumAfter.OffsetY:0}->{albumFocused.OffsetY:0} " +
                $"initial={(MathF.Abs(albumAfter.OffsetY - albumBefore.OffsetY) > 1f ? "PASS" : "FAIL")} " +
                $"reactivate={(MathF.Abs(albumFocused.OffsetY - beforeFocusWheel) > 1f ? "PASS" : "FAIL")}");
        }
        else Log.Info("[tracklist-shot] album-scroll MISSING");
        Shot("album");

        // Bundled Spotify collection art + the intentional no-layer exception on the Liked Songs rail.
        Nav("liked", null); Settle(40); System.Threading.Thread.Sleep(500); Settle(50); Shot("liked");

        // 3) Library albums → the pane auto-selects the first album → its tracks render via the embedded TrackList.
        Nav("albums", null); Settle(50); System.Threading.Thread.Sleep(800); Settle(80); Shot("library");
        // 4) Artist page → "Popular" top-tracks (synthesized for any artist uri).
        Nav("artist:spotify:artist:ar0", "Artist"); Settle(50); System.Threading.Thread.Sleep(700); Settle(60); Shot("artist");
        Nav("artist:spotify:artist:04gDigrS5kc9YWfZHwBETP", "Maroon 5"); Settle(60); System.Threading.Thread.Sleep(2500); Settle(120); Shot("artist_maroon5");
        // 5) Search → the "Songs" rows in the All view.
        Nav("search", "the"); Settle(50); System.Threading.Thread.Sleep(700); Settle(60); Shot("search");

        Log.Info("[tracklist-shot] done");
    }

    // WAVEE_RAIL_SHOT=1: open the right rail in each mode (Details / Lyrics / Queue) and capture PNGs — the rail
    // background, the content↔rail seam, and the bottom corner treatment. Output → <logs>\artifacts\rail_*.png.
    static void RunRailShot(AppHost host, Win32Window window, D3D12Device gpu)
    {
        try { System.IO.Directory.CreateDirectory(ProbeArtifacts.Dir); } catch { }
        window.SetClientSize(1700, 950);   // wide → the rail DOCKS (RailFits) — the mode the reports are about
        void Frame() { if (!window.IsClosed) { gpu.SuppressLatencyWaitOnce(); gpu.SuppressVsyncOnce(); host.RunFrame(); } }
        void Settle(int n) { for (int i = 0; i < n && !window.IsClosed; i++) Frame(); }
        void Shot(string name)
        {
            var px = gpu.CaptureBgra(out int cw, out int ch);
            PngWriter.WriteBgra(ProbeArtifacts.PathFor($"rail_{name}.png"), px, cw, ch);
            Log.Info($"[rail-shot] wrote rail_{name}.png ({cw}x{ch})");
        }

        WaveeShell.ProbeNav!("home", null); Settle(60); System.Threading.Thread.Sleep(2500); Settle(120);
        // Open-transition frames: the "background only shows a couple frames later" report — capture the first frames
        // of the width animation, then steady.
        WaveeShell.ProbeRail!(2 /*Details*/);
        for (int f = 2; f <= 12; f += 2) { Settle(2); Shot($"details_open{f:D2}"); }
        Settle(48); System.Threading.Thread.Sleep(800); Settle(60); Shot("details");
        WaveeShell.ProbeRail!(0 /*Lyrics*/); Settle(60); System.Threading.Thread.Sleep(800); Settle(60); Shot("lyrics");
        WaveeShell.ProbeRail!(1 /*Queue*/); Settle(60); System.Threading.Thread.Sleep(500); Settle(60); Shot("queue");
        Log.Info("[rail-shot] done");
    }

    // The three sidebar designs, in SidebarDesign ordinal order (Classic=0, LibraryV3=1, Curated=2). The shot name is the
    // design's short name so the artifacts sort into design-then-pane-state pairs.
    static readonly (int Design, string Name)[] SidebarDesignShots =
    [
        (0, "classic"), (1, "v3"), (2, "curated"),
    ];

    // WAVEE_SIDEBAR_MODE_SHOT=1: capture every sidebar DESIGN in both pane states — expanded and the collapsed rail — so
    // the three modes can be reviewed side by side. Both switches go through the REAL seams: the design flip is
    // SidebarPreferences.SwitchDesign (snapshot → reseed → remount), the exact path the Settings picker takes, and the
    // collapse is the real pane commit — so what lands in the PNG is what a user would see, not a poked signal.
    // Output → <logs>\artifacts\sidebar_{classic,v3,curated}_{expanded,rail}.png (6 PNGs).
    static void RunSidebarModeShot(AppHost host, Win32Window window, D3D12Device gpu)
    {
        try { Directory.CreateDirectory(ProbeArtifacts.Dir); } catch { }
        window.SetClientSize(1500, 950);   // wide → the pane takes its WIDE tier, the width the designs are drawn for
        void Frame() { if (!window.IsClosed) { gpu.SuppressLatencyWaitOnce(); gpu.SuppressVsyncOnce(); host.RunFrame(); } }
        void Settle(int n) { for (int i = 0; i < n && !window.IsClosed; i++) Frame(); }
        void Shot(string name)
        {
            var px = gpu.CaptureBgra(out int cw, out int ch);
            PngWriter.WriteBgra(ProbeArtifacts.PathFor($"sidebar_{name}.png"), px, cw, ch);
            Log.Info($"[sidebar-mode-shot] wrote sidebar_{name}.png ({cw}x{ch})");
        }
        if (WaveeShell.ProbeSidebarDesign is null || WaveeShell.ProbeSidebarCompact is null)
        {
            Log.Warn("[sidebar-mode-shot] sidebar probe hooks not wired — aborting");
            return;
        }

        // Home, fully settled: the sidebar lists the library projection, which only has content once the first sync has
        // landed. Same warmup budget the rail shot uses.
        WaveeShell.ProbeNav!("home", null); Settle(60); System.Threading.Thread.Sleep(2500); Settle(120);
        // A real WM_ACTIVATE activate through the wndproc: an inactive window paints the unfocused chrome/Mica arm, which
        // is not the state these shots get reviewed in.
        SendMessageW(window.Handle.Value, 0x0006 /*WM_ACTIVATE*/, 1 /*WA_ACTIVE*/, 0);
        Settle(20);

        foreach (var (design, name) in SidebarDesignShots)
        {
            // A switch to the ALREADY-active design is a documented no-op, so the first iteration is correct whatever
            // design the profile happens to have persisted — every design still gets both shots.
            WaveeShell.ProbeSidebarDesign!(design);
            Settle(60); System.Threading.Thread.Sleep(700); Settle(60);   // remount + the width/collapse animation
            WaveeShell.ProbeSidebarCompact!(false);
            Settle(60); System.Threading.Thread.Sleep(400); Settle(60);
            Shot($"{name}_expanded");
            WaveeShell.ProbeSidebarCompact!(true);
            Settle(60); System.Threading.Thread.Sleep(400); Settle(60);
            Shot($"{name}_rail");
        }

        // Leave the pane expanded: a follow-up scenario in the same run should not inherit the rail.
        WaveeShell.ProbeSidebarCompact!(false); Settle(30);
        Log.Info("[sidebar-mode-shot] done");
    }

    // The Library-V3 presentations (SidebarV3View) and kind filters (SidebarV3Filter), by ordinal.
    static readonly (int View, string Name)[] SidebarV3ViewShots =
    [
        (0, "compactlist"), (1, "list"), (2, "compactgrid"), (3, "grid"),
    ];
    static readonly (int Filter, string Name)[] SidebarV3FilterShots =
    [
        (1, "playlists"), (2, "podcasts"), (3, "albums"), (4, "artists"),
    ];

    // WAVEE_SIDEBAR_V3_SHOT=1: the Library-V3 design's view × filter matrix — each of the four presentations at the All
    // filter, then the List presentation under each kind filter. Both axes go through the real persisting setters
    // (SetV3View / SetV3Filter), the ones the sort/view flyout and the filter chips write, so a shot shows the mode as a
    // user's own change would leave it. Output → <logs>\artifacts\sidebar_v3_view_*.png + sidebar_v3_filter_*.png (8 PNGs).
    static void RunSidebarV3Shot(AppHost host, Win32Window window, D3D12Device gpu)
    {
        try { Directory.CreateDirectory(ProbeArtifacts.Dir); } catch { }
        window.SetClientSize(1500, 950);
        void Frame() { if (!window.IsClosed) { gpu.SuppressLatencyWaitOnce(); gpu.SuppressVsyncOnce(); host.RunFrame(); } }
        void Settle(int n) { for (int i = 0; i < n && !window.IsClosed; i++) Frame(); }
        void Shot(string name)
        {
            var px = gpu.CaptureBgra(out int cw, out int ch);
            PngWriter.WriteBgra(ProbeArtifacts.PathFor($"sidebar_v3_{name}.png"), px, cw, ch);
            Log.Info($"[sidebar-v3-shot] wrote sidebar_v3_{name}.png ({cw}x{ch})");
        }
        if (WaveeShell.ProbeSidebarDesign is null || WaveeShell.ProbeSidebarV3View is null ||
            WaveeShell.ProbeSidebarV3Filter is null || WaveeShell.ProbeSidebarCompact is null)
        {
            Log.Warn("[sidebar-v3-shot] sidebar probe hooks not wired — aborting");
            return;
        }

        WaveeShell.ProbeNav!("home", null); Settle(60); System.Threading.Thread.Sleep(2500); Settle(120);
        SendMessageW(window.Handle.Value, 0x0006 /*WM_ACTIVATE*/, 1 /*WA_ACTIVE*/, 0);
        Settle(20);

        WaveeShell.ProbeSidebarCompact!(false);
        WaveeShell.ProbeSidebarDesign!(1 /*LibraryV3*/);
        Settle(60); System.Threading.Thread.Sleep(900); Settle(60);

        // 1) The four presentations at the All filter (0) — list vs grid, compact vs regular.
        WaveeShell.ProbeSidebarV3Filter!(0 /*All*/); Settle(40); System.Threading.Thread.Sleep(300); Settle(40);
        foreach (var (view, name) in SidebarV3ViewShots)
        {
            WaveeShell.ProbeSidebarV3View!(view);
            Settle(50); System.Threading.Thread.Sleep(400); Settle(50);
            Shot($"view_{name}");
        }

        // 2) Each kind filter at the List presentation (1) — the chip row's selected state plus the filtered rows.
        WaveeShell.ProbeSidebarV3View!(1 /*List*/); Settle(40); System.Threading.Thread.Sleep(300); Settle(40);
        foreach (var (filter, name) in SidebarV3FilterShots)
        {
            WaveeShell.ProbeSidebarV3Filter!(filter);
            Settle(50); System.Threading.Thread.Sleep(400); Settle(50);
            Shot($"filter_{name}");
        }

        // Restore the neutral view state so the shots are not order-dependent across runs.
        WaveeShell.ProbeSidebarV3Filter!(0 /*All*/); WaveeShell.ProbeSidebarV3View!(1 /*List*/); Settle(30);
        Log.Info("[sidebar-v3-shot] done");
    }

    // WAVEE_SHELF_SHOT=1: navigate home and force a horizontal PagedShelf strip to a MID-PAGE offset (what a touchpad
    // free-pan leaves behind), then capture PNGs — the LEFT edge fade must appear once content extends past the left
    // edge, and the left chevron must re-enable (the page state re-syncs from the settled offset). Output → <logs>\artifacts\shelf_*.png.
    static void RunShelfFadeShot(AppHost host, Win32Window window, D3D12Device gpu)
    {
        string shotDir = Path.Combine(Environment.CurrentDirectory, ".tmp", "shelf-shot-" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        try { Directory.CreateDirectory(shotDir); } catch { }
        window.SetClientSize(1280, 900);
        FrameStats lastFrame = default;
        int maxEdgeFadeGroups = 0;
        void Frame()
        {
            if (window.IsClosed) return;
            gpu.SuppressLatencyWaitOnce(); gpu.SuppressVsyncOnce();
            lastFrame = host.RunFrame();
            maxEdgeFadeGroups = Math.Max(maxEdgeFadeGroups, lastFrame.EdgeFadeGroupCount);
        }
        void Settle(int n) { for (int i = 0; i < n && !window.IsClosed; i++) Frame(); }
        void Shot(string name)
        {
            var px = gpu.CaptureBgra(out int cw, out int ch);
            string path = Path.Combine(shotDir, $"shelf_{name}.png");
            PngWriter.WriteBgra(path, px, cw, ch);
            Log.Info($"[shelf-shot] wrote {path} ({cw}x{ch}); edgeFadeGroups={lastFrame.EdgeFadeGroupCount}");
        }

        WaveeShell.ProbeNav!("home", null); Settle(60); System.Threading.Thread.Sleep(1500); Settle(60);
        WaveeShell.ProbeNav!("home", null); Settle(60); System.Threading.Thread.Sleep(2000); Settle(120);

        var scene = host.Scene;
        // Drive the REAL input/integrator path. The old diagnostic mutated ScrollState directly, bypassed the dispatcher
        // transform writer, and captured a stale back buffer — it could claim the offset moved while the shelf stayed
        // off-screen. First wheel the Home viewport until a shelf is actually visible.
        var homeVp = FindScrollViewportByKey(scene, "home");
        if (homeVp.IsNull || !scene.TryGetScroll(homeVp, out var homeBefore))
        { Log.Warn("[shelf-shot] Home viewport missing — aborting"); return; }
        var homeRect = scene.AbsoluteRect(homeVp);
        var pagePoint = new Point2(homeRect.X + homeRect.W * 0.5f, homeRect.Y + homeBefore.ViewportH * 0.5f);
        window.QueueInput(new InputEvent(InputKind.PointerMove, pagePoint, 0, 0));
        window.QueueInput(new InputEvent(InputKind.Wheel, pagePoint, 0, 0, 520f));
        for (int i = 0; i < 90 && !window.IsClosed; i++) { System.Threading.Thread.Sleep(4); Frame(); }
        scene.TryGetScroll(homeVp, out var homeAfter);
        Log.Info($"[shelf-shot] Home offset {homeBefore.OffsetY:0}->{homeAfter.OffsetY:0}");

        NodeHandle shelfVp = NodeHandle.Null;
        float bestVisibleY = float.MaxValue;
        var st = new Stack<NodeHandle>(); if (!scene.Root.IsNull) st.Push(scene.Root);
        while (st.Count > 0)
        {
            var n = st.Pop();
            if (n.IsNull || !scene.IsLive(n)) continue;
            if (scene.HasScroll(n) && scene.TryGetScroll(n, out var s) && s.Orientation == 1
                && s.ContentW > s.ViewportW + 40f && s.AutoEdgeFade)
            {
                var r = scene.AbsoluteRect(n);
                bool visible = r.Bottom > homeRect.Y + 20f && r.Y < homeRect.Bottom - 20f && r.W > 400f;
                Log.Info($"[shelf-shot]   h-scroller rect=({r.X:0},{r.Y:0} {r.W:0}x{r.H:0}) viewW={s.ViewportW:0} contentW={s.ContentW:0} offX={s.OffsetX:0} visible={visible}");
                if (visible && MathF.Abs(r.Y - (homeRect.Y + 180f)) < bestVisibleY)
                { bestVisibleY = MathF.Abs(r.Y - (homeRect.Y + 180f)); shelfVp = n; }
            }
            for (var c = scene.FirstChild(n); !c.IsNull; c = scene.NextSibling(c)) st.Push(c);
        }
        if (shelfVp.IsNull || !scene.TryGetScroll(shelfVp, out var shelfBefore))
        { Log.Warn("[shelf-shot] no visible horizontal shelf found — aborting"); return; }

        var shelfRect = scene.AbsoluteRect(shelfVp);
        var shelfPoint = new Point2(shelfRect.X + shelfRect.W * 0.5f, shelfRect.Y + MathF.Min(shelfRect.H, shelfBefore.ViewportH) * 0.5f);
        var routedBefore = host.Input.ScrollableUnderForAxis(shelfPoint, wantHorizontal: true);
        Shot("before");                         // start edge: right fade only
        maxEdgeFadeGroups = 0;
        window.QueueInput(new InputEvent(InputKind.PointerMove, shelfPoint, 0, 0));
        window.QueueInput(new InputEvent(InputKind.Wheel, shelfPoint, 0, 0, ScrollDeltaX: MathF.Min(300f, shelfBefore.ContentW - shelfBefore.ViewportW)));
        for (int i = 0; i < 90 && !window.IsClosed; i++) { System.Threading.Thread.Sleep(4); Frame(); }
        scene.TryGetScroll(shelfVp, out var shelfAfter);
        var routedAfter = host.Input.ScrollableUnderForAxis(shelfPoint, wantHorizontal: true);
        Shot("after");                          // mid strip: left + right fades
        Log.Info($"[shelf-shot] shelf expected=n#{shelfVp.Raw.Index} routed=n#{routedBefore.Raw.Index}/n#{routedAfter.Raw.Index} " +
            $"offX={shelfBefore.OffsetX:0}->{shelfAfter.OffsetX:0} maxEdgeFadeGroups={maxEdgeFadeGroups} " +
            $"result={(shelfAfter.OffsetX > shelfBefore.OffsetX + 1f && maxEdgeFadeGroups > 0 ? "PASS" : "FAIL")}");

        // Artist page: the measured-virtual shelves ("Appears on" / "Fans also like" / concerts / merch / gallery) —
        // the probe-stuck regression left them as blank reserved bands. Scroll deep and capture the shelf zone.
        WaveeShell.ProbeNav!("artist:spotify:artist:04gDigrS5kc9YWfZHwBETP", "Maroon 5");
        Settle(60); System.Threading.Thread.Sleep(2500); Settle(120);
        NodeHandle pageVp = NodeHandle.Null; float bestC = 0f;
        var st2 = new Stack<NodeHandle>(); if (!scene.Root.IsNull) st2.Push(scene.Root);
        while (st2.Count > 0)
        {
            var n = st2.Pop();
            if (n.IsNull || !scene.IsLive(n)) continue;
            if (scene.HasScroll(n) && scene.TryGetScroll(n, out var s) && s.Orientation != 1 && s.ContentH > s.ViewportH + 1f && s.ContentH > bestC)
            { bestC = s.ContentH; pageVp = n; }
            for (var c = scene.FirstChild(n); !c.IsNull; c = scene.NextSibling(c)) st2.Push(c);
        }
        if (!pageVp.IsNull)
        {
            for (float frac = 0.30f; frac < 1.01f; frac += 0.10f)
            {
                ref FluentGpu.Scene.ScrollState sc = ref scene.ScrollRef(pageVp);
                float t = MathF.Max(0f, (sc.ContentH - sc.ViewportH) * frac);
                // OffsetY/TargetY are kernel-owned result columns now (single-writer token) — post through the port
                // instead of writing the scene directly; the kernel's own sink applies the offset + content transform.
                scene.ScrollPort!.Post(FluentGpu.Scroll.ScrollInput.ScrollTo((int)pageVp.Raw.Index, t, immediate: true, halflifeMs: 0));
                Settle(30);
                Shot($"artist_{(int)(frac * 100):D2}");
            }
        }
        Log.Info("[shelf-shot] done");
    }

    // WAVEE_HERO_SHOT=1: navigate to an artist page and capture the collapsing hero at several scroll offsets, dumping the
    // collapse geometry (pin shift / presented height / child shift / parent height) so it can be verified numerically.
    // Output → <logs>\artifacts\hero_{offset}.png + a stderr [hero-geom] dump.
    static void RunHeroCollapseShot(AppHost host, Win32Window window, D3D12Device gpu)
    {
        try { System.IO.Directory.CreateDirectory(ProbeArtifacts.Dir); } catch { }
        window.SetClientSize(1280, 900);
        void Frame() { if (!window.IsClosed) { gpu.SuppressLatencyWaitOnce(); gpu.SuppressVsyncOnce(); host.RunFrame(); } }
        void Settle(int n) { for (int i = 0; i < n && !window.IsClosed; i++) Frame(); }
        void Shot(int off)
        {
            var px = gpu.CaptureBgra(out int cw, out int ch);
            PngWriter.WriteBgra(ProbeArtifacts.PathFor($"hero_{off:D3}.png"), px, cw, ch);
            Log.Info($"[hero-shot] wrote hero_{off:D3}.png ({cw}x{ch})");
        }

        WaveeShell.ProbeNav!("home", null); Settle(40); System.Threading.Thread.Sleep(700); Settle(20);
        WaveeShell.ProbeNav!("artist:spotify:artist:04gDigrS5kc9YWfZHwBETP", "Maroon 5");
        Settle(50); System.Threading.Thread.Sleep(2500); Settle(120);

        // List every scroller so a too-strict pick is obvious, then take the most-scrollable by content extent (lenient).
        NodeHandle vp = NodeHandle.Null; float bestContent = 0f;
        {
            var st = new Stack<NodeHandle>(); if (!host.Scene.Root.IsNull) st.Push(host.Scene.Root);
            while (st.Count > 0)
            {
                var n = st.Pop();
                if (n.IsNull || !host.Scene.IsLive(n)) continue;
                if (host.Scene.HasScroll(n) && host.Scene.TryGetScroll(n, out var s))
                {
                    var r = host.Scene.AbsoluteRect(n);
                    Log.Info($"[hero-shot]   scroller rect=({r.X:0},{r.Y:0} {r.W:0}x{r.H:0}) viewH={s.ViewportH:0} contentH={s.ContentH:0}");
                    if (s.ContentH > s.ViewportH + 1f && s.ContentH > bestContent) { bestContent = s.ContentH; vp = n; }
                }
                for (var c = host.Scene.FirstChild(n); !c.IsNull; c = host.Scene.NextSibling(c)) st.Push(c);
            }
        }
        if (vp.IsNull) { Log.Warn("[hero-shot] no scroll viewport — aborting"); return; }
        var vr = host.Scene.AbsoluteRect(vp);
        host.Scene.TryGetScroll(vp, out var s0);
        float vh = s0.ViewportH > 20f ? s0.ViewportH : vr.H;
        var pos = new Point2(vr.X + vr.W * 0.5f, vr.Y + vh * 0.5f);
        window.QueueInput(new InputEvent(InputKind.PointerMove, pos, 0, 0));
        Settle(2);
        Log.Info($"[hero-shot] viewport content {s0.ContentH:0} viewH {s0.ViewportH:0} @ ({pos.X:0},{pos.Y:0})");

        foreach (int target in new[] { 0, 90, 150, 210, 300, 410 })
        {
            // Approach gently: one small notch, let it settle (velocity ~0) before the next, so fling momentum doesn't
            // overshoot the low sample points.
            for (int i = 0; i < 200 && !window.IsClosed; i++)
            {
                host.Scene.TryGetScroll(vp, out var st);
                if (st.OffsetY >= target - 2f) break;
                window.QueueInput(new InputEvent(InputKind.Wheel, pos, 0, 0, MathF.Min(30f, target - st.OffsetY)));
                for (int k = 0; k < 5 && !window.IsClosed; k++) Frame();
            }
            Settle(6);
            host.Scene.TryGetScroll(vp, out var stNow);
            DumpCollapse(host.Scene, vp, stNow.OffsetY);
            Shot((int)stNow.OffsetY);
        }

        // Focus-regain regression (user report): with the hero fully collapsed, send REAL WM_ACTIVATE deactivate →
        // reactivate through the wndproc (flips Win32Window._active → the Mica re-theme + chrome epoch path) and
        // capture the steady frames after each — the collapsed hero band must NOT re-appear.
        host.Scene.TryGetScroll(vp, out var stC);
        DumpCollapse(host.Scene, vp, stC.OffsetY);
        Shot(900);   // baseline: collapsed, focused
        nint hwnd = window.Handle.Value;
        SendMessageW(hwnd, 0x0006 /*WM_ACTIVATE*/, 0 /*WA_INACTIVE*/, 0);
        Settle(20);
        host.Scene.TryGetScroll(vp, out var stB);
        DumpCollapse(host.Scene, vp, stB.OffsetY);
        Shot(901);   // blurred steady
        SendMessageW(hwnd, 0x0006 /*WM_ACTIVATE*/, 1 /*WA_ACTIVE*/, 0);
        Settle(3);
        Shot(902);   // regain +3 frames
        Settle(20);
        host.Scene.TryGetScroll(vp, out var stF);
        DumpCollapse(host.Scene, vp, stF.OffsetY);
        Shot(903);   // regain steady
        Log.Info("[hero-shot] done");
    }

    // Walk the scene under the scroll content and print any node carrying collapse state (a PresentedH override or a
    // ChildShiftY), with its pin shift (LocalTransform.Dy), its laid-out height, and its PARENT's height — the parent
    // height is the sticky containing-block clamp, so a parent that tightly equals the node's height means the pin can't
    // engage. Also prints the node's siblings so the gap between the collapsed hero and the following content is visible.
    static void DumpCollapse(FluentGpu.Scene.SceneStore scene, NodeHandle vp, float offset)
    {
        scene.TryGetScroll(vp, out var sc);
        var content = sc.ContentNode;
        NodeHandle collapse = NodeHandle.Null;
        var stack = new Stack<NodeHandle>();
        if (!content.IsNull) stack.Push(content);
        while (stack.Count > 0 && collapse.IsNull)
        {
            var n = stack.Pop();
            if (n.IsNull || !scene.IsLive(n)) continue;
            ref var p = ref scene.Paint(n);
            if (!float.IsNaN(p.PresentedH) || MathF.Abs(p.ChildShiftY) > 0.01f) { collapse = n; break; }
            for (var c = scene.FirstChild(n); !c.IsNull; c = scene.NextSibling(c)) stack.Push(c);
        }
        if (collapse.IsNull) { Log.Info($"[hero-geom] off={offset:0}: no collapse node (scroll-away mode)"); return; }
        ref var cp = ref scene.Paint(collapse);
        var cr = scene.AbsoluteRect(collapse);
        var par = scene.Parent(collapse);
        float parH = par.IsNull ? -1f : scene.Bounds(par).H;
        Log.Info(
            $"[hero-geom] off={offset:0}: collapse node rectY={cr.Y:0} boundsH={scene.Bounds(collapse).H:0} " +
            $"presentedH={cp.PresentedH:0} childShiftY={cp.ChildShiftY:0} pinShift(Dy)={cp.LocalTransform.Dy:0} " +
            $"| parentH={parH:0} (==boundsH ⇒ pin clamp=0)");
        if (!par.IsNull)
            for (var c = scene.FirstChild(par); !c.IsNull; c = scene.NextSibling(c))
            {
                var rc = scene.AbsoluteRect(c);
                Log.Info($"           sibling rectY={rc.Y:0} H={scene.Bounds(c).H:0} bottom={rc.Bottom:0}");
            }
    }

    // WAVEE_CONN_STRESS=1 (+ optional WAVEE_STRESS_N=100, WAVEE_STRESS_NOPACE=1): the user's scenario — SLOWLY click a Home
    // card (connected-animation fly to detail), WAIT for the nav to settle, go BACK (reverse fly), wait, repeat N times. For
    // every frame it captures BOTH the loop's RecommendedWaitMs (0 = display-rate, >0 = throttled to the 30Hz ambient cap)
    // AND the pure per-frame WORK cost (vsync suppressed). Headline metric = "await-dest throttle": with the
    // AnimIsAmbient(_connected) fix, EVERY in-flight (fly/mount) frame must be display-rate (wait==0) — any wait>0 there is
    // the stall that made connected animations "sometimes laggy". Cycles 1..K (K = distinct home cards) are COLD/uncached.
    sealed class Seg
    {
        public int Frames, Active, Over8, Over16, Gen0, MaxAwaitWait;
        public double MaxWorkMs;
        public long Alloc;
        public readonly List<int> FirstWaits = new(24);
        public readonly List<double> ActiveWork = new(64);
    }

    static void RunConnStress(AppHost host, Win32Window window, D3D12Device gpu)
    {
        int N = int.TryParse(Environment.GetEnvironmentVariable("WAVEE_STRESS_N"), out var nn) && nn > 0 ? nn : 100;
        bool realPace = !Diag.EnvFlag("WAVEE_STRESS_NOPACE");
        if (WaveeShell.ProbeCardNav is null) { Log.Warn("[conn-stress] ProbeCardNav not wired — aborting"); return; }
        Log.Info($"[conn-stress] {N} cycles (click->settle->back->settle); realistic pacing={realPace}");

        void Spin(int frames) { for (int f = 0; f < frames && !window.IsClosed; f++) { gpu.SuppressLatencyWaitOnce(); gpu.SuppressVsyncOnce(); host.RunFrame(); } }

        // Warmup: home live + card covers decoded (the fly needs an image source), then a clean GC baseline.
        WaveeShell.ProbeNav!("home", null); Spin(60);
        System.Threading.Thread.Sleep(900);
        Spin(20);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();

        var keys = new List<string>(); host.CollectMorphKeys(keys);
        if (keys.Count == 0) { Log.Warn("[conn-stress] no home-card morph keys — aborting"); return; }
        Log.Info($"[conn-stress] {keys.Count} distinct home-card keys (cycles 1..{keys.Count} are COLD/uncached)");

        // Drive one navigation to rest: capture per-frame RecommendedWaitMs (the perceived cadence) + pure WORK (vsync
        // removed). "Settled" = the transient fly+mount stops driving display-rate frames (4 consecutive non-display frames).
        Seg Drive()
        {
            var seg = new Seg();
            long a0 = GC.GetAllocatedBytesForCurrentThread(); int g0 = GC.CollectionCount(0);
            int consecIdle = 0;
            for (int f = 0; f < 90 && !window.IsClosed; f++)
            {
                int wait = host.RecommendedWaitMs();      // the pace the real loop would apply now (also feeds Fix 1's resync)
                if (seg.FirstWaits.Count < 24) seg.FirstWaits.Add(wait);
                if (f < 10 && wait > seg.MaxAwaitWait) seg.MaxAwaitWait = wait;   // throttle during the await-dest/early-fly window
                bool display = wait == 0;                 // a live fly/mount frame runs at display rate
                if (display) consecIdle = 0; else consecIdle++;
                if (realPace && wait > 0) System.Threading.Thread.Sleep(Math.Min(wait, 40));
                gpu.SuppressLatencyWaitOnce(); gpu.SuppressVsyncOnce();
                var s = host.RunFrame();
                seg.Frames++;
                if (display)
                {
                    seg.Active++;
                    seg.ActiveWork.Add(s.FrameMs);
                    if (s.FrameMs > seg.MaxWorkMs) seg.MaxWorkMs = s.FrameMs;
                    if (s.FrameMs > 8.33) seg.Over8++;
                    if (s.FrameMs > 16.7) seg.Over16++;
                }
                if (consecIdle >= 4) break;               // transient fly+mount done — only ambient/idle remains
            }
            seg.Alloc = GC.GetAllocatedBytesForCurrentThread() - a0;
            seg.Gen0 = GC.CollectionCount(0) - g0;
            return seg;
        }

        var fwd = new List<Seg>(N); var back = new List<Seg>(N);
        for (int i = 0; i < N && !window.IsClosed; i++)
        {
            WaveeShell.ProbeNav!("home", null); Spin(realPace ? 18 : 12);          // home = the fly's source card; let it settle
            WaveeShell.ProbeCardNav!(keys[i % keys.Count], null, true);            // CLICK -> connected-animation fly to detail
            fwd.Add(Drive());
            WaveeShell.ProbeBack?.Invoke();                                        // BACK -> reverse fly
            back.Add(Drive());
            if ((i + 1) % 10 == 0) Log.Info($"[conn-stress] {i + 1}/{N}");
        }

        var sb = new StringBuilder(8192);
        sb.AppendLine();
        sb.AppendLine($"=== WAVEE CONNECTED-ANIM STRESS — {N} cycles (click->settle->back->settle) ===");
        sb.AppendLine("await-throttle = an in-flight (fly/mount) frame the loop would THROTTLE to the 30Hz ambient cap — MUST be 0");
        sb.AppendLine("(that 30Hz stall was the 'connected anim sometimes laggy'); work = pure per-frame cost, vsync removed.");
        ReportSeg(sb, "FORWARD click->detail", fwd, keys.Count);
        ReportSeg(sb, "BACK    ->home", back, keys.Count);
        string rep = sb.ToString();
        Log.Info(rep);
        try { Directory.CreateDirectory(ProbeArtifacts.Dir); File.WriteAllText(ProbeArtifacts.PathFor("wavee-conn-stress.txt"), rep); Log.Info("[conn-stress] wrote " + ProbeArtifacts.PathFor("wavee-conn-stress.txt")); } catch { }
    }

    static void ReportSeg(StringBuilder sb, string title, List<Seg> segs, int coldCount)
    {
        sb.AppendLine();
        sb.AppendLine($"-- {title} — {segs.Count} cycles --");
        if (segs.Count == 0) { sb.AppendLine("  (none)"); return; }
        int throttledCycles = 0, maxAwait = 0;
        foreach (var s in segs) { if (s.MaxAwaitWait > 0) throttledCycles++; if (s.MaxAwaitWait > maxAwait) maxAwait = s.MaxAwaitWait; }
        sb.AppendLine($"  >> await-dest THROTTLE: {throttledCycles}/{segs.Count} cycles had a throttled in-flight frame (max wait {maxAwait}ms) — want 0/{segs.Count}");
        var work = new List<double>(8192); foreach (var s in segs) work.AddRange(s.ActiveWork);
        var wa = work.ToArray(); Array.Sort(wa);
        int over8 = 0, over16 = 0; foreach (var s in segs) { over8 += s.Over8; over16 += s.Over16; }
        sb.AppendLine($"  in-flight frames: {wa.Length} | WORK ms p50 {Pct(wa,50):0.00} p90 {Pct(wa,90):0.00} p99 {Pct(wa,99):0.00} max {(wa.Length>0?wa[^1]:0):0.00} | >8.33ms={over8} >16.7ms={over16}");
        var maxw = new double[segs.Count]; var settle = new double[segs.Count]; var alloc = new double[segs.Count];
        long totAlloc = 0; int totGc = 0;
        for (int i = 0; i < segs.Count; i++) { maxw[i] = segs[i].MaxWorkMs; settle[i] = segs[i].Frames; alloc[i] = segs[i].Alloc / 1024.0; totAlloc += segs[i].Alloc; totGc += segs[i].Gen0; }
        Array.Sort(maxw); Array.Sort(settle); Array.Sort(alloc);
        sb.AppendLine($"  per-cycle worst-frame ms: p50 {Pct(maxw,50):0.00} p90 {Pct(maxw,90):0.00} max {maxw[^1]:0.00}");
        sb.AppendLine($"  per-cycle frames-to-settle: p50 {Pct(settle,50):0} p90 {Pct(settle,90):0} max {settle[^1]:0}");
        sb.AppendLine($"  alloc/cycle KB: p50 {Pct(alloc,50):0} p90 {Pct(alloc,90):0} max {alloc[^1]:0} | total {totAlloc/1024/1024}MB Gen0={totGc}");
        if (segs.Count > coldCount && coldCount > 0)
        {
            double coldMax = 0, warmMax = 0; int cOver16 = 0, wOver16 = 0;
            for (int i = 0; i < segs.Count; i++)
            {
                bool cold = i < coldCount;
                foreach (var v in segs[i].ActiveWork) if (v > 16.7) { if (cold) cOver16++; else wOver16++; }
                if (cold) coldMax = Math.Max(coldMax, segs[i].MaxWorkMs); else warmMax = Math.Max(warmMax, segs[i].MaxWorkMs);
            }
            sb.AppendLine($"  COLD (1..{coldCount}) worst {coldMax:0.00}ms >16.7ms={cOver16}  |  WARM worst {warmMax:0.00}ms >16.7ms={wOver16}");
        }
        var w0 = segs[0].FirstWaits; var hd = new StringBuilder("  cycle#1 first waits(ms, 0=display rate): ");
        foreach (var x in w0) hd.Append(x).Append(' ');
        sb.AppendLine(hd.ToString());
    }

    sealed class Phase(string name)
    {
        public readonly string Name = name;
        public readonly List<double> Ms = new(4096);
        public int OverBudget;       // frames whose WORK exceeded one vblank (would drop a frame in vsync-paced use)
        public int OverBudgetGc;     // ...of those, frames on which a Gen0+ GC fired (a GC spike, not a work spike)
    }

    // WAVEE_HOME_SCROLL_PROBE=1: scroll-feel harness. Warms Home, exercises sidebar compact/expand/drag, scrolls Home
    // (image cards) + Liked (dense track rows) with real wheel input, then reports per-phase percentiles + hitch
    // attribution (fence/realize/alloc/blur/relayout-escape). Output defaults under .wavee-diagnostics; override with
    // WAVEE_PROBE_OUT. Pair with FG_SCROLL_TRACE=<path> for the full input→offset POD CSV.
    static void RunHomeScrollProbe(AppHost host, Win32Window window, D3D12Device gpu)
    {
        string? outDir = ProbeOutputDir();
        string? csvPath = outDir is null ? null : Path.Combine(outDir, "wavee-home-scroll-probe.csv");
        string? summaryPath = outDir is null ? null : Path.Combine(outDir, "wavee-home-scroll-probe-summary.txt");
        var csv = new StringBuilder(1 << 16);
        csv.AppendLine("phase,frame,label,frameMs,flushMs,rxFlushMs,vrRealizeMs,layoutMs,animMs,recordMs,submitMs,fenceWaitMs,presentMs,imagePumpMs,realizeMs,hotAlloc,rootEscapes,blurGroups,rendered,gen0,gen1,comps,nodes,draws,overBudget");
        bool keepVsync = Diag.EnvFlag("WAVEE_PROBE_VSYNC");

        // Rolling segment sums for scroll-phase attribution (mean of each Paint segment across measured frames).
        double sumFlush = 0, sumLayout = 0, sumAnim = 0, sumRecord = 0, sumSubmit = 0, sumFence = 0, sumPresent = 0, sumPump = 0, sumRealize = 0;
        double sumRx = 0, sumVr = 0;
        long sumHotAlloc = 0, sumRootEsc = 0, sumBlur = 0, sumComps = 0;
        int nAttrib = 0, renderedFrames = 0, hotAllocFrames = 0, rootEscFrames = 0;
        var censusDumps = new List<string>(64);

        FrameStats Measure(Phase phase, string label)
        {
            int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1);
            if (!keepVsync) { gpu.SuppressLatencyWaitOnce(); gpu.SuppressVsyncOnce(); }
            var s = host.RunFrame();
            int dg0 = GC.CollectionCount(0) - g0, dg1 = GC.CollectionCount(1) - g1;
            string census = host.LastRenderCensusDump;
            if (census.Length > 0) censusDumps.Add(phase.Name + ":" + label + " | " + census);
            if (s.Rendered || s.DrawCommandCount > 0)
            {
                phase.Ms.Add(s.FrameMs);
                bool over = s.FrameMs > BudgetMs;
                if (over) { phase.OverBudget++; if (dg0 > 0) phase.OverBudgetGc++; }
                sumFlush += s.FlushMs; sumLayout += s.LayoutMs; sumAnim += s.AnimMs; sumRecord += s.RecordMs; sumSubmit += s.SubmitMs;
                sumFence += s.FenceWaitMs; sumPresent += s.PresentMs; sumPump += s.ImagePumpMs; sumRealize += s.RealizeCatchupMs;
                sumRx += s.ReactiveFlushMs; sumVr += s.VirtualRealizeMs;
                sumHotAlloc += s.HotPhaseAllocBytes; sumRootEsc += s.RootRelayoutEscapes; sumBlur += s.BlurGroupCount;
                sumComps += s.ComponentsRendered; nAttrib++;
                if (s.Rendered) renderedFrames++;
                if (s.HotPhaseAllocBytes > 0) hotAllocFrames++;
                if (s.RootRelayoutEscapes > 0) rootEscFrames++;
                static string F(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);
                csv.Append(phase.Name).Append(',').Append(phase.Ms.Count.ToString(CultureInfo.InvariantCulture)).Append(',')
                   .Append(label).Append(',')
                   .Append(s.FrameMs.ToString("0.000", CultureInfo.InvariantCulture)).Append(',')
                   .Append(F(s.FlushMs)).Append(',').Append(F(s.ReactiveFlushMs)).Append(',').Append(F(s.VirtualRealizeMs)).Append(',')
                   .Append(F(s.LayoutMs)).Append(',').Append(F(s.AnimMs)).Append(',')
                   .Append(F(s.RecordMs)).Append(',').Append(F(s.SubmitMs)).Append(',')
                   .Append(F(s.FenceWaitMs)).Append(',').Append(F(s.PresentMs)).Append(',')
                   .Append(F(s.ImagePumpMs)).Append(',').Append(F(s.RealizeCatchupMs)).Append(',')
                   .Append(s.HotPhaseAllocBytes.ToString(CultureInfo.InvariantCulture)).Append(',')
                   .Append(s.RootRelayoutEscapes.ToString(CultureInfo.InvariantCulture)).Append(',')
                   .Append(s.BlurGroupCount.ToString(CultureInfo.InvariantCulture)).Append(',')
                   .Append(s.Rendered ? '1' : '0').Append(',')
                   .Append(dg0).Append(',').Append(dg1).Append(',')
                   .Append(s.ComponentsRendered).Append(',').Append(s.NodesVisited).Append(',').Append(s.DrawCommandCount).Append(',')
                   .Append(over ? '1' : '0').AppendLine();
            }
            return s;
        }

        void Nav(string key, string? arg) => WaveeShell.ProbeNav!(key, arg);
        void Settle(int n) { for (int i = 0; i < n && !window.IsClosed; i++) host.RunFrame(); }

        // Wheel burst on a named viewport (or largest content scroller when key is null). Returns whether offset moved.
        bool WheelScroll(Phase phase, string? viewportKey, string labelPrefix, int wheelFrames, int flickReps)
        {
            var viewport = viewportKey is null ? FindLargestScrollViewport(host.Scene) : FindScrollViewportByKey(host.Scene, viewportKey);
            if (viewport.IsNull)
            {
                Log.Info($"[home-scroll-probe] no scroll viewport for key={(viewportKey ?? "<largest>")}");
                return false;
            }
            var vr = host.Scene.AbsoluteRect(viewport);
            host.Scene.TryGetScroll(viewport, out var s0);
            float vh = s0.ViewportH > 20f ? s0.ViewportH : (vr.H > 20f ? vr.H : 400f);
            // Prefer content centre for liked/lists; home keeps the trailing-edge dead-strip repro (2-DIP thumb).
            float px = viewportKey == "home" ? vr.X + vr.W - 2f : vr.X + vr.W * 0.5f;
            var pos = new Point2(px, vr.Y + vh * 0.5f);
            window.QueueInput(new InputEvent(InputKind.PointerMove, pos, 0, 0));
            Settle(2);
            var routed0 = host.Input.ScrollableUnderForAxis(pos, wantHorizontal: false);
            Log.Info($"[home-scroll-probe] {labelPrefix} wheel @ ({pos.X:0},{pos.Y:0}) vp=n#{viewport.Raw.Index} routed=n#{routed0.Raw.Index} " +
                $"content {s0.ContentH:0} viewH {s0.ViewportH:0}");

            for (int i = 0; i < wheelFrames && !window.IsClosed; i++)
            {
                window.QueueInput(new InputEvent(InputKind.Wheel, pos, 0, 0, ((i / 30) & 1) == 0 ? +60f : -60f));
                Measure(phase, labelPrefix + "-wheel");
            }
            for (int rep = 0; rep < flickReps && !window.IsClosed; rep++)
            {
                for (int k = 0; k < 6 && !window.IsClosed; k++)
                {
                    window.QueueInput(new InputEvent(InputKind.Wheel, pos, 0, 0, +60f));
                    Measure(phase, labelPrefix + "-flick");
                }
                for (int st = 0; st < 16 && !window.IsClosed; st++) Measure(phase, labelPrefix + "-coast");
            }
            host.Scene.TryGetScroll(viewport, out var s1);
            bool moved = MathF.Abs(s1.OffsetY - s0.OffsetY) > 1f;
            Log.Info($"[home-scroll-probe] {labelPrefix} endOff {s1.OffsetY:0} — wheel {(moved ? "moved" : "DID NOT MOVE")}");
            return moved;
        }

        Log.Info("[home-scroll-probe] warmup");
        for (int i = 0; i < 80 && !window.IsClosed; i++) { gpu.SuppressLatencyWaitOnce(); gpu.SuppressVsyncOnce(); host.RunFrame(); }
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();

        var sidebar = new Phase("sidebar-home");
        var homeScroll = new Phase("home-scroll");
        var likedScroll = new Phase("liked-scroll");
        var navPhase = new Phase("nav");

        Nav("home", null);
        for (int i = 0; i < 40 && !window.IsClosed; i++) Measure(navPhase, "home-mount");
        System.Threading.Thread.Sleep(700);     // let the Home feed and visible art settle before the sidebar scenario
        for (int i = 0; i < 30 && !window.IsClosed; i++) Measure(navPhase, "home-settle");

        if (WaveeShell.ProbeSidebarCompact is null || WaveeShell.ProbeSidebarDragBegin is null ||
            WaveeShell.ProbeSidebarDragWidth is null || WaveeShell.ProbeSidebarDragEnd is null)
        {
            Log.Warn("[home-scroll-probe] sidebar hooks unavailable; skipping sidebar state phase");
        }
        else
        {
            Log.Info("[home-scroll-probe] sidebar compact -> expanded -> drag-resize");
            WaveeShell.ProbeSidebarCompact(true);
            for (int f = 0; f < 18 && !window.IsClosed; f++) Measure(sidebar, "compact");
            WaveeShell.ProbeSidebarCompact(false);
            for (int f = 0; f < 26 && !window.IsClosed; f++) Measure(sidebar, "expand");

            WaveeShell.ProbeSidebarDragBegin();
            foreach (float w in new[] { 300f, 340f, 380f, 420f, 360f, 300f, 260f, 330f })
            {
                WaveeShell.ProbeSidebarDragWidth(w);
                Measure(sidebar, "drag");
            }
            WaveeShell.ProbeSidebarDragEnd();
            for (int f = 0; f < 12 && !window.IsClosed; f++) Measure(sidebar, "drag-end");
        }

        // Home (image-card feed) — trailing-edge wheel target keeps the dead-strip routing regression covered.
        WheelScroll(homeScroll, "home", "home", wheelFrames: 180, flickReps: 4);

        // Liked (dense TrackRow list) — KeepAlive cross-slot swap is measured here (navPhase).
        Nav("liked", null);
        for (int i = 0; i < 50 && !window.IsClosed; i++) Measure(navPhase, "liked-mount");
        System.Threading.Thread.Sleep(400);
        for (int i = 0; i < 20 && !window.IsClosed; i++) Measure(navPhase, "liked-settle");
        WheelScroll(likedScroll, null, "liked", wheelFrames: 120, flickReps: 3);

        // Reactivate / real-cadence checks stay on Home (routing regression + ambient-throttle detection).
        Nav("home", null);
        for (int i = 0; i < 30 && !window.IsClosed; i++) Measure(navPhase, "home-return");
        var homeVp = FindScrollViewportByKey(host.Scene, "home");
        double realLoopMeanMs = 0, realLoopWorstMs = 0;
        int realLoopOver16 = 0, realLoopThrottled = 0, realLoopMaxWait = 0;
        int realLoopScrollActive = 0, realLoopWaitAsync = 0, realLoopWaitAmbient = 0, realLoopWaitOther = 0;
        ulong realLoopPresents = 0, realLoopDrainPresents = 0;
        double realLoopPresentFps = 0, realLoopTrailingPresentFps = 0;
        float realLoopOffsetSpan = 0f;
        if (!homeVp.IsNull)
        {
            var vr = host.Scene.AbsoluteRect(homeVp);
            host.Scene.TryGetScroll(homeVp, out var s0);
            float vh = s0.ViewportH > 20f ? s0.ViewportH : (vr.H > 20f ? vr.H : 400f);
            // Keep this inside content, not the trailing-edge scrollbar/dead strip used by the routing regression above.
            // The old X=right-2 target could land on a wheel-handling child after the Home remount; then this block timed
            // an ambient loop while claiming it was scrolling.
            var pos = new Point2(vr.X + vr.W * 0.5f, vr.Y + vh * 0.5f);

            window.QueueInput(new InputEvent(InputKind.WindowBlur, default, 0, 0));
            host.RunFrame();
            for (int i = 0; i < 145 && !window.IsClosed; i++)
            {
                System.Threading.Thread.Sleep(16);
                host.RunFrame();
            }
            host.Scene.TryGetScroll(homeVp, out var hidden);
            host.Scene.ScrollChrome.TryGet((int)homeVp.Raw.Index, out var hiddenChrome);   // FadeT/PointerOver moved off ScrollState
            Log.Info($"[home-scroll-reactivate] hidden fade={hiddenChrome.FadeT:0.00} pointerOver={hiddenChrome.PointerOver} off={hidden.OffsetY:0}");

            window.QueueInput(new InputEvent(InputKind.WindowFocus, default, 0, 0));
            window.QueueInput(new InputEvent(InputKind.PointerMove, pos, 0, 0));
            for (int i = 0; i < 8 && !window.IsClosed; i++) { System.Threading.Thread.Sleep(16); host.RunFrame(); }
            host.Scene.TryGetScroll(homeVp, out var focused);
            var routedAfterFocus = host.Input.ScrollableUnderForAxis(pos, wantHorizontal: false);
            float beforeReactivatedWheel = focused.OffsetY;
            float maxReactivated = MathF.Max(0f, focused.ContentH - focused.ViewportH);
            float reactivatedDelta = beforeReactivatedWheel > maxReactivated * 0.5f ? -60f : 60f;
            for (int i = 0; i < 24 && !window.IsClosed; i++)
            {
                window.QueueInput(new InputEvent(InputKind.Wheel, pos, 0, 0, reactivatedDelta));
                System.Threading.Thread.Sleep(8);
                host.RunFrame();
            }
            host.Scene.TryGetScroll(homeVp, out var reactivated);
            host.Scene.ScrollChrome.TryGet((int)homeVp.Raw.Index, out var reactivatedChrome);   // FadeT/PointerOver moved off ScrollState
            bool reactivatedMoved = MathF.Abs(reactivated.OffsetY - beforeReactivatedWheel) > 1f;
            bool reactivatedBar = reactivatedChrome.FadeT > 0.5f;
            Log.Info($"[home-scroll-reactivate] home=n#{homeVp.Raw.Index} routed=n#{routedAfterFocus.Raw.Index} fade={reactivatedChrome.FadeT:0.00} pointerOver={reactivatedChrome.PointerOver} " +
                $"off={beforeReactivatedWheel:0}->{reactivated.OffsetY:0} wheel={(reactivatedMoved ? "PASS" : "FAIL")} bar={(reactivatedBar ? "PASS" : "FAIL")}");

            // Real app-loop cadence: production order is RunFrame -> RecommendedWaitMs -> WaitForWork. The old probe
            // queried/slept BEFORE RunFrame, so the just-queued synthetic wheel had not armed ScrollAnim yet and every
            // sample inherited the ambient 60-Hz timeout. Drive a movable target, then record successful presents and
            // wait-branch attribution so "low FPS" is split into production pacing vs render/present cadence.
            var iv = new List<double>(220);
            ulong presentStart = host.PresentedSequence;
            long loopStart = Stopwatch.GetTimestamp();
            long prev = 0;
            float minOffset = reactivated.OffsetY, maxOffsetSeen = reactivated.OffsetY;
            float direction = reactivated.OffsetY > maxReactivated * 0.5f ? -60f : 60f;
            for (int i = 0; i < 200 && !window.IsClosed; i++)
            {
                if (i > 0 && i % 40 == 0) direction = -direction;
                host.Scene.TryGetScroll(homeVp, out var before);
                if (before.OffsetY <= 0.5f) direction = +60f;
                else if (before.OffsetY >= maxReactivated - 0.5f) direction = -60f;
                window.QueueInput(new InputEvent(InputKind.Wheel, pos, 0, 0, direction));
                host.RunFrame();
                long now = Stopwatch.GetTimestamp();
                if (prev != 0) iv.Add((now - prev) * 1000.0 / Stopwatch.Frequency);
                prev = now;
                if ((host.CurrentWakeReasons & WakeReasons.ScrollAnim) != 0) realLoopScrollActive++;
                host.Scene.TryGetScroll(homeVp, out var after);
                minOffset = MathF.Min(minOffset, after.OffsetY);
                maxOffsetSeen = MathF.Max(maxOffsetSeen, after.OffsetY);

                int wait = host.RecommendedWaitMs();
                if (wait > 0) { realLoopThrottled++; if (wait > realLoopMaxWait) realLoopMaxWait = wait; }
                if (host.LastWaitKind is HostWaitKind.DisplayTick or HostWaitKind.SoftwarePace) realLoopWaitAsync++;
                else if (host.LastWaitKind == HostWaitKind.Ambient) realLoopWaitAmbient++;
                else realLoopWaitOther++;
                // A synthetic queued event is not an HWND message and therefore cannot wake an infinite idle wait.
                // Clamp that diagnostic-only case; a correctly armed scroll returns a display-paced wait here.
                window.WaitForWork(wait < 0 ? 8 : Math.Min(wait, 40));
            }
            long loopEnd = Stopwatch.GetTimestamp();
            ulong presentAtLoopEnd = host.PresentedSequence;
            realLoopPresents = presentAtLoopEnd - presentStart;
            double loopSec = (loopEnd - loopStart) / (double)Stopwatch.Frequency;
            realLoopPresentFps = loopSec > 0 ? realLoopPresents / loopSec : 0;
            realLoopTrailingPresentFps = host.PresentFps;
            System.Threading.Thread.Sleep(80);   // async-drain accounting only — deliberately OUTSIDE the active FPS denominator
            realLoopDrainPresents = host.PresentedSequence - presentAtLoopEnd;
            realLoopOffsetSpan = maxOffsetSeen - minOffset;
            var a = iv.ToArray();
            double tot = 0;
            foreach (var v in a) { tot += v; if (v > realLoopWorstMs) realLoopWorstMs = v; if (v > 16.7) realLoopOver16++; }
            realLoopMeanMs = a.Length > 0 ? tot / a.Length : 0;
            Log.Info($"[home-scroll-fps] REAL app-loop wheel scroll: {a.Length} frames mean {realLoopMeanMs:0.0}ms ({(realLoopMeanMs > 0 ? 1000.0 / realLoopMeanMs : 0):0} fps) worst {realLoopWorstMs:0.0}ms ({(realLoopWorstMs > 0 ? 1000.0 / realLoopWorstMs : 0):0} fps) >16.7ms(<60fps)={realLoopOver16} " +
                $"offsetSpan={realLoopOffsetSpan:0} scrollActive={realLoopScrollActive}/200 waits(async/ambient/other)={realLoopWaitAsync}/{realLoopWaitAmbient}/{realLoopWaitOther} " +
                $"activePresents={realLoopPresents} activePresentFps={realLoopPresentFps:0.0} trailingPresentFps={realLoopTrailingPresentFps:0.0} drainPresents={realLoopDrainPresents} " +
                $"throttledWaitFrames={realLoopThrottled} maxWait={realLoopMaxWait}ms");
        }

        var phases = new[] { sidebar, homeScroll, likedScroll, navPhase };
        var all = new Phase("ALL");
        foreach (var p in phases) { all.Ms.AddRange(p.Ms); all.OverBudget += p.OverBudget; all.OverBudgetGc += p.OverBudgetGc; }

        var sb = new StringBuilder(8192);
        sb.AppendLine();
        sb.AppendLine("=== WAVEE HOME SCROLL PROBE - per-frame production time (ms); target < 8.33 ms at 120 Hz ===");
        sb.AppendLine(keepVsync ? "(WAVEE_PROBE_VSYNC: vblank-paced)" : "(vsync/latency throttle removed -> pure work cost)");
        sb.AppendLine();
        sb.AppendLine($"{"phase",-14} {"n",5} {"p50",6} {"p90",6} {"p99",7} {"p99.9",7} {"max",8}   {"over8.3ms",10} {"(ofwhich GC)",12}");
        foreach (var p in phases) sb.AppendLine(Format(p));
        sb.AppendLine(new string('-', 96));
        sb.AppendLine(Format(all));

        if (nAttrib > 0)
        {
            double inv = 1.0 / nAttrib;
            sb.AppendLine();
            sb.AppendLine("=== segment attribution (mean ms / measured frame) ===");
            sb.AppendLine($"  flush={sumFlush * inv:0.00}  rx={sumRx * inv:0.00}  vr={sumVr * inv:0.00}  layout={sumLayout * inv:0.00}  anim={sumAnim * inv:0.00}  record={sumRecord * inv:0.00}  submit={sumSubmit * inv:0.00}");
            sb.AppendLine($"  fenceWait={sumFence * inv:0.00}  present={sumPresent * inv:0.00}  imagePump={sumPump * inv:0.00}  realizeCatchup={sumRealize * inv:0.00}");
            sb.AppendLine($"  hotAllocBytes/frame={sumHotAlloc * inv:0.0}  rootRelayoutEscapes/frame={sumRootEsc * inv:0.00}  blurGroups/frame={sumBlur * inv:0.00}  compsRendered/frame={sumComps * inv:0.00}");
            sb.AppendLine($"  renderedFrames={renderedFrames}/{nAttrib}  hotAllocFrames={hotAllocFrames}  rootEscapeFrames={rootEscFrames}");
        }

        if (censusDumps.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"=== FG_RENDER_CENSUS dumps ({censusDumps.Count}) ===");
            int show = Math.Min(40, censusDumps.Count);
            for (int i = 0; i < show; i++) sb.AppendLine("  " + censusDumps[i]);
            if (censusDumps.Count > show) sb.AppendLine($"  … +{censusDumps.Count - show} more");
        }

        var rows = new List<(string Tag, double Ms, double Flush, double Layout, double Anim, double Record, double Submit, double Fence, double Realize, long Hot, int Esc, int G0)>();
        foreach (var line in csv.ToString().Split('\n'))
        {
            var c = line.Split(',');
            if (c.Length < 25 || c[0] == "phase") continue;
            double D(int i) => double.TryParse(c[i], NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;
            // cols: frameMs(3) flush(4) rx(5) vr(6) layout(7) anim(8) record(9) submit(10) fence(11) … realize(14) hot(15) esc(16) … gen0(19)
            long.TryParse(c[15], NumberStyles.Integer, CultureInfo.InvariantCulture, out long hot);
            int.TryParse(c[16], NumberStyles.Integer, CultureInfo.InvariantCulture, out int esc);
            int.TryParse(c[19], NumberStyles.Integer, CultureInfo.InvariantCulture, out int g0);
            rows.Add((c[0] + ":" + c[2], D(3), D(4), D(7), D(8), D(9), D(10), D(11), D(14), hot, esc, g0));
        }
        rows.Sort((a, b) => b.Ms.CompareTo(a.Ms));
        sb.AppendLine();
        sb.AppendLine($"Worst 12 frames - {"total",7} = flush+layout+anim+record+submit  (fence / realize / hotAlloc / esc / gc)");
        for (int i = 0; i < Math.Min(12, rows.Count); i++)
        {
            var r = rows[i];
            sb.AppendLine($"  {r.Ms,7:0.00} = {r.Flush,5:0.00}+{r.Layout,5:0.00}+{r.Anim,5:0.00}+{r.Record,5:0.00}+{r.Submit,5:0.00}  fence={r.Fence:0.00} realize={r.Realize:0.00} hot={r.Hot} esc={r.Esc} gc={r.G0}  {r.Tag}");
        }

        // Auto-verdicts: classify the likely "heavy scroll" cause from measured work (not present-paced).
        sb.AppendLine();
        sb.AppendLine("=== VERDICTS ===");
        if (realLoopMeanMs > 0)
            sb.AppendLine($"  real-loop proof: offsetSpan={realLoopOffsetSpan:0} scrollActive={realLoopScrollActive}/200 " +
                $"waits(async/ambient/other)={realLoopWaitAsync}/{realLoopWaitAmbient}/{realLoopWaitOther} " +
                $"activePresents={realLoopPresents} activePresentFps={realLoopPresentFps:0.0} " +
                $"trailingPresentFps={realLoopTrailingPresentFps:0.0} drainPresents={realLoopDrainPresents}");
        AppendScrollVerdicts(sb, homeScroll, likedScroll, all, nAttrib, sumFlush, sumLayout, sumAnim, sumRecord, sumSubmit,
            sumFence, sumPresent, sumPump, sumRealize, sumHotAlloc, sumRootEsc, sumBlur, sumComps,
            renderedFrames, hotAllocFrames, rootEscFrames,
            realLoopMeanMs, realLoopWorstMs, realLoopOver16, realLoopThrottled, realLoopMaxWait);

        string report = sb.ToString();
        Log.Info(report);
        if (csvPath is not null) WriteProbeFile(csvPath, csv.ToString(), "home-scroll-probe");
        if (summaryPath is not null) WriteProbeFile(summaryPath, report, "home-scroll-probe");
    }

    static void AppendScrollVerdicts(StringBuilder sb, Phase home, Phase liked, Phase all, int nAttrib,
        double sumFlush, double sumLayout, double sumAnim, double sumRecord, double sumSubmit,
        double sumFence, double sumPresent, double sumPump, double sumRealize, long sumHotAlloc, long sumRootEsc, long sumBlur, long sumComps,
        int renderedFrames, int hotAllocFrames, int rootEscFrames,
        double realMean, double realWorst, int realOver16, int realThrottled, int realMaxWait)
    {
        if (all.Ms.Count == 0) { sb.AppendLine("  (no measured frames — probe did not paint)"); return; }
        var sorted = all.Ms.ToArray(); Array.Sort(sorted);
        double p50 = Pct(sorted, 50), p90 = Pct(sorted, 90), p99 = Pct(sorted, 99), max = sorted[^1];
        double inv = nAttrib > 0 ? 1.0 / nAttrib : 0;
        double mFlush = sumFlush * inv, mLayout = sumLayout * inv, mAnim = sumAnim * inv, mRecord = sumRecord * inv, mSubmit = sumSubmit * inv;
        double mFence = sumFence * inv, mPump = sumPump * inv, mRealize = sumRealize * inv;
        double mHot = sumHotAlloc * inv, mEsc = sumRootEsc * inv, mBlur = sumBlur * inv, mComps = sumComps * inv;

        // Dominant segment among flush/layout/anim/record/(submit−fence).
        var segs = new (string Name, double Ms)[] { ("flush", mFlush), ("layout", mLayout), ("anim", mAnim), ("record", mRecord), ("submit-cpu", Math.Max(0, mSubmit - mFence - sumPresent * inv)) };
        Array.Sort(segs, (a, b) => b.Ms.CompareTo(a.Ms));
        string dominant = segs[0].Name;

        bool workOk120 = p90 <= BudgetMs;
        bool workOk60 = p90 <= 16.7;
        sb.AppendLine($"  work p50={p50:0.00}ms p90={p90:0.00}ms p99={p99:0.00}ms max={max:0.00}ms  over8.3={all.OverBudget}/{all.Ms.Count} (gc={all.OverBudgetGc})");
        sb.AppendLine(workOk120
            ? "  PASS 120Hz work budget: p90 ≤ 8.33 ms — CPU work is not the limiter on a 120 Hz panel."
            : workOk60
                ? "  WARN 120Hz work budget: p90 > 8.33 ms but ≤ 16.7 ms — drops 120→60 under load; feels 'heavy' on high-Hz panels."
                : "  FAIL 60Hz work budget: p90 > 16.7 ms — visible stutter; CPU/GPU work exceeds one 60 Hz frame.");

        if (home.Ms.Count > 0 && liked.Ms.Count > 0)
        {
            var h = home.Ms.ToArray(); Array.Sort(h);
            var l = liked.Ms.ToArray(); Array.Sort(l);
            double hp90 = Pct(h, 90), lp90 = Pct(l, 90);
            sb.AppendLine(lp90 > hp90 * 1.25
                ? $"  LIKED heavier than HOME: liked p90={lp90:0.00}ms vs home p90={hp90:0.00}ms — dense TrackRow / virtualize path is the likely feel culprit."
                : hp90 > lp90 * 1.25
                    ? $"  HOME heavier than LIKED: home p90={hp90:0.00}ms vs liked p90={lp90:0.00}ms — image-card / blur / art decode path dominates."
                    : $"  HOME≈LIKED: home p90={hp90:0.00}ms liked p90={lp90:0.00}ms — cost is shared (shell/chrome/player), not page-local.");
        }

        sb.AppendLine($"  dominant segment: {dominant} ({segs[0].Ms:0.00} ms/frame mean)");
        if (mFence > 2.0) sb.AppendLine($"  GPU-bound signal: mean fenceWait={mFence:0.00}ms — GPU/present pacing, not UI reconcile.");
        if (mRealize > 1.0) sb.AppendLine($"  virtualize hitch: mean realizeCatchup={mRealize:0.00}ms — fling realizes windows mid-scroll.");
        if (mPump > 1.0) sb.AppendLine($"  image-pump hitch: mean imagePump={mPump:0.00}ms — decode/upload during scroll.");
        if (mEsc > 0.05) sb.AppendLine($"  layout-escape: mean rootRelayoutEscapes={mEsc:0.00}/frame ({rootEscFrames} frames) — missing ClipToBounds boundary on a hot subtree.");
        if (hotAllocFrames > nAttrib * 0.05) sb.AppendLine($"  hot-phase alloc: {hotAllocFrames}/{nAttrib} frames alloc'd (mean {mHot:0.0} B) — phases 6–13 should be 0.");
        if (mComps > 2.0) sb.AppendLine($"  re-render during scroll: mean compsRendered={mComps:0.00}/frame (rendered={renderedFrames}/{nAttrib}) — a signal/bind is forcing component work on the scroll path.");
        if (mBlur > 2.0) sb.AppendLine($"  blur load: mean blurGroups={mBlur:0.00}/frame — acrylic/backdrop cost rides along with scroll.");
        if (realMean > 0)
        {
            sb.AppendLine($"  real app-loop: mean {realMean:0.0}ms ({(realMean > 0 ? 1000.0 / realMean : 0):0} fps) worst {realWorst:0.0}ms >16.7ms={realOver16} throttledWait={realThrottled} maxWait={realMaxWait}ms");
            if (realThrottled > 0 && realMaxWait >= 8)
                sb.AppendLine("  ambient throttle during scroll: RecommendedWaitMs > 0 while wheeling — scroll grace/hold may be failing to keep display-rate pacing.");
        }
        if (workOk120 && mFence < 1.0 && mRealize < 0.5 && hotAllocFrames == 0 && mEsc < 0.01 && realOver16 == 0)
            sb.AppendLine("  OVERALL: work path looks clean. If the app still 'feels heavy', suspect input latency / async present / subjective motion (not CPU frame budget).");
    }

    // WAVEE_RAIL_PROBE=1 (projected-motion P0): the dedicated rail/sidebar transition perf harness. Wide-window (rail
    // DOCKS) scenario driving (a) rail open→settle→close→settle ×3, (b) rapid rail reversal (toggle mid-flight), (c)
    // sidebar compact toggle ×3, (d) a synthesized sidebar-grip drag sweep. Per-frame CSV rows carry the segment ms PLUS
    // the FlexLayout measure/arrange/text-reshape counters (need FG_LAYOUT_DIAG=1 to be non-zero) and span
    // reused/rebased/re-recorded from FrameStats — so a Reflow-per-tick regression shows up as non-zero measure/arrange on
    // every animation tick, and the projected (Reveal/FLIP) fix as ~0 on ticks (only the commit frame is large).
    // WAVEE_RAIL_BASELINE=1 selects the pre-fix Reflow path from the same build for an A/B. Output → <logs>\artifacts\projected-motion
    // (override with WAVEE_PROBE_OUT); writes wavee-rail-probe.csv + -summary.txt + rail_*.png first/mid/final captures.
    static void RunRailProbe(AppHost host, Win32Window window, D3D12Device gpu)
    {
        string outDir = Environment.GetEnvironmentVariable("WAVEE_PROBE_OUT") ?? "";
        if (string.IsNullOrWhiteSpace(outDir)) outDir = ProbeArtifacts.PathFor("projected-motion");
        try { Directory.CreateDirectory(outDir); } catch { }
        string csvPath = Path.Combine(outDir, "wavee-rail-probe.csv");
        string summaryPath = Path.Combine(outDir, "wavee-rail-probe-summary.txt");
        bool baseline = Diag.EnvFlag("WAVEE_RAIL_BASELINE");
        bool keepVsync = Diag.EnvFlag("WAVEE_PROBE_VSYNC");
        var csv = new StringBuilder(1 << 16);
        csv.AppendLine("phase,frame,label,frameMs,flushMs,layoutMs,animMs,recordMs,submitMs,measure,arrange,textMiss,spansReused,spansRebased,spansRerec,depthAborts,gen0,comps,nodes,draws,overBudget");

        window.SetClientSize(1700, 950);   // wide → the rail DOCKS (RailFits) — inline reserved width, the reported mode

        FrameStats Measure(Phase phase, string label)
        {
            int g0 = GC.CollectionCount(0);
            if (!keepVsync) { gpu.SuppressLatencyWaitOnce(); gpu.SuppressVsyncOnce(); }
            var s = host.RunFrame();
            int dg0 = GC.CollectionCount(0) - g0;
            if (s.Rendered || s.DrawCommandCount > 0)
            {
                phase.Ms.Add(s.FrameMs);
                bool over = s.FrameMs > BudgetMs;
                if (over) { phase.OverBudget++; if (dg0 > 0) phase.OverBudgetGc++; }
                static string F(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);
                csv.Append(phase.Name).Append(',').Append(phase.Ms.Count.ToString(CultureInfo.InvariantCulture)).Append(',')
                   .Append(label).Append(',')
                   .Append(s.FrameMs.ToString("0.000", CultureInfo.InvariantCulture)).Append(',')
                   .Append(F(s.FlushMs)).Append(',').Append(F(s.LayoutMs)).Append(',').Append(F(s.AnimMs)).Append(',')
                   .Append(F(s.RecordMs)).Append(',').Append(F(s.SubmitMs)).Append(',')
                   .Append(s.MeasureCount).Append(',').Append(s.ArrangeCount).Append(',').Append(s.TextShapeMisses).Append(',')
                   .Append(s.SpansReused).Append(',').Append(s.SpansRebased).Append(',').Append(s.SpansReRecorded).Append(',')
                   .Append(s.DepthAborts).Append(',')   // must stay 0: a nonzero frame painted an incomplete page (recorder out of stack)
                   .Append(dg0).Append(',').Append(s.ComponentsRendered).Append(',').Append(s.NodesVisited).Append(',').Append(s.DrawCommandCount).Append(',')
                   .Append(over ? '1' : '0').AppendLine();
            }
            return s;
        }

        void Nav(string k, string? a) => WaveeShell.ProbeNav!(k, a);
        void Settle(int n) { for (int i = 0; i < n && !window.IsClosed; i++) host.RunFrame(); }
        void Shot(string name)
        {
            var px = gpu.CaptureBgra(out int cw, out int ch);
            PngWriter.WriteBgra(Path.Combine(outDir, $"rail_{name}.png"), px, cw, ch);
            Log.Info($"[rail-probe] wrote rail_{name}.png ({cw}x{ch})");
        }

        Log.Info($"[rail-probe] {(baseline ? "BASELINE (SizeMode.Reflow)" : "P1 (Reveal/FLIP)")} out={outDir}");
        for (int i = 0; i < 80 && !window.IsClosed; i++) { gpu.SuppressLatencyWaitOnce(); gpu.SuppressVsyncOnce(); host.RunFrame(); }
        Nav("home", null); Settle(40); System.Threading.Thread.Sleep(700); Settle(30);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();

        if (WaveeShell.ProbeRailOpen is null || WaveeShell.ProbeSidebarCompact is null)
        {
            Log.Warn("[rail-probe] rail/sidebar hooks not wired — aborting");
            return;
        }

        var railOC = new Phase("rail-open-close");
        var railOCTracks = new Phase("rail-open-close-tracks");
        var railRev = new Phase("rail-reversal");
        var sbToggle = new Phase("sidebar-toggle");
        var sbDrag = new Phase("sidebar-drag");

        // (a0) THE leg this probe existed without: a rail toggle over a real TRACK LIST. On "home" the rail toggle moves
        // chrome only — it never crosses a track-list breakpoint, so the single most expensive frame of the whole gesture
        // (the column-set commit: every realized row re-renders, the grid re-arranges, the title lane re-truncates) was
        // simply not measured. The acceptance bar lives here: measure/arrange non-zero on the COMMIT frame only, ~0 on
        // every animation tick, and no tick over the 8.33 ms 120 Hz budget.
        Log.Info("[rail-probe] (a0) rail open/close x3 over a track list");
        Nav("liked", null); Settle(60); System.Threading.Thread.Sleep(700); Settle(30);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        for (int rep = 0; rep < 3 && !window.IsClosed; rep++)
        {
            WaveeShell.ProbeRailOpen!(true);
            for (int f = 0; f < 40 && !window.IsClosed; f++)
            {
                Measure(railOCTracks, "open");
                if (rep == 0 && f == 1) Shot("tracks_open_commit");
                if (rep == 0 && f == 8) Shot("tracks_open_mid");
            }
            if (rep == 0) Shot("tracks_open_final");
            WaveeShell.ProbeRailOpen!(false);
            for (int f = 0; f < 40 && !window.IsClosed; f++)
            {
                Measure(railOCTracks, "close");
                if (rep == 0 && f == 8) Shot("tracks_close_mid");
            }
            if (rep == 0) Shot("tracks_close_final");
        }
        Nav("home", null); Settle(40);

        // (a) rail open→settle→close→settle ×3 (first cycle captures first/mid/final PNGs of both legs).
        Log.Info("[rail-probe] (a) rail open/close x3");
        for (int rep = 0; rep < 3 && !window.IsClosed; rep++)
        {
            WaveeShell.ProbeRailOpen!(true);
            for (int f = 0; f < 40 && !window.IsClosed; f++)
            {
                Measure(railOC, "open");
                if (rep == 0 && f == 1) Shot("open_first");
                if (rep == 0 && f == 8) Shot("open_mid");
            }
            if (rep == 0) Shot("open_final");
            WaveeShell.ProbeRailOpen!(false);
            for (int f = 0; f < 40 && !window.IsClosed; f++)
            {
                Measure(railOC, "close");
                if (rep == 0 && f == 8) Shot("close_mid");
            }
            if (rep == 0) Shot("close_final");
        }

        // (b) rapid reversal — flip the target every 6 frames so each toggle interrupts an in-flight transition.
        Log.Info("[rail-probe] (b) rapid rail reversal");
        bool open = false;
        for (int i = 0; i < 120 && !window.IsClosed; i++)
        {
            if (i % 6 == 0) { open = !open; WaveeShell.ProbeRailOpen!(open); }
            Measure(railRev, open ? "toward-open" : "toward-close");
        }
        WaveeShell.ProbeRailOpen!(false); Settle(40);

        // (c) sidebar compact toggle ×3 (56↔expanded).
        Log.Info("[rail-probe] (c) sidebar compact toggle x3");
        for (int rep = 0; rep < 3 && !window.IsClosed; rep++)
        {
            WaveeShell.ProbeSidebarCompact!(true);
            for (int f = 0; f < 30 && !window.IsClosed; f++)
            {
                Measure(sbToggle, "compact");
                if (rep == 0 && f == 1) Shot("sb_compact_first");
                if (rep == 0 && f == 8) Shot("sb_compact_mid");
            }
            if (rep == 0) Shot("sb_compact_final");
            WaveeShell.ProbeSidebarCompact!(false);
            for (int f = 0; f < 30 && !window.IsClosed; f++)
            {
                Measure(sbToggle, "expand");
                if (rep == 0 && f == 1) Shot("sb_expand_first");
                if (rep == 0 && f == 8) Shot("sb_expand_mid");
            }
            if (rep == 0) Shot("sb_expand_final");
        }

        // (c2) sidebar-toggle-via-synthetic-click — the DISCRIMINATION leg. Section (c) flips the compact signal DIRECTLY
        // (WaveeShell.ProbeSidebarCompact) and the pane/content transition ANIMATES. This leg instead injects a REAL pointer
        // click on the hamburger through the SAME queue the WndProc feeds (Win32Window.QueueInput → PumpInto → dispatcher
        // hit-test → the IconButton's click → `_sidebarCompact.Value = !…`), so the toggle travels the exact production
        // click→reconcile path a mouse press takes — NOT a signal bypass. If THIS snaps while (c) animates, the cause lives
        // in the click/reconcile ORDERING (e.g. the click's flush vs CaptureProjections), not the animation seed. It runs
        // with REAL vsync pacing (no latency/vsync suppression) so it reproduces the exact frame cadence the snap surfaced
        // under. Captures every frame → sbclick_a_NN.png (collapse) + sbclick_b_NN.png (expand) for a frame-by-frame diff.
        {
            // Hamburger client-DIP position: the merged chrome row's pane-toggle built-in. With WaveeShell.ChromeParts'
            // four-DIP nudge it occupies x 8..48 / y 2..46 in the window's ONLY 48-DIP chrome row, so its centre is
            // (28, 24) DIP — the same x the 56-DIP compact rail centres its icons on. (It used to be at (36, 108) when
            // a separate ShellToolbar row lived under the title bar.) QueueInput consumes DIP DIRECTLY (the WndProc
            // converts screen-px→DIP before it enqueues), so NO DIP→px scale conversion is needed here. Both coords are
            // env-overridable for robustness.
            float clickX = float.TryParse(Environment.GetEnvironmentVariable("WAVEE_CLICK_X"), NumberStyles.Float, CultureInfo.InvariantCulture, out var cx) ? cx : 28f;
            float clickY = float.TryParse(Environment.GetEnvironmentVariable("WAVEE_CLICK_Y"), NumberStyles.Float, CultureInfo.InvariantCulture, out var cy) ? cy : 24f;
            var clickPt = new Point2(clickX, clickY);
            uint clickTime = 1;   // monotonically increasing stamp so the two toggles never read as a double-click

            void SynthClick()
            {
                // Down+Up in sequence onto the same queue the OS message pump uses — the dispatcher drains both this frame
                // (press then release→click). Mirrors the VerticalSlice input gates (window.QueueInput(PointerDown/Up)).
                window.QueueInput(new InputEvent(InputKind.PointerDown, clickPt, 0, 0, Pointer: PointerKind.Mouse, TimestampMs: clickTime++));
                window.QueueInput(new InputEvent(InputKind.PointerUp,   clickPt, 0, 0, Pointer: PointerKind.Mouse, TimestampMs: clickTime++));
            }

            void ClickLeg(char tag)
            {
                for (int f = 0; f < 45 && !window.IsClosed; f++)
                {
                    var s = host.RunFrame();
                    window.WaitForWork(Math.Min(host.RecommendedWaitMs(), 16));   // REAL pacing (mirrors the warmup loop): present at vsync, wait the loop's interval
                    Log.Info($"[sbclick] {tag} frame={f:00} frameMs={s.FrameMs:0.00} rendered={s.Rendered}");
                    var px = gpu.CaptureBgra(out int cw, out int chh);
                    PngWriter.WriteBgra(Path.Combine(outDir, $"sbclick_{tag}_{f:00}.png"), px, cw, chh);
                }
            }

            Log.Info($"[rail-probe] (c2) sidebar-toggle-via-synthetic-click at ({clickX:0},{clickY:0}) DIP");
            WaveeShell.ProbeSidebarCompact!(false); Settle(40);   // ensure EXPANDED before the collapsing click
            SynthClick();                                          // real click → toggle to COMPACT
            ClickLeg('a');
            Settle(40);                                            // settle before the reverse click
            SynthClick();                                          // real click → toggle back to EXPANDED
            ClickLeg('b');
        }

        // (d) sidebar-grip drag sweep — synthesize a pointer drag across ~40 steps (out to 460 and back to 240). This
        // exercises the suppressed path (SnapStructuralToLayout): with Reveal transitions the drag must still track 1:1.
        if (WaveeShell.ProbeSidebarDragBegin is not null && WaveeShell.ProbeSidebarDragWidth is not null && WaveeShell.ProbeSidebarDragEnd is not null)
        {
            Log.Info("[rail-probe] (d) sidebar drag sweep");
            WaveeShell.ProbeSidebarDragBegin!();
            for (int i = 0; i < 40 && !window.IsClosed; i++)
            {
                float t = i / 39f;
                float w = 240f + (460f - 240f) * (0.5f - 0.5f * MathF.Cos(t * MathF.PI * 2f));   // ease out to max and back
                WaveeShell.ProbeSidebarDragWidth!(w);
                Measure(sbDrag, "drag");
            }
            WaveeShell.ProbeSidebarDragEnd!();
            for (int f = 0; f < 12 && !window.IsClosed; f++) Measure(sbDrag, "drag-end");
        }

        // ── report ──
        var phases = new[] { railOCTracks, railOC, railRev, sbToggle, sbDrag };
        var all = new Phase("ALL");
        foreach (var p in phases) { all.Ms.AddRange(p.Ms); all.OverBudget += p.OverBudget; all.OverBudgetGc += p.OverBudgetGc; }

        var sb = new StringBuilder(8192);
        sb.AppendLine();
        sb.AppendLine($"=== WAVEE RAIL PROBE — projected-motion {(baseline ? "BASELINE (SizeMode.Reflow)" : "P1 (Reveal/FLIP)")} — per-frame ms; target < 8.33 ms at 120 Hz ===");
        sb.AppendLine(keepVsync ? "(WAVEE_PROBE_VSYNC: vblank-paced)" : "(vsync/latency throttle removed → pure work cost)");
        sb.AppendLine();
        sb.AppendLine($"{"phase",-16} {"n",5} {"p50",6} {"p90",6} {"p99",7} {"p99.9",7} {"max",8}   {"over8.3ms",10} {"(ofwhich GC)",12}");
        foreach (var p in phases) sb.AppendLine(Format(p));
        sb.AppendLine(new string('-', 98));
        sb.AppendLine(Format(all));

        // Per-phase layout-cost aggregate (the projected-motion headline): on a Reveal/FLIP toggle only the COMMIT frame
        // should carry measure/arrange; every anim tick must be ~0. Baseline (Reflow) re-measures on EVERY tick.
        var perPhase = new Dictionary<string, (int frames, int nonZeroMeasure, long sumMeasure, int maxMeasure, int maxArrange, int maxTextMiss)>();
        long anyMeasure = 0;
        foreach (var line in csv.ToString().Split('\n'))
        {
            var c = line.Split(',');
            if (c.Length < 20 || c[0] == "phase") continue;
            int.TryParse(c[9], out int meas); int.TryParse(c[10], out int arr); int.TryParse(c[11], out int tmiss);
            anyMeasure += meas;
            perPhase.TryGetValue(c[0], out var agg);
            agg.frames++;
            if (meas > 0) agg.nonZeroMeasure++;
            agg.sumMeasure += meas;
            if (meas > agg.maxMeasure) agg.maxMeasure = meas;
            if (arr > agg.maxArrange) agg.maxArrange = arr;
            if (tmiss > agg.maxTextMiss) agg.maxTextMiss = tmiss;
            perPhase[c[0]] = agg;
        }
        sb.AppendLine();
        sb.AppendLine("layout cost per phase (FG_LAYOUT_DIAG): frames with measure>0 / total, avg measure/frame, max measure/arrange/textMiss");
        foreach (var p in phases)
        {
            if (!perPhase.TryGetValue(p.Name, out var a) || a.frames == 0) continue;
            sb.AppendLine($"  {p.Name,-16} measureFrames={a.nonZeroMeasure}/{a.frames}  avgMeasure={(double)a.sumMeasure / a.frames:0.0}  maxMeasure={a.maxMeasure}  maxArrange={a.maxArrange}  maxTextMiss={a.maxTextMiss}");
        }
        if (anyMeasure == 0)
            sb.AppendLine("  NOTE: all measure counts are 0 — re-run with FG_LAYOUT_DIAG=1 to populate the layout-cost columns.");

        // Worst 12 frames overall, segment-broken-down.
        var rows = new List<(string Tag, double Ms, double Flush, double Layout, double Anim, double Record, double Submit, int Meas, int Arr, int G0)>();
        foreach (var line in csv.ToString().Split('\n'))
        {
            var c = line.Split(',');
            if (c.Length < 20 || c[0] == "phase") continue;
            double D(int i) => double.TryParse(c[i], NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;
            int.TryParse(c[9], out int meas); int.TryParse(c[10], out int arr); int.TryParse(c[15], out int g0);
            rows.Add((c[0] + ":" + c[2], D(3), D(4), D(5), D(6), D(7), D(8), meas, arr, g0));
        }
        rows.Sort((a, b) => b.Ms.CompareTo(a.Ms));
        sb.AppendLine();
        sb.AppendLine($"Worst 12 frames — {"total",7} = {"flush",6} + {"layout",6} + {"anim",5} + {"record",6} + {"submit",6}  (meas/arr, gc)  transition");
        for (int i = 0; i < Math.Min(12, rows.Count); i++)
        {
            var r = rows[i];
            sb.AppendLine($"  {r.Ms,7:0.00} = {r.Flush,6:0.00} + {r.Layout,6:0.00} + {r.Anim,5:0.00} + {r.Record,6:0.00} + {r.Submit,6:0.00}  (m={r.Meas} a={r.Arr}, gc={r.G0})  {r.Tag}");
        }

        string report = sb.ToString();
        Log.Info(report);
        WriteProbeFile(csvPath, csv.ToString(), "rail-probe");
        WriteProbeFile(summaryPath, report, "rail-probe");
    }

    // WAVEE_LIVE_LYRICS_SCROLL_PROBE=1: REAL-backend, long-duration repro for "the rest of the app scrolls poorly while
    // lyrics are open". This intentionally refuses the fake backend and waits for live authenticated playback with an
    // advancing position before measuring. Output defaults to .wavee-diagnostics; tune with:
    //   WAVEE_PROBE_REAL_FRAMES (default 1800), WAVEE_PROBE_WORK_FRAMES (default 600),
    //   WAVEE_PROBE_PLAYBACK_FRAMES / WAVEE_PROBE_LYRICS_FRAMES for readiness waits.
    static void RunLiveLyricsScrollProbe(AppHost host, Win32Window window, D3D12Device gpu)
    {
        string? outDir = ProbeOutputDir();
        string? csvPath = outDir is null ? null : Path.Combine(outDir, "wavee-live-lyrics-scroll-probe.csv");
        string? summaryPath = outDir is null ? null : Path.Combine(outDir, "wavee-live-lyrics-scroll-probe-summary.txt");
        var csv = new StringBuilder(1 << 18);
        csv.AppendLine("phase,frame,label,intervalMs,frameMs,flushMs,layoutMs,animMs,recordMs,submitMs,fenceWaitMs,presentMs,gen0,gen1,comps,nodes,draws,blurCandidates,blurLayers,blurSuppressed,blurHoldCandidates,edgeFadeGroups,d3dBlurHit,d3dBlurMiss,d3dBlurHoldHit,d3dBlurHoldFallback,d3dOpacityGroups,lyricsNowMs,lyricsAuthMs,lyricsActiveLine,lyricsVoiceLine,lyricsActiveChanged,lyricsScrollSnapped,trackMs,isPlaying,mainOff,mainTarget,mainMode,mainTransformDirty,lyricsOff,lyricsTarget,lyricsMode,lyricsTransformDirty,track");

        int realFrames = EnvInt("WAVEE_PROBE_REAL_FRAMES", 1800, 120, 20000);
        int workFrames = EnvInt("WAVEE_PROBE_WORK_FRAMES", 600, 0, 20000);
        int playbackFrames = EnvInt("WAVEE_PROBE_PLAYBACK_FRAMES", 5400, 120, 36000);
        int lyricsFrames = EnvInt("WAVEE_PROBE_LYRICS_FRAMES", 3600, 60, 36000);

        void FrameLive()
        {
            if (window.IsClosed) return;
            host.RunFrame();
            int wait = host.RecommendedWaitMs();
            if (wait > 0) window.WaitForWork(Math.Min(wait, 16));
        }
        void SettleLive(int n) { for (int i = 0; i < n && !window.IsClosed; i++) FrameLive(); }

        bool WaitFor(string label, int frames, Func<bool> ready)
        {
            for (int i = 0; i < frames && !window.IsClosed; i++)
            {
                if (ready()) return true;
                FrameLive();
            }
            Log.Info($"[live-lyrics-scroll] timed out waiting for {label}");
            return false;
        }

        if (!Services.UseRealBackend)
        {
            Log.Info("[live-lyrics-scroll] refusing to run: app is using --fake; run without --fake / with --real-backend");
            return;
        }
        if (WaveeApp.ProbePlayback is null)
        {
            Log.Info("[live-lyrics-scroll] no playback bridge exposed; shell/app did not mount");
            return;
        }

        long lastPos = -1;
        int advances = 0;
        bool PlaybackReady()
        {
            var b = WaveeApp.ProbePlayback;
            if (b is null) return false;
            long pos = b.PositionMs.Peek();
            if (pos > lastPos) advances++;
            lastPos = pos;
            return b.Auth.Peek() == AuthStatus.Authenticated
                && b.CurrentTrack.Peek() is not null
                && b.IsPlaying.Peek()
                && advances >= 2;
        }

        Log.Info("[live-lyrics-scroll] waiting for REAL authenticated playback with advancing position");
        if (!WaitFor("live playing track", playbackFrames, PlaybackReady))
        {
            var b = WaveeApp.ProbePlayback;
            Log.Info($"[live-lyrics-scroll] playback state: auth={b?.Auth.Peek()} playing={b?.IsPlaying.Peek()} track={TrackLabel(b)} pos={b?.PositionMs.Peek() ?? 0}");
            return;
        }

        Log.Info($"[live-lyrics-scroll] track: {TrackLabel(WaveeApp.ProbePlayback)}");
        NodeHandle lyrics = default;
        bool LyricsReady()
        {
            lyrics = FindLyricsViewport(host.Scene);
            if (lyrics.IsNull) return false;
            var root = host.Scene.Root.IsNull ? default : host.Scene.AbsoluteRect(host.Scene.Root);
            var lr = host.Scene.AbsoluteRect(lyrics);
            return root.W <= 0f || lr.X >= root.W * 0.55f;
        }

        Log.Info("[live-lyrics-scroll] waiting for lyrics viewport");
        if (!WaitFor("lyrics viewport", lyricsFrames, LyricsReady))
        {
            DumpScrollers(host.Scene);
            return;
        }

        void Nav(string key, string? arg) => WaveeShell.ProbeNav!(key, arg);
        var routes = BuildLiveLyricsRoutes(WaveeApp.ProbePlayback);
        int routeSettleFrames = EnvInt("WAVEE_PROBE_ROUTE_SETTLE_FRAMES", 150, 12, 3600);
        int extraRoutes = EnvInt("WAVEE_PROBE_EXTRA_ROUTES", 8, 0, 40);
        if (extraRoutes > 0)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var r in routes) seen.Add(r.Key + "\u001F" + (r.Arg ?? ""));

            Nav("home", null);
            SettleLive(routeSettleFrames);
            var homeKeys = new List<string>();
            host.CollectMorphKeys(homeKeys);
            int added = 0, playlistN = 0, albumN = 0;
            foreach (var key in homeKeys)
            {
                if (added >= extraRoutes) break;
                bool playlist = key.StartsWith("pl:", StringComparison.Ordinal);
                bool album = key.StartsWith("album:", StringComparison.Ordinal);
                if (!playlist && !album) continue;
                string sig = key + "\u001F";
                if (!seen.Add(sig)) continue;
                string label = playlist ? "playlist-" + (++playlistN).ToString(CultureInfo.InvariantCulture) : "album-" + (++albumN).ToString(CultureInfo.InvariantCulture);
                routes.Add(new ProbeRoute(key, null, SafeRouteLabel(label)));
                added++;
            }
            Log.Info($"[live-lyrics-scroll] added {added} home playlist/album routes");
        }
        int perRouteRealFrames = EnvInt("WAVEE_PROBE_ROUTE_FRAMES", Math.Max(60, realFrames / Math.Max(1, routes.Count)), 0, 20000);
        int perRouteWorkFrames = EnvInt("WAVEE_PROBE_WORK_ROUTE_FRAMES", workFrames / Math.Max(1, routes.Count), 0, 20000);

        var allIntervals = new List<double>(Math.Max(realFrames, perRouteRealFrames * routes.Count));
        var intervalGroups = new List<(string Label, List<double> Values)>();
        var phases = new List<Phase>(routes.Count * 2);
        var routeReports = new List<string>(routes.Count);
        int throttled = 0, maxWait = 0, measuredRoutes = 0, skippedRoutes = 0;
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();

        foreach (var route in routes)
        {
            if (window.IsClosed) break;
            Log.Info($"[live-lyrics-scroll] route {route.Label}: {route.Key}" + (route.Arg is null ? "" : $" ({route.Arg})"));
            Nav(route.Key, route.Arg);
            SettleLive(routeSettleFrames);

            lyrics = FindLyricsViewport(host.Scene);
            if (lyrics.IsNull)
            {
                Log.Info($"[live-lyrics-scroll] route {route.Label}: lyrics viewport missing after navigation");
                skippedRoutes++;
                continue;
            }

            NodeHandle main = FindMainScrollViewport(host.Scene, lyrics);
            if (main.IsNull)
            {
                Log.Info($"[live-lyrics-scroll] route {route.Label}: no main page scroller; skipped");
                skippedRoutes++;
                continue;
            }

            var mainRect = host.Scene.AbsoluteRect(main);
            host.Scene.TryGetScroll(main, out var mainScroll0);
            host.Scene.TryGetScroll(lyrics, out var lyricsScroll0);
            float mainMin = mainScroll0.OffsetY, mainMax = mainScroll0.OffsetY;
            float lyricsMin = lyricsScroll0.OffsetY, lyricsMax = lyricsScroll0.OffsetY;
            void TrackRouteOffsets()
            {
                if (host.Scene.TryGetScroll(main, out var ms))
                {
                    mainMin = MathF.Min(mainMin, ms.OffsetY);
                    mainMax = MathF.Max(mainMax, ms.OffsetY);
                }
                if (host.Scene.TryGetScroll(lyrics, out var ls))
                {
                    lyricsMin = MathF.Min(lyricsMin, ls.OffsetY);
                    lyricsMax = MathF.Max(lyricsMax, ls.OffsetY);
                }
            }
            float vh = mainScroll0.ViewportH > 20f ? mainScroll0.ViewportH : (mainRect.H > 20f ? mainRect.H : 400f);
            var posPt = new Point2(mainRect.X + mainRect.W * 0.5f, mainRect.Y + vh * 0.5f);
            window.QueueInput(new InputEvent(InputKind.PointerMove, posPt, 0, 0));
            SettleLive(4);
            Log.Info($"[live-lyrics-scroll] route {route.Label}: wheel @ ({posPt.X:0},{posPt.Y:0}) main content={mainScroll0.ContentH:0} viewH={mainScroll0.ViewportH:0}; lyrics content={lyricsScroll0.ContentH:0} viewH={lyricsScroll0.ViewportH:0}");

            var routeIntervals = new List<double>(perRouteRealFrames);
            var realPhase = new Phase("real:" + route.Label);
            phases.Add(realPhase);
            int activeChangeFrames = 0, blurHeldFrames = 0, zeroBlurWithCandidates = 0;
            int maxBlurHeld = 0, maxBlurCandidates = 0, maxBlurLayers = 0, maxBlurCacheMiss = 0;
            int minBlurLayers = int.MaxValue;
            void TrackLyricsPipeline(FrameStats s)
            {
                var ld = LyricsView.LastFrameDiagnostics;
                if (ld.ActiveChanged) activeChangeFrames++;
                if (s.BlurCandidateCount > 0)
                {
                    minBlurLayers = Math.Min(minBlurLayers, s.BlurGroupCount);
                    if (s.BlurGroupCount == 0) zeroBlurWithCandidates++;
                }
                // During this MAIN-page wheel the lyrics sibling self-blur HOLDS under its cache policy (served by its
                // position-independent pin), counted by BlurHoldCandidateCount; 0 held frames across a sustained scroll =
                // the sibling-defer path is dead (the metric that replaced the retired BlurSuppressedByScrollCount).
                if (s.BlurHoldCandidateCount > 0) blurHeldFrames++;
                maxBlurHeld = Math.Max(maxBlurHeld, s.BlurHoldCandidateCount);
                maxBlurCandidates = Math.Max(maxBlurCandidates, s.BlurCandidateCount);
                maxBlurLayers = Math.Max(maxBlurLayers, s.BlurGroupCount);
                maxBlurCacheMiss = Math.Max(maxBlurCacheMiss, gpu.LastBlurCacheMiss);
            }
            long prevTick = Stopwatch.GetTimestamp();
            for (int i = 0; i < perRouteRealFrames && !window.IsClosed; i++)
            {
                float wheel = ((i / 90) & 1) == 0 ? +60f : -60f;
                window.QueueInput(new InputEvent(InputKind.Wheel, posPt, 0, 0, wheel));
                int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1);
                var s = host.RunFrame();
                long now = Stopwatch.GetTimestamp();
                double intervalMs = (now - prevTick) * 1000.0 / Stopwatch.Frequency;
                prevTick = now;
                routeIntervals.Add(intervalMs);
                allIntervals.Add(intervalMs);
                int dg0 = GC.CollectionCount(0) - g0, dg1 = GC.CollectionCount(1) - g1;
                if (s.Rendered || s.DrawCommandCount > 0)
                {
                    realPhase.Ms.Add(s.FrameMs);
                    if (s.FrameMs > BudgetMs) { realPhase.OverBudget++; if (dg0 > 0) realPhase.OverBudgetGc++; }
                }
                AppendLiveLyricsRow(csv, "real:" + route.Label, i, wheel > 0 ? "wheel-down" : "wheel-up", intervalMs, s, dg0, dg1, host.Scene, main, lyrics, WaveeApp.ProbePlayback, gpu);
                TrackLyricsPipeline(s);
                TrackRouteOffsets();
                int wait = host.RecommendedWaitMs();
                if (wait > 0)
                {
                    throttled++;
                    if (wait > maxWait) maxWait = wait;
                    window.WaitForWork(Math.Min(wait, 16));
                }
            }
            intervalGroups.Add((route.Label, routeIntervals));

            var workPhase = new Phase("work:" + route.Label);
            if (perRouteWorkFrames > 0) phases.Add(workPhase);
            for (int i = 0; i < perRouteWorkFrames && !window.IsClosed; i++)
            {
                float wheel = ((i / 90) & 1) == 0 ? +60f : -60f;
                window.QueueInput(new InputEvent(InputKind.Wheel, posPt, 0, 0, wheel));
                int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1);
                gpu.SuppressLatencyWaitOnce();
                gpu.SuppressVsyncOnce();
                var s = host.RunFrame();
                int dg0 = GC.CollectionCount(0) - g0, dg1 = GC.CollectionCount(1) - g1;
                if (s.Rendered || s.DrawCommandCount > 0)
                {
                    workPhase.Ms.Add(s.FrameMs);
                    if (s.FrameMs > BudgetMs) { workPhase.OverBudget++; if (dg0 > 0) workPhase.OverBudgetGc++; }
                }
                AppendLiveLyricsRow(csv, "work:" + route.Label, i, wheel > 0 ? "wheel-down" : "wheel-up", 0, s, dg0, dg1, host.Scene, main, lyrics, WaveeApp.ProbePlayback, gpu);
                TrackLyricsPipeline(s);
                TrackRouteOffsets();
            }

            host.Scene.TryGetScroll(main, out var mainScroll1);
            host.Scene.TryGetScroll(lyrics, out var lyricsScroll1);
            bool mainMoved = mainMax - mainMin > 1f;
            bool lyricsMoved = lyricsMax - lyricsMin > 1f;
            string minBlur = minBlurLayers == int.MaxValue ? "-" : minBlurLayers.ToString(CultureInfo.InvariantCulture);
            routeReports.Add($"{route.Label}: main offset {mainScroll0.OffsetY:0}->{mainScroll1.OffsetY:0} range {mainMin:0}-{mainMax:0} {(mainMoved ? "moved" : "NO-MOVE")}; lyrics offset {lyricsScroll0.OffsetY:0}->{lyricsScroll1.OffsetY:0} range {lyricsMin:0}-{lyricsMax:0} {(lyricsMoved ? "moved" : "still")}; frames real={routeIntervals.Count} work={workPhase.Ms.Count}; activeChanges={activeChangeFrames}; blurLayers min/max={minBlur}/{maxBlurLayers}; blurCandidates max={maxBlurCandidates}; blurHeld frames/max={blurHeldFrames}/{maxBlurHeld} (sibling DoF deferred during the main scroll; 0 across a moving scroll = regression); zeroBlurWithCandidates={zeroBlurWithCandidates}; maxBlurCacheMiss={maxBlurCacheMiss}");
            measuredRoutes++;
        }

        if (measuredRoutes == 0)
        {
            Log.Info("[live-lyrics-scroll] no routes with usable main scrollers found");
            DumpScrollers(host.Scene);
            return;
        }

        var sb = new StringBuilder(4096);
        sb.AppendLine();
        sb.AppendLine("=== WAVEE LIVE LYRICS SCROLL PROBE - REAL backend, lyrics rail open, multi-page main wheel scroll ===");
        sb.AppendLine($"track: {TrackLabel(WaveeApp.ProbePlayback)}");
        sb.AppendLine($"routes measured={measuredRoutes}; skipped={skippedRoutes}; route frames real={perRouteRealFrames}; work={perRouteWorkFrames}; throttledWaitFrames={throttled}; maxWait={maxWait}ms");
        AppendIntervalSummary(sb, "all real app-loop intervals", allIntervals);
        foreach (var group in intervalGroups) AppendIntervalSummary(sb, "real intervals " + group.Label, group.Values);
        sb.AppendLine();
        sb.AppendLine($"{"phase",-14} {"n",5} {"p50",6} {"p90",6} {"p99",7} {"p99.9",7} {"max",8}   {"over8.3ms",10} {"(ofwhich GC)",12}");
        foreach (var phase in phases) sb.AppendLine(Format(phase));
        sb.AppendLine();
        foreach (var line in routeReports) sb.AppendLine(line);
        sb.AppendLine("CSV columns include playback, LyricsView active/voice line diagnostics, recorder blur counts, D3D blur cache counts, and main/lyrics scroll state per frame.");
        sb.AppendLine("Note: the lyrics follow is an instant viewport LATCH plus a per-line compensating cascade (Wave D), not a programmatic spring — a lyrics 'moved' offset range is expected to be one-frame jumps and lyricsMode stays 0 (Idle) through a handoff. Cascade correctness is asserted by WAVEE_LYRICS_ADVANCE_PROBE, not here.");

        string report = sb.ToString();
        Log.Info(report);
        if (csvPath is not null) WriteProbeFile(csvPath, csv.ToString(), "live-lyrics-scroll");
        if (summaryPath is not null) WriteProbeFile(summaryPath, report, "live-lyrics-scroll");
    }

    // WAVEE_LYRICS_ADVANCE_PROBE=1: the REDESIGNED, trustworthy lyrics probe. It drives the media clock SYNCHRONOUSLY
    // (LyricsView.ProbeStep, with the async ticker silenced by ProbeSyncMode) so a line advance and the RunFrame that
    // records the resulting scroll SETTLE are the SAME frame — killing the timer-decoupling artifact that made the prior
    // probe's "blur-drop ≠ line-change" claim meaningless. Reads the DoF-defer inputs (ScrollMode / UserScrollActive /
    // content-TransformDirty) captured at RECORD time (before ClearTransformDirty), plus the whole-frame blur counters.
    //   P1 handoff cascade    : the Wave D model. The follow no longer runs the engine's programmatic spring — a handoff
    //                           is ONE instant viewport latch plus per-line compensating translates springing to 0 on a
    //                           60 ms-per-rank stagger. P1 asserts (a) the offset lands in exactly ONE frame, (b) every
    //                           line's comp decays monotonically without ever crossing zero (zero overshoot), (c) the
    //                           settle is ≤0.55 s of wall time, (d) onset order is top-first, (e) steady-state
    //                           HotPhaseAllocBytes == 0. It also keeps the original BUG1 check (the lyric DoF must
    //                           never go absent on a content-advance frame).
    //   P2 sibling-isolation  : wheel-scroll the MAIN page while lyrics sit still — the lyrics viewport must stay
    //                           mode 0 / not-user-scrolling / not-content-dirty (a sibling scroll can't cascade its DoF).
    //   P3 skip-submit (BUG3) : stationary, no input — count frames force-PRESENTED despite a static scene (a loop anim
    //                           marking TransformDirty defeats skip-submit → the app-wide vsync-paced present cost).
    static void RunLyricsAdvanceProbe(AppHost host, Win32Window window, D3D12Device gpu)
    {
        string? outDir = ProbeOutputDir();
        string? csvPath = outDir is null ? null : Path.Combine(outDir, "wavee-lyrics-advance-probe.csv");
        string? summaryPath = outDir is null ? null : Path.Combine(outDir, "wavee-lyrics-advance-probe-summary.txt");
        var csv = new StringBuilder(1 << 16);
        csv.AppendLine("phase,line,frame,label,injectedNowMs,activeLine,voiceLine,activeChanged,voiceChanged,lyMode,lyPrevMode,lyUserScroll,lyContentDirty,lyOff,lyTgt,blurCandidates,blurGroups,blurSuppressed,blurHoldCandidates,d3dBlurMiss,d3dBlurHoldHit,d3dBlurHoldFallback,frameMs,recordMs,submitMs,fenceWaitMs,presentMs,presented,animMs,mainMode,mainContentDirty,track");

        void Paced() { if (window.IsClosed) return; host.RunFrame(); window.WaitForWork(16); }   // ~16 ms dt so the spring eases realistically
        void FrameLive() { if (window.IsClosed) return; host.RunFrame(); int wt = host.RecommendedWaitMs(); if (wt > 0) window.WaitForWork(Math.Min(wt, 16)); }
        bool WaitFor(string label, int frames, Func<bool> ready)
        {
            for (int i = 0; i < frames && !window.IsClosed; i++) { if (ready()) return true; FrameLive(); }
            Log.Info($"[lyrics-advance] timed out waiting for {label}");
            return false;
        }

        if (!Services.UseRealBackend) { Log.Info("[lyrics-advance] refusing to run under --fake; launch the real backend, log in, and play a word-synced track"); return; }
        if (WaveeApp.ProbePlayback is null) { Log.Info("[lyrics-advance] no playback bridge exposed"); return; }

        long lastPos = -1; int adv = 0;
        bool PlaybackReady()
        {
            var b = WaveeApp.ProbePlayback; if (b is null) return false;
            long p = b.PositionMs.Peek(); if (p > lastPos) adv++; lastPos = p;
            return b.Auth.Peek() == AuthStatus.Authenticated && b.CurrentTrack.Peek() is not null;
        }
        int playbackFrames = EnvInt("WAVEE_PROBE_PLAYBACK_FRAMES", 5400, 120, 36000);
        Log.Info("[lyrics-advance] waiting for REAL authenticated playback");
        if (!WaitFor("authenticated track", playbackFrames, PlaybackReady)) return;

        int lyricsFrames = EnvInt("WAVEE_PROBE_LYRICS_FRAMES", 3600, 60, 36000);
        NodeHandle lyricsVp = default;
        bool LyricsReady()
        {
            var lv = LyricsView.ProbeActive; if (lv is null) return false;
            lyricsVp = lv.ProbeViewport;
            return !lyricsVp.IsNull && host.Scene.IsLive(lyricsVp) && lv.ProbeLineCount >= 3;
        }
        Log.Info("[lyrics-advance] waiting for the lyrics view + viewport + a >=3-line synced doc");
        if (!WaitFor("lyrics view/viewport/doc", lyricsFrames, LyricsReady)) { DumpScrollers(host.Scene); return; }

        var view = LyricsView.ProbeActive!;
        int lineCount = view.ProbeLineCount;
        host.ProbeLyricsViewport = lyricsVp;
        NodeHandle mainVp = FindMainScrollViewport(host.Scene, lyricsVp);
        host.ProbeMainViewport = mainVp;
        Log.Info($"[lyrics-advance] track: {TrackLabel(WaveeApp.ProbePlayback)}; lines={lineCount}; sync-driving (ticker silenced)");

        // Pre-settle onto line 0 so the ONE-TIME first-landing instant jump fires there, then force snapped so every
        // MEASURED advance takes the steady-state follow path — the latch + cascade, not the first-landing hard jump.
        view.ProbeStep(view.ProbeLineStartMs(0));
        for (int i = 0; i < 10 && !window.IsClosed; i++) Paced();
        view.ProbeForceSnapped();

        // Frames per handoff window. The cascade settles in ~0.48 s (≈30 frames at 16 ms); the tail beyond that is the
        // quiescent stretch phase (e) samples, so keep a comfortable margin over the 0.55 s assertion.
        int cascadeFrames = EnvInt("WAVEE_PROBE_CASCADE_FRAMES", 56, 16, 600);
        int startLine = Math.Min(1, lineCount - 1);
        int endLine = Math.Min(lineCount - 1, startLine + EnvInt("WAVEE_PROBE_ADVANCES", 20, 1, 4096));

        float LyricsOffsetY() => host.Scene.TryGetScroll(lyricsVp, out var lsc) ? lsc.OffsetY : 0f;

        // ── P1: the handoff cascade ────────────────────────────────────────────────────────────────────────────────
        // Each handoff FREEZES the media clock at lineStart(li) − LeadMs, so `active` resolves to li and STAYS there
        // while the frame loop keeps stepping on real wall dt. The cascade is wall-driven and the active line is not,
        // so freezing isolates exactly one handoff per window — no re-arm halfway through a measurement.
        int p1Frames = 0, p1BlurAbsentFrames = 0, p1Handoffs = 0;
        int p1SpringFrames = 0;      // lyrics viewport in a programmatic chase — must be 0, the follow is a latch now
        int p1LateOffsetFrames = 0;  // (a) offset moved on a frame AFTER the latch frame ⇒ not a one-frame land
        int p1SignFlips = 0;         // (b) a comp crossed zero ⇒ overshoot
        int p1NonMonotone = 0;       // (b) |comp| GREW mid-flight ⇒ not a decay
        int p1OrderViolations = 0;   // (d) a lower rank started moving BEFORE the rank above it
        int p1AllocFrames = 0;       // (e) steady-state HotPhaseAllocBytes != 0
        long p1MaxAllocBytes = 0;
        double p1MaxSettleMs = 0, p1SumSettleMs = 0; int p1SettledHandoffs = 0, p1UnsettledHandoffs = 0;
        int p1MaxOnsetSpreadFrames = 0;
        var casPrev = new float[lineCount];
        var casSign = new float[lineCount];
        var casOnset = new int[lineCount];
        int prevLyMode = 0;
        float offPrev = LyricsOffsetY();
        for (int li = startLine; li <= endLine && !window.IsClosed; li++)
        {
            long freezeMs = view.ProbeLineStartMs(li) - LyricsView.LeadMs;

            // ── the LATCH frame: one ProbeStep moves `active` to li, latches the viewport and arms the cascade ──
            view.ProbeStep(freezeMs);
            long armQpc = Stopwatch.GetTimestamp();   // t=0 is the ArmCascade INSIDE that step, not the frame around it
            var sArm = host.RunFrame();
            window.WaitForWork(16);
            p1Frames++;
            float offArm = LyricsOffsetY();
            bool handoff = MathF.Abs(offArm - offPrev) > 0.5f;
            if (handoff) p1Handoffs++;
            if (sArm.LyricsScrollMode == (int)FluentGpu.Scroll.ScrollActivity.Driven) p1SpringFrames++;
            if (!sArm.LyricsUserScrollActive && sArm.LyricsContentDirtyAtRecord && sArm.BlurCandidateCount == 0) p1BlurAbsentFrames++;
            AppendAdvanceRow(csv, "P1-cascade", li, 0, handoff ? "latch" : "no-move", sArm, prevLyMode, host.Scene, lyricsVp, mainVp, gpu);
            prevLyMode = sArm.LyricsScrollMode;
            for (int i = 0; i < lineCount; i++)
            {
                float c = view.ProbeCascadeComp(i);
                casPrev[i] = c;
                casSign[i] = c > 0f ? 1f : c < 0f ? -1f : 0f;
                casOnset[i] = -1;
            }

            // ── the settle window: the clock stays frozen, only wall time advances ──
            double settleMs = -1;
            for (int f = 1; f < cascadeFrames && !window.IsClosed; f++)
            {
                view.ProbeStep(freezeMs);
                // (c) the settle is over when the cascade self-quiesces (every line landed exactly on 0). Sampled HERE,
                // straight after the step that ran DriveCascade, so neither the frame nor the pacing wait is counted in.
                if (settleMs < 0 && !view.ProbeCascadeActive)
                    settleMs = (Stopwatch.GetTimestamp() - armQpc) * 1000.0 / Stopwatch.Frequency;
                var s = host.RunFrame();
                window.WaitForWork(16);
                p1Frames++;

                // (a) the offset LANDED on the latch frame: nothing may move it again for the rest of the handoff.
                if (MathF.Abs(LyricsOffsetY() - offArm) > 0.5f) p1LateOffsetFrames++;
                if (s.LyricsScrollMode == (int)FluentGpu.Scroll.ScrollActivity.Driven) p1SpringFrames++;

                // (b) + (d): every line's comp must decay monotonically to 0 without crossing zero, and a line must not
                // start moving before the line above it (the onset frame is the first CHANGE, not the first non-zero —
                // a staggered line holds a non-zero comp for its whole delay).
                for (int i = 0; i < lineCount; i++)
                {
                    float c = view.ProbeCascadeComp(i);
                    float p = casPrev[i], sgn = casSign[i];
                    if (sgn != 0f)
                    {
                        if (c * sgn < -0.01f) p1SignFlips++;
                        if (MathF.Abs(c) > MathF.Abs(p) + 0.05f) p1NonMonotone++;
                    }
                    if (casOnset[i] < 0 && MathF.Abs(c - p) > 0.02f) casOnset[i] = f;
                    casPrev[i] = c;
                }

                // (e) steady state = the quiescent tail after the settle, clock frozen, nothing animating.
                if (settleMs >= 0 && f >= cascadeFrames - 8)
                {
                    if (s.HotPhaseAllocBytes != 0) p1AllocFrames++;
                    if (s.HotPhaseAllocBytes > p1MaxAllocBytes) p1MaxAllocBytes = s.HotPhaseAllocBytes;
                }

                // BUG1 signature under the pin-cache pipeline: the recorder no longer DROPS a stationary blur (the retired
                // BlurSuppressedByScrollCount is always 0), so the observable regression is the DoF VANISHING — a
                // content-advance frame (content transform written, NOT a user scroll) that records zero blur candidates
                // means the lyric depth-of-field went absent instead of being served by its pin.
                if (!s.LyricsUserScrollActive && s.LyricsContentDirtyAtRecord && s.BlurCandidateCount == 0) p1BlurAbsentFrames++;
                AppendAdvanceRow(csv, "P1-cascade", li, f, settleMs >= 0 ? "settled" : "", s, prevLyMode, host.Scene, lyricsVp, mainVp, gpu);
                prevLyMode = s.LyricsScrollMode;
            }

            if (handoff)
            {
                if (settleMs < 0) p1UnsettledHandoffs++;
                else
                {
                    p1SettledHandoffs++;
                    p1SumSettleMs += settleMs;
                    if (settleMs > p1MaxSettleMs) p1MaxSettleMs = settleMs;
                }
                // (d) top-first onset: rank 0 is the OUTGOING line (li-1) and everything above it; each rank below must
                // start no EARLIER than the rank above. Ranks beyond the stagger cap share one onset — equal is legal.
                int prevOnset = -1;
                for (int k = 0; k <= 6; k++)
                {
                    int idx = li - 1 + k;
                    if (idx < 0 || idx >= lineCount) continue;
                    int onset = casOnset[idx];
                    if (onset < 0) continue;
                    if (prevOnset >= 0 && onset < prevOnset) p1OrderViolations++;
                    if (prevOnset >= 0 && onset - prevOnset > p1MaxOnsetSpreadFrames) p1MaxOnsetSpreadFrames = onset - prevOnset;
                    prevOnset = Math.Max(prevOnset, onset);
                }
            }
            offPrev = offArm;
        }

        // ── P2: wheel the MAIN page while lyrics sit still (sibling isolation) ──────────────────────────────────────
        int p2Frames = 0, p2LyricsTouched = 0, p2LyricsHeld = 0;
        if (!mainVp.IsNull && host.Scene.IsLive(mainVp))
        {
            var mr = host.Scene.AbsoluteRect(mainVp);
            host.Scene.TryGetScroll(mainVp, out var msc0);
            float vh = msc0.ViewportH > 20f ? msc0.ViewportH : (mr.H > 20f ? mr.H : 400f);
            var posPt = new Point2(mr.X + mr.W * 0.5f, mr.Y + vh * 0.5f);
            window.QueueInput(new InputEvent(InputKind.PointerMove, posPt, 0, 0));
            int p2Total = EnvInt("WAVEE_PROBE_P2_FRAMES", 180, 0, 4096);
            for (int f = 0; f < p2Total && !window.IsClosed; f++)
            {
                window.QueueInput(new InputEvent(InputKind.Wheel, posPt, 0, 0, ((f / 45) & 1) == 0 ? +60f : -60f));
                var s = host.RunFrame();
                window.WaitForWork(16);
                p2Frames++;
                if (s.LyricsUserScrollActive || s.LyricsContentDirtyAtRecord) p2LyricsTouched++;   // must stay 0 — lyrics untouched by a main scroll
                // Sibling isolation now MEANS the stationary lyrics DoF is HELD (served by its position-independent pin),
                // not dropped, while the main page scrolls — BlurHoldCandidateCount > 0 with the lyrics themselves untouched.
                if (s.BlurHoldCandidateCount > 0) p2LyricsHeld++;
                AppendAdvanceRow(csv, "P2-mainscroll", -1, f, "", s, prevLyMode, host.Scene, lyricsVp, mainVp, gpu);
                prevLyMode = s.LyricsScrollMode;
            }
        }

        // ── P3: skip-submit / present cost with the rail open, no input (BUG3 mechanism) ───────────────────────────
        int p3Frames = 0, p3Presented = 0, p3StaticButPresented = 0; double p3AnimMs = 0, p3FenceMs = 0;
        int p3Total = EnvInt("WAVEE_PROBE_P3_FRAMES", 240, 0, 4096);
        for (int f = 0; f < p3Total && !window.IsClosed; f++)
        {
            var s = host.RunFrame();
            window.WaitForWork(16);
            p3Frames++;
            if (s.Presented) p3Presented++;
            p3AnimMs += s.AnimMs; p3FenceMs += s.FenceWaitMs;
            bool stationary = s.LyricsScrollMode == 0 && !s.LyricsContentDirtyAtRecord && s.MainScrollMode == 0 && !s.MainContentDirtyAtRecord;
            if (s.Presented && stationary) p3StaticButPresented++;   // force-present despite a static scene → skip-submit defeated
            AppendAdvanceRow(csv, "P3-idle", -1, f, "", s, prevLyMode, host.Scene, lyricsVp, mainVp, gpu);
            prevLyMode = s.LyricsScrollMode;
        }

        // ── Probe phase 4 — BUG2 — voice-transition REMOUNT + wipe/glow integrity ─────────────────────────────────────────────
        // Step the clock finely across interior line boundaries so VOICE crosses li-1→li while ACTIVE (the lead) stays
        // ~stationary near li — this is the exact frame the row just above active leaves the voice slot. On the fixed
        // (stable two-child) tree its node identity must NOT change (no remount → no re-bake flicker), and the karaoke
        // wipe/glow must still be present on every voice line (guards against the restructure breaking the feature).
        int p4Frames = 0, p4VoiceTransitions = 0, p4Remounts = 0, p4WipeMissing = 0, p4GlowDead = 0;
        if (lineCount >= 4)
        {
            view.ProbeForceSnapped();
            int b0 = Math.Max(2, lineCount / 3);
            int bEnd = Math.Min(lineCount - 2, b0 + EnvInt("WAVEE_PROBE_BUG2_BOUNDARIES", 8, 1, 4096));
            int prevVoice = -1;
            NodeHandle prevVoiceHandle = default;
            for (int li = b0; li <= bEnd && !window.IsClosed; li++)
            {
                long ls = view.ProbeLineStartMs(li);
                for (long dt = -80; dt <= 80 && !window.IsClosed; dt += 20)
                {
                    view.ProbeStep(ls + dt);
                    var s = host.RunFrame();
                    window.WaitForWork(16);
                    p4Frames++;
                    var ld = LyricsView.LastFrameDiagnostics;
                    int vln = ld.VoiceLine;
                    if (ld.VoiceChanged && prevVoice >= 0 && prevVoice != vln)
                    {
                        p4VoiceTransitions++;
                        var leaving = view.ProbeLineNode(prevVoice);   // the line that just left the voice slot (now dimmed)
                        if (!prevVoiceHandle.IsNull && !leaving.IsNull && !leaving.Equals(prevVoiceHandle)) p4Remounts++;
                    }
                    if (!ld.VoiceChanged && vln >= 0)   // integrity on a STABLE voice frame (the enter frame adds the wipe one frame later)
                    {
                        var wn = view.ProbeLineNode(vln);
                        if (wn.IsNull || !host.Scene.IsLive(wn) || !host.Scene.TryGetGlyphWipe(wn, out _)) p4WipeMissing++;
                        var gn = view.ProbeGlowNode(vln);
                        if (gn.IsNull || !host.Scene.IsLive(gn)) p4GlowDead++;
                    }
                    AppendAdvanceRow(csv, "P4-bug2", li, (int)dt, ld.VoiceChanged ? "voiceChg" : "", s, prevLyMode, host.Scene, lyricsVp, mainVp, gpu);
                    prevLyMode = s.LyricsScrollMode;
                    prevVoice = vln;
                    prevVoiceHandle = vln >= 0 ? view.ProbeLineNode(vln) : default;
                }
            }
        }

        // ── report ─────────────────────────────────────────────────────────────────────────────────────────────────
        bool bug1Fixed = p1BlurAbsentFrames == 0;
        bool oneFrameLand = p1LateOffsetFrames == 0 && p1SpringFrames == 0;
        bool zeroOvershoot = p1SignFlips == 0 && p1NonMonotone == 0;
        bool settleOk = p1UnsettledHandoffs == 0 && p1MaxSettleMs <= 550.0;
        bool topFirst = p1OrderViolations == 0;
        bool allocClean = p1AllocFrames == 0;
        bool cascadeOk = oneFrameLand && zeroOvershoot && settleOk && topFirst && allocClean;
        var sb = new StringBuilder(2048);
        sb.AppendLine();
        sb.AppendLine("=== WAVEE LYRICS ADVANCE PROBE — synchronous, timer-decoupling-free ===");
        sb.AppendLine($"track: {TrackLabel(WaveeApp.ProbePlayback)}; lines={lineCount}; advances={Math.Max(0, endLine - startLine + 1)}; frames/handoff={cascadeFrames}");
        sb.AppendLine();
        sb.AppendLine("P1 handoff cascade (Wave D: instant viewport latch + staggered per-line compensating springs):");
        sb.AppendLine($"  frames={p1Frames}; handoffs measured={p1Handoffs}");
        sb.AppendLine($"  (a) one-frame land : offset moves after the latch frame={p1LateOffsetFrames} (expect 0); frames the lyrics viewport was in a programmatic chase={p1SpringFrames} (expect 0 — the engine spring is no longer the follow) >>> {(oneFrameLand ? "PASS" : "FAIL")}");
        sb.AppendLine($"  (b) zero overshoot : comp sign flips={p1SignFlips} (expect 0); non-monotone |comp| growths={p1NonMonotone} (expect 0) >>> {(zeroOvershoot ? "PASS" : "FAIL")}");
        sb.AppendLine($"  (c) settle        : settled handoffs={p1SettledHandoffs}; never-settled={p1UnsettledHandoffs} (expect 0); mean={(p1SettledHandoffs > 0 ? p1SumSettleMs / p1SettledHandoffs : 0):0} ms; max={p1MaxSettleMs:0} ms (budget 550) >>> {(settleOk ? "PASS" : "FAIL")}");
        sb.AppendLine($"  (d) top-first     : onset-order violations={p1OrderViolations} (expect 0); max onset spread between adjacent ranks={p1MaxOnsetSpreadFrames} frames (60 ms/rank ≈ 4 frames @16 ms) >>> {(topFirst ? "PASS" : "FAIL")}");
        sb.AppendLine($"  (e) steady alloc  : post-settle frames with HotPhaseAllocBytes != 0={p1AllocFrames} (expect 0); max={p1MaxAllocBytes} B >>> {(allocClean ? "PASS" : "FAIL")}");
        sb.AppendLine($"  >>> CASCADE {(cascadeOk ? "GREEN" : "RED")}");
        sb.AppendLine($"  DoF-absent frames (contentDirty & !userScroll & zero blur candidates)={p1BlurAbsentFrames}");
        sb.AppendLine($"  >>> BUG1 {(bug1Fixed ? "FIXED" : "PRESENT")} — lyric DoF was {(bug1Fixed ? "NEVER" : "STILL")} dropped while advancing (a pure translation of the DoF layer is a blur-pin HIT, so a live blur candidate is expected on every content frame).");
        sb.AppendLine();
        sb.AppendLine("P2 main-scroll sibling isolation:");
        sb.AppendLine($"  frames={p2Frames}; frames where lyrics were user-scrolling/content-dirty during a MAIN scroll={p2LyricsTouched} (expect 0); frames the stationary lyrics DoF was HELD (served by its pin)={p2LyricsHeld} (expect >0 while the main page moves; 0 = the sibling-defer path is dead)");
        sb.AppendLine();
        sb.AppendLine("P3 skip-submit / present (BUG3 mechanism):");
        string p3rate = p3Frames > 0 ? $"{100.0 * p3Presented / p3Frames:0}%" : "n/a";
        string p3staticRate = p3Frames > 0 ? $"{100.0 * p3StaticButPresented / p3Frames:0}%" : "n/a";
        sb.AppendLine($"  frames={p3Frames}; presented={p3Presented} ({p3rate}); STATIC-scene-but-presented={p3StaticButPresented} ({p3staticRate} — these are skip-submit defeats from loop anims); meanAnimMs={(p3Frames > 0 ? p3AnimMs / p3Frames : 0):0.00}; meanFenceWaitMs={(p3Frames > 0 ? p3FenceMs / p3Frames : 0):0.00}");
        sb.AppendLine();
        bool wipeGlowIntact = p4WipeMissing == 0 && p4GlowDead == 0;
        sb.AppendLine("P4 BUG2 voice-transition (remount + wipe/glow integrity):");
        sb.AppendLine($"  frames={p4Frames}; voice transitions={p4VoiceTransitions}; leaving-line REMOUNTS={p4Remounts} (expect 0); voice-frames missing wipe={p4WipeMissing}; voice-frames dead glow={p4GlowDead}");
        sb.AppendLine($"  >>> BUG2 remount {(p4Remounts == 0 ? "GONE" : "STILL PRESENT")}; karaoke wipe/glow {(wipeGlowIntact ? "INTACT" : "BROKEN — REGRESSION, revert the LyricLineView restructure")}.");
        sb.AppendLine();
        sb.AppendLine("CSV per-frame columns: lyMode/lyPrevMode/lyUserScroll/lyContentDirty (record-time DoF-defer inputs), blurCandidates/blurGroups/blurSuppressed, presented, per-phase timing.");
        sb.AppendLine("Note: in Following mode the lyrics viewport no longer runs a programmatic spring — lyOff jumps once per handoff and lyMode stays 0 (Idle). A WheelAnimating lyrics mode now means a RESYNC glide (ζ=1, 110 ms half-life) or a user fling.");

        string report = sb.ToString();
        Log.Info(report);
        if (csvPath is not null) WriteProbeFile(csvPath, csv.ToString(), "lyrics-advance");
        if (summaryPath is not null) WriteProbeFile(summaryPath, report, "lyrics-advance");
    }

    static void AppendAdvanceRow(StringBuilder csv, string phase, int line, int frame, string label, FrameStats s, int prevLyMode,
        FluentGpu.Scene.SceneStore scene, NodeHandle lyricsVp, NodeHandle mainVp, D3D12Device gpu)
    {
        var ld = LyricsView.LastFrameDiagnostics;
        // TargetY is gone (ScrollState.TargetY deleted — the kernel's live Driven-chase target is now
        // ScrollBody-internal and not introspectable from outside the kernel). Keep the CSV column present (NaN) so
        // existing column-position parsers don't shift.
        float lyOff = 0f, lyTgt = float.NaN;
        if (!lyricsVp.IsNull && scene.IsLive(lyricsVp) && scene.TryGetScroll(lyricsVp, out var lsc)) lyOff = lsc.OffsetY;
        csv.Append(phase).Append(',')
           .Append(line.ToString(CultureInfo.InvariantCulture)).Append(',')
           .Append(frame.ToString(CultureInfo.InvariantCulture)).Append(',')
           .Append(label).Append(',')
           .Append(ld.NowMs.ToString(CultureInfo.InvariantCulture)).Append(',')
           .Append(ld.ActiveLine.ToString(CultureInfo.InvariantCulture)).Append(',')
           .Append(ld.VoiceLine.ToString(CultureInfo.InvariantCulture)).Append(',')
           .Append(ld.ActiveChanged ? '1' : '0').Append(',')
           .Append(ld.VoiceChanged ? '1' : '0').Append(',')
           .Append(s.LyricsScrollMode.ToString(CultureInfo.InvariantCulture)).Append(',')
           .Append(prevLyMode.ToString(CultureInfo.InvariantCulture)).Append(',')
           .Append(s.LyricsUserScrollActive ? '1' : '0').Append(',')
           .Append(s.LyricsContentDirtyAtRecord ? '1' : '0').Append(',')
           .Append(F(lyOff)).Append(',')
           .Append(F(lyTgt)).Append(',')
           .Append(s.BlurCandidateCount.ToString(CultureInfo.InvariantCulture)).Append(',')
           .Append(s.BlurGroupCount.ToString(CultureInfo.InvariantCulture)).Append(',')
           .Append(s.BlurSuppressedByScrollCount.ToString(CultureInfo.InvariantCulture)).Append(',')
           .Append(s.BlurHoldCandidateCount.ToString(CultureInfo.InvariantCulture)).Append(',')
           .Append(gpu.LastBlurCacheMiss.ToString(CultureInfo.InvariantCulture)).Append(',')
           .Append(gpu.LastBlurHoldHit.ToString(CultureInfo.InvariantCulture)).Append(',')
           .Append(gpu.LastBlurHoldFallback.ToString(CultureInfo.InvariantCulture)).Append(',')
           .Append(F(s.FrameMs)).Append(',')
           .Append(F(s.RecordMs)).Append(',')
           .Append(F(s.SubmitMs)).Append(',')
           .Append(F(s.FenceWaitMs)).Append(',')
           .Append(F(s.PresentMs)).Append(',')
           .Append(s.Presented ? '1' : '0').Append(',')
           .Append(F(s.AnimMs)).Append(',')
           .Append(s.MainScrollMode.ToString(CultureInfo.InvariantCulture)).Append(',')
           .Append(s.MainContentDirtyAtRecord ? '1' : '0').Append(',')
           .Append(CsvCell(TrackLabel(WaveeApp.ProbePlayback))).AppendLine();
    }

    static int EnvInt(string name, int fallback, int min, int max)
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
            return fallback;
        return Math.Clamp(v, min, max);
    }

    readonly record struct ProbeRoute(string Key, string? Arg, string Label);

    static List<ProbeRoute> BuildLiveLyricsRoutes(PlaybackBridge? b)
    {
        var routes = new List<ProbeRoute>(10);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(string? key, string? arg, string label)
        {
            if (string.IsNullOrWhiteSpace(key)) return;
            string sig = key + "\u001F" + (arg ?? "");
            if (!seen.Add(sig)) return;
            routes.Add(new ProbeRoute(key, arg, SafeRouteLabel(label)));
        }

        var track = b?.CurrentTrack.Peek();
        Add("home", null, "home");
        Add("liked", null, "liked");
        Add(RichText.RouteForUri(b?.CurrentContext.Peek()), null, "current-context");
        Add(RichText.RouteForUri(track?.Album.Uri), null, "current-album");
        Add(RichText.RouteForUri(track?.Artists.Count > 0 ? track.Artists[0].Uri : null), null, "current-artist");
        Add("albums", null, "albums");
        Add("artists", null, "artists");
        Add("podcasts", null, "podcasts");
        Add("search", track?.Artists.Count > 0 ? track.Artists[0].Name : "the", "search");
        return routes;
    }

    static string SafeRouteLabel(string label)
    {
        var sb = new StringBuilder(label.Length);
        foreach (char ch in label)
            sb.Append(char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' ? ch : '-');
        return sb.Length == 0 ? "route" : sb.ToString();
    }

    static string TrackLabel(PlaybackBridge? b)
    {
        var t = b?.CurrentTrack.Peek();
        if (t is null) return "(none)";
        string artist = t.Artists.Count > 0 ? t.Artists[0].Name : "";
        return artist.Length > 0 ? $"{t.Title} - {artist} [{t.Uri}]" : $"{t.Title} [{t.Uri}]";
    }

    static string CsvCell(string text) => "\"" + text.Replace("\"", "\"\"") + "\"";

    static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    static void AppendLiveLyricsRow(StringBuilder csv, string phase, int frame, string label, double intervalMs, FrameStats s,
        int gen0, int gen1, FluentGpu.Scene.SceneStore scene, NodeHandle main, NodeHandle lyrics, PlaybackBridge? b, D3D12Device gpu)
    {
        var ld = LyricsView.LastFrameDiagnostics;
        csv.Append(phase).Append(',')
           .Append(frame.ToString(CultureInfo.InvariantCulture)).Append(',')
           .Append(label).Append(',')
           .Append(F(intervalMs)).Append(',')
           .Append(F(s.FrameMs)).Append(',')
           .Append(F(s.FlushMs)).Append(',')
           .Append(F(s.LayoutMs)).Append(',')
           .Append(F(s.AnimMs)).Append(',')
           .Append(F(s.RecordMs)).Append(',')
           .Append(F(s.SubmitMs)).Append(',')
           .Append(F(s.FenceWaitMs)).Append(',')
           .Append(F(s.PresentMs)).Append(',')
           .Append(gen0.ToString(CultureInfo.InvariantCulture)).Append(',')
           .Append(gen1.ToString(CultureInfo.InvariantCulture)).Append(',')
           .Append(s.ComponentsRendered.ToString(CultureInfo.InvariantCulture)).Append(',')
           .Append(s.NodesVisited.ToString(CultureInfo.InvariantCulture)).Append(',')
           .Append(s.DrawCommandCount.ToString(CultureInfo.InvariantCulture)).Append(',')
           .Append(s.BlurCandidateCount.ToString(CultureInfo.InvariantCulture)).Append(',')
           .Append(s.BlurGroupCount.ToString(CultureInfo.InvariantCulture)).Append(',')
           .Append(s.BlurSuppressedByScrollCount.ToString(CultureInfo.InvariantCulture)).Append(',')
           .Append(s.BlurHoldCandidateCount.ToString(CultureInfo.InvariantCulture)).Append(',')
           .Append(s.EdgeFadeGroupCount.ToString(CultureInfo.InvariantCulture)).Append(',')
           .Append(gpu.LastBlurCacheHit.ToString(CultureInfo.InvariantCulture)).Append(',')
           .Append(gpu.LastBlurCacheMiss.ToString(CultureInfo.InvariantCulture)).Append(',')
           .Append(gpu.LastBlurHoldHit.ToString(CultureInfo.InvariantCulture)).Append(',')
           .Append(gpu.LastBlurHoldFallback.ToString(CultureInfo.InvariantCulture)).Append(',')
           .Append(gpu.LastOpacityGroups.ToString(CultureInfo.InvariantCulture)).Append(',')
           .Append(ld.NowMs.ToString(CultureInfo.InvariantCulture)).Append(',')
           .Append(ld.AuthMs.ToString(CultureInfo.InvariantCulture)).Append(',')
           .Append(ld.ActiveLine.ToString(CultureInfo.InvariantCulture)).Append(',')
           .Append(ld.VoiceLine.ToString(CultureInfo.InvariantCulture)).Append(',')
           .Append(ld.ActiveChanged ? '1' : '0').Append(',')
           .Append(ld.ScrollSnapped ? '1' : '0').Append(',')
           .Append((b?.PositionMs.Peek() ?? 0).ToString(CultureInfo.InvariantCulture)).Append(',')
           .Append((b?.IsPlaying.Peek() ?? false) ? '1' : '0');
        AppendScrollCells(csv, scene, main);
        AppendScrollCells(csv, scene, lyrics);
        csv.Append(',').Append(CsvCell(TrackLabel(b))).AppendLine();
    }

    static void AppendScrollCells(StringBuilder csv, FluentGpu.Scene.SceneStore scene, NodeHandle node)
    {
        if (node.IsNull || !scene.IsLive(node) || !scene.HasScroll(node) || !scene.TryGetScroll(node, out var sc))
        {
            csv.Append(",,,,");
            return;
        }
        int transformDirty = 0;
        if (!sc.ContentNode.IsNull && scene.IsLive(sc.ContentNode) && (scene.Flags(sc.ContentNode) & NodeFlags.TransformDirty) != 0)
            transformDirty = 1;
        // TargetY is gone (kernel-internal, ScrollBody-only) — NaN keeps the column present so existing CSV
        // column-position parsers don't shift; Phase's successor is the Activity result column (int-cast, same as the
        // old byte Phase value's shape).
        csv.Append(',').Append(F(sc.OffsetY))
           .Append(',').Append(F(float.NaN))
           .Append(',').Append(((int)sc.Activity).ToString(CultureInfo.InvariantCulture))
           .Append(',').Append(transformDirty.ToString(CultureInfo.InvariantCulture));
    }

    static void AppendIntervalSummary(StringBuilder sb, string label, List<double> intervals)
    {
        if (intervals.Count == 0)
        {
            sb.AppendLine($"{label}: no frames");
            return;
        }
        var a = intervals.ToArray();
        Array.Sort(a);
        double total = 0, worst = 0;
        int over16 = 0, over33 = 0;
        foreach (double v in intervals)
        {
            total += v;
            if (v > worst) worst = v;
            if (v > 16.7) over16++;
            if (v > 33.3) over33++;
        }
        double mean = total / intervals.Count;
        sb.AppendLine($"{label}: n={intervals.Count} mean={mean:0.0}ms ({(mean > 0 ? 1000.0 / mean : 0):0}fps) p50={Pct(a, 50):0.0} p90={Pct(a, 90):0.0} p99={Pct(a, 99):0.0} worst={worst:0.0}ms >16.7={over16} >33.3={over33}");
    }

    static NodeHandle FindMainScrollViewport(FluentGpu.Scene.SceneStore scene, NodeHandle lyrics)
    {
        var best = default(NodeHandle);
        float bestScore = -1f;
        RectF lyricsRect = default;
        if (!lyrics.IsNull && scene.IsLive(lyrics)) lyricsRect = scene.AbsoluteRect(lyrics);
        float rootW = scene.Root.IsNull ? 0f : scene.AbsoluteRect(scene.Root).W;

        var stack = new Stack<NodeHandle>();
        if (!scene.Root.IsNull) stack.Push(scene.Root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (n.IsNull || !scene.IsLive(n)) continue;
            if (n != lyrics && scene.HasScroll(n) && scene.TryGetScroll(n, out var sc) && sc.ViewportH > 50f && sc.ContentH > sc.ViewportH + 1f)
            {
                var r = scene.AbsoluteRect(n);
                if (!lyrics.IsNull && lyricsRect.W > 0f && r.X >= lyricsRect.X - 4f)
                {
                    for (var c = scene.FirstChild(n); !c.IsNull; c = scene.NextSibling(c)) stack.Push(c);
                    continue;
                }
                if (rootW > 0f && r.X > rootW * 0.72f)
                {
                    for (var c = scene.FirstChild(n); !c.IsNull; c = scene.NextSibling(c)) stack.Push(c);
                    continue;
                }
                float w = sc.ViewportW > 20f ? sc.ViewportW : r.W;
                float h = sc.ViewportH > 20f ? sc.ViewportH : r.H;
                float score = w * h + MathF.Min(sc.ContentH - sc.ViewportH, 5000f);
                if (score > bestScore) { bestScore = score; best = n; }
            }
            for (var c = scene.FirstChild(n); !c.IsNull; c = scene.NextSibling(c)) stack.Push(c);
        }
        return best;
    }

    static string? ProbeOutputDir()
    {
        string? dir = Environment.GetEnvironmentVariable("WAVEE_PROBE_OUT");
        if (string.IsNullOrWhiteSpace(dir))
            dir = Path.Combine(Environment.CurrentDirectory, ".wavee-diagnostics");
        try
        {
            Directory.CreateDirectory(dir);
            return dir;
        }
        catch (Exception ex)
        {
            Log.Info($"[home-scroll-probe] file output disabled: cannot create {dir}: {ex.Message}");
            return null;
        }
    }

    static void WriteProbeFile(string path, string text, string tag)
    {
        try
        {
            File.WriteAllText(path, text);
            Log.Info($"[{tag}] wrote {path}");
        }
        catch (Exception ex)
        {
            Log.Info($"[{tag}] failed to write {path}: {ex.Message}");
        }
    }

    // WAVEE_LYRICS_PROBE=1 (run with WAVEE_LYRICS_OPEN=1 to open the rail and --fake for the 40-line synced doc): measure
    // the lyrics surface, which renders ~10-13 per-line depth-of-field self-blur layers every frame. Reports the perceived
    // FPS at REAL vsync (GPU stalls included) AND the vsync-suppressed CPU work breakdown (flush/layout/anim/record/submit)
    // + the rendered-frame count (the idle-spin signal), for an IDLE (paused) hold and a wheel SCROLL through the lines.
    static void RunLyricsProbe(AppHost host, Win32Window window, D3D12Device gpu)
    {
        void FrameFree() { if (!window.IsClosed) { gpu.SuppressLatencyWaitOnce(); gpu.SuppressVsyncOnce(); host.RunFrame(); } }
        void Settle(int n) { for (int i = 0; i < n && !window.IsClosed; i++) FrameFree(); }

        // Optional resolution stress (WAVEE_PROBE_W/H): the layered-path submit + blit scale with the canvas pixel area,
        // so a high-DPI / large window is where the lyrics blur actually drops frames — reproduce it here.
        int pw = int.TryParse(Environment.GetEnvironmentVariable("WAVEE_PROBE_W"), out var w0) ? w0 : 0;
        int ph = int.TryParse(Environment.GetEnvironmentVariable("WAVEE_PROBE_H"), out var h0) ? h0 : 0;
        if (pw > 200 && ph > 200) { window.SetClientSize(pw, ph); Log.Info($"[lyrics-probe] window -> {pw}x{ph}"); }

        Log.Info("[lyrics-probe] warmup (rail opens via WAVEE_LYRICS_OPEN; lyrics doc loads)");
        Settle(60);
        System.Threading.Thread.Sleep(500);     // FakeData.GetLyricsAsync Task.Delay(150) + paint settle
        Settle(80);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();

        var vp = FindLyricsViewport(host.Scene);
        if (vp.IsNull)
        {
            Log.Info("[lyrics-probe] NO lyrics viewport found (rail not open / no current track / doc empty). Scrollers seen:");
            DumpScrollers(host.Scene);
            return;
        }
        var vr = host.Scene.AbsoluteRect(vp);
        host.Scene.TryGetScroll(vp, out var s0);
        Log.Info($"[lyrics-probe] lyrics viewport rect=({vr.X:0},{vr.Y:0} {vr.W:0}x{vr.H:0}) content={s0.ContentH:0} viewH={s0.ViewportH:0}");

        MeasureLyrics("IDLE  ", host, window, gpu, null);

        float vh = s0.ViewportH > 20f ? s0.ViewportH : vr.H;
        var pos = new Point2(vr.X + vr.W * 0.5f, vr.Y + vh * 0.5f);
        window.QueueInput(new InputEvent(InputKind.PointerMove, pos, 0, 0));
        Settle(2);
        MeasureLyrics("SCROLL", host, window, gpu, i => window.QueueInput(new InputEvent(InputKind.Wheel, pos, 0, 0, ((i / 18) & 1) == 0 ? +50f : -50f)));

        Log.Info("[lyrics-probe] done");
    }

    static void MeasureLyrics(string tag, AppHost host, Win32Window window, D3D12Device gpu, Action<int>? inject)
    {
        // (1) perceived FPS at TRUE vsync cadence (present waits for the vblank → a GPU-bound frame stretches the interval).
        var iv = new List<double>(260);
        long prev = Stopwatch.GetTimestamp();
        for (int i = 0; i < 220 && !window.IsClosed; i++)
        {
            inject?.Invoke(i);
            host.RunFrame();
            long now = Stopwatch.GetTimestamp();
            iv.Add((now - prev) * 1000.0 / Stopwatch.Frequency); prev = now;
        }
        var a = iv.ToArray(); double tot = 0, worst = 0; foreach (var v in a) { tot += v; if (v > worst) worst = v; }
        double mean = a.Length > 0 ? tot / a.Length : 0; int o16 = 0, o33 = 0; foreach (var v in a) { if (v > 16.7) o16++; if (v > 33.3) o33++; }

        // (2) vsync-suppressed CPU work breakdown + rendered-frame count (IDLE should render ~0 — a spin renders ~all).
        double sFrame = 0, sFlush = 0, sLayout = 0, sAnim = 0, sRecord = 0, sSubmit = 0, wWorst = 0;
        int rendered = 0, total = 0, g0 = GC.CollectionCount(0); long alloc0 = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 160 && !window.IsClosed; i++)
        {
            inject?.Invoke(i);
            gpu.SuppressLatencyWaitOnce(); gpu.SuppressVsyncOnce();
            var s = host.RunFrame();
            total++;
            if (s.Rendered || s.DrawCommandCount > 0)
            {
                rendered++;
                sFrame += s.FrameMs; sFlush += s.FlushMs; sLayout += s.LayoutMs; sAnim += s.AnimMs; sRecord += s.RecordMs; sSubmit += s.SubmitMs;
                if (s.FrameMs > wWorst) wWorst = s.FrameMs;
            }
        }
        int dg0 = GC.CollectionCount(0) - g0;
        int den = Math.Max(1, rendered);
        double allocKb = (GC.GetAllocatedBytesForCurrentThread() - alloc0) / 1024.0 / den;
        Log.Info($"[lyrics-fps] {tag} perceived(vsync): {a.Length}f mean {mean:0.0}ms ({(mean > 0 ? 1000.0 / mean : 0):0}fps) worst {worst:0.0}ms | <60fps={o16} <30fps={o33}");
        Log.Info($"             work(no-vsync): rendered {rendered}/{total} | frame {sFrame / den:0.00}ms (worst {wWorst:0.00}) = flush {sFlush / den:0.00} + layout {sLayout / den:0.00} + anim {sAnim / den:0.00} + record {sRecord / den:0.00} + submit {sSubmit / den:0.00} | alloc {allocKb:0.0}KB/f gen0={dg0}");
    }

    static NodeHandle FindLyricsViewport(FluentGpu.Scene.SceneStore scene)
    {
        var best = default(NodeHandle); float bestX = -1f;
        var stack = new Stack<NodeHandle>(); if (!scene.Root.IsNull) stack.Push(scene.Root);
        while (stack.Count > 0)
        {
            var n = stack.Pop(); if (n.IsNull || !scene.IsLive(n)) continue;
            if (scene.HasScroll(n) && scene.TryGetScroll(n, out var sc) && sc.ViewportH > 50f && sc.ContentH > sc.ViewportH + 1f)
            {
                var r = scene.AbsoluteRect(n);
                if (r.X > bestX) { bestX = r.X; best = n; }   // rightmost content scroller = the lyrics rail (right side)
            }
            for (var c = scene.FirstChild(n); !c.IsNull; c = scene.NextSibling(c)) stack.Push(c);
        }
        return best;
    }

    static void DumpScrollers(FluentGpu.Scene.SceneStore scene)
    {
        var stack = new Stack<NodeHandle>(); if (!scene.Root.IsNull) stack.Push(scene.Root);
        while (stack.Count > 0)
        {
            var n = stack.Pop(); if (n.IsNull || !scene.IsLive(n)) continue;
            if (scene.HasScroll(n) && scene.TryGetScroll(n, out var sc))
            { var r = scene.AbsoluteRect(n); Log.Info($"  scroller rect=({r.X:0},{r.Y:0} {r.W:0}x{r.H:0}) viewH={sc.ViewportH:0} contentH={sc.ContentH:0}"); }
            for (var c = scene.FirstChild(n); !c.IsNull; c = scene.NextSibling(c)) stack.Push(c);
        }
    }

    static void Run(AppHost host, Win32Window window, D3D12Device gpu)
    {
        Directory.CreateDirectory(ProbeArtifacts.Dir);
        var csv = new StringBuilder(1 << 16);
        csv.AppendLine("phase,frame,label,frameMs,flushMs,layoutMs,animMs,recordMs,submitMs,gen0,gen1,comps,nodes,draws,overBudget");
        bool keepVsync = Diag.EnvFlag("WAVEE_PROBE_VSYNC");

        // One measured frame under `phase`, tagged `label`. Strips the throttle (unless WAVEE_PROBE_VSYNC) so FrameMs is
        // pure production cost; captures the Gen0/Gen1 collection deltas so a GC spike is distinguishable from a work spike.
        FrameStats Measure(Phase phase, string label)
        {
            int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1);
            if (!keepVsync) { gpu.SuppressLatencyWaitOnce(); gpu.SuppressVsyncOnce(); }
            var s = host.RunFrame();
            int dg0 = GC.CollectionCount(0) - g0, dg1 = GC.CollectionCount(1) - g1;
            if (s.Rendered || s.DrawCommandCount > 0)
            {
                phase.Ms.Add(s.FrameMs);
                bool over = s.FrameMs > BudgetMs;
                if (over) { phase.OverBudget++; if (dg0 > 0) phase.OverBudgetGc++; }
                static string F(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);
                csv.Append(phase.Name).Append(',').Append(phase.Ms.Count.ToString(CultureInfo.InvariantCulture)).Append(',')
                   .Append(label).Append(',')
                   .Append(s.FrameMs.ToString("0.000", CultureInfo.InvariantCulture)).Append(',')
                   .Append(F(s.FlushMs)).Append(',').Append(F(s.LayoutMs)).Append(',').Append(F(s.AnimMs)).Append(',')
                   .Append(F(s.RecordMs)).Append(',').Append(F(s.SubmitMs)).Append(',')
                   .Append(dg0).Append(',').Append(dg1).Append(',')
                   .Append(s.ComponentsRendered).Append(',').Append(s.NodesVisited).Append(',').Append(s.DrawCommandCount).Append(',')
                   .Append(over ? '1' : '0').AppendLine();
            }
            return s;
        }

        void Nav(string key, string? arg) => WaveeShell.ProbeNav!(key, arg);
        void Settle(int n) { for (int i = 0; i < n && !window.IsClosed; i++) host.RunFrame(); }

        // ── Warmup (not measured); collect once so the baseline starts from a known GC state. ──
        Log.Info("[wavee-nav-probe] warmup");
        for (int i = 0; i < 80 && !window.IsClosed; i++) { gpu.SuppressLatencyWaitOnce(); gpu.SuppressVsyncOnce(); host.RunFrame(); }
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();

        // ── Phase 0: HOME ↔ DETAIL (the named case). Bounce home → a fresh playlist → home → next playlist …, holding a
        //    few settle frames each so the cold-mount transition frame AND its settle are captured + labelled by route. ──
        var homeDetail = new Phase("home<->detail");
        Log.Info("[wavee-nav-probe] home<->detail (the named case)");
        bool capture = Diag.EnvFlag("WAVEE_PB_CAPTURE");   // dump the first home→detail transition frame-by-frame to PNG

        // Player-bar-glitch repro: the EXACT Home-card click path (preview → DetailShell preview-path mount + Hero fly),
        // which the sidebar-style ProbeNav never exercises. Home must be mounted first (the fly's source card).
        if (capture && WaveeShell.ProbeCardNav is not null)
        {
            Log.Info("[wavee-nav-probe] card-click capture (home→detail via preview + Hero fly)");
            Nav("home", null);
            for (int f = 0; f < 40 && !window.IsClosed; f++) host.RunFrame();
            System.Threading.Thread.Sleep(700);                         // let card covers decode (async) so the fly has an image source
            for (int f = 0; f < 20 && !window.IsClosed; f++) host.RunFrame();
            string? flyKey = host.FirstMorphKey;                        // a REAL home-card key → the morph actually FLIES
            Log.Info($"[wavee-nav-probe] fly key = {flyKey ?? "(none found)"}");
            WaveeShell.ProbeCardNav(flyKey ?? "pl:spotify:playlist:pl0", null, true);
            for (int f = 0; f < 12 && !window.IsClosed; f++)
            {
                gpu.SuppressLatencyWaitOnce(); gpu.SuppressVsyncOnce();
                host.RunFrame();
                var px = gpu.CaptureBgra(out int cw, out int ch); PngWriter.WriteBgra(ProbeArtifacts.PathFor($"card_{f:D2}.png"), px, cw, ch);
            }
        }
        for (int rep = 0; rep < 6 && !window.IsClosed; rep++)
        {
            Nav("home", null);
            for (int f = 0; f < 8 && !window.IsClosed; f++) Measure(homeDetail, "home");
            var (k, a) = HeavyRoutes[2 + rep % 6];   // pl0..pl5 (fresh each bounce → real cold mount)
            Nav(k, a);
            for (int f = 0; f < 8 && !window.IsClosed; f++)
            {
                Measure(homeDetail, k);
                if (capture && rep == 0) { var px = gpu.CaptureBgra(out int cw, out int ch); PngWriter.WriteBgra(ProbeArtifacts.PathFor($"pb_{f:D2}.png"), px, cw, ch); }
            }
        }

        // ── Phase 0b: ALBUM → ALBUM (the reported related-albums hitch). Two sub-phases over DISTINCT heavy album pages
        //    (cover + tracks + related/similar/merch trailing shelves): SKELETON = today's bare related-card nav (no preview
        //    → DetailPage.Skel.Region full-page remount on every hop); PREVIEW = the fix (a related card now opens via
        //    DetailNav.OpenAlbum, stashing the card's partial model → DetailPage reuses the mounted shell in place). Same
        //    routes, same settle frames, started from album land (a reused detail slot) — so the delta IS the remount the
        //    fix removes. Distinct album indices per sub-phase so the second isn't warmed by the first's data/image cache. ──
        var albumSkel = new Phase("album skel");
        var albumPrev = new Phase("album preview");
        if (WaveeShell.ProbeOpenAlbum is not null)
        {
            const int AlbumSettle = 12;
            var skelAlbums = new List<Wavee.Core.Album>(8);
            var prevAlbums = new List<Wavee.Core.Album>(8);
            for (int i = 0; i < 8; i++) { skelAlbums.Add(Wavee.Core.FakeData.Album(120 + i)); prevAlbums.Add(Wavee.Core.FakeData.Album(140 + i)); }
            Log.Info("[wavee-nav-probe] album→album (skeleton vs preview, 8 hops each)");

            // OLD path: enter album land, then hop album→album via BARE nav (no preview) — skeleton remount each hop.
            Nav("home", null); Settle(10);
            Nav("album:" + skelAlbums[0].Uri, skelAlbums[0].Name); Settle(AlbumSettle);
            for (int i = 1; i < skelAlbums.Count && !window.IsClosed; i++)
            {
                Nav("album:" + skelAlbums[i].Uri, skelAlbums[i].Name);   // bare GoNav — no preview → Skel.Region remount
                for (int f = 0; f < AlbumSettle && !window.IsClosed; f++) Measure(albumSkel, skelAlbums[i].Uri);
            }

            // FIXED path: same shape, but each hop opens via DetailNav.OpenAlbum (stash preview + nav) — reused-shell fast path.
            Nav("home", null); Settle(10);
            WaveeShell.ProbeOpenAlbum(prevAlbums[0]); Settle(AlbumSettle);
            for (int i = 1; i < prevAlbums.Count && !window.IsClosed; i++)
            {
                WaveeShell.ProbeOpenAlbum(prevAlbums[i]);                // the fixed related-card path
                for (int f = 0; f < AlbumSettle && !window.IsClosed; f++) Measure(albumPrev, prevAlbums[i].Uri);
            }
        }

        // ── Phase 1: NAV-SETTLE across all route types (2 sweeps). ──
        var navSettle = new Phase("nav-settle");
        const int SettleFrames = 14;
        var sweep = new List<(string, string?)>();
        for (int s = 0; s < HeavyRoutes.Length; s++) { sweep.Add(HeavyRoutes[s]); if (s < CheapRoutes.Length) sweep.Add(CheapRoutes[s]); }
        Log.Info($"[wavee-nav-probe] nav-settle ({sweep.Count} routes x 2 sweeps)");
        for (int rep = 0; rep < 2 && !window.IsClosed; rep++)
            foreach (var (key, arg) in sweep)
            {
                if (window.IsClosed) break;
                Nav(key, arg);
                for (int f = 0; f < SettleFrames && !window.IsClosed; f++) Measure(navSettle, key);
            }

        // ── Phase 2: NAV-HAMMER (route swap every frame — worst-case churn). ──
        var hammer = new Phase("nav-hammer");
        Log.Info("[wavee-nav-probe] nav-hammer (400 frames)");
        for (int i = 0; i < 400 && !window.IsClosed; i++)
        {
            var (key, arg) = HeavyRoutes[i % HeavyRoutes.Length];
            Nav(key, arg);
            Measure(hammer, key);
        }

        // ── Phase 3: BACK / FORWARD (KeepAlive page transition; connected cover animation is disabled in Wavee). ──
        var backfwd = new Phase("back-forward");
        for (int i = 0; i < HeavyRoutes.Length && !window.IsClosed; i++) { var (k, a) = HeavyRoutes[i]; Nav(k, a); Settle(3); }
        Log.Info("[wavee-nav-probe] back-forward (240 frames)");
        for (int i = 0; i < 240 && !window.IsClosed; i++)
        {
            if ((i / 8) % 2 == 0) WaveeShell.ProbeBack?.Invoke(); else WaveeShell.ProbeForward?.Invoke();
            Measure(backfwd, "nav");
            // Capture the FIRST back/forward transition (the connected-animation Hero-fly path) frame-by-frame.
            if (capture && i < 16) { var px = gpu.CaptureBgra(out int cw, out int ch); PngWriter.WriteBgra(ProbeArtifacts.PathFor($"bf_{i:D2}.png"), px, cw, ch); }
        }

        // ── Phase 4: SCROLL via REAL mouse-wheel INPUT. NOT WriteScrollOffset (that bypasses input): here every notch is an
        //    InputKind.Wheel event injected through window.QueueInput → PumpInto → the dispatcher ring → DispatchWheel/ScrollAt
        //    (hit-test the cursor pos, route to the nearest VERTICAL scroller), and SmoothScroll eases each +60 DIP notch via
        //    the scroll kernel (inertia). So this measures the full real path a mouse drives: hit-test + wheel routing + the
        //    eased per-frame re-layout / virtualization re-realize / re-record, including the inertial COAST after a flick. ──
        var scroll = new Phase("scroll (real wheel)");
        if (Diag.EnvFlag("WAVEE_LIKED_SHOT")) { window.SetClientSize(1456, 820); Settle(12); Nav("liked", null); Settle(50); var px0 = gpu.CaptureBgra(out int cw0, out int ch0); PngWriter.WriteBgra(ProbeArtifacts.PathFor("liked_header.png"), px0, cw0, ch0); }
        // Navigate to a TWO-COLUMN playlist: its track list cross-stretches to the rail height → a real non-zero viewport that
        // renders rows the wheel can hit-test. (The single-column Liked list measures a 0-height viewport — a pre-existing bug
        // that leaves it empty + un-hit-testable; not what we want to audit scroll feel on.)
        Nav("home", null); Settle(20);
        System.Threading.Thread.Sleep(600);
        for (int f = 0; f < 20 && !window.IsClosed; f++) host.RunFrame();
        var skeys = new List<string>(); host.CollectMorphKeys(skeys);
        Nav(skeys.Count > 0 ? skeys[0] : "pl:local:1", null); Settle(40);
        var viewport = FindLargestScrollViewport(host.Scene);
        if (viewport.IsNull) Log.Info("[wavee-nav-probe] scroll: no scroll viewport — skipped");
        else
        {
            var vr = host.Scene.AbsoluteRect(viewport);
            host.Scene.TryGetScroll(viewport, out var s0);
            // Cursor over the viewport centre (layout-DIP, the hit-test space). The scroll node's own box height can be 0, so
            // use the ScrollState ViewportH to place the point over a REAL row (so HitTestAny descends to it and routes the wheel).
            float vh = s0.ViewportH > 20f ? s0.ViewportH : (vr.H > 20f ? vr.H : 400f);
            var pos = new Point2(vr.X + vr.W * 0.5f, vr.Y + vh * 0.5f);
            window.QueueInput(new InputEvent(InputKind.PointerMove, pos, 0, 0));   // a real mouse hovers first → routes the wheel here + reveals the auto-hide scrollbar
            Settle(2);
            Log.Info($"[wavee-nav-probe] scroll: REAL wheel @ ({pos.X:0},{pos.Y:0}) viewport content {s0.ContentH:0} viewH {s0.ViewportH:0}");

            // Brisk continuous spin — ~1 notch/frame DOWN then UP. The offset crosses item boundaries continuously, so the
            // virtualized list recycles/re-realizes rows every frame (the heaviest steady-scroll case). Inject + measure each frame.
            for (int i = 0; i < 200 && !window.IsClosed; i++)
            {
                window.QueueInput(new InputEvent(InputKind.Wheel, pos, 0, 0, i < 100 ? +60f : -60f));
                Measure(scroll, i < 100 ? "wheel-down" : "wheel-up");
            }
            // Flick-and-coast: a 6-notch burst, then STOP — the scroll kernel eases the residual to rest. These COAST frames
            // are exactly what a programmatic offset-set can never see (it lands instantly); a real flick animates for ~15 frames.
            for (int rep = 0; rep < 6 && !window.IsClosed; rep++)
            {
                for (int k = 0; k < 6 && !window.IsClosed; k++) { window.QueueInput(new InputEvent(InputKind.Wheel, pos, 0, 0, +60f)); Measure(scroll, "flick"); }
                for (int st = 0; st < 16 && !window.IsClosed; st++) Measure(scroll, "coast");
            }
            host.Scene.TryGetScroll(viewport, out var s1);
            bool moved = Math.Abs(s1.OffsetY - s0.OffsetY) > 1f;
            Log.Info($"[wavee-nav-probe] scroll: endOff {s1.OffsetY:0} — wheel input {(moved ? "TOOK EFFECT (offset moved by dispatch+ease)" : "NO-OP — routing FAILED")}");

            // REAL-vsync wheel-scroll FPS: drive notches at TRUE present cadence (no vsync suppression) and time the wall-clock
            // frame interval — the FPS a user actually perceives while spinning the wheel. Oscillate so the list never sits clamped.
            var iv = new List<double>(220);
            long prev = Stopwatch.GetTimestamp();
            for (int i = 0; i < 200 && !window.IsClosed; i++)
            {
                window.QueueInput(new InputEvent(InputKind.Wheel, pos, 0, 0, ((i / 20) % 2 == 0) ? +60f : -60f));   // 20 down / 20 up → always moving
                host.RunFrame();
                long now = Stopwatch.GetTimestamp(); iv.Add((now - prev) * 1000.0 / Stopwatch.Frequency); prev = now;
            }
            var a = iv.ToArray(); double tot = 0, worst = 0; foreach (var v in a) { tot += v; if (v > worst) worst = v; }
            double mean = a.Length > 0 ? tot / a.Length : 0; int o16 = 0; foreach (var v in a) if (v > 16.7) o16++;
            Log.Info($"[scroll-fps] REAL wheel scroll at vsync: {a.Length} frames mean {mean:0.0}ms ({(mean > 0 ? 1000.0 / mean : 0):0} fps) worst {worst:0.0}ms ({(worst > 0 ? 1000.0 / worst : 0):0} fps) >16.7ms(<60fps)={o16}");
        }

        // ── Phase 5: THEME TOGGLE (RethemeAll re-renders every component + arms the cross-fade). ──
        var theme = new Phase("theme-toggle");
        Nav("home", null); Settle(6);
        Log.Info("[wavee-nav-probe] theme-toggle (240 frames)");
        for (int i = 0; i < 240 && !window.IsClosed; i++)
        {
            if (i % 20 == 0) WaveeShell.ProbeTheme?.Invoke();
            Measure(theme, i % 20 == 0 ? "flip" : "settle");
        }

        // ── Phase 6: IDLE (steady-state floor). ──
        var idle = new Phase("idle");
        Nav("home", null); Settle(10);
        Log.Info("[wavee-nav-probe] idle (180 frames)");
        for (int i = 0; i < 180 && !window.IsClosed; i++) Measure(idle, "idle");

        // ── Phase: CONNECTED ANIMATION (Hero cover fly) cost. Trigger REAL flies (home→detail→back) and measure the
        //    per-frame production cost WHILE a fly is in flight, vs the SAME navigation on the SAME path WITHOUT the fly
        //    (doMorph=false) — so the delta is purely the connected-animation overhead. Needs card covers loaded first. ──
        var connFly = new Phase("conn-anim FLY");
        var connNo = new Phase("conn-anim no-fly");
        if (WaveeShell.ProbeCardNav is not null)
        {
            Nav("home", null);
            for (int f = 0; f < 40 && !window.IsClosed; f++) host.RunFrame();
            System.Threading.Thread.Sleep(800);                        // let card covers decode (the fly needs an image source)
            for (int f = 0; f < 20 && !window.IsClosed; f++) host.RunFrame();
            string? flyKey = host.FirstMorphKey;
            Log.Info($"[wavee-nav-probe] conn-anim fly key = {flyKey ?? "(none)"}");
            if (flyKey is not null)
            {
                var keys = new List<string>(); host.CollectMorphKeys(keys);
                // FRESH flies (DISTINCT cards → cold mount + load + fly) WITH per-segment capture, to pin where the
                // ~9-10ms/frame that drops the fly to 60fps actually goes (flush=realize, record=image crossfades, …).
                for (int ki = 0; ki < keys.Count && ki < 12 && !window.IsClosed; ki++)
                {
                    Nav("home", null);
                    for (int f = 0; f < 16 && !window.IsClosed; f++) host.RunFrame();
                    WaveeShell.ProbeCardNav(keys[ki], null, true);
                    for (int f = 0; f < 16 && !window.IsClosed; f++) Measure(connFly, "fresh");
                }
                for (int rep = 0; rep < 14 && !window.IsClosed; rep++)   // SAME nav, NO fly (preview path, no morph)
                {
                    WaveeShell.ProbeCardNav(flyKey, null, false);
                    for (int f = 0; f < 14 && !window.IsClosed; f++) Measure(connNo, "fwd");
                    WaveeShell.ProbeBack?.Invoke();
                    for (int f = 0; f < 14 && !window.IsClosed; f++) Measure(connNo, "back");
                }

                // ── REAL FPS of a COMPLETE Hero fly: present at TRUE vsync cadence (do NOT suppress vsync/latency) and time
                //    the WALL-CLOCK frame interval click→settle, so this is the FPS actually perceived during the fly. Fly to
                //    DISTINCT home-card pages so each is a FRESH (uncached) cold mount + fly — the realistic worst case. ──
                host.CollectMorphKeys(keys);   // reuse the list collected above
                Log.Info($"[fly-fps] complete connected animations at REAL vsync cadence — {keys.Count} fresh home-card keys (interval ms / fps):");
                for (int fly = 0; fly < keys.Count && fly < 6 && !window.IsClosed; fly++)
                {
                    Nav("home", null);
                    for (int f = 0; f < 18 && !window.IsClosed; f++) host.RunFrame();   // home live (the fly's source card) — not measured
                    var iv = new List<double>(96);
                    double sFlush = 0, sLayout = 0, sAnim = 0, sRecord = 0, sSubmit = 0; int sN = 0;
                    WaveeShell.ProbeCardNav(keys[fly], null, true);                       // CLICK a FRESH card — fly + cold mount start
                    long prev = Stopwatch.GetTimestamp();
                    for (int f = 0; f < 60 && !window.IsClosed; f++)
                    {
                        var s = host.RunFrame();                                         // vsync NOT suppressed → real present pacing
                        long now = Stopwatch.GetTimestamp();
                        double dms = (now - prev) * 1000.0 / Stopwatch.Frequency;
                        iv.Add(dms); prev = now;
                        if (f < 20) { sFlush += s.FlushMs; sLayout += s.LayoutMs; sAnim += s.AnimMs; sRecord += s.RecordMs; sSubmit += s.SubmitMs; sN++; }
                    }
                    var arr = iv.ToArray();
                    double tot = 0, worst = 0; foreach (var v in arr) { tot += v; if (v > worst) worst = v; }
                    double mean = tot / arr.Length;
                    int over16 = 0, over33 = 0; foreach (var v in arr) { if (v > 16.7) over16++; if (v > 33.3) over33++; }
                    Log.Info($"[fly-fps] fresh fly#{fly}: {arr.Length} frames {tot:0}ms | mean {mean:0.0}ms ({1000.0/mean:0} fps) | worst {worst:0.0}ms ({1000.0/worst:0} fps) | >16.7ms(<60fps)={over16} >33ms(<30fps)={over33}");
                    if (sN > 0) Log.Info($"           REAL-vsync segs(first {sN}): flush {sFlush/sN:0.0} layout {sLayout/sN:0.0} anim {sAnim/sN:0.0} record {sRecord/sN:0.0} submit {sSubmit/sN:0.0}");
                    var head = new StringBuilder("           first 18 intervals(ms):");
                    for (int i = 0; i < Math.Min(18, arr.Length); i++) head.Append(' ').Append(arr[i].ToString("0", CultureInfo.InvariantCulture));
                    Log.Info(head.ToString());
                }
            }
        }

        // ── Report ──
        var phases = new[] { idle, scroll, backfwd, connNo, connFly, albumSkel, albumPrev, homeDetail, navSettle, hammer, theme };
        var all = new Phase("ALL");
        foreach (var p in phases) { all.Ms.AddRange(p.Ms); all.OverBudget += p.OverBudget; all.OverBudgetGc += p.OverBudgetGc; }

        var sb = new StringBuilder(8192);
        sb.AppendLine();
        sb.AppendLine("=== WAVEE STRESS — per-frame PRODUCTION time (ms); target = every frame < 8.33 ms (one 120 Hz vblank) ===");
        sb.AppendLine(keepVsync ? "(WAVEE_PROBE_VSYNC: vblank-paced — FrameMs includes the present wait)" : "(vsync/latency throttle removed → pure work cost)");
        sb.AppendLine();
        sb.AppendLine($"{"phase",-14} {"n",5} {"p50",6} {"p90",6} {"p99",7} {"p99.9",7} {"max",8}   {"over8.3ms",10} {"(ofwhich GC)",12}");
        foreach (var p in phases) sb.AppendLine(Format(p));
        sb.AppendLine(new string('-', 96));
        sb.AppendLine(Format(all));
        sb.AppendLine();
        sb.AppendLine("over8.3ms = frames whose WORK would miss a vblank (the stutters). (ofwhich GC) = those coinciding with a Gen0+ collection.");

        // Worst 15 frames overall, broken down by segment — the exact spikes to remedy.
        var rows = new List<(string Tag, double Ms, double Flush, double Layout, double Anim, double Record, double Submit, int G0)>();
        foreach (var line in csv.ToString().Split('\n'))
        {
            var c = line.Split(',');
            if (c.Length < 15 || c[0] == "phase") continue;
            double D(int i) => double.TryParse(c[i], NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;
            int.TryParse(c[9], out int g0);
            rows.Add((c[0] + ":" + c[2], D(3), D(4), D(5), D(6), D(7), D(8), g0));
        }
        rows.Sort((a, b) => b.Ms.CompareTo(a.Ms));
        sb.AppendLine();
        sb.AppendLine($"Worst 15 frames — {"total",7} = {"flush",6} + {"layout",6} + {"anim",5} + {"record",6} + {"submit",6}  (gc=Gen0)  transition");
        for (int i = 0; i < Math.Min(15, rows.Count); i++)
        {
            var r = rows[i];
            sb.AppendLine($"  {r.Ms,7:0.00} = {r.Flush,6:0.00} + {r.Layout,6:0.00} + {r.Anim,5:0.00} + {r.Record,6:0.00} + {r.Submit,6:0.00}  gc={r.G0}  {r.Tag}");
        }

        string report = sb.ToString();
        Log.Info(report);
        File.WriteAllText(ProbeArtifacts.PathFor("wavee-nav-probe.csv"), csv.ToString());
        File.WriteAllText(ProbeArtifacts.PathFor("wavee-nav-probe-summary.txt"), report);
        Log.Info("[wavee-nav-probe] wrote " + ProbeArtifacts.PathFor("wavee-nav-probe.csv") + " + -summary.txt");
    }

    static string Format(Phase p)
    {
        if (p.Ms.Count == 0) return $"{p.Name,-14} {0,5}   (no frames)";
        var a = p.Ms.ToArray();
        Array.Sort(a);
        return $"{p.Name,-14} {a.Length,5} {Pct(a, 50),6:0.00} {Pct(a, 90),6:0.00} {Pct(a, 99),7:0.00} {Pct(a, 99.9),7:0.00} {a[^1],8:0.00}   {p.OverBudget,10} {p.OverBudgetGc,12}";
    }

    static double Pct(double[] sorted, double p)
    {
        if (sorted.Length == 0) return 0;
        if (sorted.Length == 1) return sorted[0];
        double rank = p / 100.0 * (sorted.Length - 1);
        int lo = (int)Math.Floor(rank), hi = (int)Math.Ceiling(rank);
        return sorted[lo] * (1 - (rank - lo)) + sorted[hi] * (rank - lo);
    }

    // The most-scrollable viewport by VERTICAL CONTENT extent. Ranking by box area fails here: a virtualized list's scroll
    // node reports a laid-out box HEIGHT of 0 (its extent lives in ScrollState.ContentH, not the rect), so a tall-but-narrow
    // sidebar would win on area. ContentH picks the real content list and ignores horizontal-only scrollers.
    static NodeHandle FindLargestScrollViewport(FluentGpu.Scene.SceneStore scene)
    {
        var best = default(NodeHandle);
        float bestContent = 0f;
        var stack = new Stack<NodeHandle>();
        if (!scene.Root.IsNull) stack.Push(scene.Root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (n.IsNull || !scene.IsLive(n)) continue;
            if (scene.HasScroll(n) && scene.TryGetScroll(n, out var sc))
            {
                var r = scene.AbsoluteRect(n);
                // Real, hit-testable CONTENT scroller: a non-zero viewport (a 0-height viewport stops hit-test descent),
                // genuinely scrollable, and past the sidebar (x>100) so we never pick the nav rail. Ranked by content extent.
                if (sc.ViewportH > 50f && sc.ContentH > sc.ViewportH + 1f && r.X > 100f && sc.ContentH > bestContent)
                { bestContent = sc.ContentH; best = n; }
            }
            for (var c = scene.FirstChild(n); !c.IsNull; c = scene.NextSibling(c)) stack.Push(c);
        }
        return best;
    }

    static NodeHandle FindScrollViewportByKey(FluentGpu.Scene.SceneStore scene, string key)
    {
        var stack = new Stack<NodeHandle>();
        if (!scene.Root.IsNull) stack.Push(scene.Root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (n.IsNull || !scene.IsLive(n)) continue;
            if (scene.HasScroll(n) && scene.TryGetScroll(n, out var sc)
                && string.Equals(sc.ScrollKey, key, StringComparison.Ordinal))
                return n;
            for (var c = scene.FirstChild(n); !c.IsNull; c = scene.NextSibling(c)) stack.Push(c);
        }
        return NodeHandle.Null;
    }
}
