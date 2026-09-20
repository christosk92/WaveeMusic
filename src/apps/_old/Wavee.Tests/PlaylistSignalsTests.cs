using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Wavee.Backend;
using Wavee.Backend.Collections;
using Wavee.Backend.Library;
using Wavee.Backend.Playlists;
using Wavee.Backend.Realtime;
using Wavee.Backend.Spotify;
using Wavee.Backend.Sync;
using Wavee.Core;
using Xunit;
using Pl = Wavee.Protocol.Playlist;

namespace Wavee.Tests;

public sealed class PlaylistSignalsTests
{

    // The facade every StoreLibrarySource read goes through. Offline = store-only, never networks (design §1.3).
    static SwitchableEntityHydrator Offline(IStore store) => new(new Wavee.Backend.Hydration.OfflineEntityHydrator(store));
    const string Uri = "spotify:playlist:mix";
    const string ChoiceA = "session_control_display$mix$more_discovery";
    const string ChoiceB = "session_control_display$mix$soft_pop:nl_genre";
    const string Reset = "session-control-reset";

    static byte[] Rev(byte value)
    {
        var revision = new byte[24];
        revision[23] = value;
        return revision;
    }

    static Pl.SelectedListContent Snapshot(byte revision, string? selected = null)
    {
        var slc = new Pl.SelectedListContent
        {
            Revision = ByteString.CopyFrom(Rev(revision)),
            Length = 1,
            OwnerUsername = "spotify",
            Attributes = new Pl.ListAttributes { Name = "Automatic Mix", Format = "inspiredby-mix" },
            Contents = new Pl.ListItems { Pos = 0, Truncated = false },
        };
        slc.Attributes.FormatAttributes.Add(new Pl.FormatListAttribute
            { Key = "session_control_display.displayName.more_discovery", Value = "More discovery tracks" });
        slc.Attributes.FormatAttributes.Add(new Pl.FormatListAttribute
            { Key = "session_control_display.displayName.soft_pop:nl_genre", Value = "Make it more soft pop" });
        if (selected is not null)
            slc.Attributes.FormatAttributes.Add(new Pl.FormatListAttribute
                { Key = "session_control.selected_signals", Value = selected });
        slc.Contents.AvailableSignals.Add(new Pl.AvailableSignal { Identifier = ChoiceA });
        slc.Contents.AvailableSignals.Add(new Pl.AvailableSignal { Identifier = ChoiceB });
        slc.Contents.AvailableSignals.Add(new Pl.AvailableSignal { Identifier = Reset });
        slc.Contents.Items.Add(new Pl.Item { Uri = "spotify:track:" + revision });
        return slc;
    }

    [Fact]
    public async Task Client_PostsCapturedWireShape_AndDecodesZstdSnapshot()
    {
        HttpReq? captured = null;
        using var compressor = new ZstdSharp.Compressor(3);
        byte[] compressed = compressor.Wrap(Snapshot(2, ChoiceA).ToByteArray()).ToArray();
        var http = new FakeExchange((req, _) =>
        {
            captured = req;
            return new HttpResp(200, new Dictionary<string, string>(), compressed);
        });
        var client = new PlaylistSignalsClient(http, () => "https://gew4-spclient.spotify.com", () => "nl-NL");

        var result = await client.ApplyAsync(Uri, Rev(1), ChoiceA, TestContext.Current.CancellationToken);

        Assert.Equal(Rev(2), result.Revision.ToByteArray());
        Assert.NotNull(captured);
        Assert.Equal("POST", captured!.Method);
        Assert.EndsWith("/playlist/v2/playlist/mix/signals", captured.Url);
        Assert.Equal("application/x-www-form-urlencoded", captured.Headers["Content-Type"]);
        Assert.Equal("application/x-protobuf", captured.Headers["Accept"]);
        Assert.Equal("CA8QAQ==", captured.Headers["spotify-playlist-sync-reason"]);
        Assert.Equal("nl", captured.Headers["Accept-Language"]);

        var request = Pl.ApplyPlaylistSignals.Parser.ParseFrom(captured.Body);
        Assert.Equal(Rev(1), request.Revision.ToByteArray());
        var signal = Assert.Single(request.Signals);
        Assert.Equal(ChoiceA, signal.Identifier);
        Assert.True(Guid.TryParseExact(signal.Interaction.Uuid, "D", out _));
        Assert.Equal(signal.Interaction.Uuid.ToLowerInvariant(), signal.Interaction.Uuid);
    }

    [Fact]
    public void SnapshotAdoption_MapsFormatRosterLabelsSelectionAndReset()
    {
        var store = new InMemoryStore();
        var fetcher = new PlaylistFetcher(
            new FakeExchange((_, _) => throw new InvalidOperationException()),
            () => "https://x",
            store,
            (_, _) => Task.CompletedTask,
            () => "");

        fetcher.AdoptSnapshot(Uri, Snapshot(4, ChoiceB));

        Assert.Equal(Rev(4), store.PlaylistRevision(Uri));
        var playlist = Assert.IsType<Playlist>(store.GetPlaylist(Uri));
        Assert.Equal("inspiredby-mix", playlist.Format);
        var tuning = Assert.IsType<PlaylistTuning>(playlist.Tuning);
        Assert.Equal(ChoiceB, tuning.SelectedIdentifier);
        Assert.Equal(3, tuning.Available.Count);
        Assert.Equal("More discovery tracks", tuning.Available[0].DisplayName);
        Assert.Equal("Make it more soft pop", tuning.Available[1].DisplayName);
        Assert.Equal(PlaylistTuningOptionKind.Reset, tuning.Available[2].Kind);
        Assert.Null(tuning.Available[2].DisplayName);
        Assert.Equal("spotify:track:4", Assert.Single(store.Membership(Uri)).ItemUri);
    }

    [Fact]
    public void MenuModel_HidesUnlabelledChoices_AndShowsResetOnlyWhenSelected()
    {
        var options = new PlaylistTuningOption[]
        {
            new(ChoiceA, "More discovery tracks", PlaylistTuningOptionKind.Choice),
            new(ChoiceB, null, PlaylistTuningOptionKind.Choice),
            new(Reset, null, PlaylistTuningOptionKind.Reset),
        };
        var idle = new PlaylistTuning(Rev(1), options, null);
        var selected = idle with { SelectedIdentifier = ChoiceA };

        Assert.True(PlaylistTuneMenuModel.IsEligible(idle, sourceAvailable: true));
        Assert.False(PlaylistTuneMenuModel.IsEligible(idle, sourceAvailable: false));
        Assert.Equal(ChoiceA, Assert.Single(PlaylistTuneMenuModel.VisibleChoices(idle)).Identifier);
        Assert.Null(PlaylistTuneMenuModel.ResetOption(idle));
        Assert.Equal(Reset, PlaylistTuneMenuModel.ResetOption(selected)!.Identifier);
    }

    [Fact]
    public async Task StoreRead_HidesTuningWhenMembershipRevisionHasAdvanced()
    {
        var store = new InMemoryStore();
        store.UpsertPlaylist(new Playlist(
            "mix", Uri, "Mix", null, "spotify", null, 1,
            Tuning: new PlaylistTuning(
                Rev(1),
                new[] { new PlaylistTuningOption(ChoiceA, "More discovery tracks", PlaylistTuningOptionKind.Choice) },
                null)));
        store.SetMembership(Uri, new[] { new PlaylistMember("a", "spotify:track:a", null, 0) }, Rev(2));
        using var source = new StoreLibrarySource(store, Offline(store), OfflineOnlineCatalog.Instance);

        var playlist = await source.GetPlaylistAsync(Uri, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(playlist);
        Assert.Null(playlist!.Tuning);
    }

    [Fact]
    public async Task Sync_SerializesTwoApplies_AndChainsResponseRevision()
    {
        var requestRevisions = new List<byte[]>();
        int calls = 0;
        var http = new FakeExchange((req, _) =>
        {
            var request = Pl.ApplyPlaylistSignals.Parser.ParseFrom(req.Body);
            requestRevisions.Add(request.Revision.ToByteArray());
            calls++;
            string selected = request.Signals[0].Identifier;
            return new HttpResp(200, new Dictionary<string, string>(), Snapshot((byte)(calls + 1), selected).ToByteArray());
        });
        var store = new InMemoryStore();
        store.UpsertPlaylist(new Playlist(
            "mix", Uri, "Automatic Mix", null, "spotify", null, 1,
            Tuning: new PlaylistTuning(
                Rev(1),
                new PlaylistTuningOption[]
                {
                    new(ChoiceA, "More discovery tracks", PlaylistTuningOptionKind.Choice),
                    new(ChoiceB, "Make it more soft pop", PlaylistTuningOptionKind.Choice),
                    new(Reset, null, PlaylistTuningOptionKind.Reset),
                },
                null)));
        store.SetMembership(Uri, new[] { new PlaylistMember("a", "spotify:track:a", null, 0) }, Rev(1));

        var fetcher = new PlaylistFetcher(http, () => "https://x", store, (_, _) => Task.CompletedTask, () => "");
        var revisions = new Dictionary<string, string?>();
        var collections = new CollectionFetcher(http, () => "https://x", () => "bob", store,
            s => revisions.TryGetValue(s, out var r) ? r : null,
            (s, r) => revisions[s] = r,
            (_, _) => Task.CompletedTask);
        var echo = new CollectionEchoRing();
        var mutations = new MutationEngine(store,
            new IMutationStrategy[]
            {
                new SetReplayStrategy(echo),
                new OpRebaseStrategy(store, () => "https://x", new PlaylistResyncQueue()),
                new RootlistFollowStrategy(store, new RootlistLane()),
            });
        using var cts = new CancellationTokenSource();
        var transport = new StubTransport();
        var client = new PlaylistSignalsClient(http, () => "https://x", () => "en");
        await using var sync = new LibrarySync(
            store, fetcher, collections, mutations, new PlaylistResyncQueue(), transport,
            () => new SessionContext("bob", "US", "premium", "en", Tier.Premium, false),
            () => "bob", default, cts.Token, echo, client);

        await sync.ApplyAsync(Uri, ChoiceA, TestContext.Current.CancellationToken);
        await sync.ApplyAsync(Uri, ChoiceB, TestContext.Current.CancellationToken);

        Assert.Equal(2, calls);
        Assert.Equal(Rev(1), requestRevisions[0]);
        Assert.Equal(Rev(2), requestRevisions[1]);
        Assert.Equal(Rev(3), store.PlaylistRevision(Uri));
        Assert.Equal(ChoiceB, store.GetPlaylist(Uri)!.Tuning!.SelectedIdentifier);
        Assert.Equal(2, sync.SignalApplies);
    }

    [Fact]
    public async Task EmptyOpDealerPush_IsFullRefreshRequired_AndDuplicateEchoDrops()
    {
        var response = Snapshot(2, ChoiceA);
        await using var harness = new SyncHarness(_ => SyncHarness.Ok(response.ToByteArray()));
        harness.Store.UpsertPlaylist(new Playlist("mix", Uri, "Mix", null, "spotify", null, 1));
        harness.Store.SetMembership(Uri, new[] { new PlaylistMember("a", "spotify:track:a", null, 0) }, Rev(1));
        using var router = new DealerRouter(harness.Dealer, harness.Sync);

        var info = new Pl.PlaylistModificationInfo
        {
            Uri = ByteString.CopyFromUtf8(Uri),
            NewRevision = ByteString.CopyFrom(Rev(2)),
        };
        var wire = new WireEvent("hm://playlist/v2/playlist/mix", info.ToByteArray());
        harness.Dealer.PushEvent(wire);
        await harness.Sync.WaitForIdleAsync();
        Assert.Equal(0, harness.PlaylistGets);

        await harness.Sync.OpenPlaylistAsync(Uri, TestContext.Current.CancellationToken);
        Assert.Equal(1, harness.PlaylistGets);
        Assert.Equal(Rev(2), harness.Store.PlaylistRevision(Uri));

        harness.Dealer.PushEvent(wire);
        await harness.Sync.WaitForIdleAsync();
        Assert.Equal(1, harness.PlaylistGets);
        Assert.Equal(1, harness.Sync.EchoDropped);
    }
}
