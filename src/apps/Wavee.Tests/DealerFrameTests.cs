// ── Wavee.Tests/DealerFrameTests.cs — the dealer frame parse and the audio-key packets ───────────────────────────
//
// Wave 2's gate for the two wire shapes the session itself has to understand (plan §5 Wave 2: "dealer frame parse").
// The socket is unverifiable here; the decode is not — and the 0.2.9 defects these pin are real ones: a `payloads`
// array with a plain-JSON entry used to lose the whole frame's topic, and a REQUEST frame's payload is a SINGULAR
// object, not the array.

using System.IO.Compression;
using System.Text;
using Wavee;
using Xunit;

namespace Wavee.Tests;

public class DealerFrameTests
{
    static byte[] Scratch() => new byte[64 * 1024];

    static byte[] Gzip(byte[] raw)
    {
        using var output = new MemoryStream();
        using (var gz = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true)) gz.Write(raw, 0, raw.Length);
        return output.ToArray();
    }

    [Fact]
    public void A_ping_is_a_ping()
    {
        var frame = Spotify.DealerFrame.Parse("{\"type\":\"ping\"}"u8, Scratch());
        Assert.Equal(Spotify.DealerFrameKind.Ping, frame.Kind);
        Assert.True(frame.IsPing);
    }

    [Fact]
    public void A_message_carries_its_topic_and_its_base64_payload()
    {
        byte[] payload = { 1, 2, 3, 4 };
        string json = "{\"type\":\"message\",\"uri\":\"hm://playlist/v2/playlist/p\",\"payloads\":[\""
            + Convert.ToBase64String(payload) + "\"]}";
        var frame = Spotify.DealerFrame.Parse(Encoding.UTF8.GetBytes(json), Scratch());

        Assert.Equal(Spotify.DealerFrameKind.Message, frame.Kind);
        Assert.Equal("hm://playlist/v2/playlist/p", Encoding.UTF8.GetString(frame.Uri));
        Assert.Equal(payload, frame.Payload.ToArray());
        Assert.False(frame.Truncated);
    }

    [Fact]
    public void Payload_chunks_concatenate()
    {
        string json = "{\"type\":\"message\",\"uri\":\"hm://x\",\"payloads\":[\""
            + Convert.ToBase64String(new byte[] { 1, 2, 3 }) + "\",\"" + Convert.ToBase64String(new byte[] { 4, 5, 6 }) + "\"]}";
        var frame = Spotify.DealerFrame.Parse(Encoding.UTF8.GetBytes(json), Scratch());
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, frame.Payload.ToArray());
    }

    [Fact]
    public void A_message_without_a_payload_still_has_its_topic()
    {
        var frame = Spotify.DealerFrame.Parse("{\"type\":\"message\",\"uri\":\"hm://presence2/user/x\"}"u8, Scratch());
        Assert.Equal(Spotify.DealerFrameKind.Message, frame.Kind);
        Assert.Equal("hm://presence2/user/x", Encoding.UTF8.GetString(frame.Uri));
        Assert.True(frame.Payload.IsEmpty);
    }

    [Fact]
    public void A_gzip_message_comes_back_inflated()
    {
        byte[] raw = { 9, 8, 7, 6, 5 };
        string json = "{\"type\":\"message\",\"uri\":\"hm://collection/x\",\"headers\":{\"Transfer-Encoding\":\"gzip\"},"
            + "\"payloads\":[\"" + Convert.ToBase64String(Gzip(raw)) + "\"]}";
        var frame = Spotify.DealerFrame.Parse(Encoding.UTF8.GetBytes(json), Scratch());
        Assert.Equal(raw, frame.Payload.ToArray());
    }

    [Fact]
    public void A_request_frame_keeps_its_key_ident_and_gunzipped_command()
    {
        byte[] command = Encoding.UTF8.GetBytes("{\"command\":{\"endpoint\":\"pause\"}}");
        string json = "{\"type\":\"request\",\"key\":\"abc-123\",\"message_ident\":\"hm://connect-state/v1/player/command\","
            + "\"payload\":{\"compressed\":\"" + Convert.ToBase64String(Gzip(command)) + "\"}}";
        var frame = Spotify.DealerFrame.Parse(Encoding.UTF8.GetBytes(json), Scratch());

        Assert.True(frame.IsRequest);
        Assert.Equal("abc-123", Encoding.UTF8.GetString(frame.Key));
        Assert.Equal("hm://connect-state/v1/player/command", Encoding.UTF8.GetString(frame.Ident));
        Assert.Equal(command, frame.Payload.ToArray());
    }

    [Fact]
    public void A_cluster_push_is_recognised_by_its_topic()
    {
        var cluster = Spotify.DealerFrame.Parse(
            "{\"type\":\"message\",\"uri\":\"hm://connect-state/v1/cluster\",\"payloads\":[\"AQID\"]}"u8, Scratch());
        Assert.True(cluster.IsClusterUpdate);

        var other = Spotify.DealerFrame.Parse(
            "{\"type\":\"message\",\"uri\":\"hm://collection/collection/user/1\",\"payloads\":[\"AQID\"]}"u8, Scratch());
        Assert.False(other.IsClusterUpdate);
    }

    [Fact]
    public void The_connection_id_header_is_read_off_the_pusher_hello()
    {
        var frame = Spotify.DealerFrame.Parse(
            "{\"type\":\"message\",\"uri\":\"hm://pusher/v1/connections/xyz\",\"headers\":{\"Spotify-Connection-Id\":\"conn-42\"}}"u8,
            Scratch());
        Assert.Equal("conn-42", Encoding.UTF8.GetString(frame.ConnectionId));
    }

    [Fact]
    public void A_non_base64_entry_is_skipped_and_the_topic_survives()
    {
        // social-connect ships a plain JSON OBJECT inside `payloads`. 0.2.9's first parser threw here, and the outer
        // catch reported the whole frame as Unknown with no uri — the topic the router needed was gone.
        string json = "{\"type\":\"message\",\"uri\":\"hm://social-connect/v2/broadcast_status_update\","
            + "\"payloads\":[{\"state\":\"playing\"}]}";
        var frame = Spotify.DealerFrame.Parse(Encoding.UTF8.GetBytes(json), Scratch());
        Assert.Equal(Spotify.DealerFrameKind.Message, frame.Kind);
        Assert.Equal("hm://social-connect/v2/broadcast_status_update", Encoding.UTF8.GetString(frame.Uri));
        Assert.True(frame.Payload.IsEmpty);
    }

    [Fact]
    public void Malformed_json_is_unknown_rather_than_an_exception()
    {
        Assert.Equal(Spotify.DealerFrameKind.Unknown, Spotify.DealerFrame.Parse("{not json"u8, Scratch()).Kind);
        Assert.Equal(Spotify.DealerFrameKind.Unknown, Spotify.DealerFrame.Parse([], Scratch()).Kind);
    }

    [Fact]
    public void The_reply_escapes_the_key_it_was_given()
    {
        Span<byte> buffer = stackalloc byte[256];
        int n = Spotify.DealerFrame.WriteReply(buffer, "ab\"c"u8, ok: true);
        Assert.Equal("{\"type\":\"reply\",\"key\":\"ab\\\"c\",\"payload\":{\"success\":true}}",
            Encoding.UTF8.GetString(buffer[..n]));

        n = Spotify.DealerFrame.WriteReply(buffer, "k"u8, ok: false);
        Assert.Equal("{\"type\":\"reply\",\"key\":\"k\",\"payload\":{\"success\":false}}",
            Encoding.UTF8.GetString(buffer[..n]));
    }

    [Fact]
    public void The_keepalive_frames_are_the_two_the_dealer_expects()
    {
        Span<byte> buffer = stackalloc byte[32];
        int ping = Spotify.DealerFrame.WritePing(buffer);
        Assert.Equal("{\"type\":\"ping\"}", Encoding.UTF8.GetString(buffer[..ping]));
        int pong = Spotify.DealerFrame.WritePong(buffer);
        Assert.Equal("{\"type\":\"pong\"}", Encoding.UTF8.GetString(buffer[..pong]));
    }
}

public class AudioKeyPacketTests
{
    [Fact]
    public void The_request_body_has_the_captured_shape()
    {
        var fileId = new byte[20];
        for (int i = 0; i < 20; i++) fileId[i] = (byte)i;
        var gid = new byte[16];
        for (int i = 0; i < 16; i++) gid[i] = (byte)(100 + i);

        Span<byte> body = stackalloc byte[Spotify.AudioKey.RequestLength];
        int n = Spotify.AudioKey.WriteRequest(fileId, gid, 7, body);

        Assert.Equal(42, n);                                    // 20 + 16 + 4 + 2
        Assert.Equal(fileId, body[..20].ToArray());
        Assert.Equal(gid, body[20..36].ToArray());
        Assert.Equal(7u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(body[36..40]));
        Assert.Equal(0, body[40]);
        Assert.Equal(0, body[41]);
    }

    [Fact]
    public void A_key_reply_yields_its_sequence_and_sixteen_bytes()
    {
        var payload = new byte[20];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(payload, 3);
        for (int i = 0; i < 16; i++) payload[4 + i] = (byte)(200 + i);

        Span<byte> key = stackalloc byte[16];
        Assert.True(Spotify.AudioKey.TryReadKey(payload, out uint seq, key));
        Assert.Equal(3u, seq);
        Assert.Equal(payload[4..], key.ToArray());
    }

    [Fact]
    public void A_short_key_reply_is_refused()
    {
        Assert.False(Spotify.AudioKey.TryReadKey(new byte[8], out _, stackalloc byte[16]));
    }

    [Fact]
    public void An_error_reply_yields_its_sequence_and_code()
    {
        var payload = new byte[6];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(payload, 9);
        payload[5] = 2;
        Assert.True(Spotify.AudioKey.TryReadError(payload, out uint seq, out int code));
        Assert.Equal(9u, seq);
        Assert.Equal(2, code);

        Assert.True(Spotify.AudioKey.TryReadError(new byte[4], out _, out int missing));
        Assert.Equal(-1, missing);
    }
}
