using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Wavee.Core;
using CachedExtension = Wavee.Backend.Metadata.CachedExtension;
using Xm = Wavee.Protocol.ExtendedMetadata;

namespace Wavee.Backend.Hydration;

// ── The trait projector contract (design §2.4) ───────────────────────────────────────────────────────────────────────
// A PROJECTOR owns exactly one extension kind: it says who that kind applies to, whether a resident entity already
// carries it, and how a payload folds into the store. That is the whole surface — the pipeline owns planning, the ONE
// POST, the bulk window, the negative memo and the log, so a projector never sees a transport, a chunk size or an etag.
//
// Why the split matters: the four services this replaces each re-derived "which uris do I ask for?" from their own
// prefix test and their own memo, which is how the album page came to ask for kind 185 twice and the show page for
// nothing. With planning in one place, adding a facet is one projector — not a service, a cap, a memo and a caller list.
//
// NOTE the EntityKind in this file is Wavee.Core's ROUTING kind (Track/Album/…), never Wavee.Backend.Metadata's
// persisted transport enum — which is exactly why this file aliases CachedExtension in rather than importing that
// namespace (a namespace member would shadow nothing and quietly re-bind EntityKind under it).

/// <summary>What projecting ONE kind onto ONE uri did. The pipeline turns this into the memo decision and the tally:
/// <see cref="Negative"/> and <see cref="Unchanged"/> are memoized (do not ask again this session), <see cref="Applied"/>
/// wrote, and <see cref="NotResident"/> is NEVER memoized — the row simply is not in the store yet, and the answer will
/// be wanted the moment it lands (memoizing it is how a trait silently goes missing for a session).</summary>
public enum TraitOutcome
{
    /// <summary>The payload folded into the store — a real write happened inside the batch's bulk window.</summary>
    Applied,
    /// <summary>The payload arrived and the store already agreed with it. Worth memoizing: re-asking cannot change it.</summary>
    Unchanged,
    /// <summary>The wire answered "this entity has no such extension" (404 / an empty body). Memoized for the session —
    /// the extension cache's own 24h negative TTL is the durable half of the same answer.</summary>
    Negative,
    /// <summary>There is no resident row to decorate. A trait NEVER mints an entity (a minted row is a row with no
    /// title, which every surface then paints as a placeholder), so this is a no-op that must stay re-askable.</summary>
    NotResident,
}

/// <summary>ONE extension kind's projector. Implementations live in <c>Backend/Hydration/Projectors/</c> (store-only) or
/// <c>SpotifyLive/Hydration/</c> (when the target plane is a Spotify concrete, e.g. the cover-colour plane).</summary>
public interface ITraitProjector
{
    /// <summary>The <see cref="TraitSet"/> flag a surface has to want for this projector to run.</summary>
    TraitSet Trait { get; }

    /// <summary>The extension kind this projector decodes — its identity in the plan, the memo and the tally.</summary>
    Xm.ExtensionKind Kind { get; }

    /// <summary>Extra kinds to ride the SAME uri group in the SAME POST because this projector needs them to decide
    /// (video: 182 CONSUMPTION_EXPERIENCE next to 99). They are not planned, memoized or tallied on their own — they
    /// are this projector's payload, reachable through <see cref="TraitPayloads"/>.</summary>
    ReadOnlySpan<Xm.ExtensionKind> Companions => default;

    /// <summary>Is this kind meaningful for that entity kind? Consult <see cref="TraitApplicability"/> rather than
    /// re-deriving a table — an unknown pairing is "ask once and honour the 404", never "never ask".</summary>
    bool AppliesTo(EntityKind kind);

    /// <summary>The per-kind mark: does the resident entity already carry this facet? Pure and store-only — it runs
    /// once per uri per pass on the UI's thread, so it must not allocate a query or touch a transport. Answering true
    /// is what keeps a warm page at ZERO requests.</summary>
    bool AlreadyHas(IStore store, string uri, DateTimeOffset now);

    /// <summary>Fold the payload into the store. Runs inside the page's ONE lazy bulk window (write through
    /// <see cref="TraitBatch.Write"/>, never straight at the store, or the page pays a change signal per row).</summary>
    TraitOutcome Project(TraitBatch batch, string uri, in TraitPayloads payloads);

    /// <summary>Called once after every uri in the page has been projected — the place for a follow-up that only makes
    /// sense in aggregate (video's canonical-alias recovery). Best-effort: the pipeline guards it, and a failure here
    /// never fails the batch.</summary>
    ValueTask CompleteBatchAsync(TraitBatch batch, CancellationToken ct) => default;
}

/// <summary>The write window for ONE page of a trait pass: the store, the clock, who asked, and a bulk scope that is
/// opened LAZILY on the first write. Lazy because the common warm case projects nothing at all — opening a bulk scope
/// unconditionally would publish a <see cref="StoreChange.Bulk"/> signal (and so a re-read of every subscribed surface)
/// for a pass that changed nothing.</summary>
public sealed class TraitBatch : IDisposable
{
    IDisposable? _bulk;
    bool _disposed;

    public TraitBatch(IStore store, DateTimeOffset now, TraitSurface surface, WaveeLogger log = default)
    {
        Store = store ?? throw new ArgumentNullException(nameof(store));
        Now = now;
        Surface = surface;
        Log = log;
    }

    public IStore Store { get; }
    public DateTimeOffset Now { get; }
    public TraitSurface Surface { get; }
    public WaveeLogger Log { get; }

    /// <summary>How many <see cref="Write"/>s landed. Zero means the bulk scope was never opened — the assertion the
    /// waste tests pin ("a fully warm page emits no bulk signal").</summary>
    public int Writes { get; private set; }

    /// <summary>Uris a projector wants a follow-up pass for (video's canonical aliases). The pipeline hands the list to
    /// <see cref="ITraitProjector.CompleteBatchAsync"/>; nothing else reads it.</summary>
    public List<string> FollowUp { get; } = new();

    /// <summary>Write through here, not at the store: the first call opens the bulk scope that coalesces the whole page
    /// into ONE change signal.</summary>
    public void Write(Action<IStore> write)
    {
        ArgumentNullException.ThrowIfNull(write);
        ObjectDisposedException.ThrowIf(_disposed, this);
        _bulk ??= Store.BeginBulk();
        Writes++;
        write(Store);
    }

    /// <summary>Close the bulk scope NOW (publishing its one signal) without ending the batch — a later
    /// <see cref="Write"/> simply opens a fresh one.
    ///
    /// <para>Why this exists: <c>IStore.BeginBulk</c> suppression is STORE-WIDE, not per-uri, so an open scope silences
    /// every change signal in the app — the now-playing fold, a save toggle, a playlist mutation — until it closes. That
    /// is correct for the projection sweep (which is synchronous and microseconds long) and WRONG across
    /// <see cref="ITraitProjector.CompleteBatchAsync"/>, whose video arm makes two network round trips. The service this
    /// replaced closed its bulk before recovering for exactly this reason
    /// (<c>SpotifyVideoService</c>: <c>using (bulk) {…}</c> then <c>await RecoverCanonicalAsync</c>); holding it open
    /// froze the whole UI's repaints for the length of a POST.</para></summary>
    public void FlushBulk()
    {
        if (_disposed) return;
        _bulk?.Dispose();
        _bulk = null;
    }

    /// <summary>Closes the bulk scope (publishing the one signal) iff anything was written.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _bulk?.Dispose();
        _bulk = null;
    }
}

/// <summary>One uri's slice of the batch response. A view, not a copy: it holds the whole page's dictionary plus the
/// uri, so handing a projector its payloads costs nothing per row.</summary>
public readonly struct TraitPayloads
{
    readonly IReadOnlyDictionary<(string Uri, Xm.ExtensionKind Kind), CachedExtension>? _values;
    readonly string _uri;

    public TraitPayloads(IReadOnlyDictionary<(string Uri, Xm.ExtensionKind Kind), CachedExtension> values, string uri)
    {
        _values = values;
        _uri = uri;
    }

    /// <summary>The uri these payloads belong to.</summary>
    public string Uri => _uri ?? "";

    /// <summary>The wire's answer for a kind, or null when it did not answer at all. ABSENT IS NOT MISSING — the
    /// extension cache deliberately refuses to invent a negative for a key the response omitted, and a projector must
    /// keep that distinction (an invented negative is a 24h wedge).</summary>
    public CachedExtension? Get(Xm.ExtensionKind kind)
        => _values is not null && _values.TryGetValue((Uri, kind), out var value) ? value : null;

    /// <summary>Did the wire answer at all for this kind (payload or explicit negative)?</summary>
    public bool HasAnswer(Xm.ExtensionKind kind) => _values is not null && _values.ContainsKey((Uri, kind));

    /// <summary>An EXPLICIT negative: the entity has no such extension (404 / empty body).</summary>
    public bool Missing(Xm.ExtensionKind kind) => Get(kind) is { Missing: true };

    /// <summary>The decodable body, or null for "no answer" and for an explicit negative.</summary>
    public ByteString? Payload(Xm.ExtensionKind kind)
        => Get(kind) is { Missing: false, Payload: { IsEmpty: false } payload } ? payload : null;
}

/// <summary>THE kind → entity-kind applicability table, pinned from the wire probe (docs/plans/wavee/xm-kind-probe-overview.md
/// via design §2.4). Two rules encoded here and nowhere else:
/// <list type="bullet">
/// <item>a kind that the probe showed answering for an entity kind is ASKED — no prefix test re-derives it;</item>
/// <item>a pairing the probe never covered is <b>ask-once</b> (true), because the 404 is the cheap authoritative answer
/// and the negative memo makes it cost exactly one request per session. Guessing "never" is what left every episode in
/// the app without a single trait.</item>
/// </list></summary>
public static class TraitApplicability
{
    /// <summary>Is <paramref name="kind"/> worth asking for an entity of <paramref name="entity"/> kind?</summary>
    public static bool Applies(Xm.ExtensionKind kind, EntityKind entity) => kind switch
    {
        // Track facets: 99/182 video, 222 tempo/key, 6 descriptors, 185 play counts. Pinned for tracks; EPISODES are
        // ask-once (the probe never covered them, and an episode with a trailer video is exactly the case that would be
        // invisible forever under a "tracks only" guess). Everything else — an album, an artist, a playlist — is a hard
        // no: these are per-playable facts and asking for them by the hundred is pure waste.
        Xm.ExtensionKind.VideoAssociations or Xm.ExtensionKind.ConsumptionExperienceTrait
            or Xm.ExtensionKind.AudioAttributesV2 or Xm.ExtensionKind.TrackDescriptor
            or Xm.ExtensionKind.OnPlatformReputationTrait
            => entity is EntityKind.Track or EntityKind.Episode,

        // 179 tints ANY card that has an image — the recents grid asks it for albums, artists and shows alike.
        Xm.ExtensionKind.VisualIdentityTrait => entity is not EntityKind.Unknown,

        // 183 carries ©/℗ and the calendar release date: an ALBUM fact. A track's copyright line comes from its album.
        Xm.ExtensionKind.PublishingMetadataTrait => entity is EntityKind.Album,

        // 178/220 are wire-fidelity asks (nothing is projected), so they follow whatever the surface is showing.
        Xm.ExtensionKind.IdentityTrait or Xm.ExtensionKind.EntityTypeTrait => entity is not EntityKind.Unknown,

        // A kind nobody has tabulated yet: ask once, honour the 404. Unknown is still excluded — an unroutable uri has
        // no entity to decorate at all.
        _ => entity is not EntityKind.Unknown,
    };
}
