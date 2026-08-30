using System;
using System.Collections;
using System.Collections.Generic;
using FluentGpu.Signals;

namespace Wavee;

/// <summary>
/// The ONE pin list, shared by all three sidebar designs (locked decision 4: unlimited, no cap, no eviction). Owned by
/// <c>SidebarPreferences</c> and reached as <c>prefs.Pins</c>; persisted inside <c>sidebar-layout.json</c>.
///
/// Identity is the pin <c>Id</c> (Ordinal) — the stable scheme in <c>SidebarPinId</c>, which for every navigable kind IS
/// the nav route key. A pin therefore survives a library refresh, a rename, and an offline launch: <c>Name</c>/<c>Uri</c>
/// are only a display cache refreshed through <see cref="Touch"/>.
///
/// Implements <see cref="IReadOnlyList{T}"/> so a caller can index/enumerate it directly (<c>prefs.Pins[i]</c>,
/// <c>prefs.Pins.Count</c>) while the mutators stay on the store; <see cref="GetEnumerator"/> hands back a STRUCT
/// enumerator so the pinned-section render path does not allocate one per frame.
///
/// ENGINE-FREE apart from <c>Signal&lt;int&gt;</c> (the VirtualCollection precedent: the test assembly's
/// <c>VirtualCollectionSignalShim</c> supplies that one type), so <c>SidebarPinStoreTests</c> drives the real store.
///
/// THREADING: UI thread only, unsynchronized — the same discipline as the rest of <c>SidebarPreferences</c>.
/// </summary>
public sealed class SidebarPinStore : IReadOnlyList<SidebarPin>
{
    readonly List<SidebarPin> _items = new();
    readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);   // id → position in _items
    readonly Signal<int> _version = new(0);

    /// <summary>Raised after every accepted mutation, so the owner can persist. Set once by <c>SidebarPreferences</c>;
    /// the store itself knows nothing about the document.</summary>
    public Action? OnChanged;

    /// <summary>Bumped on every accepted mutation — the render dep for every Pinned section and rail band.</summary>
    public IReadSignal<int> Version => _version;

    /// <summary>The ordered pin list. This IS the render order of every Pinned section, and the leading band of the
    /// entry projection (pins sort before everything else in every sort mode).</summary>
    public IReadOnlyList<SidebarPin> Items => this;

    public int Count => _items.Count;
    public SidebarPin this[int i] => _items[i];

    public bool IsPinned(string? pinId) => IndexOf(pinId) >= 0;

    /// <summary>Position of a pin, or -1. Ordinal identity, plus the raw-uri alias a card drop used to persist
    /// (<c>spotify:playlist:…</c> vs <c>pl:spotify:playlist:…</c>) so a menu looking up the canonical id still finds
    /// the row.</summary>
    public int IndexOf(string? pinId)
    {
        if (string.IsNullOrEmpty(pinId)) return -1;
        if (_index.TryGetValue(pinId, out int i)) return i;
        string? canon = SidebarPinId.Canonical(pinId);
        if (canon is not null && _index.TryGetValue(canon, out i)) return i;
        string alias = SidebarPinId.LegacyUriAlias(canon ?? pinId);
        return alias.Length > 0 && _index.TryGetValue(alias, out i) ? i : -1;
    }

    /// <summary>Append a pin. Returns false when already pinned (idempotent — the menu shows Unpin in that state) and
    /// keeps the original position, so a double invoke can never reorder the list. UNLIMITED by decision 4.
    /// The id is canonicalized on the way in so a raw entity uri and a prefixed pin id cannot coexist.</summary>
    public bool Pin(SidebarPin pin)
    {
        var stored = Canonicalize(pin);
        if (string.IsNullOrEmpty(stored.Id) || IndexOf(stored.Id) >= 0) return false;
        _index[stored.Id] = _items.Count;
        _items.Add(stored);
        Bump();
        return true;
    }

    /// <summary>Insert at a position (the undo path for <see cref="Unpin"/>: restore at the FORMER index). The index is
    /// CLAMPED to <c>[0, Count]</c> rather than throwing — an undo that arrives after other pins were removed must still
    /// land somewhere sane. Returns false when already pinned.</summary>
    public bool Insert(SidebarPin pin, int index)
    {
        var stored = Canonicalize(pin);
        if (string.IsNullOrEmpty(stored.Id) || IndexOf(stored.Id) >= 0) return false;
        int at = index < 0 ? 0 : index > _items.Count ? _items.Count : index;
        _items.Insert(at, stored);
        Reindex(at);
        Bump();
        return true;
    }

    /// <summary>Remove by id. Returns the index it occupied (for the undo toast) or -1 when absent.</summary>
    public int Unpin(string? pinId)
    {
        int at = IndexOf(pinId);
        if (at < 0) return -1;
        string storedId = _items[at].Id;
        _items.RemoveAt(at);
        _index.Remove(storedId);
        Reindex(at);
        Bump();
        return at;
    }

    /// <summary>Reorder within the list (a drag/keyboard drop). Both indices are clamped; <c>to == Count</c> means "move
    /// to the end". A no-op move neither bumps the version nor persists.</summary>
    public void Move(int fromIndex, int toIndex)
    {
        int n = _items.Count;
        if (n < 2 || (uint)fromIndex >= (uint)n) return;
        int to = toIndex < 0 ? 0 : toIndex >= n ? n - 1 : toIndex;
        if (to == fromIndex) return;
        var moved = _items[fromIndex];
        _items.RemoveAt(fromIndex);
        _items.Insert(to, moved);
        Reindex(Math.Min(fromIndex, to));
        Bump();
    }

    /// <summary>Refresh a pin's cached display name (a renamed playlist) from live library data. Returns true when it
    /// actually changed. Deliberately does NOT bump the version or raise <see cref="OnChanged"/>: a cache refresh must
    /// never commit on its own (commit point #2 folds it into the next real commit) and must never invalidate a render
    /// mid-projection. Called by the projection, never by rows.</summary>
    public bool Touch(string? pinId, string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        int at = IndexOf(pinId);
        if (at < 0) return false;
        var cur = _items[at];
        if (string.Equals(cur.Name, name, StringComparison.Ordinal)) return false;
        _items[at] = cur with { Name = name };
        return true;
    }

    /// <summary>Replace the whole list from the loaded document (startup only). Skips null/empty and duplicate ids so a
    /// hand-edited file can never produce two rows with one identity. Silent — no <see cref="OnChanged"/>.</summary>
    public void LoadFrom(IReadOnlyList<SidebarPin>? pins)
    {
        _items.Clear();
        _index.Clear();
        if (pins is not null)
            for (int i = 0; i < pins.Count; i++)
            {
                var p = Canonicalize(pins[i]);
                if (string.IsNullOrEmpty(p.Id) || _index.ContainsKey(p.Id)) continue;
                _index[p.Id] = _items.Count;
                _items.Add(p);
            }
        _version.Value = _version.Peek() + 1;
    }

    static SidebarPin Canonicalize(SidebarPin pin)
    {
        string? id = SidebarPinId.Canonical(pin.Id);
        if (id is null || string.Equals(id, pin.Id, StringComparison.Ordinal)) return pin;
        string uri = pin.Uri.Length > 0 ? pin.Uri : SidebarPinId.UriOf(id);
        return pin with { Id = id, Uri = uri };
    }

    void Reindex(int from)
    {
        for (int i = from; i < _items.Count; i++) _index[_items[i].Id] = i;
    }

    void Bump()
    {
        _version.Value = _version.Peek() + 1;
        OnChanged?.Invoke();
    }

    public Enumerator GetEnumerator() => new(_items);
    IEnumerator<SidebarPin> IEnumerable<SidebarPin>.GetEnumerator() => _items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();

    /// <summary>Allocation-free <c>foreach</c> over the pins (the pinned section renders per pin, per render).</summary>
    public struct Enumerator
    {
        readonly List<SidebarPin> _list;
        int _i;
        internal Enumerator(List<SidebarPin> list) { _list = list; _i = -1; }
        public SidebarPin Current => _list[_i];
        public bool MoveNext() => ++_i < _list.Count;
    }
}
