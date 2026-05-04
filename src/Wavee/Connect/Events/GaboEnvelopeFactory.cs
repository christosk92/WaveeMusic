using System.Diagnostics;
using Google.Protobuf;
using Wavee.Protocol.EventSender;

namespace Wavee.Connect.Events;

/// <summary>
/// Builds <see cref="EventEnvelope"/> instances for gabo-receiver-service
/// (<c>spclient.wg.spotify.com/gabo-receiver-service/v3/events/</c>).
///
/// An envelope is a per-event wrapper carrying the event_name (e.g.
/// <c>"RawCoreStream"</c>) plus a list of <see cref="EventEnvelope.Types.EventFragment"/>s.
/// One fragment named <c>"message"</c> carries the serialized event payload
/// (e.g. a <see cref="Protocol.EventSender.Events.RawCoreStream"/> message);
/// the rest are the shared context block (client id, installation id, app /
/// device descriptors, time, sdk).
/// </summary>
public static class GaboEnvelopeFactory
{
    // Mirror desktop's context_sdk strings verbatim. Spotify's anti-fraud
    // pipeline drops batches whose SDK identifies as a non-first-party
    // client (verified 2026-04-28: every Wavee event was returning
    // reason=3 with sdk={'wavee-1.0','csharp'}; spot was accepted with
    // sdk={'0.9.4-rl-...','cpp'}). Match what the C++ desktop client emits.
    private const string SdkVersionName = "0.9.4-rl-essopt-loginsend-onlinesend-bcdsend-heartbeat300.0s/30.0s-modern-payload125kB-batch100";
    private const string SdkType = "cpp";

    // Verified from spot SAZ 255: monotonic_clock {id=4, value=534380432}.
    // The id is a small integer (clock identifier — possibly CLOCK_BOOTTIME
    // index 4), value is a per-clock monotonic measurement (looks like
    // microseconds-since-boot or similar). Wavee was sending Unix-epoch-ms
    // as id which doesn't match the desktop wire shape — flip them.
    private const long MonotonicClockId = 4;
    private static readonly Stopwatch _monotonic = Stopwatch.StartNew();

    /// <summary>
    /// Wraps a serialized event payload in a complete EventEnvelope, including
    /// all the standard context fragments. <paramref name="eventName"/> must
    /// match the message type name (e.g. <c>"RawCoreStream"</c>).
    /// </summary>
    /// <param name="eventName">Per-event name (matches the message type).</param>
    /// <param name="messageBytes">Serialized bytes of the per-event payload.</param>
    /// <param name="ctx">Static device/client context for fragment population.</param>
    /// <param name="sequenceId">20-byte session-scoped sequence id.</param>
    /// <param name="sequenceNumber">Monotonically incrementing per (sequence_id, event_name).</param>
    public static EventEnvelope BuildEnvelope(
        string eventName,
        byte[] messageBytes,
        GaboContext ctx,
        byte[] sequenceId,
        long sequenceNumber)
    {
        var envelope = new EventEnvelope
        {
            EventName = eventName,
            SequenceId = ByteString.CopyFrom(sequenceId),
            SequenceNumber = sequenceNumber,
        };

        // Per-event payload fragment.
        envelope.EventFragment.Add(new EventEnvelope.Types.EventFragment
        {
            Name = "message",
            Data = ByteString.CopyFrom(messageBytes),
        });

        // Shared context fragments — order matters less, but match the
        // desktop client's order for parity (verified via Fiddler decode).
        envelope.EventFragment.Add(SerializeFragment(
            "context_client_id",
            new ClientId { Value = ByteString.CopyFrom(ctx.ClientIdBytes) }));
        envelope.EventFragment.Add(SerializeFragment(
            "context_installation_id",
            new InstallationId { Value = ByteString.CopyFrom(ctx.InstallationIdBytes) }));
        envelope.EventFragment.Add(SerializeFragment(
            "context_application_desktop",
            new ApplicationDesktop
            {
                VersionString = ctx.AppVersionString,
                VersionCode = ctx.AppVersionCode,
                // 16-byte stable per-app-process session id. Desktop ALWAYS
                // populates this; omitting it is one of the signals the
                // server uses to drop a batch with reason=3.
                SessionId = ByteString.CopyFrom(ctx.AppSessionIdBytes),
            }));
        envelope.EventFragment.Add(SerializeFragment(
            "context_device_desktop",
            new DeviceDesktop
            {
                PlatformType = ctx.PlatformType,
                DeviceManufacturer = ctx.DeviceManufacturer,
                DeviceModel = ctx.DeviceModel,
                DeviceId = ctx.DeviceIdString,
                OsVersion = ctx.OsVersion,
            }));
        envelope.EventFragment.Add(SerializeFragment(
            "context_time",
            new Time { Value = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }));
        envelope.EventFragment.Add(SerializeFragment(
            "context_monotonic_clock",
            // Match desktop wire shape: small clock id + monotonic micros.
            new MonotonicClock { Id = MonotonicClockId, Value = _monotonic.Elapsed.Ticks * 100 / 1000 }));
        envelope.EventFragment.Add(SerializeFragment(
            "context_sdk",
            new Sdk { VersionName = SdkVersionName, Type = SdkType }));
        // Empty payload — verified by structural decode of spot SAZ session
        // 255 (3 envelopes, all carry context_client_context_id with 0 bytes
        // of data). Sending non-empty bytes here is one of the signals that
        // gets the entire batch rejected with reason=3.
        envelope.EventFragment.Add(new EventEnvelope.Types.EventFragment
        {
            Name = "context_client_context_id",
            Data = ByteString.Empty,
        });

        return envelope;
    }

    private static EventEnvelope.Types.EventFragment SerializeFragment(string name, IMessage payload)
        => new()
        {
            Name = name,
            Data = payload.ToByteString(),
        };
}

/// <summary>
/// Static device/client context shared across every gabo event in a session.
/// Mirrors the per-fragment data the desktop client packs into every batch.
/// </summary>
public sealed record GaboContext
{
    public required byte[] ClientIdBytes { get; init; }            // 16-byte form of the keymaster client id
    public required byte[] InstallationIdBytes { get; init; }      // 16-byte stable per-install id
    public required byte[] ClientContextIdBytes { get; init; }     // 20-byte session-scoped id
    public required string AppVersionString { get; init; }         // e.g. "1.2.88.483"
    public required long AppVersionCode { get; init; }             // e.g. 128800483
    public required byte[] AppSessionIdBytes { get; init; }        // 16-byte per-app-process id (ApplicationDesktop.session_id)
    public required string PlatformType { get; init; }             // "windows" / "macos" / "linux"
    public required string DeviceManufacturer { get; init; }       // BIOS-reported (e.g. "Microsoft Corporation")
    public required string DeviceModel { get; init; }              // BIOS-reported (e.g. "Microsoft Surface Laptop, 7th Edition")
    public required string DeviceIdString { get; init; }           // Windows machine SID (e.g. "S-1-5-21-...")
    public required string OsVersion { get; init; }                // 3-part (e.g. "10.0.26200" — NO trailing .0)
}
