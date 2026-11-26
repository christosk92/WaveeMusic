// ── Entities/Mix.Editor.Session.cs — #48 Mix, which pair is being edited and where ────────────────────────────────────────────
//
// The transition editor is opened from a pair's chip in the list (Entities/Mix.Seams.cs) and lives in the right panel ("Edit transition",
// the panel's Transition body), or full-page after its expand button (the `transition:` route, MixEditorPage). ONE session: the pair, the
// size, and the draft with its undo and redo. The draft lives here, not in the host, so expanding or collapsing keeps every unsaved edit
// (the panel's host and the page's host are two mounts of one editor).
//
//   Edit(pl, i)   opens the panel on the pair (another pair: a fresh draft); the chip shows it is open
//   Expand        the page takes the editor (the panel goes back to what it showed); Collapse returns it to the panel
//   Close         drops the draft, stops a preview, gives the panel back (or leaves the page)
//
// The panel's other doors (a Queue or Lyrics toggle) take the panel from the editor: the body's unmount ends the session (MixEditorPanelBody).

using FluentGpu.Signals;

namespace Wavee;

public static class MixEditSession
{
    /// <summary>The pair being edited: the playlist slot (0: none) and the pair's index (playlist row i → i + 1).</summary>
    public readonly record struct Target(int Playlist, int Index)
    {
        public bool IsNone => Playlist <= 0;
    }

    /// <summary>The open pair (tracked).</summary>
    public static readonly Signal<Target> Open = new(default);

    /// <summary>The editor is full-page (the `transition:` route) rather than in the panel (tracked).</summary>
    public static readonly Signal<bool> Expanded = new(false);

    // ── the draft, shared by the panel's host and the page's ──

    /// <summary>The edited draft (null: the pair's own state).</summary>
    internal static readonly Signal<MixDraft?> Edited = new(null);
    /// <summary>Bumped on every undo-stack change (the host re-renders on it).</summary>
    internal static readonly Signal<int> History = new(0);
    internal static readonly List<MixDraft> Undo = [], Redo = [];
    /// <summary>Save's prompt is up (a list the user can't edit); a Duplicate Mix is out.</summary>
    internal static readonly Signal<bool> Prompt = new(false), Duplicating = new(false);
    /// <summary>The numbers while a point is dragged (null: the draft's own).</summary>
    internal static readonly Signal<Theming.Contracts.MixReadout?> LiveReadout = new(null);

    /// <summary>What the panel showed before the editor took it, given back on close.</summary>
    static Shell.RailMode s_prevMode = Shell.RailMode.Queue;
    static bool s_prevOpen;

    /// <summary>The editor is on screen for <paramref name="playlistSlot"/>'s pair <paramref name="index"/> (tracked): the chip's selected face.</summary>
    public static bool Shows(int playlistSlot, int index)
    {
        var t = Open.Value;
        if (t.Playlist != playlistSlot || t.Index != index) return false;
        return Expanded.Value || Shell.Ui.RailOpen.Value && Shell.Ui.Mode.Value == Shell.RailMode.Transition;
    }

    /// <summary>Open the editor on a pair, in the panel (or the page, while expanded). The same pair again keeps its draft.</summary>
    public static void Edit(int playlistSlot, int index)
    {
        if (playlistSlot <= 0 || index < 0) return;
        var target = new Target(playlistSlot, index);
        if (Open.Peek() != target)
        {
            Forget();
            MixHearIt.StopIfPlaying();
            Open.Value = target;
        }
        if (Expanded.Peek()) return;
        var host = Shell.PanelHost.Shared;
        var mode = host.Cells.Mode.Peek();
        if (mode != Shell.RailMode.Transition)
        {
            s_prevMode = mode;
            s_prevOpen = host.Cells.Open.Peek();
        }
        host.SetMode(Shell.RailMode.Transition);
    }

    /// <summary>The expand button: the full page while in the panel, the panel while full-page.</summary>
    public static void ToggleExpand()
    {
        var t = Open.Peek();
        if (t.IsNone) return;
        if (!Expanded.Peek())
        {
            var p = new Playlist(t.Playlist);
            if (!p.IsValid) return;
            Expanded.Value = true;
            GiveBackPanel();
            Shell.GoTo(new Shell.Route(Shell.RouteKind.MixTransition, p.Uri));
            return;
        }
        // Back into the panel: the page leaves (the history's step back to the playlist), the panel opens on the editor.
        Expanded.Value = false;
        Edit(t.Playlist, t.Index);
        LeavePage();
    }

    /// <summary>Close the editor: the draft is dropped, a preview stops, the panel shows what it showed before (or the page goes back).</summary>
    public static void Close()
    {
        if (Open.Peek().IsNone) return;
        bool expanded = Expanded.Peek();
        End();
        if (expanded) LeavePage();
        else GiveBackPanel();
    }

    /// <summary>The editor left the screen some other way (the panel switched to another body, the page was navigated away from): the session
    /// ends, the panel stays as the user left it.</summary>
    internal static void Ended()
    {
        if (Open.Peek().IsNone) return;
        End();
    }

    static void End()
    {
        MixHearIt.StopIfPlaying();
        Forget();
        Expanded.Value = false;
        Open.Value = default;
    }

    /// <summary>The draft is spent (saved, reset, cancelled, another pair): the pair's own state shows again.</summary>
    internal static void Forget()
    {
        Undo.Clear();
        Redo.Clear();
        Prompt.Value = false;
        Duplicating.Value = false;
        LiveReadout.Value = null;
        Edited.Value = null;
        History.Value = History.Peek() + 1;
    }

    /// <summary>The panel goes back to the body it showed before the editor took it (closed when it was closed).</summary>
    static void GiveBackPanel()
    {
        var host = Shell.PanelHost.Shared;
        if (host.Cells.Mode.Peek() != Shell.RailMode.Transition) return;
        if (s_prevOpen) host.SetMode(s_prevMode);
        else
        {
            host.Close();
            host.Cells.Mode.Value = s_prevMode;
        }
    }

    /// <summary>The full page leaves: back to the page it came from when that is in the history, else the playlist.</summary>
    static void LeavePage()
    {
        if (Shell.Current.Peek().Kind != Shell.RouteKind.MixTransition) return;
        Shell.GoBack();
    }

    /// <summary>A restored session never reopens the editor: the panel's persisted body is what the editor replaced.</summary>
    public static Shell.RailMode Persisted(Shell.RailMode mode) => mode == Shell.RailMode.Transition ? s_prevMode : mode;
}
