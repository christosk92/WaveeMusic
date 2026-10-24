// ── Shell/PanelHost.cs ─────────────────────────────────────────────────────────────────────────────────────────────
// the right PANEL as one core host (themes plan C3, X9/X19): its state (open, mode, width, floats, the docked video), the verbs
// a panel view calls (show a mode, close, commit a resize, put the video somewhere, open the stage), the persisted sizes
// behind one service, and the "Next up" window. A theme's panel view draws `PanelState` and calls `IPanelVerbs`; it never
// writes `Shell.Ui`, `Platform.Settings` or `Video.State` itself.
//
// Role: CORE
// Spec: ch 21 §1-§6, themes technical plan §5.1 row C3
//
// Before this, those writes lived in `Rail.UI.cs` (`CommitCap`, `ShowVideoAt`, the close button, the NowPlaying demand, the
// friends activation, the "Next up" windowing) and `Shell.UI.cs` (`CommitRailDrag`, the persisted-state seed).
//
// The CELLS stay where they have always been (`Shell.Ui`): every reader of the rail state binds those very signals, so moving
// them would change nothing but a name. `PanelCells.Frame` is the one view of them; `PanelCells.Fresh()` is a private set
// for a unit fact. Everything the host reaches OUTSIDE its cells (the video model, the stage, the entity demand, the friends
// feed) goes through `IPanelEnv`, so `PanelHostTests` drives the real host with a recording env and no engine.

using FluentGpu.Signals;

namespace Wavee;

public static partial class Shell
{
    // ══ STATE ════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The docked video's persisted cap: its height and whether the user pinned it against the content fit.</summary>
    public readonly record struct DockedVideoState(float Height, bool Pinned);

    /// <summary>What a panel view draws. <see cref="Width"/> is the COMMITTED width (the layout's; a drag previews on
    /// <see cref="PanelCells.DragWidth"/> and commits on release). <see cref="Floats"/> is true when the window tier does not dock
    /// the panel and it overlays the content instead; <see cref="Placement"/> says how (floating or a bottom sheet).</summary>
    public readonly record struct PanelState(bool Open, RailMode Mode, float Width, bool Floats, DockedVideoState DockedVideo,
        Theming.Contracts.PanelPlacement Placement = Theming.Contracts.PanelPlacement.Dock);

    /// <summary>The panel's cells. The shell's are <see cref="Ui"/>'s own signals (<see cref="Frame"/>). The last five are the window
    /// tier's placement facts and the docked panes (TC2, ZT2); a unit fact that leaves them out gets private ones (one docked panel, and
    /// Classic's queue beside it where a fact allows two).</summary>
    public sealed class PanelCells(Signal<bool> open, Signal<RailMode> mode, FloatSignal width, FloatSignal dragWidth,
        FloatSignal dockedVideoHeight, Signal<bool> dockedVideoPinned, FloatSignal bodyHeight, Signal<bool> fits,
        Signal<bool>? queueBeside = null, Signal<bool>? secondAllowed = null, FloatSignal? floor = null,
        Signal<Theming.Contracts.PanelPlacement>? placement = null, Signal<Theming.PanelArrangement>? panes = null)
    {
        public Signal<bool> Open { get; } = open;
        public Signal<RailMode> Mode { get; } = mode;
        public FloatSignal Width { get; } = width;
        public FloatSignal DragWidth { get; } = dragWidth;
        public FloatSignal DockedVideoHeight { get; } = dockedVideoHeight;
        public Signal<bool> DockedVideoPinned { get; } = dockedVideoPinned;
        public FloatSignal BodyHeight { get; } = bodyHeight;
        public Signal<bool> Fits { get; } = fits;

        /// <summary>The queue is open beside the primary panel (an INDEPENDENT docked pane, Classic's, where the tier docks two). A linked
        /// arrangement never reads it: its panes open with the panel.</summary>
        public Signal<bool> QueueBeside { get; } = queueBeside ?? new Signal<bool>(false);

        /// <summary>The window tier docks two panels (or more).</summary>
        public Signal<bool> SecondAllowed { get; } = secondAllowed ?? new Signal<bool>(false);

        /// <summary>The panes the window tier docks beside the primary, in order (contract 1.29); read only while
        /// <see cref="SecondAllowed"/> holds.</summary>
        public Signal<Theming.PanelArrangement> Panes { get; } = panes ?? new Signal<Theming.PanelArrangement>(Theming.PanelArrangement.QueueBeside);

        /// <summary>The least width a docked panel keeps in the window tier (0: none).</summary>
        public FloatSignal Floor { get; } = floor ?? new FloatSignal(0f);

        /// <summary>Where the window tier lays the panel out.</summary>
        public Signal<Theming.Contracts.PanelPlacement> Placement { get; } = placement ?? new Signal<Theming.Contracts.PanelPlacement>(Theming.Contracts.PanelPlacement.Dock);

        /// <summary>The shell's panel: the very signals every reader binds.</summary>
        public static readonly PanelCells Frame = new(Ui.RailOpen, Ui.Mode, Ui.RailWidth, Ui.RailDragWidth,
            Ui.DockedVideoHeight, Ui.DockedVideoHeightPinned, Ui.RailBodyHeight, Ui.RailFits,
            Ui.QueueBeside, Ui.SecondPaneAllowed, Ui.PanelFloor, Ui.PanelPlacement, Ui.PanelPanes);

        /// <summary>A private set at <see cref="Ui"/>'s defaults (closed, Lyrics, 340 wide, fits).</summary>
        public static PanelCells Fresh() => new(new Signal<bool>(false), new Signal<RailMode>(RailMode.Lyrics),
            new FloatSignal(RailDefaultW), new FloatSignal(RailDefaultW), new FloatSignal(DockedVideoNaturalH(RailDefaultW)),
            new Signal<bool>(false), new FloatSignal(0f), new Signal<bool>(true));
    }

    /// <summary>The docked video card of a pane that hosts none (a docked pane beside the primary, TC2): zero size, never hit.</summary>
    internal static FluentGpu.Dsl.Element NoDockedCard() => new FluentGpu.Dsl.BoxEl { Width = 0f, Height = 0f, Shrink = 0f, HitTestVisible = false };

    // ══ THE PERSISTED SIZES ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The two sizes the panel persists.</summary>
    public enum PanelSize : byte { RailWidth, DockedVideoHeight }

    /// <summary>Where a size lives. Production is the ACTIVE THEME's state (<see cref="PanelSizeKeys"/>, owner Q6: panel sizes are per
    /// theme); a unit fact swaps in a dictionary.</summary>
    public interface IPanelSizeStore
    {
        float Get(PanelSize size);
        void Set(PanelSize size, float value);
    }

    /// <summary>The panel's two theme-state keys (`theme.&lt;id&gt;.state.panelWidth` / `.dockedVideoHeight`), declared here because the core host
    /// writes them. The width is clamped to the shell's baseline bounds on every read and write (the active panel policy narrows it
    /// further on load); a docked height of 0 means "not set yet: follow the 16:9 floor".</summary>
    public static class PanelSizeKeys
    {
        public static readonly Theming.StateKey<float> Width = new("panelWidth", RailDefaultW, RailMinW, RailMaxW);
        public static readonly Theming.StateKey<float> DockedVideoHeight = new("dockedVideoHeight", 0f, 0f, DockedVideoMaxH);
    }

    sealed class ThemeStatePanelSizeStore : IPanelSizeStore
    {
        public float Get(PanelSize size) => Theming.ThemeState.Get(size == PanelSize.RailWidth ? PanelSizeKeys.Width : PanelSizeKeys.DockedVideoHeight);

        public void Set(PanelSize size, float value)
            => Theming.ThemeState.Set(size == PanelSize.RailWidth ? PanelSizeKeys.Width : PanelSizeKeys.DockedVideoHeight, value);
    }

    /// <summary>The panel's persisted sizes behind one service: what to START at (clamped, so a stale or hand-edited value
    /// never escapes the panel's bounds) and where a committed size goes.</summary>
    public sealed class PanelSizes(IPanelSizeStore store)
    {
        /// <summary>The theme-state-backed sizes the shell uses.</summary>
        public static readonly PanelSizes Shared = new(new ThemeStatePanelSizeStore());

        /// <summary>The width to start at.</summary>
        public float LoadWidth() => ClampRailWidth(store.Get(PanelSize.RailWidth));

        /// <summary>The docked video's height to start at, against <paramref name="railWidth"/> (the 16:9 floor follows it).</summary>
        public float LoadDockedVideoHeight(float railWidth)
            => ClampDockedVideoHeight(store.Get(PanelSize.DockedVideoHeight), railWidth);

        public void SaveWidth(float width) => store.Set(PanelSize.RailWidth, width);

        public void SaveDockedVideoHeight(float height) => store.Set(PanelSize.DockedVideoHeight, height);
    }

    // ══ "NEXT UP" ════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The rows the panel's "Next up" list shows: at most <see cref="Max"/> of the queue's upcoming rows, the TRACK
    /// ones (an episode is not a panel row). A caller must already subscribe the queue edge.</summary>
    public static class NextUpWindow
    {
        public const int Max = 5;

        /// <summary>PURE: the queue indices of the window. Looks at the first <c>min(into.Length, length)</c> upcoming rows from
        /// <paramref name="start"/> and keeps the tracks, in order; the window never reaches past those rows to make up the
        /// count. Returns how many were written.</summary>
        public static int Pick(bool hasUpNext, int start, int length, Span<int> into, Func<int, EntityRef> refAt)
        {
            if (!hasUpNext) return 0;
            int n = Math.Min(into.Length, length), count = 0;
            for (int i = start; i < start + n; i++)
            {
                var r = refAt(i);
                if (r.Kind != EntityKind.Track || r.IsNone) continue;
                into[count++] = i;
            }
            return count;
        }

        /// <summary>The window over the live queue.</summary>
        public static int Fill(Span<int> into)
        {
            bool has = Queue.UpNext(out int start, out int length);
            return Pick(has, start, length, into, Queue.RefAt);
        }

        /// <summary>Is there any "Next up" row? The parents ask it to decide whether the section mounts at all.</summary>
        public static bool Any()
        {
            Span<int> window = stackalloc int[Max];
            return Fill(window) > 0;
        }
    }

    // ══ THE VERBS ════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>What a panel view can ask of the panel.</summary>
    public interface IPanelVerbs
    {
        /// <summary>Show <paramref name="mode"/> and open the panel (never closes it; see <see cref="Toggle"/>).</summary>
        void SetMode(RailMode mode);

        /// <summary>The mode's door: clicking the mode already showing CLOSES the panel; otherwise switch to it and open.</summary>
        void Toggle(RailMode mode);

        /// <summary>Close the panel, whatever it shows (a docked video is demoted by the coupling). Where one panel shows at a time it
        /// also forgets the queue beside it, so closing never uncovers a second panel.</summary>
        void Close();

        /// <summary>Close the docked pane beside the primary panel: Classic's queue pane on its own; a linked arrangement's panes close
        /// with the panel, so their close closes the panel.</summary>
        void CloseQueuePane();

        /// <summary>Commit the width the seam preview holds: the layout, the docked video and the persisted width change here,
        /// once per drag. Nothing happens when there is nothing to commit.</summary>
        void ResizePanel();

        /// <summary>Commit the docked video's height: clamped, pinned against the content fit for as long as this source plays
        /// (the card clears the pin at the next source) and persisted.</summary>
        void ResizeDockedVideo();

        /// <summary>A placement intent: re-stamp the availability THIS playable has, then open the video at <paramref name="at"/>.</summary>
        void ShowVideoAt(Video.SurfacePlacement at);

        /// <summary>Turn the docked video back into the art ("switch to audio").</summary>
        void HideVideo();

        /// <summary>Enter the fullscreen stage (<paramref name="begin"/> is the caller's <c>UseContext(SharedTransition.Begin)</c>).</summary>
        void OpenStage(Action<string>? begin, string cause);
    }

    /// <summary>Everything the host reaches outside its own cells.</summary>
    public interface IPanelEnv
    {
        /// <summary>Does the deck's track have a video (peeked)?</summary>
        bool NowTrackHasVideo();

        void OpenVideoAt(Video.SurfacePlacement at, bool hasVideo);

        void CloseDockedVideo();

        void OpenStage(Action<string>? begin, string cause);

        /// <summary>The now-playing body's entity demand: the rail is not a page and never mounts on a route, so it asks for
        /// the PLAYING track's identity and its first artist's overview.</summary>
        void DemandNowPlaying(int trackSlot, int artistSlot);

        /// <summary>The friends panel is mounted (true) or not (false): drives the feed's watchdog.</summary>
        void SetFriendsActive(bool active);
    }

    sealed class PanelEnv : IPanelEnv
    {
        public bool NowTrackHasVideo()
        {
            var r = Playback.Current.Peek();
            return r.Kind == EntityKind.Track && new Track(r.Slot).HasVideo;
        }

        public void OpenVideoAt(Video.SurfacePlacement at, bool hasVideo) => PlayerVerbs.Default.SetVideoPlacement(at, hasVideo);

        public void CloseDockedVideo() => Video.State.ReportClosed(Video.SurfacePlacement.Docked);

        public void OpenStage(Action<string>? begin, string cause) => PlayerVerbs.Default.OpenStage(begin, cause);

        public void DemandNowPlaying(int trackSlot, int artistSlot)
        {
            if (trackSlot > 0) Entities.Ensure(new Track(trackSlot), TrackFields.Identity);
            if (artistSlot > 0) Entities.Ensure(new Artist(artistSlot), ArtistFields.Overview);
        }

        public void SetFriendsActive(bool active) => Rail.Friends.SetActive?.Invoke(active);
    }

    // ══ THE HOST ═════════════════════════════════════════════════════════════════════════════════════════════════════

    public sealed class PanelHost(PanelCells cells, PanelSizes sizes, IPanelEnv env) : IPanelVerbs
    {
        /// <summary>The shell's panel host. UI thread only.</summary>
        public static readonly PanelHost Shared = new(PanelCells.Frame, PanelSizes.Shared, new PanelEnv());

        public PanelCells Cells { get; } = cells;

        // Static doors for a splitter's commit callback (a static method group is one cached delegate).
        public static void CommitWidth() => Shared.ResizePanel();
        public static void CommitDockedVideo() => Shared.ResizeDockedVideo();

        // ── state ───────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The panel's state, with the subscriptions of a render. The docked video is read only on request:
        /// a reader that does not draw it must not re-render on every height drag.</summary>
        public PanelState State(bool withDockedVideo = false)
        {
            bool open = Cells.Open.Value;
            float width = Cells.Width.Value;
            var mode = Cells.Mode.Value;
            bool floats = !Cells.Fits.Value;
            var placement = Cells.Placement.Value;
            var docked = withDockedVideo
                ? new DockedVideoState(Cells.DockedVideoHeight.Value, Cells.DockedVideoPinned.Value)
                : default;
            return new PanelState(open, mode, width, floats, docked, placement);
        }

        /// <summary>The panes the window tier docks beside the primary (tracked, contract 1.29): none where it docks one.</summary>
        public Theming.PanelArrangement Panes()
        {
            var panes = Cells.Panes.Value;
            return Cells.SecondAllowed.Value ? panes : default;
        }

        /// <summary><see cref="Panes"/> without subscribing.</summary>
        public Theming.PanelArrangement PanesNow() => Cells.SecondAllowed.Peek() ? Cells.Panes.Peek() : default;

        /// <summary>Docked pane <paramref name="index"/> is on screen (tracked; 0 the second pane, 1 the third): see
        /// <see cref="FrameRules.PaneOpen"/>.</summary>
        public bool PaneOpen(int index)
        {
            var panes = Panes();
            return FrameRules.PaneOpen(in panes, index, Cells.QueueBeside.Value, Cells.Open.Value, Cells.Mode.Value);
        }

        /// <summary><see cref="PaneOpen"/> without subscribing.</summary>
        public bool PaneOpenNow(int index)
        {
            var panes = PanesNow();
            return FrameRules.PaneOpen(in panes, index, Cells.QueueBeside.Peek(), Cells.Open.Peek(), Cells.Mode.Peek());
        }

        /// <summary>A docked pane's width (tracked): the tier's floor, never under the panel default.</summary>
        public float PaneWidth() => FrameRules.SecondWidth(Cells.Floor.Value);

        /// <summary>The tab each docked pane's tab group shows (P6 WK, <c>DockedPane.Tabs</c>): a <c>PanelMode</c> value, or -1 for the pane's first tab.
        /// Session state: a fresh panel opens every pane on its first tab.</summary>
        readonly Signal<int>[] _paneTabs = [new(-1), new(-1)];

        /// <summary>The body docked pane <paramref name="index"/> shows (tracked): its tab group's pick, else its one body.</summary>
        public Theming.Contracts.PanelMode PaneBody(int index, Theming.DockedPane pane)
            => (uint)index < (uint)_paneTabs.Length ? pane.BodyFor(_paneTabs[index].Value) : pane.Mode;

        /// <summary><see cref="PaneBody"/> without subscribing.</summary>
        public Theming.Contracts.PanelMode PaneBodyNow(int index, Theming.DockedPane pane)
            => (uint)index < (uint)_paneTabs.Length ? pane.BodyFor(_paneTabs[index].Peek()) : pane.Mode;

        /// <summary>A docked pane's tab click (P6 WK): show <paramref name="mode"/> in pane <paramref name="index"/> where its tab group offers it.</summary>
        public void ShowPaneTab(int index, Theming.Contracts.PanelMode mode)
        {
            if ((uint)index >= (uint)_paneTabs.Length) return;
            if (PanesNow().At(index) is not { } pane || !pane.Bodies.Contains(mode)) return;
            _paneTabs[index].Value = (int)mode;
        }

        /// <summary>The second docked pane is on screen (tracked): Classic's queue beside the primary at a tier that docks two while the
        /// primary is not already showing it; a linked arrangement's second pane while the panel is open.</summary>
        public bool SecondOpen() => PaneOpen(0);

        /// <summary><see cref="SecondOpen"/> without subscribing.</summary>
        public bool SecondOpenNow() => PaneOpenNow(0);

        /// <summary>The second pane's width (tracked): the tier's floor.</summary>
        public float SecondWidth() => PaneWidth();

        // ── the persisted chrome state the frame's first layout must already have ──────────────────────────────────

        /// <summary>Seed the sizes from the store (no startup animation: runs before the root component exists).</summary>
        public void SeedSizes()
        {
            Cells.Width.Value = sizes.LoadWidth();
            Cells.DragWidth.Value = Cells.Width.Peek();
            Cells.DockedVideoHeight.Value = sizes.LoadDockedVideoHeight(Cells.Width.Peek());
        }

        /// <summary>Restore the session's open state, mode and the queue beside the panel. A mode outside the vocabulary (a newer
        /// build's) is ignored.</summary>
        public void RestoreSession(bool open, int railMode, bool queueBeside = false)
        {
            if (railMode < (int)RailMode.Lyrics || railMode > (int)RailMode.Video) return;
            Cells.Open.Value = open;
            Cells.Mode.Value = (RailMode)railMode;
            Cells.QueueBeside.Value = queueBeside;
        }

        // ── the verbs ───────────────────────────────────────────────────────────────────────────────────────────────

        public void SetMode(RailMode mode)
        {
            Cells.Mode.Value = mode;
            Cells.Open.Value = true;
        }

        /// <summary>The mode's door. Where ONE panel shows at a time: the mode on show closes it (and forgets the queue beside it),
        /// any other switches to it. Where TWO dock (D3), the queue lives in the second pane: its door opens or closes that pane (or
        /// the primary, while the primary itself shows the queue), and switching the primary away from the queue moves the queue into
        /// the second pane instead of closing it. Where the panes are LINKED (contract 1.29, Zune, WinUI Huge), they open and close with the
        /// panel: a door whose body is shown by choice (the primary's mode, or a docked pane's body) closes the panel; a door whose body the
        /// primary shows only as the fallback of its step aside makes it the primary's mode (the Huge queue then lyrics path ends with both
        /// panes docked, as Classic's does); any other shows its body in the primary.</summary>
        public void Toggle(RailMode mode)
        {
            bool open = Cells.Open.Peek();
            var current = Cells.Mode.Peek();
            var panes = PanesNow();
            if (panes.Count == 0)
            {
                if (open && current == mode) { Cells.Open.Value = false; Cells.QueueBeside.Value = false; return; }
                SetMode(mode);
                return;
            }
            if (panes.Linked)
            {
                var asked = (Theming.Contracts.PanelMode)(byte)mode;
                // A docked pane with a tab group of its own (P6 WK) that offers the body but shows another: the door picks that tab (and opens the panel).
                int holder = panes.IndexHolding(asked);
                if (holder >= 0 && panes.At(holder) is { HasTabs: true } tabbed && PaneBodyNow(holder, tabbed) != asked)
                {
                    _paneTabs[holder].Value = (int)asked;
                    Cells.Open.Value = true;
                    return;
                }
                // Shown by choice: a docked pane's body, or the primary's own mode. A body the primary shows only as the arrangement's FALLBACK
                // (the queue asked for, its pane holding it, the primary stepped aside to lyrics) was never picked: its door adopts it as the
                // primary's mode (both panes stay docked, the door lights) instead of closing the panel the user just opened.
                bool chosen = panes.Holds(asked) || current == mode;
                if (open && chosen) { Cells.Open.Value = false; return; }
                SetMode(mode);
                return;
            }
            // An independent pane: Classic's queue (the policy holds an independent pane to the queue).
            if (mode == RailMode.Queue)
            {
                if (open && current == RailMode.Queue) { Cells.Open.Value = false; Cells.QueueBeside.Value = false; return; }
                if (SecondOpenNow()) { Cells.QueueBeside.Value = false; return; }
                if (open) { Cells.QueueBeside.Value = true; return; }
                SetMode(RailMode.Queue);
                return;
            }
            if (open && current == mode) { Cells.Open.Value = false; return; }
            if (open && current == RailMode.Queue) Cells.QueueBeside.Value = true;   // the queue moves beside, it does not close
            SetMode(mode);
        }

        public void Close()
        {
            Cells.Open.Value = false;
            if (!Cells.SecondAllowed.Peek()) Cells.QueueBeside.Value = false;
        }

        public void CloseQueuePane()
        {
            if (PanesNow().Linked) { Close(); return; }   // a linked pane closes with the panel
            Cells.QueueBeside.Value = false;
        }

        /// <summary>A crossing to a tier that docks ONE panel while only the queue's second pane was open: the queue becomes the panel,
        /// so the crossing never hides what the user had open. The same where the new tier LINKS its panes (a theme switch from Classic's
        /// lone queue pane): the panel opens on the queue, and a linked arrangement shows it in its pane. Nothing to do otherwise.</summary>
        public void FoldSecondIntoPrimary()
        {
            if (!Cells.QueueBeside.Peek()) return;
            bool linked = PanesNow().Linked;
            if (Cells.SecondAllowed.Peek() && !linked) return;
            if (!Cells.Open.Peek())
            {
                Cells.Mode.Value = RailMode.Queue;
                Cells.Open.Value = true;
            }
            else if (!linked) return;
            Cells.QueueBeside.Value = false;
        }

        /// <summary>The window tier's floor moved (a crossing into or out of Huge, or a Huge window resized): the committed width is the
        /// stored preference brought up to <paramref name="floor"/> (never persisted), so every reader of the width (the reservation, the
        /// docked video, the seam) sees the width on screen. The floor is honoured above the policy's own bounds
        /// (<see cref="FrameRules.FloorWidth"/>: Zune's 340-only panel docks at 360 at Huge).</summary>
        public void FitWidthToFloor(float floor)
        {
            float target = MathF.Max(sizes.LoadWidth(), FrameRules.FloorWidth(floor));
            if (Cells.Width.Peek() != target) Cells.Width.Value = target;
            if (Cells.DragWidth.Peek() != target) Cells.DragWidth.Value = target;
        }

        public void ResizePanel()
        {
            // A drag that went past the minimum closed the panel: it commits nothing. The width the user left the panel at stays the
            // committed (and stored) one, so it reopens there, not at the minimum the release clamped the preview to.
            if (!Cells.Open.Peek()) return;
            if (FrameRules.RailDragCommit(Cells.DragWidth.Peek(), Cells.Width.Peek(), Cells.Floor.Peek()) is not { } w) return;
            Cells.Width.Value = w;
            Cells.DragWidth.Value = w;
            sizes.SaveWidth(w);
        }

        /// <summary>The panel's bounds changed (a theme switch gave it another policy): bring the committed width, and the width
        /// a seam preview parks at, inside <paramref name="policy"/>. Nothing is persisted: the stored preference stays what the
        /// user chose, and the next launch loads it through the bounds in force then. A width already inside the bounds is
        /// untouched, so the call is free to run on every policy publication. The window tier's floor still holds (a docked panel never
        /// presents under it, whichever of the two publications lands first).</summary>
        public void FitWidthTo(Theming.PanelPolicy policy)
        {
            float committed = Cells.Width.Peek();
            float fitted = MathF.Max(policy.ClampWidth(committed), FrameRules.FloorWidth(Cells.Floor.Peek()));
            if (fitted != committed) Cells.Width.Value = fitted;
            if (Cells.DragWidth.Peek() != fitted) Cells.DragWidth.Value = fitted;
        }

        public void ResizeDockedVideo()
        {
            float h = ClampDockedVideoHeight(Cells.DockedVideoHeight.Peek(), Cells.Width.Peek(), Cells.BodyHeight.Peek());
            Cells.DockedVideoHeight.Value = h;
            Cells.DockedVideoPinned.Value = true;
            sizes.SaveDockedVideoHeight(h);
        }

        public void ShowVideoAt(Video.SurfacePlacement at) => env.OpenVideoAt(at, env.NowTrackHasVideo());

        public void HideVideo() => env.CloseDockedVideo();

        public void OpenStage(Action<string>? begin, string cause) => env.OpenStage(begin, cause);

        // ── the host's demands (not user verbs) ─────────────────────────────────────────────────────────────────────

        public void DemandNowPlaying(int trackSlot, int artistSlot) => env.DemandNowPlaying(trackSlot, artistSlot);

        public void SetFriendsActive(bool active) => env.SetFriendsActive(active);
    }
}
