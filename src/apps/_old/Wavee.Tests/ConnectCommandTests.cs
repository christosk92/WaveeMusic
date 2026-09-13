using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Xunit;

namespace Wavee.Tests;

// Stage C — parse the dealer REQUEST command JSON into the flat POD, and ack-on-dispatch via the router.
public class ConnectCommandTests
{
    const string Ident = "hm://connect-state/v1/player/command";
    static readonly IReadOnlyDictionary<string, string> NoHeaders = new Dictionary<string, string>();
    static WireRequest Req(string json, string key = "k") => new(key, Ident, Encoding.UTF8.GetBytes(json), NoHeaders);

    [Fact]
    public void Parse_Pause_ReadsIdsAndKey()
    {
        Assert.True(ConnectCommand.TryParse(Req("{\"message_id\":7,\"sent_by_device_id\":\"phone\",\"command\":{\"endpoint\":\"pause\"}}", "7/phone"), out var c));
        Assert.Equal(ConnectCmd.Pause, c.Kind);
        Assert.Equal(7, c.MessageId);
        Assert.Equal("phone", c.SenderDeviceId);
        Assert.Equal("7/phone", c.Key);
    }

    [Fact]
    public void Parse_SeekTo_NumberAndString()
    {
        Assert.True(ConnectCommand.TryParse(Req("{\"command\":{\"endpoint\":\"seek_to\",\"value\":12345}}"), out var a));
        Assert.Equal(ConnectCmd.SeekTo, a.Kind);
        Assert.Equal(12345, a.SeekToMs);

        Assert.True(ConnectCommand.TryParse(Req("{\"command\":{\"endpoint\":\"seek_to\",\"value\":\"6789\"}}"), out var b));
        Assert.Equal(6789, b.SeekToMs);   // wire sometimes sends the position as a JSON string
    }

    [Fact]
    public void Parse_ShuffleRepeat_BoolArg()
    {
        Assert.True(ConnectCommand.TryParse(Req("{\"command\":{\"endpoint\":\"set_shuffling_context\",\"value\":true}}"), out var s));
        Assert.Equal(ConnectCmd.SetShufflingContext, s.Kind);
        Assert.True(s.BoolArg);

        Assert.True(ConnectCommand.TryParse(Req("{\"command\":{\"endpoint\":\"set_repeating_track\",\"value\":false}}"), out var r));
        Assert.Equal(ConnectCmd.SetRepeatingTrack, r.Kind);
        Assert.False(r.BoolArg);
    }

    [Fact]
    public void Parse_UnknownEndpoint_ReturnsFalse()
        => Assert.False(ConnectCommand.TryParse(Req("{\"command\":{\"endpoint\":\"frobnicate\"}}"), out _));

    [Fact]
    public async Task Router_KnownCommand_Dispatches_AndAcksSuccess()
    {
        var t = new StubTransport();
        var dispatched = new TaskCompletionSource<ConnectCmd>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var r = new ConnectCommandRouter(t, c => dispatched.TrySetResult(c.Kind));
        t.PushRequest(Req("{\"command\":{\"endpoint\":\"skip_next\"}}", "k1"));
        Assert.Equal(ConnectCmd.SkipNext, await dispatched.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(RequestResult.Success, t.LastReply);
    }

    [Fact]
    public void Router_Unsupported_AcksDeviceDoesNotSupport()
    {
        var t = new StubTransport();
        using var r = new ConnectCommandRouter(t, _ => { });
        t.PushRequest(Req("{\"command\":{\"endpoint\":\"frobnicate\"}}", "k2"));
        Assert.Equal(RequestResult.DeviceDoesNotSupportCommand, t.LastReply);
    }

    [Fact]
    public void Router_DispatchThrows_AcksQueueAdmission()
    {
        var t = new StubTransport();
        using var r = new ConnectCommandRouter(t, _ => throw new InvalidOperationException("boom"));
        t.PushRequest(Req("{\"command\":{\"endpoint\":\"pause\"}}", "k3"));
        Assert.Equal(RequestResult.Success, t.LastReply);   // execution is observed by the worker after prompt admission ACK
    }

    [Fact]
    public async Task Router_VolumeMessage_DecodesInnerProtobufBody()
    {
        var t = new StubTransport();
        var applied = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var r = new ConnectCommandRouter(
            t,
            (_, _) => Task.FromResult(ConnectCommandOutcome.NoOp),
            (volume, _) =>
            {
                applied.TrySetResult(volume);
                return Task.FromResult(ConnectCommandOutcome.Applied);
            });

        t.PushEvent(new WireEvent(
            "hm://connect-state/v1/connect/volume",
            [0x08, 0xA6, 0x8D, 0x01, 0x1A, 0x00, 0x22, 0x04, 0x77, 0x6C, 0x61, 0x6E]));

        Assert.Equal(18086, await applied.Task.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task Router_ExactReplay_IsAcknowledgedButDispatchedOnce()
    {
        var t = new StubTransport();
        int calls = 0;
        var applied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var r = new ConnectCommandRouter(t, (command, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1) applied.TrySetResult();
            return Task.FromResult(ConnectCommandOutcome.Applied);
        });
        var req = Req(
            "{\"message_id\":77,\"sent_by_device_id\":\"phone\",\"command\":{\"endpoint\":\"pause\"}}",
            "77/phone");

        t.PushRequest(req);
        t.PushRequest(req with { RequestId = "77/phone/replay" });
        await applied.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(30);

        Assert.Equal(1, calls);
        Assert.Equal(RequestResult.Success, t.LastReply);
    }
}
