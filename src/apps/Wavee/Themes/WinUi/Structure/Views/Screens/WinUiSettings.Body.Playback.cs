// ── Themes/WinUi/Structure/Views/Screens/WinUiSettings.Body.Playback.cs — Playback's WinUI cards: crossfade, speed, video, attached videos, the runtime ──────
//
// Crossfade is ONE expander (its switch in the header; the duration as a slider and a number box in lock-step through one commit, re-keyed on the switch so
// both follow it on the same frame). The episode speed is the speed menus' range on a slider. The two video combos word their heights. The attached videos
// row is the summary of the warm roster with its "Manage" flyout (the manager, ch 24 W20-W22: a ROOT of the newest few and a search, a LEAF of the whole roster
// with the repair verbs; presentational over `Video.OverrideUx` and `Video.Overrides`). The runtime card has three shapes over `Setup.Runtime.Status`: the
// ready expander with its facts, the Informational "not set up" bar, the Error bar with the diagnostics doors; never a skeleton, never blank.

using System.Globalization;
using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.WinUi;

internal static partial class WinUiSettings
{
    // ══ 1. CROSSFADE (W9) ═════════════════════════════════════════════════════════════════════════════════════════════

    static readonly Slider.SliderOptions s_crossSliderOptions = new()
    {
        Min = 0f, Max = (float)Settings.Quality.CrossfadeMaxSeconds, Step = 0.5f, TickFrequency = 2f, IsThumbToolTipEnabled = true,
        ThumbToolTipValueConverter = static v => v.ToString("0.#", CultureInfo.InvariantCulture) + " s",
    };
    static readonly NumberBox.NumberBoxOptions s_crossBoxOn = new()
    {
        Minimum = 0, Maximum = Settings.Quality.CrossfadeMaxSeconds, SmallChange = 0.5,
        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, Width = WinUiSettingsRules.CrossBoxWidth,
        Formatter = static v => v.ToString("0.#", CultureInfo.InvariantCulture) + " s",
    };
    static readonly NumberBox.NumberBoxOptions s_crossBoxOff = s_crossBoxOn with { IsEnabled = false };

    /// <summary>The crossfade expander: the switch in its header, the duration as its item (the child dial, hosted: its key, its badge, its arrival).</summary>
    static Element CrossfadeCard(RowCtx x)
    {
        var row = x.Row;
        bool crossOn = row.Value is true;
        var items = new List<Element>(1);
        if (Child(x, "playback.crossfade.duration") is { } duration)
        {
            string seconds = SettingsFacts.CrossfadeSeconds().ToString("0.#", CultureInfo.InvariantCulture);
            var control = Embed.Comp(new CrossfadeProps(duration.Dial, crossOn), static () => new CrossfadeDuration())
                with { Key = crossOn ? "crossfade-duration-on" : "crossfade-duration-off" };
            var card = ItemCard(duration.Label, Strings.Settings.Sound.Seconds(seconds), control, true) with { Explain = ExplainOf(duration) };
            items.Add(ChildHost(x, duration, DialCard(card)));
        }
        return Expander(BaseCard(x, DialToggle(row.Dial, row.Enabled)), items, x.Expanded) with { Key = "playback.crossfade" };
    }

    sealed record CrossfadeProps(SettingsDial Dial, bool On);

    /// <summary>The slider and the NumberBox in lock-step through ONE commit: <c>round(clamp(s, 0, 12) × 1000)</c> ms, mirrored into BOTH signals, then written
    /// through the duration dial (persisted and pushed only when the millisecond value moved: a drag reports every sample). Each control binds one signal for its life.</summary>
    sealed class CrossfadeDuration : SettingsComponent
    {
        public override Element Render()
        {
            UseLayout();
            var p = UseProps<CrossfadeProps>();
            double stored = SettingsFacts.CrossfadeSeconds();
            var seconds = UseSignal(stored);
            var slider = UseFloatSignal((float)stored);
            UseEffect(() =>
            {
                seconds.SetIfChanged(stored);
                slider.SetIfChanged((float)stored);
            }, DepKey.From((int)Math.Round(stored * 1000.0)));
            var dial = p.Dial;
            void Commit(double s)
            {
                double snapped = Settings.Quality.CrossfadeMs(s) / 1000.0;
                seconds.Value = snapped;
                slider.Value = (float)snapped;
                if (dial.Writer.Slide is { } slide && slide((float)s)) Settings.Bump();
            }
            return new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, Wrap = s_layout.StacksControls,
                Children =
                [
                    SliderControl(slider, v => Commit(v), s_crossSliderOptions, length: WinUiSettingsRules.CrossSliderLength, isEnabled: p.On),
                    NumberField(seconds, v => Commit(v), p.On ? s_crossBoxOn : s_crossBoxOff, "crossfade-seconds"),
                ],
            };
        }
    }

    // ══ 2. A SLIDER OVER A DIAL'S RANGE ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>A slider over a dial (the registry carries no range: each card names its own). Its own component: the slider binds one signal for its life,
    /// kept in step with the dial's value; a write goes through the dial's writer.</summary>
    static Element DialSlider(SettingsBodyRow row, Slider.SliderOptions options, float length)
        => Embed.Comp(new DialSliderProps(row.Dial, row.Value is float f ? f : options.Min, options, length, row.Enabled), static () => new DialSliderView())
            with { Key = "slider:" + row.Id };

    sealed record DialSliderProps(SettingsDial Dial, float Value, Slider.SliderOptions Options, float Length, bool Enabled);

    sealed class DialSliderView : SettingsComponent
    {
        public override Element Render()
        {
            UseLayout();
            var p = UseProps<DialSliderProps>();
            var value = UseFloatSignal(p.Value);
            float live = p.Value;
            UseEffect(() => value.SetIfChanged(live), DepKey.From(live));
            var dial = p.Dial;
            return SliderControl(value, v =>
            {
                if (dial.Writer.Slide is { } slide && slide(v)) Settings.Bump();
            }, p.Options, length: p.Length, isEnabled: p.Enabled);
        }
    }

    static readonly Slider.SliderOptions s_episodeSpeedOptions = new()
    {
        Min = WinUiSettingsRules.EpisodeSpeedMin, Max = WinUiSettingsRules.EpisodeSpeedMax, Step = WinUiSettingsRules.EpisodeSpeedStep,
        TickFrequency = WinUiSettingsRules.EpisodeSpeedStep, IsThumbToolTipEnabled = true,
        ThumbToolTipValueConverter = static v => v.ToString("0.##", CultureInfo.InvariantCulture) + "×",
    };

    /// <summary>The episode speed: the speed menus' rates (0.5x to 2x by quarters) on a slider; a song always plays at 1x.</summary>
    static Element EpisodeSpeedCard(RowCtx x)
        => DialCard(BaseCard(x, DialSlider(x.Row, s_episodeSpeedOptions, WinUiSettingsRules.SliderLength))
            with { Description = x.Row.Description ?? ((x.Row.Value is float rate ? rate : 1f).ToString("0.##", CultureInfo.InvariantCulture) + "×") });

    // ══ 3. VIDEO (W11) ════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The video quality: Auto, then the heights (stored as a height; the verb persists, then pins live: the ABR ceiling and the engine's selection).</summary>
    static Element VideoQualityCard(RowCtx x)
        => DialCard(BaseCard(x, DialCombo(x.Row, [Loc.Get(Strings.Settings.Playback.VideoQualityAuto), "180p", "240p", "320p", "480p", "720p", "1080p"],
            WinUiSettingsRules.WideComboWidth)));

    /// <summary>The metered video cap: Unlimited, 480p, 720p, 1080p (a stored height that matches nothing reads as 480p).</summary>
    static Element MeteredVideoCard(RowCtx x)
        => DialCard(BaseCard(x, DialCombo(x.Row, [Loc.Get(Strings.Settings.Playback.VideoQualityUnlimited), "480p", "720p", "1080p"], WinUiSettingsRules.WideComboWidth)));

    // ── the attached videos and their "Manage" flyout ────────────────────────────────────────────────────────────────

    // The flyout's handle and its anchor (a wrapper around the button — Button owns its own root props). ROOT-LIFETIME: they belong to the mounted root's tree
    // and overlay host.
    static readonly RootState<OverlayHandle?> s_voHandleState = new("WinUi.Settings.VideoOverrides.Handle");
    static readonly RootState<NodeHandle> s_voAnchorState = new("WinUi.Settings.VideoOverrides.Anchor");
    static OverlayHandle? s_voHandle { get => s_voHandleState.Value; set => s_voHandleState.Value = value; }
    static NodeHandle s_voAnchor { get => s_voAnchorState.Value; set => s_voAnchorState.Value = value; }

    /// <summary>Leaving Playback (another category, the results page, another page) closes the manager and drops its (about-to-be-destroyed) anchor.</summary>
    static void CloseVideoOverrideManager()
    {
        s_voAnchor = NodeHandle.Null;
        if (s_voHandle is { IsOpen: true } open) open.Close();
        s_voHandle = null;
    }

    /// <summary>The attached-videos SUMMARY (ch 27 W11's states): no roster store → the whole row greyed with NO control; 0 rows → "No videos attached yet" +
    /// Manage and no "Remove all"; N rows → the count + Manage + Remove all. The roster is warm and synchronous, so there is no cold-load spinner — and the
    /// Manage wrapper node never swaps out under an open flyout.</summary>
    static Element VideoOverridesCard(RowCtx x)
    {
        _ = Video.Overrides.Epoch.Value;   // an attach/remove anywhere (the track menu, an undo toast, the flyout) re-reads
        var dial = x.Row.Dial;
        var card = BaseCard(x, null) with { Label = Loc.Get(Strings.VideoOverride.SettingsHeader), Description = Loc.Get(Strings.VideoOverride.SettingsSub) };
        if (!Video.Overrides.Present)
        {
            s_voAnchor = NodeHandle.Null;
            return DialCard(card with { Enabled = false });
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
            OnRealized = static h => s_voAnchor = h,   // the realized wrapper is the anchor
            Children = [StdButton(Loc.Get(Strings.VideoOverride.Manage), static () => ToggleVideoOverrideManager())],
        };
        if (count > 0 && dial.Confirm is { } confirm)
            // Bulk detach is the ONE place a confirm earns its keep here: N links at once, no per-row undo (N11).
            kids[2] = StdButton(Loc.Get(Strings.VideoOverride.ClearAll), () =>
                ConfirmThen(Loc.Get(confirm.TitleKey), Loc.Get(confirm.BodyKey), Loc.Get(confirm.PrimaryKey), () => Press(dial)));
        else if (count > 0)
            kids[2] = StdButton(Loc.Get(Strings.VideoOverride.ClearAll), () => Press(dial));
        return DialCard(card with { Control = new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, Children = kids } });
    }

    /// <summary>"Manage" is a TOGGLE (35d): a second press closes the flyout.</summary>
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

    // ══ 4. THE RUNTIME CARD (W7 ready · W10 problem · the not-set-up bar) ═══════════════════════════════════════════

    /// <summary>Three rendered shapes over <see cref="Setup.Runtime.Status"/> — never a skeleton, never blank. Nothing known yet (<c>NotApplicable</c>) and a
    /// runtime that was never set up (<c>Missing</c>) are the Informational "Not set up" bar; the other issues are the Error bar with the diagnostics doors.</summary>
    static Element RuntimeCard(RowCtx x)
    {
        var facts = Setup.Runtime.Status.Value;
        var setup = x.Row.Dial;
        if (facts.IsReady)
        {
            var items = new List<Element>(5)
            {
                RuntimeFact(Loc.Get(Strings.Playback.Runtime.DetailVersion), facts.PackId is { Length: > 0 } pack ? facts.Version + " (" + pack + ")" : facts.Version),
                RuntimeFact(Loc.Get(Strings.Playback.Runtime.DetailArch), facts.Arch),
                HostedItem(x, "playback.runtime.signature", RuntimeSignatureItem(facts)),
                RuntimeFact(Loc.Get(Strings.Playback.Runtime.DetailLocation), facts.Location),
                // ch 27 §9.6: Connect diagnostics must not be reachable ONLY from a failure banner.
                PlainItem(Loc.Get("settings.playback.runtimeDiagnostics"), null, DiagnosticsLinks()),
            };
            var header = BaseCard(x, StdButton(Loc.Get(Strings.Settings.Common.Manage), () => Press(setup)))
                with { Label = Loc.Get(Strings.Settings.Common.Ready), Description = Loc.Get(Strings.Settings.Playback.RuntimeReadySub) };
            return Expander(header, items, x.Expanded) with { Key = "playback.runtime:ready" };
        }

        if (facts.Issue is Setup.RuntimeIssue.None or Setup.RuntimeIssue.Missing)
            return InfoBar.Create(InfoBarSeverity.Informational,
                Loc.Get(Strings.Settings.Playback.RuntimeNotSetUp), Loc.Get(Strings.Settings.Playback.RuntimeNotSetUpSub),
                isClosable: false,
                actionButton: AccentButton(Loc.Get(Strings.Playback.Runtime.SetUp), () => Press(setup)))
                with { Key = "playback.runtime:setup" };

        // The typed issue's own sentence — the banner used to show one static line and drop the reason.
        return InfoBar.Create(InfoBarSeverity.Error,
            Loc.Get(Strings.Settings.Common.Problem), Loc.Get(Setup.RuntimeRules.BannerLocKey(facts.Issue)),
            isClosable: false,
            actionButton: new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S,
                Children = [DiagnosticsLinks(), AccentButton(Loc.Get(Strings.Settings.Common.RetrySetup), () => Press(setup))],
            }) with { Key = "playback.runtime:problem" };
    }

    /// <summary>"View diagnostics" (the runtime report) + "Connect diagnostics" (who owns playback — orthogonal to whether local playback is provisioned, so it
    /// rides the same row in every state). The two doors are Help &amp; about's Diagnostics rows.</summary>
    static Element DiagnosticsLinks() => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S,
        Children =
        [
            LinkButton(Loc.Get(Strings.Playback.Runtime.ViewDiagnostics), static () => Press(Dial("help.tools.playbackRuntime"))),
            LinkButton(Loc.Get(Strings.Nav.ConnectDiagnostics), static () => Press(Dial("help.connectDiagnostics"))),
        ],
    };

    /// <summary>A fact the provisioner does not have is a GREYED row with no description, never a blank one (35c).</summary>
    static Element RuntimeFact(string label, string? value)
    {
        bool known = !string.IsNullOrWhiteSpace(value);
        return PlainItem(label, known ? value : null, isEnabled: known);
    }

    /// <summary>The signature summary, plus "View signature" ONLY while the signed file still exists on disk.</summary>
    static Element RuntimeSignatureItem(Setup.RuntimeFacts facts)
    {
        string summary = Setup.RuntimeSignatureSummary(facts);
        bool canView = facts.Signature is { FilePath: { Length: > 0 } path } && File.Exists(path);
        Element? link = canView
            ? LinkButton(Loc.Get(Strings.Playback.Runtime.ViewSignature), static () => Press(Dial("playback.runtime.signature")))
            : null;
        return PlainItem(Loc.Get(Strings.Playback.Runtime.DetailSignature), summary.Length > 0 ? summary : null, link);
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
