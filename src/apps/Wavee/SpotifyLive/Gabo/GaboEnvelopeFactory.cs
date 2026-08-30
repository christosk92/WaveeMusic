using System;
using System.Diagnostics;
using System.Security.Cryptography;
using Google.Protobuf;
using Wavee.Protocol.EventSender;

namespace Wavee.SpotifyLive.Gabo;

public sealed record GaboContext(
    byte[] ClientIdBytes,
    byte[] InstallationIdBytes,
    byte[] AppSessionIdBytes,
    string AppVersionString,
    long AppVersionCode,
    string PlatformType,
    string DeviceManufacturer,
    string DeviceModel,
    string DeviceIdString,
    string OsVersion);

public static class GaboEnvelopeFactory
{
    const string SdkVersionName = "0.9.4-rl-essopt-loginsend-onlinesend-bcdsend-heartbeat300.0s/30.0s-modern-payload125kB-batch100";
    const string SdkType = "cpp";
    const long MonotonicClockId = 9;
    static readonly Stopwatch Monotonic = Stopwatch.StartNew();

    public static EventEnvelope Build(string eventName, byte[] messageBytes, GaboContext ctx,
        byte[] sequenceId, long sequenceNumber)
    {
        var envelope = new EventEnvelope
        {
            EventName = eventName,
            SequenceId = ByteString.CopyFrom(sequenceId),
            SequenceNumber = sequenceNumber,
        };
        envelope.EventFragment.Add(Fragment("message", messageBytes));
        envelope.EventFragment.Add(Fragment("context_client_id", new ClientId { Value = ByteString.CopyFrom(ctx.ClientIdBytes) }.ToByteArray()));
        envelope.EventFragment.Add(Fragment("context_installation_id", new InstallationId { Value = ByteString.CopyFrom(ctx.InstallationIdBytes) }.ToByteArray()));
        envelope.EventFragment.Add(Fragment("context_application_desktop", new ApplicationDesktop
        {
            VersionString = ctx.AppVersionString,
            VersionCode = ctx.AppVersionCode,
            SessionId = ByteString.CopyFrom(ctx.AppSessionIdBytes),
        }.ToByteArray()));
        envelope.EventFragment.Add(Fragment("context_device_desktop", new DeviceDesktop
        {
            PlatformType = ctx.PlatformType,
            DeviceManufacturer = ctx.DeviceManufacturer,
            DeviceModel = ctx.DeviceModel,
            DeviceId = ctx.DeviceIdString,
            OsVersion = ctx.OsVersion,
        }.ToByteArray()));
        envelope.EventFragment.Add(Fragment("context_time", new Time { Value = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }.ToByteArray()));
        envelope.EventFragment.Add(Fragment("context_monotonic_clock", new MonotonicClock
        {
            Id = MonotonicClockId,
            Value = Monotonic.ElapsedMilliseconds,
        }.ToByteArray()));
        envelope.EventFragment.Add(Fragment("context_sdk", new Wavee.Protocol.EventSender.Sdk { VersionName = SdkVersionName, Type = SdkType }.ToByteArray()));
        envelope.EventFragment.Add(new EventEnvelope.Types.EventFragment { Name = "context_client_context_id", Data = ByteString.Empty });
        return envelope;
    }

    static EventEnvelope.Types.EventFragment Fragment(string name, byte[] data) =>
        new() { Name = name, Data = ByteString.CopyFrom(data) };
}
