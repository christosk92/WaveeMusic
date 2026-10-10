// ── Shell/Sidebar.Disclosures.cs ───────────────────────────────────────────────────────────────────────────────────────
// the sidebar's in-flight disclosures, by key: which section/folder is opening or closing right now, and the direction
// its chevron shows while it does
//
// Pure and engine-free so Wavee.Tests pins it (SidebarDisclosuresTests). fluent-gpu
// docs/plans/smooth-reveal-implementation.md §12: any number run at once, and a toggle of a key already in flight
// REVERSES it — never queued behind another, never snapped to its end.

using System;
using System.Collections.Generic;

namespace Wavee;

/// <summary>The bookkeeping behind PaneView's disclosure choreography: which section/folder keys are opening or closing
/// right now, and the direction a chevron shows while they do.</summary>
public sealed class SidebarDisclosures
{
    /// <summary>One disclosure in flight: its band key ("section:id" / "folder:id"), the section/folder id, and the
    /// direction it is heading.</summary>
    public readonly record struct Entry(string Key, string Id, bool Folder, bool Open);

    /// <summary>What a user toggle means: start a new disclosure, reverse the one in flight, or nothing (already heading there).</summary>
    public enum Toggle : byte { Begin, Reverse, Ignore }

    private readonly List<Entry> _entries = new(4);

    public int Count => _entries.Count;

    /// <summary>Record a toggle of <paramref name="key"/> toward <paramref name="open"/>.</summary>
    public Toggle Start(string key, string id, bool folder, bool open)
    {
        int i = IndexOf(key);
        if (i < 0)
        {
            _entries.Add(new Entry(key, id, folder, open));
            return Toggle.Begin;
        }
        if (_entries[i].Open == open) return Toggle.Ignore;
        _entries[i] = _entries[i] with { Open = open };
        return Toggle.Reverse;
    }

    /// <summary>The disclosure under <paramref name="key"/> came to rest: forget it. False for a key nothing runs under.</summary>
    public bool Settled(string key)
    {
        int i = IndexOf(key);
        if (i < 0) return false;
        _entries.RemoveAt(i);
        return true;
    }

    public bool TryGet(string key, out Entry entry)
    {
        int i = IndexOf(key);
        entry = i < 0 ? default : _entries[i];
        return i >= 0;
    }

    /// <summary>The direction a chevron shows: an in-flight disclosure's, else <paramref name="fallback"/> (the persisted state).</summary>
    public bool IsOpen(string id, bool folder, bool fallback)
    {
        for (int i = 0; i < _entries.Count; i++)
            if (_entries[i].Folder == folder && string.Equals(_entries[i].Id, id, StringComparison.Ordinal)) return _entries[i].Open;
        return fallback;
    }

    /// <summary>Is a FOLDER disclosure in flight? A folder's band range comes from the plan's entries, which the key-matched
    /// publish path does not walk, so it stands down while one runs.</summary>
    public bool AnyFolder()
    {
        for (int i = 0; i < _entries.Count; i++)
            if (_entries[i].Folder) return true;
        return false;
    }

    /// <summary>The ids of the SECTION disclosures in flight (a cold path: one publish of a toggle).</summary>
    public List<string> SectionIds()
    {
        var ids = new List<string>(_entries.Count);
        for (int i = 0; i < _entries.Count; i++)
            if (!_entries[i].Folder) ids.Add(_entries[i].Id);
        return ids;
    }

    private int IndexOf(string key)
    {
        for (int i = 0; i < _entries.Count; i++)
            if (string.Equals(_entries[i].Key, key, StringComparison.Ordinal)) return i;
        return -1;
    }
}
