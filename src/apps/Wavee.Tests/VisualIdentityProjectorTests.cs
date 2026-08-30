using System;
using System.Collections.Generic;
using System.IO;
using Google.Protobuf;
using Wavee.Backend;
using Wavee.Backend.Hydration;
using Wavee.Backend.Metadata;
using Wavee.Core;
using Wavee.SpotifyLive;
using Wavee.SpotifyLive.Hydration;
using Xunit;
using Va = Wavee.Protocol.ContentAgnostic;
using Xm = Wavee.Protocol.ExtendedMetadata;

// EntityKind: the ONE uri vocabulary (Wavee.Core), not Backend.Metadata's thin transport projection of it.
using EntityKind = Wavee.Core.EntityKind;

namespace Wavee.Tests;

// ── The kind-179 projector (design §2.4), from SpotifyTrackAdornmentService.FeedColors/Pack ───────────────────────────
// Two properties matter and neither is about the store: the grading is keyed by the IMAGE the payload names (so one
// track's answer tints its album's grid card too), and the "already asked" mark is a PURE probe of the plane — the one
// place a mark could accidentally enqueue a getDynamicColorsByUris batch for every warm row on the page.
public class VisualIdentityProjectorTests
{
    static string TempFile() => Path.Combine(Path.GetTempPath(), "wavee-colors-" + Guid.NewGuid().ToString("N") + ".json");

    const string Small = "https://i.scdn.co/image/ab67616d00004851e86f30ec6f14a30f1cf9bb9d";
    const string Large = "https://i.scdn.co/image/ab67616d0000b273e86f30ec6f14a30f1cf9bb9d";

    static CoverColorPlane.Scheme Dark => new(0xFF101040u, 0xFF3C4478u, 0xFFFFFFFFu, 0xFFB3B3B3u, 0xFFFFFFFFu);

    // ── HasFreshDark: the mark ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void HasFreshDark_IsTrueOnlyForAFreshNonNegativeDarkGrading()
    {
        long now = 1_000_000;
        var plane = new CoverColorPlane(TempFile(), () => now);

        Assert.False(plane.HasFreshDark(Large));   // nothing yet
        Assert.False(plane.HasFreshDark(null));
        Assert.False(plane.HasFreshDark(""));

        plane.SetDark(Large, Dark);
        Assert.True(plane.HasFreshDark(Large));
        Assert.True(plane.HasFreshDark(Small));    // size-independent: one 179 payload answers for every size

        // A cover the colour server declined is NOT an answer for kind 179 — a 179 payload can still arrive for it.
        var negative = new CoverColorPlane(TempFile(), () => now);
        negative.SetGraded(CoverColorPlane.KeyForUrl(Large), null);
        Assert.False(negative.HasFreshDark(Large));
    }

    [Fact]
    public void HasFreshDark_NeverEnqueuesTheImageForTheFiller()
    {
        // The whole reason it exists: a planning question must not become a request. TryGetTint's miss DOES enqueue —
        // that is the render path's demand-driven fill — so probing with it would make every warm page fetch colours.
        var plane = new CoverColorPlane(TempFile());
        var asked = new List<IReadOnlyList<string>>();
        plane.Filler = (ids, _) =>
        {
            asked.Add(ids);
            return System.Threading.Tasks.Task.FromResult<IReadOnlyList<CoverColorPlane.GradedColors?>>(
                new CoverColorPlane.GradedColors?[ids.Count]);
        };

        Assert.False(plane.HasFreshDark(Large));
        Assert.Empty(asked);
    }

    // ── the projection ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Project_WritesTheGradingUnderEveryImageThePayloadNames_NotTheEntityUri()
    {
        var plane = new CoverColorPlane(TempFile());
        var (store, batch, p) = Build(plane);

        Assert.Equal(TraitOutcome.Applied, p.Project(batch, "spotify:track:a", Answer("spotify:track:a", Small, Large)));

        Assert.True(plane.HasFreshDark(Large));
        var scheme = plane.TryGetScheme(Large, lightTheme: false);
        Assert.NotNull(scheme);
        Assert.Equal(0xFF101040u, scheme!.Value.BackgroundBase);   // colors.base.background_base, NOT colors.flat
        Assert.Equal(0xFF3C4478u, scheme.Value.BackgroundTintedBase);
        Assert.Equal(0, batch.Writes);                             // the plane is not the store: no bulk scope opens
        Assert.Null(store.GetTrack("spotify:track:a"));            // and nothing is minted
    }

    [Fact]
    public void Project_APayloadWithNoBaseSchemeIsNegative()
    {
        var (_, batch, p) = Build(new CoverColorPlane(TempFile()));
        var msg = new Va.VisualIdentityTrait { VisualIdentity = new Va.VisualIdentity() };
        msg.VisualIdentity.Images.Add(new Va.ImageEntry { Image = new Va.ImageRef { Url = Large } });

        Assert.Equal(TraitOutcome.Negative, p.Project(batch, "spotify:track:a", Raw("spotify:track:a", msg.ToByteString())));
    }

    [Fact]
    public void Project_AColourWithNoImageToKeyItOnIsNegative()
    {
        // The grading is IMAGE-keyed by construction. A payload that carries colours and names no image has nowhere to
        // put them — an entity-keyed copy is exactly what used to leave album grids grey.
        var (_, batch, p) = Build(new CoverColorPlane(TempFile()));
        Assert.Equal(TraitOutcome.Negative, p.Project(batch, "spotify:track:a", Answer("spotify:track:a")));
    }

    [Fact]
    public void Project_A404IsNegative_AndAnUnansweredUriIsNot()
    {
        var (_, batch, p) = Build(new CoverColorPlane(TempFile()));
        Assert.Equal(TraitOutcome.Negative, p.Project(batch, "spotify:track:a", Raw("spotify:track:a", null)));
        Assert.Equal(TraitOutcome.NotResident, p.Project(batch, "spotify:track:a",
            new TraitPayloads(new Dictionary<(string, Xm.ExtensionKind), CachedExtension>(), "spotify:track:a")));
    }

    // ── the mark, per entity kind ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AlreadyHas_ReadsTheRowsOwnCoverUrl_PerKind()
    {
        var plane = new CoverColorPlane(TempFile());
        plane.SetDark(Large, Dark);
        var store = new InMemoryStore();
        var p = new VisualIdentityProjector(() => plane);
        var now = DateTimeOffset.UtcNow;

        // No row at all, and a row with no cover yet: false — the 179 payload is exactly what supplies one.
        Assert.False(p.AlreadyHas(store, "spotify:track:a", now));
        store.UpsertTrack(new Track("a", "spotify:track:a", "T", [], new AlbumRef("", "", ""), 1, false, null));
        Assert.False(p.AlreadyHas(store, "spotify:track:a", now));

        store.UpsertTrack(new Track("b", "spotify:track:b", "T", [], new AlbumRef("", "", ""), 1, false, new Image(Small)));
        store.UpsertAlbum(new Album("al", "spotify:album:al", "A", new Image(Large), [], 2014, 0));
        store.UpsertArtist(new Artist("ar", "spotify:artist:ar", "A", new Image(Large)));
        Assert.True(p.AlreadyHas(store, "spotify:track:b", now));   // the small size resolves to the same identity
        Assert.True(p.AlreadyHas(store, "spotify:album:al", now));
        Assert.True(p.AlreadyHas(store, "spotify:artist:ar", now));
    }

    [Fact]
    public void AlreadyHas_IsFalseWithNoPlaneInstalled()
    {
        // Offline / logged out / tests: no plane means nothing to mark, and the projector simply answers NotResident
        // when a payload arrives.
        var store = new InMemoryStore();
        store.UpsertTrack(new Track("b", "spotify:track:b", "T", [], new AlbumRef("", "", ""), 1, false, new Image(Large)));
        var p = new VisualIdentityProjector(static () => null);
        Assert.False(p.AlreadyHas(store, "spotify:track:b", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void AppliesTo_IsEveryRoutableKind()
    {
        // 179 tints ANY card that has an image — the recents grid asks it for albums, artists and shows alike.
        var p = new VisualIdentityProjector(static () => null);
        foreach (var kind in new[] { EntityKind.Track, EntityKind.Episode, EntityKind.Album, EntityKind.Artist,
                                     EntityKind.Playlist, EntityKind.Show })
            Assert.True(p.AppliesTo(kind));
        Assert.False(p.AppliesTo(EntityKind.Unknown));
        Assert.Equal(TraitSet.VisualIdentity, p.Trait);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────────────────────

    static (InMemoryStore Store, TraitBatch Batch, VisualIdentityProjector P) Build(CoverColorPlane plane)
    {
        var store = new InMemoryStore();
        return (store, new TraitBatch(store, DateTimeOffset.UtcNow, TraitSurface.Recents),
                new VisualIdentityProjector(() => plane));
    }

    static TraitPayloads Answer(string uri, params string[] imageUrls)
    {
        var identity = new Va.VisualIdentity
        {
            Colors = new Va.ColorSet
            {
                Base = new Va.ColorScheme
                {
                    BackgroundBase = Rgba(0x10, 0x10, 0x40),
                    BackgroundTintedBase = Rgba(0x3C, 0x44, 0x78),
                    TextBase = Rgba(0xFF, 0xFF, 0xFF),
                    TextSubdued = Rgba(0xB3, 0xB3, 0xB3),
                    TextBrightAccent = Rgba(0xFF, 0xFF, 0xFF),
                },
            },
        };
        foreach (var url in imageUrls) identity.Images.Add(new Va.ImageEntry { Image = new Va.ImageRef { Url = url } });
        return Raw(uri, new Va.VisualIdentityTrait { VisualIdentity = identity }.ToByteString());
    }

    static Va.Rgba Rgba(uint r, uint g, uint b) => new() { R = r, G = g, B = b, A = 255 };

    static TraitPayloads Raw(string uri, ByteString? payload)
    {
        var map = new Dictionary<(string Uri, Xm.ExtensionKind Kind), CachedExtension>
        {
            [(uri, Xm.ExtensionKind.VisualIdentityTrait)] =
                new(uri, Xm.ExtensionKind.VisualIdentityTrait, null, 0, payload, Missing: payload is null),
        };
        return new TraitPayloads(map, uri);
    }
}
