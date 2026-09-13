using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>The "Manage" flyout behind Settings → Playback → Video overrides. Presentational only: the settings page
/// still owns the roster, the mutations and the status chip — this panel just decides WHAT to show, through the pure
/// <see cref="VideoOverrideUx"/> helpers (<c>RecentlyAdded</c> / <c>Search</c> / <c>RootSection</c>).
/// <para>Two views on one keyed child, riding the app's standing page transition exactly like
/// <see cref="ConcertDateFlyout"/>: the ROOT (search box → either the newest few attachments + a "Browse all…" drill,
/// or the live search results) and the LEAF (the full roster with the complete per-row action set). Drill-in slides
/// forward, Back mirrors.</para>
/// <para>Everything here is built ONCE per open (the lazy open-thunk convention) and re-built only when the roster
/// epoch bumps or the user types — never per frame.</para></summary>
sealed class VideoOverrideManagerFlyout : Component
{
    /// <summary>Live roster read. A thunk rather than a value because component props freeze at mount and the roster
    /// is rebuilt off-thread by the settings page; <see cref="Version"/> is what makes the re-read happen.</summary>
    public required Func<IReadOnlyList<VideoOverrideRow>> Rows;
    /// <summary>The settings page's roster epoch (bumped by the store's <c>video-overrides</c> sentinel and by every
    /// async rebuild). Read in <see cref="Render"/> → an attach/remove made anywhere refreshes this panel live.</summary>
    public required IReadSignal<int> Version;
    /// <summary>The page's per-row action set (Replace / Locate… / Show in Explorer / Remove), so the flyout and the
    /// track context menu can never drift apart.</summary>
    public required Func<VideoOverrideRow, Element> RowActions;
    /// <summary>The page's status chip.</summary>
    public required Func<VideoOverrideStatus, Element> StatusChip;

    const float PanelWidth = 420f;
    const float PanelPad = Spacing.M;

    readonly Signal<int> _view = new(0);        // 0 = root; 1 = the browse-all leaf
    readonly Signal<string> _query = new("");
    readonly Signal<string?> _focus = new(null);   // the uri a recent-row tap drilled into (leaf highlight)
    bool _forward = true;                        // last drill direction → picks the page-slide recipe

    public override Element Render()
    {
        _ = Version.Value;                       // subscribe → live refresh on the roster sentinel
        int view = _view.Value;
        string query = _query.Value;
        var all = Rows() ?? Array.Empty<VideoOverrideRow>();

        Element body = new BoxEl
        {
            Key = view == 0 ? "vo-view:root" : "vo-view:all",
            Animate = _forward ? MotionRecipes.PageSlideForward : MotionRecipes.PageSlideBack,
            Direction = 1, MinWidth = 0f,
            Children = [view == 0 ? BuildRoot(all, query) : BuildAll(all)],
        };
        return new BoxEl
        {
            Direction = 1, Width = PanelWidth, ClipToBounds = true,
            Padding = new Edges4(PanelPad, PanelPad, PanelPad, PanelPad),
            Children = [body],
        };
    }

    // ── root ─────────────────────────────────────────────────────────────────────────────────────────────────────────
    Element BuildRoot(IReadOnlyList<VideoOverrideRow> all, string query)
    {
        var hits = VideoOverrideUx.Search(all, query);
        var section = VideoOverrideUx.RootSection(all.Count, query, hits.Count);

        var kids = new List<Element>(6);
        // The search box stays MOUNTED across every section swap (same key, same slot) — retyping must never steal
        // focus or drop a keystroke because the list underneath changed shape.
        if (section != VideoManagerSection.Empty)
        {
            kids.Add(Embed.Comp(() => new EditableText
            {
                Placeholder = Loc.Get(Strings.VideoOverride.SearchPlaceholder),
                Width = PanelWidth - (2f * PanelPad),
                Height = WaveeSize.ControlH,
                Text = _query,
            }) with { Key = "vo-search" });
        }

        switch (section)
        {
            case VideoManagerSection.Empty:
                kids.Add(EmptyState());
                break;

            case VideoManagerSection.NoMatches:
                kids.Add(Hint(Loc.Get(Strings.VideoOverride.NoMatches)));
                break;

            case VideoManagerSection.Results:
                kids.Add(SectionLabel(Strings.VideoOverride.MatchCount(hits.Count)));
                kids.Add(RowList(hits, compact: false, maxHeight: 380f));
                break;

            default:
                kids.Add(SectionLabel(Loc.Get(Strings.VideoOverride.RecentlyAdded)));
                kids.Add(RowList(VideoOverrideUx.RecentlyAdded(all), compact: true, maxHeight: 280f));
                break;
        }

        if (VideoOverrideUx.ShowsBrowseAll(all.Count, query))
        {
            kids.Add(Divider());
            kids.Add(BrowseAllRow(all.Count));
        }

        return new BoxEl { Direction = 1, Gap = Spacing.S, MinWidth = 0f, Children = kids.ToArray() };
    }

    Element BrowseAllRow(int total) => new BoxEl
    {
        Direction = 0, MinHeight = 40f, AlignItems = FlexAlign.Center, Gap = Spacing.S,
        Padding = new Edges4(Spacing.S, Spacing.XS, Spacing.S, Spacing.XS),
        Corners = CornerRadius4.All(Radii.Control),
        Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
        OnClick = () => Drill(null),
        Children =
        [
            Icon(Icons.Folder, 16f, Tok.TextSecondary) with { Shrink = 0f },
            Body(Loc.Get(Strings.VideoOverride.BrowseAll)) with
            {
                Color = Tok.TextPrimary, Grow = 1f, Basis = 0f, MinWidth = 0f, MaxLines = 1,
                Trim = TextTrim.CharacterEllipsis,
            },
            Caption(Strings.VideoOverride.SettingsCount(total)) with { Color = Tok.TextSecondary, Shrink = 0f },
            Icon(Icons.ChevronRight, 14f, Tok.TextSecondary) with { Shrink = 0f },
        ],
    }.Interactive(Interaction.Subtle);

    // ── the browse-all leaf ──────────────────────────────────────────────────────────────────────────────────────────
    Element BuildAll(IReadOnlyList<VideoOverrideRow> all) => new BoxEl
    {
        Direction = 1, Gap = Spacing.S, MinWidth = 0f,
        Children =
        [
            new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, MinHeight = 36f,
                Children =
                [
                    BackButton(),
                    BodyStrong(Loc.Get(Strings.VideoOverride.SettingsHeader)) with
                    {
                        Color = Tok.TextPrimary, Grow = 1f, Basis = 0f, MinWidth = 0f, MaxLines = 1,
                    },
                    Caption(Strings.VideoOverride.SettingsCount(all.Count)) with
                    {
                        Color = Tok.TextSecondary, Shrink = 0f,
                    },
                ],
            },
            RowList(all, compact: false, maxHeight: 400f),
        ],
    };

    Element BackButton() => new BoxEl
    {
        Width = 28f, Height = 28f, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Corners = CornerRadius4.All(Radii.Control),
        Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
        OnClick = () => { _forward = false; _focus.Value = null; _view.Value = 0; },
        Children = [Icon(Icons.ChevronLeft, 14f, Tok.TextSecondary)],
    }.Interactive(Interaction.Subtle);

    void Drill(string? uri)
    {
        _forward = true;
        _focus.Value = uri;
        _view.Value = 1;
    }

    // ── rows ─────────────────────────────────────────────────────────────────────────────────────────────────────────
    Element RowList(IReadOnlyList<VideoOverrideRow> rows, bool compact, float maxHeight)
    {
        if (rows.Count == 0) return Hint(Loc.Get(Strings.VideoOverride.SettingsEmpty));
        string? focus = _focus.Value;
        var kids = new Element[rows.Count];
        for (int i = 0; i < rows.Count; i++)
            kids[i] = compact ? CompactRow(rows[i]) : FullRow(rows[i], focus);
        return new ScrollEl
        {
            ContentSized = true, MaxHeight = maxHeight,
            Content = new BoxEl { Direction = 1, Gap = compact ? 2f : Spacing.XS, Children = kids },
        };
    }

    /// <summary>The compact "Recently added" row: title + file name + status only. Tapping it drills into the leaf
    /// with the row highlighted — the repair verbs live there, so the recent list stays a glance surface.</summary>
    Element CompactRow(VideoOverrideRow row) => new BoxEl
    {
        Key = "vo-recent:" + row.Uri,
        Direction = 0, MinHeight = 44f, AlignItems = FlexAlign.Center, Gap = Spacing.S,
        Padding = new Edges4(Spacing.S, Spacing.XS, Spacing.S, Spacing.XS),
        Corners = CornerRadius4.All(Radii.Control),
        Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
        OnClick = () => Drill(row.Uri),
        Children =
        [
            Icon(Icons.Movie, 16f, Tok.TextSecondary) with { Shrink = 0f },
            new BoxEl
            {
                Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = 1f,
                Children =
                [
                    Body(row.Title) with { Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                    Caption(row.FileName) with
                    {
                        Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                    },
                ],
            },
            StatusChip(row.Status),
        ],
    }.Interactive(Interaction.Subtle);

    /// <summary>The full row: title + status, the FULL path (the thing that answers "is this the file I meant?"), and
    /// the complete action set. Used by the leaf and by the search results, so a repair never needs a drill.</summary>
    Element FullRow(VideoOverrideRow row, string? focus)
    {
        bool highlighted = focus is { Length: > 0 } f && string.Equals(f, row.Uri, StringComparison.Ordinal);
        string sub = row.Subtitle is { Length: > 0 } artists ? artists + "  ·  " + row.Path : row.Path;
        return new BoxEl
        {
            Key = "vo-row:" + row.Uri,
            Direction = 1, Gap = Spacing.XS, MinWidth = 0f,
            Padding = new Edges4(Spacing.S, Spacing.S, Spacing.S, Spacing.S),
            Corners = CornerRadius4.All(Radii.Control),
            Fill = highlighted ? Tok.AccentSubtle : Tok.FillSubtleSecondary,
            Children =
            [
                new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, MinWidth = 0f,
                    Children =
                    [
                        Body(row.Title) with
                        {
                            Color = Tok.TextPrimary, Weight = 600, Grow = 1f, Basis = 0f, MinWidth = 0f,
                            MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                        },
                        StatusChip(row.Status),
                    ],
                },
                Caption(sub) with
                {
                    Color = Tok.TextSecondary, MinWidth = 0f, MaxLines = 2, Wrap = TextWrap.Wrap,
                    Trim = TextTrim.CharacterEllipsis,
                },
                RowActions(row),
            ],
        };
    }

    // ── chrome ───────────────────────────────────────────────────────────────────────────────────────────────────────
    Element EmptyState() => new BoxEl
    {
        Direction = 1, Gap = Spacing.XS, MinWidth = 0f,
        Padding = new Edges4(Spacing.S, Spacing.S, Spacing.S, Spacing.S),
        Children =
        [
            BodyStrong(Loc.Get(Strings.VideoOverride.SettingsEmpty)) with { Color = Tok.TextPrimary },
            Caption(Loc.Get(Strings.VideoOverride.SettingsEmptySub)) with
            {
                Color = Tok.TextSecondary, MinWidth = 0f, Wrap = TextWrap.Wrap, MaxLines = 4,
            },
        ],
    };

    static Element Hint(string text) => new BoxEl
    {
        MinHeight = 44f, AlignItems = FlexAlign.Center, MinWidth = 0f,
        Padding = new Edges4(Spacing.S, 0f, Spacing.S, 0f),
        Children = [Body(text) with { Color = Tok.TextSecondary, MinWidth = 0f, Wrap = TextWrap.Wrap, MaxLines = 2 }],
    };

    static Element SectionLabel(string text) => Caption(text) with
    {
        Color = Tok.TextSecondary, Weight = 600,
        Margin = new Edges4(Spacing.XS, Spacing.XXS, 0f, 0f),
        MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
    };

    static Element Divider() => new BoxEl
    {
        AlignSelf = FlexAlign.Stretch,
        Children = [new BoxEl { Height = 1f, AlignSelf = FlexAlign.Stretch, Fill = Tok.StrokeSurfaceDefault }],
    };
}
