// ── Entities/User.Cover.cs ─────────────────────────────────────────────────────────────────────────────────────────
// the Liked Songs cover's CORE HALF: the contract surface (LikedCover / LikedArtwork / LikedChipArt / LikedToneAnchorUrl), the snapshot
// that gates every cover on one equality, the demand, and the two hosts that mount the structure's views (the cover and the picker's
// flyout)
//
// T9c (themes plan 6.1): the PICTURES moved to Classic: the nine treatments, the heart geometry, the four palette leaves, the stock
// cover, the ambient loops, the site ladder, the picker and its flyout (`Themes\Classic\Structure\Views\Liked\`, keys in
// `Theming\Contracts\ViewKeys.Liked.cs`). What stays here decides: which style the persisted setting and the library's floor make
// effective, the newest-first distinct tiles and their content key, the count (stated only once the liked edge is Complete: a partial
// count on the cover is a lie, ch 07 §7), the demand, and the persisted pick.
//
// Role: UI
// Owner: O
// Wave: 5
// Spec: ch 07 §0 items 1-6, 11, 15; W5-W15; §3-§6 cover rows; §9 cover notes. Ported from 0.2.9
//       `Features/Detail/LikedCoverTreatments.cs`, `LikedCoverLeaves.cs`, `LikedHeart.cs`, `LikedCoverArt.cs`,
//       `LikedCoverPicker.cs`, `Components/LikedSongsArtwork.cs`. Contract §2.3 (LikedCover /
//       LikedChipArt / LikedToneAnchorUrl).
//
// 0.3 DATA: the tiles are the newest-first `Edges.Liked` prefix through `LikedCoverRules.Tiles(ReadOnlySpan<Track>)`
// (dedupe by album slot + image id). The persisted style is `Prefs.Appearance.LikedCover` (the epoch is the recompute
// trigger; the VALUE is carried in the snapshot: the 0.2.9 epoch caveat).

using System.Runtime.InteropServices;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using Contracts = Wavee.Theming.Contracts;
using ViewSlots = FluentGpu.Theming.Slots;

namespace Wavee;

public readonly partial struct User
{
    // ══ 1. THE CONTRACT SURFACE (§2.3) ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>The dynamic Liked Songs cover. <paramref name="picker"/> mounts the "Cover style" pill + flyout (the rail
    /// cover, the vertical hero); false for strips, cards, chips and rows. The geometry is a RE-PUSHED prop (audit B9: never a
    /// key built from a size): a rail drag or a tier crossing that resizes the cover keeps its nodes and re-lays the same
    /// treatment. The two arms key under DIFFERENT prefixes so a page switching arms never position-matches one onto the other.</summary>
    public static Element LikedCover(float size, float radius, bool picker, string? morphKey = null)
        => picker
            ? ViewSlots.View(Contracts.ViewKeys.LikedCoverPicker, new Contracts.LikedCoverPickerProps
            {
                Size = size, Radius = radius, MorphKey = morphKey,
                Art = LikedCover(size, radius, picker: false, morphKey),
                Flyout = static () => Embed.Comp(static () => new LikedCoverStyleFlyout()),
            }) with
            {
                Key = "liked-cover-pick:" + (morphKey ?? ""),
            }
            : Embed.Comp(new LikedArtProps(size, radius, morphKey), static () => new LikedCoverArt()) with
            {
                Key = "liked-cover:" + (morphKey ?? ""),
            };

    /// <summary>The cover's geometry, re-pushed on every parent render (equal values coalesce).</summary>
    sealed record LikedArtProps(float Size, float Radius, string? MorphKey);

    /// <summary><c>Drag.LikedChipArt</c>: the 40-DIP art box's cover — below the treatment floor, so the flat 2×2 mosaic
    /// of the newest likes (or the stock PNG). A live component element: build it ONCE at install, never per frame.</summary>
    public static Element LikedChipArt() => LikedCover(ChipArtEdge, Radii.Control, picker: false);

    const float ChipArtEdge = 40f;

    /// <summary>ch 07 §0.12: the page tone keys to the NEWEST tile when the persisted style composes a treatment, else
    /// null. Reads the liked edge and the tracks — call inside a render that subscribes <c>Edges.Liked.Changed</c>.</summary>
    public static string? LikedToneAnchorUrl()
    {
        var requested = LikedCoverRules.FromSetting(Prefs.Appearance.LikedCover(LikedCoverRules.StyleCount));
        if (requested == LikedCoverStyle.Stock) return null;
        int n = FillLikedTiles(s_anchorScratch);
        return n > 0 && LikedCoverRules.Effective(requested, n) != LikedCoverStyle.Stock ? s_anchorScratch[0] : null;
    }

    static readonly string[] s_anchorScratch = new string[LikedCoverRules.MaxTiles];

    /// <summary>The newest-first distinct liked cover urls, written into <paramref name="into"/>. UI thread.</summary>
    static int FillLikedTiles(string[] into)
    {
        var scope = Entities.Current;
        int me = scope.MeSlot;
        if (me <= Table.None) return 0;
        return LikedCoverRules.Tiles(MemoryMarshal.Cast<int, Track>(scope.Edges.Liked.Targets(me)), into);
    }

    // ══ 2. THE SNAPSHOT (one equality-gated view of everything a cover depends on) ═══════════════════════════════════

    /// <summary>Equality over the scalars and <see cref="TileKey"/>, never over <see cref="Tiles"/>: an array compares by
    /// reference, so default record equality would report "changed" on every recompute and defeat the gate (ch 07 §9).</summary>
    sealed record LikedSnapshot(LikedCoverStyle Requested, LikedCoverStyle Effective, bool WantsArt,
                                string[] Tiles, string TileKey, int TrackCount)
    {
        public static readonly LikedSnapshot Stock = new(LikedCoverStyle.Stock, LikedCoverStyle.Stock, false, [], "", 0);

        public bool Equals(LikedSnapshot? other)
            => other is not null && Requested == other.Requested && Effective == other.Effective
               && WantsArt == other.WantsArt && TrackCount == other.TrackCount
               && string.Equals(TileKey, other.TileKey, StringComparison.Ordinal);

        public override int GetHashCode() => HashCode.Combine(Requested, Effective, WantsArt, TrackCount, TileKey);
    }

    /// <summary>Read the snapshot, SUBSCRIBING the calling memo to the scope, the appearance epoch, the liked edge and the
    /// track rows. <paramref name="previous"/>'s tile array is reused when the content key did not move, so a like that
    /// does not change the newest sixteen covers allocates one string and repaints nothing.</summary>
    static LikedSnapshot ReadLikedSnapshot(LikedSnapshot? previous, string[] scratch)
    {
        _ = Entities.ScopeEpoch.Value;
        var requested = LikedCoverRules.FromSetting(Prefs.Appearance.LikedCover(LikedCoverRules.StyleCount));
        // "Would this style compose art AT ALL, given an unlimited library?" — the cost gate (E4). Only Stock says no.
        bool wantsArt = LikedCoverRules.Effective(requested, int.MaxValue) != LikedCoverStyle.Stock;
        if (!wantsArt) return new LikedSnapshot(requested, LikedCoverStyle.Stock, false, [], "", 0);

        var scope = Entities.Current;
        var liked = scope.Edges.Liked;
        _ = liked.Changed.Value;
        _ = scope.Tracks.Changed.Value;
        var (tiles, key) = KeyedTiles(scratch, previous);
        int n = tiles.Length;
        int me = scope.MeSlot;
        int count = me > Table.None && liked.State(me) == EdgeState.Complete ? liked.Count(me) : 0;
        return new LikedSnapshot(requested, LikedCoverRules.Effective(requested, n), true, tiles, key, count);
    }

    /// <summary>The current tiles and their content key (unit-separator joined). Reuses <paramref name="previous"/>'s
    /// array when the key did not move. Subscribes nothing itself.</summary>
    static (string[] Tiles, string Key) KeyedTiles(string[] scratch, LikedSnapshot? previous)
    {
        const char TileKeySeparator = (char)0x1F;
        int n = FillLikedTiles(scratch);
        string key = n == 0 ? "" : string.Join(TileKeySeparator, scratch, 0, n);
        string[] tiles = previous is not null && string.Equals(previous.TileKey, key, StringComparison.Ordinal)
            ? previous.Tiles
            : scratch.AsSpan(0, n).ToArray();
        return (tiles, key);
    }

    /// <summary>The cover's demand: the liked list once per scope when nobody answered (a failed ask waits for its Retry),
    /// then the identity bundle of the newest rows the tile scan can reach. Idempotent — auto-tracked effects re-run it as
    /// pages land.</summary>
    static void DemandLikedCover()
    {
        _ = Entities.ScopeEpoch.Value;
        var scope = Entities.Current;
        int me = scope.MeSlot;
        if (me <= Table.None) return;
        var liked = scope.Edges.Liked;
        _ = liked.Changed.Value;
        if (liked.State(me) == EdgeState.Unknown)
        {
            if (!liked.IsFailed(me)) Entities.EnsureEdge(FetchEdge.Liked, me);
            return;
        }
        var slots = liked.Targets(me);
        int take = Math.Min(slots.Length, CoverRowWindow);
        if (take > 0) Entities.Ensure(MemoryMarshal.Cast<int, Track>(slots[..take]), TrackFields.Image | TrackFields.Album);
    }

    /// <summary>How deep the tile scan may need to look: sixteen distinct albums are usually inside the newest ~50 likes;
    /// 96 bounds a pathological run of one album without making the cover a whole-library fetch.</summary>
    const int CoverRowWindow = 96;

    // ══ 3. THE COVER HOST (the snapshot, the demand; the picture is `Liked.Cover`) ═══════════════════════════════════

    /// <summary>The component that owns the cover's DATA hooks: the snapshot memo and the demand. The picture and its ambient-loop hooks
    /// are the structure's view (<c>Liked.Cover</c>), so the hook order inside it is the view's own.</summary>
    sealed class LikedCoverArt : Component
    {
        readonly string[] _scratch = new string[LikedCoverRules.MaxTiles];
        LikedSnapshot? _last;
        Memo<LikedSnapshot>? _snapshot;
        readonly Func<LikedSnapshot> _compute;
        readonly Action _demand;

        public LikedCoverArt()
        {
            _compute = () => _last = ReadLikedSnapshot(_last, _scratch);
            _demand = () => { if (_snapshot!.Value.WantsArt) DemandLikedCover(); };
        }

        public override Element Render()
        {
            var geometry = UseProps<LikedArtProps>();
            _snapshot = UseComputed(_compute);
            var snap = _snapshot.Value;
            UseEffect(_demand);
            return ViewSlots.View(Contracts.ViewKeys.LikedCover, new Contracts.LikedCoverProps
            {
                Size = geometry.Size, Radius = geometry.Radius, MorphKey = geometry.MorphKey,
                Style = snap.Effective, Tiles = snap.Tiles, TileKey = snap.TileKey, TrackCount = snap.TrackCount,
            });
        }
    }

    // ══ 4. THE PICKER'S FLYOUT HOST (the snapshot, the demand, the persisted pick; the picture is `Liked.CoverStyleFlyout`) ═══════

    /// <summary>A component so it re-renders on the appearance bump and the checked card tracks the value that PERSISTED (E15).</summary>
    sealed class LikedCoverStyleFlyout : Component
    {
        readonly string[] _scratch = new string[LikedCoverRules.MaxTiles];
        LikedSnapshot? _last;
        readonly Func<LikedSnapshot> _compute;
        readonly Action _demand;
        readonly Action<int> _apply;

        public LikedCoverStyleFlyout()
        {
            // The picker reads the tiles even while the persisted style is Stock — every miniature needs the art.
            _compute = () =>
            {
                var s = ReadLikedSnapshot(_last, _scratch);
                if (!s.WantsArt)
                {
                    _ = Entities.Current.Edges.Liked.Changed.Value;
                    _ = Entities.Current.Tracks.Changed.Value;
                    var (tiles, key) = KeyedTiles(_scratch, _last);
                    s = s with { Tiles = tiles, TileKey = key };
                }
                return _last = s;
            };
            // E4: opening the picker is the SECOND thing that charges the liked-list warm.
            _demand = DemandLikedCover;
            _apply = Apply;
        }

        public override Element Render()
        {
            var snap = UseComputed(_compute).Value;
            UseEffect(_demand);
            return ViewSlots.View(Contracts.ViewKeys.LikedCoverStyleFlyout, new Contracts.LikedCoverStyleFlyoutProps
            {
                Tiles = snap.Tiles, TileKey = snap.TileKey, TrackCount = snap.TrackCount,
                Selected = IndexOf(LikedCoverRules.PickerOrder, snap.Requested), Apply = _apply,
            });
        }

        /// <summary>Idempotent and cheap, which it MUST be: the strip fires on every keyboard rove (selection follows focus),
        /// and roving IS the preview — the cover behind the flyout repaints on the bump.</summary>
        static void Apply(int i)
        {
            var order = LikedCoverRules.PickerOrder;
            if ((uint)i >= (uint)order.Length) return;
            Prefs.Appearance.Set(Platform.Keys.LikedCoverStyle, LikedCoverRules.ToSetting(order[i]));
        }

        /// <summary>The persisted style's slot, or Stock's (a hand-edited value this build does not define reads as Stock).</summary>
        static int IndexOf(LikedCoverStyle[] order, LikedCoverStyle style)
        {
            int stock = 0;
            for (int i = 0; i < order.Length; i++)
            {
                if (order[i] == style) return i;
                if (order[i] == LikedCoverStyle.Stock) stock = i;
            }
            return stock;
        }
    }
}
