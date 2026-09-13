using System;
using System.Text;
using FluentGpu.Foundation;
using Wavee;
using Xunit;


namespace Wavee.Tests;

/// <summary>Wave 1's second gate (plan §5): "10k tracks resident in &lt; 1.5 MB managed, and zero allocations in a
/// 1k-row Knows sweep". The zero-allocation half is asserted per file beside the code that must not allocate; this
/// file owns the FOOTPRINT half, which no single file can claim because it is a property of the layout as a whole.
///
/// <para><b>IT MEASURES ROWS, IN BOTH FORMS, AND SAYS WHICH IS WHICH.</b> Since the packed <see cref="EntityId"/>
/// landed (docs/plans/wavee/wavee-0.3-entity-identity-memory.md, option 2, 2026-09-12) a row has two possible
/// identities, and they do not cost the same:
/// <list type="bullet">
/// <item><b>gid form</b> — the six catalog kinds off the Spotify wire, i.e. every real track. Identity is 16 packed
/// bytes in <c>Column&lt;EntityId&gt;</c> plus a slot in an open-addressed <c>int[]</c>. NO uri string exists anywhere
/// in the process. This is the gate's headline population, at the plan's 10,000.</item>
/// <item><b>text form</b> — local files, module playables, and any catalog uri whose id is not 22 base62 characters.
/// Identity is an interned <c>StringId</c>, so the row also carries a uri string and an entry in a
/// <c>Dictionary&lt;StringId,int&gt;</c>. A smaller population (<see cref="TextFormRows"/>), because it IS smaller in
/// the field — but measured on the same run and printed beside the other, since it is the shape 0.2.9 paid for
/// universally and the number is the size of what the change bought.</item>
/// </list>
/// The gate used to report a "text" line that was 10,000 interned strings belonging to NO row — never AddRef'd,
/// therefore permanent — added to a gid-row layout and printed as one total. That total described no population that
/// has ever existed, and it hid the point: a real Spotify track costs the first line, not the sum.</para>
///
/// <para>Why a test and not a benchmark: the number is the reason the columns exist. A row that quietly grows a
/// managed object — a string per title, a bool column, a boxed enum — costs a few bytes each and nothing catches it
/// until a library of 50k tracks is resident. The bounds are deliberately generous (the plan says so): a tripwire for
/// a structural regression, not a score to optimise against.</para></summary>
[Collection(EntitiesCollection.Name)]
public class FootprintGateTests(ITestOutputHelper output)
{
    const int Rows = 10_000;

    /// <summary>The text-form population is measured at a fifth of the scale, and that is on purpose: it is a
    /// minority shape now, and a 10,000-row run of it would intern 10,000 uri strings whose only job is to make the
    /// printed line look symmetrical. Nothing is lost by the smaller run — the per-row figure is flat in the scale
    /// (312.3 / 317.6 / 313.2 / 316.6 B per row at 1,000 / 2,000 / 5,000 / 10,000 rows, measured 2026-09-12), because
    /// what a text row costs is a string and a dictionary bucket, not a share of a fixed overhead.</summary>
    const int TextFormRows = 2_000;

    /// <summary>THE GID BUDGET, re-baselined for the packed identity (defect 6). The arithmetic it is drawn against,
    /// at <see cref="Rows"/> + 1 presized rows:
    /// <list type="bullet">
    /// <item>6 bookkeeping columns = 21 B/row (Version 4, Known 4, Authority 1, FetchedAt 4, Touched 4, Inflight 4);</item>
    /// <item><c>Column&lt;EntityId&gt;</c> = 24 B/row — where the old <c>Column&lt;StringId&gt; Uri</c> was 4 B/row but
    /// dragged a <c>Dictionary&lt;StringId,int&gt;</c> behind it at 20-35 B/row (doc §1.2);</item>
    /// <item>TrackTable's 21 value columns = 80 B/row — 64 of them, plus the 16 of
    /// <c>Column&lt;UInt128&gt; OriginalAudio</c> (see below);</item>
    /// <item>the open-addressed <c>int[]</c> index = 16,384 buckets × 4 B = 65,536 B, i.e. 6.6 B/row at load 0.61.</item>
    /// </list>
    /// 125 B/row of columns + 6.6 B/row of index ≈ <b>1.32 MB</b>. MEASURED, on this gate, over gid rows on
    /// 2026-09-12, BEFORE the audio key: <b>1,153,992 B (115.4 B/row) in Debug, 1,145,088 B (114.5 B/row) in
    /// Release</b> — the doc's ~120 B/row prediction met and slightly beaten.
    ///
    /// <para><b>RAISED ONCE, ON PURPOSE, 2026-09-13: 1,300,000 → 1,470,000 (+170,000 B).</b> The FLAC work adds exactly
    /// one column, <c>TrackTable.OriginalAudio</c> — <c>Track.original_audio.uuid</c>, the key to the
    /// <c>spotify:audio:</c> entity — at <b>16 B/row, +160,016 B at this scale</b>, taking the measured figure to about
    /// 1,314,000 B (131.4 B/row). It is not narrowable and not derivable: the uuid is on no other payload, it is not a
    /// function of the track's gid, and extension kind 5 on that entity is the only route that carries a FLAC file id
    /// (FLAC plan §5.1/§5.2 — TRACK_V4's own <c>file[]</c> lists Ogg and AAC and nothing else). Without the column the
    /// app would re-fetch a whole TrackV4 per track to ask one question about it. The new gate keeps the same ~12 % of
    /// headroom over the new measurement, and stays under the plan's original "10k tracks in &lt; 1.5 MB" sentence —
    /// it is still a tripwire for a STRUCTURAL regression (a managed object per row, a second identity map, a per-row
    /// string), and no other column may take this as a precedent without the same argument.</para></summary>
    const long GidLayoutBudget = 1_470_000;

    /// <summary>THE TEXT BUDGET, per row rather than absolute, because the two populations are deliberately different
    /// sizes and the only comparable number is per-row. A text-form row pays the same ~109 B of columns and then, on
    /// top of it, everything the packed identity removed: the interned uri (a 39-char <c>string</c>, plus the
    /// <c>StringTable</c>'s map entry, slab bytes and ref-count slot) and a <c>Dictionary&lt;StringId,int&gt;</c>
    /// bucket where a gid row has 4 bytes of <c>int[]</c>. MEASURED on this gate: <b>317.6 B/row</b> (2026-09-12,
    /// 317.5 in Release), so
    /// <b>a text-form row is 2.75× a gid row</b> — 202 B of it identity. That ratio is the whole return on the
    /// change, and it is why the six catalog kinds must never fall back to this form. 365 B/row is that measurement
    /// with ~15 % of headroom: bounded, not optimised, but it must not grow either.</summary>
    const double TextFormBudgetPerRow = 365.0;

    [Fact]
    public void Ten_thousand_tracks_are_resident_in_under_one_and_a_half_megabytes()
    {
        // ── the GID population: 10,000 real-shaped catalog rows, and not one uri string ──────────────────────────────
        TestScope.Fresh();
        var tracks = Entities.Current.Tracks;
        // One shared title, so the columns are exercised through the ref-counted write path (defect 1) without the
        // measurement becoming a measurement of 10,000 distinct titles. Interned BEFORE the baseline for that reason.
        var title = Entities.Strings.Intern("a title every row in the gate shares");
        int mapBefore = Entities.Strings.MapCount;

        Settle();
        long gidBefore = GC.GetTotalMemory(forceFullCollection: true);
        tracks.EnsureCapacity(Rows + 1);
        Span<byte> uri = stackalloc byte[48];
        for (int i = 0; i < Rows; i++)
        {
            // A real-shaped catalog uri: `spotify:track:` + 22 base62 characters. Digits alone are a legal (small)
            // gid, so `{i:D22}` decodes without ever becoming a string — which is the whole point of the phase.
            int written = Encoding.UTF8.GetBytes($"spotify:track:{i:D22}", uri);
            int slot = tracks.Slot(uri[..written]);
            Fill(tracks, slot, title, i);
        }
        Settle();
        long gid = GC.GetTotalMemory(forceFullCollection: true) - gidBefore;

        Assert.Equal(Rows + 1, tracks.Count);
        // THE headline of the identity change: 10,000 catalog rows interned not one string. Before it, each one cost a
        // 36-char uri that no trim could ever reclaim (doc §4.4) — the memory floor could only rise.
        Assert.Equal(mapBefore, Entities.Strings.MapCount);
        Assert.Equal(Rows, tracks.IndexedRows);
        Assert.Equal(0, tracks.TextRows);

        // ── the TEXT-FORM population: the same fields, the other identity ────────────────────────────────────────────
        // A fresh scope, so this is a measurement of these rows and not of the ten thousand above; `Entities.Boot`
        // hands the old set's interned text back on the way out (defect 1), which is what makes that legal.
        TestScope.Fresh();
        var locals = Entities.Current.Tracks;
        var localTitle = Entities.Strings.Intern("a title every local row in the gate shares");
        int localMapBefore = Entities.Strings.MapCount;

        Settle();
        long textBefore = GC.GetTotalMemory(forceFullCollection: true);
        locals.EnsureCapacity(TextFormRows + 1);
        for (int i = 0; i < TextFormRows; i++)
        {
            // Not Spotify, so no gid however the id is spelled: the uri is interned and the row is keyed by StringId.
            int written = Encoding.UTF8.GetBytes($"wavee:local:file:{i:D22}", uri);
            int slot = locals.Slot(uri[..written]);
            Fill(locals, slot, localTitle, i);
        }
        Settle();
        long text = GC.GetTotalMemory(forceFullCollection: true) - textBefore;

        Assert.Equal(TextFormRows + 1, locals.Count);
        Assert.Equal(localMapBefore + TextFormRows, Entities.Strings.MapCount);   // one interned uri per row…
        Assert.Equal(TextFormRows, locals.TextRows);                              // …and a dictionary entry for each
        Assert.Equal(0, locals.IndexedRows);

        output.WriteLine($"gid rows  {gid:N0} B for {Rows:N0} ({gid / (double)Rows:N1}/row, no uri string anywhere) · " +
                         $"text rows {text:N0} B for {TextFormRows:N0} ({text / (double)TextFormRows:N1}/row, " +
                         $"uri string + StringId map included)");

        Assert.True(gid < GidLayoutBudget,
            $"a gid row's layout costs {gid:N0} managed bytes for {Rows:N0} tracks ({gid / (double)Rows:N1} per row); " +
            $"the gate is {GidLayoutBudget:N0}. A regression here is structural — a managed object per row, a second " +
            $"identity map, or a per-row string — not a wider column.");
        Assert.True(text / (double)TextFormRows < TextFormBudgetPerRow,
            $"a text-form row costs {text / (double)TextFormRows:N1} B; the gate is {TextFormBudgetPerRow:N1}. This " +
            $"is the shape the packed identity exists to avoid, so it is bounded rather than optimised — but it must " +
            $"not grow either.");
    }

    /// <summary>The identity group, exactly as a TrackV4 answer fills it: interned text, slot references, packed
    /// flags. The same for both forms, so the two numbers differ only in what identity costs.</summary>
    static void Fill(TrackTable t, int slot, StringId title, int i)
    {
        t.SetText(ref t.Title, slot, title);
        t.SetText(ref t.Image, slot, title);
        t.DurationMs[slot] = 200_000 + i;
        t.Album[slot] = 0;
        t.Flags[slot] = (uint)TrackFlags.None;
        t.Applied(slot, (uint)TrackFields.Identity, Authority.Full, ref t.IdentityAuthority);
    }

    /// <summary>The sweep half, at the gate's own size: a 1,000-row <c>Knows</c> scan allocates nothing at all.</summary>
    [Fact]
    public void A_thousand_row_knows_sweep_allocates_nothing()
    {
        const int SweepRows = 1_000;
        TestScope.Fresh();

        var tracks = Entities.Current.Tracks;
        tracks.EnsureCapacity(SweepRows + 1);
        Span<byte> uri = stackalloc byte[48];
        for (int i = 0; i < SweepRows; i++)
        {
            int written = Encoding.UTF8.GetBytes($"spotify:track:{i:D22}", uri);
            tracks.Slot(uri[..written]);
        }

        // Warm the paths the loop below takes, so the measurement is the sweep and not its first call.
        int warm = 0;
        for (int slot = 1; slot <= SweepRows; slot++) if (tracks.Knows(slot, (uint)TrackFields.Identity)) warm++;

        long before = GC.GetAllocatedBytesForCurrentThread();
        int known = 0;
        for (int slot = 1; slot <= SweepRows; slot++) if (tracks.Knows(slot, (uint)TrackFields.Identity)) known++;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(warm, known);
        Assert.Equal(0L, allocated);
    }

    static void Settle()
    {
        for (int i = 0; i < 2; i++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
    }
}
