using System;
using Google.Protobuf;
using Wavee.Backend;
using Wavee.Backend.Hydration;
using Wavee.Core;
using Va = Wavee.Protocol.ContentAgnostic;
using Xm = Wavee.Protocol.ExtendedMetadata;

namespace Wavee.SpotifyLive.Hydration;

// ── Kind 179 VISUAL_IDENTITY_TRAIT → CoverColorPlane (design §2.4) ───────────────────────────────────────────────────
// Moved verbatim from SpotifyTrackAdornmentService.FeedColors/Pack. It lives in SpotifyLive rather than Backend for one
// reason: its target plane is a Spotify concrete (the cover-colour table), not the store — every other projector writes
// rows and stays engine/provider-neutral in Backend/Hydration/Projectors.
//
// Why the trait matters: kind 179 carries the colour in the same payload as the image URLs, so a placeholder can be
// tinted BEFORE any image byte arrives. That is the difference between a list of blank grey squares and a list that
// paints its covers immediately.
//
// It is keyed by the IMAGE the payload itself carries, never by the entity uri — that pairing is the whole point of the
// trait: one track's response also tints its album's grid card, its playlist's hero and every other slot showing the
// same cover. An entity-keyed copy is what used to leave album grids grey while the track list beside them was colour.

/// <summary>The kind-179 projector: dominant-colour grading for whatever cover an entity's payload names.</summary>
public sealed class VisualIdentityProjector : ITraitProjector
{
    static readonly MessageParser<Va.VisualIdentityTrait> VisualParser =
        Va.VisualIdentityTrait.Parser.WithDiscardUnknownFields(true);

    readonly Func<CoverColorPlane?> _plane;

    /// <param name="plane">The ambient plane, resolved per pass. A Func because the plane is installed by the live
    /// session (and absent in tests / offline), and this projector is constructed once at go-live.</param>
    public VisualIdentityProjector(Func<CoverColorPlane?> plane) => _plane = plane ?? throw new ArgumentNullException(nameof(plane));

    public TraitSet Trait => TraitSet.VisualIdentity;
    public Xm.ExtensionKind Kind => Xm.ExtensionKind.VisualIdentityTrait;

    public bool AppliesTo(EntityKind kind) => TraitApplicability.Applies(Kind, kind);

    /// <summary>The mark is on the PLANE, not the row: the resident entity's own cover url, probed for a fresh dark
    /// grading. A row with no cover url yet answers false — its 179 payload is exactly what would supply one, and the
    /// entity-level ask is how the tint arrives before the image does.</summary>
    public bool AlreadyHas(IStore store, string uri, DateTimeOffset now)
        => _plane() is { } plane && CoverUrl(store, uri) is { Length: > 0 } url && plane.HasFreshDark(url);

    public TraitOutcome Project(TraitBatch batch, string uri, in TraitPayloads payloads)
    {
        var payload = payloads.Payload(Kind);
        if (payload is null)
            return payloads.HasAnswer(Kind) ? TraitOutcome.Negative : TraitOutcome.NotResident;
        if (_plane() is not { } plane) return TraitOutcome.NotResident;   // no plane installed (offline / tests)

        try
        {
            var identity = VisualParser.ParseFrom(payload)?.VisualIdentity;
            // Takes colors.base.background_base — NOT colors.flat, which is a light desaturated accent (#ACB8F5 for a
            // navy cover) where background_base is the dominant tone (#101040) an art placeholder wants. The schemes
            // are DARK-only (base/darker/darkest are elevation levels — see visual_identity_trait.proto), so the plane
            // files this as a dark grading and light theme waits for getDynamicColorsByUris.
            var scheme = identity?.Colors?.Base;
            if (identity is null || scheme?.BackgroundBase is null) return TraitOutcome.Negative;

            var graded = new CoverColorPlane.Scheme(
                Pack(scheme.BackgroundBase), Pack(scheme.BackgroundTintedBase), Pack(scheme.TextBase),
                Pack(scheme.TextSubdued), Pack(scheme.TextBrightAccent));

            bool any = false;
            foreach (var entry in identity.Images)
            {
                if (entry?.Image?.Url is not { Length: > 0 } url) continue;
                // NOT batch.Write: the plane is not the store, so there is no bulk scope to open and no store change
                // signal to coalesce — it publishes its own Epoch bump once per landed batch.
                plane.SetDark(url, graded);
                any = true;
            }
            return any ? TraitOutcome.Applied : TraitOutcome.Negative;
        }
        catch (InvalidProtocolBufferException ex)
        {
            // One malformed entity must not sink the page — the other 299 rows still get their colour.
            batch.Log.Event(WaveeLogLevel.Warning, "traits.179.parse", "visual-identity parse failed", uri, ex: ex);
            return TraitOutcome.Negative;
        }
    }

    /// <summary>The resident row's own cover url, per kind — the one thing the plane can be probed with before a
    /// payload lands. Null when the entity is not resident or carries no image yet.</summary>
    static string? CoverUrl(IStore store, string uri) => EntityUri.KindOf(uri) switch
    {
        EntityKind.Track => store.GetTrack(uri)?.Image?.Url,
        EntityKind.Episode => store.GetEpisode(uri)?.Image?.Url,
        EntityKind.Album => store.GetAlbum(uri)?.Cover?.Url,
        EntityKind.Artist => store.GetArtist(uri)?.Image?.Url,
        EntityKind.Playlist => store.GetPlaylist(uri)?.Cover?.Url,
        EntityKind.Show => store.GetShow(uri)?.Cover?.Url,
        _ => null,
    };

    // Opaque ARGB, the packing CoverColorPlane stores. SpotifyColor owns the clamping + the alpha-0-means-unspecified
    // rule so every surface packs colour identically.
    /// <summary>A role's RGBA → opaque ARGB; 0 when the server omitted that role (the plane treats 0 as "absent").</summary>
    static uint Pack(Va.Rgba? c) => c is null ? 0u : SpotifyColor.Pack(c.R, c.G, c.B, c.A);
}
