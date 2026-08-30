using System;
using System.Threading.Tasks;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

// The remote-only playback stubs (UnsupportedPlaybackPlayer / NoConnectDevices) that replaced the deleted in-process
// fakes: local playback is unsupported, so every PLAY intent raises the rejection hook (→ the app's "choose a remote
// device" toast) while every other verb no-ops, and the pre-login device roster is empty.
public class PlaybackStubsTests
{
    static readonly PlaybackContextTrack[] OneTrack = { new("spotify:track:a") };

    [Fact]
    public async Task UnsupportedPlayer_PlayIntents_RaiseRejection()
    {
        var p = new UnsupportedPlaybackPlayer();
        int rejects = 0;
        p.OnPlayIntentRejected = () => rejects++;

        await p.PlayAsync("spotify:playlist:p");
        await p.PlayOrderedAsync("spotify:playlist:p", OneTrack);
        await p.PlayTrackAsync("spotify:track:a");
        await p.ResumeAsync();
        await p.EnqueueAsync("spotify:track:a");
        await p.PlayNextAsync(OneTrack);

        Assert.Equal(6, rejects);   // exactly the six play intents fire the hook
    }

    [Fact]
    public async Task UnsupportedPlayer_OtherVerbs_NoOp_NoRejection()
    {
        var p = new UnsupportedPlaybackPlayer();
        int rejects = 0;
        p.OnPlayIntentRejected = () => rejects++;

        await p.PauseAsync();
        await p.NextAsync();
        await p.PreviousAsync();
        await p.SeekAsync(1000, SeekMode.Accurate);
        await p.SetVolumeAsync(0.5);
        await p.SetShuffleAsync(true);
        await p.SetRepeatAsync(RepeatMode.Context);
        await p.MoveQueueItemAsync(new QueueItemId(1), 1);
        await p.RemoveQueueItemAsync(new QueueItemId(1));

        Assert.Equal(0, rejects);   // no non-play verb triggers the toast
    }

    [Fact]
    public void UnsupportedPlayer_State_IsPermanentlyEmpty()
    {
        var p = new UnsupportedPlaybackPlayer();
        var s = p.State;
        Assert.Same(p, s);                       // the player IS its own state
        Assert.Null(s.CurrentTrack);
        Assert.False(s.IsPlaying);
        Assert.Empty(s.Queue);
        Assert.Null(s.ActiveDeviceId);
        Assert.Null(s.Error);
    }

    [Fact]
    public async Task UnsupportedPlayer_NullHook_DoesNotThrow()
    {
        var p = new UnsupportedPlaybackPlayer();   // no hook wired
        await p.PlayAsync("spotify:playlist:p");   // must not NRE
        await p.ResumeAsync();
    }

    [Fact]
    public async Task NoConnectDevices_IsEmpty_TransferNoops()
    {
        var d = new NoConnectDevices();
        Assert.Empty(d.Devices);
        await d.TransferAsync("anything");   // no-op, must not throw
        Assert.Empty(d.Devices);
    }

    [Fact]
    public void NoLyricsProvider_ReturnsNull()
    {
        var l = new NoLyricsProvider();
        Assert.Null(l.GetLyricsAsync("spotify:track:a").GetAwaiter().GetResult());
    }
}
