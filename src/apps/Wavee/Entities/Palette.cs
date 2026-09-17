// ── Entities/Palette.cs — CORE (owner A, wave 1, budget 400; plan §9.4 A5, §9.5) ─────────────────────────────────────
//
// THE COVER PALETTE: one durable, IMAGE-KEYED table of the colour roles the provider extracts from a cover, and the
// only place in the app that resolves an art colour. Ported from 0.2.9's `SpotifyLive/CoverColorPlane.cs` (556 lines):
// this file is the TABLE half — the columns, the key identity, the TTLs, `Watch`, `Ensure`, the demand-driven enqueue
// and the three writers. `Palette.Host.cs` (SHELL, Wave 4, owner L) is the other half: the 120 ms debounced pump, the
// `getDynamicColorsByUris` filler and the persisted store.
//
// WHY IT IS AN ENTITY FILE AND NOT A DESIGN TOKEN (arbitration A5, 2026-09-12). Six chapters ground on it — 00 §9.5,
// 03, 07 (G1), 08 (GAP 1), 12 and 21 — and what they need is a per-image, TTL'd, fetched, persisted, `Ensure`-able
// plane keyed exactly like an entity. `Platform/Design.cs` is a colour-maths file with no store, no fetch and no
// readiness; putting a table with a 180-day TTL inside it would be the wrong shape twice over. It lands in Wave 1,
// before anything that paints, because five of those chapters' GROUNDS depend on it.
//
// WHY IT IS KEYED BY IMAGE AND NOT BY ENTITY. The colours are a property of the COVER, not of the row that happens to
// show it. Extension kind 179 proves it by shipping the image urls and the colour set in ONE payload, so a track's
// answer also tints its album's grid card and a colour can exist before a single image byte arrives. 0.2.9 tried the
// other way first (`Track.Tint` / `Album.Tint` columns) and every surface ended up fetching, storing and threading its
// own copy — which is why grids painted grey while track lists painted colour.
//
// WHY IT IS NOT ON `Scope`. A cover's colours do not change with locale, market, tier or account, so a scope switch
// must NOT throw them away: this is the one table in `Entities/` that is process-wide. Its persisted form outlives
// every scope the app ever opens.
//
// WHY THE KEY IS STILL A `StringId` AND NOT AN `EntityId` (reviewed 2026-09-12 against
// docs/plans/wavee/wavee-0.3-entity-identity-memory.md, option 2 — which says the same at §5.1, "Palette.cs is
// untouched"). An artwork identity is not an entity: it has no kind and no provider, so `EntityId.ForText` would file
// it under `Unknown`/`None` and cost 20 bytes a row to carry two fields that mean nothing here. The GID form is the
// interesting question — a 24-hex artwork tail is 96 bits and WOULD pack — and the answer is still no, three times
// over: `EntityKind` has no image member and `EntityId.IsGidKind` gates the gid form on the six catalog kinds (that
// enum is Entities.cs's, not this file's); the open-addressed `int[]` that makes a packed key worth 3.0 ns lives on
// `Table`, and this is a `Publishable` by deliberate choice; and ids of ANY OTHER SHAPE key on themselves here
// (`ArtIdentityOf`), so a second, text-keyed index would be needed anyway. What this file DOES owe the packed-identity
// change is the other half of it — defect 1, below.
//
// REF-COUNTING (defect 1). Two strings per artwork used to be permanent. (1) The row's `Key` is the dictionary's key
// and `UriKeys` hashes RESOLVED text, so the table must own a reference or a release by whoever interned it would
// corrupt every bucket after that one; `Alloc` AddRefs it through `Entities.RetainText`. (2) The demand queue used to
// intern the FULL 40-character image id and drop it on `Drain` — one permanent string per artwork per run, which the
// old comment called "the same bargain 0.2.9 struck". The queue now holds SLOTS and the ROW owns its fetch id
// (`FetchId`), released the moment an answer lands. Rows themselves are never freed here (no trim, by design), so the
// `Key` reference is held for the life of the process on purpose — that is what "durable, process-wide" means.
//
// WHAT IS DELIBERATELY NOT HERE: the ARGB maths. `Lift`, `Vivid`, `Accent`, `ChromeAccent`, `PageTone`, `TextInk`,
// `Hairline`, `DataDotInk` and the `Neutral` fallback scheme are `WaveePalette`'s, and ch 00 §8 sends all of them to
// `Platform/Design.cs` as `Design.Palette` — pure functions over a `Scheme`, with no table behind them. This file
// answers "what did the provider say about this cover"; that one answers "what colour should this be".

using FluentGpu.Foundation;
using FluentGpu.Signals;

namespace Wavee;

// ── the graded scheme ────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>The five colour roles the provider grades per cover — the shape BOTH extension kind 179
/// (<c>VISUAL_IDENTITY_TRAIT</c>) and the <c>getDynamicColorsByUris</c> pathfinder query return. Opaque ARGB, framework
/// neutral: <c>Design.Palette.ToColor</c> maps a role to a renderer colour at the UI boundary.
///
/// <para>Five <c>uint</c>s in one unmanaged value, so a whole scheme is ONE column read and one cache line rather than
/// five (P2 — the roles are always read together; nothing reads <c>TextSubdued</c> alone).</para>
///
/// <para><b>Do not read <see cref="TextBrightAccent"/> as "the accent".</b> Over 9,316 cached gradings it is pure
/// <c>#FFFFFF</c> in every dark half and pure <c>#000000</c> in every light half — 100%. Treating it as the accent is
/// what made every Play CTA render system blue in 0.2.9; the accent is the most saturated of four roles, and that pick
/// is <c>Design.Palette.Accent</c> (ch 00 §4.2).</para></summary>
public readonly record struct Scheme(
    uint BackgroundBase,
    uint BackgroundTintedBase,
    uint TextBase,
    uint TextSubdued,
    uint TextBrightAccent)
{
    /// <summary>Nothing graded. A zero <see cref="BackgroundBase"/> is the honest "no answer" because the provider
    /// never ships a fully transparent role.</summary>
    public bool IsEmpty => BackgroundBase == 0;
}

/// <summary>What the palette knows about one image (the row's <c>Known</c> bits).</summary>
[Flags]
public enum PaletteBits : uint
{
    None = 0,
    /// <summary>A dark grading. Kind 179 only ever ships this half — its three schemes are elevation levels
    /// (base / darker / darkest), not light-vs-dark.</summary>
    Dark = 1 << 0,
    /// <summary>A light grading. Only the filler produces one, which is why a light-theme read of a 179-only entry
    /// must MISS and stay queued rather than drop a dark slab onto a pale page.</summary>
    Light = 1 << 1,
    /// <summary>The server has no colours for this cover. A real answer with its own (shorter) TTL — not an error, and
    /// not a reason to ask again this week.</summary>
    Negative = 1 << 2,
    /// <summary>Which half the server thinks suits the cover. Read by nothing in this layer today, but it is on the
    /// wire and it is one bit (ch 00 DATA GAPS).</summary>
    BestFitIsLight = 1 << 3,
    /// <summary>This image is in the pending queue. Kept as a bit rather than a second hash set: the queue is deduped
    /// by row, and a row is already the thing a lookup produced.</summary>
    Queued = 1 << 4,

    Graded = Dark | Light,
    Answered = Dark | Light | Negative,
}

// ── the table ────────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>The graded covers, as columns, keyed by ARTWORK IDENTITY (see <see cref="Palette.ArtIdentityOf"/>). Not a
/// <see cref="Table"/>: an image has no entity uri, no authority ladder and no field groups — it has one answer, a
/// timestamp and a TTL. <see cref="Publishable"/>, so a grading batch rides the same publication as everything else in
/// the drain (C3).</summary>
public sealed class PaletteTable : Publishable
{
    /// <summary>Slot 0 is the permanent "no image" row, as in every other table.</summary>
    public int Count = 1;

    /// <summary>The two halves. One column read per scheme (P2).</summary>
    public Column<Scheme> Dark, Light;
    /// <summary><see cref="PaletteBits"/>.</summary>
    public Column<uint> Known;
    /// <summary>When this row was last ANSWERED, in <see cref="Entities.Now"/> seconds — the TTL clock.</summary>
    public Column<int> Ts;
    /// <summary>The artwork identity this row is filed under (what the store persists as its primary key). OWNED: the
    /// table holds an interner reference on it from <see cref="Alloc"/> (file header, defect 1).</summary>
    public Column<StringId> Key;

    /// <summary>The FULL 40-character image id this row was queued with — what <c>spotify:image:</c> needs and what
    /// <see cref="Key"/> is deliberately not (the key is the size-independent tail, so one entry serves every
    /// rendition). OWNED while the row is <see cref="PaletteBits.Queued"/> and released when the answer lands; empty
    /// otherwise. Four bytes a row to stop a permanent string per artwork per run (file header, defect 1).</summary>
    public Column<StringId> FetchId;

    /// <summary>identity → slot, probeable by <c>ReadOnlySpan&lt;char&gt;</c> with no allocation on a hit (P6/P14) —
    /// which matters more here than anywhere else, because this lookup runs once per art tile per realize.</summary>
    public readonly Dictionary<StringId, int> ByKey = new(UriKeys.Instance);

    readonly Dictionary<StringId, int>.AlternateLookup<ReadOnlySpan<char>> _byChars;

    /// <summary>Per-image change signals, by slot. Sparse on purpose: a signal exists only for an image some leaf has
    /// actually subscribed to — a scrolling grid of 400 tiles that never calls <see cref="Palette.Watch"/> allocates
    /// none of them.</summary>
    readonly Dictionary<int, Signal<uint>> _watch = new();

    public PaletteTable()
    {
        _byChars = ByKey.GetAlternateLookup<ReadOnlySpan<char>>();
        EnsureCapacity(64);
    }

    public void EnsureCapacity(int capacity)
    {
        if (capacity <= Known.Capacity) return;
        Dark.EnsureCapacity(capacity);
        Light.EnsureCapacity(capacity);
        Known.EnsureCapacity(capacity);
        Ts.EnsureCapacity(capacity);
        Key.EnsureCapacity(capacity);
        FetchId.EnsureCapacity(capacity);
    }

    /// <summary>The slot for an artwork identity, allocating an empty row when it is new.</summary>
    public int Slot(StringId key)
    {
        if (key.IsEmpty) return 0;
        if (ByKey.TryGetValue(key, out int slot)) return slot;
        return Alloc(key);
    }

    /// <inheritdoc cref="Slot(StringId)"/>
    public int Slot(ReadOnlySpan<char> key)
    {
        if (key.IsEmpty) return 0;
        if (_byChars.TryGetValue(key, out int slot)) return slot;
        return Alloc(Entities.Strings.Intern(key));
    }

    /// <summary>Look up WITHOUT allocating a row — the probe a planner uses, so asking "has this been graded?" cannot
    /// itself create work.</summary>
    public bool TryGetSlot(ReadOnlySpan<char> key, out int slot)
    {
        if (!key.IsEmpty) return _byChars.TryGetValue(key, out slot);
        slot = 0;
        return false;
    }

    int Alloc(StringId key)
    {
        int slot = Count++;
        EnsureCapacity(Count);
        Dark[slot] = default;
        Light[slot] = default;
        Known[slot] = 0;
        Ts[slot] = 0;
        FetchId[slot] = StringId.Empty;
        // The row — and through it `ByKey`, whose comparer hashes the RESOLVED text — now OWNS this string. Without
        // the AddRef a release by whoever interned it would reclaim the id and leave the dictionary hashing "" for
        // this key, which corrupts the whole probe chain and not just this entry (defect 1, file header). Slots are
        // never recycled here, so the cell is guaranteed empty and this is a plain AddRef.
        Entities.RetainText(ref Key[slot], key);
        ByKey[key] = slot;
        return slot;                                  // NOT dirty: an empty row is not news
    }

    /// <summary>The signal that changes when THIS image is graded. Page chrome (a hero wash, an accent bar, the shell
    /// material tint) reads it at page scope so the wash lands the moment its OWN cover resolves, with no coupling to
    /// unrelated batches; art tiles read it per tile. Subscribing to <see cref="Publishable.Changed"/> instead would
    /// re-render a whole page — and every shelf in it — each time a scrolling grid finished another batch.</summary>
    public IReadSignal<uint> Watch(int slot)
    {
        if (slot <= 0) return Changed;
        if (_watch.TryGetValue(slot, out var signal)) return signal;
        signal = new Signal<uint>(0);
        _watch[slot] = signal;
        return signal;
    }

    /// <summary>Fire one image's watchers. Called by the three writers, inside the drain (C1). Per image and not per
    /// batch, exactly as 0.2.9: a batch of fifty gradings wakes only the leaves that were waiting for those fifty
    /// covers, and <see cref="Publishable.Changed"/> carries the batch itself.</summary>
    internal void Bump(int slot)
    {
        if (_watch.TryGetValue(slot, out var signal)) signal.Value = signal.Peek() + 1;

        // RE-ARM GUARD, and it is here rather than in Entities.cs because only this table needs it. `Entities.Boot`
        // and `Entities.Switch` clear the dirty LIST without clearing each table's `Dirty` flag. For the eight entity
        // tables that is invisible — a switch replaces them along with the scope — but this table is process-wide and
        // OUTLIVES the scope, so a switch between a `MarkDirty` and the drain that would have published it would
        // strand it as permanently "already enqueued" and its `Changed` signal would never fire again.
        // `Dirty` set with the publication counter moved on since we enqueued means exactly that: our enqueue was
        // dropped. (Reported to the orchestrator as a foundation issue; the guard is cheap and correct either way.)
        if (Dirty && Entities.Publication != _markedAt) Dirty = false;
        if (!Dirty) _markedAt = Entities.Publication;
        MarkDirty();
    }

    uint _markedAt;
}

// ── the front door ───────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>The palette's public surface: the key rules, the reads, the demand queue and the writers. Partial —
/// <c>Palette.Host.cs</c> (Wave 4) implements <see cref="PalettePump"/> with the debounce and the filler.</summary>
public static partial class Palette
{
    // ── the table ───────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Process-wide, not per <see cref="Scope"/> — see the file header. Named <c>Images</c> rather than
    /// <c>Table</c> so that the simple name <see cref="Wavee.Table"/> still means the entity-table base class
    /// everywhere inside this file.</summary>
    public static PaletteTable Images { get; } = new();

    /// <summary>Bumped once per publication in which any grading landed. Subscribe from ART TILES only; page chrome
    /// wants <see cref="Watch(ReadOnlySpan{char})"/>, which is one cover.</summary>
    public static IReadSignal<uint> Changed => Images.Changed;

    // ── TTLs and queue limits (ported verbatim from CoverColorPlane) ────────────────────────────────────────────────

    /// <summary>A cover's colours never change: persist for about half a year.</summary>
    public const int HitTtlSeconds = 180 * 24 * 60 * 60;
    /// <summary>A colourless cover: do not re-ask every launch, but do recover.</summary>
    public const int MissTtlSeconds = 7 * 24 * 60 * 60;
    /// <summary>Coalesce a grid realize — dozens of misses in one frame — into ONE batch. The host's debounce.</summary>
    public const int PumpDebounceMs = 120;
    /// <summary>Uris per <c>getDynamicColorsByUris</c> request.</summary>
    public const int BatchCap = 50;
    /// <summary>Beyond this the queue stops growing: a runaway scroll must not turn into an unbounded backlog.</summary>
    public const int MaxQueue = 512;

    // ── key identity ────────────────────────────────────────────────────────────────────────────────────────────────
    //
    // A Spotify image id is 40 hex characters whose FIRST 16 are a size/kind marker and whose last 24 identify the
    // ARTWORK (verified across 349 kind-179 payloads):
    //     ab67616d00004851<hash>  64px album      ab67616d00001e02<hash>  300px      ab67616d0000b273<hash>  640px
    //     ab6761610000f178<hash>  artist          ab67706c00006c11<hash>  playlist
    // So the COLOUR identity is the size-independent tail — one entry serves the row thumbnail and the hero — while a
    // FETCH needs the full id, because `getDynamicColorsByUris` takes `spotify:image:<full id>`. Ported from 0.2.9's
    // `Wavee.Core/Domain/ImageSource.cs`, which owned the rule so that the colour cache and the detail hero's cover
    // latch could never disagree about "same art". THAT REMAINS THE CONTRACT: `Detail.cs`'s latch must compare
    // `ArtIdentityOf`, not urls — comparing urls is exactly what made the hero re-decode and fade the same picture back
    // in when the full model landed.

    const int ImageIdLength = 40;
    const int SizePrefixLength = 16;
    const string ImageUriPrefix = "spotify:image:";
    const string CdnSegment = "/image/";

    /// <summary>The FULL image id: the segment after <c>/image/</c> of a CDN url, or the token after
    /// <c>spotify:image:</c>. A slice of the input — no substring, safe on a render path. Any other url shape yields
    /// its last path segment; empty in, empty out.</summary>
    public static ReadOnlySpan<char> ImageIdOf(ReadOnlySpan<char> url)
    {
        url = url.Trim();
        if (url.IsEmpty) return default;
        int q = url.IndexOf('?');
        if (q >= 0) url = url[..q];
        if (url.StartsWith(ImageUriPrefix, StringComparison.OrdinalIgnoreCase))
            return url[ImageUriPrefix.Length..].Trim();
        int img = url.LastIndexOf(CdnSegment, StringComparison.OrdinalIgnoreCase);
        if (img >= 0) return url[(img + CdnSegment.Length)..];
        int slash = url.LastIndexOf('/');
        return slash >= 0 && slash + 1 < url.Length ? url[(slash + 1)..] : url;
    }

    /// <summary>The size-independent ARTWORK identity of an image id: the 24-character tail of a 40-character Spotify
    /// id. Ids of any other shape key on themselves.</summary>
    public static ReadOnlySpan<char> ArtIdentityOf(ReadOnlySpan<char> imageId)
        => imageId.Length == ImageIdLength ? imageId[SizePrefixLength..] : imageId;

    /// <summary>The colour identity of a cover url, in one call: every pre-sized rendition of one artwork shares it.</summary>
    public static ReadOnlySpan<char> KeyOf(ReadOnlySpan<char> url) => ArtIdentityOf(ImageIdOf(url));

    /// <summary>Does this url carry the provider's full 40-hex image id, and can it therefore be SENT to
    /// <c>getDynamicColorsByUris</c>? Playlist mosaics and custom covers still key the local table — a kind-179 payload
    /// may have seeded them — but a render-path miss for one of those can never be filled by the endpoint, so a caller
    /// with richer context can pick a gradeable fallback instead of waiting forever.</summary>
    public static bool CanGrade(ReadOnlySpan<char> url)
    {
        var id = ImageIdOf(url);
        if (id.Length != ImageIdLength) return false;
        for (int i = 0; i < id.Length; i++)
        {
            char c = id[i];
            if ((uint)(c - '0') > 9u && (uint)(c - 'a') > 5u && (uint)(c - 'A') > 5u) return false;
        }
        return true;
    }

    /// <summary>The <c>spotify:image:&lt;id&gt;</c> token the filler takes. It does NOT accept an https url.</summary>
    public static string ImageUriFor(string imageId) => ImageUriPrefix + imageId;

    // ── the render-path reads ───────────────────────────────────────────────────────────────────────────────────────
    //
    // UI thread, like every other column read (C1); the render thread only resolves StringIds. A MISS ENQUEUES, which
    // is what makes every art slot in the app self-resolving: rendering the art IS the request, and no surface has to
    // remember to prefetch. The one probe that must not enqueue is `HasFreshDark`, and it says so.

    /// <summary>The graded roles for a cover, or false when nothing fresh is known (⇒ the caller paints the neutral
    /// tile, which is a designed state and not a skeleton).
    ///
    /// <para>A light theme needs a LIGHT scheme: an entry the filler has never completed holds only kind 179's dark
    /// half, and dropping that onto a pale page is the "graded, but wrong half" state ch 00 §4.1 spells out — it must
    /// miss and stay queued.</para></summary>
    public static bool TryScheme(ReadOnlySpan<char> url, bool lightTheme, out Scheme scheme)
    {
        scheme = default;
        var id = ImageIdOf(url);
        var key = ArtIdentityOf(id);
        if (key.IsEmpty) return false;

        if (!Images.TryGetSlot(key, out int slot))
        {
            // Unseen. PROBE first and mint a row only for an id the filler could actually answer: a playlist mosaic
            // tile or a custom cover is not gradeable, and a get-or-create here would cost a permanent row — plus its
            // dictionary entry — for every such image the app ever paints. That is a per-library memory cost for a
            // request that can never be made (R2, and the Wave-1 memory gate).
            if (id.Length != ImageIdLength) return false;
            slot = Images.Slot(key);
        }
        uint known = Images.Known[slot];

        if (Fresh(slot, known))
        {
            if ((known & (uint)PaletteBits.Negative) != 0) return false;
            if (!lightTheme && (known & (uint)PaletteBits.Dark) != 0) { scheme = Images.Dark[slot]; return true; }
            if (lightTheme && (known & (uint)PaletteBits.Light) != 0) { scheme = Images.Light[slot]; return true; }
        }

        Enqueue(slot, id);
        return false;
    }

    /// <inheritdoc cref="TryScheme(ReadOnlySpan{char},bool,out Scheme)"/>
    public static bool TryScheme(StringId imageRef, bool lightTheme, out Scheme scheme)
        => TryScheme(Entities.Strings.Resolve(imageRef).AsSpan(), lightTheme, out scheme);

    /// <summary>The art placeholder colour for a cover — the <see cref="Scheme.BackgroundBase"/> role of the half the
    /// theme needs. Same demand-driven fill as <see cref="TryScheme(ReadOnlySpan{char},bool,out Scheme)"/>.</summary>
    public static bool TryTint(ReadOnlySpan<char> url, bool lightTheme, out uint argb)
    {
        if (TryScheme(url, lightTheme, out var scheme) && !scheme.IsEmpty) { argb = scheme.BackgroundBase; return true; }
        argb = 0;
        return false;
    }

    /// <inheritdoc cref="TryTint(ReadOnlySpan{char},bool,out uint)"/>
    public static bool TryTint(StringId imageRef, bool lightTheme, out uint argb)
        => TryTint(Entities.Strings.Resolve(imageRef).AsSpan(), lightTheme, out argb);

    /// <summary>Does this cover already carry a fresh DARK grading? A PURE probe: unlike the reads above it never
    /// enqueues, because its caller is not rendering anything — it is the kind-179 trait projector asking "has this
    /// image been through here?" before a request is planned, and enqueuing from a planning question would turn every
    /// warm page into a <c>getDynamicColorsByUris</c> batch.
    ///
    /// <para>Dark specifically: 179 only ever ships dark, so a dark entry is exactly what a 179 answer would produce —
    /// and a NEGATIVE is not one, because a cover the colour server declined can still get a 179 payload.</para></summary>
    public static bool HasFreshDark(ReadOnlySpan<char> url)
    {
        if (!Images.TryGetSlot(KeyOf(url), out int slot)) return false;
        uint known = Images.Known[slot];
        return (known & (uint)PaletteBits.Dark) != 0
            && (known & (uint)PaletteBits.Negative) == 0
            && Entities.Now - Images.Ts[slot] <= HitTtlSeconds;
    }

    /// <summary>Does this cover already carry a fresh NEGATIVE (the server has no colours for it)? A PURE probe, like
    /// <see cref="HasFreshDark"/>: it never enqueues, so a caller checking "has this already been asked about" cannot
    /// itself turn into a request.</summary>
    public static bool HasFreshNegative(string url)
    {
        if (!Images.TryGetSlot(KeyOf(url.AsSpan()), out int slot)) return false;
        uint known = Images.Known[slot];
        return (known & (uint)PaletteBits.Negative) != 0 && Entities.Now - Images.Ts[slot] <= MissTtlSeconds;
    }

    /// <summary>The per-image change signal (see <see cref="PaletteTable.Watch"/>). Allocates the row if the image is
    /// new, so a page can watch a cover it has not painted yet and still be woken when the grading lands.</summary>
    public static IReadSignal<uint> Watch(ReadOnlySpan<char> url) => Images.Watch(Images.Slot(KeyOf(url)));

    /// <inheritdoc cref="Watch(ReadOnlySpan{char})"/>
    public static IReadSignal<uint> Watch(StringId imageRef)
        => imageRef.IsEmpty ? Changed : Watch(Entities.Strings.Resolve(imageRef).AsSpan());

    /// <summary>Is this row's answer still within its TTL? A hit lasts 180 days, a negative 7 (ch 00 DATA GAPS).</summary>
    static bool Fresh(int slot, uint known)
    {
        if ((known & (uint)PaletteBits.Answered) == 0) return false;
        int ttl = (known & (uint)PaletteBits.Negative) != 0 ? MissTtlSeconds : HitTtlSeconds;
        return Entities.Now - Images.Ts[slot] <= ttl;
    }

    // ── the demand queue ────────────────────────────────────────────────────────────────────────────────────────────

    // SLOTS, not ids: the row owns the interned full image id (`PaletteTable.FetchId`) and this is only the order
    // they are asked in. Dedupe is still the row's `Queued` bit, so ten renditions of one cover queue once.
    static readonly Queue<int> s_pending = new();

    /// <summary>How many images are waiting to be graded — the host's pump reads it, and the diagnostics page shows it.</summary>
    public static int Pending => s_pending.Count;

    /// <summary>Ask for a batch of images explicitly (ch 07 G1: "<c>Palette.Ensure(span of image ids)</c>, batched by
    /// <c>Fetch</c> exactly like rows"). Rare: the render-path miss is the normal request, and ch 00 is explicit that
    /// an image-keyed colour is NOT part of a page's model — do not fold it into a page's mount demand.</summary>
    public static void Ensure(ReadOnlySpan<StringId> imageRefs)
    {
        for (int i = 0; i < imageRefs.Length; i++)
        {
            if (imageRefs[i].IsEmpty) continue;
            var url = Entities.Strings.Resolve(imageRefs[i]).AsSpan();
            var id = ImageIdOf(url);
            if (id.Length != ImageIdLength) continue;              // same rule as the read path: no row for an
            int slot = Images.Slot(ArtIdentityOf(id));             // image the filler could never answer about
            if (slot > 0 && !Fresh(slot, Images.Known[slot])) Enqueue(slot, id);
        }
    }

    /// <summary>Queue one artwork. Deduped by COLOUR IDENTITY through the row's <see cref="PaletteBits.Queued"/> bit
    /// (so ten sizes of one cover ask once), while the request itself needs the FULL image id — which is what
    /// <see cref="PaletteTable.FetchId"/> holds, and why the queue carries slots and not ids.
    ///
    /// <para>Interning that full id is the ONE allocation on this read path and it happens once per artwork per run,
    /// which is the bargain 0.2.9 struck — but the string is now RELEASED when the answer lands (defect 1), so the
    /// bargain no longer includes making it permanent.</para></summary>
    static void Enqueue(int slot, ReadOnlySpan<char> imageId)
    {
        if (slot <= 0 || imageId.IsEmpty || s_pending.Count >= MaxQueue) return;
        if ((Images.Known[slot] & (uint)PaletteBits.Queued) != 0) return;
        if (imageId.Length != ImageIdLength) return;              // not gradeable: never queue an impossible request
        Images.Known[slot] |= (uint)PaletteBits.Queued;
        // The ROW owns the full id: RetainText AddRefs it and releases whatever this row was last queued with, and
        // `Answered` hands it straight back. It is still ONE intern per artwork on the render path — but no longer a
        // permanent one, which is the whole of defect 1 at this call site.
        Entities.RetainText(ref Images.FetchId[slot], Entities.Strings.Intern(imageId));
        s_pending.Enqueue(slot);
        PalettePump();
    }

    /// <summary>An answer landed for this row: drop the queued marker and give the full image id back. Every writer
    /// below ends here, which is what keeps "owned while queued" true with no second bookkeeping (defect 1).</summary>
    static void Answered(int slot)
    {
        Images.Known[slot] &= ~(uint)PaletteBits.Queued;
        Entities.ReleaseText(ref Images.FetchId[slot]);
    }

    /// <summary>Take up to <paramref name="dst"/>.Length queued image ids for one request. The host's pump calls it
    /// after its debounce; the rows stay marked <see cref="PaletteBits.Queued"/> until an answer clears them, which is
    /// what stops a second batch from re-asking the same covers while the first is in flight.</summary>
    public static int Drain(Span<StringId> dst)
    {
        int n = 0;
        while (n < dst.Length && s_pending.Count > 0)
        {
            // An empty fetch id means the answer beat the drain — a kind-179 payload landed for a cover the render
            // path had already queued. Skipping it is how that cancellation reaches the pump, and it costs one load.
            var id = Images.FetchId[s_pending.Dequeue()];
            if (!id.IsEmpty) dst[n++] = id;
        }
        return n;
    }

    /// <summary>Implemented by <c>Palette.Host.cs</c> (SHELL, Wave 4): the 120 ms debounce and the filler. Erased when
    /// that file is absent, so tests and probes get a pure in-memory plane with no network at all.</summary>
    static partial void PalettePump();

    // ── the writers (UI thread, inside the drain — C1) ──────────────────────────────────────────────────────────────

    /// <summary>A DARK-ONLY grading (extension kind 179, or a GraphQL <c>visualIdentity</c> node). Never downgrades an
    /// entry the filler has already completed with a light half — that is the whole reason this is not one setter.</summary>
    public static void SetDark(ReadOnlySpan<char> url, in Scheme dark)
    {
        if (dark.IsEmpty) return;
        var key = KeyOf(url);
        if (key.IsEmpty) return;
        int slot = Images.Slot(key);

        Images.Dark[slot] = dark;
        // Keep the graded light half if there is one; clear Negative, because we now have a colour.
        Images.Known[slot] = (Images.Known[slot] & (uint)(PaletteBits.Light | PaletteBits.BestFitIsLight))
                          | (uint)PaletteBits.Dark;
        Images.Ts[slot] = Entities.Now;
        Answered(slot);                                            // this IS the answer: unqueue and release the id
        MarkPersist(slot);
        Images.Bump(slot);
    }

    /// <summary>A FULL grading from the filler: dark, an optional light half, and the server's best-fit hint. Takes the
    /// image id it was REQUESTED with and files it under the size-independent identity, so one answer serves every size
    /// of that artwork.</summary>
    public static void SetGraded(ReadOnlySpan<char> imageId, in Scheme dark, in Scheme light, bool hasLight, bool bestFitIsLight)
    {
        var key = ArtIdentityOf(imageId);
        if (key.IsEmpty) return;
        int slot = Images.Slot(key);

        Images.Dark[slot] = dark;
        Images.Light[slot] = hasLight ? light : default;
        uint known = dark.IsEmpty ? 0u : (uint)PaletteBits.Dark;
        if (hasLight && !light.IsEmpty) known |= (uint)PaletteBits.Light;
        if (bestFitIsLight) known |= (uint)PaletteBits.BestFitIsLight;
        Images.Known[slot] = known;                                // clears Queued and Negative: this IS the answer
        Images.Ts[slot] = Entities.Now;
        Answered(slot);
        MarkPersist(slot);
        Images.Bump(slot);
    }

    /// <summary>The server has no colours for this cover. A real answer, on the 7-day miss TTL — recording it is what
    /// stops the same impossible cover from being re-asked on every launch.</summary>
    public static void SetNegative(ReadOnlySpan<char> imageId)
    {
        var key = ArtIdentityOf(imageId);
        if (key.IsEmpty) return;
        int slot = Images.Slot(key);

        Images.Dark[slot] = default;
        Images.Light[slot] = default;
        Images.Known[slot] = (uint)PaletteBits.Negative;
        Images.Ts[slot] = Entities.Now;
        Answered(slot);
        MarkPersist(slot);
        Images.Bump(slot);
    }

    // ── persistence (Store.Palette.cs is the other half) ────────────────────────────────────────────────────────────

    /// <summary>Slots written since the last drain. A <c>List</c>, not a set: two marks for one slot before a flush
    /// just cost one redundant (idempotent) upsert, which is cheaper than deduping on every write.</summary>
    static readonly List<int> s_dirtySlots = new();

    /// <summary>How many slots are waiting for their next disk flush — the store's own flush loop reads this to decide
    /// whether to re-arm after a batch.</summary>
    public static int DirtyCount => s_dirtySlots.Count;

    static void MarkPersist(int slot)
    {
        if (slot <= 0) return;
        s_dirtySlots.Add(slot);
        PalettePersistArm();
    }

    /// <summary>Implemented by <c>Store.Palette.cs</c> as <c>Store.ArmPaletteFlush()</c>. Erased with no store file
    /// (<c>--fake</c>, every unit test that never calls <c>Store.Use</c>), so a dirty mark here costs nothing more
    /// than the list entry.</summary>
    static partial void PalettePersistArm();

    /// <summary>One dirty row, snapshot for the crossing to the store thread: the key resolved to text (UI thread
    /// only — the store thread may never touch the interner), the bits worth persisting, and the two schemes.
    /// <paramref name="TsUnix"/> is unix seconds (Store.cs's clock is the conversion).</summary>
    public readonly record struct PaletteRowSnapshot(string Key, uint Known, long TsUnix, Scheme Dark, Scheme Light);

    /// <summary>Take up to <paramref name="dst"/>.Length dirty rows, resolving each one's key text here (UI thread).
    /// Drained entries are removed from the dirty list; any left over (a batch bigger than <paramref name="dst"/>)
    /// stay dirty for the next drain.</summary>
    public static int DrainDirty(Span<PaletteRowSnapshot> dst)
    {
        int n = 0, taken = 0;
        while (taken < s_dirtySlots.Count && n < dst.Length)
        {
            int slot = s_dirtySlots[taken++];
            if (slot <= 0 || slot >= Images.Count) continue;
            string key = Entities.Strings.Resolve(Images.Key[slot]);
            if (key.Length == 0) continue;
            dst[n++] = new PaletteRowSnapshot(key, Images.Known[slot], Store.ToUnix(Images.Ts[slot]),
                                               Images.Dark[slot], Images.Light[slot]);
        }
        if (taken >= s_dirtySlots.Count) s_dirtySlots.Clear();
        else s_dirtySlots.RemoveRange(0, taken);
        return n;
    }

    /// <summary>Rows the store loaded off disk at boot, applied to the live table. UI thread (posted back from the
    /// store thread's warm read).
    ///
    /// <para>A row is skipped when the live table already holds a FRESHER answer than the one on disk — a grading
    /// that landed from the network between boot and this restore must win, never be overwritten by yesterday's
    /// file.</para></summary>
    public static void Restore(List<PaletteRowSnapshot> rows)
    {
        bool any = false;
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row.Key.Length == 0) continue;
            int slot = Images.Slot(row.Key.AsSpan());
            int rowTsApp = Store.ToApp(row.TsUnix);

            uint liveKnown = Images.Known[slot];
            if ((liveKnown & (uint)PaletteBits.Answered) != 0 && Images.Ts[slot] >= rowTsApp) continue;

            Images.Dark[slot] = row.Dark;
            Images.Light[slot] = row.Light;
            // Keep a live Queued bit — a render-path miss may already have asked for this cover before the warm
            // read landed, and the answer for that ask is still in flight.
            Images.Known[slot] = PalettePersistence.RestoreMask(row.Known) | (liveKnown & (uint)PaletteBits.Queued);
            Images.Ts[slot] = rowTsApp;
            Images.Bump(slot);
            any = true;
        }
        if (any) Entities.Publish();
    }

    /// <summary>A batch attempt FAILED (the request threw, or the connection dropped). Free the rows so the next render
    /// can re-queue them: a failure is not an answer, and the covers must not stay silently queued forever.
    /// <para>The row KEEPS its <see cref="PaletteTable.FetchId"/> — a failure is not an answer, so the id it will be
    /// re-asked with has not stopped being wanted, and the re-queue's <see cref="Entities.RetainText"/> is then a
    /// no-op rather than a second intern.</para></summary>
    public static void Failed(ReadOnlySpan<StringId> imageIds)
    {
        for (int i = 0; i < imageIds.Length; i++)
        {
            if (imageIds[i].IsEmpty) continue;
            if (!Images.TryGetSlot(ArtIdentityOf(Entities.Strings.Resolve(imageIds[i]).AsSpan()), out int slot)) continue;
            Images.Known[slot] &= ~(uint)PaletteBits.Queued;
        }
    }
}

// ── staging: the gradings a decode hands the drain ────────────────────────────────────────────────────────────────────

/// <summary>One image's dark grading, staged: extension kind 179 names the image and the colour in the SAME payload, so
/// a placeholder can be tinted before any image byte arrives. It lands here rather than in the decoder because
/// <see cref="Palette.SetDark"/> is a UI-thread writer and 179 arrives on the socket thread (C1/C10).</summary>
public struct StagedGrading
{
    public TextRef Url;
    public Scheme Dark;
}

/// <inheritdoc cref="StagedGrading"/>
public sealed class StagedGradingList : StagedList
{
    StagedGrading[] _a = new StagedGrading[16];

    public ref StagedGrading Add()
    {
        if (Count == _a.Length) Array.Resize(ref _a, _a.Length * 2);
        ref var g = ref _a[Count++];
        g = default;
        return ref g;
    }

    public ref StagedGrading this[int i] => ref _a[i];
    public ReadOnlySpan<StagedGrading> Span => _a.AsSpan(0, Count);
    public override void Clear() => Count = 0;
}

public sealed partial class Staging
{
    StagedGradingList? _gradings;

    /// <summary>The staged dark gradings (extension kind 179). Lazy: a decode that names no colour allocates nothing.</summary>
    public StagedGradingList Gradings => _gradings ??= Register(new StagedGradingList());

    internal StagedGradingList? GradingsOrNull => _gradings;
}

public static partial class Entities
{
    /// <summary>The dark gradings into the palette plane, keyed by the IMAGE and never by the entity — one track's
    /// answer also tints its album's grid card and its playlist's hero (Palette.cs's own "why it is keyed by image").</summary>
    static partial void CommitGradings(Staging s)
    {
        var list = s.GradingsOrNull;
        if (list is null || list.Count == 0) return;

        var rows = list.Span;
        Span<char> url = stackalloc char[512];
        for (int i = 0; i < rows.Length; i++)
        {
            ref readonly var row = ref rows[i];
            if (row.Url.IsEmpty || row.Dark.IsEmpty) continue;
            var utf8 = s.Utf8(row.Url);
            if (utf8.Length > url.Length) continue;
            Palette.SetDark(url[..System.Text.Encoding.UTF8.GetChars(utf8, url)], in row.Dark);
        }
    }
}
