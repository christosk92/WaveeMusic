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
    /// <see cref="PanelCells.DragWidth"/> and commits on release). <see cref="Floats"/> is true when the panel does not fit
    /// inline and overlays the content instead.</summary>
    public readonly record struct PanelState(bool Open, RailMode Mode, float Width, bool Floats, DockedVideoState DockedVideo);

    /// <summary>The panel's cells. The shell's are <see cref="Ui"/>'s own signals (<see cref="Frame"/>).</summary>
    public sealed class PanelCells(Signal<bool> open, Signal<RailMode> mode, FloatSignal width, FloatSignal dragWidth,
        FloatSignal dockedVideoHeight, Signal<bool> dockedVideoPinned, FloatSignal bodyHeight, Signal<bool> fits)
    {
        public Signal<bool> Open { get; } = open;
        public Signal<RailMode> Mode { get; } = mode;
        public FloatSignal Width { get; } = width;
        public FloatSignal DragWidth { get; } = dragWidth;
        public FloatSignal DockedVideoHeight { get; } = dockedVideoHeight;
        public Signal<bool> DockedVideoPinned { get; } = dockedVideoPinned;
        public FloatSignal BodyHeight { get; } = bodyHeight;
        public Signal<bool> Fits { get; } = fits;

        /// <summary>The shell's panel: the very signals every reader binds.</summary>
        public static readonly PanelCells Frame = new(Ui.RailOpen, Ui.Mode, Ui.RailWidth, Ui.RailDragWidth,
            Ui.DockedVideoHeight, Ui.DockedVideoHeightPinned, Ui.RailBodyHeight, Ui.RailFits);

        /// <summary>A private set at <see cref="Ui"/>'s defaults (closed, Lyrics, 340 wide, fits).</summary>
        public static PanelCells Fresh() => new(new Signal<bool>(false), new Signal<RailMode>(RailMode.Lyrics),
            new FloatSignal(RailDefaultW), new FloatSignal(RailDefaultW), new FloatSignal(DockedVideoNaturalH(RailDefaultW)),
            new Signal<bool>(false), new FloatSignal(0f), new Signal<bool>(true));
    }

    // ══ THE PERSISTED SIZES ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The two sizes the panel persists.</summary>
    public enum PanelSize : byte { RailWidth, DockedVideoHeight }

    /// <summary>Where a size lives. Production is the settings store; the per-theme state keys of the themes plan (M12)
    /// replace it without touching a caller.</summary>
    public interface IPanelSizeStore
    {
        float Get(PanelSize size);
        void Set(PanelSize size, float value);
    }

    sealed class SettingsPanelSizeStore : IPanelSizeStore
    {
        public float Get(PanelSize size) => size == PanelSize.RailWidth
            ? Platform.Settings.Get(Platform.Keys.ShellRailWidth)
            : Platform.Settings.Get(Platform.Keys.ShellDockedVideoHeight);

        public void Set(PanelSize size, float value)
        {
            if (size == PanelSize.RailWidth) Platform.Settings.Set(Platform.Keys.ShellRailWidth, value);
            else Platform.Settings.Set(Platform.Keys.ShellDockedVideoHeight, value);
        }
    }

    /// <summary>The panel's persisted sizes behind one service: what to START at (clamped, so a stale or hand-edited value
    /// never escapes the panel's bounds) and where a committed size goes.</summary>
    public sealed class PanelSizes(IPanelSizeStore store)
    {
        /// <summary>The settings-backed sizes the shell uses.</summary>
        public static readonly PanelSizes Shared = new(new SettingsPanelSizeStore());

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

        /// <summary>Close the panel, whatever it shows (a docked video is demoted by the coupling).</summary>
        void Close();

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
            var docked = withDockedVideo
                ? new DockedVideoState(Cells.DockedVideoHeight.Value, Cells.DockedVideoPinned.Value)
                : default;
            return new PanelState(open, mode, width, floats, docked);
        }

        // ── the persisted chrome state the frame's first layout must already have ──────────────────────────────────

        /// <summary>Seed the sizes from the store (no startup animation: runs before the root component exists).</summary>
        public void SeedSizes()
        {
            Cells.Width.Value = sizes.LoadWidth();
            Cells.DragWidth.Value = Cells.Width.Peek();
            Cells.DockedVideoHeight.Value = sizes.LoadDockedVideoHeight(Cells.Width.Peek());
        }

        /// <summary>Restore the session's open state and mode. A mode outside the vocabulary (a newer build's) is ignored.</summary>
        public void RestoreSession(bool open, int railMode)
        {
            if (railMode < (int)RailMode.Lyrics || railMode > (int)RailMode.Video) return;
            Cells.Open.Value = open;
            Cells.Mode.Value = (RailMode)railMode;
        }

        // ── the verbs ───────────────────────────────────────────────────────────────────────────────────────────────

        public void SetMode(RailMode mode)
        {
            Cells.Mode.Value = mode;
            Cells.Open.Value = true;
        }

        public void Toggle(RailMode mode)
        {
            if (Cells.Open.Peek() && Cells.Mode.Peek() == mode) { Cells.Open.Value = false; return; }
            SetMode(mode);
        }

        public void Close() => Cells.Open.Value = false;

        public void ResizePanel()
        {
            if (FrameRules.RailDragCommit(Cells.DragWidth.Peek(), Cells.Width.Peek()) is not { } w) return;
            Cells.Width.Value = w;
            Cells.DragWidth.Value = w;
            sizes.SaveWidth(w);
        }

        /// <summary>The panel's bounds changed (a theme switch gave it another policy): bring the committed width, and the width
        /// a seam preview parks at, inside <paramref name="policy"/>. Nothing is persisted: the stored preference stays what the
        /// user chose, and the next launch loads it through the bounds in force then. A width already inside the bounds is
        /// untouched, so the call is free to run on every policy publication.</summary>
        public void FitWidthTo(Theming.PanelPolicy policy)
        {
            float committed = Cells.Width.Peek();
            float fitted = policy.ClampWidth(committed);
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
