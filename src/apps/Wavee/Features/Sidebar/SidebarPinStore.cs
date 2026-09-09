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

    /// <summary>Raised for a USER-INTENT membership change only (<see cref="Pin"/> / <see cref="Insert"/> /
    /// <see cref="Unpin"/>). Never by <see cref="ApplyRemote"/>, <see cref="LoadFrom"/>, <see cref="Move"/> or
    /// <see cref="Touch"/> — that asymmetry is what keeps a server-originated change from echoing back as a write.
    /// Set by <c>SidebarPinSync</c> (App/SidebarPinSync.cs); the store itself knows nothing about Spotify's ylpin set.
    /// The bool is the target state: true = pinned (Pin/Insert), false = unpinned (Unpin).</summary>
    public Action<SidebarPin, bool>? OnLocalPinChanged;

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
        OnLocalPinChanged?.Invoke(stored, true);
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
        OnLocalPinChanged?.Invoke(stored, true);
        return true;
    }

    /// <summary>Remove by id. Returns the index it occupied (for the undo toast) or -1 when absent.</summary>
    public int Unpin(string? pinId)
    {
        int at = IndexOf(pinId);
        if (at < 0) return -1;
        var removed = _items[at];
        _items.RemoveAt(at);
        _index.Remove(removed.Id);
        Reindex(at);
        Bump();
        OnLocalPinChanged?.Invoke(removed, false);
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
    /// never invalidate a render mid-projection (the owner writes through <c>SidebarLayoutStore.Commit</c> instead).
    /// Called by the projection, never by rows.
    ///
    /// <para>The liked-songs route pin never carries a name — its title is <c>ShellNav.Dest("liked").Title</c>. A
    /// non-empty cache here is always wrong (and was how a <c>--fake</c> session persisted a blank/fake title).</para></summary>
    public bool Touch(string? pinId, string? name)
    {
        int at = IndexOf(pinId);
        if (at < 0) return false;
        var cur = _items[at];
        if (IsLikedRoute(cur.Id))
        {
            if (cur.Name.Length == 0) return false;
            _items[at] = cur with { Name = "" };
            return true;
        }
        if (string.IsNullOrEmpty(name)) return false;
        if (string.Equals(cur.Name, name, StringComparison.Ordinal)) return false;
        _items[at] = cur with { Name = name };
        return true;
    }

    /// <summary>Replace the whole list from the loaded document (startup only). Skips null/empty and duplicate ids so a
    /// hand-edited file can never produce two rows with one identity. Silent — no <see cref="OnChanged"/>.
    ///
    /// <para>W7: also drops a pin whose id names a RETIRED route (<see cref="SidebarPinId.IsRetiredRoute"/>) —
    /// today that means a pin id of exactly <c>"local"</c>, left behind by the retired Local Files page. The test is the
    /// explicit retired list, never "not pinnable": a bare id the store has never heard of is kept (rule 9). This is a
    /// deliberate retirement PRUNE, not the general "missing entity renders disabled" rule (iron rule 9): an AppRoute
    /// pin has no entity to resolve later and no menu offers it back, so keeping it around would only paint a dead row
    /// forever. Entity pins (playlist/album/artist/show/folder/track) are never touched here.</para></summary>
    public void LoadFrom(IReadOnlyList<SidebarPin>? pins)
    {
        _items.Clear();
        _index.Clear();
        if (pins is not null)
            for (int i = 0; i < pins.Count; i++)
            {
                var p = Canonicalize(pins[i]);
                if (string.IsNullOrEmpty(p.Id) || _index.ContainsKey(p.Id)) continue;
                if (SidebarPinId.IsRetiredRoute(p.Id)) continue;
                _index[p.Id] = _items.Count;
                _items.Add(p);
            }
        _version.Value = _version.Peek() + 1;
    }

    /// <summary>Converge the SYNCABLE pins onto the server's membership without touching order, local-only pins, or the
    /// local-change event. <paramref name="serverPins"/> is the server set already mapped to pin ids (+ display cache);
    /// <paramref name="isSyncable"/> decides which local pins are eligible for removal at all. Adds are APPENDED in the
    /// given order (the caller sorts by added_at ascending, so a batch of remote pins lands oldest-first); removals only
    /// happen when <paramref name="removeMissing"/> is true — the caller gates that on "the server set has actually
    /// converged once" (see <c>SidebarPinSync</c>). Returns true when anything changed; commits (<see cref="OnChanged"/>)
    /// exactly once, and never raises <see cref="OnLocalPinChanged"/> — this is a server-originated change.</summary>
    public bool ApplyRemote(IReadOnlyList<SidebarPin> serverPins, Func<string, bool> isSyncable, bool removeMissing)
    {
        bool changed = false;
        var keep = new HashSet<string>(serverPins.Count, StringComparer.Ordinal);
        for (int i = 0; i < serverPins.Count; i++)
        {
            var p = Canonicalize(serverPins[i]);
            if (string.IsNullOrEmpty(p.Id)) continue;
            keep.Add(p.Id);
            // Required-change B: a remote (ylpin) pin always arrives with Name == "" (App/SidebarPinSync.cs never
            // knows the entity's display name, only its id/uri) — an ALREADY-PRESENT pin is skipped entirely rather
            // than replaced, so a name this store resolved via Touch (from the pin's own header query) can never be
            // stomped back to "" by a later remote sync of the very same pin.
            if (IndexOf(p.Id) >= 0) continue;
            _index[p.Id] = _items.Count;
            _items.Add(p);
            changed = true;
        }
        if (removeMissing)
            for (int i = _items.Count - 1; i >= 0; i--)
            {
                var id = _items[i].Id;
                if (!isSyncable(id) || keep.Contains(id)) continue;
                _items.RemoveAt(i);
                _index.Remove(id);
                Reindex(i);
                changed = true;
            }
        if (changed) Bump();          // version + OnChanged (persist) — deliberately NOT OnLocalPinChanged
        return changed;
    }

    static bool IsLikedRoute(string? pinId)
        => string.Equals(pinId, "liked", StringComparison.Ordinal)
           || string.Equals(SidebarPinId.Canonical(pinId), "liked", StringComparison.Ordinal);

    static SidebarPin Canonicalize(SidebarPin pin)
    {
        string? id = SidebarPinId.Canonical(pin.Id);
        if (id is null) return pin;
        string uri = string.Equals(id, pin.Id, StringComparison.Ordinal)
            ? pin.Uri
            : (pin.Uri.Length > 0 ? pin.Uri : SidebarPinId.UriOf(id));
        // Liked Songs title is ShellNav.Dest("liked").Title — never a paint-before-data cache.
        string name = string.Equals(id, "liked", StringComparison.Ordinal) ? "" : pin.Name;
        if (string.Equals(id, pin.Id, StringComparison.Ordinal)
            && string.Equals(uri, pin.Uri, StringComparison.Ordinal)
            && string.Equals(name, pin.Name, StringComparison.Ordinal))
            return pin;
        return pin with { Id = id, Uri = uri, Name = name };
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
