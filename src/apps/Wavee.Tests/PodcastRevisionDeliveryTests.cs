using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Wavee;
using Xunit;
using Col = Wavee.Protocol.Collection;
using Ev = Wavee.Protocol.EventSender;
using Rs = Wavee.Protocol.Resumption;

namespace Wavee.Tests;

// Re-encoded vc4 shapes. All URIs and revision ids are synthetic; no archive or account data is shipped.
public class PodcastRevisionDeliveryTests
{
    const string EpisodeUri = "spotify:episode:0Q86acNRm6V9GYx55SXKwf";

    static Rs.CurrentStateRevision Revision(int created, int nanos, bool marker = false)
        => new()
        {
            Value = marker ? new Rs.CurrentStateValue { EntityUri = EpisodeUri, Marker8 = new Rs.StateMarker() }
                : new Rs.CurrentStateValue { EntityUri = EpisodeUri, ResumePoint = new Duration { Seconds = 436, Nanos = 770_000_000 } },
            CreateTime = new Timestamp { Seconds = created },
            UpdateTime = new Timestamp { Seconds = 1789823110, Nanos = nanos },
        };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Grouped_batch_keeps_both_revisions_and_uses_creation_time_to_break_equal_server_timestamps(bool markerLast)
    {
        var marker = Revision(1789823105, 707_000_000, true);
        var position = Revision(1789823109, 707_000_000);
        byte[] markerBytes = marker.ToByteArray(), positionBytes = position.ToByteArray();
        // Wire result = {1 URI, 2 {1 revision, 1 revision}}. A singular field would merge and lose one revision.
        byte[] response = HerodotusWire.Len(1,
            HerodotusWire.Len(1, HerodotusWire.Ascii(EpisodeUri)),
            HerodotusWire.Len(2,
                HerodotusWire.Len(1, markerLast ? positionBytes : markerBytes),
                HerodotusWire.Len(1, markerLast ? markerBytes : positionBytes)));
        var decoded = Spotify.Telemetry.DecodeProgressPush(response, batch: true);
        Assert.Equal(2, decoded.States[0].Revisions.Count);
        var newest = Spotify.Telemetry.CurrentRevisionOf(decoded.States[0]);
        Assert.Equal(436_770, Spotify.Telemetry.PositionMsOf(newest!.Value.ResumePoint));
    }

    [Fact]
    public void Sub_millisecond_server_precision_is_not_lost()
    {
        var earlier = Revision(1789823109, 707_000_001);
        var later = Revision(1789823105, 707_000_002);
        Assert.True(Spotify.Telemetry.CompareRevisions(later, earlier) > 0);
    }

    [Fact]
    public void Singular_dealer_payload_is_a_revision_without_an_entry_wrapper()
    {
        var answer = Spotify.Telemetry.DecodeProgressPush(Revision(1789823109, 707_000_000).ToByteArray(), false);
        Assert.Equal(EpisodeUri, answer.States[0].EntityUri);
        Assert.Single(answer.States[0].Revisions);
    }

    [Fact]
    public void Field_eight_alone_does_not_claim_a_position_or_completion()
    {
        var answer = Spotify.Telemetry.DecodeProgressPush(Revision(1789823105, 707_000_000, true).ToByteArray(), false);
        var staged = Staging.Rent();
        try { Assert.Equal(0, Spotify.Telemetry.FoldCurrentStates(answer, staged)); }
        finally { Staging.Return(staged); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Completion_mutation_is_marker_or_zero_plus_collection_membership(bool played)
    {
        const long at = 1789823105000;
        var revision = Spotify.Telemetry.MarkRevision(EpisodeUri, played, at);
        Assert.Equal(played ? Rs.CurrentStateValue.StateOneofCase.Marker8 : Rs.CurrentStateValue.StateOneofCase.ResumePoint,
            revision.Revision.Value.StateCase);
        if (!played) Assert.Equal(0, Spotify.Telemetry.PositionMsOf(revision.Revision.Value.ResumePoint));
        var collection = Col.WriteRequest.Parser.ParseFrom(Spotify.Telemetry.CompletionWriteBody("synthetic-account", EpisodeUri, played, at));
        Assert.Equal("markedasfinished", collection.Set);
        Assert.Equal(!played, collection.Items[0].IsRemoved);
        Assert.Equal(1789823105, collection.Items[0].AddedAt);
        Assert.Equal(collection.ToByteArray(), Spotify.Telemetry.CompletionWriteBody("synthetic-account", EpisodeUri, played, at));
    }

    [Fact]
    public void Unacknowledged_local_position_compares_full_creation_time()
    {
        var local = new EpisodeRevisionStamp(0, 0, 100, 900_000_000);
        Assert.True(new EpisodeRevisionStamp(110, 0, 100, 899_000_000).CompareForLanding(local) < 0);
        Assert.True(new EpisodeRevisionStamp(110, 0, 100, 901_000_000).CompareForLanding(local) > 0);
    }

    [Fact]
    public void Gabo_retries_only_transiently_rejected_event_indices()
    {
        var response = new Ev.PublishEventsResponse();
        response.Error.Add(new Ev.PublishEventsResponse.Types.EventError { Index = 1, Transient = true, Reason = 1 });
        response.Error.Add(new Ev.PublishEventsResponse.Types.EventError { Index = 3, Transient = false, Reason = 2 });
        Assert.Equal(new[] { 1 }, Spotify.Telemetry.RetryGaboIndices(response, 5, out int rejected));
        Assert.Equal(1, rejected);
    }

    [Fact]
    public void Gabo_malformed_error_index_does_not_acknowledge_an_unidentified_event()
    {
        var response = new Ev.PublishEventsResponse();
        response.Error.Add(new Ev.PublishEventsResponse.Types.EventError { Index = 10, Transient = true });
        Assert.Throws<InvalidDataException>(() => Spotify.Telemetry.RetryGaboIndices(response, 2, out _));
    }
}

[Collection(EntitiesCollection.Name)]
public class PodcastProgressIndependenceTests
{
    static EntityId Id()
    {
        Assert.True(EntityId.TryParseGid("spotify:episode:0Q86acNRm6V9GYx55SXKwf".AsSpan(), out var id));
        return id;
    }

    [Fact]
    public void Explicit_completion_survives_position_updates_and_does_not_destroy_the_cursor()
    {
        TestScope.Fresh();
        EntityId id = Id();
        Entities.MirrorEpisodeProgress(id, 123_456, 100000);
        Entities.MirrorEpisodeCompletion(id, true, 100001);
        Episode episode = Entities.Episode(id);
        Assert.Equal(123_456, episode.ProgressMs);
        Assert.True(episode.Completed);
        Assert.Equal(1f, Episode.Rules.PctOf(episode));
        Entities.MirrorEpisodeProgress(id, 124_000, 100002);
        Assert.True(episode.Completed);
        Entities.MirrorEpisodeCompletion(id, false, 100003);
        Assert.False(episode.ExplicitCompleted);
        Assert.Equal(124_000, episode.ProgressMs);
    }

    [Fact]
    public void Transcript_hydration_does_not_seal_detail_or_overwrite_progress()
    {
        TestScope.Fresh();
        EntityId id = Id();
        Entities.MirrorEpisodeProgress(id, 123_456, 100000);
        var s = Staging.Rent();
        ref var row = ref s.Episodes.RowFor(id, Authority.Full, (uint)EpisodeFields.Transcript);
        row.Flags = (uint)EpisodeFlags.HasTranscript;
        TestScope.CommitAndPublish(s);
        Episode episode = Entities.Episode(id);
        Assert.True(episode.Knows(EpisodeFields.Transcript));
        Assert.False(episode.Knows(EpisodeFields.Detail));
        Assert.Equal(123_456, episode.ProgressMs);
        Assert.True((episode.Flags & EpisodeFlags.HasTranscript) != 0);
    }

    [Theory]
    [InlineData(29_999, 30_000, false)]
    [InlineData(30_000, 30_000, true)]
    [InlineData(98_000, 100_000, false)]
    [InlineData(100_000, 100_000, true)]
    public void Duration_completion_requires_reaching_the_end(int position, int duration, bool completed)
        => Assert.Equal(completed, Episode.Rules.Completed(position, duration));
}
