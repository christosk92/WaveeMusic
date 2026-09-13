using System;
using System.Collections.Generic;
using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using Wavee.Core;

namespace Wavee;

/// <summary>The dynamic Liked Songs cover: the persisted treatment, composed from the newest likes, degrading to the
/// bundled PNG whenever the library cannot honestly feed it.
///
/// <para>This is the ONLY component in the feature — the treatments and the rules are static, the palette work lives
/// in leaves — so every hook the Liked cover owns is in this file, declared unconditionally, in one order, on every
/// path. That matters more than usual here: what renders depends on a persisted enum, on a lazily-loaded list and on
/// a size, and a hook behind any of those three would shift hook order the first time a user picked a style.</para>
///
/// <para><b>Geometry is FROZEN at mount</b> (component-props-contract.md): size / radius / morph key are constructor
/// fields, and <see cref="LikedSongsArtwork.Dynamic"/> puts all three in the element <c>Key</c> — so a rail-width
/// change REMOUNTS this component rather than silently keeping the first size. Everything that can change while
/// mounted (the style, the likes, the gradings) arrives through signals instead.</para></summary>
sealed class LikedCoverArt : Component
{
    /// <summary>One equality-gated view of everything the cover depends on.
    ///
    /// <para><b>The Epoch caveat</b> (DetailTracks.cs's contract, which this file is the second consumer of):
    /// <c>AppearancePrefs.Epoch</c> is only the RECOMPUTE TRIGGER — the settings store is not observable — so the
    /// value actually read from it has to be CARRIED here. A memo that merely touched the epoch would recompute on
    /// every appearance bump, compare equal, and never propagate the style change it was bumped for.</para>
    ///
    /// <para>Equality is declared over the scalars and <see cref="TileKey"/> rather than over
    /// <see cref="Tiles"/>: an array compares by reference, so the default record equality would report "changed" on
    /// every recompute and defeat the gate entirely. With the key, a like that does not alter the newest sixteen
    /// covers (liking a second track from an album already on the cover) costs one comparison and repaints
    /// nothing.</para></summary>
    internal sealed record Snapshot(LikedCoverStyle Requested, LikedCoverStyle Effective, bool WantsArt,
                                    string[] Tiles, string TileKey, int TrackCount)
    {
        public bool Equals(Snapshot? other)
            => other is not null && Requested == other.Requested && Effective == other.Effective
               && WantsArt == other.WantsArt && TrackCount == other.TrackCount
               && string.Equals(TileKey, other.TileKey, StringComparison.Ordinal);

        public override int GetHashCode() => HashCode.Combine(Requested, Effective, WantsArt, TrackCount, TileKey);
    }

    static readonly string[] NoTiles = [];

    readonly float _size;
    readonly float _radius;
    readonly string? _morphKey;

    // The ambient-loop nodes, as INSTANCE state rather than hooks: the sinks below are allocated once per component
    // instead of once per render, which is what keeps a treatment swap off the allocation profile. THREE, because
    // Marquee runs three independently-timed bands and each band's TranslateX track is keyed to its own row node
    // (Wall uses only the first). Fixed fields rather than an array, so the widest treatment costs three delegates for
    // the life of the component and nothing per frame.
    NodeHandle _loopA, _loopB, _loopC;
    readonly Action<NodeHandle> _sinkA, _sinkB, _sinkC;

    public LikedCoverArt(float size, float radius, string? morphKey)
    {
        _size = size; _radius = radius; _morphKey = morphKey;
        _sinkA = h => _loopA = h;
        _sinkB = h => _loopB = h;
        _sinkC = h => _loopC = h;
    }

    public override Element Render()
    {
        var svc = UseContext(Services.Slot);
        // The store CONTEXT first, then the services' own reference as the fallback — the off-page resolution the rest
        // of the app already uses (Services.LibraryStore). This component is mounted from surfaces that are not pages:
        // a context-menu header, a drag chip, an overlay flyout. Those render under the overlay host, and requiring
        // every such host to re-provide LibraryStore.Slot is exactly the "every page needs special wiring" trap that
        // left the cover stock wherever the wiring was missed. Same instance either way (Services builds it), so the
        // fallback changes nothing where the context IS present.
        var store = UseContext(LibraryStore.Slot) ?? svc?.LibraryStore;

        var snapshot = UseComputed(() =>
        {
            var requested = AppearancePrefs.LikedCover(svc?.Settings);
            // "Would this style compose art AT ALL, given an unlimited library?" — the cost gate (E4), asked through
            // the rules so it stays correct without this file knowing which styles are art-backed. Only Stock answers
            // no, so only a Stock-pinned user pays nothing; every other style, the shipped Lens default included,
            // charges the liked-list warm because it genuinely needs the art.
            bool wantsArt = LikedCoverRules.Effective(requested, int.MaxValue) != LikedCoverStyle.Stock;
            if (!wantsArt)
                return new Snapshot(requested, LikedCoverStyle.Stock, false, NoTiles, "", 0);

            // Reading the Loadable SUBSCRIBES: a like/unlike refreshes this cell IN PLACE (no Pending flip), so the
            // cover recomposes from the new newest-first list without a skeleton flash (E8). Still Pending ⇒ an empty
            // list ⇒ no tiles ⇒ Stock, which is exactly what the app paints today (E2).
            var tracks = store?.Liked.Value.Value ?? (IReadOnlyList<Track>)Array.Empty<Track>();
            var tiles = LikedCoverRules.Tiles(tracks);
            var arr = tiles as string[] ?? ToArray(tiles);
            return new Snapshot(requested, LikedCoverRules.Effective(requested, arr.Length), true,
                                arr, string.Join('', arr), tracks.Count);
        });

        var snap = snapshot.Value;
        // Liked is deliberately NOT in LibraryStore.WarmCheap — it is the one large collection — so a consumer has to
        // ask. Idempotent (a guarded one-shot) and asynchronous, so this is not a write during render.
        if (snap.WantsArt) store?.EnsureLiked();

        bool hasLoops = LikedCoverTreatments.HasLoops(snap.Effective) && _size >= LikedCoverTreatments.BadgeMinSize;

        // Wired here rather than inside OnRealized because the handles are only valid AFTER realize, and because the
        // scene-liveness check (HeroBell's contract) needs a scene. Deps carry the style, the art and the size, so a
        // treatment swap re-seeds and a mere repaint does not. Looping tracks quiesce by themselves under a parked
        // page (E17) — there is nothing to tear down on nav away.
        UseLayoutEffect(() =>
        {
            if (!hasLoops) return;
            if (Context.Anim is not { } anim || Context.Scene is not { } scene) return;
            var a = !_loopA.IsNull && scene.IsLive(_loopA) ? _loopA : default;
            var b = !_loopB.IsNull && scene.IsLive(_loopB) ? _loopB : default;
            var c = !_loopC.IsNull && scene.IsLive(_loopC) ? _loopC : default;
            LikedCoverTreatments.SeedLoops(snap.Effective, anim, a, b, c);
        }, DepKey.From(HashCode.Combine((int)snap.Effective, snap.TileKey, (int)_size)));

        // ── the site ladder ───────────────────────────────────────────────────────────────────────────────────────
        // WHICH of the four things a cover of this size paints is a PURE decision, and it is now owned by
        // LikedCoverRules.Site with Theory rows behind it — because it is asked at every size in the app (a 304 rail
        // hero, a 150 shelf tile, a 38 menu header, a 20 sidebar row) and a floor that drifted between two of them
        // would be invisible until a user reported exactly the bug this pass is fixing.
        //
        // A 48-DIP Wall is thirty-six unreadable specks and an ambient loop with no reader; a sidebar row of them is
        // cost with no message. So below the treatment floor the answer is the app's ordinary 2x2 collection mosaic —
        // the same shape a cover-less playlist already shows everywhere — and Tone, which carries no art and reads
        // perfectly small, keeps its own gradient and heart.
        switch (LikedCoverRules.Site(snap.Effective, _size, snap.Tiles.Length, LikedCoverTreatments.BadgeMinSize))
        {
            case LikedCoverSite.Stock:
                return LikedSongsArtwork.Cover(_size, _radius, _morphKey);
            case LikedCoverSite.MiniTone:
                return LikedCoverTreatments.Build(LikedCoverStyle.Tone, snap.Tiles, snap.TileKey, snap.TrackCount,
                                                  _size, _radius, mini: true, _morphKey);
            case LikedCoverSite.MiniMosaic:
                return Surfaces.Mosaic(snap.Tiles, _size, _size, _radius) with { MorphId = _morphKey };
            default:
                return LikedCoverTreatments.Build(
                    snap.Effective, snap.Tiles, snap.TileKey, snap.TrackCount, _size, _radius, mini: false, _morphKey,
                    hasLoops ? new LikedCoverTreatments.Loops(_sinkA, _sinkB, _sinkC) : default);
        }
    }

    static string[] ToArray(IReadOnlyList<string> tiles)
    {
        if (tiles.Count == 0) return NoTiles;
        var arr = new string[tiles.Count];
        for (int i = 0; i < arr.Length; i++) arr[i] = tiles[i];
        return arr;
    }
}
