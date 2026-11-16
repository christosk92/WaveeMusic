// ── Home/Board/Hosts/HomeBoardVerbs.cs — every flow of the board ends here (pinned-home-tech.md 1.5, spec 8.0) ──────────────────
//
// Role: CORE. One document command per verb through `HomeBoardStore.Dispatch` (one undo entry, or coalesced by its tag), then the theme's
// confirmation (`ThemePolicies.HomeConfirm`: an info-bar toast with Undo for WinUI and Classic, nothing for Zune) and one announcement. The
// open tile is a batch: `Open` begins it, `Close` ends it, and on a close that changed something the toast says "{name} updated".
//
// Themes never mutate anything; they call these. The inspector's `home do=` verbs call the same object.

using FluentGpu.Controls;
using FluentGpu.Input;
using FluentGpu.Localization;
using Wavee.Theming.Contracts;

namespace Wavee;

/// <summary>The board's verbs.</summary>
public sealed class HomeBoardVerbs : IHomeBoardVerbs
{
    readonly HomeBoardView _view;
    readonly PackGridController _grid;
    readonly Func<BoardModel> _shown;
    long _openRevAtStart = -1;

    internal HomeBoardVerbs(HomeBoardView view, PackGridController grid, Func<BoardModel> shown)
    {
        _view = view;
        _grid = grid;
        _shown = shown;
    }

    /// <summary>The overlay the menus open on (set by the host).</summary>
    internal IOverlayService? Overlay { get; set; }

    static HomeBoardStore Store => HomeBoardStore.Instance;

    BoardEntry? Entry(string id)
    {
        var entries = _shown().Entries;
        for (int i = 0; i < entries.Length; i++) if (entries[i].Id == id) return entries[i];
        return null;
    }

    string NameOf(string id) => Entry(id)?.Title ?? "";

    /// <inheritdoc/>
    public void Pin(string pinId, string name, string cover, string? groupId = null)
    {
        var applied = Store.Dispatch(new HomeCommand.Pin(pinId, name, cover, groupId));
        if (!applied.Changed) return;
        Confirm(Strings.Home.BoardVerbs.Pinned(name: name));
        if (applied.ItemId is { } id) Ring(id);
    }

    /// <inheritdoc/>
    public void Unpin(string pinId)
    {
        string name = HomePinVerbs.NameOf(pinId);   // read before the pin leaves the document
        var applied = Store.Dispatch(new HomeCommand.Unpin(pinId));
        if (applied.Changed) Confirm(name.Length > 0 ? Strings.Home.Board.Pin.Unpinned(name) : Loc.Get(Strings.Home.BoardVerbs.Unpinned));
    }

    /// <inheritdoc/>
    public void Add(HomeCandidate candidate, int atEntryIndex = -1, HomeSize size = HomeSize.Auto)
    {
        HomeApplied applied;
        var (group, at) = PlaceOf(atEntryIndex, candidate.TargetGroupId);
        if (candidate.Source is PinSource pin && at < 0)
            applied = Store.Dispatch(new HomeCommand.Pin(pin.PinId, candidate.Title, candidate.Cover ?? "", group));
        else
            applied = Store.Dispatch(new HomeCommand.Add(candidate.Source, group, at, size));
        if (!applied.Changed) return;
        Confirm(Strings.Home.BoardVerbs.Pinned(name: candidate.Title));
        if (applied.ItemId is { } id) Ring(id);
    }

    /// <summary>An entry index of the board as shown -> the group it is in and the place in that group's document items (the entry there moves
    /// down; a group head: the group's start). -1: the group's natural place.</summary>
    (string Group, int At) PlaceOf(int entryIndex, string fallbackGroup)
    {
        var entries = _shown().Entries;
        if ((uint)entryIndex >= (uint)entries.Length) return (fallbackGroup, -1);
        var target = entries[entryIndex];
        if (target.Kind == BoardEntryKind.GroupHead) return (target.GroupId, 0);
        var doc = Store.Doc.Peek();
        foreach (var g in doc.Groups)
        {
            if (g.Id != target.GroupId) continue;
            for (int i = 0; i < g.Items.Length; i++) if (g.Items[i].Id == target.Id) return (g.Id, i);
            return (g.Id, -1);
        }
        return (fallbackGroup, -1);
    }

    /// <inheritdoc/>
    public void Remove(string entryId)
    {
        string name = NameOf(entryId);
        CloseIfOpen(entryId);
        var applied = Store.Dispatch(new HomeCommand.Remove(entryId));
        if (!applied.Changed) return;
        Confirm(Strings.Home.BoardVerbs.Removed(name: name));
    }

    /// <inheritdoc/>
    public void SetSize(string entryId, HomeSize size)
    {
        var applied = Store.Dispatch(new HomeCommand.SetSize(entryId, size));
        if (applied.Changed) Say(Strings.Home.BoardVerbs.Resized(name: NameOf(applied.ItemId ?? entryId), size: SizeWord(size)));
    }

    /// <inheritdoc/>
    public void CycleSize(string entryId)
    {
        var applied = Store.Dispatch(new HomeCommand.CycleSize(entryId));
        if (applied.Changed && Entry(applied.ItemId ?? entryId) is { } e)
            Say(Strings.Home.BoardVerbs.Resized(name: e.Title, size: SizeWord(e.Size)));
    }

    /// <inheritdoc/>
    public void MoveOnto(string entryId, string targetEntryId) => Moved(Store.Dispatch(new HomeCommand.MoveOnto(entryId, targetEntryId)), entryId);

    /// <inheritdoc/>
    public void ReorderShortcut(string entryId, int toIndex)
        => Moved(Store.Dispatch(new HomeCommand.ReorderShortcut(entryId, toIndex)), entryId);

    /// <inheritdoc/>
    public void MoveBy(string entryId, int delta)
        => Moved(Store.Dispatch(new HomeCommand.MoveBy(entryId, delta), tag: "step:" + entryId), entryId);

    /// <inheritdoc/>
    public void MoveToEdge(string entryId, bool toStart) => Moved(Store.Dispatch(new HomeCommand.MoveToEdge(entryId, toStart)), entryId);

    /// <inheritdoc/>
    public void Commit(PackMove move)
    {
        var entries = _shown().Entries;
        if ((uint)move.To >= (uint)entries.Length || move.To == move.From) return;
        MoveOnto(move.Key, entries[move.To].Id);
    }

    void Moved(HomeApplied applied, string entryId)
    {
        if (!applied.Changed) return;
        string id = applied.ItemId ?? entryId;
        Say(Strings.Home.BoardVerbs.Moved(name: NameOf(id), where: Where(id)));
    }

    /// <inheritdoc/>
    public void Open(string entryId)
    {
        if (_view.OpenId.Peek() is { } open && open != entryId) Close(refocus: false);
        if (_view.OpenId.Peek() == entryId) return;
        Store.BeginBatch("home.board.undo.options");
        _openRevAtStart = Store.Doc.Peek().Rev;
        _view.OpenId.Value = entryId;
        Say(Strings.Home.BoardVerbs.Customizing(name: NameOf(entryId)));
    }

    /// <inheritdoc/>
    public void Close(bool refocus)
    {
        if (_view.OpenId.Peek() is not { } id) return;
        string name = NameOf(id);
        _view.OpenId.Value = null;
        Store.EndBatch();
        bool changed = _openRevAtStart >= 0 && Store.Doc.Peek().Rev != _openRevAtStart;
        _openRevAtStart = -1;
        if (changed) Confirm(Strings.Home.BoardVerbs.Updated(name: name));
        if (refocus) _grid.Focus(HomeBoardKeys.Tile(id));
    }

    void CloseIfOpen(string entryId)
    {
        if (_view.OpenId.Peek() == entryId) Close(refocus: false);
    }

    /// <inheritdoc/>
    public void SetOption(string key, string value)
    {
        if (_view.OpenId.Peek() is not { } id) return;
        Store.Dispatch(new HomeCommand.SetOptions(id, System.Collections.Immutable.ImmutableSortedDictionary.Create<string, string>(StringComparer.Ordinal).Add(key, value)));
    }

    /// <inheritdoc/>
    public void ToggleLive(string entryId)
    {
        var e = Entry(entryId);
        if (e is null) return;
        bool paused = !e.Paused;
        Store.Dispatch(new HomeCommand.SetOptions(entryId,
            System.Collections.Immutable.ImmutableSortedDictionary.Create<string, string>(StringComparer.Ordinal).Add("live", paused ? "0" : "1")));
        Say(Loc.Get(paused ? Strings.Home.BoardVerbs.LivePaused : Strings.Home.BoardVerbs.LiveResumed));
    }

    /// <inheritdoc/>
    public void Undo()
    {
        Close(refocus: false);
        if (Store.Undo()) Say(Loc.Get(Strings.Home.BoardVerbs.Undone));
    }

    /// <inheritdoc/>
    public void Redo()
    {
        if (Store.Redo()) Say(Loc.Get(Strings.Home.BoardVerbs.Redone));
    }

    /// <inheritdoc/>
    public void Reset(HomeResetScope scope)
    {
        Close(refocus: false);
        var applied = Store.Dispatch(new HomeCommand.Reset(scope));
        if (applied.Changed) Confirm(Loc.Get(Strings.Home.BoardVerbs.Reset));
    }

    /// <inheritdoc/>
    public void Reveal(string entryId, bool ring = true)
    {
        _grid.BringIntoView(HomeBoardKeys.Tile(entryId));
        if (ring) Ring(entryId);
    }

    void Ring(string entryId)
    {
        _view.RingId.Value = entryId;
        _grid.BringIntoView(HomeBoardKeys.Tile(entryId));
    }

    /// <inheritdoc/>
    public void Say(string text)
    {
        if (Announcer.IsAvailable && text.Length > 0) Announcer.Say(text, assertive: false);
    }

    /// <inheritdoc/>
    public string Where(string entryId)
    {
        var entries = _shown().Entries;
        string group = "", groupTitle = "";
        int pos = 0, count = 0;
        for (int i = 0; i < entries.Length; i++)
        {
            var e = entries[i];
            if (e.Kind == BoardEntryKind.GroupHead) { if (pos > 0) break; group = e.GroupId; groupTitle = e.Title; count = 0; continue; }
            count++;
            if (e.Id == entryId) pos = count;
        }
        _ = group;
        return pos > 0 ? Strings.Home.BoardVerbs.Position(n: pos, count: count, group: groupTitle) : "";
    }

    /// <summary>The theme's confirmation: an info-bar toast with Undo (WinUI, Classic), else the announcement alone (Zune: the tile is the answer).</summary>
    void Confirm(string text)
    {
        if (ShellPolicy.Now.Home.Confirm == Theming.HomeConfirm.Toast)
            Notify.Say(text, InfoBarSeverity.Informational, Loc.Get(Strings.Home.BoardVerbs.UndoAction), Undo,
                       dedupeKey: "home.undo." + Store.Doc.Peek().Rev, durationMs: 7000f);
        else Say(text);
    }

    static string SizeWord(HomeSize size) => Loc.Get(size switch
    {
        HomeSize.S => Strings.Home.BoardVerbs.SizeS,
        HomeSize.M => Strings.Home.BoardVerbs.SizeM,
        HomeSize.L => Strings.Home.BoardVerbs.SizeL,
        HomeSize.Full => Strings.Home.BoardVerbs.SizeFull,
        _ => Strings.Home.BoardVerbs.SizeAuto,
    });
}
