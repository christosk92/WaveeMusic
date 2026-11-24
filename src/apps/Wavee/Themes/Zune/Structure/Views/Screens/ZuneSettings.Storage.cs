// ── Themes/Zune/Structure/Views/Screens/ZuneSettings.Storage.cs — Storage's sections (themes plan 6.3, Z8b; settings rework phase 4) ─────────────────
//
// Music on this PC (#240 S3, the plan's §3.8 Zune wireframe): "music folders" and its sentence, a line per folder (the path as typed, wrapping; its words
// lowercase; "options" opens the folder's card; a network folder adds its share's state and one verb, S13), the status line with the scan's bar, then the
// command words; "more options" is its two settings' rows.
// The words are the core's (`SettingsMusic`).
//
// Board Z-Settings-Sections ("downloads & storage"), without its downloads (owner Q29 / NQ4: no offline playback, so no downloaded songs, no "remove all downloads"):
// the census (the usage line and bar, the nine fixed hues) over On this PC, each census category's row (its hue, its size once the census lands, "open folder",
// and its clear or remove where it has one), the three-mode budget editor, the cache location and the metadata cache. The census itself (the phase, the snapshot,
// the cache status, the key count, the metadata stats; the first visit starts it; a clear re-runs it) is the core's (`Settings.Host.cs`) and arrives as
// `SettingsPageProps.Storage`; the clears, the budget writes, the folder doors and the factory reset are the dials' writers.

using System.Globalization;
using FluentGpu;
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
using Z = Wavee.Themes.Zune.ZuneSettingsRules;

namespace Wavee.Themes.Zune;

internal static partial class ZuneSettings
{
    /// <summary>The census parts' words, in `Settings.StorageRules.Parts` order: library, runtime, logs, local store, audio bodies, license keys, images,
    /// AI models, other app data.</summary>
    static readonly string[] s_storagePartKeys =
    [
        Strings.Settings.Storage.Library, Strings.Settings.Storage.Runtime, Strings.Settings.Storage.Logs,
        Strings.Settings.Storage.LocalStore, Strings.Settings.Storage.AudioBodies, Strings.Settings.Storage.LicenseKeys,
        Strings.Settings.Storage.ImageCache, Strings.Settings.Storage.AiModels, Strings.Settings.Storage.OtherData,
    ];

    // ── Music on this PC (#240 S3; plan §3.8's Zune wireframe) ───────────────────────────────────────────────────────────────

    sealed record MusicArgs(SettingsBodyRow Row, SettingsArrival Focus);

    const string MusicAddDial = "storage.music.add", MusicAddDefaultDial = "storage.music.addDefault", MusicRescanDial = "storage.music.rescan";

    /// <summary>"More options": its two settings as Zune's own rows (Zune draws no expanders), keyed as the options dial's row.</summary>
    static Element MusicOptions(Paint d, SettingsBodyRow row)
    {
        var rows = new List<Element>(row.Children.Length);
        foreach (var child in row.Children) DrawRow(d, child, rows);
        return new BoxEl
        {
            Key = SettingsKeys.ZuneRow(row.Id), Direction = 1, Gap = Z.RowGap, AlignSelf = FlexAlign.Stretch, MinWidth = 0f, OnRealized = NoteRow(row.Id),
            Children = rows.ToArray(),
        };
    }

    /// <summary>The music folders in Zune's voice: "music folders" and its sentence, one line per folder (the path as typed, never lowered nor shrunk:
    /// it wraps; its words lowercase; "options" opens the folder's card: show in explorer, rescan this folder, remove), the status line with the scan's
    /// bar, and the command words (add a folder, add my music folder, rescan now). A folder line that appears or leaves rides the drawer reveal; with no
    /// folder the empty sentence holds one line's height.</summary>
    sealed class MusicFoldersBlock : ZuneScreenComponent
    {
        readonly Dictionary<string, NodeHandle> _anchors = new(StringComparer.OrdinalIgnoreCase);
        OverlayHandle? _handle;

        public override Element Render()
        {
            var lay = UseLayout();
            var k = new ZuneKit(lay);
            var args = UseProps<MusicArgs>();
            var row = args.Row;
            UseEffect(() => (Action?)(() => { if (_handle is { IsOpen: true } open) open.Close(); _handle = null; }), DepKey.Empty);
            UseActivation(onDeactivated: () => { if (_handle is { IsOpen: true } open) open.Close(); _handle = null; });
            SettingsMusic.Track();
            var tick = UseSignal(0);
            UseInterval(() => tick.Value = tick.Peek() + 1, Z.MusicAgoTickMs);
            _ = tick.Value;

            var folders = SettingsMusic.Folders;
            SettingsBodyRow? rescan = null, add = null, offer = null;
            foreach (var child in row.Children)
                switch (child.Id)
                {
                    case MusicRescanDial: rescan = child; break;
                    case MusicAddDial: add = child; break;
                    case MusicAddDefaultDial: offer = child; break;
                }
            bool composedWith = rescan is { Enabled: true };
            bool composedOffered = offer is not null;
            UseEffect(() => { if (SettingsMusic.Stale(composedWith, composedOffered)) Settings.Bump(); },
                DepKey.From(folders.Count * 4 + (composedWith ? 2 : 0) + (composedOffered ? 1 : 0)));

            var kids = new List<Element>(folders.Count + 4)
            {
                new BoxEl
                {
                    Key = "zs:music:head", Direction = 1, Gap = Z.LabelGap, MinWidth = 0f,
                    Children = [LabelOf(args.Focus, row.Id, ZuneText.Label(row.Label)), ZuneText.Hint(row.Description ?? "")],
                },
            };
            var list = new List<Element>(folders.Count + 1);
            if (folders.Count == 0)
                list.Add(Drawer("zs:music:empty", new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, MinHeight = Z.MusicRowH, MinWidth = 0f,
                    Children = [ZuneText.Prose(Loc.Get(Strings.Settings.Music.Empty)) with { Grow = 1f }],
                }));
            foreach (string folder in folders) list.Add(Drawer("zs:music:folder:" + folder, FolderLine(k, SettingsMusic.Line(folder))));
            if (_anchors.Count > folders.Count) PruneAnchors(folders);
            kids.Add(new BoxEl { Key = "zs:music:list", Direction = 1, AlignSelf = FlexAlign.Stretch, MinWidth = 0f, Children = list.ToArray() });

            var status = SettingsMusic.Status();
            Element bar = status.Scanning && status.Fraction < 0f
                ? ProgressBar.Indeterminate(Z.MusicBarWidth) with { Key = "zs:music:bar:busy" }
                : ProgressBar.Determinate(Math.Max(0f, status.Fraction), Z.MusicBarWidth) with { Key = "zs:music:bar", Opacity = status.Scanning ? 1f : 0f };
            kids.Add(new BoxEl
            {
                Key = "zs:music:status", Direction = 1, Gap = Z.MusicStatusGap, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
                Children =
                [
                    new BoxEl
                    {
                        Direction = 0, AlignItems = FlexAlign.Center, Gap = Z.ControlGap, MinWidth = 0f,
                        Children =
                        [
                            ZuneText.Meta(status.Text) with { Key = "zs:music:status:text", Color = status.Scanning ? Tok.TextPrimary : Tok.TextSecondary, Shrink = 1f },
                            ZuneText.Meta(status.Aside) with { Color = Tok.TextTertiary, Shrink = 0f },
                        ],
                    },
                    bar,
                ],
            });

            var cmds = new List<Element>(3);
            if (add is { } a) cmds.Add(k.Cmd(a.Label, RunOf(a), a.Enabled, accent: true) with { IsEnabled = a.Enabled, Key = SettingsKeys.ZuneRow(a.Id), OnRealized = NoteRow(a.Id) });
            if (offer is { } o) cmds.Add(k.Cmd(o.Label, RunOf(o), o.Enabled) with { IsEnabled = o.Enabled, Key = SettingsKeys.ZuneRow(o.Id), OnRealized = NoteRow(o.Id) });
            if (rescan is { } r)
                cmds.Add(k.Cmd(r.Label, RunOf(r), r.Enabled && !status.Scanning) with { IsEnabled = r.Enabled && !status.Scanning, Key = SettingsKeys.ZuneRow(r.Id), OnRealized = NoteRow(r.Id) });
            kids.Add(new BoxEl
            {
                Key = "zs:music:commands", Direction = 0, Wrap = true, Gap = Z.LinkGap, AlignItems = FlexAlign.Center, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
                // The commands' padding stands their words in from the column; the row stands back so the first word sits on the column's edge.
                Margin = new Edges4(-Z.CmdPadding.Left, 0f, 0f, 0f),
                Children = cmds.ToArray(),
            });
            return new BoxEl
            {
                Key = SettingsKeys.ZuneRow(row.Id), Direction = 1, Gap = Z.MusicBlockGap, AlignSelf = FlexAlign.Stretch, MinWidth = 0f, OnRealized = NoteRow(row.Id),
                Children = kids.ToArray(),
            };
        }

        /// <summary>A line in its drawer: the clip box whose presented height springs as it mounts and leaves, the lines below riding it.</summary>
        static Element Drawer(string key, Element line) => new BoxEl
        {
            Key = key, Direction = 1, AlignSelf = FlexAlign.Stretch, MinWidth = 0f, ClipToBounds = true, Animate = Design.Reveal.Drawer, Children = [line],
        };

        /// <summary>One folder: the path as the user typed it (the 15/20 voice, wrapping, never lowered), its words (lowercase; a state in the caution ink) and
        /// "options". Compact stacks the words under the path. A network folder (S13) shows the path the user knows, "1,204 songs · network" under it,
        /// the share's state in a column that keeps two lines and the scan's bar in every state, and the state's one verb ("sign in", "retry") in a slot
        /// of its own before "options": a state change swaps the words and the verb in place.</summary>
        Element FolderLine(ZuneKit k, SettingsMusic.FolderLine line)
        {
            string folder = line.Path;
            bool network = line.Network;
            var path = R.Type.TrackTitle(line.Display) with
            {
                Color = line.Dim ? Tok.TextSecondary : Tok.TextPrimary, Wrap = TextWrap.Wrap, MinWidth = 0f, Grow = 1f, Shrink = 1f, Basis = 0f,
            };
            var words = new List<Element>(2);
            if (!network && line.Lead is { } lead)
                words.Add(ZuneText.Meta(line.Rest.Length > 0 ? lead + " · " : lead) with { Color = Tok.SystemFillCaution, Shrink = 0f });
            if (!network && line.Rest.Length > 0) words.Add(ZuneText.Meta(line.Rest) with { Shrink = 1f });
            bool stack = k.Lay.StacksRows;
            if (network)
            {
                // A network folder's words wrap as one paragraph (the lead in its ink, the rest in the secondary), lowercase as every word Zune writes.
                var meta = R.Type.DenseMeta("");
                var spans = new List<TextSpan>(2);
                if (line.Lead is { } netLead)
                {
                    bool caution = line.LeadTone == SettingsMusic.Tone.Caution;
                    spans.Add(new TextSpan(ZuneText.Word(line.Rest.Length == 0 ? netLead : caution ? netLead + " · " : netLead + " "),
                        Color: caution ? Tok.SystemFillCaution : Tok.TextPrimary));
                }
                if (line.Rest.Length > 0) spans.Add(new TextSpan(ZuneText.Word(line.Rest), Color: Tok.TextSecondary));
                words.Add(new SpanTextEl(spans.ToArray())
                {
                    Size = meta.Size, LineHeight = meta.LineHeight, Color = Tok.TextSecondary, Shrink = 1f, MinWidth = 0f,
                    Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.WordEllipsis,
                });
            }
            var status = new BoxEl
            {
                Key = "zs:music:line:" + folder, Direction = 0, AlignItems = network ? FlexAlign.Start : FlexAlign.Center, MinWidth = 0f,
                MinHeight = network ? Z.MusicTwoLines : 0f, Shrink = stack ? 1f : 0f,
                Width = stack || network ? float.NaN : Z.MusicStatusW, Children = words.ToArray(),
            };
            var options = k.Cmd(Loc.Get(Strings.Settings.Music.FolderOptions), () => Open(folder, k)) with
            {
                Key = "zs:music:options:" + folder, OnRealized = h => _anchors[folder] = h,
            };
            if (!network)
            {
                Element text = stack
                    ? new BoxEl { Direction = 1, Gap = Z.LabelGap, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, Children = [path with { Grow = 0f, Basis = float.NaN }, status] }
                    : new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = Z.RowColumnGap, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, Children = [path, status] };
                return new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Gap = Z.ControlGap, MinHeight = Z.MusicRowH, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
                    Children = [text, options],
                };
            }

            // ── a network folder ──
            var where = new BoxEl
            {
                Direction = 1, Gap = Z.LabelGap, MinWidth = 0f, Grow = stack ? 0f : 1f, Shrink = 1f, Basis = stack ? float.NaN : 0f,
                Children = [path with { Grow = 0f, Basis = float.NaN }, ZuneText.Meta(line.Sub ?? "") with { Color = Tok.TextTertiary }],
            };
            var bar = ProgressBar.Determinate(Math.Max(0f, line.Fraction), stack ? Z.MusicNetBarWidthCompact : Z.MusicNetStatusW)
                with { Key = "zs:music:netbar:" + folder, Opacity = line.Fraction >= 0f ? 1f : 0f };
            var state = new BoxEl
            {
                Direction = 1, Gap = Z.MusicStatusGap, MinWidth = 0f, Shrink = stack ? 1f : 0f, Width = stack ? float.NaN : Z.MusicNetStatusW,
                Children = [status, bar],
            };
            Element text2 = stack
                ? new BoxEl { Direction = 1, Gap = Z.LabelGap, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, Children = [where, state] }
                : new BoxEl { Direction = 0, AlignItems = FlexAlign.Start, Gap = Z.RowColumnGap, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, Children = [where, state] };
            var verb = line.Action;
            // The verb's slot is laid out in every state, so a verb that comes or goes never moves the words beside it.
            var slot = new BoxEl
            {
                Key = "zs:music:verbslot:" + folder, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.End, MinWidth = Z.MusicVerbW, Shrink = 0f,
                Children = SettingsMusic.VerbKey(verb) is { } verbKey
                    ? [k.Cmd(Loc.Get(verbKey), () => SettingsMusic.Run(verb, folder), accent: verb == SettingsMusic.Verb.SignIn) with { Key = "zs:music:verb:" + folder }]
                    : [],
            };
            return new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = Z.ControlGap, MinHeight = Z.MusicNetRowH, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
                Padding = new Edges4(0f, Z.MusicNetPadY, 0f, Z.MusicNetPadY),
                Children = [text2, slot, options],
            };
        }

        /// <summary>The folder's options card under its "options" word (light dismiss); a second press closes it.</summary>
        void Open(string folder, ZuneKit k)
        {
            if (_handle is { IsOpen: true } open) { open.Close(); return; }
            if (SettingsContext.Overlay is not { } overlay) return;
            var opened = overlay.Open(() => _anchors.TryGetValue(folder, out var a) ? a : NodeHandle.Null,
                () => new BoxEl
                {
                    Key = "zs:music:card", Width = Z.MusicOptionsW, Direction = 1, Padding = Z.MusicOptionsPad,
                    Children =
                    [
                        k.Cmd(Loc.Get(Strings.Settings.Music.ShowInExplorer), () => { Close(); SettingsVerbs.RevealMusicFolder(folder); }) with { Key = "zs:music:card:reveal" },
                        k.Cmd(Loc.Get(Strings.Settings.Music.RescanFolder), () => { Close(); SettingsVerbs.RescanMusicFolder(folder); }) with { Key = "zs:music:card:rescan" },
                        k.Cmd(Loc.Get(Strings.Settings.Music.Remove), () => { Close(); SettingsVerbs.RemoveMusicFolder(folder); }) with { Key = "zs:music:card:remove" },
                    ],
                },
                FlyoutPlacement.BottomEdgeAlignedRight,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Popup) { ConstrainToRootBounds = true });
            _handle = opened;
            opened.ClosedAction = () => { if (ReferenceEquals(_handle, opened)) _handle = null; };
        }

        void Close()
        {
            if (_handle is { IsOpen: true } open) open.Close();
            _handle = null;
        }

        /// <summary>A removed folder's "options" handle goes; the others keep theirs (a realization callback fires once per node).</summary>
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

    /// <summary>The census over On this PC: the usage line and bar once it lands, a spinner while it reads, the failure with a retry.</summary>
    static Element Census(Paint d)
    {
        var storage = d.Props.Storage;
        var s = storage.Snapshot;
        return storage.Phase == Settings.StorageLoadPhase.Failed
            ? ZuneKit.Notice(InfoBarSeverity.Error, Loc.Get(Strings.Settings.Storage.ReadFailed), storage.Error ?? Loc.Get(Strings.Common.ErrorTitle),
                d.K.Cmd(Loc.Get(Strings.Common.Retry), static () => Settings.RefreshStorage())) with { Key = "zs:usage" }
            : s is null
                ? new BoxEl
                {
                    Key = "zs:usage", Direction = 0, AlignItems = FlexAlign.Center, Gap = Z.ControlGap, MinHeight = Z.CensusMinHeight,
                    Children = [ProgressRing.Indeterminate(size: Z.SpinnerSize), ZuneText.Meta(Loc.Get(Strings.Settings.Storage.Reading))],
                }
                : UsageCard(s);
    }

    /// <summary>The census part a Storage folder dial stands for (its hue's index in `Settings.StorageRules.Parts` order), or null for another dial.</summary>
    static int? StoragePart(string id) => id switch
    {
        "storage.library" => 0,
        "storage.runtime" => 1,
        "storage.logs" => 2,
        "storage.localStore" => 3,
        "storage.cache.audioBodies" => 4,
        "storage.cache.licenseKeys" => 5,
        "storage.imageCache" => 6,
        "storage.aiModels" => 7,
        "storage.otherData" => 8,
        _ => null,
    };

    /// <summary>A census category's row: the part's fixed hue, its words (the census's count where it says more than the caption), its size (simply ABSENT until
    /// the census lands: never a dash, never 0 B), "open folder" (the dial's writer) and its clear or remove (the child dial, confirmed, keyed as its row).</summary>
    static Element CensusRow(Paint d, SettingsBodyRow row, int hue)
    {
        var k = d.K;
        var storage = d.Props.Storage;
        var s = storage.Snapshot;
        long? size = s is null ? null : hue switch
        {
            0 => s.LibraryDb, 1 => s.Runtime, 2 => s.Logs, 3 => s.Store, 4 => s.AudioBody, 5 => s.LicenseDb, 6 => s.ImageCache, 7 => s.AiModels, _ => s.Other,
        };
        string? sub = hue switch
        {
            2 => s is null ? Loc.Get(Strings.Settings.Storage.LogsSubEmpty) : s.LogFiles == 1 ? Loc.Get(Strings.Settings.Storage.LogsSubOne) : Strings.Settings.Storage.LogsSub(s.LogFiles),
            5 => storage.KeyCount is not { } keys ? Loc.Get(Strings.Settings.Storage.LicenseKeysSub)
                : keys == 1 ? Loc.Get(Strings.Settings.Storage.LicenseKeysCountOne) : Strings.Settings.Storage.LicenseKeysCount(keys),
            7 => s is null ? row.Description
                : s.AiModels <= 0 ? Loc.Get(Strings.Settings.Storage.AiModelsNone)
                : s.AiLanguages == 1 ? Loc.Get(Strings.Settings.Storage.AiModelsCountOne)
                : s.AiLanguages > 1 ? Strings.Settings.Storage.AiModelsCount(s.AiLanguages) : row.Description,
            _ => row.Description,
        };
        var controls = new List<Element>(4)
        {
            new BoxEl { Width = Z.HueSize, Height = Z.HueSize, Shrink = 0f, Fill = Z.StorageHues[hue] },
        };
        if (size is { } bytes) controls.Add(R.Type.DenseMeta(Settings.StorageFormat.Bytes(bytes)) with { Color = Tok.TextPrimary, Shrink = 0f });
        controls.Add(k.Cmd(Loc.Get(Strings.Settings.Storage.OpenFolder), RunOf(row)));
        foreach (var child in row.Children)
            if (child.Kind == SettingsRowKind.Action && child.Confirm is { } confirm)
                controls.Add(ConfirmCmd(k, child, Loc.Get(confirm.TitleKey)) with { Key = SettingsKeys.ZuneRow(child.Id), OnRealized = NoteRow(child.Id) });
        return DialRow(d, row, new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = Z.ControlGap, Wrap = true, MinWidth = 0f, Children = controls.ToArray() }, sub);
    }

    /// <summary>The cache location: the PATH is the description; "choose location" (the dial's two-dialog flow) and "use default" (its child) need the cache,
    /// "open folder" never does.</summary>
    static Element CacheLocationRow(Paint d, SettingsBodyRow row)
    {
        var k = d.K;
        var controls = new List<Element>(3) { k.Cmd(row.Label, RunOf(row), row.Enabled) };
        foreach (var child in row.Children)
            if (child.Kind == SettingsRowKind.Action)
                controls.Add(k.Cmd(child.Label, RunOf(child), child.Enabled) with { Key = SettingsKeys.ZuneRow(child.Id), OnRealized = NoteRow(child.Id) });
        controls.Add(k.Cmd(Loc.Get(Strings.Settings.Storage.OpenFolder), static () => SettingsVerbs.OpenStorageFolder("cacheLocation")));
        return DialRow(d, row, new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = Z.ControlGap, Wrap = true, MinWidth = 0f, Children = controls.ToArray() },
            d.Props.Storage.AudioDirectory, enabled: true, label: Loc.Get(Strings.Settings.Storage.CacheLocation));
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
    /// life, seeded from the dials and re-seeded on every page epoch. Keyed as the budget dial's row; the preset and the two numbers are its children's writers.</summary>
    sealed record BudgetArgs(SettingsBodyRow Row, Settings.StorageSnapshot? Snapshot, AudioBodyCacheStatus? Status, SettingsArrival Focus);

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
            var row = args.Row;
            _ = Settings.PageEpoch.Value;
            var budget = row.Dial;
            int mode = budget.Current is int m ? Math.Clamp(m, 0, 2) : 0;
            int preset = SettingsSpec.Dial(BudgetPresetDial).Current is int pr ? pr : 5;
            int percent = SettingsSpec.Dial(BudgetPercentDial).Current is int pc ? pc : 0;
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
                            if (SettingsSpec.Dial(BudgetPresetDial).Writer.Choose?.Invoke(i) == true) Settings.Bump();
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
            return k.Row(SettingsKeys.ZuneRow(row.Id), LabelOf(args.Focus, row.Id, ZuneText.Label(row.Label)), row.Description, new BoxEl
            {
                Direction = 1, Gap = Z.ControlGap, MinWidth = 0f, AlignItems = FlexAlign.Start,
                Children =
                [
                    k.Choice(row.Id, new ZuneChoice(row.Choices,
                        () => budget.Current is int at ? at : 0,
                        m2 => { if (budget.Writer.Choose?.Invoke(m2) == true) Settings.Bump(); }, Chips: true)),
                    editor,
                    ProgressBar.Determinate(fraction, width: Z.BudgetMeterWidth, state: over ? ProgressBarState.Error : ProgressBarState.Normal),
                    ZuneText.Hint(Strings.Settings.Storage.UsedOfBudget(Settings.StorageFormat.Bytes(used), budgetLabel)),
                    status is { Available: false }
                        ? ZuneText.Hint(Loc.Get(Strings.Settings.Storage.LocationUnavailable)) with { Color = Tok.SystemFillCritical }
                        : ZuneText.Hint(Strings.Settings.Storage.FreeReserve(Settings.StorageFormat.Bytes(status?.ReserveBytes ?? 0))),
                ],
            }, row.Enabled) with { OnRealized = NoteRow(row.Id) };
        }

        const string BudgetPresetDial = "storage.cache.budget.preset", BudgetPercentDial = "storage.cache.budget.percent";

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
