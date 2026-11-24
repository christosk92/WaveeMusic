// ── Themes/Classic/Structure/Views/Screens/ClassicSettings.Storage.cs — Storage's cards (settings rework 7.1) ──────────────────────────────────────
//
// Music on this PC (#240 S3) is the WinUI card (board boards240/Main; the owner converges Classic on WinUI's layout): one expander, its header the words and
// the two add buttons, its body the folder rows (a state mark, the path, the folder's line, "⋯" and "✕") in drawer reveals and the status row with
// "Rescan now". The words are the core's (`SettingsMusic`).
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
using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Sdk.Streams;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

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

    // ══ 0. MUSIC ON THIS PC (#240 S3): the WinUI card (board boards240/Main), in Classic's file ═══════════════════════════════════════

    /// <summary>The music folders card, the WinUI layout: an expander whose header is the words, the glyph and the two add buttons (the child dials, each
    /// framed as its row), and whose one item is the folder rows and the status row with "Rescan now". Open at first. "More options" is the generic
    /// expander of its two settings.</summary>
    static Element MusicFoldersCard(SettingsBodyRow row)
    {
        var head = new List<Element>(2);
        if (ChildRow(row, MusicAddDefaultDial) is { } offer) head.Add(Framed(offer, StdButton(offer.Label, () => RunDial(offer.Dial))));
        if (ChildRow(row, MusicAddDial) is { } add) head.Add(Framed(add, AccentButton(add.Label, () => RunDial(add.Dial))));
        return SettingsExpander.Create(new SettingsExpander.Options
        {
            Header = row.Label,
            Description = row.Description,
            HeaderIcon = GlyphOf(row.Dial),
            // One line of buttons beside the words; Compact puts them under the words.
            Content = new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, Children = head.ToArray() },
            Alignment = s_layout.Touch ? SettingsCard.ContentAlignment.Vertical : SettingsCard.ContentAlignment.Right,
            Items = [Embed.Comp(new MusicProps(row), static () => new MusicFolders()) with { Key = "settings:music:body" }],
            IsExpanded = ExpandCell(row.Id, true),
        }) with { Key = "settings:expander:" + row.Id };
    }

    /// <summary>Press an action dial; the page re-reads when it asks.</summary>
    static void RunDial(SettingsDial dial)
    {
        if (dial.Writer.Run is { } run && run()) Settings.Bump();
    }

    const string MusicAddDial = "storage.music.add", MusicAddDefaultDial = "storage.music.addDefault", MusicRescanDial = "storage.music.rescan";

    sealed record MusicProps(SettingsBodyRow Row);

    /// <summary>The folder rows and the status row. It follows the library (its scan progress at most four times a second, its restate) and the
    /// clock (the age of the last scan). A row that appears or leaves rides the drawer reveal; with no folder the empty sentence holds one row's
    /// height, so adding the first folder moves nothing below.</summary>
    sealed class MusicFolders : SettingsComponent
    {
        readonly Dictionary<string, NodeHandle> _anchors = new(StringComparer.OrdinalIgnoreCase);
        OverlayHandle? _menu;

        public override Element Render()
        {
            var layout = UseLayout();
            var row = UseProps<MusicProps>().Row;
            SettingsMusic.Track();
            var tick = UseSignal(0);
            UseInterval(() => tick.Value = tick.Peek() + 1, ClassicSettingsRules.MusicAgoTickMs);
            _ = tick.Value;

            var folders = SettingsMusic.Folders;
            var rescan = ChildRow(row, MusicRescanDial);
            bool composedWith = rescan is { Enabled: true };
            bool composedOffered = ChildRow(row, MusicAddDefaultDial) is not null;
            UseEffect(() => { if (SettingsMusic.Stale(composedWith, composedOffered)) Settings.Bump(); },
                DepKey.From(folders.Count * 4 + (composedWith ? 2 : 0) + (composedOffered ? 1 : 0)));

            var kids = new List<Element>(folders.Count + 1);
            if (folders.Count == 0) kids.Add(MusicDrawer("settings:music:empty", EmptyRow()));
            foreach (string folder in folders)
            {
                kids.Add(MusicDrawer("settings:music:folder:" + folder, FolderRow(SettingsMusic.Line(folder), layout.Touch)));
                // A subfolder Wavee could not read, nested under its music folder (board 15).
                foreach (var subLine in SettingsMusic.Unreadable(folder))
                    kids.Add(MusicDrawer("settings:music:sub:" + subLine.Path, SubRow(subLine, layout.Touch)));
            }
            if (_anchors.Count > folders.Count) PruneAnchors(folders);

            var status = SettingsMusic.Status();
            float barW = layout.Touch ? ClassicSettingsRules.MusicBarWidthCompact : ClassicSettingsRules.MusicBarWidth;
            Element bar = status.Scanning && status.Fraction < 0f
                ? ProgressBar.Indeterminate(barW) with { Key = "settings:music:bar:busy" }
                : ProgressBar.Determinate(Math.Max(0f, status.Fraction), barW) with { Key = "settings:music:bar", Opacity = status.Scanning ? 1f : 0f };
            Element rescanButton = StdButton(Loc.Get(Strings.Settings.Music.Rescan), () => { if (rescan is { } r && r.Dial.Writer.Run is { } run && run()) Settings.Bump(); },
                isEnabled: rescan is { Enabled: true } && !status.Scanning);
            kids.Add(new BoxEl
            {
                // Compact stacks the line (and its bar) over "Rescan now": beside it the line would ellipsize to nothing.
                Key = "settings:music:status", Direction = (byte)(layout.Touch ? 1 : 0), AlignItems = layout.Touch ? FlexAlign.Start : FlexAlign.Center,
                Gap = layout.Touch ? Spacing.M : Spacing.L, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
                MinHeight = ClassicSettingsRules.MusicStatusHeight,
                Padding = new Edges4(MusicTextInset(layout.Touch), Spacing.S, Spacing.L, Spacing.S),
                Children =
                [
                    new BoxEl
                    {
                        Direction = 1, Gap = ClassicSettingsRules.MusicBarGap, Grow = 1f, Shrink = 1f, Basis = layout.Touch ? float.NaN : 0f, MinWidth = 0f,
                        AlignSelf = layout.Touch ? FlexAlign.Stretch : FlexAlign.Auto,
                        Children =
                        [
                            new BoxEl
                            {
                                Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, MinWidth = 0f,
                                Children =
                                [
                                    new TextEl(status.Text)
                                    {
                                        Key = "settings:music:status:text", Size = ClassicSettingsRules.MusicTextSize,
                                        Color = status.Scanning ? Tok.TextPrimary : Tok.TextSecondary,
                                        Grow = 1f, Shrink = 1f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                                    },
                                    new TextEl(status.Aside)
                                    {
                                        Size = ClassicSettingsRules.MusicAsideSize, Color = Tok.TextSecondary, Shrink = 0f, MaxLines = 1,
                                    },
                                ],
                            },
                            bar,
                        ],
                    },
                    // A held scan offers Resume where "Rescan now" stands (board 15, the WinUI layout); the button's slot does not move.
                    status.Paused ? AccentButton(SettingsMusic.ResumeLabel, SettingsMusic.Resume) with { Key = "settings:music:resume" }
                    : rescan is { } rescanRow ? Framed(rescanRow, rescanButton) : rescanButton,
                ],
            });
            return new BoxEl { Direction = 1, AlignSelf = FlexAlign.Stretch, MinWidth = 0f, Children = kids.ToArray() };
        }

        /// <summary>A row in its drawer: the clip box whose presented height springs as it mounts and leaves, the rows below riding it; a hairline
        /// under it (the last one is the divider over the status row).</summary>
        static Element MusicDrawer(string key, Element row) => new BoxEl
        {
            Key = key, Direction = 1, AlignSelf = FlexAlign.Stretch, MinWidth = 0f, ClipToBounds = true, Animate = Design.Reveal.Drawer,
            Children = [row, new BoxEl { Height = 1f, AlignSelf = FlexAlign.Stretch, Fill = Tok.StrokeDividerDefault }],
        };

        /// <summary>Where the folder rows' text starts (past the row's padding, the mark's cell and the gap): the empty sentence and the status line
        /// stand on it. Compact's rows keep the mark too, closer.</summary>
        static float MusicTextInset(bool touch) => Spacing.L + ClassicSettingsRules.MusicMarkCell + (touch ? Spacing.M : Spacing.L);

        /// <summary>No folder yet: the sentence in one row's height.</summary>
        static Element EmptyRow() => new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, AlignSelf = FlexAlign.Stretch, MinWidth = 0f, MinHeight = ClassicSettingsRules.MusicRowHeight,
            Padding = new Edges4(MusicTextInset(s_layout.Touch), Spacing.S, Spacing.L, Spacing.S),
            Children =
            [
                new TextEl(Loc.Get(Strings.Settings.Music.Empty))
                    { Size = ClassicSettingsRules.MusicTextSize, Color = Tok.TextSecondary, Grow = 1f, MinWidth = 0f, Wrap = TextWrap.Wrap },
            ],
        };

        /// <summary>One folder: its state mark, the path, its line ("4,812 songs · 12 can't be played", or the state in the caution ink), "⋯" (Show in
        /// Explorer, Rescan this folder, Remove) and "✕" (remove; no confirm, the toast offers Undo). Compact stacks the path over its line and lifts
        /// both buttons to the 40-DIP target.</summary>
        Element FolderRow(SettingsMusic.FolderLine line, bool touch)
        {
            string folder = line.Path;
            var path = new TextEl(folder)
            {
                Size = ClassicSettingsRules.MusicTextSize, FontFamily = ClassicSettingsRules.MusicPathFont, Color = line.Lead is null ? Tok.TextPrimary : Tok.TextSecondary,
                MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Shrink = 1f, MinWidth = 0f,
            };
            var words = new List<Element>(2);
            if (line.Lead is { } lead)
                words.Add(new TextEl(line.Rest.Length > 0 ? lead + " · " : lead)
                    { Size = ClassicSettingsRules.MusicTextSize, Color = Tok.SystemFillCaution, Shrink = 0f, MaxLines = 1 });
            if (line.Rest.Length > 0)
                // Compact wraps the folder's words onto a second line rather than cut them.
                words.Add(new TextEl(line.Rest)
                {
                    Size = ClassicSettingsRules.MusicTextSize, Color = Tok.TextSecondary, Shrink = 1f, MinWidth = 0f,
                    Wrap = touch ? TextWrap.Wrap : TextWrap.NoWrap, MaxLines = touch ? 2 : 1, Trim = touch ? TextTrim.WordEllipsis : TextTrim.CharacterEllipsis,
                });
            var status = new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, MinWidth = 0f, Shrink = 1f, Children = words.ToArray() };

            var buttonStyle = IconButton.DefaultStyle with { Size = touch ? s_layout.Target : ClassicSettingsRules.MusicIconButton };
            var more = ToolTip.Wrap(IconButton.Create(Icons.More, () => OpenMenu(folder), buttonStyle with { GlyphSize = ClassicSettingsRules.MusicMoreGlyph })
                with { Key = "settings:music:more:" + folder, Shrink = 0f, OnRealized = h => _anchors[folder] = h }, Loc.Get(Strings.Settings.Music.FolderMenu));
            var remove = ToolTip.Wrap(IconButton.Create(Icons.Cancel, () => SettingsVerbs.RemoveMusicFolder(folder),
                buttonStyle with { GlyphSize = ClassicSettingsRules.MusicRemoveGlyph }) with { Key = "settings:music:remove:" + folder, Shrink = 0f },
                Loc.Get(Strings.Settings.Music.Remove));

            Element text = touch
                ? new BoxEl
                {
                    Direction = 1, Gap = ClassicSettingsRules.MusicStackGap, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f,
                    Children = [path, status],
                }
                : new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.L, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f,
                    Children = [path with { Width = ClassicSettingsRules.MusicPathWidth }, status with { Grow = 1f, Basis = 0f }],
                };
            var kids = new List<Element>(5) { MarkCell(line.Mark), text };
            // A folder that refuses this user: Fix permissions beside its verbs (board 15).
            if (line.Fix is { } fix) kids.Add(StdButton(SettingsMusic.FixLabel, fix) with { Key = "settings:music:fix:" + folder, Shrink = 0f });
            kids.Add(more);
            kids.Add(remove);
            return new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = touch ? Spacing.M : Spacing.L, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
                MinHeight = ClassicSettingsRules.MusicRowHeight, Padding = new Edges4(Spacing.L, Spacing.XS, touch ? Spacing.XS : Spacing.L, Spacing.XS),
                Children = kids.ToArray(),
            };
        }

        /// <summary>An unreadable subfolder under its music folder (board 15): the nesting elbow and the lock in the mark's cell, the path, "Can't
        /// read this folder" in the caution ink, Fix permissions and Remove (skip it). Compact stacks the words over the two buttons.</summary>
        static Element SubRow(SettingsMusic.SubLine line, bool touch)
        {
            var words = new BoxEl
            {
                Direction = (byte)(touch ? 1 : 0), AlignItems = touch ? FlexAlign.Start : FlexAlign.Center, Gap = touch ? ClassicSettingsRules.MusicStackGap : Spacing.L,
                Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f,
                Children =
                [
                    new TextEl(line.Path)
                    {
                        Size = ClassicSettingsRules.MusicTextSize, FontFamily = ClassicSettingsRules.MusicPathFont, Color = Tok.TextPrimary,
                        MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Shrink = 1f, MinWidth = 0f, Width = touch ? float.NaN : ClassicSettingsRules.MusicPathWidth,
                    },
                    new TextEl(line.Lead) { Size = ClassicSettingsRules.MusicTextSize, Color = Tok.SystemFillCaution, Shrink = 1f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                ],
            };
            var verbs = new BoxEl
            {
                Direction = 0, Gap = Spacing.M, Shrink = 0f, AlignItems = FlexAlign.Center,
                Children =
                [
                    StdButton(SettingsMusic.FixLabel, line.Fix) with { Key = "settings:music:sub:fix:" + line.Path },
                    StdButton(Loc.Get(Strings.Settings.Music.Remove), line.Skip) with { Key = "settings:music:sub:skip:" + line.Path },
                ],
            };
            Element mark = new BoxEl
            {
                Width = ClassicSettingsRules.MusicMarkCell, Height = ClassicSettingsRules.MusicMarkCell, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Children = [Icon(Icons.Lock, ClassicSettingsRules.MusicMarkSize, Tok.SystemFillCaution)],
            };
            return new BoxEl
            {
                Direction = (byte)(touch ? 1 : 0), AlignItems = touch ? FlexAlign.Stretch : FlexAlign.Center, Gap = touch ? Spacing.M : Spacing.L,
                AlignSelf = FlexAlign.Stretch, MinWidth = 0f, MinHeight = ClassicSettingsRules.MusicRowHeight,
                // Nested: one mark cell further in than its music folder's row.
                Padding = new Edges4(Spacing.L + ClassicSettingsRules.MusicMarkCell + Spacing.L, Spacing.XS, touch ? Spacing.XS : Spacing.L, Spacing.XS),
                Children = touch
                    ? [new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, MinWidth = 0f, Children = [mark, words] }, verbs]
                    : [mark, words, verbs],
            };
        }

        /// <summary>The row's state mark in its fixed cell: up to date (the success check), working (a ring), needs you (the caution mark).</summary>
        static Element MarkCell(SettingsMusic.Mark mark) => new BoxEl
        {
            Width = ClassicSettingsRules.MusicMarkCell, Height = ClassicSettingsRules.MusicMarkCell, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Children = mark switch
            {
                SettingsMusic.Mark.Ok => [Icon(Icons.Accept, ClassicSettingsRules.MusicMarkSize, Tok.SystemFillSuccess)],
                SettingsMusic.Mark.Busy => [ProgressRing.Indeterminate(size: ClassicSettingsRules.MusicMarkSize)],
                SettingsMusic.Mark.Caution => [Icon(Icons.Warning, ClassicSettingsRules.MusicMarkSize, Tok.SystemFillCaution)],
                _ => [],
            },
        };

        /// <summary>The folder's one menu, anchored at its "⋯"; a second press closes it.</summary>
        void OpenMenu(string folder)
        {
            if (SettingsContext.Overlay is not { } overlay) return;
            if (_menu is { IsOpen: true } open) { open.Close(); return; }
            var items = new List<MenuFlyoutItem>(4)
            {
                new(Loc.Get(Strings.Settings.Music.ShowInExplorer), Icons.FolderOpen, true, () => SettingsVerbs.RevealMusicFolder(folder)),
                new(Loc.Get(Strings.Settings.Music.RescanFolder), Icons.Refresh, true, () => SettingsVerbs.RescanMusicFolder(folder)),
                MenuFlyoutItem.Separator,
                new(Loc.Get(Strings.Settings.Music.Remove), Icons.Delete, true, () => SettingsVerbs.RemoveMusicFolder(folder)),
            };
            var handle = overlay.Open(() => _anchors.TryGetValue(folder, out var a) ? a : NodeHandle.Null,
                () => MenuFlyout.Create(items, () => _menu?.Close(), size: OverlayFit.MenuSize),
                FlyoutPlacement.BottomEdgeAlignedRight,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss) { ConstrainToRootBounds = false });
            _menu = handle;
            handle.ClosedAction = () => { if (ReferenceEquals(_menu, handle)) _menu = null; };
        }

        /// <summary>A removed folder's "⋯" handle goes; the others keep theirs (a realization callback fires once per node).</summary>
        void PruneAnchors(IReadOnlyList<string> folders)
        {
            List<string>? stale = null;
            foreach (string key in _anchors.Keys)
            {
                bool live = false;
                for (int i = 0; i < folders.Count && !live; i++) live = string.Equals(folders[i], key, StringComparison.OrdinalIgnoreCase);
                if (!live) (stale ??= []).Add(key);
            }
            if (stale is null) return;
            foreach (string key in stale) _anchors.Remove(key);
        }
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
            "storage.music.folders" => MusicFoldersCard(row),
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
