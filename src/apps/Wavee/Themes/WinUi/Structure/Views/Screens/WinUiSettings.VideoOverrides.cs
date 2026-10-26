// ── Themes/WinUi/Structure/Views/Screens/WinUiSettings.VideoOverrides.cs — the attached-videos row and its "Manage" flyout (themes plan 6.1, T11b) ────
//
// From `Screens\Settings.UI.Playback.cs` §7 (`VideoOverridesRow`, `ToggleVideoOverrideManager`) and `Screens\Settings.UI.Video.cs` (the flyout body, written by K in
// Wave 4, mounted by R's Playback tab in Wave 6; spec ch 24 §9).
//
// THE "MANAGE" FLYOUT (ch 24 W20-W22, §6). Presentational over `Video.OverrideUx` (every decision) and `Video.Overrides` (the warm roster): a ROOT (search box →
// the newest few + "Browse all…", or the live results) and a LEAF (the whole roster with the full repair verbs), two views on ONE keyed child riding the page slide.
// The roster is rebuilt only when its epoch bumps (disk probes run once per load, never per keystroke). The search box stays MOUNTED across every section swap
// except Empty, so retyping never loses focus. The Playback tab anchors it (BottomEdgeAlignedLeft, FocusTrap, LightDismiss, PopupChrome.Popup). The repair verbs
// (replace · locate · show in Explorer · remove) and the track-title adapters are the core's (`SettingsVerbs`, `SettingsFacts`).

using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;
using Tab = Wavee.Settings.Tab;

namespace Wavee.Themes.WinUi;

internal static partial class WinUiSettings
{
    /// <summary>The attached-videos SUMMARY (ch 27 W11's states): no roster store → the whole row greyed with NO control;
    /// 0 rows → "No videos attached yet" + Manage and no "Remove all"; N rows → the count + Manage + Remove all. The roster
    /// is warm and synchronous (<see cref="Video.Overrides"/>), so there is no cold-load spinner to show — and the Manage
    /// wrapper node never swaps out under an open flyout.</summary>
    static Element VideoOverridesRow()
    {
        _ = Video.Overrides.Epoch.Value;   // an attach/remove anywhere (the track menu, an undo toast, the flyout) re-reads
        string header = Loc.Get(Strings.VideoOverride.SettingsHeader);
        string sub = Loc.Get(Strings.VideoOverride.SettingsSub);   // the trust disclosure: device-wide, linked not copied
        string glyph = RowGlyph(Tab.Playback, "videoOverrides");
        if (!Video.Overrides.Present)
        {
            s_voAnchor = NodeHandle.Null;
            return Row(header, sub, null, glyph, isEnabled: false);
        }

        int count = Video.Overrides.Count;
        var kids = new Element[count > 0 ? 3 : 2];
        kids[0] = new TextEl(count > 0 ? Strings.VideoOverride.SettingsCount(count) : Loc.Get(Strings.VideoOverride.SettingsEmpty))
        {
            Size = WinUiSettingsRules.CaptionSize, Color = Tok.TextSecondary,
        };
        kids[1] = new BoxEl
        {
            Direction = 0, Shrink = 0f,
            // The realized wrapper is the anchor.
            OnRealized = static h => s_voAnchor = h,
            Children = [StdButton(Loc.Get(Strings.VideoOverride.Manage), static () => ToggleVideoOverrideManager())],
        };
        if (count > 0)
            // Bulk detach is the ONE place a confirm earns its keep here: N links at once, no per-row undo (N11).
            kids[2] = StdButton(Loc.Get(Strings.VideoOverride.ClearAll), static () =>
            {
                var clear = D(Tab.Playback, "videoOverrides");
                ConfirmThen(Loc.Get(clear.Confirm!.TitleKey), Loc.Get(clear.Confirm.BodyKey), Loc.Get(clear.Confirm.PrimaryKey),
                    () => clear.Writer.Run!());
            });

        return Row(header, sub, new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, Children = kids }, glyph);
    }

    /// <summary>"Manage" is a TOGGLE (35d): a second press closes the flyout. The body is owner K's.</summary>
    static void ToggleVideoOverrideManager()
    {
        if (SettingsContext.Overlay is not { } overlay || !Video.Overrides.Present) return;
        if (s_voHandle is { IsOpen: true } open) { open.Close(); return; }
        var handle = overlay.Open(
            static () => s_voAnchor,
            static () => VideoOverridesBody(),
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
    static Element VideoOverridesBody() => Embed.Comp(static () => new VideoOverrideManager());

    /// <summary>The manager component. Every helper is nested here so the other tabs' helpers cannot collide with a name.</summary>
    sealed class VideoOverrideManager : Component
    {
        const float ManagerW = WinUiSettingsRules.ManagerWidth;
        readonly Signal<int> _view = new(0);                               // 0 root · 1 the browse-all leaf
        readonly Signal<string> _query = new("");
        readonly Signal<string?> _focus = new(null);                       // the row a recent-row tap drilled into
        bool _forward = true;
        int _rosterEpoch = -1;
        IReadOnlyList<Video.OverrideRow> _rows = [];

        public override Element Render()
        {
            int epoch = Video.Overrides.Epoch.Value;
            if (epoch != _rosterEpoch)
            {
                _rosterEpoch = epoch;
                _rows = Video.OverrideUx.BuildRoster(Video.Overrides.All(), Video.Overrides.DecideRecord, Video.Overrides.DirectoryExists,
                    SettingsFacts.VideoOverrideTitle, SettingsFacts.VideoOverrideArtistLine);
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
            var hits = Video.OverrideUx.Search(_rows, query);
            var section = Video.OverrideUx.RootSection(_rows.Count, query, hits.Count);
            var kids = new List<Element>(6);
            if (section != Video.ManagerSection.Empty)
                kids.Add(Embed.Comp(() => new EditableText
                {
                    Placeholder = Loc.Get(Strings.VideoOverride.SearchPlaceholder),
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
                            BodyStrong(Loc.Get(Strings.VideoOverride.SettingsEmpty)) with { Color = Tok.TextPrimary },
                            Caption(Loc.Get(Strings.VideoOverride.SettingsEmptySub)) with { Color = Tok.TextSecondary, MinWidth = 0f, Wrap = TextWrap.Wrap, MaxLines = 4 },
                        ],
                    });
                    break;
                case Video.ManagerSection.NoMatches:
                    kids.Add(Hint(Loc.Get(Strings.VideoOverride.NoMatches)));
                    break;
                case Video.ManagerSection.Results:
                    kids.Add(SectionLabel(Strings.VideoOverride.MatchCount(hits.Count)));
                    kids.Add(RowList(hits, compact: false, WinUiSettingsRules.ManagerResultsMaxHeight));
                    break;
                default:
                    kids.Add(SectionLabel(Loc.Get(Strings.VideoOverride.RecentlyAdded)));
                    kids.Add(RowList(Video.OverrideUx.RecentlyAdded(_rows), compact: true, WinUiSettingsRules.ManagerRecentMaxHeight));
                    break;
            }
            if (Video.OverrideUx.ShowsBrowseAll(_rows.Count, query))
            {
                kids.Add(new BoxEl { Height = 1f, AlignSelf = FlexAlign.Stretch, Fill = Tok.StrokeSurfaceDefault });
                kids.Add(new BoxEl
                {
                    Direction = 0, MinHeight = WinUiSettingsRules.ManagerBrowseRowHeight, AlignItems = FlexAlign.Center, Gap = Spacing.S,
                    Padding = new Edges4(Spacing.S, Spacing.XS, Spacing.S, Spacing.XS), Corners = Radii.ControlAll,
                    Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = () => Drill(null),
                    Children =
                    [
                        Icon(Icons.Folder, 16f, Tok.TextSecondary) with { Shrink = 0f },
                        Body(Loc.Get(Strings.VideoOverride.BrowseAll)) with { Color = Tok.TextPrimary, Grow = 1f, Basis = 0f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                        Caption(Strings.VideoOverride.SettingsCount(_rows.Count)) with { Color = Tok.TextSecondary, Shrink = 0f },
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
                    Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, MinHeight = WinUiSettingsRules.ManagerLeafHeaderHeight,
                    Children =
                    [
                        new BoxEl
                        {
                            Width = WinUiSettingsRules.ManagerBackSize, Height = WinUiSettingsRules.ManagerBackSize, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Corners = Radii.ControlAll,
                            Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
                            OnClick = () => { _forward = false; _focus.Value = null; _view.Value = 0; },
                            Children = [Icon(Icons.ChevronLeft, 14f, Tok.TextSecondary)],
                        }.Interactive(Interaction.Subtle),
                        BodyStrong(Loc.Get(Strings.VideoOverride.SettingsHeader)) with { Color = Tok.TextPrimary, Grow = 1f, Basis = 0f, MinWidth = 0f, MaxLines = 1 },
                        Caption(Strings.VideoOverride.SettingsCount(_rows.Count)) with { Color = Tok.TextSecondary, Shrink = 0f },
                    ],
                },
                RowList(_rows, compact: false, WinUiSettingsRules.ManagerLeafMaxHeight),
            ],
        };

        void Drill(string? uri)
        {
            _forward = true;
            _focus.Value = uri;
            _view.Value = 1;
        }

        Element RowList(IReadOnlyList<Video.OverrideRow> rows, bool compact, float maxHeight)
        {
            if (rows.Count == 0) return Hint(Loc.Get(Strings.VideoOverride.SettingsEmpty));
            string? focus = _focus.Value;
            var kids = new Element[rows.Count];
            for (int i = 0; i < rows.Count; i++) kids[i] = compact ? CompactRow(rows[i]) : FullRow(rows[i], focus);
            return new ScrollEl { ContentSized = true, MaxHeight = maxHeight, Content = new BoxEl { Direction = 1, Gap = compact ? WinUiSettingsRules.ManagerCompactGap : Spacing.XS, Children = kids } };
        }

        /// <summary>A glance row (title · file · status); a tap drills into the leaf with this row highlighted.</summary>
        Element CompactRow(Video.OverrideRow row) => new BoxEl
        {
            Key = "vo-recent:" + row.Uri, Direction = 0, MinHeight = WinUiSettingsRules.ManagerCompactRowHeight, AlignItems = FlexAlign.Center, Gap = Spacing.S,
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
        static Element FullRow(Video.OverrideRow row, string? focus)
        {
            bool highlighted = focus is { Length: > 0 } f && string.Equals(f, row.Uri, StringComparison.Ordinal);
            string sub = row.Subtitle is { Length: > 0 } artists ? artists + "  ·  " + row.Path : row.Path;
            string uri = row.Uri, path = row.Path, sourceKey = row.Override.SourceKey;
            var actions = new List<Element>(4)
            {
                LinkButton(Loc.Get(Strings.VideoOverride.Replace), () => Pick(uri, sourceKey, Loc.Get(Strings.VideoOverride.PickTitle), null)),
            };
            if (row.CanLocate)
                actions.Add(LinkButton(Loc.Get(Strings.VideoOverride.Locate), () =>
                {
                    string? start = Video.OverrideUx.NearestExistingAncestor(path, Video.Overrides.DirectoryExists);
                    Pick(uri, sourceKey, Loc.Get(Strings.VideoOverride.LocateTitle), start);
                }));
            if (row.CanReveal)
                actions.Add(LinkButton(Loc.Get(Strings.VideoOverride.ShowInExplorer), () => Reveal(path)));
            // Applies immediately with a toast-undo, never a dialog (the verb).
            actions.Add(StdButton(Loc.Get(Strings.VideoOverride.Remove), () => SettingsVerbs.RemoveVideoOverride(uri, path, sourceKey)));

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
            => SettingsVerbs.PickVideoOverride(uri, sourceKey, title, start);

        static void Reveal(string path) => SettingsVerbs.RevealInExplorer(path);

        /// <summary>Ok is deliberately QUIET; the two repairable states are caution; only a file that failed to play is critical.</summary>
        static Element StatusChip(Video.OverrideStatus status)
        {
            (string text, ColorF fg, ColorF bg) = status switch
            {
                Video.OverrideStatus.Missing => (Loc.Get(Strings.VideoOverride.StatusMissing), Tok.SystemFillCaution, Tok.SystemFillCautionBackground),
                Video.OverrideStatus.DriveOffline => (Loc.Get(Strings.VideoOverride.StatusDriveOffline), Tok.SystemFillCaution, Tok.SystemFillCautionBackground),
                Video.OverrideStatus.Unplayable => (Loc.Get(Strings.VideoOverride.StatusUnplayable), Tok.SystemFillCritical, Tok.SystemFillCriticalBackground),
                _ => (Loc.Get(Strings.VideoOverride.StatusOk), Tok.TextSecondary, Tok.FillSubtleSecondary),
            };
            return new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Shrink = 0f, Padding = new Edges4(8f, 3f, 8f, 3f), Corners = Radii.FullAll, Fill = bg,
                Children = [new TextEl(text) { Size = WinUiSettingsRules.CaptionSize, Weight = 600, Color = fg }],
            };
        }

        static Element Hint(string text) => new BoxEl
        {
            MinHeight = WinUiSettingsRules.ManagerCompactRowHeight, AlignItems = FlexAlign.Center, MinWidth = 0f, Padding = new Edges4(Spacing.S, 0f, Spacing.S, 0f),
            Children = [Body(text) with { Color = Tok.TextSecondary, MinWidth = 0f, Wrap = TextWrap.Wrap, MaxLines = 2 }],
        };

        static Element SectionLabel(string text) => Caption(text) with
        {
            Color = Tok.TextSecondary, Weight = 600, Margin = new Edges4(Spacing.XS, Spacing.XXS, 0f, 0f), MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
        };
    }
}
