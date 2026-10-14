// ── Screens/Settings.UI.Storage.cs ─────────────────────────────────────────────────────────────────────────────────
// the Storage tab: the census block (spinner / usage card / failed bar), the seven fixed hues on the bar, the legend and
// the row accents, the playback-cache rows (the three-mode budget editor, the cache location), the metadata cache, reset
//
// Role: UI
// Owner: R
// Wave: 6
// Budget: 350 lines
// Spec: ch 27 §0 N9/N11, W15-W18, §3 (storage rows), §4.1, §5 (census swap), §7 (+ G2-G5), §8, §10 parity 41-49
//
// RESIDENT CACHE (ch 27 G2, decided): 0.3 has no resident-playlist cache and no per-table census, so the "In memory"
// section and its "Resident library cache" row are DELETED here. `Settings.Catalog` still lists Storage › Memory ›
// residentCache; the section and the row leave the catalog together (the every-section-has-a-row invariant).

using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Sdk.Streams;

namespace Wavee;

public static partial class Settings
{
    // ══ 1. PAGE STATE (0.2.9 `SettingsPage.Storage.cs` instance fields; written by Settings.Host.cs's census) ═════════

    /// <summary>N9 — seven hand-picked hues, THEME-INVARIANT (accent tints read as one blue on dark), indexed in
    /// <see cref="StorageRules.Parts"/> order: library, runtime, logs, local store, audio bodies, license keys, images.</summary>
    static readonly ColorF[] s_storageHues =
    [
        ColorF.FromRgba(0x4A, 0x90, 0xD9), ColorF.FromRgba(0x9B, 0x59, 0xB6), ColorF.FromRgba(0xF5, 0xA6, 0x23),
        ColorF.FromRgba(0x27, 0xAE, 0x60), ColorF.FromRgba(0x1A, 0xBC, 0x9C), ColorF.FromRgba(0x95, 0xA5, 0xA6),
        ColorF.FromRgba(0xE7, 0x4C, 0x3C),
    ];

    static readonly string[] s_storagePartKeys =
    [
        Strings.Settings.Storage.Library, Strings.Settings.Storage.Runtime, Strings.Settings.Storage.Logs,
        Strings.Settings.Storage.LocalStore, Strings.Settings.Storage.AudioBodies, Strings.Settings.Storage.LicenseKeys,
        Strings.Settings.Storage.ImageCache,
    ];

    static readonly Signal<StorageLoadPhase> s_storageLoad = new(StorageLoadPhase.NotStarted);
    static StorageSnapshot? s_storage;
    static string? s_storageError;
    static AudioBodyCacheStatus? s_audioStatus;
    static int? s_keyCount;
    static MetadataCacheSnapshot? s_metaStats;
    static bool s_recount;

    // Stable control signals (the controls freeze the instance at mount); re-seeded from the store on every page mount.
    static readonly Signal<int> s_budgetMode = new((int)AudioCacheBudgetMode.DriveShare);
    static readonly Signal<int> s_budgetPreset = new(5);
    static readonly Signal<double> s_budgetGiB = new(32);
    static readonly Signal<double> s_budgetPercent = new(0);
    static readonly Signal<int> s_metaBudget = new(1);

    static readonly NumberBox.NumberBoxOptions s_percentBox = new()
    {
        Minimum = 0, Maximum = 90, SmallChange = 1, Width = 150f,
        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        Formatter = static v => v <= 0 ? Loc.Get(Strings.Settings.Storage.AutoTenPercent) : v.ToString("0", CultureInfo.InvariantCulture) + "%",
    };

    static readonly NumberBox.NumberBoxOptions s_gibBox = new()
    {
        Minimum = 0.0625, Maximum = 1 << 20, SmallChange = 1, Width = 150f,
        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        Formatter = static v => v.ToString("0.###", CultureInfo.InvariantCulture) + " GB",
    };

    static partial void SeedStorage()
    {
        var store = Platform.Settings;
        s_budgetMode.Value = Math.Clamp(store.Get(Platform.Keys.AudioBodyCacheBudgetMode), 0, 2);
        long fixedBytes = Math.Max(ChunkDiskCache.MinBudgetBytes, store.Get(Platform.Keys.AudioBodyCacheBudgetBytes));
        s_budgetGiB.Value = fixedBytes / (double)(1L << 30);
        s_budgetPreset.Value = StorageFormat.BodyBudgetExactIndex(fixedBytes);
        s_budgetPercent.Value = Math.Clamp(store.Get(Platform.Keys.AudioBodyCacheBudgetPercent), 0, 90);
        s_metaBudget.Value = StorageFormat.MetaBudgetIndex(store.Get(Platform.Keys.MetadataCacheBudgetBytes));
        // A fresh page mount censuses again on its first Storage visit (0.2.9's per-instance phase).
        if (s_storageLoad.Peek() != StorageLoadPhase.Loading) s_storageLoad.Value = StorageLoadPhase.NotStarted;
    }

    static partial void EnterStorage(bool active)
    {
        if (active && s_storageLoad.Peek() == StorageLoadPhase.NotStarted) RefreshStorage();
    }

    // ══ 2. THE TAB (W15 loading · W16 ready · W18 failed) ══════════════════════════════════════════════════════════

    private static partial Element StorageTab()
    {
        var phase = s_storageLoad.Value;
        var s = s_storage;
        string audioDir = s_audioStatus?.Directory ?? AudioCacheDirectory();

        Element census = phase == StorageLoadPhase.Failed
            ? InfoBar.Create(InfoBarSeverity.Error, Loc.Get(Strings.Settings.Storage.ReadFailed),
                s_storageError ?? Loc.Get(Strings.Common.ErrorTitle), isClosable: false,
                actionButton: Button.Standard(Loc.Get(Strings.Common.Retry), static () => RefreshStorage()))
            : s is null
                ? new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, Padding = new Edges4(0f, Spacing.L, 0f, Spacing.L),
                    Children =
                    [
                        ProgressRing.Indeterminate(size: 20f),
                        new TextEl(Loc.Get(Strings.Settings.Storage.Reading)) { Size = 12f, Color = Tok.TextSecondary },
                    ],
                }
                : StorageUsageCard(s);

        string logsSub = s is null ? Loc.Get(Strings.Settings.Storage.LogsSubEmpty)
            : s.LogFiles == 1 ? Loc.Get(Strings.Settings.Storage.LogsSubOne) : Strings.Settings.Storage.LogsSub(s.LogFiles);
        string keysSub = s_keyCount is not { } keys ? Loc.Get(Strings.Settings.Storage.LicenseKeysSub)
            : keys == 1 ? Loc.Get(Strings.Settings.Storage.LicenseKeysCountOne) : Strings.Settings.Storage.LicenseKeysCount(keys);
        var facts = SettingsFacts.Live;

        return TabStack(
            SectionHeader(Loc.Get(Strings.Settings.Storage.OnThisPc), SectionGlyph(Tab.Storage, "On this PC")),
            census,
            StorageRow(0, "library", Loc.Get(Strings.Settings.Storage.LibrarySub), s?.LibraryDb),
            StorageRow(1, "runtime", Loc.Get(Strings.Settings.Storage.RuntimeSub), s?.Runtime),
            StorageRow(2, "logs", logsSub, s?.Logs, StorageClearButton(D(Tab.Storage, "logsClear"), facts)),
            StorageRow(3, "localStore", Loc.Get(Strings.Settings.Storage.LocalStoreSub), s?.Store),
            StorageRow(6, "imageCache", Loc.Get(Strings.Settings.Storage.ImageCacheSub), s?.ImageCache),

            SectionHeader(Loc.Get(Strings.Settings.Storage.PlaybackCache), SectionGlyph(Tab.Storage, "Playback cache")),
            DocRow(D(Tab.Storage, "cacheAudio"), RowToggle(D(Tab.Storage, "cacheAudio"))),
            DocRow(D(Tab.Storage, "cacheKeys"), RowToggle(D(Tab.Storage, "cacheKeys"))),
            DocRow(D(Tab.Storage, "budget"), BodyBudgetControl(s)),
            // The PATH is the sub (W16). The row itself stays enabled; only the two relocation buttons need the cache.
            Row(Loc.Get(Strings.Settings.Storage.CacheLocation), audioDir, new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, Wrap = true,
                Children =
                [
                    Button.Standard(Loc.Get(Strings.Settings.Storage.ChooseLocation),
                        static () => D(Tab.Storage, "cacheLocation").Writer.Run!(), isEnabled: D(Tab.Storage, "cacheLocation").Enabled(facts).Enabled),
                    Button.Standard(Loc.Get(Strings.Settings.Storage.UseDefaultLocation),
                        static () => D(Tab.Storage, "cacheLocationDefault").Writer.Run!(), isEnabled: D(Tab.Storage, "cacheLocationDefault").Enabled(facts).Enabled),
                    HyperlinkButton.Create(Loc.Get(Strings.Settings.Storage.OpenFolder), static () => SettingsVerbs.OpenStorageFolder("cacheLocation")),
                ],
            }, RowGlyph(Tab.Storage, "cacheLocation")),
            StorageRow(4, "audioBodies", Loc.Get(Strings.Settings.Storage.AudioBodiesSub), s?.AudioBody,
                StorageClearButton(D(Tab.Storage, "audioBodiesClear"), facts)),
            StorageRow(5, "licenseKeys", keysSub, s?.LicenseDb, StorageClearButton(D(Tab.Storage, "licenseKeysClear"), facts)),

            SectionHeader(Loc.Get(Strings.Settings.Storage.MetadataCache), SectionGlyph(Tab.Storage, "Metadata cache")),
            DocRow(D(Tab.Storage, "metadataBudget"),
                ComboBox.Create(StorageFormat.MetaBudgetLabels, s_metaBudget, width: 120f, onChange: SetMetaBudget)),
            Row(Loc.Get(Strings.Settings.Storage.MetadataCache), MetadataSub(),
                StorageClearButton(D(Tab.Storage, "clearMetadata"), facts),
                RowGlyph(Tab.Storage, "clearMetadata")),

            SectionHeader(Loc.Get(Strings.Settings.Storage.FactoryReset), SectionGlyph(Tab.Storage, "Reset"),
                Loc.Get(Strings.Settings.Storage.FactoryResetSub)),
            DocRow(D(Tab.Storage, "factoryReset"),
                Button.Standard(Loc.Get(Strings.Settings.Storage.FactoryResetAction), static () =>
                {
                    var reset = D(Tab.Storage, "factoryReset");
                    ConfirmThen(Loc.Get(reset.Confirm!.TitleKey), Loc.Get(reset.Confirm.BodyKey), Loc.Get(reset.Confirm.PrimaryKey),
                        () => reset.Writer.Run!());
                })));
    }

    /// <summary>N11: the verb opens the ONE confirm (Cancel default, the row's <see cref="SettingsConfirm"/>); the confirmed
    /// action — the row's writer — runs off-thread and toasts.</summary>
    static Element StorageClearButton(SettingsRow row, ISettingsFacts facts)
    {
        var confirm = row.Confirm!;
        return Button.Standard(Loc.Get(confirm.TitleKey),
            () => ConfirmThen(Loc.Get(confirm.TitleKey), Loc.Get(confirm.BodyKey), Loc.Get(confirm.PrimaryKey), () => row.Writer.Run!()),
            isEnabled: row.Enabled(facts).Enabled);
    }

    /// <summary>A category row: the 3-DIP hue accent + the card, whose action lane carries the size (13 DIP secondary,
    /// simply ABSENT until the census lands — never "—", never "0 B"), "Open folder", and an optional verb. The card sits
    /// in a COLUMN so a wrapping description grows the card and the accent with it (#132).</summary>
    static Element StorageRow(int hue, string rowId, string sub, long? size, Element? extra = null)
    {
        var actions = new List<Element>(3);
        if (size is { } bytes) actions.Add(R.Type.DenseMeta(StorageFormat.Bytes(bytes)) with { Color = Tok.TextSecondary, Shrink = 0f });
        // "Open folder" is the category row's writer (the folder door).
        actions.Add(HyperlinkButton.Create(Loc.Get(Strings.Settings.Storage.OpenFolder), () => D(Tab.Storage, rowId).Writer.Run!()));
        if (extra is not null) actions.Add(extra);
        return new BoxEl
        {
            Direction = 0, AlignSelf = FlexAlign.Stretch,
            Children =
            [
                new BoxEl { Width = 3f, AlignSelf = FlexAlign.Stretch, Fill = s_storageHues[hue], Corners = new CornerRadius4(2f, 0f, 0f, 2f) },
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, AlignSelf = FlexAlign.Stretch,
                    Children =
                    [
                        Row(Loc.Get(s_storagePartKeys[hue]), sub,
                            new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, Children = actions.ToArray() },
                            RowGlyph(Tab.Storage, rowId)),
                    ],
                },
            ],
        };
    }

    /// <summary>W16's usage card: "total" + the 18/700 figure, the 10-DIP segmented bar (each segment Grow = its bytes,
    /// so widths are exact proportions), and a wrapping legend. Zero-byte categories leave both; all-zero → no bar.</summary>
    static Element StorageUsageCard(StorageSnapshot s)
    {
        long[] parts = StorageRules.Parts(s);
        var bar = new List<Element>(parts.Length);
        var legend = new List<Element>(parts.Length);
        for (int i = 0; i < parts.Length; i++)
        {
            long bytes = parts[i];
            if (bytes <= 0) continue;
            bar.Add(new BoxEl { Grow = bytes, Height = 10f, Fill = s_storageHues[i], Corners = CornerRadius4.All(2f) });
            legend.Add(new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, Grow = 1f, Basis = 0f, MinWidth = 200f,
                Children =
                [
                    new BoxEl { Width = 10f, Height = 10f, Corners = CornerRadius4.All(2f), Fill = s_storageHues[i], Shrink = 0f },
                    new TextEl(Loc.Get(s_storagePartKeys[i])) { Size = 12f, Color = Tok.TextPrimary, Grow = 1f },
                    new TextEl(StorageFormat.Bytes(bytes)) { Size = 12f, Color = Tok.TextSecondary, Shrink = 0f },
                    R.Type.MicroMeta(StorageFormat.Percent(bytes, s.Total).ToString(CultureInfo.InvariantCulture) + "%")
                        with { Color = Tok.TextTertiary, Width = 36f, Shrink = 0f },
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
                        new TextEl(Loc.Get(Strings.Settings.Storage.Total)) { Size = 12f, Weight = 600, Color = Tok.TextSecondary },
                        new TextEl(StorageFormat.Bytes(s.Total)) { Size = 18f, Weight = 700, Color = Tok.TextPrimary },
                    ],
                },
                StorageRules.ShowsBar(s)
                    ? new BoxEl
                    {
                        Direction = 0, Height = 10f, Gap = 2f, ClipToBounds = true, Corners = CornerRadius4.All(3f),
                        Fill = Tok.StrokeDividerDefault, Children = bar.ToArray(),
                    }
                    : new BoxEl(),
                new BoxEl { Direction = 0, Gap = Spacing.S, Wrap = true, Children = legend.ToArray() },
            ],
        };
    }

    // ══ 3. THE AUDIO BODY BUDGET (W17: Fixed size · Drive share · Unlimited) ═══════════════════════════════════════

    static Element BodyBudgetControl(StorageSnapshot? s)
    {
        var status = s_audioStatus;
        int mode = Math.Clamp(s_budgetMode.Peek(), 0, 2);
        long used = status?.Bytes ?? s?.AudioBody ?? 0;
        var (fraction, over) = StorageRules.BudgetMeter(used, status?.BudgetBytes);

        Element editor = mode switch
        {
            (int)AudioCacheBudgetMode.FixedBytes => new BoxEl
            {
                Key = "budget:fixed", Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, Wrap = true,
                Children =
                [
                    ComboBox.Create(StorageFormat.BodyBudgetLabels, s_budgetPreset, width: 120f, onChange: static i =>
                    {
                        if ((uint)i >= (uint)StorageFormat.BodyBudgetBytes.Length) return;
                        s_budgetGiB.Value = StorageFormat.BodyBudgetBytes[i] / (double)(1L << 30);
                        if (D(Tab.Storage, "budgetPreset").Writer.Choose!(i)) Bump();
                    }),
                    NumberBox.Create(s_budgetGiB, static v =>
                    {
                        if (!double.IsFinite(v)) return;
                        long bytes = StorageFormat.GiBToBytes(v);
                        s_budgetPreset.Value = StorageFormat.BodyBudgetExactIndex(bytes);   // a typed value snaps to Custom
                        CommitBudgetBytes(bytes);
                    }, s_gibBox),
                ],
            },
            (int)AudioCacheBudgetMode.DriveShare => NumberBox.Create(s_budgetPercent, static v =>
            {
                s_budgetPercent.Value = SettingsVerbs.ClampBudgetPercent(v);
                if (SettingsVerbs.SetBudgetPercent(v)) Bump();
            }, s_percentBox) with { Key = "budget:share" },
            _ => new TextEl(Loc.Get(Strings.Settings.Storage.UnlimitedReserve)) { Key = "budget:unlimited", Size = 12f, Color = Tok.TextSecondary },
        };

        string budgetLabel = status?.BudgetBytes is { } limit ? StorageFormat.Bytes(limit) : Loc.Get(Strings.Settings.Storage.Unlimited);
        return new BoxEl
        {
            Direction = 1, Gap = 6f,
            Children =
            [
                SelectorBar.Create(
                    [Loc.Get(Strings.Settings.Storage.FixedSize), Loc.Get(Strings.Settings.Storage.DriveShare), Loc.Get(Strings.Settings.Storage.Unlimited)],
                    s_budgetMode, onChange: static m =>
                    {
                        if (D(Tab.Storage, "budget").Writer.Choose!(m)) Bump();
                    }),
                editor,
                ProgressBar.Determinate(fraction, width: 300f, state: over ? ProgressBarState.Error : ProgressBarState.Normal),
                R.Type.MicroMeta(Strings.Settings.Storage.UsedOfBudget(StorageFormat.Bytes(used), budgetLabel)) with { Color = Tok.TextSecondary },
                status is { Available: false }
                    ? R.Type.MicroMeta(Loc.Get(Strings.Settings.Storage.LocationUnavailable)) with { Color = Tok.SystemFillCritical }
                    : R.Type.MicroMeta(Strings.Settings.Storage.FreeReserve(StorageFormat.Bytes(status?.ReserveBytes ?? 0))) with { Color = Tok.TextTertiary },
            ],
        };
    }

    static void CommitBudgetBytes(long bytes)
    {
        s_budgetGiB.Value = bytes / (double)(1L << 30);
        if (SettingsVerbs.CommitBudgetBytes(bytes)) Bump();
    }

    // ══ 4. THE METADATA CACHE (library.db's cache tier) ═══════════════════════════════════════════════════════════

    /// <summary>The store sweep reads <see cref="Store.Policy"/> on its next pass; the tick asks for that pass now, so a
    /// lowered ceiling trims without a restart.</summary>
    static void SetMetaBudget(int i)
    {
        if (D(Tab.Storage, "metadataBudget").Writer.Choose!(i)) Bump();
    }

    /// <summary>The generic sub until the census answers, then the stats sentence ("N extras", localized) plus the
    /// deliberately un-localized evictable-vs-budget suffix.</summary>
    static string MetadataSub() => s_metaStats is not { } m
        ? Loc.Get(Strings.Settings.Storage.MetadataCacheSub)
        : Loc.Format("settings.storage.metadataCacheStats",
              ("db", StorageFormat.Bytes(m.DbBytes)), ("cache", StorageFormat.Bytes(m.CacheBytes)),
              ("pinned", StorageFormat.Bytes(m.PinnedBytes)), ("rows", m.EntityRows), ("pinnedRows", m.PinnedRows),
              ("overviews", m.OverviewRows), ("extensions", m.ExtensionRows))
          + " · evictable " + StorageFormat.Bytes(m.EvictableBytes) + " of " + StorageFormat.Bytes(m.BudgetBytes) + " budget";
}
