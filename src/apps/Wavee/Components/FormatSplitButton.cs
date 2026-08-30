using System;
using System.Collections.Generic;
using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Signals;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>Play, with the audio format attached: a primary play half and a caret that opens the formats this item
/// ACTUALLY has (extended-metadata kind 5), each with its measured average bitrate.
///
/// The format lives on the play control rather than in a separate panel because that is the scope it applies to — one
/// playable, one play. A track and its live version are different audio entities with different format ladders, so a
/// single drawer-level picker would be telling a lie about at least one of them.</summary>
sealed class FormatSplitButton : Component
{
    readonly string _uri;
    readonly Action<string> _onPlay;

    Ref<NodeHandle> _anchor = null!;
    Ref<OverlayHandle?> _handle = null!;

    public FormatSplitButton(string uri, Action<string> onPlay)
    {
        _uri = uri;
        _onPlay = onPlay;
    }

    public override Element Render()
    {
        var svc = UseContext(Services.Slot);
        var overlay = UseContext(Overlay.Service);
        _anchor = UseRef<NodeHandle>(default);
        _handle = UseRef<OverlayHandle?>(null);

        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Stretch, Shrink = 0f,
            OnRealized = h => _anchor.Value = h,
            Children =
            [
                // Primary half — plays at whatever format is currently in force (override, else the user's default).
                new BoxEl
                {
                    Width = 30f, Height = 28f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Corners = new CornerRadius4(Radii.Control, 0f, 0f, Radii.Control),
                    Fill = Tok.FillControlDefault, HoverFill = Tok.AccentDefault,
                    BorderWidth = 1f, BorderColor = Tok.StrokeControlDefault,
                    Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
                    OnClick = () => _onPlay(_uri),
                    Children = [Icon(Icons.Play, 11f, Tok.TextPrimary)],
                },
                // Caret half — opens the ladder.
                new BoxEl
                {
                    Width = 20f, Height = 28f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Corners = new CornerRadius4(0f, Radii.Control, Radii.Control, 0f),
                    Fill = Tok.FillControlDefault, HoverFill = Tok.AccentDefault,
                    BorderWidth = 1f, BorderColor = Tok.StrokeControlDefault,
                    Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
                    OnClick = () => OpenMenu(svc, overlay),
                    Children = [Icon(Icons.ChevronDown, 9f, Tok.TextSecondary)],
                },
            ],
        };
    }

    void OpenMenu(Services? svc, IOverlayService? overlay)
    {
        if (svc is null || overlay is null) return;
        if (_handle.Value is { IsOpen: true } open) { open.Close(); return; }
        // Resolve lazily: the ladder is only needed once someone reaches for it, and the expansion service caches per
        // track, so after the drawer's own fetch this is a dictionary hit.
        _ = LoadAndOpenAsync(svc, overlay);
    }

    async System.Threading.Tasks.Task LoadAndOpenAsync(Services svc, IOverlayService overlay)
    {
        IReadOnlyList<AudioFormatOption> formats;
        try
        {
            var data = await svc.TrackExpansion.GetAsync(_uri).ConfigureAwait(true);
            formats = data.Formats;
        }
        catch { formats = Array.Empty<AudioFormatOption>(); }

        // Nothing to choose between — opening an empty menu would be a dead end, so the caret simply does nothing.
        if (formats.Count == 0) return;

        int? current = svc.TrackExpansion.FormatOverrideFor(_uri);
        var items = new MenuFlyoutItem[formats.Count + 1];
        for (int i = 0; i < formats.Count; i++)
        {
            var fmt = formats[i];
            bool isCurrent = current == fmt.FormatId;
            // Radio, not Toggle: the formats are mutually exclusive, and WinUI's radio column (E915 bullet) is what
            // communicates "pick one" rather than "turn several on".
            items[i] = MenuFlyoutItem.RadioItem(
                fmt.Label + "   " + Kbps(fmt.AverageBitrate),
                isCurrent,
                () => svc.TrackExpansion.SetFormatOverride(_uri, fmt.FormatId),
                enabled: fmt.AvailableOnDevice);
        }
        // An explicit way back to the global preference — without it an override is a one-way door.
        items[formats.Count] = MenuFlyoutItem.RadioItem(
            Loc.Get(Strings.Detail.Versions.UseDefaultQuality),
            current is null,
            () => svc.TrackExpansion.SetFormatOverride(_uri, null));

        _handle.Value = overlay.Open(
            () => _anchor.Value,
            () => MenuFlyout.Create(items, () => _handle.Value?.Close()),
            FlyoutPlacement.BottomEdgeAlignedRight,
            new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss) { ConstrainToRootBounds = false });
        _handle.Value.ClosedAction = () => _handle.Value = null;
    }

    static string Kbps(int bitsPerSecond)
        => bitsPerSecond <= 0
            ? ""
            : (bitsPerSecond / 1000).ToString(CultureInfo.InvariantCulture) + " kbps";
}
