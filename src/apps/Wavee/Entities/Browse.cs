// ── Entities/Browse.cs — CORE (owner B, wave 1; plan §2, ch 13 §7) ───────────────────────────────────────────────────
//
// BROWSE IS A TREE OF PAGES, AND EVERY NODE IS ONE ROW. 0.2.9 held it in two hand-rolled caches — a
// `BrowseDirectoryStore` with a 15-minute TTL and a `BrowsePageStore` with 16 FIFO slots — precisely because the page
// models were objects a page owned and a keep-alive eviction destroyed. Both stores are DELETED in 0.3: table rows
// outlive page mounts by construction, so a remount reads the row and paints full-height content immediately, which is
// what makes the keyed scroll offset restore against real layout instead of against a skeleton (ch 13 §7).
//
//        Browse["wavee:browse"]        the directory  ── categories ──▶ Browse["spotify:page:…"] rows
//        Browse["spotify:page:0JQ5…"]  a category page ── sections  ──▶ Sections[…]   (Home.cs's shared table)
//
// ONE TABLE FOR THE TILE AND THE PAGE, because they are the same thing seen twice: `browseAll` gives a category its
// title, colour and artwork, and opening it gives the SAME uri a set of sections. Two tables would mean the tile's
// colour and the page's accent living apart and disagreeing after a refresh, which is the class of bug the row model
// exists to remove. A genre tile is the same row again: `spotify:genre:<id>` and `spotify:page:<id>` address ONE node
// (0.2.9 `SearchRoutes.cs:15-17`), and <see cref="Browse.PageUriOf"/> is the fold.
//
// THE SECTIONS ARE `Home.cs`'s `SectionTable` — see that file's header for why there is one section store and one
// `SectionKind` with family-namespaced members rather than two of each.
//
// A BROWSE NODE IS THE TEXT FORM, AND IT OWNS ITS TEXT (defect 1 of the identity investigation,
// docs/plans/wavee/wavee-0.3-entity-identity-memory.md §4.4). `spotify:page:<id>` names no catalog KIND, so the gid
// parse declines it and the row keeps an interned uri; its title and artwork id are interned too. The engine reclaims
// a string only when its last reference is released and a never-AddRef'd id is PERMANENT (the engine's
// `StringTable.cs:26`), so ~70 directory tiles times every scope this process ever opens is a floor that only rose.
// Both columns go through <see cref="Table.SetText"/> and come back in <see cref="BrowseTable.ReleaseText"/>.
//
// What is NOT here: `BrowseTaxonomy`'s curated uri→band map, `BrowseDirectorySeeds`, `BrowsePageLayout`,
// `BrowseLayout` and `BrowseMastheadMetrics` — ch 13 §8's rule set, owner P's, Wave 5, in this same file. Wave 1 owns
// the columns and the identity they read; <see cref="BrowseGroup"/> is declared here because it is the taxonomy's
// answer type and three other files name it.

using FluentGpu.Foundation;

namespace Wavee;

// ── 1. field groups and the band taxonomy ────────────────────────────────────────────────────────────────────────────

/// <summary>Which column groups of a browse node are filled (P3). A node can legitimately know its TILE and not its
/// PAGE (the directory answered, nobody has opened it) or its page and not its tile (a deep link straight in), which
/// is exactly why they are two groups and not one.</summary>
[Flags]
public enum BrowseFields : uint
{
    /// <summary>The tile: title, artwork, the category colour, and the client-feature flag.</summary>
    Identity = 1 << 0,
    /// <summary>The page: its accent and its section ledger. Filled by opening the node, not by listing it.</summary>
    Page = 1 << 1,

    All = Identity | Page,
}

/// <summary>The Browse directory's bands. The WIRE HAS NO GROUPING — <c>browseAll</c> returns one flat section of
/// ~70 categories — so these six bands and their order are a product decision, carried by a curated uri map that Wave
/// 5 ports (0.2.9 <c>BrowseTaxonomy.cs</c>). An unmapped category falls into <see cref="More"/>, never into nothing;
/// the enum's declaration order IS the band order.</summary>
public enum BrowseGroup : byte { Top = 0, ForYou = 1, Genres = 2, MoodActivity = 3, Charts = 4, More = 5 }

/// <summary>Row-level booleans for a browse node.</summary>
[Flags]
public enum BrowseFlags : uint
{
    None = 0,
    /// <summary>A <c>BrowseClientFeature</c> such as Live Events: NOT a browse page. It carries a feature uri
    /// (<c>spotify:concerts</c>) and routes into the client's own surface, so a tile that "opens" it must not push a
    /// browse route (0.2.9 <c>BrowseCategory.IsClientFeature</c>).</summary>
    ClientFeature = 1 << 0,
}

// ── 2. the table ─────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>The browse nodes: the directory, every category tile and every category page, keyed by
/// <c>spotify:page:&lt;id&gt;</c>.</summary>
public sealed class BrowseTable : Table
{
    public Column<StringId> Title, Image;
    /// <summary>The TILE's colour (0.2.9's <c>BrowseCategory.Color</c>, opaque ARGB, 0 = none).</summary>
    public Column<uint> Color;
    /// <summary>The PAGE's own header accent, which is frequently absent and is a different value from
    /// <see cref="Color"/> — the directory and the page header are graded separately by the server.</summary>
    public Column<uint> Accent;
    public Column<uint> Flags;

    /// <summary>How many sections the page has in total, and the page-level paging cursor. Deliberately kept even
    /// though nothing pages it in practice (captured browse pages return every section at offset 0): the field exists
    /// on the wire, and a column that mirrors the wire cannot silently disagree with it later.</summary>
    public Column<int> TotalSections, NextSectionOffset;

    public Column<byte> IdentityAuthority, PageAuthority;

    /// <summary>A browse node is not a catalog entity: nothing fetches it by kind and the store's warm/trim walk does
    /// not carry it. Its <see cref="Table.Id"/> is the TEXT form of <c>spotify:page:&lt;id&gt;</c>.</summary>
    public override EntityKind Kind => EntityKind.Unknown;

    /// <summary>Give back the two strings a browse node owns (defect 1, file header). Called by
    /// <see cref="Table.FreeSlot"/> and by <see cref="Table.ReleaseAllText"/> when the scope retires (D9).</summary>
    protected override void ReleaseText(int slot)
    {
        ClearText(ref Title, slot);
        ClearText(ref Image, slot);
    }

    protected override void GrowColumns(int capacity)
    {
        Title.EnsureCapacity(capacity);
        Image.EnsureCapacity(capacity);
        Color.EnsureCapacity(capacity);
        Accent.EnsureCapacity(capacity);
        Flags.EnsureCapacity(capacity);
        TotalSections.EnsureCapacity(capacity);
        NextSectionOffset.EnsureCapacity(capacity);
        IdentityAuthority.EnsureCapacity(capacity);
        PageAuthority.EnsureCapacity(capacity);
    }
}

/// <summary>The browse half of the scope's synthetic subjects (see <c>Home.cs</c> for the rest).</summary>
public sealed partial class Scope
{
    public readonly BrowseTable Browses = new();
}

// ── 3. the handle ────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>One browse node — the directory, a category tile, a category page, or a genre (all the same row). Partial
/// so Wave 5 adds ch 13 §8's rule set to the same type.</summary>
public readonly partial struct Browse(int slot) : IEquatable<Browse>
{
    /// <summary>The directory's own subject uri. A synthetic row like Home's: it is what the ~70 category tiles hang
    /// off, and what makes the directory survive a page unmount.</summary>
    public const string DirectoryUri = "wavee:browse";

    /// <summary>The wire prefix a browse NODE is addressed by.</summary>
    public const string PagePrefix = "spotify:page:";
    /// <summary>The wire prefix a search GENRE tile is addressed by. Same node, other spelling.</summary>
    public const string GenrePrefix = "spotify:genre:";

    static BrowseTable T => Entities.Current.Browses;

    public int Slot { get; } = slot;

    public bool IsValid => Slot > Table.None && Slot < T.Count;
    public uint Version => T.Version[Slot];
    public bool Knows(BrowseFields fields) => T.Knows(Slot, (uint)fields);

    /// <summary>THE node's identity, packed (<c>Entities.cs</c> §2). Always the TEXT form, and always the PAGE
    /// spelling: <see cref="PageUriOf"/> folds a genre uri before the row is allocated, so the tile and the page are
    /// literally one id (ch 13 §7 gap 3).</summary>
    public EntityId Id => T.Id[Slot];

    public StringId TitleId => T.Title[Slot];
    public StringId ImageId => T.Image[Slot];
    public uint Color => T.Color[Slot];
    public uint Accent => T.Accent[Slot];
    public bool IsClientFeature => (T.Flags[Slot] & (uint)BrowseFlags.ClientFeature) != 0;
    public int TotalSections => T.TotalSections[Slot];
    public int NextSectionOffset => T.NextSectionOffset[Slot];

    /// <summary>Fold a genre uri onto its page uri — <c>spotify:genre:&lt;id&gt;</c> and <c>spotify:page:&lt;id&gt;</c>
    /// are ONE node (ch 13 §7 gap 3). Anything that is not a genre uri passes straight through, so the caller can
    /// route every tile through this without a kind test. Writes into <paramref name="into"/> and returns the written
    /// slice — no substring, no allocation (P6/P8).</summary>
    public static ReadOnlySpan<char> PageUriOf(ReadOnlySpan<char> uri, Span<char> into)
    {
        if (!uri.StartsWith(GenrePrefix)) return uri;
        var id = uri[GenrePrefix.Length..];
        int n = PagePrefix.Length + id.Length;
        if (n > into.Length) return uri;                            // an id longer than the buffer: pass it through
        PagePrefix.AsSpan().CopyTo(into);
        id.CopyTo(into[PagePrefix.Length..]);
        return into[..n];
    }

    /// <summary>Is this uri a browse node at all (either spelling)? The tile's own "can I open this" test, and the
    /// route table's guard.</summary>
    public static bool IsNodeUri(ReadOnlySpan<char> uri) => uri.StartsWith(PagePrefix) || uri.StartsWith(GenrePrefix);

    public bool Equals(Browse other) => other.Slot == Slot;
    public override bool Equals(object? o) => o is Browse b && b.Slot == Slot;
    public override int GetHashCode() => Slot;
    public static bool operator ==(Browse a, Browse b) => a.Slot == b.Slot;
    public static bool operator !=(Browse a, Browse b) => a.Slot != b.Slot;
}

// ── 4. factories, batch sugar and the commit ─────────────────────────────────────────────────────────────────────────

public static partial class Entities
{
    /// <summary>The Browse directory subject (D10: allocated on first ask).</summary>
    public static Browse BrowseDirectory() => new(Current.Browses.Slot(Browse.DirectoryUri.AsSpan()));

    /// <summary>A browse node by uri, in either spelling — a genre uri resolves to the SAME row as its page uri.</summary>
    public static Browse BrowseNode(ReadOnlySpan<char> uri)
    {
        Span<char> buffer = stackalloc char[EntityUri.StackChars];
        return new Browse(Current.Browses.Slot(Browse.PageUriOf(uri, buffer)));
    }

    /// <inheritdoc cref="Ensure(Table,ReadOnlySpan{int},uint,FetchPriority)"/>
    public static void Ensure(ReadOnlySpan<Browse> rows, BrowseFields wanted, FetchPriority priority = FetchPriority.Visible)
        => Ensure(Current.Browses, Slots(rows), (uint)wanted, priority);

    /// <inheritdoc cref="Ensure(Table,ReadOnlySpan{int},uint,FetchPriority)"/>
    public static void Ensure(Browse row, BrowseFields wanted, FetchPriority priority = FetchPriority.Visible)
    {
        int slot = row.Slot;
        Ensure(Current.Browses, new ReadOnlySpan<int>(in slot), (uint)wanted, priority);
    }
}

/// <summary>One decoded browse node — a directory tile, a page header, or both. Text is a <see cref="TextRef"/>: the
/// decoder cannot intern (C1/C10).</summary>
public struct StagedBrowse : IStagedRow
{
    /// <summary>THE row's identity as the wire gave it (<see cref="StagedId"/>): the packed <see cref="EntityId"/> when
    /// it arrived as 16 gid bytes, the uri's UTF-8 in the arena when it arrived as text. One field, one resolve —
    /// <c>s.Slot(table, in row.Id)</c> — and no format-to-arena-then-parse-back round trip.</summary>
    public StagedId Id;
    public TextRef Title, Image;
    public uint Color, Accent, Flags;
    public int TotalSections, NextSectionOffset;
    public uint Known;
    public Authority Authority;

    /// <inheritdoc cref="IStagedRow.Init"/>
    public void Init(in StagedId id, Authority authority, uint known) { Id = id; Authority = authority; Known = known; }
    /// <inheritdoc cref="IStagedRow.Identity"/>
    public readonly StagedId Identity => Id;
}

public sealed partial class Staging
{
    StagedList<StagedBrowse>? _browses;
    /// <summary>Lazy: a decode that touches no browse node allocates no browse list.</summary>
    public StagedList<StagedBrowse> Browses => _browses ??= Register(new StagedList<StagedBrowse>());
    internal StagedList<StagedBrowse>? StagedBrowses => _browses;
}

public readonly partial struct Browse
{
    /// <summary>Copy a staged batch of browse nodes into the columns. NOT wired into <c>Entities.Commit</c> yet — its
    /// partial hooks are one per ENTITY KIND and a browse node is not one, so the orchestrator adds the single call
    /// (see this wave's return). Same shape as every kind commit (D16).</summary>
    public static void Commit(Staging s)
    {
        var staged = s.StagedBrowses;
        if (staged is null || staged.Count == 0) return;

        var t = Entities.Current.Browses;
        var rows = staged.Span;
        for (int i = 0; i < rows.Length; i++)
        {
            ref var row = ref rows[i];
            int slot = s.Slot(t, in row.Id);
            if (slot == Table.None) continue;   // a row with no identity is not a row
            var authority = row.Authority == Authority.None ? s.Authority : row.Authority;

            if ((row.Known & (uint)BrowseFields.Identity) != 0
                && t.Accepts(slot, (uint)BrowseFields.Identity, authority, in t.IdentityAuthority))
            {
                // SetText, never `t.Title[slot] = …`: the write AddRefs the incoming id and releases the one it
                // overwrites, so a directory re-listed every 15 minutes owns one title per tile (defect 1, header).
                t.SetText(ref t.Title, slot, s.Intern(row.Title));
                t.SetText(ref t.Image, slot, s.Intern(row.Image));
                t.Color[slot] = row.Color;
                t.Flags[slot] = row.Flags;
                t.Applied(slot, (uint)BrowseFields.Identity, authority, ref t.IdentityAuthority);
            }

            if ((row.Known & (uint)BrowseFields.Page) != 0
                && t.Accepts(slot, (uint)BrowseFields.Page, authority, in t.PageAuthority))
            {
                t.Accent[slot] = row.Accent;
                t.TotalSections[slot] = row.TotalSections;
                t.NextSectionOffset[slot] = row.NextSectionOffset;
                t.Applied(slot, (uint)BrowseFields.Page, authority, ref t.PageAuthority);
            }
        }
    }
}
