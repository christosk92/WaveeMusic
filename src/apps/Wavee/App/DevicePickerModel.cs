using System;
using System.Collections.Generic;
using Wavee.Core;

namespace Wavee;

/// <summary>The role of a picker row (engine-free so the composition is unit-tested without the MenuFlyout type).</summary>
public enum DevicePickerRowKind
{
    Header,          // a disabled section-header row ("This computer" / "Spotify Connect")
    Separator,       // divider between sections
    LocalDefault,    // the "System default" radio (DeviceId == null)
    LocalDevice,     // a specific this-computer output radio
    ConnectDevice,   // a Spotify Connect device radio (click = transfer)
    Empty,           // a disabled placeholder row (no-devices / hint)
}

/// <summary>One row in the two-section device picker — a POD the engine layer maps to a MenuFlyoutItem.</summary>
public readonly record struct DevicePickerRow(
    DevicePickerRowKind Kind,
    string Label,
    bool IsChecked = false,
    bool Enabled = true,
    string? DeviceId = null,
    string? Accelerator = null,
    LocalAudioDeviceKind LocalKind = LocalAudioDeviceKind.Unknown,
    DeviceKind ConnectKind = DeviceKind.Computer);

/// <summary>Pure (engine-free) builder for the two-section device picker (plan §C2): "This computer" (System default + the
/// local render endpoints) then a separator + "Spotify Connect" (the Connect roster, ThisDevice filtered out — this PC is
/// section 1). Unit-tested directly (DevicePickerItemsTests); the engine layer maps rows → MenuFlyoutItem.</summary>
public static class DevicePickerModel
{
    /// <summary>Hard cap on device labels — endpoint names are short (DeviceDesc-first) but Connect names and duplicate-name
    /// fallbacks can still be arbitrarily long, and the flyout sizes to its widest row.</summary>
    public const int MaxLabelChars = 48;

    internal static string CapLabel(string label) =>
        label.Length <= MaxLabelChars ? label : label[..(MaxLabelChars - 1)].TrimEnd() + "…";

    public static IReadOnlyList<DevicePickerRow> Build(
        IReadOnlyList<LocalAudioDevice> local,
        string? selectedLocalId,
        bool localSupported,
        bool weAreActiveOutput,
        IReadOnlyList<PlaybackDevice> connect,
        string? activeConnectId,
        string thisComputerLabel,
        string systemDefaultLabel,
        string spotifyConnectLabel,
        string unavailableLabel,
        string noDevicesLabel,
        string noDevicesHintLabel)
    {
        var rows = new List<DevicePickerRow>(local.Count + connect.Count + 4);
        string? acc = localSupported ? null : unavailableLabel;   // stale-truthful "Unavailable" only when unsupported

        // ── Section 1: This computer ──
        rows.Add(new DevicePickerRow(DevicePickerRowKind.Header, thisComputerLabel, Enabled: false));
        rows.Add(new DevicePickerRow(DevicePickerRowKind.LocalDefault, systemDefaultLabel,
            IsChecked: localSupported && weAreActiveOutput && selectedLocalId is null,
            Enabled: localSupported, DeviceId: null, Accelerator: acc));
        foreach (var d in local)
            rows.Add(new DevicePickerRow(DevicePickerRowKind.LocalDevice, CapLabel(d.Name),
                IsChecked: localSupported && weAreActiveOutput && selectedLocalId is not null
                           && string.Equals(d.Id, selectedLocalId, StringComparison.OrdinalIgnoreCase),
                Enabled: localSupported, DeviceId: d.Id, Accelerator: acc, LocalKind: d.Kind));

        // ── Section 2: Spotify Connect (ThisDevice filtered — represented by section 1) ──
        rows.Add(new DevicePickerRow(DevicePickerRowKind.Separator, ""));
        rows.Add(new DevicePickerRow(DevicePickerRowKind.Header, spotifyConnectLabel, Enabled: false));
        int connectCount = 0;
        foreach (var d in connect)
        {
            if (d.Kind == DeviceKind.ThisDevice) continue;
            connectCount++;
            rows.Add(new DevicePickerRow(DevicePickerRowKind.ConnectDevice, CapLabel(d.Name),
                IsChecked: string.Equals(d.Id, activeConnectId, StringComparison.OrdinalIgnoreCase) || d.IsActive,
                DeviceId: d.Id, ConnectKind: d.Kind));
        }
        if (connectCount == 0)
        {
            rows.Add(new DevicePickerRow(DevicePickerRowKind.Empty, noDevicesLabel, Enabled: false));
            rows.Add(new DevicePickerRow(DevicePickerRowKind.Empty, noDevicesHintLabel, Enabled: false));
        }
        return rows;
    }
}
