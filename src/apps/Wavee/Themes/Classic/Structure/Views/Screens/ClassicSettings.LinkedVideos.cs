// ── Themes/Classic/Structure/Views/Screens/ClassicSettings.LinkedVideos.cs — the attached-videos row and its "Manage" flyout (themes plan 6.1, T11b) ────
//
// Settings rework 7.1: the `playback.linkedVideos` dial's card in Playback › Videos and Canvas; "Manage" is its child dial (`playback.linkedVideos.item`).
//
// From `Screens\Settings.UI.Playback.cs` §7 (`LinkedVideosRow`, `ToggleLinkedVideoManager`) and `Screens\Settings.UI.Video.cs` (the flyout body, written by K in
// Wave 4, mounted on Playback in Wave 6; spec ch 24 §9).
//
// THE "MANAGE" FLYOUT (ch 24 W20-W22, §6). Presentational over `Video.LinkedVideoUx` (every decision) and `Video.Overrides` (the warm linked-video list): a ROOT (search box →
// the newest few + "Browse all…", or the live results) and a LEAF (the whole linked-video list with the full repair verbs), two views on ONE keyed child riding the page slide.
// The linked videos is rebuilt only when its epoch bumps (disk probes run once per load, never per keystroke). The search box stays MOUNTED across every section swap
// except Empty, so retyping never loses focus. The Playback category anchors it (BottomEdgeAlignedLeft, FocusTrap, LightDismiss, PopupChrome.Popup). The repair verbs
// (replace · locate · show in Explorer · remove) and the track-title adapters are the core's (`SettingsVerbs`, `SettingsFacts`).

using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

internal static partial class ClassicSettings
{
    /// <summary>The attached-videos SUMMARY (ch 27 W11's states): no linked-video list store → the whole row greyed with NO control;
    /// 0 rows → "No videos attached yet" + Manage and no "Remove all"; N rows → the count + Manage + Remove all. The linked videos
    /// is warm and synchronous (<see cref="Video.Overrides"/>), so there is no cold-load spinner to show — and the Manage
    /// wrapper node never swaps out under an open flyout.</summary>
    static Element LinkedVideosRow(SettingsBodyRow row)
    {
        _ = Video.LinkedVideos.Epoch.Value;   // an attach/remove anywhere (the track menu, an undo toast, the flyout) re-reads
        string header = row.Label;
        string sub = Loc.Get(Strings.LinkedVideo.SettingsSub);   // the trust disclosure: device-wide, linked not copied
        string? glyph = GlyphOf(row.Dial);
        if (!Video.LinkedVideos.Present)
        {
            s_voAnchor = NodeHandle.Null;
            return Row(header, sub, null, glyph, isEnabled: false);
        }

        int count = Video.LinkedVideos.Count;
        var kids = new Element[count > 0 ? 3 : 2];
        kids[0] = new TextEl(count > 0 ? Strings.LinkedVideo.SettingsCount(count) : Loc.Get(Strings.LinkedVideo.SettingsEmpty))
        {
            Size = ClassicSettingsRules.CaptionSize, Color = Tok.TextSecondary,
        };
        // "Manage" is the dial's child (the manager flyout), keyed as its row; the realized wrapper is the anchor.
        kids[1] = ChildFrame(row, "playback.linkedVideos.item", new BoxEl
        {
            Direction = 0, Shrink = 0f,
            OnRealized = static h => s_voAnchor = h,
            Children = [StdButton(Loc.Get(Strings.LinkedVideo.Manage), static () => ToggleLinkedVideoManager())],
        });
        if (count > 0)
            // Bulk detach is the ONE place a confirm earns its keep here: N links at once, no per-row undo (N11).
            kids[2] = StdButton(Loc.Get(Strings.LinkedVideo.ClearAll), static () =>
            {
                var clear = Dial("playback.linkedVideos");
                ConfirmThen(Loc.Get(clear.Confirm!.TitleKey), Loc.Get(clear.Confirm.BodyKey), Loc.Get(clear.Confirm.PrimaryKey),
                    () => clear.Writer.Run!());
            });

        return Row(header, sub, new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, Children = kids }, glyph);
    }

    /// <summary>"Manage" is a TOGGLE (35d): a second press closes the flyout. The body is owner K's.</summary>
    static void ToggleLinkedVideoManager()
    {
        if (SettingsContext.Overlay is not { } overlay || !Video.LinkedVideos.Present) return;
        if (s_voHandle is { IsOpen: true } open) { open.Close(); return; }
        var handle = overlay.Open(
            static () => s_voAnchor,
            static () => LinkedVideosBody(),
            FlyoutPlacement.BottomEdgeAlignedLeft,
            new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Popup)
            {
                ConstrainToRootBounds = true,
            });
        s_voHandle = handle;
        handle.ClosedAction = () => { if (ReferenceEquals(s_voHandle, handle)) s_voHandle = null; };
    }

    // MOUNT POINT (stage B contract)
    /// <summary>The video-override manager flyout body (420 wide).</summary>
    static Element LinkedVideosBody() => Embed.Comp(static () => new LinkedVideoManager());

    /// <summary>The manager component. Every helper is nested here so the other cards' helpers cannot collide with a name.</summary>
    sealed class LinkedVideoManager : Component
    {
        const float ManagerW = ClassicSettingsRules.ManagerWidth;
        readonly Signal<int> _view = new(0);                               // 0 root · 1 the browse-all leaf
        readonly Signal<string> _query = new("");
        readonly Signal<string?> _focus = new(null);                       // the row a recent-row tap drilled into
        bool _forward = true;
        int _rosterEpoch = -1;
        IReadOnlyList<Video.LinkedVideoRow> _rows = [];

        public override Element Render()
        {
            int epoch = Video.LinkedVideos.Epoch.Value;
            if (epoch != _rosterEpoch)
            {
                _rosterEpoch = epoch;
                _rows = Video.LinkedVideoUx.BuildRoster(Video.LinkedVideos.All(), Video.LinkedVideos.DecideRecord, Video.LinkedVideos.DirectoryExists,
                    SettingsFacts.LinkedVideoTitle, SettingsFacts.LinkedVideoArtistLine);
            }
            int view = _view.Value;
            string query = _query.Value;
            return new BoxEl
            {
                Direction = 1, Width = ManagerW, ClipToBounds = true, Padding = Edges4.All(Spacing.M),
                Children =
                [
                    new BoxEl
                    {
                        Key = view == 0 ? "vo-view:root" : "vo-view:all",
                        Animate = _forward ? MotionRecipes.PageSlideForward : MotionRecipes.PageSlideBack,
                        Direction = 1, MinWidth = 0f,
                        Children = [view == 0 ? Root(query) : Leaf()],
                    },
                ],
            };
        }

        Element Root(string query)
        {
            var hits = Video.LinkedVideoUx.Search(_rows, query);
            var section = Video.LinkedVideoUx.RootSection(_rows.Count, query, hits.Count);
            var kids = new List<Element>(6);
            if (section != Video.ManagerSection.Empty)
                kids.Add(Embed.Comp(() => new EditableText
                {
                    Placeholder = Loc.Get(Strings.LinkedVideo.SearchPlaceholder),
                    Width = ManagerW - 2f * Spacing.M, Height = Design.Size.ControlH, Text = _query,
                }) with { Key = "vo-search" });
            switch (section)
            {
                case Video.ManagerSection.Empty:
                    kids.Add(new BoxEl
                    {
                        Direction = 1, Gap = Spacing.XS, MinWidth = 0f, Padding = Edges4.All(Spacing.S),
                        Children =
                        [
                            BodyStrong(Loc.Get(Strings.LinkedVideo.SettingsEmpty)) with { Color = Tok.TextPrimary },
                            Caption(Loc.Get(Strings.LinkedVideo.SettingsEmptySub)) with { Color = Tok.TextSecondary, MinWidth = 0f, Wrap = TextWrap.Wrap, MaxLines = 4 },
                        ],
                    });
                    break;
                case Video.ManagerSection.NoMatches:
                    kids.Add(Hint(Loc.Get(Strings.LinkedVideo.NoMatches)));
                    break;
                case Video.ManagerSection.Results:
                    kids.Add(SectionLabel(Strings.LinkedVideo.MatchCount(hits.Count)));
                    kids.Add(RowList(hits, compact: false, ClassicSettingsRules.ManagerResultsMaxHeight));
                    break;
                default:
                    kids.Add(SectionLabel(Loc.Get(Strings.LinkedVideo.RecentlyAdded)));
                    kids.Add(RowList(Video.LinkedVideoUx.RecentlyAdded(_rows), compact: true, ClassicSettingsRules.ManagerRecentMaxHeight));
                    break;
            }
            if (Video.LinkedVideoUx.ShowsBrowseAll(_rows.Count, query))
            {
                kids.Add(new BoxEl { Height = 1f, AlignSelf = FlexAlign.Stretch, Fill = Tok.StrokeSurfaceDefault });
                kids.Add(new BoxEl
                {
                    Direction = 0, MinHeight = ClassicSettingsRules.ManagerBrowseRowHeight, AlignItems = FlexAlign.Center, Gap = Spacing.S,
                    Padding = new Edges4(Spacing.S, Spacing.XS, Spacing.S, Spacing.XS), Corners = Radii.ControlAll,
                    Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = () => Drill(null),
                    Children =
                    [
                        Icon(Icons.Folder, 16f, Tok.TextSecondary) with { Shrink = 0f },
                        Body(Loc.Get(Strings.LinkedVideo.BrowseAll)) with { Color = Tok.TextPrimary, Grow = 1f, Basis = 0f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                        Caption(Strings.LinkedVideo.SettingsCount(_rows.Count)) with { Color = Tok.TextSecondary, Shrink = 0f },
                        Icon(Icons.ChevronRight, 14f, Tok.TextSecondary) with { Shrink = 0f },
                    ],
                }.Interactive(Interaction.Subtle));
            }
            return new BoxEl { Direction = 1, Gap = Spacing.S, MinWidth = 0f, Children = kids.ToArray() };
        }

        Element Leaf() => new BoxEl
        {
            Direction = 1, Gap = Spacing.S, MinWidth = 0f,
            Children =
            [
                new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, MinHeight = ClassicSettingsRules.ManagerLeafHeaderHeight,
                    Children =
                    [
                        new BoxEl
                        {
                            Width = ClassicSettingsRules.ManagerBackSize, Height = ClassicSettingsRules.ManagerBackSize, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Corners = Radii.ControlAll,
                            Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
                            OnClick = () => { _forward = false; _focus.Value = null; _view.Value = 0; },
                            Children = [Icon(Icons.ChevronLeft, 14f, Tok.TextSecondary)],
                        }.Interactive(Interaction.Subtle),
                        BodyStrong(Loc.Get(Strings.LinkedVideo.SettingsHeader)) with { Color = Tok.TextPrimary, Grow = 1f, Basis = 0f, MinWidth = 0f, MaxLines = 1 },
                        Caption(Strings.LinkedVideo.SettingsCount(_rows.Count)) with { Color = Tok.TextSecondary, Shrink = 0f },
                    ],
                },
                RowList(_rows, compact: false, ClassicSettingsRules.ManagerLeafMaxHeight),
            ],
        };

        void Drill(string? uri)
        {
            _forward = true;
            _focus.Value = uri;
            _view.Value = 1;
        }

        Element RowList(IReadOnlyList<Video.LinkedVideoRow> rows, bool compact, float maxHeight)
        {
            if (rows.Count == 0) return Hint(Loc.Get(Strings.LinkedVideo.SettingsEmpty));
            string? focus = _focus.Value;
            var kids = new Element[rows.Count];
            for (int i = 0; i < rows.Count; i++) kids[i] = compact ? CompactRow(rows[i]) : FullRow(rows[i], focus);
            return new ScrollEl { ContentSized = true, MaxHeight = maxHeight, Content = new BoxEl { Direction = 1, Gap = compact ? ClassicSettingsRules.ManagerCompactGap : Spacing.XS, Children = kids } };
        }

        /// <summary>A glance row (title · file · status); a tap drills into the leaf with this row highlighted.</summary>
        Element CompactRow(Video.LinkedVideoRow row) => new BoxEl
        {
            Key = "vo-recent:" + row.Uri, Direction = 0, MinHeight = ClassicSettingsRules.ManagerCompactRowHeight, AlignItems = FlexAlign.Center, Gap = Spacing.S,
            Padding = new Edges4(Spacing.S, Spacing.XS, Spacing.S, Spacing.XS), Corners = Radii.ControlAll,
            Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = () => Drill(row.Uri),
            Children =
            [
                Icon(Icons.Movie, 16f, Tok.TextSecondary) with { Shrink = 0f },
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = 1f,
                    Children =
                    [
                        Body(row.Title) with { Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                        Caption(row.FileName) with { Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                    ],
                },
                StatusChip(row.Status),
            ],
        }.Interactive(Interaction.Subtle);

        /// <summary>The full row: title + status, the FULL path (it answers "is this the file I meant?") and the full-sentence
        /// repair verbs, wrapping rather than ellipsising.</summary>
        static Element FullRow(Video.LinkedVideoRow row, string? focus)
        {
            bool highlighted = focus is { Length: > 0 } f && string.Equals(f, row.Uri, StringComparison.Ordinal);
            string sub = row.Subtitle is { Length: > 0 } artists ? artists + "  ·  " + row.Path : row.Path;
            string uri = row.Uri, path = row.Path, sourceKey = row.Video.SourceKey;
            var actions = new List<Element>(4)
            {
                LinkButton(Loc.Get(Strings.LinkedVideo.Replace), () => Pick(uri, sourceKey, Loc.Get(Strings.LinkedVideo.PickTitle), null)),
            };
            if (row.CanLocate)
                actions.Add(LinkButton(Loc.Get(Strings.LinkedVideo.Locate), () =>
                {
                    string? start = Video.LinkedVideoUx.NearestExistingAncestor(path, Video.LinkedVideos.DirectoryExists);
                    Pick(uri, sourceKey, Loc.Get(Strings.LinkedVideo.LocateTitle), start);
                }));
            if (row.CanReveal)
                actions.Add(LinkButton(Loc.Get(Strings.LinkedVideo.ShowInExplorer), () => Reveal(path)));
            // Applies immediately with a toast-undo, never a dialog (the verb).
            actions.Add(StdButton(Loc.Get(Strings.LinkedVideo.Remove), () => SettingsVerbs.RemoveLinkedVideo(uri, path, sourceKey)));

            return new BoxEl
            {
                Key = "vo-row:" + uri, Direction = 1, Gap = Spacing.XS, MinWidth = 0f, Padding = Edges4.All(Spacing.S),
                Corners = Radii.ControlAll, Fill = highlighted ? Tok.AccentSubtle : Tok.FillSubtleSecondary,
                Children =
                [
                    new BoxEl
                    {
                        Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, MinWidth = 0f,
                        Children =
                        [
                            Body(row.Title) with { Color = Tok.TextPrimary, Weight = 600, Grow = 1f, Basis = 0f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                            StatusChip(row.Status),
                        ],
                    },
                    Caption(sub) with { Color = Tok.TextSecondary, MinWidth = 0f, MaxLines = 2, Wrap = TextWrap.Wrap, Trim = TextTrim.CharacterEllipsis },
                    new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, MinWidth = 0f, Wrap = true, Children = actions.ToArray() },
                ],
            };
        }

        /// <summary>Replace / Locate: pick, validate (a refusal is an ERROR toast + an always-on line), attach, success toast.
        /// Locate spells the deepest surviving folder into the dialog caption.</summary>
        static void Pick(string uri, string sourceKey, string title, string? start)
            => SettingsVerbs.PickLinkedVideo(uri, sourceKey, title, start);

        static void Reveal(string path) => SettingsVerbs.RevealInExplorer(path);

        /// <summary>Ok is deliberately QUIET; the two repairable states are caution; only a file that failed to play is critical.</summary>
        static Element StatusChip(Video.LinkedVideoStatus status)
        {
            (string text, ColorF fg, ColorF bg) = status switch
            {
                Video.LinkedVideoStatus.Missing => (Loc.Get(Strings.LinkedVideo.StatusMissing), Tok.SystemFillCaution, Tok.SystemFillCautionBackground),
                Video.LinkedVideoStatus.DriveOffline => (Loc.Get(Strings.LinkedVideo.StatusDriveOffline), Tok.SystemFillCaution, Tok.SystemFillCautionBackground),
                Video.LinkedVideoStatus.Unplayable => (Loc.Get(Strings.LinkedVideo.StatusUnplayable), Tok.SystemFillCritical, Tok.SystemFillCriticalBackground),
                _ => (Loc.Get(Strings.LinkedVideo.StatusOk), Tok.TextSecondary, Tok.FillSubtleSecondary),
            };
            return new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Shrink = 0f, Padding = new Edges4(8f, 3f, 8f, 3f), Corners = Radii.FullAll, Fill = bg,
                Children = [new TextEl(text) { Size = ClassicSettingsRules.CaptionSize, Weight = 600, Color = fg }],
            };
        }

        static Element Hint(string text) => new BoxEl
        {
            MinHeight = ClassicSettingsRules.ManagerCompactRowHeight, AlignItems = FlexAlign.Center, MinWidth = 0f, Padding = new Edges4(Spacing.S, 0f, Spacing.S, 0f),
            Children = [Body(text) with { Color = Tok.TextSecondary, MinWidth = 0f, Wrap = TextWrap.Wrap, MaxLines = 2 }],
        };

        static Element SectionLabel(string text) => Caption(text) with
        {
            Color = Tok.TextSecondary, Weight = 600, Margin = new Edges4(Spacing.XS, Spacing.XXS, 0f, 0f), MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
        };
    }
}
