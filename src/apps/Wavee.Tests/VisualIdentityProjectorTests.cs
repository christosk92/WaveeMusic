using System;
using System.Collections.Generic;
using System.IO;
using Google.Protobuf;
using Wavee.Backend;
using Wavee.Backend.Metadata;
using Wavee.Core;
using Wavee.SpotifyLive;
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
public class CoverColorPlaneProbeTests
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

}
