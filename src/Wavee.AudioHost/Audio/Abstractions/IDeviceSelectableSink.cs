using Wavee.Playback.Contracts;

namespace Wavee.AudioHost.Audio.Abstractions;

/// <summary>
/// Optional capability for <see cref="IAudioSink"/> implementations that support
/// enumerating and switching between multiple local output devices (e.g. PortAudio).
/// </summary>
public interface IDeviceSelectableSink
{
    /// <summary>Friendly name of the device the sink is currently writing to.</summary>
    string? CurrentDeviceName { get; }

    /// <summary>
    /// Enumerate all local output devices the sink could switch to.
    /// </summary>
    IReadOnlyList<AudioOutputDeviceDto> EnumerateOutputDevices();

    /// <summary>
    /// Switch the active output device to the one at the given index.
    /// Flushes/re-opens the underlying stream on a best-effort basis and
    /// preserves any buffered PCM so playback resumes without gaps.
    /// </summary>
    Task SwitchToDeviceAsync(int deviceIndex, CancellationToken ct = default);

    /// <summary>
    /// Forces the underlying audio backend to re-scan the live system device list,
    /// so newly-plugged devices (e.g. Bluetooth headphones) show up on the next
    /// <see cref="EnumerateOutputDevices"/> call. May briefly interrupt the stream.
    /// </summary>
    void RefreshDeviceList();

    /// <summary>
    /// Re-scans the device list and reopens the output stream on the current Windows
    /// system default device. Call this when Windows signals that the default output
    /// changed (e.g. Bluetooth headphones connected and Windows auto-selected them).
    /// </summary>
    Task SwitchToDefaultDeviceAsync(CancellationToken ct = default);
}
