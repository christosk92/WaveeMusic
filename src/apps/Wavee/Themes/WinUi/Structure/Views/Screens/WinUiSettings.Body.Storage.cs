// ── Themes/WinUi/Structure/Views/Screens/WinUiSettings.Body.Storage.cs — Storage's WinUI cards: the music folders, the census, the folders, the budget editor ──
//
// Music on this PC (#240 S3, board boards240/Main): one expander card, its header the words and the two add buttons, its body the folder rows (a state mark, the
// path, the folder's line, "⋯" and "✕"), each in a drawer reveal, then the status row (the scan's line and bar, or when the last one ran) with "Rescan now";
// "More options" is the generic expander of its two settings. The words are the core's (`SettingsMusic`); Classic draws the same card.
//
// The census (the phase, the snapshot, the cache status, the key count, the metadata stats; the first visit starts it; a clear re-runs it) is the core's and
// arrives as `SettingsPageProps.Storage`. On this PC leads with its block (a spinner while it reads, the usage card with the nine fixed hues on its bar and
// its legend, or the failed bar with Retry); each folder row carries its hue accent, its size (simply ABSENT until the census lands — never "—", never
// "0 B"), "Open folder" (the folder dial's own writer) and, where it has one, its clear (the child dial: it confirms first). The playback cache's budget is
// the three-mode editor (Fixed size · Drive share · Unlimited) with its meter; the cache location's PATH is its description.
//
// RESIDENT CACHE (ch 27 G2, decided): 0.3 has no resident-playlist cache and no per-table census, so there is no "In memory" section.

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

    // ══ 0. MUSIC ON THIS PC (#240 S3; board boards240/Main "1 · Settings › Storage › Music on this PC") ═══════════════════════════

    /// <summary>The music folders card: its glyph, words and two add buttons (the child dials, hosted) in the header, and one body under the expander: the
    /// folder rows, then the status row with "Rescan now". Open at first (<see cref="InitiallyOpen"/>).</summary>
    static Element MusicFoldersCard(RowCtx x)
    {
        var head = new List<Element>(2);
        if (Child(x, MusicAddDefaultDial) is { } offer) head.Add(ChildHost(x, offer, StdButton(offer.Label, () => Press(offer.Dial))));
        if (Child(x, MusicAddDial) is { } add) head.Add(ChildHost(x, add, AccentButton(add.Label, () => Press(add.Dial))));
        // One line of buttons beside the words; where the words would get less than their basis (Compact) the header wraps them under the words.
        var buttons = new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, Children = head.ToArray() };
        var card = BaseCard(x, buttons) with { Glyph = Glyph(x.Row.Dial.Glyph), WrapControl = true };
        return Expander(card, [Embed.Comp(new MusicProps(x), static () => new MusicFolders()) with { Key = "settings:music:body" }], x.Expanded);
    }

    /// <summary>"More options": the two library settings (the generic toggle and combo) under an expander with the board's glyph.</summary>
    static Element MusicOptionsCard(RowCtx x)
    {
        var items = new Element[x.Row.Children.Length];
        for (int i = 0; i < items.Length; i++) items[i] = ChildHost(x, x.Row.Children[i]);
        return Expander(BaseCard(x, null) with { Glyph = Glyph(x.Row.Dial.Glyph) }, items, x.Expanded);
    }

    const string MusicAddDial = "storage.music.add", MusicAddDefaultDial = "storage.music.addDefault", MusicRescanDial = "storage.music.rescan";

    sealed record MusicProps(RowCtx X);

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
            var x = UseProps<MusicProps>().X;
            SettingsMusic.Track();
            var tick = UseSignal(0);
            UseInterval(() => tick.Value = tick.Peek() + 1, WinUiSettingsRules.MusicAgoTickMs);
            _ = tick.Value;

            var folders = SettingsMusic.Folders;
            var rescan = Child(x, MusicRescanDial);
            bool composedWith = rescan is { Enabled: true };
            bool composedOffered = Child(x, MusicAddDefaultDial) is not null;
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
            float barW = layout.Touch ? WinUiSettingsRules.MusicBarWidthCompact : WinUiSettingsRules.MusicBarWidth;
            Element bar = status.Scanning && status.Fraction < 0f
                ? ProgressBar.Indeterminate(barW) with { Key = "settings:music:bar:busy" }
                : ProgressBar.Determinate(Math.Max(0f, status.Fraction), barW) with { Key = "settings:music:bar", Opacity = status.Scanning ? 1f : 0f };
            Element rescanButton = StdButton(Loc.Get(Strings.Settings.Music.Rescan), () => { if (rescan is { } r) Press(r.Dial); },
                isEnabled: rescan is { Enabled: true } && !status.Scanning);
            kids.Add(new BoxEl
            {
                // Compact stacks the line (and its bar) over "Rescan now": beside it the line would ellipsize to nothing.
                Key = "settings:music:status", Direction = (byte)(layout.Touch ? 1 : 0), AlignItems = layout.Touch ? FlexAlign.Start : FlexAlign.Center,
                Gap = layout.Touch ? Spacing.M : Spacing.L, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
                MinHeight = WinUiSettingsRules.MusicStatusHeight,
                Padding = new Edges4(MusicTextInset(layout.Touch), Spacing.S, Spacing.L, Spacing.S),
                Children =
                [
                    new BoxEl
                    {
                        Direction = 1, Gap = WinUiSettingsRules.MusicBarGap, Grow = 1f, Shrink = 1f, Basis = layout.Touch ? float.NaN : 0f, MinWidth = 0f,
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
                                        Key = "settings:music:status:text", Size = WinUiSettingsRules.MusicTextSize,
                                        Color = status.Scanning ? Tok.TextPrimary : Tok.TextSecondary,
                                        Grow = 1f, Shrink = 1f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                                    },
                                    new TextEl(status.Aside)
                                    {
                                        Size = WinUiSettingsRules.MusicAsideSize, Color = Tok.TextSecondary, Shrink = 0f, MaxLines = 1,
                                    },
                                ],
                            },
                            bar,
                        ],
                    },
                    // A held scan offers Resume where "Rescan now" stands (board 15); the button's slot does not move.
                    status.Paused ? AccentButton(SettingsMusic.ResumeLabel, SettingsMusic.Resume) with { Key = "settings:music:resume" }
                    : rescan is { } rescanRow ? ChildHost(x, rescanRow, rescanButton) : rescanButton,
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
        static float MusicTextInset(bool touch) => Spacing.L + WinUiSettingsRules.MusicMarkCell + (touch ? Spacing.M : Spacing.L);

        /// <summary>No folder yet: the sentence in one row's height.</summary>
        static Element EmptyRow() => new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, AlignSelf = FlexAlign.Stretch, MinWidth = 0f, MinHeight = WinUiSettingsRules.MusicRowHeight,
            Padding = new Edges4(MusicTextInset(s_layout.Touch), Spacing.S, Spacing.L, Spacing.S),
            Children =
            [
                new TextEl(Loc.Get(Strings.Settings.Music.Empty))
                    { Size = WinUiSettingsRules.MusicTextSize, Color = Tok.TextSecondary, Grow = 1f, MinWidth = 0f, Wrap = TextWrap.Wrap },
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
                Size = WinUiSettingsRules.MusicTextSize, FontFamily = WinUiSettingsRules.MusicPathFont, Color = line.Lead is null ? Tok.TextPrimary : Tok.TextSecondary,
                MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Shrink = 1f, MinWidth = 0f,
            };
            var words = new List<Element>(2);
            if (line.Lead is { } lead)
                words.Add(new TextEl(line.Rest.Length > 0 ? lead + " · " : lead)
                    { Size = WinUiSettingsRules.MusicTextSize, Color = Tok.SystemFillCaution, Shrink = 0f, MaxLines = 1 });
            if (line.Rest.Length > 0)
                // Compact wraps the folder's words onto a second line rather than cut them.
                words.Add(new TextEl(line.Rest)
                {
                    Size = WinUiSettingsRules.MusicTextSize, Color = Tok.TextSecondary, Shrink = 1f, MinWidth = 0f,
                    Wrap = touch ? TextWrap.Wrap : TextWrap.NoWrap, MaxLines = touch ? 2 : 1, Trim = touch ? TextTrim.WordEllipsis : TextTrim.CharacterEllipsis,
                });
            var status = new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, MinWidth = 0f, Shrink = 1f, Children = words.ToArray() };

            var buttonStyle = IconButton.DefaultStyle with { Size = touch ? s_layout.Target : WinUiSettingsRules.MusicIconButton };
            var more = ToolTip.Wrap(IconButton.Create(Icons.More, () => OpenMenu(folder), buttonStyle with { GlyphSize = WinUiSettingsRules.MusicMoreGlyph })
                with { Key = "settings:music:more:" + folder, Shrink = 0f, OnRealized = h => _anchors[folder] = h }, Loc.Get(Strings.Settings.Music.FolderMenu));
            var remove = ToolTip.Wrap(IconButton.Create(Icons.Cancel, () => SettingsVerbs.RemoveMusicFolder(folder),
                buttonStyle with { GlyphSize = WinUiSettingsRules.MusicRemoveGlyph }) with { Key = "settings:music:remove:" + folder, Shrink = 0f },
                Loc.Get(Strings.Settings.Music.Remove));

            Element text = touch
                ? new BoxEl
                {
                    Direction = 1, Gap = WinUiSettingsRules.MusicStackGap, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f,
                    Children = [path, status],
                }
                : new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.L, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f,
                    Children = [path with { Width = WinUiSettingsRules.MusicPathWidth }, status with { Grow = 1f, Basis = 0f }],
                };
            var kids = new List<Element>(5) { MarkCell(line.Mark), text };
            // A folder that refuses this user: Fix permissions beside its verbs (board 15).
            if (line.Fix is { } fix) kids.Add(StdButton(SettingsMusic.FixLabel, fix) with { Key = "settings:music:fix:" + folder, Shrink = 0f });
            kids.Add(more);
            kids.Add(remove);
            return new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = touch ? Spacing.M : Spacing.L, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
                MinHeight = WinUiSettingsRules.MusicRowHeight, Padding = new Edges4(Spacing.L, Spacing.XS, touch ? Spacing.XS : Spacing.L, Spacing.XS),
                Children = kids.ToArray(),
            };
        }

        /// <summary>An unreadable subfolder under its music folder (board 15): the nesting elbow and the lock in the mark's cell, the path, "Can't
        /// read this folder" in the caution ink, Fix permissions and Remove (skip it). Compact stacks the words over the two buttons.</summary>
        static Element SubRow(SettingsMusic.SubLine line, bool touch)
        {
            var words = new BoxEl
            {
                Direction = (byte)(touch ? 1 : 0), AlignItems = touch ? FlexAlign.Start : FlexAlign.Center, Gap = touch ? WinUiSettingsRules.MusicStackGap : Spacing.L,
                Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f,
                Children =
                [
                    new TextEl(line.Path)
                    {
                        Size = WinUiSettingsRules.MusicTextSize, FontFamily = WinUiSettingsRules.MusicPathFont, Color = Tok.TextPrimary,
                        MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Shrink = 1f, MinWidth = 0f,
                        // One mark cell further in than its folder's path, so its words stand on the folder rows' status column.
                        Width = touch ? float.NaN : WinUiSettingsRules.MusicPathWidth - WinUiSettingsRules.MusicMarkCell - Spacing.L,
                    },
                    new TextEl(line.Lead) { Size = WinUiSettingsRules.MusicTextSize, Color = Tok.SystemFillCaution, Shrink = 1f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
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
                Width = WinUiSettingsRules.MusicMarkCell, Height = WinUiSettingsRules.MusicMarkCell, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Children = [Icon(Icons.Lock, WinUiSettingsRules.MusicMarkSize, Tok.SystemFillCaution)],
            };
            return new BoxEl
            {
                Direction = (byte)(touch ? 1 : 0), AlignItems = touch ? FlexAlign.Stretch : FlexAlign.Center, Gap = touch ? Spacing.M : Spacing.L,
                AlignSelf = FlexAlign.Stretch, MinWidth = 0f, MinHeight = WinUiSettingsRules.MusicRowHeight,
                // Nested: one mark cell further in than its music folder's row.
                Padding = new Edges4(Spacing.L + WinUiSettingsRules.MusicMarkCell + Spacing.L, Spacing.XS, touch ? Spacing.XS : Spacing.L, Spacing.XS),
                Children = touch
                    ? [new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, MinWidth = 0f, Children = [mark, words] }, verbs]
                    : [mark, words, verbs],
            };
        }

        /// <summary>The row's state mark in its fixed cell: up to date (the success check), working (a ring), needs you (the caution mark).</summary>
        static Element MarkCell(SettingsMusic.Mark mark) => new BoxEl
        {
            Width = WinUiSettingsRules.MusicMarkCell, Height = WinUiSettingsRules.MusicMarkCell, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Children = mark switch
            {
                SettingsMusic.Mark.Ok => [Icon(Icons.Accept, WinUiSettingsRules.MusicMarkSize, Tok.SystemFillSuccess)],
                SettingsMusic.Mark.Busy => [ProgressRing.Indeterminate(size: WinUiSettingsRules.MusicMarkSize)],
                SettingsMusic.Mark.Caution => [Icon(Icons.Warning, WinUiSettingsRules.MusicMarkSize, Tok.SystemFillCaution)],
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
