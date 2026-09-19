using FluentGpu.Signals;

namespace Wavee;

public static partial class Playback
{
    public static readonly Signal<float> EpisodeSpeed = new(1f);
    static readonly SettingKey<float> s_episodeSpeedKey = new("playback.episodeSpeed", 1f);
    public static float ValidEpisodeSpeed(float rate) => float.IsFinite(rate) ? Math.Clamp(rate, .5f, 3f) : 1f;
    public static float RateFor(EntityId id) => id.Kind == EntityKind.Episode ? EpisodeSpeed.Peek() : 1f;

    public static void SetEpisodeSpeed(float rate)
    {
        rate = ValidEpisodeSpeed(rate);
        ApplyEpisodeSpeed(rate);
        Platform.Settings.Set(s_episodeSpeedKey, rate);
        if (!Spotify.Current.IsOnline) return;
        float value = rate;
        Spotify.Api.Run(() =>
        {
            byte[] body = SpeedSettingsBody(value);
            var result = Spotify.Api.PostEncoded(
                "/playback-settings/spotify.playbacksettings.PlaybackSettingsService/WriteContentValue",
                Spotify.ApiHost.Spclient, Spotify.HeaderSet.Bearer | Spotify.HeaderSet.ClientToken | Spotify.HeaderSet.Identity,
                body, "application/x-protobuf", null, CancellationToken.None);
            if (!result.Ok) Log.Warn("playback", "episode speed setting rejected status=" + result.Status);
        });
    }

    static void ApplyEpisodeSpeed(float rate)
    {
        EpisodeSpeed.SetIfChanged(rate);
        Post(new Input(InputKind.SetSpeed, intArg: BitConverter.SingleToInt32Bits(rate), nowMs: FrameNowMs()));
    }

    public static byte[] SpeedSettingsBody(float rate)
    {
        Span<byte> bytes = stackalloc byte[64];
        var w = new Spotify.Decode.ProtoWriter(bytes);
        w.Utf8(1, "GLOBAL"u8);
        int setting = w.Open(2);
        w.U(1, 3);
        int value = w.Open(2);
        w.F32(4, ValidEpisodeSpeed(rate));
        w.Close(value); w.Close(setting);
        return bytes[..w.Length].ToArray();
    }

    public static bool OnSpeedSettings(ReadOnlySpan<byte> topic, ReadOnlySpan<byte> payload)
    {
        if (topic.IndexOf("playback-settings/content-settings-update"u8) < 0) return false;
        var r = new Spotify.Decode.ProtoReader(payload);
        if (!r.Bytes(2).SequenceEqual("GLOBAL"u8)) return true;
        var setting = new Spotify.Decode.ProtoReader(new Spotify.Decode.ProtoReader(payload).Bytes(3));
        if (setting.Varint(1, 0) != 3) return true;
        var value = new Spotify.Decode.ProtoReader(new Spotify.Decode.ProtoReader(new Spotify.Decode.ProtoReader(payload).Bytes(3)).Bytes(2));
        while (value.Next())
        {
            if (value.Field == 4 && value.Wire == 5)
            {
                float rate = ValidEpisodeSpeed(BitConverter.UInt32BitsToSingle(value.Fixed32()));
                ToUi(() => ApplyEpisodeSpeed(rate));
                break;
            }
            value.Skip();
        }
        return true;
    }
}
