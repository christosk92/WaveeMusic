// ── Themes/Zune/Structure/Views/Screens/ZuneSettings.Storage.cs — the Storage tab (themes plan 6.3, Z8b) ───────────────────────────────────────
//
// Board Z-Settings-Sections ("downloads & storage"), without its downloads (owner Q29 / NQ4: no offline playback, so no downloaded songs, no "remove all downloads"):
// the core Storage settings stay reachable. The census (the usage line and bar, the seven fixed hues), the playback-cache rows (the audio and key switches, the
// three-mode budget editor, the cache location), the metadata cache and the factory reset. The census itself (the phase, the snapshot, the cache status, the key
// count, the metadata stats; the first visit to the tab starts it; a clear re-runs it) is the core's (`Settings.Host.cs`) and arrives as `SettingsPageProps.Storage`;
// the clears, the budget writes, the folder doors and the factory reset are the rows' writers.

using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Sdk.Streams;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;
using Tab = Wavee.Settings.Tab;
using Z = Wavee.Themes.Zune.ZuneSettingsRules;

namespace Wavee.Themes.Zune;

internal static partial class ZuneSettings
{
    /// <summary>The census parts' words, in `Settings.StorageRules.Parts` order: library, runtime, logs, local store, audio bodies, license keys, images.</summary>
    static readonly string[] s_storagePartKeys =
    [
        Strings.Settings.Storage.Library, Strings.Settings.Storage.Runtime, Strings.Settings.Storage.Logs,
        Strings.Settings.Storage.LocalStore, Strings.Settings.Storage.AudioBodies, Strings.Settings.Storage.LicenseKeys,
        Strings.Settings.Storage.ImageCache,
    ];

    static Named[] StorageTab(ZuneKit k, SettingsStorageProps storage)
    {
        var s = storage.Snapshot;
        var facts = SettingsFacts.Live;

        Element census = storage.Phase == Settings.StorageLoadPhase.Failed
            ? ZuneKit.Notice(InfoBarSeverity.Error, Loc.Get(Strings.Settings.Storage.ReadFailed), storage.Error ?? Loc.Get(Strings.Common.ErrorTitle),
                k.Cmd(Loc.Get(Strings.Common.Retry), static () => Settings.RefreshStorage()))
            : s is null
                ? new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Gap = Z.ControlGap, MinHeight = Z.CensusMinHeight,
                    Children = [ProgressRing.Indeterminate(size: Z.SpinnerSize), ZuneText.Meta(Loc.Get(Strings.Settings.Storage.Reading))],
                }
                : UsageCard(s);

        string logsSub = s is null ? Loc.Get(Strings.Settings.Storage.LogsSubEmpty)
            : s.LogFiles == 1 ? Loc.Get(Strings.Settings.Storage.LogsSubOne) : Strings.Settings.Storage.LogsSub(s.LogFiles);
        string keysSub = storage.KeyCount is not { } keys ? Loc.Get(Strings.Settings.Storage.LicenseKeysSub)
            : keys == 1 ? Loc.Get(Strings.Settings.Storage.LicenseKeysCountOne) : Strings.Settings.Storage.LicenseKeysCount(keys);
        var location = D(Tab.Storage, "cacheLocation");
        var locationDefault = D(Tab.Storage, "cacheLocationDefault");
        var metaBudget = D(Tab.Storage, "metadataBudget");
        var clearMeta = D(Tab.Storage, "clearMetadata");
        var reset = D(Tab.Storage, "factoryReset");

        return
        [
            Group(k, Tab.Storage, "On this PC", census,
                CategoryRow(k, 0, "library", Loc.Get(Strings.Settings.Storage.LibrarySub), s?.LibraryDb),
                CategoryRow(k, 1, "runtime", Loc.Get(Strings.Settings.Storage.RuntimeSub), s?.Runtime),
                CategoryRow(k, 2, "logs", logsSub, s?.Logs, ClearCmd(k, D(Tab.Storage, "logsClear"), facts)),
                CategoryRow(k, 3, "localStore", Loc.Get(Strings.Settings.Storage.LocalStoreSub), s?.Store),
                CategoryRow(k, 6, "imageCache", Loc.Get(Strings.Settings.Storage.ImageCacheSub), s?.ImageCache)),

            Group(k, Tab.Storage, "Playback cache",
                ToggleRow(k, Tab.Storage, "cacheAudio"), ToggleRow(k, Tab.Storage, "cacheKeys"),
                Embed.Comp(new BudgetArgs(s, storage.Audio), static () => new BudgetRow()) with { Key = "zs:budget" },
                // The PATH is the hint. The row itself stays enabled; only the two relocation commands need the cache.
                k.Row("zs:row:cache-location", Loc.Get(Strings.Settings.Storage.CacheLocation), storage.AudioDirectory,
                    new BoxEl
                    {
                        Direction = 0, AlignItems = FlexAlign.Center, Gap = Z.ControlGap, Wrap = true, MinWidth = 0f,
                        Children =
                        [
                            k.Cmd(Loc.Get(Strings.Settings.Storage.ChooseLocation), () => location.Writer.Run!(), location.Enabled(facts).Enabled),
                            k.Cmd(Loc.Get(Strings.Settings.Storage.UseDefaultLocation), () => locationDefault.Writer.Run!(), locationDefault.Enabled(facts).Enabled),
                            k.Cmd(Loc.Get(Strings.Settings.Storage.OpenFolder), static () => SettingsVerbs.OpenStorageFolder("cacheLocation")),
                        ],
                    }),
                CategoryRow(k, 4, "audioBodies", Loc.Get(Strings.Settings.Storage.AudioBodiesSub), s?.AudioBody, ClearCmd(k, D(Tab.Storage, "audioBodiesClear"), facts)),
                CategoryRow(k, 5, "licenseKeys", keysSub, s?.LicenseDb, ClearCmd(k, D(Tab.Storage, "licenseKeysClear"), facts))),

            Group(k, Tab.Storage, "Metadata cache",
                k.DocRow(metaBudget, k.Choice(metaBudget, Settings.StorageFormat.MetaBudgetLabels, chips: false, width: Z.BudgetComboWidth)),
                k.Row(ZuneKit.RowKey(clearMeta), Loc.Get(Strings.Settings.Storage.MetadataCache), MetadataSub(storage.Meta), ClearCmd(k, clearMeta, facts))),

            Group(k, Tab.Storage, "Reset",
                k.DocRow(reset, k.Cmd(Loc.Get(Strings.Settings.Storage.FactoryResetAction),
                    () => ConfirmThen(Loc.Get(reset.Confirm!.TitleKey), Loc.Get(reset.Confirm.BodyKey), Loc.Get(reset.Confirm.PrimaryKey), () => reset.Writer.Run!()))
                    )),
        ];
    }

    /// <summary>The verb opens the ONE confirm (Cancel default, the row's <see cref="SettingsConfirm"/>); the confirmed action, the row's writer, runs off-thread and toasts.</summary>
    static Element ClearCmd(ZuneKit k, SettingsRow row, ISettingsFacts facts)
    {
        var confirm = row.Confirm!;
        return k.Cmd(Loc.Get(confirm.TitleKey), () => ConfirmThen(Loc.Get(confirm.TitleKey), Loc.Get(confirm.BodyKey), Loc.Get(confirm.PrimaryKey), () => row.Writer.Run!()),
            row.Enabled(facts).Enabled);
    }

    /// <summary>A census row: the part's fixed hue, its words, its size (simply ABSENT until the census lands: never a dash, never 0 B), "open folder" and an optional verb.</summary>
    static Element CategoryRow(ZuneKit k, int hue, string rowId, string sub, long? size, Element? extra = null)
    {
        var controls = new List<Element>(4)
        {
            new BoxEl { Width = Z.HueSize, Height = Z.HueSize, Shrink = 0f, Fill = Z.StorageHues[hue] },
        };
        if (size is { } bytes) controls.Add(R.Type.DenseMeta(Settings.StorageFormat.Bytes(bytes)) with { Color = Tok.TextPrimary, Shrink = 0f });
        controls.Add(k.Cmd(Loc.Get(Strings.Settings.Storage.OpenFolder), () => D(Tab.Storage, rowId).Writer.Run!()));
        if (extra is not null) controls.Add(extra);
        return k.Row(ZuneKit.RowKey(D(Tab.Storage, rowId)), Loc.Get(s_storagePartKeys[hue]), sub,
            new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = Z.ControlGap, Wrap = true, MinWidth = 0f, Children = controls.ToArray() });
    }

    /// <summary>The usage line and bar: the total as a numeral, the segmented bar (each segment grows by its bytes, so widths are exact proportions), and a wrapping
    /// legend. Zero-byte categories leave both; all zero draws no bar.</summary>
    static Element UsageCard(Settings.StorageSnapshot s)
    {
        long[] parts = Settings.StorageRules.Parts(s);
        var bar = new List<Element>(parts.Length);
        var legend = new List<Element>(parts.Length);
        for (int i = 0; i < parts.Length; i++)
        {
            long bytes = parts[i];
            if (bytes <= 0) continue;
            bar.Add(new BoxEl { Grow = bytes, Height = Z.MeterHeight, Fill = Z.StorageHues[i] });
            legend.Add(new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = Z.ControlGap, Basis = Z.LegendWidth, Grow = 1f, MinWidth = 0f,
                Children =
                [
                    new BoxEl { Width = Z.HueSize, Height = Z.HueSize, Shrink = 0f, Fill = Z.StorageHues[i] },
                    ZuneText.Cased(R.Type.DenseMeta(Loc.Get(s_storagePartKeys[i])), LabelKind.Label) with { Color = Tok.TextPrimary, Grow = 1f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                    R.Type.DenseMeta(Settings.StorageFormat.Bytes(bytes)) with { Color = Tok.TextSecondary, Shrink = 0f },
                ],
            });
        }
        return new BoxEl
        {
            Key = "zs:usage", Direction = 1, Gap = Z.ControlGap, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
            Children =
            [
                new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.End, Gap = Z.ControlGap, Wrap = true,
                    Children =
                    [
                        R.Type.NumeralLarge(Settings.StorageFormat.Bytes(s.Total)) with { Shrink = 0f },
                        ZuneText.Meta(Loc.Get(Strings.Settings.Storage.Total)) with { Color = Tok.TextTertiary },
                    ],
                },
                Settings.StorageRules.ShowsBar(s)
                    ? new BoxEl { Direction = 0, Height = Z.MeterHeight, Gap = Z.MeterGap, ClipToBounds = true, Fill = Tok.StrokeDividerDefault, Children = bar.ToArray() }
                    : new BoxEl(),
                new BoxEl { Direction = 0, Gap = Z.ControlGap, Wrap = true, Children = legend.ToArray() },
            ],
        };
    }

    // ── the audio body budget (fixed size, drive share, unlimited) ────────────────────────────────────────────────────────

    /// <summary>The budget row: the mode as chips, the editor of the mode, the meter and its line. Its own component: the editors' controls bind one signal for their
    /// life, seeded from the document and re-seeded on every page epoch.</summary>
    sealed record BudgetArgs(Settings.StorageSnapshot? Snapshot, AudioBodyCacheStatus? Status);

    sealed class BudgetRow : ZuneScreenComponent
    {
        readonly Signal<int> _mode = new((int)AudioCacheBudgetMode.DriveShare);
        readonly Signal<int> _preset = new(5);
        readonly Signal<double> _gib = new(32);
        readonly Signal<double> _percent = new(0);

        static readonly NumberBox.NumberBoxOptions s_percentBox = new()
        {
            Minimum = 0, Maximum = Z.BudgetPercentMax, SmallChange = 1, Width = Z.BudgetBoxWidth,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            Formatter = static v => v <= 0 ? Loc.Get(Strings.Settings.Storage.AutoTenPercent) : v.ToString("0", CultureInfo.InvariantCulture) + "%",
        };

        static readonly NumberBox.NumberBoxOptions s_gibBox = new()
        {
            Minimum = Z.BudgetGibMin, Maximum = Z.BudgetGibMax, SmallChange = 1, Width = Z.BudgetBoxWidth,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            Formatter = static v => v.ToString("0.###", CultureInfo.InvariantCulture) + " GB",
        };

        public override Element Render()
        {
            var lay = UseLayout();
            var k = new ZuneKit(lay);
            var args = UseProps<BudgetArgs>();
            var snapshot = args.Snapshot;
            var status = args.Status;
            _ = Settings.PageEpoch.Value;
            var budget = D(Tab.Storage, "budget");
            int mode = budget.Current is int m ? Math.Clamp(m, 0, 2) : 0;
            int preset = D(Tab.Storage, "budgetPreset").Current is int pr ? pr : 5;
            int percent = D(Tab.Storage, "budgetPercent").Current is int pc ? pc : 0;
            double gib = SettingsFacts.AudioBudgetBytes() / (double)(1L << 30);
            UseEffect(() =>
            {
                _mode.SetIfChanged(mode);
                _preset.SetIfChanged(preset);
                _percent.SetIfChanged(percent);
                _gib.SetIfChanged(gib);
            }, DepKey.From(HashCode.Combine(mode, preset, percent, (long)(gib * 1024))));

            long used = status?.Bytes ?? snapshot?.AudioBody ?? 0;
            var (fraction, over) = Settings.StorageRules.BudgetMeter(used, status?.BudgetBytes);
            Element editor = mode switch
            {
                (int)AudioCacheBudgetMode.FixedBytes => new BoxEl
                {
                    Key = "zs:budget:fixed", Direction = 0, AlignItems = FlexAlign.Center, Gap = Z.ControlGap, Wrap = true,
                    Children =
                    [
                        k.Combo(Settings.StorageFormat.BodyBudgetLabels, _preset, Z.BudgetComboWidth, true, i =>
                        {
                            if ((uint)i >= (uint)Settings.StorageFormat.BodyBudgetBytes.Length) return;
                            _gib.Value = Settings.StorageFormat.BodyBudgetBytes[i] / (double)(1L << 30);
                            if (D(Tab.Storage, "budgetPreset").Writer.Choose!(i)) Settings.Bump();
                        }),
                        Number(lay, _gib, v =>
                        {
                            if (!double.IsFinite(v)) return;
                            long bytes = Settings.StorageFormat.GiBToBytes(v);
                            _preset.Value = Settings.StorageFormat.BodyBudgetExactIndex(bytes);   // a typed value snaps to Custom
                            _gib.Value = bytes / (double)(1L << 30);
                            if (SettingsVerbs.CommitBudgetBytes(bytes)) Settings.Bump();
                        }, s_gibBox, "zs:budget:gib"),
                    ],
                },
                (int)AudioCacheBudgetMode.DriveShare => Number(lay, _percent, v =>
                {
                    _percent.Value = SettingsVerbs.ClampBudgetPercent(v);
                    if (SettingsVerbs.SetBudgetPercent(v)) Settings.Bump();
                }, s_percentBox, "zs:budget:share"),
                _ => ZuneText.Meta(Loc.Get(Strings.Settings.Storage.UnlimitedReserve)) with { Key = "zs:budget:unlimited" },
            };

            string budgetLabel = status?.BudgetBytes is { } limit ? Settings.StorageFormat.Bytes(limit) : Loc.Get(Strings.Settings.Storage.Unlimited);
            return k.DocRow(budget, new BoxEl
            {
                Direction = 1, Gap = Z.ControlGap, MinWidth = 0f, AlignItems = FlexAlign.Start,
                Children =
                [
                    k.Choice(budget.Id, new ZuneChoice(
                        [Loc.Get(Strings.Settings.Storage.FixedSize), Loc.Get(Strings.Settings.Storage.DriveShare), Loc.Get(Strings.Settings.Storage.Unlimited)],
                        () => budget.Current is int at ? at : 0,
                        m2 => { if (budget.Writer.Choose!(m2)) Settings.Bump(); }, Chips: true)),
                    editor,
                    ProgressBar.Determinate(fraction, width: Z.BudgetMeterWidth, state: over ? ProgressBarState.Error : ProgressBarState.Normal),
                    ZuneText.Hint(Strings.Settings.Storage.UsedOfBudget(Settings.StorageFormat.Bytes(used), budgetLabel)),
                    status is { Available: false }
                        ? ZuneText.Hint(Loc.Get(Strings.Settings.Storage.LocationUnavailable)) with { Color = Tok.SystemFillCritical }
                        : ZuneText.Hint(Strings.Settings.Storage.FreeReserve(Settings.StorageFormat.Bytes(status?.ReserveBytes ?? 0))),
                ],
            });
        }

        /// <summary>A number box whose field is the tier's hit target tall (the engine's own 32 elsewhere). ONE key at every tier: the style is a live prop.</summary>
        static Element Number(ZuneSettingsLayout lay, Signal<double> value, Action<double> onChange, NumberBox.NumberBoxOptions options, string key)
            => NumberBox.Create(value, onChange, lay.Target > 0f ? options with { Style = NumberBox.DefaultStyle with { FieldHeight = lay.Target } } : options with { Style = null })
                with { Key = key };
    }

    /// <summary>The generic sub until the census answers, then the stats sentence plus the deliberately un-localized evictable-vs-budget suffix.</summary>
    static string MetadataSub(Settings.MetadataCacheSnapshot? stats) => stats is not { } m
        ? Loc.Get(Strings.Settings.Storage.MetadataCacheSub)
        : Loc.Format("settings.storage.metadataCacheStats",
              ("db", Settings.StorageFormat.Bytes(m.DbBytes)), ("cache", Settings.StorageFormat.Bytes(m.CacheBytes)),
              ("pinned", Settings.StorageFormat.Bytes(m.PinnedBytes)), ("rows", m.EntityRows), ("pinnedRows", m.PinnedRows),
              ("overviews", m.OverviewRows), ("extensions", m.ExtensionRows))
          + " · evictable " + Settings.StorageFormat.Bytes(m.EvictableBytes) + " of " + Settings.StorageFormat.Bytes(m.BudgetBytes) + " budget";
}
