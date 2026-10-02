// ── Entities/Profile.Lists.cs — the Following / Followers list pages: the pure decisions ───────────────────────────────
//
// Role: CORE (pure, engine-free — only Wavee values: EntityKind, EdgeState, LibraryLetters, JumpIndex, SurfaceGeometry)
// Owner: R2 (profile pages, issue #161)
// Spec: docs/plans/wavee/profile-pages-implementation.md Appendix R §4.1 + "Reconciliation, round 2"
//
// WHAT LIVES HERE. Everything the list page DECIDES, as values a fact can pin without an engine:
//   · ProfileListLoadRule  — which of six faces the page shows (Pending | Ready | Empty | Hidden | Unavailable | Failed), built on
//                            the landed ProfileLoadRule (User.Profile.Rules.cs): the header's verdict and the list's readiness.
//   · ProfileListFilter / ProfileListModel — the entries of one list, their display order (the library's a–z filing), the
//                            All / Artists / People chip and the find box, in pooled buffers (zero allocation once warm).
//   · ProfileGridFit / ProfileLetterRows — the card grid: how many columns fit, and the flat "letter header | row of N cards"
//                            projection the bound list scrolls over (the Recents / library pattern: PINNED extents, so the
//                            sticky letter and the jump strip never drift from what is painted).
// The UI half (Profile.Lists.Page.cs) reads these and paints; it decides nothing.

using System.Globalization;

namespace Wavee;

// ══ 1. THE LOAD RULE ═════════════════════════════════════════════════════════════════════════════════════════════════

/// <summary>What the list page shows. <see cref="Hidden"/> = the profile keeps its follows private; <see cref="Unavailable"/> =
/// the profile (or the list) is not there; <see cref="Empty"/> = a real, complete, empty list.</summary>
public enum ProfileListLoad : byte { Pending, Ready, Empty, Hidden, Unavailable, Failed }

/// <summary>The facts the load rule reads, as one value. <paramref name="Header"/> is the profile row's verdict
/// (<see cref="ProfileLoadRule.Header(User)"/>; <see cref="ProfileLoad.Unavailable"/> for an invalid user);
/// <paramref name="Readiness"/>/<paramref name="Failure"/> are the shelf's (<see cref="User.ProfileReadiness"/> and
/// <see cref="User.ProfileFailure"/>); <paramref name="Count"/> is how many entries the page can actually show.</summary>
public readonly record struct ProfileListFacts(ProfileLoad Header, bool FollowsKnown, bool ShowFollows, bool IsCurrentUser,
                                               EdgeState Readiness, int Failure, int Count);

public static class ProfileListLoadRule
{
    /// <summary>Unavailable &gt; Hidden &gt; rows ⇒ Ready (even while a refresh failed or is out) &gt; Failed &gt; Empty &gt; Pending.
    /// <list type="bullet">
    /// <item><b>Unavailable</b> — the profile row says so (the 404 seal, an invalid user), or the provider had no route for the list.</item>
    /// <item><b>Hidden</b> — the profile is KNOWN, it is not yours, and it does not show its follows. Wins over stale rows.</item>
    /// <item><b>Failed</b> — nothing to show and the list (or the profile row) terminally failed: Retry.</item>
    /// <item><b>Empty</b> — the list is Complete and has no rows, AND the profile row has answered: a private profile's list
    /// answers empty too, and "nobody" must never flash before the row says the follows are hidden.</item>
    /// <item><b>Pending</b> — everything else: Unknown, Partial with no rows yet, or the row still loading.</item>
    /// </list></summary>
    public static ProfileListLoad Of(in ProfileListFacts f)
    {
        var list = ProfileLoadRule.List(f.Readiness, f.Failure);
        if (f.Header == ProfileLoad.Unavailable || list == ProfileLoad.Unavailable) return ProfileListLoad.Unavailable;
        if (f.FollowsKnown && !f.ShowFollows && !f.IsCurrentUser) return ProfileListLoad.Hidden;
        if (f.Count > 0) return ProfileListLoad.Ready;
        if (list == ProfileLoad.Failed || f.Header == ProfileLoad.Failed) return ProfileListLoad.Failed;
        if (f.Readiness == EdgeState.Complete && f.Header == ProfileLoad.Ready) return ProfileListLoad.Empty;
        return ProfileListLoad.Pending;
    }
}

/// <summary>The two list facets as the data layer names them. <see cref="ProfileFacet"/> is the ROUTE's enum
/// (Profile.Route.cs) and its numeric values are the route key's digit — nothing here depends on them.</summary>
public static class ProfileListFacets
{
    /// <summary>The selector bar's order: index 0 Following, 1 Followers.</summary>
    public static readonly ProfileFacet[] Order = [ProfileFacet.Following, ProfileFacet.Followers];

    public static int IndexOf(ProfileFacet facet) => facet == ProfileFacet.Followers ? 1 : 0;
    public static ProfileFacet At(int index) => index == 1 ? ProfileFacet.Followers : ProfileFacet.Following;
    public static ProfileShelf ShelfOf(ProfileFacet facet) => facet == ProfileFacet.Followers ? ProfileShelf.Followers : ProfileShelf.Following;
    public static ProfileSurface SurfaceOf(ProfileFacet facet) => facet == ProfileFacet.Followers ? ProfileSurface.Followers : ProfileSurface.Following;
    /// <summary>Only Following mixes artists and people, so only it has the All / Artists / People chips.</summary>
    public static bool HasChips(ProfileFacet facet) => facet == ProfileFacet.Following;
}

// ══ 2. THE ENTRIES, THEIR ORDER, THE CHIP AND THE FIND BOX ═══════════════════════════════════════════════════════════

public enum ProfileChip : byte { All = 0, Artists = 1, People = 2 }

/// <summary>One entry of a list, resolved for display (engine-free: the page fills it off the tables). <see cref="Slot"/>
/// indexes the table <see cref="Kind"/> names (Artists or Users); <see cref="Followers"/> is the count the card states.</summary>
public readonly record struct ProfileEntry(EntityKind Kind, int Slot, string Name, int Followers)
{
    public bool IsArtist => Kind == EntityKind.Artist;
    public bool IsPerson => Kind == EntityKind.User;
    /// <summary>The a–z filing letter: 0 = '#', 1..26 = A..Z (<see cref="LibraryLetters.Of"/>: "the " is skipped).</summary>
    public int Letter => LibraryLetters.Of(Name);
}

public readonly record struct ProfileChipCounts(int All, int Artists, int People);

public static class ProfileListFilter
{
    public static ProfileChipCounts Counts(ReadOnlySpan<ProfileEntry> e)
    {
        int a = 0, p = 0;
        foreach (var x in e) { if (x.IsArtist) a++; else if (x.IsPerson) p++; }
        return new(e.Length, a, p);
    }

    /// <summary>THE display order, a total one: the library's a–z filing (<see cref="LibraryLetters.Of"/>: "the " skipped,
    /// anything outside A–Z is '#' and files FIRST), then the name (<c>OrdinalIgnoreCase</c>), then artists before people,
    /// then the slot. The letter leads because it is what the grouping bands by: a list ordered by name alone would open a
    /// band per flip.</summary>
    public static int Compare(in ProfileEntry a, in ProfileEntry b)
    {
        int c = a.Letter.CompareTo(b.Letter);
        if (c == 0) c = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        if (c == 0) c = (a.IsArtist ? 0 : 1).CompareTo(b.IsArtist ? 0 : 1);
        return c != 0 ? c : a.Slot.CompareTo(b.Slot);
    }

    /// <summary>Write the display order of <paramref name="entries"/>[0..count) into <paramref name="order"/>[0..count)
    /// (indices into <paramref name="entries"/>). One comparer allocation per call; the buffers are the caller's.</summary>
    public static void Order(ProfileEntry[] entries, int count, int[] order)
    {
        for (int i = 0; i < count; i++) order[i] = i;
        if (count > 1) Array.Sort(order, 0, count, new EntryOrder(entries));
    }

    sealed class EntryOrder(ProfileEntry[] entries) : IComparer<int>
    {
        public int Compare(int x, int y) => ProfileListFilter.Compare(in entries[x], in entries[y]);
    }

    /// <summary>The chip, then the trimmed query as a case-insensitive substring of the name. <paramref name="query"/>
    /// must already be trimmed (<see cref="ProfileListModel.Filter"/> does it once).</summary>
    public static bool Matches(in ProfileEntry e, ProfileChip chip, ReadOnlySpan<char> query)
    {
        if (chip == ProfileChip.Artists && !e.IsArtist) return false;
        if (chip == ProfileChip.People && !e.IsPerson) return false;
        return query.IsEmpty || e.Name.AsSpan().Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>"Word · 1,063", or the bare word while the count is unknown or 0 (the library's <c>ScopeWordText</c> rule).</summary>
    public static string WordCount(string word, int count, bool known, CultureInfo culture)
        => known && count > 0 ? word + " · " + count.ToString("N0", culture) : word;
}

/// <summary>One list's entries in POOLED buffers: <see cref="Begin"/> · <see cref="Add"/>… · <see cref="Seal"/> (sort + chip
/// counts) · <see cref="Filter"/> (the visible subset, in display order, with each visible entry's letter and artist bit).
/// Buffers grow and never shrink, so rebuilding per data landing or per settled keystroke allocates nothing once warm
/// (bar the sort's one comparer). The page owns ONE instance. UI thread.</summary>
public sealed class ProfileListModel
{
    ProfileEntry[] _entries = new ProfileEntry[64];
    int[] _order = new int[64];
    int[] _visible = new int[64];
    int[] _letters = new int[64];
    bool[] _artist = new bool[64];
    int _count, _visibleCount;

    public int Count => _count;
    public int VisibleCount => _visibleCount;
    public ProfileChipCounts Counts { get; private set; }
    public ReadOnlySpan<ProfileEntry> Entries => _entries.AsSpan(0, _count);
    /// <summary>The visible entries as indices into <see cref="Entries"/>, in display order.</summary>
    public ReadOnlySpan<int> Visible => _visible.AsSpan(0, _visibleCount);
    /// <summary>Per VISIBLE entry: its filing letter (non-decreasing) and whether it is an artist.</summary>
    public ReadOnlySpan<int> VisibleLetters => _letters.AsSpan(0, _visibleCount);
    public ReadOnlySpan<bool> VisibleArtists => _artist.AsSpan(0, _visibleCount);

    public ProfileEntry EntryAt(int entry) => _entries[entry];
    /// <summary>The <paramref name="k"/>-th VISIBLE entry.</summary>
    public ProfileEntry VisibleAt(int k) => _entries[_visible[k]];

    public void Begin() { _count = 0; _visibleCount = 0; }

    public void Add(in ProfileEntry e)
    {
        if (_count == _entries.Length) Array.Resize(ref _entries, _entries.Length * 2);
        _entries[_count++] = e;
    }

    public void Seal()
    {
        if (_order.Length < _count)
        {
            int size = Math.Max(_count, _order.Length * 2);
            _order = new int[size]; _visible = new int[size]; _letters = new int[size]; _artist = new bool[size];
        }
        Counts = ProfileListFilter.Counts(Entries);
        ProfileListFilter.Order(_entries, _count, _order);
        _visibleCount = 0;
    }

    /// <summary>The visible subset for <paramref name="chip"/> and <paramref name="query"/> (trimmed here), in display
    /// order. An empty query and <see cref="ProfileChip.All"/> keep everything.</summary>
    public void Filter(ProfileChip chip, ReadOnlySpan<char> query)
    {
        var q = query.Trim();
        int n = 0;
        for (int k = 0; k < _count; k++)
        {
            int i = _order[k];
            if (!ProfileListFilter.Matches(in _entries[i], chip, q)) continue;
            _visible[n] = i;
            _letters[n] = _entries[i].Letter;
            _artist[n] = _entries[i].IsArtist;
            n++;
        }
        _visibleCount = n;
    }
}

/// <summary>A person's display name when the row has none yet: the id after the last ':' of the uri
/// (<c>spotify:user:jane</c> → <c>jane</c>), so a card never paints a blank title.</summary>
public static class ProfileEntryName
{
    public static string OrId(string name, string uri)
    {
        if (name.Length > 0) return name;
        int colon = uri.LastIndexOf(':');
        return colon >= 0 && colon < uri.Length - 1 ? uri[(colon + 1)..] : uri;
    }
}

// ══ 3. THE GRID: COLUMNS, THE LETTER-GROUPED FLAT PROJECTION ═════════════════════════════════════════════════════════

/// <summary>The measured body → how many cards fit, how wide each is, whether the A–Z strip and the side-by-side tools fit.</summary>
public readonly record struct ProfileGridPlan(bool Strip, bool StackTools, int Columns, float CellWidth);

public static class ProfileGridFit
{
    public const float MinCell = 176f;                 // SectionScreen's SectionCardMinWidth
    public const float ActionLine = 32f + 8f;          // FollowToggle (32) + Spacing.S above it
    public const float StripMinWidth = 640f;
    public const float ToolsStackBelow = 640f;
    /// <summary>The A–Z strip's own width and the gap between it and the list (= <c>Spacing.S</c>).</summary>
    public const float StripWidth = 18f, StripGap = 8f;

    public static int Columns(float width, float gap) => width <= 0f ? 0 : Math.Max(1, (int)((width + gap) / (MinCell + gap)));
    public static float CellWidth(float width, int cols, float gap) => cols <= 0 ? 0f : MathF.Floor((width - (cols - 1) * gap) / cols);

    /// <summary>The card row's PINNED height — the surface's own grid estimate (≥ its rendered height), so the seed extent
    /// equals the measured one and the list never re-pins its scroll anchor.</summary>
    public static float CardRow(float cellW) => SurfaceGeometry.GridRowEstimate(cellW, Shape.Grid, hasSubtitle: true);

    public static bool ShowsStrip(float width) => width >= StripMinWidth;

    /// <summary>The plan for a body of <paramref name="bodyW"/> DIP (unmeasured = 0 → no columns, and the wide tools
    /// arrangement, the one most windows have). The strip takes <see cref="StripWidth"/> + <see cref="StripGap"/> off the
    /// list's width when it shows.</summary>
    public static ProfileGridPlan For(float bodyW, float cardGap)
    {
        bool strip = ShowsStrip(bodyW);
        float listW = bodyW - (strip ? StripWidth + StripGap : 0f);
        int cols = Columns(listW, cardGap);
        return new(strip, bodyW > 0f && bodyW < ToolsStackBelow, cols, CellWidth(listW, cols, cardGap));
    }
}

/// <summary>One flat item: a letter HEADER (<see cref="Count"/> 0) or a ROW of <see cref="Count"/> cards starting at the
/// visible entry <see cref="Start"/>. <see cref="Epoch"/> makes a data landing a bound-item change (it re-fires the slot)
/// without a remount.</summary>
public readonly record struct ProfileRowItem(int Letter, int Start, int Count, bool HasArtist, uint Epoch)
{
    public bool IsHeader => Count == 0;
}

/// <summary>The grouped flat projection (<see cref="LibraryLetters"/>' twin for a card GRID): a header per letter, each
/// letter's cards chunked by <c>columns</c>. Offsets are prefix sums over PINNED extents, so the sticky letter and the jump
/// never drift. Reused: grows, never shrinks. A grid has no header items of its own (<c>RepeatLayout.GridFit</c>), which is
/// why this is a flat list of headers and card ROWS.</summary>
public sealed class ProfileLetterRows
{
    /// <summary>The header's extent — the ONE number the offsets, the extents and the sticky overlay share.</summary>
    public const float HeaderExtent = LibraryLetters.HeaderExtent;
    ProfileRowItem[] _items = new ProfileRowItem[32];
    float[] _offset = new float[33];
    int[] _seq = new int[32];
    readonly JumpGroup[] _groups = new JumpGroup[LibraryLetters.Count];
    readonly Func<int, bool> _isHeader;
    readonly Func<int, int> _letterAt;
    int _flat, _groupCount;
    uint _present;

    public ProfileLetterRows()
    {
        _isHeader = f => _items[f].IsHeader;
        _letterAt = f => _items[f].Letter;
    }

    public int FlatCount => _flat;
    /// <summary>Bit i = letter i has at least one card (the strip's mask).</summary>
    public uint Present => _present;
    public ReadOnlySpan<ProfileRowItem> Items => _items.AsSpan(0, _flat);
    public ProfileRowItem Item(int flat) => (uint)flat < (uint)_flat ? _items[flat] : default;
    public bool IsHeader(int flat) => (uint)flat < (uint)_flat && _items[flat].IsHeader;
    public float OffsetOf(int flat) => _offset[Math.Clamp(flat, 0, _flat)];
    public float ExtentOf(int flat) => (uint)flat < (uint)_flat ? _offset[flat + 1] - _offset[flat] : HeaderExtent;
    public bool Has(int letter) => (uint)letter < LibraryLetters.Count && (_present & (1u << letter)) != 0;
    /// <summary>The header's flat index for <paramref name="letter"/>, or -1 (an absent letter — the strip's tap no-ops).</summary>
    public int HeaderFlat(int letter) => JumpIndex.Resolve(_groups.AsSpan(0, _groupCount), letter);

    /// <summary><paramref name="letters"/>/<paramref name="artist"/> are per VISIBLE entry, in display order (the letters
    /// non-decreasing). A card row is <paramref name="cardRow"/> tall, plus <see cref="ProfileGridFit.ActionLine"/> when
    /// any card of it is an artist (its Follow toggle).</summary>
    public void Build(ReadOnlySpan<int> letters, ReadOnlySpan<bool> artist, int columns, float cardRow, uint epoch)
    {
        columns = Math.Max(1, columns);
        // WORST CASE, not the sorted one: a band opens on every letter CHANGE, so unsorted input can open one header per
        // entry. A grouping helper must never be able to crash on its input.
        Grow(letters.Length * 2 + 1);
        _flat = 0; _present = 0;
        float off = 0f;
        for (int i = 0; i < letters.Length;)
        {
            int letter = letters[i], end = i;
            while (end < letters.Length && letters[end] == letter) end++;
            if ((uint)letter < LibraryLetters.Count) _present |= 1u << letter;
            Push(new(letter, i, 0, false, epoch), HeaderExtent, ref off);
            for (int s = i; s < end; s += columns)
            {
                int n = Math.Min(columns, end - s);
                bool a = false;
                for (int k = 0; k < n; k++) a |= artist[s + k];
                Push(new(letter, s, n, a, epoch), cardRow + (a ? ProfileGridFit.ActionLine : 0f), ref off);
            }
            i = end;
        }
        _offset[_flat] = off;
        for (int f = 0; f < _flat; f++) _seq[f] = f;
        _groupCount = JumpIndex.Project(_seq.AsSpan(0, _flat), _isHeader, _letterAt, _groups);
    }

    /// <summary>The letter whose band contains <paramref name="offset"/> (a binary search over the prefix sums); -1 above
    /// the first item, and for an empty build.</summary>
    public int StickyLetterAt(float offset)
    {
        int lo = 0, hi = _flat - 1, hit = -1;
        while (lo <= hi)
        {
            int m = (lo + hi) >> 1;
            if (_offset[m] <= offset) { hit = m; lo = m + 1; } else hi = m - 1;
        }
        return hit < 0 ? -1 : _items[hit].Letter;
    }

    /// <summary>FNV-1a of the projection's GEOMETRY (letter, start, count, artist) — never the epoch: the list's remount
    /// key. Two builds of the same grouping agree; a different grouping does not.</summary>
    public ulong Key()
    {
        ulong h = 14695981039346656037UL;
        for (int f = 0; f < _flat; f++)
        {
            var it = _items[f];
            h = (h ^ (uint)(it.Letter + 1)) * 1099511628211UL;
            h = (h ^ (uint)it.Start) * 1099511628211UL;
            h = (h ^ (uint)(it.Count * 2 + (it.HasArtist ? 1 : 0))) * 1099511628211UL;
        }
        return h;
    }

    void Push(in ProfileRowItem it, float extent, ref float off)
    {
        _items[_flat] = it;
        _offset[_flat] = off;
        _flat++;
        off += extent;
    }

    // Grow discards the old contents on purpose: its only caller is Build, which rewrites every cell it will read.
    void Grow(int n)
    {
        if (_items.Length >= n) return;
        int size = Math.Max(n, _items.Length * 2);
        _items = new ProfileRowItem[size]; _offset = new float[size + 1]; _seq = new int[size];
    }
}
