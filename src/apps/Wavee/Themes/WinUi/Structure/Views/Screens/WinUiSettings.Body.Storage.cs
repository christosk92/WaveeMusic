// ── Themes/WinUi/Structure/Views/Screens/WinUiSettings.Body.Storage.cs — Storage's WinUI cards: the census, the folders, the budget editor ────────────────
//
// The census (the phase, the snapshot, the cache status, the key count, the metadata stats; the first visit starts it; a clear re-runs it) is the core's and
// arrives as `SettingsPageProps.Storage`. On this PC leads with its block (a spinner while it reads, the usage card with the nine fixed hues on its bar and
// its legend, or the failed bar with Retry); each folder row carries its hue accent, its size (simply ABSENT until the census lands — never "—", never
// "0 B"), "Open folder" (the folder dial's own writer) and, where it has one, its clear (the child dial: it confirms first). The playback cache's budget is
// the three-mode editor (Fixed size · Drive share · Unlimited) with its meter; the cache location's PATH is its description.
//
// RESIDENT CACHE (ch 27 G2, decided): 0.3 has no resident-playlist cache and no per-table census, so there is no "In memory" section.

using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Sdk.Streams;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.WinUi;

internal static partial class WinUiSettings
{
    // N9 — nine hand-picked hues, THEME-INVARIANT (`WinUiSettingsRules.StorageHues`), indexed in `StorageRules.Parts` order: library, runtime, logs, local store,
    // audio bodies, license keys, images, AI models, other app data.
    static readonly string[] s_storagePartKeys =
    [
        Strings.Settings.Storage.Library, Strings.Settings.Storage.Runtime, Strings.Settings.Storage.Logs,
        Strings.Settings.Storage.LocalStore, Strings.Settings.Storage.AudioBodies, Strings.Settings.Storage.LicenseKeys,
        Strings.Settings.Storage.ImageCache, Strings.Settings.Storage.AiModels, Strings.Settings.Storage.OtherData,
    ];

    static readonly NumberBox.NumberBoxOptions s_percentBox = new()
    {
        Minimum = 0, Maximum = 90, SmallChange = 1, Width = WinUiSettingsRules.BudgetBoxWidth,
        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        Formatter = static v => v <= 0 ? Loc.Get(Strings.Settings.Storage.AutoTenPercent) : v.ToString("0", CultureInfo.InvariantCulture) + "%",
    };

    static readonly NumberBox.NumberBoxOptions s_gibBox = new()
    {
        Minimum = 0.0625, Maximum = 1 << 20, SmallChange = 1, Width = WinUiSettingsRules.BudgetBoxWidth,
        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        Formatter = static v => v.ToString("0.###", CultureInfo.InvariantCulture) + " GB",
    };

    // ══ 1. THE CENSUS (W15 loading · W16 ready · W18 failed) ══════════════════════════════════════════════════════

    /// <summary>On this PC's lead: the spinner while the census reads, the usage card, or the failed bar with Retry.</summary>
    static Element StorageCensus(SettingsStorageProps storage)
    {
        var s = storage.Snapshot;
        Element census = storage.Phase == Settings.StorageLoadPhase.Failed
            ? InfoBar.Create(InfoBarSeverity.Error, Loc.Get(Strings.Settings.Storage.ReadFailed),
                storage.Error ?? Loc.Get(Strings.Common.ErrorTitle), isClosable: false,
                actionButton: StdButton(Loc.Get(Strings.Common.Retry), static () => Settings.RefreshStorage()))
            : s is null
                ? new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, Padding = new Edges4(0f, Spacing.L, 0f, Spacing.L),
                    Children =
                    [
                        ProgressRing.Indeterminate(size: WinUiSettingsRules.SpinnerSize),
                        new TextEl(Loc.Get(Strings.Settings.Storage.Reading)) { Size = WinUiSettingsRules.CaptionSize, Color = Tok.TextSecondary },
                    ],
                }
                : StorageUsageCard(s);
        return new BoxEl { Key = "settings:storage:census", Direction = 1, AlignSelf = FlexAlign.Stretch, MinWidth = 0f, Children = [census] };
    }

    /// <summary>W16's usage card: "total" + the 18/700 figure, the 10-DIP segmented bar (each segment Grow = its bytes, so widths are exact proportions), and a
    /// wrapping legend. Zero-byte categories leave both; all-zero → no bar.</summary>
    static Element StorageUsageCard(Settings.StorageSnapshot s)
    {
        long[] parts = Settings.StorageRules.Parts(s);
        var bar = new List<Element>(parts.Length);
        var legend = new List<Element>(parts.Length);
        for (int i = 0; i < parts.Length; i++)
        {
            long bytes = parts[i];
            if (bytes <= 0) continue;
            bar.Add(new BoxEl { Grow = bytes, Height = WinUiSettingsRules.MeterHeight, Fill = WinUiSettingsRules.StorageHues[i], Corners = CornerRadius4.All(2f) });
            legend.Add(new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, Grow = 1f, Basis = 0f, MinWidth = WinUiSettingsRules.LegendMinWidth,
                Children =
                [
                    new BoxEl { Width = WinUiSettingsRules.MeterHeight, Height = WinUiSettingsRules.MeterHeight, Corners = CornerRadius4.All(2f), Fill = WinUiSettingsRules.StorageHues[i], Shrink = 0f },
                    new TextEl(Loc.Get(s_storagePartKeys[i])) { Size = WinUiSettingsRules.CaptionSize, Color = Tok.TextPrimary, Grow = 1f },
                    new TextEl(Settings.StorageFormat.Bytes(bytes)) { Size = WinUiSettingsRules.CaptionSize, Color = Tok.TextSecondary, Shrink = 0f },
                    R.Type.MicroMeta(Settings.StorageFormat.Percent(bytes, s.Total).ToString(CultureInfo.InvariantCulture) + "%")
                        with { Color = Tok.TextTertiary, Width = WinUiSettingsRules.PercentColumnWidth, Shrink = 0f },
                ],
            });
        }

        return new BoxEl
        {
            Direction = 1, Gap = Spacing.M, Padding = new Edges4(Spacing.L, Spacing.M, Spacing.L, Spacing.M),
            Corners = CornerRadius4.All(Radii.Card), Fill = Tok.FillCardSecondary, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
            Children =
            [
                new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S,
                    Children =
                    [
                        new TextEl(Loc.Get(Strings.Settings.Storage.Total)) { Size = WinUiSettingsRules.CaptionSize, Weight = 600, Color = Tok.TextSecondary },
                        new TextEl(Settings.StorageFormat.Bytes(s.Total)) { Size = WinUiSettingsRules.TotalFigureSize, Weight = 700, Color = Tok.TextPrimary },
                    ],
                },
                Settings.StorageRules.ShowsBar(s)
                    ? new BoxEl
                    {
                        Direction = 0, Height = WinUiSettingsRules.MeterHeight, Gap = WinUiSettingsRules.MeterGap, ClipToBounds = true, Corners = CornerRadius4.All(3f),
                        Fill = Tok.StrokeDividerDefault, Children = bar.ToArray(),
                    }
                    : new BoxEl(),
                new BoxEl { Direction = 0, Gap = Spacing.S, Wrap = true, Children = legend.ToArray() },
            ],
        };
    }

    // ══ 2. THE FOLDER ROWS ════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>A folder row: the 3-DIP hue accent + the card, whose action lane carries the size, "Open folder" (the folder dial's writer) and its clear (the
    /// child dial, hosted). The card sits in a COLUMN so a wrapping description grows the card and the accent with it (#132).</summary>
    static Element StorageRowCard(RowCtx x)
    {
        var row = x.Row;
        var storage = x.Page.Storage;
        var s = storage.Snapshot;
        int hue = row.Id switch
        {
            "storage.library" => 0, "storage.runtime" => 1, "storage.logs" => 2, "storage.localStore" => 3, "storage.cache.audioBodies" => 4,
            "storage.cache.licenseKeys" => 5, "storage.imageCache" => 6, "storage.aiModels" => 7, _ => 8,
        };
        long? size = hue switch
        {
            0 => s?.LibraryDb, 1 => s?.Runtime, 2 => s?.Logs, 3 => s?.Store, 4 => s?.AudioBody, 5 => s?.LicenseDb, 6 => s?.ImageCache, 7 => s?.AiModels, _ => s?.Other,
        };
        string sub = hue switch
        {
            2 => s is null ? Loc.Get(Strings.Settings.Storage.LogsSubEmpty) : s.LogFiles == 1 ? Loc.Get(Strings.Settings.Storage.LogsSubOne) : Strings.Settings.Storage.LogsSub(s.LogFiles),
            5 => storage.KeyCount is not { } keys ? Loc.Get(Strings.Settings.Storage.LicenseKeysSub)
                : keys == 1 ? Loc.Get(Strings.Settings.Storage.LicenseKeysCountOne) : Strings.Settings.Storage.LicenseKeysCount(keys),
            7 => s is null ? Loc.Get(Strings.Settings.Storage.AiModelsSub)
                : s.AiModels <= 0 ? Loc.Get(Strings.Settings.Storage.AiModelsNone)
                : s.AiLanguages == 1 ? Loc.Get(Strings.Settings.Storage.AiModelsCountOne)
                : s.AiLanguages > 1 ? Strings.Settings.Storage.AiModelsCount(s.AiLanguages) : Loc.Get(Strings.Settings.Storage.AiModelsSub),
            _ => row.Description ?? "",
        };

        var dial = row.Dial;
        var actions = new List<Element>(3);
        if (size is { } bytes) actions.Add(R.Type.DenseMeta(Settings.StorageFormat.Bytes(bytes)) with { Color = Tok.TextSecondary, Shrink = 0f });
        actions.Add(LinkButton(Loc.Get(Strings.Settings.Storage.OpenFolder), () => Press(dial)));
        foreach (var child in row.Children)
            actions.Add(ChildHost(x, child, ClearButton(child)));
        var card = BaseCard(x, new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, Children = actions.ToArray() })
            with { Label = Loc.Get(s_storagePartKeys[hue]), Description = sub };
        return new BoxEl
        {
            Direction = 0, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
            Children =
            [
                new BoxEl { Width = WinUiSettingsRules.StorageAccentWidth, AlignSelf = FlexAlign.Stretch, Fill = WinUiSettingsRules.StorageHues[hue], Corners = new CornerRadius4(2f, 0f, 0f, 2f) },
                new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, AlignSelf = FlexAlign.Stretch, Children = [DialCard(card)] },
            ],
        };
    }

    /// <summary>N11: a clear opens the ONE confirm (Cancel default, the dial's own words); the confirmed writer runs off-thread and toasts.</summary>
    static Element ClearButton(SettingsBodyRow row)
    {
        var dial = row.Dial;
        if (dial.Confirm is not { } confirm) return StdButton(row.Label, () => Press(dial), isEnabled: row.Enabled);
        return StdButton(Loc.Get(confirm.TitleKey),
            () => ConfirmThen(Loc.Get(confirm.TitleKey), Loc.Get(confirm.BodyKey), Loc.Get(confirm.PrimaryKey), () => Press(dial)),
            isEnabled: row.Enabled);
    }

    /// <summary>The cache location: its PATH as the description (W16); Choose a location and Use the default (the child) need the cache; Open folder.</summary>
    static Element CacheLocationCard(RowCtx x)
    {
        var row = x.Row;
        var kids = new List<Element>(3)
        {
            StdButton(Loc.Get(Strings.Settings.Storage.ChooseLocation), () => Press(row.Dial), isEnabled: row.Enabled),
        };
        foreach (var child in row.Children)
            kids.Add(ChildHost(x, child, StdButton(Loc.Get(Strings.Settings.Storage.UseDefaultLocation), () => Press(child.Dial), isEnabled: child.Enabled)));
        kids.Add(LinkButton(Loc.Get(Strings.Settings.Storage.OpenFolder), static () => SettingsVerbs.OpenStorageFolder("cacheLocation")));
        var card = BaseCard(x, new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, Wrap = s_layout.StacksControls, Children = kids.ToArray() })
            with { Label = Loc.Get(Strings.Settings.Storage.CacheLocation), Description = x.Page.Storage.AudioDirectory, Enabled = true };
        return DialCard(card);
    }

    // ══ 3. THE AUDIO BODY BUDGET (W17: Fixed size · Drive share · Unlimited) ═══════════════════════════════════════

    /// <summary>The budget editor: the mode as segments, the mode's editor (a preset and an exact size, the drive's share, or the reserve's line), the meter and
    /// its two lines. Its own component: the editors bind one signal each for their life.</summary>
    static Element BudgetCard(RowCtx x)
        => DialCard(BaseCard(x, Embed.Comp(new BudgetProps(x), static () => new BudgetEditor())) with { Form = s_layout.StacksControls ? CardForm.Vertical : CardForm.Right });

    sealed record BudgetProps(RowCtx X);

    sealed class BudgetEditor : SettingsComponent
    {
        public override Element Render()
        {
            UseLayout();
            var x = UseProps<BudgetProps>().X;
            var row = x.Row;
            var storage = x.Page.Storage;
            var s = storage.Snapshot;
            var status = storage.Audio;
            var presetRow = Child(x, "storage.cache.budget.preset");
            var sizeRow = Child(x, "storage.cache.budget.size");
            var percentRow = Child(x, "storage.cache.budget.percent");

            int mode = Math.Clamp(row.Value is int m ? m : (int)AudioCacheBudgetMode.DriveShare, 0, 2);
            double gib = SettingsFacts.AudioBudgetBytes() / (double)(1L << 30);
            int preset = presetRow?.Value is int p ? p : 0;
            double percent = percentRow?.Value is int pc ? pc : 0;
            var modeSel = UseSignal(mode);
            var presetSel = UseSignal(preset);
            var gibSig = UseSignal(gib);
            var percentSig = UseSignal(percent);
            UseEffect(() =>
            {
                modeSel.SetIfChanged(mode);
                presetSel.SetIfChanged(preset);
                gibSig.SetIfChanged(gib);
                percentSig.SetIfChanged(percent);
            }, DepKey.From(mode, preset, (int)Math.Round(gib * 1024.0), (int)percent));

            long used = status?.Bytes ?? s?.AudioBody ?? 0;
            var (fraction, over) = Settings.StorageRules.BudgetMeter(used, status?.BudgetBytes);
            var modeDial = row.Dial;

            Element editor = mode switch
            {
                (int)AudioCacheBudgetMode.FixedBytes => new BoxEl
                {
                    Key = "budget:fixed", Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, Wrap = s_layout.StacksControls,
                    Children =
                    [
                        Combo(Settings.StorageFormat.BodyBudgetLabels, presetSel, width: WinUiSettingsRules.BudgetComboWidth, onChange: i =>
                        {
                            if ((uint)i >= (uint)Settings.StorageFormat.BodyBudgetBytes.Length) return;
                            gibSig.Value = Settings.StorageFormat.BodyBudgetBytes[i] / (double)(1L << 30);
                            if (presetRow?.Dial.Writer.Choose is { } choose && choose(i)) Settings.Bump();
                        }),
                        NumberField(gibSig, v =>
                        {
                            if (!double.IsFinite(v)) return;
                            long bytes = Settings.StorageFormat.GiBToBytes(v);
                            presetSel.Value = Settings.StorageFormat.BodyBudgetExactIndex(bytes);   // a typed value snaps to Custom
                            gibSig.Value = bytes / (double)(1L << 30);
                            if (sizeRow?.Dial.Writer.Slide is { } slide && slide((float)v)) Settings.Bump();
                        }, s_gibBox, "budget:gib"),
                    ],
                },
                (int)AudioCacheBudgetMode.DriveShare => NumberField(percentSig, v =>
                {
                    percentSig.Value = SettingsVerbs.ClampBudgetPercent(v);
                    if (percentRow?.Dial.Writer.Slide is { } slide && slide((float)v)) Settings.Bump();
                }, s_percentBox, "budget:share"),
                _ => new TextEl(Loc.Get(Strings.Settings.Storage.UnlimitedReserve)) { Key = "budget:unlimited", Size = WinUiSettingsRules.CaptionSize, Color = Tok.TextSecondary },
            };

            string budgetLabel = status?.BudgetBytes is { } limit ? Settings.StorageFormat.Bytes(limit) : Loc.Get(Strings.Settings.Storage.Unlimited);
            string[] modes = row.Choices.Length == 3 ? [.. row.Choices]
                : [Loc.Get(Strings.Settings.Storage.FixedSize), Loc.Get(Strings.Settings.Storage.DriveShare), Loc.Get(Strings.Settings.Storage.Unlimited)];
            return new BoxEl
            {
                Direction = 1, Gap = WinUiSettingsRules.BudgetGap,
                Children =
                [
                    Selector(modes, modeSel, onChange: i =>
                    {
                        if (modeDial.Writer.Choose is { } choose && choose(i)) Settings.Bump();
                    }),
                    editor,
                    ProgressBar.Determinate(fraction, width: WinUiSettingsRules.BudgetMeterWidth, state: over ? ProgressBarState.Error : ProgressBarState.Normal),
                    R.Type.MicroMeta(Strings.Settings.Storage.UsedOfBudget(Settings.StorageFormat.Bytes(used), budgetLabel)) with { Color = Tok.TextSecondary },
                    status is { Available: false }
                        ? R.Type.MicroMeta(Loc.Get(Strings.Settings.Storage.LocationUnavailable)) with { Color = Tok.SystemFillCritical }
                        : R.Type.MicroMeta(Strings.Settings.Storage.FreeReserve(Settings.StorageFormat.Bytes(status?.ReserveBytes ?? 0))) with { Color = Tok.TextTertiary },
                ],
            };
        }
    }

    // ══ 4. THE METADATA CACHE (library.db's cache tier) ═══════════════════════════════════════════════════════════

    /// <summary>The metadata cache's ceiling: the store sweep reads it on its next pass; the tick asks for that pass now, so a lowered ceiling trims without a restart.</summary>
    static Element MetadataBudgetCard(RowCtx x)
        => DialCard(BaseCard(x, DialCombo(x.Row, Settings.StorageFormat.MetaBudgetLabels, WinUiSettingsRules.BudgetComboWidth)));

    /// <summary>The metadata cache: the census's stats sentence ("N extras", localized) plus the un-localized evictable-vs-budget suffix, and its clear.</summary>
    static Element MetadataClearCard(RowCtx x)
    {
        var stats = x.Page.Storage.Meta;
        string sub = stats is not { } m
            ? Loc.Get(Strings.Settings.Storage.MetadataCacheSub)
            : Loc.Format("settings.storage.metadataCacheStats",
                  ("db", Settings.StorageFormat.Bytes(m.DbBytes)), ("cache", Settings.StorageFormat.Bytes(m.CacheBytes)),
                  ("pinned", Settings.StorageFormat.Bytes(m.PinnedBytes)), ("rows", m.EntityRows), ("pinnedRows", m.PinnedRows),
                  ("overviews", m.OverviewRows), ("extensions", m.ExtensionRows))
              + " · evictable " + Settings.StorageFormat.Bytes(m.EvictableBytes) + " of " + Settings.StorageFormat.Bytes(m.BudgetBytes) + " budget";
        return DialCard(BaseCard(x, ClearButton(x.Row)) with { Label = Loc.Get(Strings.Settings.Storage.MetadataCache), Description = sub });
    }
}
