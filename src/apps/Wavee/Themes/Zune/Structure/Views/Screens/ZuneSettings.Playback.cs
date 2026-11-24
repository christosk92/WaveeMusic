// ── Themes/Zune/Structure/Views/Screens/ZuneSettings.Playback.cs — Playback's sections: the runtime and the attached videos (settings rework phase 4) ─────
//
// Board Z-Settings-Playback: the runtime (ready / not set up / problem) under its group's heading, and the attached videos with their manager. Between songs, the
// player bar, the episode speed and the video rows are generic rows of the body. The board's "downloads" rows are not built (owner Q29 / NQ4: no offline
// playback); the audio cache is Storage.

using System.Globalization;
using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;
using Z = Wavee.Themes.Zune.ZuneSettingsRules;

namespace Wavee.Themes.Zune;

internal static partial class ZuneSettings
{
    // ── the runtime ───────────────────────────────────────────────────────────────────────────────────────────────────────

    sealed record RuntimeArgs(SettingsBodyRow Row);

    /// <summary>Three shapes over `Setup.Runtime.Status`: never a skeleton, never blank. Ready (the facts and the doors), not set up (an informational bar with the
    /// setup action) and a problem (an error bar with the diagnostics doors and a retry). Nothing known yet and a runtime never set up are the same bar. Keyed as
    /// the runtime dial's row; its doors are the dials' writers (the setup, the signature, the playback runtime's and Spotify Connect's diagnostics).</summary>
    sealed class RuntimeBlock : ZuneScreenComponent
    {
        public override Element Render()
        {
            var k = new ZuneKit(UseLayout());
            var row = UseProps<RuntimeArgs>().Row;
            var facts = Setup.Runtime.Status.Value;
            Element Door(string text, string dialId) => k.Cmd(text, () => SettingsSpec.Dial(dialId).Writer.Run?.Invoke());
            Element body = Shape(k, facts, Door);
            return new BoxEl { Key = SettingsKeys.ZuneRow(row.Id), Direction = 1, AlignSelf = FlexAlign.Stretch, MinWidth = 0f, OnRealized = NoteRow(row.Id), Children = [body] };
        }

        static Element Shape(ZuneKit k, Setup.RuntimeFacts facts, Func<string, string, Element> Door)
        {

            if (facts.IsReady)
            {
                string version = facts.PackId is { Length: > 0 } pack ? facts.Version + " (" + pack + ")" : facts.Version ?? "";
                string signature = Setup.RuntimeSignatureSummary(facts);
                bool canView = facts.Signature is { FilePath: { Length: > 0 } path } && File.Exists(path);
                return k.Block("zs:runtime:ready", Loc.Get(Strings.Settings.Common.Ready), Loc.Get(Strings.Settings.Playback.RuntimeReadySub),
                [
                    k.Fact("zs:row:runtime-version", Loc.Get(Strings.Playback.Runtime.DetailVersion), version),
                    k.Fact("zs:row:runtime-arch", Loc.Get(Strings.Playback.Runtime.DetailArch), facts.Arch),
                    k.Row("zs:row:runtime-signature", Loc.Get(Strings.Playback.Runtime.DetailSignature), null,
                        new BoxEl
                        {
                            Direction = 0, AlignItems = FlexAlign.Center, Gap = Z.ControlGap, Wrap = true, MinWidth = 0f,
                            Children =
                            [
                                R.Type.DenseMeta(Z.OrDash(signature)) with { Color = Tok.TextPrimary, MinWidth = 0f, Wrap = TextWrap.Wrap },
                                canView ? Door(Loc.Get(Strings.Playback.Runtime.ViewSignature), RuntimeSignatureDial) : new BoxEl(),
                            ],
                        }),
                    k.Fact("zs:row:runtime-location", Loc.Get(Strings.Playback.Runtime.DetailLocation), facts.Location),
                    k.Row("zs:row:runtime-doors", Loc.Get("settings.playback.runtimeDiagnostics"), null,
                        new BoxEl
                        {
                            Direction = 0, AlignItems = FlexAlign.Center, Gap = Z.ControlGap, Wrap = true, MinWidth = 0f,
                            Children =
                            [
                                Door(Loc.Get(Strings.Playback.Runtime.ViewDiagnostics), RuntimeDiagnosticsDial),
                                Door(Loc.Get(Strings.Nav.ConnectDiagnostics), ConnectDiagnosticsDial),
                                Door(Loc.Get(Strings.Settings.Common.Manage), RuntimeDial),
                            ],
                        }),
                ]);
            }

            if (facts.Issue is Setup.RuntimeIssue.None or Setup.RuntimeIssue.Missing)
                return ZuneKit.Notice(InfoBarSeverity.Informational,
                    Loc.Get(Strings.Settings.Playback.RuntimeNotSetUp), Loc.Get(Strings.Settings.Playback.RuntimeNotSetUpSub),
                    k.Cmd(Loc.Get(Strings.Playback.Runtime.SetUp), static () => SettingsSpec.Dial(RuntimeDial).Writer.Run?.Invoke(), accent: true))
                    with { Key = "zs:runtime:setup" };

            // The typed issue's own sentence: the banner never shows one static line and drops the reason.
            return ZuneKit.Notice(InfoBarSeverity.Error,
                Loc.Get(Strings.Settings.Common.Problem), Loc.Get(Setup.RuntimeRules.BannerLocKey(facts.Issue)),
                new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Gap = Z.ControlGap, Wrap = true,
                    Children =
                    [
                        Door(Loc.Get(Strings.Playback.Runtime.ViewDiagnostics), RuntimeDiagnosticsDial),
                        Door(Loc.Get(Strings.Nav.ConnectDiagnostics), ConnectDiagnosticsDial),
                        k.Cmd(Loc.Get(Strings.Settings.Common.RetrySetup), static () => SettingsSpec.Dial(RuntimeDial).Writer.Run?.Invoke(), accent: true),
                    ],
                }) with { Key = "zs:runtime:problem" };
        }
    }

    const string RuntimeDial = "playback.runtime", RuntimeSignatureDial = "playback.runtime.signature", RuntimeDiagnosticsDial = "help.tools.playbackRuntime",
        ConnectDiagnosticsDial = "help.connectDiagnostics";

    // ── the attached videos ───────────────────────────────────────────────────────────────────────────────────────────────

    sealed record VideoArgs(SettingsBodyRow Row, SettingsArrival Focus);

    /// <summary>The attached-videos summary: no linked-video list store, the whole row greyed with no control; none attached, "no videos attached yet" and Manage; some, the
    /// count, Manage and Remove all. The linked videos is warm and synchronous, so there is no cold-load state, and the Manage wrapper never swaps out under an open flyout.
    /// "Remove all" is the dial's own confirmed writer; Manage (the manager flyout, keyed as the item dial's row) holds the per-item verbs.</summary>
    sealed class LinkedVideosRow : ZuneScreenComponent
    {
        NodeHandle _anchor = NodeHandle.Null;
        OverlayHandle? _handle;

        public override Element Render()
        {
            var k = new ZuneKit(UseLayout());
            var args = UseProps<VideoArgs>();
            var row = args.Row;
            string key = SettingsKeys.ZuneRow(row.Id);
            Element label = LabelOf(args.Focus, row.Id, ZuneText.Label(row.Label));
            UseEffect(() => (Action?)(() => { if (_handle is { IsOpen: true } open) open.Close(); _handle = null; }), DepKey.Empty);
            UseActivation(onDeactivated: () => { if (_handle is { IsOpen: true } open) open.Close(); _handle = null; });
            _ = Video.LinkedVideos.Epoch.Value;   // an attach or a remove anywhere (the track menu, an undo toast, the manager) re-reads
            string sub = row.Caption ?? Loc.Get(Strings.LinkedVideo.SettingsSub);   // the trust disclosure: device-wide, linked not copied
            if (!Video.LinkedVideos.Present)
            {
                _anchor = NodeHandle.Null;
                return k.Row(key, label, sub, null, enabled: false) with { OnRealized = NoteRow(row.Id) };
            }

            int count = Video.LinkedVideos.Count;
            var controls = new List<Element>(3)
            {
                ZuneText.Meta(count > 0 ? Strings.LinkedVideo.SettingsCount(count) : Loc.Get(Strings.LinkedVideo.SettingsEmpty)) with { Shrink = 0f },
                new BoxEl
                {
                    Key = SettingsKeys.ZuneRow(VideoItemDial), Direction = 0, Shrink = 0f,
                    OnRealized = h => { _anchor = h; s_rowNodes[VideoItemDial] = h; },   // the realized wrapper is the anchor
                    Children = [k.Cmd(Loc.Get(Strings.LinkedVideo.Manage), Toggle)],
                },
            };
            // Bulk detach is the ONE place a confirm earns its keep here: N links at once, no per-row undo.
            if (count > 0 && row.Confirm is not null) controls.Add(ConfirmCmd(k, row, Loc.Get(Strings.LinkedVideo.ClearAll)));
            return k.Row(key, label, sub,
                new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = Z.ControlGap, Wrap = true, MinWidth = 0f, Children = controls.ToArray() }) with { OnRealized = NoteRow(row.Id) };
        }

        /// <summary>"Manage" is a TOGGLE: a second press closes the flyout.</summary>
        void Toggle()
        {
            if (SettingsContext.Overlay is not { } overlay || !Video.LinkedVideos.Present) return;
            if (_handle is { IsOpen: true } open) { open.Close(); return; }
            var handle = overlay.Open(() => _anchor, static () => Embed.Comp(static () => new LinkedVideoManager()), FlyoutPlacement.BottomEdgeAlignedLeft,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Popup) { ConstrainToRootBounds = true });
            _handle = handle;
            handle.ClosedAction = () => { if (ReferenceEquals(_handle, handle)) _handle = null; };
        }
    }

    const string VideoItemDial = "playback.linkedVideos.item";

    /// <summary>The manager flyout's body: the whole linked-video list, each row with its repair verbs (replace, locate, show in explorer, remove). Presentational over
    /// `Video.LinkedVideoUx` (every decision) and `Video.Overrides` (the warm linked-video list); rebuilt only when the linked videos's epoch bumps.</summary>
    sealed class LinkedVideoManager : ZuneScreenComponent
    {
        int _epoch = -1;
        IReadOnlyList<Video.LinkedVideoRow> _rows = [];

        public override Element Render()
        {
            var k = new ZuneKit(UseLayout());
            int epoch = Video.LinkedVideos.Epoch.Value;
            if (epoch != _epoch)
            {
                _epoch = epoch;
                _rows = Video.LinkedVideoUx.BuildRoster(Video.LinkedVideos.All(), Video.LinkedVideos.DecideRecord, Video.LinkedVideos.DirectoryExists,
                    SettingsFacts.LinkedVideoTitle, SettingsFacts.LinkedVideoArtistLine);
            }
            Element list = _rows.Count == 0
                ? ZuneText.Prose(Loc.Get(Strings.LinkedVideo.SettingsEmpty))
                : new ScrollEl
                {
                    ContentSized = true, MaxHeight = Z.ManagerMaxHeight,
                    Content = new BoxEl { Direction = 1, Gap = Z.ManagerRowGap, Children = Rows(k) },
                };
            return new BoxEl
            {
                Key = "zs:vo-manager", Direction = 1, Width = Z.ManagerWidth, Gap = Z.ControlGap, Padding = Edges4.All(Spacing.M), ClipToBounds = true,
                Children =
                [
                    ZuneText.Group(Loc.Get(Strings.LinkedVideo.SettingsHeader)) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                    ZuneText.Meta(Strings.LinkedVideo.SettingsCount(_rows.Count)),
                    list,
                ],
            };
        }

        Element[] Rows(ZuneKit k)
        {
            var kids = new Element[_rows.Count];
            for (int i = 0; i < kids.Length; i++) kids[i] = Row(k, _rows[i]);
            return kids;
        }

        /// <summary>The full row: title and status, the FULL path (it answers "is this the file I meant?") and the repair verbs. Replace and remove apply at once with a
        /// toast-undo, never a dialog.</summary>
        static Element Row(ZuneKit k, Video.LinkedVideoRow row)
        {
            string sub = row.Subtitle is { Length: > 0 } artists ? artists + "  ·  " + row.Path : row.Path;
            string uri = row.Uri, path = row.Path, sourceKey = row.Video.SourceKey;
            var actions = new List<Element>(4)
            {
                k.Cmd(Loc.Get(Strings.LinkedVideo.Replace), () => SettingsVerbs.PickLinkedVideo(uri, sourceKey, Loc.Get(Strings.LinkedVideo.PickTitle), null)),
            };
            if (row.CanLocate)
                actions.Add(k.Cmd(Loc.Get(Strings.LinkedVideo.Locate), () =>
                {
                    string? start = Video.LinkedVideoUx.NearestExistingAncestor(path, Video.LinkedVideos.DirectoryExists);
                    SettingsVerbs.PickLinkedVideo(uri, sourceKey, Loc.Get(Strings.LinkedVideo.LocateTitle), start);
                }));
            if (row.CanReveal) actions.Add(k.Cmd(Loc.Get(Strings.LinkedVideo.ShowInExplorer), () => SettingsVerbs.RevealInExplorer(path)));
            actions.Add(k.Cmd(Loc.Get(Strings.LinkedVideo.Remove), () => SettingsVerbs.RemoveLinkedVideo(uri, path, sourceKey)));
            return new BoxEl
            {
                Key = "zs:vo-row:" + uri, Direction = 1, Gap = Z.ManagerRowGap, MinWidth = 0f, Padding = Edges4.All(Spacing.S), Fill = Tok.FillSubtleSecondary,
                Children =
                [
                    new BoxEl
                    {
                        Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, MinWidth = 0f,
                        Children =
                        [
                            R.Type.TrackTitle(row.Title) with { Grow = 1f, Basis = 0f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                            ZuneText.Meta(StatusWord(row.Status)) with { Shrink = 0f, Color = StatusInk(row.Status) },
                        ],
                    },
                    ZuneText.Hint(sub) with { MaxLines = 2, Trim = TextTrim.CharacterEllipsis },
                    new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, MinWidth = 0f, Wrap = true, Children = actions.ToArray() },
                ],
            };
        }

        static string StatusWord(Video.LinkedVideoStatus status) => Loc.Get(status switch
        {
            Video.LinkedVideoStatus.Missing => Strings.LinkedVideo.StatusMissing,
            Video.LinkedVideoStatus.DriveOffline => Strings.LinkedVideo.StatusDriveOffline,
            Video.LinkedVideoStatus.Unplayable => Strings.LinkedVideo.StatusUnplayable,
            _ => Strings.LinkedVideo.StatusOk,
        });

        /// <summary>Ok is deliberately quiet; the two repairable states are caution; only a file that failed to play is critical.</summary>
        static ColorF StatusInk(Video.LinkedVideoStatus status) => status switch
        {
            Video.LinkedVideoStatus.Missing or Video.LinkedVideoStatus.DriveOffline => Tok.SystemFillCaution,
            Video.LinkedVideoStatus.Unplayable => Tok.SystemFillCritical,
            _ => Tok.TextSecondary,
        };
    }
}
