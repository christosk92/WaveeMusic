// ── Playback/Playback.Host.FakeConnect.cs — `--fake`'s Spotify Connect devices (the inspector's `mount connect=nearby`) ─────────────────
//
// Role: SHELL (`--fake` only)
//
// `--fake` has no Connect cluster, so the device picker's "Other devices" could only ever be empty and "Play on this PC" could never be
// seen. This stands in for the cluster over a fixed roster, through the reducer's OWN inputs, so the picker, the bar's "Playing on" line
// and the volume route run the paths a real session runs: the roster rows go into `Devices` as a cluster would put them; a transfer to
// one of them is answered as the service answers one (the POST accepted, then a push naming the target active and mirroring what was
// playing, at the device's own volume); "Play on this PC" is the reducer's own local claim, which needs no answer (no other device is
// seen during its protection, so it stands).

using ClusterOrigin = Wavee.Spotify.Decode.ClusterOrigin;

namespace Wavee;

public static partial class Playback
{
    /// <summary>One fake Spotify Connect device: its id, name, kind and volume (0..65535, the wire scale), and whether a controller
    /// is kept from setting that volume (<see cref="Devices.Row.VolumeFixed"/>: true stands in for an iPhone's <c>disable_volume</c>); what it
    /// says it plays while it owns playback (<see cref="Devices.Row.Quality"/>) and where it plays (its output's name and type).</summary>
    public readonly record struct FakeConnectDevice(string Id, string Name, Spotify.Decode.DeviceKind Kind, int Volume, bool VolumeFixed = false,
        Spotify.Decode.PlaybackQuality Quality = default, string? OutputName = null, byte OutputType = 0);

    static FakeConnectDevice[]? s_fakeConnect;
    static ulong s_fakePushTarget;

    /// <summary>The fake roster is installed (a transfer is answered here, never sent).</summary>
    public static bool FakeConnectActive => s_fakeConnect is not null;

    /// <summary>Install <paramref name="roster"/> as the Connect devices (empty: nothing nearby), or with null remove the stand-in. UI thread;
    /// the roster replaces whatever stood, every device inactive.</summary>
    public static void UseFakeConnect(FakeConnectDevice[]? roster)
    {
        s_fakeConnect = roster;
        s_fakePushTarget = 0;
        Devices.Seed(roster ?? []);
        Log.Info("playback", "fake connect roster: " + (roster is null ? "off" : roster.Length + " device(s)"));
    }

    /// <summary>A transfer the fake roster answers: accepted, then (next drain) a push naming the target active.</summary>
    static void AnswerFakeTransfer(ulong target, uint epoch)
    {
        Post(Input.TransferDone(epoch, ok: true));
        s_fakePushTarget = target;
        RequestDrain();
        Log.Info("playback", "fake connect: transfer to " + Devices.NameOf(target));
    }

    /// <summary>The push a transfer earns, folded at the top of a drain as a real cluster is (<see cref="FoldCluster"/>): the target active,
    /// playing what this device played, from where it stopped, at the target's own volume.</summary>
    static void FoldFakePush(long now)
    {
        ulong target = s_fakePushTarget;
        if (target == 0) return;
        s_fakePushTarget = 0;
        int slot = Devices.SlotOf(target);
        if (slot < 0) return;
        Devices.MarkActive(target);
        long unix = UnixNowMs();
        var frame = new ClusterFrame(ClusterOrigin.Push, 0, target, unix);
        var remote = new RemoteState(
            s_state.HasCurrent, s_state.CurrentId, IsPlaying: true, IsPaused: false, IsBuffering: false,
            s_state.PosMs, unix, s_state.DurationMs, s_state.Shuffle, s_state.Repeat, Devices.Rows[slot].Volume,
            NoPrev: false, NoNext: false, NoSeek: false, s_state.Context, VolumeFixed: Devices.Rows[slot].VolumeFixed);
        StepOne(Input.Cluster(in frame, in remote, now));
        s_state.ActiveDeviceSlot = Devices.SlotOf(s_state.ActiveDevice);
    }

    // ── the inspector's cluster (`dj do=cluster`) ─────────────────────────────────────────────────────────────────────

    static readonly System.Collections.Generic.Queue<byte[]> s_inspectClusters = new();

    /// <summary>The inspector hands a CAPTURED or synthetic <c>ClusterUpdate</c> (protobuf) to the fold a dealer push takes: decoded with
    /// this device's id and folded at the top of the next drain (<see cref="FoldCluster(Spotify.Decode.ClusterBuffer, in Spotify.Decode.ClusterDelta, long)"/>),
    /// so the ownership, the mirror and the DJ facts are the real ones. UI thread.</summary>
    public static void FoldClusterForInspect(byte[] clusterUpdate)
    {
        s_inspectClusters.Enqueue(clusterUpdate);
        RequestDrain();
    }

    static void FoldInspectClusters(long now)
    {
        while (s_inspectClusters.TryDequeue(out byte[]? raw))
        {
            Spotify.Decode.ClusterBuffer buffer = Spotify.Decode.ClusterBuffer.Rent();
            try
            {
                Spotify.Decode.ClusterDelta d = Spotify.Decode.ClusterUpdate(raw, System.Text.Encoding.UTF8.GetBytes(s_identity.DeviceId ?? ""), buffer);
                FoldCluster(buffer, in d, now);
            }
            finally { Spotify.Decode.ClusterBuffer.Return(buffer); }
        }
    }
}
