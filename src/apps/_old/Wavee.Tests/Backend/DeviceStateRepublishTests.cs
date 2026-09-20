using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

// PublishStateChanged: the out-of-band PutState behind connect-state video parity. A music-video association landing under
// an already-playing track changes what remote controllers must see (associated_video_id + the switch-to-video offer) but
// swaps no host, so no playback event fires and the steady-state change gate would swallow a normal publish.
public class DeviceStateRepublishTests
{
    static Track T(string uri) => new(uri[(uri.LastIndexOf(':') + 1)..], uri, uri,
        Array.Empty<ArtistRef>(), new AlbumRef("", "", ""), 1000, false, null);

    sealed class Harness
    {
        public readonly StubTransport Transport = new();
        public readonly NowPlayingProjection Proj = new("us", NotOwnedEntityHydrator.Instance, new InMemoryStore(), () => 0);
        public readonly SimpleSubject<string?> ConnId = new(null);
        public readonly List<PutStateReasonKind> Reasons = new();
        public readonly DeviceStatePublisher Publisher;

        public Harness()
        {
            Publisher = new DeviceStatePublisher(Transport, "us", Proj, Proj.Ownership, ConnId, () => "c1",
                (reason, snap, mid, active) =>
                {
                    Reasons.Add(reason);
                    return Encoding.UTF8.GetBytes(reason + "|" + active + "|" + (snap?.Track.Uri ?? "-") + "|" + mid);
                },
                onCluster: null, clock: () => 1000);
        }

        // A real local play always claims ownership BEFORE the event reaches the publisher — is_active is derived from
        // ConnectOwnership now (contract item 1), not from a bare CurrentTrack fact.
        public void Play(string uri)
        {
            Proj.Ownership.Claim(ClaimCause.UserPlay);
            var e = new PlaybackEvent(EvKind.Started, T(uri), 0);
            Proj.OnEvent(e);
            Publisher.OnEvent(e);
        }
    }

    static async Task SettleAsync() => await Task.Delay(30);

    [Fact]
    public async Task PublishStateChanged_PublishesExactlyOnce_EvenThoughNothingInTheChangeGateMoved()
    {
        var h = new Harness();
        h.Play("spotify:track:a");
        await SettleAsync();
        int before = h.Transport.PublishCount;

        h.Publisher.PublishStateChanged();
        await SettleAsync();

        Assert.Equal(before + 1, h.Transport.PublishCount);
        Assert.Equal(PutStateReasonKind.PlayerStateChanged, h.Reasons[^1]);
        Assert.Contains("|True|spotify:track:a|", Encoding.UTF8.GetString(h.Transport.LastPublishBody!));
    }

    // Was PublishStateChanged_AfterOwnershipRetired_PublishesNothing, gated on the deleted `_ownershipRetired` flag
    // (set by calling PublishInactive() directly). is_active is now derived from the ConnectOwnership AUTHORITY
    // (contract item 1): PublishInactive() alone sends a bare PUT and no longer flips anything — only
    // ConnectOwnership.Release actually gives up the slot, so that is what this now exercises. A duplicate scenario
    // (via a foreign cluster takeover instead of an explicit release) lives in ConnectPublisherTests as
    // BadgeRepublish_WhileNotUs_PublishesNothing.
    [Fact]
    public async Task PublishStateChanged_WhileNotUs_PublishesNothing()
    {
        var h = new Harness();
        h.Play("spotify:track:a");
        h.Proj.Ownership.Release(ReleaseCause.TransferAway);   // playback handed to another device
        await SettleAsync();
        int before = h.Transport.PublishCount;

        h.Publisher.PublishStateChanged();
        await SettleAsync();

        Assert.Equal(before, h.Transport.PublishCount);   // a badge land must never steal the cluster back
    }

    [Fact]
    public async Task PublishStateChanged_WithNoCurrentTrack_PublishesNothing()
    {
        var h = new Harness();
        await SettleAsync();
        int before = h.Transport.PublishCount;

        h.Publisher.PublishStateChanged();
        await SettleAsync();

        Assert.Equal(before, h.Transport.PublishCount);
    }
}
