// ── Themes/Classic/Structure/Views/Screens/ClassicSettings.Storage.cs — Storage's cards (settings rework 7.1) ──────────────────────────────────────
//
// From `Screens\Settings.UI.Storage.cs`. The census under On this PC's header (spinner / usage card / failed bar), the nine fixed hues on the bar, the legend and
// the category rows' accents, the playback cache's rows (the three-mode budget editor, the cache location, the cached bodies and keys with their clears), the
// metadata cache, the factory reset. The census itself (the phase, the snapshot, the cache status, the key count, the metadata stats; the first visit to Storage
// starts it; a clear re-runs it) is the core's (`Settings.Host.cs`) and arrives as `SettingsPageProps.Storage`; the clears, the budget writes, the folder doors
// and the factory reset are the dials' writers. A clear that is its own child dial is keyed as its row (`Framed`).
//
// RESIDENT CACHE (ch 27 G2, decided): 0.3 has no resident-playlist cache and no per-table census, so the "In memory" section and its "Resident library cache" row
// are DELETED, with their strings.

using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Sdk.Streams;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

internal static partial class ClassicSettings
{
    // ══ 1. STABLE CONTROL STATE (0.2.9 `SettingsPage.Storage.cs` instance fields) ═════════════════════════════════════

    // N9 — nine hand-picked hues, THEME-INVARIANT (accent tints read as one blue on dark): `ClassicSettingsRules.StorageHues`, indexed in
    // `StorageRules.Parts` order: library, runtime, logs, local store, audio bodies, license keys, images, AI models, other app data.

    static readonly string[] s_storagePartKeys =
    [
        Strings.Settings.Storage.Library, Strings.Settings.Storage.Runtime, Strings.Settings.Storage.Logs,
        Strings.Settings.Storage.LocalStore, Strings.Settings.Storage.AudioBodies, Strings.Settings.Storage.LicenseKeys,
        Strings.Settings.Storage.ImageCache, Strings.Settings.Storage.AiModels, Strings.Settings.Storage.OtherData,
    ];

    // Stable control signals (the controls freeze the instance at mount); re-seeded from the document on every page mount.
    static readonly Signal<int> s_budgetMode = new((int)AudioCacheBudgetMode.DriveShare);
    static readonly Signal<int> s_budgetPreset = new(5);
    static readonly Signal<double> s_budgetGiB = new(32);
    static readonly Signal<double> s_budgetPercent = new(0);
    static readonly Signal<int> s_metaBudget = new(1);

    static readonly NumberBox.NumberBoxOptions s_percentBox = new()
    {
        Minimum = 0, Maximum = 90, SmallChange = 1, Width = ClassicSettingsRules.BudgetBoxWidth,
        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        Formatter = static v => v <= 0 ? Loc.Get(Strings.Settings.Storage.AutoTenPercent) : v.ToString("0", CultureInfo.InvariantCulture) + "%",
    };

    static readonly NumberBox.NumberBoxOptions s_gibBox = new()
    {
        Minimum = 0.0625, Maximum = 1 << 20, SmallChange = 1, Width = ClassicSettingsRules.BudgetBoxWidth,
        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        Formatter = static v => v.ToString("0.###", CultureInfo.InvariantCulture) + " GB",
    };

    /// <summary>The page mount: the budget editors' signals from their dials' values. (The census phase is the core's: a fresh mount censuses again on
    /// its first Storage visit.)</summary>
    static void SeedStorage()
    {
        if (Dial("storage.cache.budget").Current is int mode) s_budgetMode.Value = mode;
        long fixedBytes = SettingsFacts.AudioBudgetBytes();
        s_budgetGiB.Value = fixedBytes / (double)(1L << 30);
        if (Dial("storage.cache.budget.preset").Current is int preset) s_budgetPreset.Value = preset;
        if (Dial("storage.cache.budget.percent").Current is int percent) s_budgetPercent.Value = percent;
        if (Dial("storage.metadata.budget").Current is int meta) s_metaBudget.Value = meta;
    }

    // ══ 2. THE CENSUS AND THE ROWS (W15 loading · W16 ready · W18 failed) ═════════════════════════════════════════

    /// <summary>On this PC's census under its header: the spinner while it reads, the usage card when it lands, the failed bar with Retry.</summary>
    static Element StorageCensus(SettingsStorageProps storage)
    {
        var s = storage.Snapshot;
        if (storage.Phase == Settings.StorageLoadPhase.Failed)
            return InfoBar.Create(InfoBarSeverity.Error, Loc.Get(Strings.Settings.Storage.ReadFailed),
                storage.Error ?? Loc.Get(Strings.Common.ErrorTitle), isClosable: false,
                actionButton: StdButton(Loc.Get(Strings.Common.Retry), static () => Settings.RefreshStorage()));
        if (s is null)
            return new BoxEl
            {
                Key = "storage:census", Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, Padding = new Edges4(0f, Spacing.L, 0f, Spacing.L),
                Children =
                [
                    ProgressRing.Indeterminate(size: ClassicSettingsRules.SpinnerSize),
                    new TextEl(Loc.Get(Strings.Settings.Storage.Reading)) { Size = ClassicSettingsRules.CaptionSize, Color = Tok.TextSecondary },
                ],
            };
        return StorageUsageCard(s);
    }

    /// <summary>A Storage dial's card (the category rows with their sizes and doors, the budget editor, the cache location, the metadata cache, the factory
    /// reset); false for a Storage dial the generic grammar draws (the two cache switches).</summary>
    static bool TryStorageCard(SettingsBodyRow row, SettingsStorageProps storage, out Element? card)
    {
        var s = storage.Snapshot;
        var facts = SettingsFacts.Live;
        card = row.Id switch
        {
            "storage.library" => StorageRow(row, 0, Loc.Get(Strings.Settings.Storage.LibrarySub), s?.LibraryDb),
            "storage.runtime" => StorageRow(row, 1, Loc.Get(Strings.Settings.Storage.RuntimeSub), s?.Runtime),
            "storage.logs" => StorageRow(row, 2, s is null ? Loc.Get(Strings.Settings.Storage.LogsSubEmpty)
                : s.LogFiles == 1 ? Loc.Get(Strings.Settings.Storage.LogsSubOne) : Strings.Settings.Storage.LogsSub(s.LogFiles), s?.Logs),
            "storage.localStore" => StorageRow(row, 3, Loc.Get(Strings.Settings.Storage.LocalStoreSub), s?.Store),
            "storage.imageCache" => StorageRow(row, 6, Loc.Get(Strings.Settings.Storage.ImageCacheSub), s?.ImageCache),
            "storage.aiModels" => StorageRow(row, 7, s is null ? Loc.Get(Strings.Settings.Storage.AiModelsSub)
                : s.AiModels <= 0 ? Loc.Get(Strings.Settings.Storage.AiModelsNone)
                : s.AiLanguages == 1 ? Loc.Get(Strings.Settings.Storage.AiModelsCountOne)
                : s.AiLanguages > 1 ? Strings.Settings.Storage.AiModelsCount(s.AiLanguages) : Loc.Get(Strings.Settings.Storage.AiModelsSub), s?.AiModels),
            "storage.otherData" => StorageRow(row, 8, Loc.Get(Strings.Settings.Storage.OtherDataSub), s?.Other),
            "storage.cache.audioBodies" => StorageRow(row, 4, Loc.Get(Strings.Settings.Storage.AudioBodiesSub), s?.AudioBody),
            "storage.cache.licenseKeys" => StorageRow(row, 5, storage.KeyCount is not { } keys ? Loc.Get(Strings.Settings.Storage.LicenseKeysSub)
                : keys == 1 ? Loc.Get(Strings.Settings.Storage.LicenseKeysCountOne) : Strings.Settings.Storage.LicenseKeysCount(keys), s?.LicenseDb),
            "storage.cache.budget" => CardRow(row, BodyBudgetControl(s, storage.Audio)),
            "storage.cache.location" => CacheLocationRow(row, storage.AudioDirectory, facts),
            "storage.metadata.budget" => CardRow(row,
                Combo(Settings.StorageFormat.MetaBudgetLabels, s_metaBudget, width: ClassicSettingsRules.BudgetComboWidth, onChange: SetMetaBudget)),
            "storage.metadata.clear" => CardRow(row, StorageClearButton(row.Dial, facts), sub: MetadataSub(storage.Meta)),
            "storage.factoryReset" => CardRow(row, StdButton(Loc.Get(Strings.Settings.Storage.FactoryResetAction), static () =>
            {
                var reset = Dial("storage.factoryReset");
                ConfirmThen(Loc.Get(reset.Confirm!.TitleKey), Loc.Get(reset.Confirm.BodyKey), Loc.Get(reset.Confirm.PrimaryKey),
                    () => reset.Writer.Run!());
            })),
            _ => null,
        };
        return card is not null;
    }

    /// <summary>The cache location: the PATH is the description (W16). The row stays enabled; only the two relocation buttons need the cache (the default
    /// is its own child dial, keyed as its row).</summary>
    static Element CacheLocationRow(SettingsBodyRow row, string audioDir, ISettingsFacts facts)
    {
        Element useDefault = StdButton(Loc.Get(Strings.Settings.Storage.UseDefaultLocation),
            static () => Dial("storage.cache.location.default").Writer.Run!(), isEnabled: Dial("storage.cache.location.default").Enabled(facts).Enabled);
        return CardRow(row, new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, Wrap = true,
            Children =
            [
                StdButton(Loc.Get(Strings.Settings.Storage.ChooseLocation),
                    static () => Dial("storage.cache.location").Writer.Run!(), isEnabled: row.Enabled),
                ChildRow(row, "storage.cache.location.default") is { } defaultRow ? Framed(defaultRow, useDefault) : useDefault,
                LinkButton(Loc.Get(Strings.Settings.Storage.OpenFolder), static () => SettingsVerbs.OpenStorageFolder("cacheLocation")),
            ],
        }, sub: audioDir);
    }

    /// <summary>N11: the verb opens the ONE confirm (Cancel default, the row's <see cref="SettingsConfirm"/>); the confirmed
    /// action — the row's writer — runs off-thread and toasts.</summary>
    static Element StorageClearButton(SettingsDial row, ISettingsFacts facts)
    {
        var confirm = row.Confirm!;
        return StdButton(Loc.Get(confirm.TitleKey),
            () => ConfirmThen(Loc.Get(confirm.TitleKey), Loc.Get(confirm.BodyKey), Loc.Get(confirm.PrimaryKey), () => row.Writer.Run!()),
            isEnabled: row.Enabled(facts).Enabled);
    }

    /// <summary>A category row: the 3-DIP hue accent + the card, whose action lane carries the size (13 DIP secondary,
    /// simply ABSENT until the census lands — never "—", never "0 B"), "Open folder", and its clear (its child dial, keyed as its row). The card sits
    /// in a COLUMN so a wrapping description grows the card and the accent with it (#132).</summary>
    static Element StorageRow(SettingsBodyRow row, int hue, string sub, long? size)
    {
        var actions = new List<Element>(3);
        if (size is { } bytes) actions.Add(R.Type.DenseMeta(Settings.StorageFormat.Bytes(bytes)) with { Color = Tok.TextSecondary, Shrink = 0f });
        // "Open folder" is the category row's writer (the folder door).
        SettingsDial door = row.Dial;
        actions.Add(LinkButton(Loc.Get(Strings.Settings.Storage.OpenFolder), () => door.Writer.Run!()));
        var facts = SettingsFacts.Live;
        foreach (SettingsBodyRow child in row.Children)
            if (child.Dial.Confirm is not null) actions.Add(Framed(child, StorageClearButton(child.Dial, facts)));
        return new BoxEl
        {
            Direction = 0, AlignSelf = FlexAlign.Stretch,
            Children =
            [
                new BoxEl { Width = ClassicSettingsRules.StorageAccentWidth, AlignSelf = FlexAlign.Stretch, Fill = ClassicSettingsRules.StorageHues[hue], Corners = new CornerRadius4(2f, 0f, 0f, 2f) },
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, AlignSelf = FlexAlign.Stretch,
                    Children =
                    [
                        Row(Loc.Get(s_storagePartKeys[hue]), sub,
                            new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, Children = actions.ToArray() },
                            GlyphOf(row.Dial)),
                    ],
                },
            ],
        };
    }

    /// <summary>W16's usage card: "total" + the 18/700 figure, the 10-DIP segmented bar (each segment Grow = its bytes,
    /// so widths are exact proportions), and a wrapping legend. Zero-byte categories leave both; all-zero → no bar.</summary>
    static Element StorageUsageCard(Settings.StorageSnapshot s)
    {
        long[] parts = Settings.StorageRules.Parts(s);
        var bar = new List<Element>(parts.Length);
        var legend = new List<Element>(parts.Length);
        for (int i = 0; i < parts.Length; i++)
        {
            long bytes = parts[i];
            if (bytes <= 0) continue;
            bar.Add(new BoxEl { Grow = bytes, Height = ClassicSettingsRules.MeterHeight, Fill = ClassicSettingsRules.StorageHues[i], Corners = CornerRadius4.All(2f) });
            legend.Add(new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, Grow = 1f, Basis = 0f, MinWidth = ClassicSettingsRules.LegendMinWidth,
                Children =
                [
                    new BoxEl { Width = ClassicSettingsRules.MeterHeight, Height = ClassicSettingsRules.MeterHeight, Corners = CornerRadius4.All(2f), Fill = ClassicSettingsRules.StorageHues[i], Shrink = 0f },
                    new TextEl(Loc.Get(s_storagePartKeys[i])) { Size = ClassicSettingsRules.CaptionSize, Color = Tok.TextPrimary, Grow = 1f },
                    new TextEl(Settings.StorageFormat.Bytes(bytes)) { Size = ClassicSettingsRules.CaptionSize, Color = Tok.TextSecondary, Shrink = 0f },
                    R.Type.MicroMeta(Settings.StorageFormat.Percent(bytes, s.Total).ToString(CultureInfo.InvariantCulture) + "%")
                        with { Color = Tok.TextTertiary, Width = ClassicSettingsRules.PercentColumnWidth, Shrink = 0f },
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
                        new TextEl(Loc.Get(Strings.Settings.Storage.Total)) { Size = ClassicSettingsRules.CaptionSize, Weight = 600, Color = Tok.TextSecondary },
                        new TextEl(Settings.StorageFormat.Bytes(s.Total)) { Size = ClassicSettingsRules.TotalFigureSize, Weight = 700, Color = Tok.TextPrimary },
                    ],
                },
                Settings.StorageRules.ShowsBar(s)
                    ? new BoxEl
                    {
                        Direction = 0, Height = ClassicSettingsRules.MeterHeight, Gap = ClassicSettingsRules.MeterGap, ClipToBounds = true, Corners = CornerRadius4.All(3f),
                        Fill = Tok.StrokeDividerDefault, Children = bar.ToArray(),
                    }
                    : new BoxEl(),
                new BoxEl { Direction = 0, Gap = Spacing.S, Wrap = true, Children = legend.ToArray() },
            ],
        };
    }

    // ══ 3. THE AUDIO BODY BUDGET (W17: Fixed size · Drive share · Unlimited) ═══════════════════════════════════════

    static Element BodyBudgetControl(Settings.StorageSnapshot? s, AudioBodyCacheStatus? status)
    {
        int mode = Math.Clamp(s_budgetMode.Peek(), 0, 2);
        long used = status?.Bytes ?? s?.AudioBody ?? 0;
        var (fraction, over) = Settings.StorageRules.BudgetMeter(used, status?.BudgetBytes);

        Element editor = mode switch
        {
            (int)AudioCacheBudgetMode.FixedBytes => new BoxEl
            {
                Key = "budget:fixed", Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, Wrap = true,
                Children =
                [
                    FrameOf("storage.cache.budget.preset", Combo(Settings.StorageFormat.BodyBudgetLabels, s_budgetPreset, width: ClassicSettingsRules.BudgetComboWidth, onChange: static i =>
                    {
                        if ((uint)i >= (uint)Settings.StorageFormat.BodyBudgetBytes.Length) return;
                        s_budgetGiB.Value = Settings.StorageFormat.BodyBudgetBytes[i] / (double)(1L << 30);
                        if (Dial("storage.cache.budget.preset").Writer.Choose!(i)) Settings.Bump();
                    })),
                    FrameOf("storage.cache.budget.size", NumberField(s_budgetGiB, static v =>
                    {
                        if (!double.IsFinite(v)) return;
                        long bytes = Settings.StorageFormat.GiBToBytes(v);
                        s_budgetPreset.Value = Settings.StorageFormat.BodyBudgetExactIndex(bytes);   // a typed value snaps to Custom
                        CommitBudgetBytes(bytes);
                    }, s_gibBox, "budget:gib")),
                ],
            },
            (int)AudioCacheBudgetMode.DriveShare => new BoxEl
            {
                Key = "budget:share", Direction = 0, AlignItems = FlexAlign.Center,
                Children =
                [
                    FrameOf("storage.cache.budget.percent", NumberField(s_budgetPercent, static v =>
                    {
                        s_budgetPercent.Value = SettingsVerbs.ClampBudgetPercent(v);
                        if (SettingsVerbs.SetBudgetPercent(v)) Settings.Bump();
                    }, s_percentBox, "budget:share")),
                ],
            },
            _ => new TextEl(Loc.Get(Strings.Settings.Storage.UnlimitedReserve)) { Key = "budget:unlimited", Size = ClassicSettingsRules.CaptionSize, Color = Tok.TextSecondary },
        };

        string budgetLabel = status?.BudgetBytes is { } limit ? Settings.StorageFormat.Bytes(limit) : Loc.Get(Strings.Settings.Storage.Unlimited);
        return new BoxEl
        {
            Direction = 1, Gap = ClassicSettingsRules.BudgetGap,
            Children =
            [
                Selector(
                    [Loc.Get(Strings.Settings.Storage.FixedSize), Loc.Get(Strings.Settings.Storage.DriveShare), Loc.Get(Strings.Settings.Storage.Unlimited)],
                    s_budgetMode, onChange: static m =>
                    {
                        if (Dial("storage.cache.budget").Writer.Choose!(m)) Settings.Bump();
                    }),
                editor,
                ProgressBar.Determinate(fraction, width: ClassicSettingsRules.BudgetMeterWidth, state: over ? ProgressBarState.Error : ProgressBarState.Normal),
                R.Type.MicroMeta(Strings.Settings.Storage.UsedOfBudget(Settings.StorageFormat.Bytes(used), budgetLabel)) with { Color = Tok.TextSecondary },
                status is { Available: false }
                    ? R.Type.MicroMeta(Loc.Get(Strings.Settings.Storage.LocationUnavailable)) with { Color = Tok.SystemFillCritical }
                    : R.Type.MicroMeta(Strings.Settings.Storage.FreeReserve(Settings.StorageFormat.Bytes(status?.ReserveBytes ?? 0))) with { Color = Tok.TextTertiary },
            ],
        };
    }

    static void CommitBudgetBytes(long bytes)
    {
        s_budgetGiB.Value = bytes / (double)(1L << 30);
        if (SettingsVerbs.CommitBudgetBytes(bytes)) Settings.Bump();
    }

    // ══ 4. THE METADATA CACHE (library.db's cache tier) ═══════════════════════════════════════════════════════════

    /// <summary>The store sweep reads its budget on its next pass; the tick asks for that pass now, so a lowered ceiling trims without a restart.</summary>
    static void SetMetaBudget(int i)
    {
        if (Dial("storage.metadata.budget").Writer.Choose!(i)) Settings.Bump();
    }

    /// <summary>The generic sub until the census answers, then the stats sentence ("N extras", localized) plus the
    /// deliberately un-localized evictable-vs-budget suffix.</summary>
    static string MetadataSub(Settings.MetadataCacheSnapshot? stats) => stats is not { } m
        ? Loc.Get(Strings.Settings.Storage.MetadataCacheSub)
        : Loc.Format("settings.storage.metadataCacheStats",
              ("db", Settings.StorageFormat.Bytes(m.DbBytes)), ("cache", Settings.StorageFormat.Bytes(m.CacheBytes)),
              ("pinned", Settings.StorageFormat.Bytes(m.PinnedBytes)), ("rows", m.EntityRows), ("pinnedRows", m.PinnedRows),
              ("overviews", m.OverviewRows), ("extensions", m.ExtensionRows))
          + " · evictable " + Settings.StorageFormat.Bytes(m.EvictableBytes) + " of " + Settings.StorageFormat.Bytes(m.BudgetBytes) + " budget";
}
