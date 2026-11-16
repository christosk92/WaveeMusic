// ── Home/Board/HomeBoardStore.cs: the live Home document, its file and its undo ring (design 1.2.2) ─────────────────
//
// File: home.acct-<hash>.json beside the sidebar's account file, the same hash (SidebarAccountKey); signed out:
// home.acct-pending.json. IO is SidebarFileStore (.tmp → File.Replace → .bak, 300 ms debounce, 256 KB cap).
// Faults: a corrupt file → .bak, else set aside as .corrupt and the default. A file with version > 3 is kept untouched
// and READ-ONLY: Home renders the default and the store never writes (spec 6.3). An account switch flushes, loads the
// other account's document and clears the ring.

using System.Collections.Immutable;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;

namespace Wavee;

public sealed class HomeBoardStore
{
    public static HomeBoardStore Instance => s_instance ??= new HomeBoardStore();
    static HomeBoardStore? s_instance;

    /// <summary>App.cs, after Sidebar.Boot (the folder and the sidebar's pins are known there).</summary>
    public static void Boot()
    {
        Booted = true;
        var s = Instance;
        s._loaded = false;
        s.EnsureAccount(Entities.Current.Key);
    }

    /// <summary>True once <see cref="Boot"/> ran: the sidebar's account edge forwards to Home only from then on.</summary>
    public static bool Booted { get; private set; }

    /// <summary>Provided at the shell root beside HomeLayout.Slot.</summary>
    public static readonly Context<HomeBoardStore> Slot = new(null!);

    readonly Signal<HomeDoc> _doc = new(HomeDocs.Default());
    readonly Signal<HomeUndoState> _undoState = new(HomeUndoState.None);
    readonly Signal<int> _pinsVersion = new(0);
    readonly HomeUndoRing _ring = new();
    SidebarFileStore? _file;
    string _key = "";
    bool _loaded;

    // batch: an open-tile visit, a drag-in
    HomeDoc? _batchBefore;
    string _batchLabel = "";
    string? _batchAt;
    int _batchDepth;

    public IReadSignal<HomeDoc> Doc => _doc;

    /// <summary>A too-new file: Home shows the default and nothing is ever written.</summary>
    public bool ReadOnly { get; private set; }

    /// <summary>What the last load found: "ok", "missing", "migrated", "corrupt", "bak", "toonew", "unreadable".</summary>
    public string LoadOutcome { get; private set; } = "";

    /// <summary>The file this account's document lives in ("" before the first load).</summary>
    public string FilePath => _file?.FilePath ?? "";

    /// <summary>The board as projected right now, set by the board host (HomeBoardHost). Commands address entries of
    /// it; without a mounted board the document's own projection (no answer) stands in.</summary>
    public Func<BoardModel>? Shown { get; set; }

    /// <summary>The source registry the reducer checks sizes against.</summary>
    public HomeSourceRegistry Sources { get; set; } = HomeSourceRegistry.Default;

    /// <summary>The wall clock the reducer stamps (Unix ms).</summary>
    public Func<long> NowMs { get; set; } = static () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>Raised after every change of the document (a command, undo, redo, a load), on the UI thread.</summary>
    public event Action<HomeApplied>? Changed;

    /// <summary>The board as shown: the mounted board's model, else the document over the answer on hand.</summary>
    public BoardModel ShownNow() => Shown?.Invoke() ?? HomeProjection.Live(_doc.Peek(), Sources);

    // ══ commands ════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>One undo entry (or coalesced by tag: keyboard steps of one item use "step:&lt;id&gt;").</summary>
    public HomeApplied Dispatch(HomeCommand command, string? tag = null)
    {
        // A too-new file is read-only (spec 6.3): Home shows the default and nothing changes, so nothing seems saved that is not.
        if (ReadOnly) return new HomeApplied(false, null, null, "home.board.readOnly");
        var before = _doc.Peek();
        var after = HomeCommands.Apply(before, command, ShownNow(), Sources, NowMs(), out var applied);
        if (!applied.Changed) return applied;
        Publish(after);
        if (_batchDepth > 0)
        {
            _batchAt = _batchAt is null ? applied.GroupId ?? "page" : _batchAt == (applied.GroupId ?? "page") ? _batchAt : "page";
        }
        else
        {
            _ring.Push(new HomeUndoEntry(before, after, applied.LabelKey, applied.GroupId ?? "page"), tag, NowMs());
            PublishUndo();
        }
        Changed?.Invoke(applied);
        return applied;
    }

    /// <summary>Opens a batch: every command until <see cref="EndBatch"/> is ONE undo step (an open-tile visit, a drag-in).
    /// Nested calls fold into the outer batch.</summary>
    public void BeginBatch(string labelKey)
    {
        if (_batchDepth++ > 0) return;
        _batchBefore = _doc.Peek();
        _batchLabel = labelKey;
        _batchAt = null;
    }

    /// <summary>Closes the batch: one entry if anything changed.</summary>
    public void EndBatch()
    {
        if (_batchDepth == 0) return;
        if (--_batchDepth > 0) return;
        var before = _batchBefore;
        _batchBefore = null;
        if (before is null) return;
        var after = _doc.Peek();
        if (ReferenceEquals(before, after)) return;
        _ring.Push(new HomeUndoEntry(before, after, _batchLabel, _batchAt ?? "page"), null, NowMs());
        PublishUndo();
    }

    public bool InBatch => _batchDepth > 0;

    public bool Undo()
    {
        if (_batchDepth > 0) EndBatch();
        if (!_ring.TryUndo(out var e)) return false;
        Publish(e.Before with { Rev = _doc.Peek().Rev + 1 });
        PublishUndo();
        Changed?.Invoke(new HomeApplied(true, null, e.At == "page" ? null : e.At, e.LabelKey));
        return true;
    }

    public bool Redo()
    {
        if (_batchDepth > 0) EndBatch();
        if (!_ring.TryRedo(out var e)) return false;
        Publish(e.After with { Rev = _doc.Peek().Rev + 1 });
        PublishUndo();
        Changed?.Invoke(new HomeApplied(true, null, e.At == "page" ? null : e.At, e.LabelKey));
        return true;
    }

    /// <summary>CanUndo, CanRedo, LabelKey, At (group id or "page"), Version.</summary>
    public IReadSignal<HomeUndoState> UndoState => _undoState;

    /// <summary>Undo steps held (the inspector's 50 cap check).</summary>
    public int UndoDepth => _ring.UndoCount;
    public int RedoDepth => _ring.RedoCount;

    void PublishUndo()
    {
        var top = _ring.Top;
        _undoState.Value = new HomeUndoState(_ring.CanUndo, _ring.CanRedo, top?.LabelKey ?? "", top?.At ?? "page",
            _undoState.Peek().Version + 1);
    }

    void Publish(HomeDoc doc)
    {
        var old = _doc.Peek();
        _doc.Value = doc;
        if (PinsChanged(old, doc)) _pinsVersion.Value = _pinsVersion.Peek() + 1;
        if (!ReadOnly) _file?.Commit(HomeDocWire.Write(doc));
    }

    /// <summary>Replace the document outright (migration's first answer, the inspector's homedoc=): no undo entry.</summary>
    public void ReplaceSilently(HomeDoc doc)
    {
        Publish(doc);
        Changed?.Invoke(new HomeApplied(true, null, null, ""));
    }

    // ══ pins ════════════════════════════════════════════════════════════════════════════════════════════════════════

    public bool IsPinned(string pinId)
    {
        _ = _pinsVersion.Value;
        return HomeCommands.FindPin(_doc.Peek(), pinId).Group >= 0;
    }

    /// <summary>Moves when the set or the order of pins changes (menus, the band strip, a card's pin mark read it).</summary>
    public IReadSignal<int> PinsVersion => _pinsVersion;

    /// <summary>The user's pins of the Pinned group, in order (Zune's band strip, 1.6).</summary>
    public IReadOnlyList<PinSource> PinnedSources(int max)
    {
        var doc = _doc.Peek();
        int g = doc.GroupIndex(HomeDocs.Pinned);
        var list = new List<PinSource>();
        if (g < 0) return list;
        foreach (var it in doc.Groups[g].Items)
        {
            if (list.Count >= max) break;
            if (it.Source is PinSource p) list.Add(p);
        }
        return list;
    }

    /// <summary>Zune's band strip (1.6): the user's pins of the Pinned group, in order, as band pins.</summary>
    public IReadOnlyList<BandPin> BandPins(int max) => HomePins.BandPinsOf(PinnedSources(max));

    static bool PinsChanged(HomeDoc a, HomeDoc b)
    {
        var pa = new List<string>();
        var pb = new List<string>();
        foreach (var g in a.Groups) foreach (var it in g.Items) if (it.Source is PinSource p) pa.Add(g.Id + "\u0001" + p.PinId);
        foreach (var g in b.Groups) foreach (var it in g.Items) if (it.Source is PinSource p) pb.Add(g.Id + "\u0001" + p.PinId);
        return !pa.SequenceEqual(pb);
    }

    // ══ the file ════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The folder: the profile root's WaveeMusic when a profile is set, else the sidebar's own folder (the
    /// per-process demo folder under --fake, %LOCALAPPDATA%\Wavee\WaveeMusic otherwise).</summary>
    public static string Folder => Platform.ProfileRoot.Length > 0 ? Path.Combine(Platform.LocalFolder, "WaveeMusic") : Sidebar.ProfileDir;

    public static string FileNameOf(string accountKey)
        => SidebarAccountKey.IsSignedOut(accountKey) ? "home.acct-pending.json" : "home.acct-" + SidebarAccountKey.Hash(accountKey) + ".json";

    /// <summary>The account edge: idempotent; a new account flushes the old file, loads its own and clears the ring.</summary>
    public void EnsureAccount(in CatalogScope scope)
    {
        string key = SidebarAccountKey.Of(scope);
        if (_loaded && string.Equals(key, _key, StringComparison.Ordinal)) return;
        _file?.FlushNow();
        _key = key;
        _loaded = true;
        Load(Path.Combine(Folder, FileNameOf(key)));
    }

    /// <summary>(Re)load from a path (the inspector's homedoc= writes a file and calls this).</summary>
    public void Load(string path)
    {
        _file?.FlushNow();
        _file?.WaitForWrites(2000);
        _ring.Clear();
        _batchDepth = 0; _batchBefore = null;
        _file = new SidebarFileStore(path, "home");
        ReadOnly = false;
        HomeDoc doc;
        var read = _file.Read();
        switch (read.Outcome)
        {
            case SidebarFileReadOutcome.Missing:
                doc = HomeMigration.Run(Path.GetDirectoryName(path) ?? Folder, Sidebar.BandPinSeeds(HomeMigration.MaxSeedPins));
                LoadOutcome = "migrated";
                _file.Commit(HomeDocWire.Write(doc));
                break;
            case SidebarFileReadOutcome.Ok:
            {
                var r = HomeDocWire.Read(read.Bytes!);
                if (r.Fault == HomeReadFault.None) { doc = r.Doc; LoadOutcome = "ok"; break; }
                if (r.Fault == HomeReadFault.TooNew)
                {
                    doc = HomeDocs.Default(); ReadOnly = true; LoadOutcome = "toonew";
                    Log.Warn("home", "home.board.file_too_new version=" + r.FileVersion + " (read-only)");
                    break;
                }
                var bak = _file.ReadBak();
                var rb = bak.Outcome == SidebarFileReadOutcome.Ok ? HomeDocWire.Read(bak.Bytes!) : default;
                if (bak.Outcome == SidebarFileReadOutcome.Ok && rb.Fault == HomeReadFault.None)
                {
                    doc = rb.Doc; LoadOutcome = "bak";
                    _file.MarkCorrupt();
                    _file.Commit(HomeDocWire.Write(doc));
                }
                else
                {
                    _file.MarkCorrupt();
                    doc = HomeDocs.Default(); LoadOutcome = "corrupt";
                    Log.Warn("home", "home.board.file_corrupt (set aside, default)");
                }
                break;
            }
            default:
                doc = HomeDocs.Default(); ReadOnly = true; LoadOutcome = "unreadable";
                break;
        }
        var old = _doc.Peek();
        _doc.Value = doc;
        if (PinsChanged(old, doc)) _pinsVersion.Value = _pinsVersion.Peek() + 1;
        PublishUndo();
        Log.Info("home", "home.board.loaded outcome=" + LoadOutcome + " items=" + doc.ItemCount);
        Changed?.Invoke(new HomeApplied(true, null, null, ""));
    }

    public void FlushNow() => _file?.FlushNow();

    /// <summary>Write what is pending now and wait for it (before a process exits, before a file is replaced under the store).</summary>
    public void Quiesce()
    {
        _file?.FlushNow();
        _file?.WaitForWrites(3000);
    }

    /// <summary>Block until the pending write landed (shutdown, the inspector between processes).</summary>
    public bool WaitForWrites(int timeoutMs = 5000) => _file?.WaitForWrites(timeoutMs) ?? true;

    /// <summary>The first answer arrived: resolve v2 hidden zones into hidden families (migration step 3), once.</summary>
    public void ResolveLegacy(IReadOnlyList<HomeUi.SectionInput> sections)
    {
        var doc = _doc.Peek();
        if (doc.LegacyHidden.IsDefaultOrEmpty || sections.Count == 0 || ReadOnly) return;
        Publish(HomeMigration.ResolveLegacy(doc, sections, NowMs()));
    }
}
