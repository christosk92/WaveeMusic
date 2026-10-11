// ── Shell/RootScope.cs — the root-lifetime half of the shell's state ───────────────────────────────────────────────────
//
// Role: CORE (themes plan §5.6, owner Q26; PR C17). A theme switch UNMOUNTS the root component and MOUNTS a new one, and the
// new tree must behave like a fresh navigation: nothing in it may point at a node, an overlay handle, a component instance or
// a page pivot of the frozen tree. State is therefore split in two lifetimes:
//
//   PROCESS   everything that was a static before and stays one: navigation (`Shell.Nav`, tabs, history), the session
//             document, the omnibar's typed text, playback, entities, settings, sessions, the core models (StageModel,
//             LyricsModel, Video, WizardModel, NavModel, PanelHost). A root remount does not touch them.
//   ROOT      what only the MOUNTED root can truth-tell about: the frame's context captures and node handles, the tab strip
//             instance, the edit pane, the overlay handles a page opened (menus, the sign-in door, the wizard, the tip, the
//             video-override flyout), the overlay hosts the pages published, the settings page's pivot, the omnibar popup's
//             measured memory. Each is a `RootState<T>` declared next to its user; its value lives in the CURRENT `RootScope`.
//
// THE SCOPE. `Shell.FrameRoot` calls `RootScope.Begin()` on its first render and `RootScope.End(...)` from its unmount
// effect. Between the two, `RootScope.Current` is the mounted root's scope; outside them (before the first mount, between an
// unmount and the next mount, in any process that never mounts a root, e.g. the unit tests) `Current` is an AMBIENT scope: it
// always exists, so a deep link or a test can write root state before the root does, and the next `Begin` ADOPTS it. A
// mounted scope is never adopted twice: a second `Begin` while one is still mounted (a remount that renders before the old
// root's cleanup ran) retires the old one first. Retiring runs the scope's dispose callbacks and installs a FRESH ambient
// scope as `Current`, so a closure of the old tree that writes late lands in the new scope's cell (harmless: every such
// write is a null or a ReferenceEquals-guarded clear) and never keeps a node of the old tree reachable.
//
// With one mount the behaviour is the one the statics had: one scope, adopted at the first render, never retired.
//
// UI THREAD ONLY, like every handle it holds. `Current` is volatile only so a reader on another thread sees a whole object.

using System;
using System.Collections.Generic;
using System.Threading;

namespace Wavee;

/// <summary>The root-lifetime state of one mounted shell root (see the file header). <see cref="Current"/> is never null.</summary>
public sealed class RootScope
{
    static volatile RootScope s_current = new();
    static int s_mounts;

    readonly object _gate = new();
    RootCell?[] _cells = new RootCell?[16];
    List<Action>? _onDispose;
    bool _mounted;
    bool _disposed;

    RootScope() { }

    /// <summary>The scope root state reads and writes: the mounted root's, else the ambient one.</summary>
    public static RootScope Current => s_current;

    /// <summary>How many roots have mounted in this process (the first mount is 1). A scope's <see cref="Generation"/> is the
    /// value this had when the scope was adopted; an ambient scope that no root adopted yet is 0.</summary>
    public static int Mounts => Volatile.Read(ref s_mounts);

    /// <summary>The mount ordinal this scope was adopted at; 0 while it is still ambient.</summary>
    public int Generation { get; private set; }

    /// <summary>A root adopted this scope and has not ended it.</summary>
    public bool IsMounted => _mounted && !_disposed;

    /// <summary>The scope was retired: its state is released and nothing reads it through <see cref="Current"/> any more.</summary>
    public bool IsDisposed => _disposed;

    // ── the lifecycle ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A root mounts: adopt the ambient scope, or retire a still-mounted one first (a remount that rendered before the
    /// old root's cleanup). Returns the scope the root owns; hand it back to <see cref="End"/> when the root unmounts.</summary>
    public static RootScope Begin()
    {
        var scope = s_current;
        if (scope._mounted)
        {
            Retire(scope);
            scope = s_current;
        }
        scope._mounted = true;
        scope.Generation = Interlocked.Increment(ref s_mounts);
        return scope;
    }

    /// <summary>The root that owns <paramref name="scope"/> unmounts: run its dispose callbacks, drop its state and make a fresh
    /// ambient scope current. A scope that was already retired (a newer <see cref="Begin"/> took it) is a no-op.</summary>
    public static void End(RootScope? scope)
    {
        if (scope is null || scope._disposed) return;
        Retire(scope);
    }

    /// <summary>Retire whatever root is mounted now, if any (the headless host calls it after it disposed its engine host, so a
    /// test never leaves a mounted scope behind even when the tree's own cleanup did not run).</summary>
    public static void EndMounted()
    {
        var scope = s_current;
        if (scope._mounted) Retire(scope);
    }

    static void Retire(RootScope scope)
    {
        scope._disposed = true;
        var callbacks = scope._onDispose;
        scope._onDispose = null;
        if (callbacks is not null)
        {
            for (int i = callbacks.Count - 1; i >= 0; i--)
            {
                try { callbacks[i](); }
                catch (Exception ex) { Log.Warn("shell", "a root-scope dispose callback threw", ex); }
            }
        }
        lock (scope._gate) scope._cells = [];
        if (ReferenceEquals(s_current, scope)) s_current = new RootScope();
    }

    /// <summary>Run <paramref name="action"/> when this scope retires (last registered runs first). For the process-lifetime
    /// facts that mirror a root-lifetime handle and must be put back when the handle goes (a tip that was on screen). An
    /// already-retired scope runs it at once.</summary>
    public void OnDispose(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (_disposed) { action(); return; }
        (_onDispose ??= new List<Action>(2)).Add(action);
    }

    // ── the cells ────────────────────────────────────────────────────────────────────────────────────────────────────

    internal RootCell<T> Cell<T>(RootState<T> state)
    {
        int id = state.Id;
        var cells = _cells;
        if ((uint)id < (uint)cells.Length && cells[id] is RootCell<T> hit) return hit;   // the hot path: no lock
        lock (_gate)
        {
            if (_disposed) return state.NewCell();   // a read through a retired scope: a throwaway cell, never stored
            cells = _cells;
            if (id >= cells.Length)
            {
                Array.Resize(ref cells, Math.Max(id + 1, cells.Length * 2));
                _cells = cells;
            }
            var existing = cells[id] as RootCell<T>;
            if (existing is not null) return existing;
            var created = state.NewCell();
            cells[id] = created;
            return created;
        }
    }

    /// <summary>True when this scope's cell for <paramref name="state"/> was ever created (touched by a read or a write).</summary>
    public bool Has(RootState state) => (uint)state.Id < (uint)_cells.Length && _cells[state.Id] is not null;

    /// <summary>The value this scope holds for <paramref name="state"/> (null when it never created one). Diagnostics and tests.</summary>
    public object? Peek(RootState state)
        => (uint)state.Id < (uint)_cells.Length ? _cells[state.Id]?.Boxed : null;
}

/// <summary>One slot of root-lifetime state, declared as a <c>static readonly</c> next to its user. The slot itself is a process
/// constant (an index and a name); the VALUE it names lives in <see cref="RootScope.Current"/>, so a new root starts with a
/// freshly created value and the old root's value is unreachable from it.</summary>
public abstract class RootState
{
    static readonly List<RootState> s_all = new();

    private protected RootState(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        Name = name;
        lock (s_all)
        {
            Id = s_all.Count;
            s_all.Add(this);
        }
    }

    internal int Id { get; }

    /// <summary>The slot's diagnostic name (<c>Shell.Hooks</c>): unique by convention, never parsed.</summary>
    public string Name { get; }

    /// <summary>Every slot declared so far in this process (a static initializer declares each slot the first time its owner is
    /// touched, so a test that wants all of them touches the owners first, or reads what a mounted scope <see cref="RootScope.Has"/>).</summary>
    public static IReadOnlyList<RootState> All
    {
        get { lock (s_all) return s_all.ToArray(); }
    }
}

/// <summary>A typed <see cref="RootState"/>. <see cref="Value"/> reads and writes the current scope's cell, creating it with the
/// slot's factory (or <c>default</c>) on first touch.</summary>
public sealed class RootState<T> : RootState
{
    readonly Func<T>? _create;

    /// <summary>Declare a slot. <paramref name="create"/> builds the value a new root starts with (a signal at its default); null
    /// starts at <c>default(T)</c> (a handle that is not mounted yet).</summary>
    public RootState(string name, Func<T>? create = null) : base(name) => _create = create;

    /// <summary>The current scope's value.</summary>
    public T Value
    {
        get => RootScope.Current.Cell(this).Value;
        set => RootScope.Current.Cell(this).Value = value;
    }

    /// <summary><paramref name="scope"/>'s value, creating it when the scope never touched it.</summary>
    public T In(RootScope scope) => scope.Cell(this).Value;

    internal RootCell<T> NewCell() => new() { Value = _create is null ? default! : _create() };
}

/// <summary>A scope's storage for one slot (a class, so a handle written in place boxes nothing).</summary>
internal abstract class RootCell
{
    internal abstract object? Boxed { get; }
}

internal sealed class RootCell<T> : RootCell
{
    internal T Value = default!;
    internal override object? Boxed => Value;
}
