// ── Wavee.Tests/XmAnswerSummaryTests.cs — the per-kind account of one extended-metadata answer (evidence, 2026-09-25) ──
//
// `XmAnswerSummary` is what the always-on `fetch.xm` line and the `wavee://diag?cmd=xm` probe print: per asked kind, how
// many asked entities came back with a payload, with nothing, with a failed status, or not at all — and, for the trait
// kinds whose "nothing" is a value, whether the payload said yes or no. Pure over bytes; the facts build real
// BatchedExtensionResponse bytes with the generated protocol types, the same way DecodeTests does.

using Google.Protobuf;
using Wavee;
using Xunit;
using Xm = Wavee.Protocol.ExtendedMetadata;

namespace Wavee.Tests;

public class XmAnswerSummaryTests
{
    const string A = "spotify:track:0000000000000000000001", B = "spotify:track:0000000000000000000002",
                 C = "spotify:track:0000000000000000000003";

    static Xm.EntityExtensionData Entity(string uri, byte[]? payload, int status = 200)
    {
        var e = new Xm.EntityExtensionData { Header = new Xm.EntityExtensionDataHeader { StatusCode = status }, EntityUri = uri };
        if (payload is not null)
            e.ExtensionData = new Google.Protobuf.WellKnownTypes.Any { TypeUrl = "type.googleapis.com/x", Value = ByteString.CopyFrom(payload) };
        return e;
    }

    static Xm.EntityExtensionDataArray Array(Xm.ExtensionKind kind, params Xm.EntityExtensionData[] entities)
    {
        var a = new Xm.EntityExtensionDataArray { ExtensionKind = kind };
        a.ExtensionData.AddRange(entities);
        return a;
    }

    static byte[] Response(params Xm.EntityExtensionDataArray[] arrays)
    {
        var r = new Xm.BatchedExtensionResponse();
        r.ExtendedMetadata.AddRange(arrays);
        return r.ToByteArray();
    }

    static byte[] Plays(byte value) => [3 << 3, value];

    [Fact]
    public void Each_asked_kind_is_counted_over_the_asked_uris()
    {
        byte[] body = Response(
            Array(Xm.ExtensionKind.OnPlatformReputationTrait, Entity(A, Plays(90)), Entity(B, Plays(0))),
            Array(Xm.ExtensionKind.VideoAssociations, Entity(A, [])));   // A: 2xx, zero-length payload; B, C omitted

        var s = XmAnswerSummary.Read(body, [A, B, C, A], [FetchRoutes.PlayCount, FetchRoutes.VideoAssociations]);

        var plays = s.Tally[0];
        Assert.Equal(FetchRoutes.PlayCount, plays.Kind);
        Assert.Equal(3, plays.Asked);                                  // the repeated A is asked once
        Assert.Equal(2, plays.Returned);
        Assert.Equal(1, plays.Positive);
        Assert.Equal(1, plays.Zero);                                   // B said zero plays
        Assert.Equal(1, plays.Omitted);                                // C was left out

        var video = s.Tally[1];
        Assert.Equal(1, video.Empty);
        Assert.Equal(2, video.Omitted);
        Assert.Equal("185:" + C, s.Named(XmAnswerSummary.Verdict.Omitted, 1).Split(';')[0]);
        Assert.Equal("185:" + B, s.Named(XmAnswerSummary.Verdict.Zero));
    }

    [Fact]
    public void A_non_2xx_entity_is_failed_and_a_bare_header_is_empty()
    {
        byte[] body = Response(Array(Xm.ExtensionKind.OnPlatformReputationTrait,
            Entity(A, Plays(5), status: 503), Entity(B, payload: null)));

        var s = XmAnswerSummary.Read(body, [A, B], [FetchRoutes.PlayCount]);

        Assert.Equal(1, s.Tally[0].Failed);
        Assert.Equal(1, s.Tally[0].Empty);
        Assert.Equal(0, s.Tally[0].Returned);
    }

    [Theory]
    [InlineData(FetchRoutes.PlayCount, new byte[] { 3 << 3, 0 }, true)]
    [InlineData(FetchRoutes.PlayCount, new byte[] { 3 << 3, 7 }, false)]
    [InlineData(FetchRoutes.ConsumptionExperience, new byte[] { 0x22, 2, 1, 4 }, true)]          // audio: 01 04
    [InlineData(FetchRoutes.ConsumptionExperience, new byte[] { 0x22, 3, 1, 2, 4 }, false)]      // video: 01 02 04
    [InlineData(FetchRoutes.VideoAssociations, new byte[] { 0x0A, 0 }, true)]                     // an empty association
    [InlineData(FetchRoutes.VideoAssociations, new byte[] { 0x0A, 3, 0x0A, 1, 0x41 }, false)]     // associated_uri "A"
    [InlineData(FetchRoutes.TrackDescriptor, new byte[] { }, true)]
    [InlineData(FetchRoutes.TrackV4, new byte[] { 1, 2, 3 }, false)]                               // no trait verdict
    public void The_trait_verdicts_read_the_decoders_fields(int kind, byte[] payload, bool zero)
        => Assert.Equal(zero, XmAnswerSummary.IsZero(kind, payload));

    [Fact]
    public void The_probe_tsv_has_a_row_per_asked_kind_and_uri_with_the_payload()
    {
        byte[] body = Response(Array(Xm.ExtensionKind.OnPlatformReputationTrait, Entity(A, Plays(90))));
        var s = XmAnswerSummary.Read(body, [A, B], [FetchRoutes.PlayCount], keepPayloads: true);
        string[] lines = s.ToTsv().TrimEnd('\n').Split('\n');
        Assert.Equal(3, lines.Length);
        Assert.StartsWith("kind\turi\tverdict", lines[0]);
        Assert.Equal("185\t" + A + "\tPositive\t200\t2\t185A", lines[1]);
        Assert.Equal("185\t" + B + "\tOmitted\t0\t0\t", lines[2]);
    }
}
