// ── Shell/Sidebar.Customizer.UI.cs ─────────────────────────────────────────────────────────────────────────────────
// the full-page sidebar-customize destination (4,247 lines of Curated/). Sequenced LAST in the wave
//
// Role: UI
// Owner: J
// Wave: 4
// Budget: 4500 lines
// Spec: ch 26 §9.4 · ch 26 §0-§6 (W1-W20) · ch 25 W9/W10 (the canvas this page drives)
//
// THE COMPANION PAGE. The docked Curated pane IS the canvas and IS the preview (J1's `PaneView`, armed while the route
// is `sidebar-customize`); this page mounts no copy of the layout and has no apply step. It keeps exactly the jobs a
// canvas cannot do for itself: the designs + the five templates, the persistent palette (Destinations first), the
// Hidden sections recovery list, Reset and the escape hatch to `sidebar-layout.json`. ONE scrolling column capped at
// 720 DIP, left-aligned, at every width — no tier, no breakpoint.
//
// THE RE-HOSTED PROPERTY SURFACE. `PropertyPanel(host, scrollKey)` + the `Cz*` rows + both pickers serve the pane's
// per-section options popover (host = `Sidebar.Edit`) without a fork: every row is CONTROLLED (it reads the document
// in render and mirrors it into its own signal from a LAYOUT effect whose dep folds `LayoutVersion·397 + RejectEpoch`),
// so a rejected edit visibly snaps back — and, new in 0.3, the popover SAYS why (`ISidebarEditHost.LastReject`).
//
// Every decision a body below would otherwise hand-roll is a tested pure rule in `Sidebar.Doc.cs`'s CUSTOMIZER PAGE
// RULES section (`SidebarRejectText`, `SidebarCustomizerBanners`, `SidebarPropertyRows`, `SidebarChoiceTreatment`,
// `SidebarConfigFieldRules`, `SidebarCustomizerPicks`, `SidebarMiniaturePlan`). Props freeze at mount: every block takes
// the reference-stable page (or host) and re-reads the service signals itself; rows are keyed by section/item id.
// INSIDE `Sidebar`, `Design` is the design SIGNAL — every token read is `global::Wavee.Design.*`.

using System.Globalization;
using System.Text.Json;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Input;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

public static partial class Sidebar
{
    // ══ 1. MOUNT POINTS ════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The `sidebar-customize` route's page (registered by I1 through <c>Shell.SetPage</c>). Reads only the
    /// static services; the route's 0.2.9 <c>?topbar</c> argument had no reader and is not carried.</summary>
    // MOUNT POINT (stage B contract)
    public static Element CustomizerPage()
    {
        // The palette chips' drag visual + captions ride the ONE resolver the shell mounts (Drag.Chip's extension point).
        Drag.ExtraChip ??= SectionChip;
        return Embed.Comp(static () => new CustomizerView());
    }

    /// <summary>The re-hosted property surface — J1's options popover (320×520) mounts it with <c>host = Sidebar.Edit</c>
    /// and scroll key <c>"sidebar.section.props"</c>, keyed per section. Width-agnostic: every right-hand control is
    /// <see cref="CzRow.ComboWidth"/>, sized for that 320 host.</summary>
    // MOUNT POINT (stage B contract, J1 ↔ J2)
    public static Element PropertyPanel(ISidebarEditHost host, string scrollKey)
        => Embed.Comp(() => new CzPropertyPanel(host, scrollKey));

    /// <summary>A palette chip mid-drag: the already-localized label composed at promotion, the kind's mark and the
    /// resting verb. The section-card REORDER band shares the kind but carries a non-palette payload, so it falls
    /// through to null and keeps its ghost lift. Runs inside the zero-alloc frame region: a lookup, never a build.</summary>
    static DragChipSpec? SectionChip(DragState state)
        => string.Equals(state.Kind, SidebarEditPlan.SectionDragKind, StringComparison.Ordinal)
           && state.Payload is SidebarSectionDropPayload section
            ? new DragChipSpec(Title: section.Label, Glyph: RowGlyphs.ForSectionKind(section.Kind), Count: 1,
                               RestingCaption: Loc.Get(PaneLoc.EditDropHere))
            : null;

    /// <summary>The template miniature's deterministic sample catalog (ch 26 §7 DATA GAPS: index-addressable, never the
    /// live library, or the dialog would gate on the network and flicker between opens). Owner Q's
    /// <c>Entities.SeedFake()</c> fills these in Wave 5; while null the miniature draws neutral placeholder art with the
    /// kind word and no counts.</summary>
    internal static Func<int, MiniatureSample>? MiniaturePlaylist { get; set; }
    internal static Func<int, MiniatureSample>? MiniatureArtist { get; set; }
    internal static Func<string, int?>? MiniatureShortcutCount { get; set; }

    internal readonly record struct MiniatureSample(string Name, string? ArtUrl, int Count);

    // ══ 2. THE PAGE ════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The page and its <see cref="ISidebarEditHost"/> (the host its palette adds and pickers dispatch
    /// through, with the inline reject strip). A KeepAlive destination: Done / Back / a tab switch PARK it, so the
    /// ergonomics reset rides the ROUTE edge (a signal effect) rather than an unmount that may never come, and what arms
    /// the canvas is J1's route check — never a flag this page would have to clear.</summary>
    internal sealed class CustomizerView : Component, ISidebarEditHost
    {
        const float HeaderHeight = 64f;
        const float ColumnMaxWidth = 720f;
        const float RejectDismissMs = 4000f;

        /// <summary>The host's subject. The page has no selection surface of its own; the canvas owns the working
        /// section (`Edit.Expanded` / `Edit.OptionsSection`) and <see cref="AppendTarget"/> reads those.</summary>
        internal readonly Signal<string?> Selected = new(null);
        internal readonly Signal<string?> SelectedItem = new(null);

        /// <summary>The palette's live search. Cleared after an accepted ADD and on entering/leaving the page; NOT on the
        /// contribution-pick mode switch, which honours the query.</summary>
        internal readonly Signal<string> PaletteQuery = new("");

        /// <summary>Bumped whenever <see cref="_rejectKey"/> changes — the strip's render dep and the timer's key.</summary>
        internal readonly Signal<int> RejectEpoch = new(0);

        readonly Signal<int> _bannerEpoch = new(0);
        bool _loadDismissed;
        SidebarPersistenceFault _dismissedSaveFault;
        string? _rejectKey;
        bool _routeActive;
        Action? _trackRoute;
        Action<KeyEventArgs>? _onKey;

        internal IOverlayService OverlaySvc = Overlay.Service.Default;

        Signal<int> ISidebarEditHost.RejectEpoch => RejectEpoch;
        Signal<string?> ISidebarEditHost.Selected => Selected;
        Signal<string?> ISidebarEditHost.SelectedItem => SelectedItem;
        SidebarRejectReason ISidebarEditHost.Dispatch(SidebarCommand command) => Send(command);
        SidebarRejectReason ISidebarEditHost.DispatchTopBar(SidebarCommand command) => Send(command, topBar: true);
        void ISidebarEditHost.Select(string? sectionId) => SelectSection(sectionId);
        string? ISidebarEditHost.LastReject => _rejectKey;

        public override Element Render()
        {
            OverlaySvc = UseContext(Overlay.Service) ?? Overlay.Service.Default;
            // Every accepted command bumps the version: undo/redo enablement, the eyebrow and the banners read the document.
            _ = LayoutVersion.Value;
            _ = _bannerEpoch.Value;
            var health = PersistenceHealth.Value;
            int rejectEpoch = RejectEpoch.Value;

            UseSignalEffect(_trackRoute ??= TrackRoute);
            // A true unmount (the page aged out of the keep-alive ring) resets too.
            UseEffect(() => (Action?)ResetSession, DepKey.Empty);
            // The inline strip auto-dismisses; a new rejection re-arms the 4 s.
            UseTimeout(ClearReject, RejectDismissMs, DepKey.From(rejectEpoch));

            return new BoxEl
            {
                Key = "customizer", Grow = 1f, Shrink = 1f, Direction = 1, MinWidth = 0f, MinHeight = 0f,
                ClipToBounds = true,
                OnKeyDown = _onKey ??= OnPageKey,
                Children =
                [
                    HeaderBar(in health), Divider(), Banners(in health), Body(),
                    // Ctrl+Z / Ctrl+Y / Ctrl+Shift+Z (ch 26 §6.1 gap, parity 88). Accelerators are window-global and a
                    // parked page's nodes stay registered, so each one re-checks the route before acting.
                    Accel(Keys.Z, KeyModifiers.Ctrl, UndoStep),
                    Accel(Keys.Y, KeyModifiers.Ctrl, RedoStep),
                    Accel(Keys.Z, KeyModifiers.Ctrl | KeyModifiers.Shift, RedoStep),
                ],
            };
        }

        BoxEl Accel(int key, KeyModifiers mods, Action run) => new()
        {
            Width = 0f, Height = 0f, Shrink = 0f, TabStop = false,
            Accelerator = new KeyAccelerator(key, mods),
            OnClick = () => { if (IsLive) run(); },
        };

        static bool IsLive => Shell.Current.Peek().Kind == Shell.RouteKind.SidebarCustomize;

        /// <summary>Esc leaves (the one exit, same as Back and Done) when nothing inside handled it first.</summary>
        void OnPageKey(KeyEventArgs e)
        {
            if (e.Handled || e.KeyCode != Keys.Escape || !IsLive) return;
            e.Handled = true;
            GoBack();
        }

        /// <summary>The route edge — entering or leaving resets the canvas ergonomics and the palette query, so a visit
        /// opens predictably. Never the document.</summary>
        void TrackRoute()
        {
            bool active = Shell.Current.Value.Kind == Shell.RouteKind.SidebarCustomize;
            if (active == _routeActive) return;
            _routeActive = active;
            ResetSession();
        }

        void ResetSession()
        {
            Edit.ResetErgonomics();
            PaletteQuery.SetIfChanged("");
        }

        // ── the header (Back · title lane · saved · Undo · Redo · Reset · Done) ──────────────────────────────────────

        /// <summary>Fixed: the title lane is the only flexible child (Grow 1 · Basis 0 · Shrink 1 · MinWidth 0), so under
        /// pressure the title ellipsizes and the command cluster holds its width.</summary>
        Element HeaderBar(in SidebarWriteResult health)
        {
            bool canUndo = CanUndo, canRedo = CanRedo;
            string undoTip = canUndo && UndoLabel is { Length: > 0 } u
                ? Loc.Format(CzLoc.UndoOf, ("action", Loc.Get(u)))
                : Loc.Get(CzLoc.Undo);
            string redoTip = canRedo && RedoLabel is { Length: > 0 } r
                ? Loc.Format(CzLoc.RedoOf, ("action", Loc.Get(r)))
                : Loc.Get(CzLoc.Redo);

            var kids = new List<Element>(4)
            {
                BackButton(),
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Basis = 0f, Shrink = 1f, MinWidth = 0f, Justify = FlexJustify.Center,
                    Children =
                    [
                        // The eyebrow names the ACTIVE template — the one fact "Customize sidebar" cannot carry.
                        global::Wavee.Design.Type.Eyebrow(Loc.Get(SidebarTemplates.NameLocKey(Layout.TemplateId))) with
                        {
                            Color = global::Wavee.Design.Accent.Decor, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                        },
                        new TextEl(Loc.Get(CzLoc.Title))
                        {
                            Size = 16f, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                        },
                    ],
                },
            };
            if (SidebarCustomizerBanners.ShowsSavedDot(in health)) kids.Add(SavedIndicator());
            kids.Add(new BoxEl
            {
                Direction = 0, Shrink = 0f, Gap = Spacing.XS, AlignItems = FlexAlign.Center,
                Children =
                [
                    ToolTip.Wrap(IconButton.Create(Icons.Undo, UndoStep, isEnabled: canUndo, size: ControlSize.Small)
                        with { Shrink = 0f }, undoTip),
                    ToolTip.Wrap(IconButton.Create(Icons.Redo, RedoStep, isEnabled: canRedo, size: ControlSize.Small)
                        with { Shrink = 0f }, redoTip),
                    Button.Create(Loc.Get(CzLoc.Reset), ConfirmReset, ButtonAppearance.Subtle, ControlSize.Small)
                        with { Shrink = 0f },
                    Button.Create(Loc.Get(CzLoc.Done), GoBack, ButtonAppearance.Accent, ControlSize.Small)
                        with { Shrink = 0f },
                ],
            });

            return new BoxEl
            {
                Key = "cmdbar", Direction = 0, Height = HeaderHeight, Shrink = 0f, Gap = Spacing.S,
                AlignItems = FlexAlign.Center, Padding = new Edges4(Spacing.S, 0f, Spacing.L, 0f),
                Children = [.. kids],
            };
        }

        /// <summary>The plain wrapper is LOAD-BEARING: <c>ToolTip</c>'s root sets <c>AlignSelf = Start</c>, which opts out
        /// of the header's centring and would pin the arrow to the top of the 64-DIP band.</summary>
        Element BackButton() => new BoxEl
        {
            Shrink = 0f,
            Children =
            [
                ToolTip.Wrap(IconButton.Create(Icons.Back, GoBack, size: ControlSize.Small) with { Shrink = 0f },
                    Loc.Get(CzLoc.Back)),
            ],
        };

        /// <summary>THE one exit, shared by Back, Done and Esc: flush the coalesced write, then the shell's real Back.
        /// A page with no back stack (a deep link, a restored tab) falls back to the newest non-customizer visit, and
        /// Home only as the last resort.</summary>
        internal void GoBack()
        {
            Flush();
            if (Shell.CanBack.Peek())
            {
                Shell.GoBack();
                return;
            }
            var log = Shell.History.Store.Entries;
            for (int i = log.Count - 1; i >= 0; i--)
            {
                var route = log[i].Route;
                if (route.Kind == Shell.RouteKind.SidebarCustomize) continue;
                Shell.GoTo(route);
                return;
            }
            Shell.GoTo(new Shell.Route(Shell.RouteKind.Home));
        }

        /// <summary>A 6-DIP success dot + "Saved locally", healthy only — the bars own every fault.</summary>
        static Element SavedIndicator() => new BoxEl
        {
            Direction = 0, Shrink = 0f, Gap = Spacing.XS, AlignItems = FlexAlign.Center,
            Margin = new Edges4(0f, 0f, Spacing.XS, 0f),
            Children =
            [
                new BoxEl
                {
                    Width = 6f, Height = 6f, Shrink = 0f, Corners = Radii.Circle(6f), Fill = Tok.SystemFillSuccess,
                    HitTestVisible = false,
                },
                new TextEl(Loc.Get(CzLoc.SavedLocally))
                {
                    Size = 11f, Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                },
            ],
        };

        // ── banners: the load fault, the save fault, the inline reject strip (stacked in that order) ───────────────

        Element Banners(in SidebarWriteResult health)
        {
            _dismissedSaveFault = SidebarCustomizerBanners.DismissedAfter(in health, _dismissedSaveFault);
            var kids = new List<Element>(3);

            var load = SidebarCustomizerBanners.LoadBanner(Fault, _loadDismissed);
            if (load != SidebarLoadBanner.None)
            {
                // 0.3 FIX (ch 26 W7): the PATH fills {path}; the store's diagnostic rides as a parenthetical.
                string path = Store.FilePath;
                bool tooNew = load == SidebarLoadBanner.TooNew;
                var actions = new List<Element>(2)
                {
                    Button.Create(Loc.Get(CzLoc.CopyPath), () => CopyPath(path), ButtonAppearance.Subtle, ControlSize.Small),
                };
                if (SidebarCustomizerBanners.OffersStartFresh(load))
                    actions.Add(Button.Create(Loc.Get(CzLoc.FaultDiscard), DiscardCorrupt, ButtonAppearance.Standard,
                        ControlSize.Small));
                kids.Add(InfoBar.Create(
                    InfoBarSeverity.Warning,
                    Loc.Get(tooNew ? CzLoc.TooNew : CzLoc.Corrupt),
                    Loc.Format(tooNew ? CzLoc.TooNewSub : CzLoc.CorruptSub, ("path", path))
                        + SidebarCustomizerBanners.DetailSuffix(FaultDetail),
                    onClose: () => { _loadDismissed = true; BumpBanner(); },
                    actionButton: new BoxEl
                    {
                        Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, Children = [.. actions],
                    }));
            }

            // The byte-budget / IO fault: writes silently stop while it stands, so this is the one place that tells the
            // user their edits are not reaching disk.
            if (SidebarCustomizerBanners.ShowsSaveFault(in health, _dismissedSaveFault))
            {
                var fault = health.Fault;
                kids.Add(InfoBar.Create(
                    InfoBarSeverity.Error,
                    Loc.Get(CzLoc.SaveFault),
                    Loc.Get(CzLoc.SaveFaultSub) + SidebarCustomizerBanners.DetailSuffix(health.SafeDetail),
                    onClose: () => { _dismissedSaveFault = fault; BumpBanner(); }));
            }

            if (_rejectKey is { Length: > 0 } key)
                kids.Add(InfoBar.Create(InfoBarSeverity.Informational, Loc.Get(key), "", onClose: ClearReject));

            if (kids.Count == 0) return new BoxEl { Key = "banners", Height = 0f, Shrink = 0f };
            return new BoxEl
            {
                Key = "banners", Direction = 1, Shrink = 0f, Gap = Spacing.XS,
                Padding = new Edges4(Spacing.L, Spacing.S, Spacing.L, 0f),
                Children = [.. kids],
            };
        }

        void BumpBanner() => _bannerEpoch.Value = _bannerEpoch.Peek() + 1;

        void DiscardCorrupt()
        {
            DiscardCorruptDocument();
            _loadDismissed = false;
            BumpBanner();
        }

        internal static void CopyPath(string path)
        {
            if (path.Length > 0) Actions.Services.Clipboard?.Invoke(path);
        }

        // ── the ONE column ────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Presets · palette · hidden · advanced, in one scroller; the 720 cap has no centring, so on a wide
        /// window the column hugs the content host's left edge.</summary>
        Element Body() => ScrollView(new BoxEl
        {
            Direction = 1, Gap = Spacing.L, MaxWidth = ColumnMaxWidth, MinWidth = 0f,
            Padding = new Edges4(Spacing.L, Spacing.M, Spacing.L, Spacing.XL),
            Children =
            [
                Embed.Comp(() => new CzPresetBlock(this)),
                Embed.Comp(() => new CzPalette(this)),
                Embed.Comp(() => new CzHiddenSections(this)),
                Embed.Comp(() => new CzAdvancedBlock(this)),
            ],
        }) with
        {
            Key = "customizer-column", Grow = 1f, Shrink = 1f, MinHeight = 0f, MinWidth = 0f,
            AutoEdgeFade = true, ScrollKey = "customizer.column",
        };

        // ── the ONE mutation path ─────────────────────────────────────────────────────────────────────────────────

        /// <summary><c>Sidebar.Dispatch</c> (reducer → undo → autosave) with the rejection surfaced inline, so a rejected
        /// command can never look like an applied one. <paramref name="topBar"/> picks the band's vocabulary only.</summary>
        internal SidebarRejectReason Send(SidebarCommand command, bool topBar = false)
        {
            var reason = Dispatch(command);
            if (reason == SidebarRejectReason.None)
            {
                if (_rejectKey is not null) ClearReject();
                return reason;
            }
            if (SidebarRejectText.LocKey(reason, topBar) is not { } key) return reason;
            _rejectKey = key;
            RejectEpoch.Value = RejectEpoch.Peek() + 1;
            return reason;
        }

        void ClearReject()
        {
            if (_rejectKey is null) return;
            _rejectKey = null;
            RejectEpoch.Value = RejectEpoch.Peek() + 1;
        }

        void UndoStep()
        {
            Undo();
            ClearReject();
        }

        void RedoStep()
        {
            Redo();
            ClearReject();
        }

        internal void SelectSection(string? sectionId)
        {
            Selected.SetIfChanged(sectionId);
            SelectedItem.SetIfChanged(null);
        }

        // ── section adds (the palette's verbs) ────────────────────────────────────────────────────────────────────

        static int TopLevelCount => Layout.Sections.Count;

        /// <summary>The StaticLinks section a destination click appends to (the popover's subject, else the expanded
        /// card). <paramref name="subscribe"/> is true only from a render.</summary>
        internal static SidebarSectionSpec? AppendTarget(bool subscribe)
            => SidebarCustomizerPicks.AppendTarget(Layout,
                subscribe ? Edit.OptionsSection.Value : Edit.OptionsSection.Peek(),
                subscribe ? Edit.Expanded.Value : Edit.Expanded.Peek());

        internal void AddSectionOfKind(SidebarSectionKind kind)
        {
            int at = TopLevelCount;
            if (Send(new AddSection(kind, at)) == SidebarRejectReason.None) AfterAdd(at);
        }

        /// <summary>A bare Links section opens the destination picker FIRST, so the gesture is still one AddSection and
        /// cancelling adds nothing rather than leaving a husk.</summary>
        internal void AddLinksSection()
            => CzPicker.OpenItem(OverlaySvc, item =>
            {
                int at = TopLevelCount;
                if (Send(new AddSection(SidebarSectionKind.StaticLinks, at, Item: item)) == SidebarRejectReason.None) AfterAdd(at);
            });

        /// <summary>One app page: appended into the StaticLinks subject through <c>SidebarItemCommands.Add</c> when there
        /// is one, else its own pre-seeded section — one command either way. No icon override: a route row resolves its
        /// glyph from the route table.</summary>
        internal void AddDestination(string routeKey)
        {
            if (routeKey.Length == 0) return;
            var item = new SidebarItemSpec(SidebarIds.NewItem(), SidebarItemTarget.Route, routeKey);
            if (AppendTarget(subscribe: false) is { } into)
            {
                if (Send(SidebarItemCommands.Add(into.Id, item, into.ItemList.Count)) == SidebarRejectReason.None) AfterAdd(-1);
                return;
            }
            int at = TopLevelCount;
            if (Send(new AddSection(SidebarSectionKind.StaticLinks, at, Item: item)) == SidebarRejectReason.None) AfterAdd(at);
        }

        internal void AddContribution(string contributionId)
        {
            if (contributionId.Length == 0) return;
            int at = TopLevelCount;
            var command = new AddSection(SidebarSectionKind.Extension, at, Extension: ContributionRef(contributionId));
            if (Send(command) == SidebarRejectReason.None) AfterAdd(at);
        }

        /// <summary>"Recently played": a JumpBackIn flipped to the play log — the one honest TWO-command gesture (AddSection
        /// seeds the kind's default display and carries no override).</summary>
        internal void AddRecentlyPlayed()
        {
            int at = TopLevelCount;
            if (Send(new AddSection(SidebarSectionKind.JumpBackIn, at)) != SidebarRejectReason.None) return;
            if (IdAt(at) is { } id)
                Send(new SetDisplayOption(id, SidebarDisplayField.RecentsSource, (int)SidebarRecentsSource.Played));
            AfterAdd(at);
        }

        /// <summary>The action picker first, then ONE <c>AddSection(StaticLinks, Item: the bound action)</c>.</summary>
        internal void AddActionShortcut()
            => CzPicker.OpenAction(OverlaySvc, null, binding =>
            {
                int at = TopLevelCount;
                var item = new SidebarItemSpec(SidebarIds.NewItem(), SidebarItemTarget.Action, binding.ActionKey,
                                               Action: binding);
                if (Send(new AddSection(SidebarSectionKind.StaticLinks, at, Item: item)) == SidebarRejectReason.None) AfterAdd(at);
            });

        internal void AddLikedSongsShortcut()
        {
            int at = TopLevelCount;
            if (Send(new AddSection(SidebarSectionKind.StaticLinks, at, Item: LikedSongsItem())) == SidebarRejectReason.None)
                AfterAdd(at);
        }

        internal static SidebarItemSpec LikedSongsItem()
            => new(SidebarIds.NewItem(), SidebarItemTarget.Route, "liked", IconOverride: "Heart");

        /// <summary>An accepted add clears the query and records the new section as this host's subject. It deliberately
        /// does NOT expand the card: the canvas owns <c>Expanded</c>; the new section in the live sidebar IS the feedback.</summary>
        void AfterAdd(int index)
        {
            PaletteQuery.SetIfChanged("");
            if (index >= 0 && IdAt(index) is { } id) SelectSection(id);
        }

        static string? IdAt(int index)
        {
            var sections = Layout.Sections;
            return (uint)index < (uint)sections.Count ? sections[index].Id : null;
        }

        /// <summary>A contributed section's ref seeded with its schema defaults — the same shape a click and a drop build.</summary>
        internal static SidebarExtensionRef ContributionRef(string contributionId)
        {
            var config = SidebarJson.EmptyObject;
            int schemaVersion = 1;
            if (Binder?.Sources is { } table && table.TryGet(contributionId, out var source))
            {
                config = SidebarConfigJson.Defaults(source.ConfigSchema);
                schemaVersion = source.ConfigSchema.Version;
            }
            return new SidebarExtensionRef(SidebarContributions.WaveeExtensionId, contributionId, schemaVersion, config);
        }

        // ── templates + reset ─────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The confirmation is SKIPPED when the document still equals a fresh build of its own template (modulo
        /// ids) — nothing to lose. Both paths are ONE command, so the dialog can honestly say "you can undo it".</summary>
        internal void ApplyTemplateWithConfirm(string templateId)
        {
            if (SidebarLayoutCompare.EqualTemplateSectionsIgnoringIds(Layout, SidebarTemplates.Build(Layout.TemplateId)))
            {
                ApplyTemplateNow(templateId);
                return;
            }
            string name = Loc.Get(SidebarTemplates.NameLocKey(templateId));
            ShowTemplateConfirmation(Loc.Format(CzLoc.ApplyTemplateTitle, ("template", name)),
                Loc.Get(CzLoc.ApplyTemplateBody), Loc.Get(CzLoc.ApplyTemplateConfirm),
                SidebarTemplates.Build(templateId), () => ApplyTemplateNow(templateId));
        }

        void ApplyTemplateNow(string templateId)
        {
            Send(new global::Wavee.ApplyTemplate(templateId));
            SelectSection(null);
        }

        internal void ConfirmReset()
        {
            var target = SidebarTemplates.Build(Layout.TemplateId);
            if (SidebarLayoutCompare.EqualTemplateSectionsIgnoringIds(Layout, target))
            {
                ResetNow();
                return;
            }
            string name = Loc.Get(SidebarTemplates.NameLocKey(Layout.TemplateId));
            ShowTemplateConfirmation(Loc.Get(CzLoc.ResetTitle), Loc.Format(CzLoc.ResetBody, ("template", name)),
                Loc.Get(CzLoc.ResetConfirm), target, ResetNow);
        }

        void ResetNow()
        {
            Send(new ResetLayout());
            SelectSection(null);
        }

        /// <summary>A RESTORATIVE confirm (ch 29 §6.5): 480 wide, the default button is the primary, and the body shows a
        /// real miniature of the target document rather than a sentence about it.</summary>
        void ShowTemplateConfirmation(string title, string body, string primary, SidebarCustomLayout target, Action confirm)
        {
            if (Controls.IsNullOverlay(OverlaySvc)) { confirm(); return; }
            ContentDialog.Show(OverlaySvc, dialog =>
            {
                dialog.Title = title;
                dialog.PrimaryText = primary;
                dialog.CloseText = Loc.Get(CzLoc.Cancel);
                dialog.DefaultButton = ContentDialog.DefaultBtn.Primary;
                dialog.DialogWidth = CzPicker.DialogW;
                dialog.Content = new BoxEl
                {
                    Direction = 1, Gap = Spacing.M, MinWidth = CzPicker.BodyW,
                    Children =
                    [
                        new TextEl(body) { Size = 13f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxLines = 3 },
                        CzMiniature.Template(target),
                    ],
                };
                dialog.PrimaryClick = confirm;
            });
        }
    }

    // ══ 3. THE PRESET BLOCK (designs + templates) ══════════════════════════════════════════════════════════════════════

    /// <summary>One decision beats an editor. The design segmented makes the menu's silent force-switch to Curated
    /// VISIBLE (Custom shows selected) and REVERSIBLE (another pick switches back through <c>SwitchDesign</c>, never a raw
    /// signal write, so per-design remembered state survives). It is the STOCK control — 34 tall, 14f, accent pill
    /// visible — on purpose; only the property surface's <see cref="CzRow.Choice"/> suppresses the pill (ch 26 §0.13).</summary>
    internal sealed class CzPresetBlock(CustomizerView page) : Component
    {
        public override Element Render()
        {
            int active = SidebarDesignGating.IndexOf(Sidebar.Design.Value);
            var index = UseSignal(active);
            // Controlled against the service: a switch from the pane's quick menu or Settings moves this control too.
            UseLayoutEffect(() => index.SetIfChanged(active), DepKey.From(active));

            bool showContents = Edit.ShowContents.Value;
            var contents = UseSignal(showContents);
            UseLayoutEffect(() => contents.SetIfChanged(showContents), DepKey.From(showContents));

            var designs = new SegmentedItem[SidebarDesignInfo.Count];
            for (int i = 0; i < designs.Length; i++)
                designs[i] = new SegmentedItem(Loc.Get(SidebarDesignGating.TitleKey(SidebarDesignInfo.FromInt(i))));

            Element[] rows =
            [
                // The group eyebrow already says "Sidebar design": the row's label is the consequence instead.
                CzRow.Wide(Loc.Get(CzLoc.DesignsSub), null, Segmented.Create(designs, index, OnDesign)),
                // ON reveals every visible section's body on the canvas and disarms section drag.
                CzRow.Prop(Loc.Get(CzLoc.ShowContents), null, ToggleSwitch.Create(contents, OnShowContents)),
            ];

            return new BoxEl
            {
                Direction = 1, Shrink = 0f, Gap = Spacing.M,
                Children = [CzRow.Group(CzLoc.Designs, rows), Embed.Comp(() => new CzTemplateList(page))],
            };
        }

        static void OnDesign(int value) => SwitchDesign(SidebarDesignGating.FromIndex(value));

        static void OnShowContents(bool on) => Edit.ShowContents.SetIfChanged(on);
    }

    /// <summary>The five template cards, radio semantics: the document's own <c>TemplateId</c> is the checked one.</summary>
    internal sealed class CzTemplateList(CustomizerView page) : Component
    {
        public override Element Render()
        {
            _ = LayoutVersion.Value;
            string active = Layout.TemplateId;
            var ids = SidebarTemplates.All;
            var cards = new Element[ids.Length];
            for (int i = 0; i < ids.Length; i++)
            {
                string id = ids[i];
                cards[i] = Card(id, string.Equals(id, active, StringComparison.Ordinal),
                    () => page.ApplyTemplateWithConfirm(id));
            }
            return CzRow.Group(CzLoc.Templates, cards);
        }

        /// <summary>The ramp is set EXPLICITLY (<c>.Interactive</c> would overwrite the active plate). The active card's
        /// pressed state is QUIETER than its rest so a press never reads as a deselect; the ✓ is the unambiguous mark.</summary>
        static Element Card(string templateId, bool active, Action onClick) => new BoxEl
        {
            Key = templateId,
            Direction = 0, Shrink = 0f, Gap = Spacing.S, AlignItems = FlexAlign.Center,
            Padding = new Edges4(Spacing.M, Spacing.S, Spacing.M, Spacing.S),
            Fill = active ? Tok.FillSubtleSecondary : ColorF.Transparent,
            HoverFill = active ? Tok.FillSubtleTertiary : Tok.FillSubtleSecondary,
            PressedFill = active ? Tok.FillSubtleSecondary : Tok.FillSubtleTertiary,
            Cursor = CursorId.Hand, Focusable = true, Role = AutomationRole.RadioButton, OnClick = onClick,
            Children =
            [
                Icon(active ? Icons.RadioBullet : Icons.Grid, 14f, active ? Tok.AccentDefault : Tok.TextSecondary),
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f,
                    Children =
                    [
                        new TextEl(Loc.Get(SidebarTemplates.NameLocKey(templateId)))
                        {
                            Size = 13f, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                        },
                        new TextEl(Loc.Get(SidebarTemplates.DescriptionLocKey(templateId)))
                        {
                            Size = 11f, Color = Tok.TextTertiary, MaxLines = 2, Wrap = TextWrap.Wrap,
                        },
                    ],
                },
                active
                    ? (Element)(Icon(Icons.Accept, 14f, Tok.AccentTextPrimary) with { Margin = new Edges4(0f, 0f, 2f, 0f) })
                    : new BoxEl { Width = 16f, Shrink = 0f },
            ],
        };
    }

    // ══ 4. THE PERSISTENT PALETTE ══════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Always visible; its only mode is the contribution pick (a LIST swap sliding ±8 DIP, not a second
    /// surface). The table and the token-wise filter are <c>SidebarPalette</c>'s; this resolves labels (a destination
    /// through the route table, the single owner of "what this page is called"), glyphs, and turns a click — or a drop on
    /// a canvas card — into the page's one dispatch.</summary>
    internal sealed class CzPalette : Component
    {
        readonly CustomizerView _page;
        readonly Signal<bool> _pickContribution = new(false);
        bool _forward = true;
        readonly List<SidebarPaletteEntry> _matches = new();

        static readonly Func<SidebarPaletteEntry, string> s_labelOf = CzText.PaletteLabel;
        static readonly Func<SidebarPaletteEntry, string?> s_descriptionOf = CzText.PaletteDescription;

        public CzPalette(CustomizerView page) => _page = page;

        public override Element Render()
        {
            string query = _page.PaletteQuery.Value;
            bool picking = _pickContribution.Value;
            _ = LayoutVersion.Value;
            // Read HERE so the Destinations header re-labels the moment the canvas's working card changes.
            var appendTo = CustomizerView.AppendTarget(subscribe: true);
            int used = Layout.SectionCount;

            var rows = new List<Element>(32);
            if (picking) AppendContributions(rows, query);
            else AppendGroups(rows, query, appendTo);

            return new BoxEl
            {
                Direction = 1, Shrink = 0f, Gap = Spacing.XS,
                Children =
                [
                    Head(used, used >= SidebarLayoutReducer.MaxSections, picking),
                    // UNCONDITIONAL, including in pick mode (defect 5); a controlled box, so clearing the query empties it.
                    TextBox.Create(_page.PaletteQuery, null, new TextBox.TextBoxOptions
                    {
                        Placeholder = Loc.Get(CzLoc.PaletteSearch), Height = 30f,
                    }),
                    new BoxEl
                    {
                        Key = picking ? "palette-card:contributions" : "palette-card:sections",
                        Animate = _forward ? MotionRecipes.PageSlideForward : MotionRecipes.PageSlideBack,
                        Direction = 1, Shrink = 0f, Corners = Radii.ControlAll,
                        Fill = Tok.FillCardSecondary, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
                        Padding = Edges4.All(Spacing.XS), ClipToBounds = true,
                        Children = [.. rows],
                    },
                ],
            };
        }

        /// <summary>The title (or the pick-mode back row) + the always-on section budget, red at the cap. The palette never
        /// disables its rows at 40/40: a click raises the inline "full" strip instead.</summary>
        Element Head(int used, bool full, bool picking) => new BoxEl
        {
            Direction = 0, Shrink = 0f, Gap = Spacing.S, AlignItems = FlexAlign.Center,
            Margin = new Edges4(Spacing.XS, 0f, Spacing.XS, 0f),
            Children =
            [
                picking ? BackRow() : CzRow.GroupLabel(Loc.Get(CzLoc.AddSection)) with { Grow = 1f, Shrink = 1f, MinWidth = 0f },
                new TextEl(Loc.Format(CzLoc.SectionCount, ("used", used), ("max", SidebarLayoutReducer.MaxSections)))
                {
                    Size = 11f, Weight = 600, Shrink = 0f, MaxLines = 1,
                    Color = full ? Tok.SystemFillCritical : Tok.TextTertiary,
                },
            ],
        };

        void AppendGroups(List<Element> into, string query, SidebarSectionSpec? appendTo)
        {
            SidebarPalette.Filter(query, s_labelOf, s_descriptionOf, _matches);
            var groups = SidebarPalette.Groups;
            for (int g = 0; g < groups.Length; g++)
            {
                var group = groups[g];
                int before = into.Count;
                for (int i = 0; i < _matches.Count; i++)
                {
                    var entry = _matches[i];
                    if (entry.Group != group) continue;
                    if (into.Count == before) into.Add(GroupHeader(group, appendTo));
                    into.Add(EntryRow(entry, appendTo));
                }
            }
            // Only a search can empty the static table, so the empty line names the RAW query (the user's casing).
            if (into.Count == 0 && query.Length > 0) into.Add(EmptyLine(query));
        }

        /// <summary>The Destinations header names where the next click lands while a StaticLinks card is the working
        /// section — the append rule is visible, never invisible state.</summary>
        static Element GroupHeader(SidebarPaletteGroup group, SidebarSectionSpec? appendTo)
        {
            string label = Loc.Get(SidebarPalette.GroupLocKey(group));
            if (group != SidebarPaletteGroup.Destinations || appendTo is null)
                return CzRow.GroupLabel(label) with { Margin = new Edges4(Spacing.XS, Spacing.S, Spacing.XS, 2f) };
            return new BoxEl
            {
                Direction = 0, Shrink = 0f, Gap = Spacing.S, AlignItems = FlexAlign.Center,
                Margin = new Edges4(Spacing.XS, Spacing.S, Spacing.XS, 2f),
                Children =
                [
                    CzRow.GroupLabel(label) with { Shrink = 0f },
                    new TextEl(Loc.Format(CzLoc.AppendsTo, ("name", CzText.TitleOf(appendTo))))
                    {
                        Size = 11f, Color = global::Wavee.Design.Accent.Decor, Grow = 1f, Shrink = 1f, MinWidth = 0f,
                        MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                    },
                ],
            };
        }

        Element EntryRow(SidebarPaletteEntry entry, SidebarSectionSpec? appendTo)
        {
            string label = CzText.PaletteLabel(entry);
            var page = _page;

            void Click()
            {
                switch (entry.Add)
                {
                    case SidebarPaletteAdd.Destination when entry.RouteKey is { Length: > 0 } route:
                        page.AddDestination(route);
                        break;
                    case SidebarPaletteAdd.LinksWithPicker:
                        page.AddLinksSection();
                        break;
                    case SidebarPaletteAdd.Contribution when entry.ContributionId is { Length: > 0 } id:
                        page.AddContribution(id);
                        break;
                    case SidebarPaletteAdd.RecentlyPlayed:
                        page.AddRecentlyPlayed();
                        break;
                    case SidebarPaletteAdd.ActionShortcut:
                        page.AddActionShortcut();
                        break;
                    case SidebarPaletteAdd.LikedSongsShortcut:
                        page.AddLikedSongsShortcut();
                        break;
                    case SidebarPaletteAdd.AnyContribution:
                        _forward = true;
                        _pickContribution.Value = true;
                        break;
                    default:
                        page.AddSectionOfKind(entry.Kind);
                        break;
                }
            }

            // Drag is one of several ways, never the only one: click-only rows (a picker opens, two commands, the mode
            // switch) carry no source rather than a drag that lies; an appending destination would mean two things.
            // The payload is built in the PROMOTION factory (once per gesture, a fresh item id each time), never per move.
            DragSource? drag = null;
            if (SidebarPalette.CanDrag(entry.Add) && !SidebarPalette.AppendsToSelection(entry, appendTo))
                drag = global::FluentGpu.Controls.Drag.Source(SidebarEditPlan.SectionDragKind,
                    () => DropPayload(entry, label),
                    thresholdMultiplier: global::FluentGpu.Controls.Drag.ClickPrimaryThresholdMultiplier);

            return Row(entry.Id, CzText.PaletteGlyph(entry), label, CzText.PaletteDescription(entry), Click, drag);
        }

        /// <summary>The whole AddSection argument list minus the index — identical to what a click adds.</summary>
        static SidebarSectionDropPayload? DropPayload(SidebarPaletteEntry entry, string label) => entry.Add switch
        {
            SidebarPaletteAdd.Destination when entry.RouteKey is { Length: > 0 } route =>
                new SidebarSectionDropPayload(SidebarSectionKind.StaticLinks, label,
                    Item: new SidebarItemSpec(SidebarIds.NewItem(), SidebarItemTarget.Route, route)),
            SidebarPaletteAdd.LikedSongsShortcut =>
                new SidebarSectionDropPayload(SidebarSectionKind.StaticLinks, label, Item: CustomizerView.LikedSongsItem()),
            SidebarPaletteAdd.Contribution when entry.ContributionId is { Length: > 0 } id =>
                new SidebarSectionDropPayload(SidebarSectionKind.Extension, label,
                    Extension: CustomizerView.ContributionRef(id)),
            SidebarPaletteAdd.Section => new SidebarSectionDropPayload(entry.Kind, label),
            _ => null,
        };

        /// <summary>The contribution pick: REGISTRATION order, the query still filters, and a raw id appears at most once
        /// — as the subtitle of a source this build has no name for.</summary>
        void AppendContributions(List<Element> into, string query)
        {
            var sources = Binder?.Sources?.Ordered;
            if (sources is null || sources.Count == 0)
            {
                into.Add(Note(Loc.Get(CzLoc.ExtensionManage)));
                return;
            }
            string q = SidebarPalette.NormalizeQuery(query);
            var page = _page;
            int shown = 0;
            for (int i = 0; i < sources.Count; i++)
            {
                string id = sources[i].Id;
                var named = SidebarPalette.EntryForContribution(id);
                string label = named is not null ? Loc.Get(named.NameLocKey) : SidebarContributions.ContributionOf(id);
                string sub = named is not null
                    ? Loc.Get(named.DescriptionLocKey)
                    : Loc.Format(CzLoc.ContributionUnnamed, ("id", id));
                if (!SidebarPalette.Matches(q, label, sub)) continue;
                shown++;
                string glyph = named is not null ? CzText.GlyphForName(named.IconName) : Icons.Code;
                into.Add(Row("contrib:" + id, glyph, label, sub, () =>
                {
                    _pickContribution.Value = false;
                    page.AddContribution(id);
                }, drag: null));
            }
            if (shown == 0 && q.Length > 0) into.Add(EmptyLine(query));
        }

        Element BackRow() => new BoxEl
        {
            Direction = 0, Height = 28f, Grow = 1f, Shrink = 1f, MinWidth = 0f,
            AlignItems = FlexAlign.Center, Gap = Spacing.XS,
            Corners = Radii.ControlAll, Cursor = CursorId.Hand, Focusable = true, Role = AutomationRole.Button,
            OnClick = () => { _forward = false; _pickContribution.Value = false; },
            Children =
            [
                Icon(Icons.ChevronLeft, 12f, Tok.TextSecondary),
                new TextEl(Loc.Get(CzLoc.AddSection)) { Size = 12f, Weight = 600, Color = Tok.TextSecondary, MaxLines = 1 },
            ],
        }.Interactive(Interaction.Subtle);

        static Element EmptyLine(string query) => Note(Loc.Format(CzLoc.PaletteEmpty, ("query", query)));

        static Element Note(string text) => new TextEl(text)
        {
            Size = 12f, Color = Tok.TextTertiary, Wrap = TextWrap.Wrap, MaxLines = 3,
            Margin = new Edges4(Spacing.XS, Spacing.S, Spacing.XS, Spacing.S),
        };

        /// <summary>One row: the DragSource sits on the CLICK-OWNING node, so one node owns hit-test, the pressed visual,
        /// the drag arm and the click and the arm's walk-up has nothing to disambiguate.</summary>
        static Element Row(string key, string glyph, string label, string? sub, Action onClick, DragSource? drag)
        {
            Element[] lines = sub is { Length: > 0 }
                ?
                [
                    new TextEl(label) { Size = 13f, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                    new TextEl(sub) { Size = 11f, Color = Tok.TextTertiary, MaxLines = 2, Wrap = TextWrap.Wrap },
                ]
                : [new TextEl(label) { Size = 13f, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis }];

            return new BoxEl
            {
                Key = key,
                Direction = 0, Shrink = 0f, Gap = Spacing.S, AlignItems = FlexAlign.Center,
                Padding = new Edges4(Spacing.XS, 6f, Spacing.XS, 6f), Corners = Radii.ControlAll,
                Cursor = CursorId.Hand, Focusable = true, Role = AutomationRole.Button, OnClick = onClick,
                Draggable = drag,
                Children =
                [
                    new BoxEl
                    {
                        Width = 24f, Height = 24f, Shrink = 0f, Corners = Radii.ControlAll, Fill = Tok.FillSubtleSecondary,
                        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, HitTestVisible = false,
                        Children = [Icon(glyph, 13f, Tok.TextSecondary)],
                    },
                    new BoxEl { Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, Children = lines },
                    AddChip(),
                ],
            }.Interactive(Interaction.ListRow);
        }

        /// <summary>The trailing "+" is an AFFORDANCE, not a second button: hit-test transparent, it only brightens on ROW
        /// hover — the engine cascades a container's hover to a descendant only for an opacity/scale reveal.</summary>
        static Element AddChip() => new BoxEl
        {
            Width = 24f, Height = 24f, Shrink = 0f, Corners = Radii.FullAll, Fill = Tok.FillSubtleSecondary,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, HitTestVisible = false,
            Opacity = 0.45f, HoverOpacity = 1f, HoverDurationMs = Motion.ControlFaster,
            Children = [Icon(Icons.Add, 12f, Tok.TextSecondary)],
        };
    }

    // ══ 5. HIDDEN SECTIONS + ADVANCED ══════════════════════════════════════════════════════════════════════════════════

    /// <summary>Nothing vanishes into an invisible elsewhere: every hidden section, top-level and children, one Show each
    /// — including a kind this build does not understand, which is still the user's.</summary>
    internal sealed class CzHiddenSections(CustomizerView page) : Component
    {
        readonly List<SidebarSectionSpec> _hidden = new();

        public override Element Render()
        {
            _ = LayoutVersion.Value;
            _ = page.RejectEpoch.Value;
            _hidden.Clear();
            SidebarCustomizerPicks.HiddenSections(Layout.Sections, _hidden);

            var rows = new Element[Math.Max(1, _hidden.Count)];
            if (_hidden.Count == 0) rows[0] = CzRow.Prop(Loc.Get(CzLoc.HiddenNone), null, null, enabled: false);
            for (int i = 0; i < _hidden.Count; i++) rows[i] = Row(_hidden[i]);
            return CzRow.Group(CzLoc.HiddenSections, rows, caption: Loc.Get(CzLoc.HiddenSectionsSub));
        }

        Element Row(SidebarSectionSpec section)
        {
            string id = section.Id;
            string title = CzText.TitleOf(section);
            if (title.Length == 0) title = Loc.Get(CzLoc.UnknownSection);
            return CzRow.Prop(title, Loc.Get(CzLoc.Hidden),
                Button.Create(Loc.Get(CzLoc.Show), () => page.Send(new SetSectionHidden(id, false)),
                    ButtonAppearance.Standard, ControlSize.Small),
                icon: RowGlyphs.ForSectionKind(section.Kind));
        }
    }

    /// <summary>Reset, and the documented escape hatch: where <c>sidebar-layout.json</c> lives. Both affordances, because
    /// they fail differently — Explorer can be unavailable, the clipboard cannot.</summary>
    internal sealed class CzAdvancedBlock(CustomizerView page) : Component
    {
        public override Element Render()
        {
            _ = LayoutVersion.Value;
            string template = Loc.Get(SidebarTemplates.NameLocKey(Layout.TemplateId));
            string path = Store.FilePath;

            return CzRow.Group(CzLoc.Advanced,
            [
                CzRow.Prop(Loc.Get(CzLoc.Reset), Loc.Format(CzLoc.ResetBody, ("template", template)),
                    Button.Create(Loc.Get(CzLoc.Reset), page.ConfirmReset, ButtonAppearance.Standard, ControlSize.Small)),
                CzRow.Wide(Loc.Get(CzLoc.LayoutFile), Loc.Get(CzLoc.LayoutFileSub), new BoxEl
                {
                    Direction = 1, Gap = Spacing.S, Grow = 1f, Shrink = 1f, MinWidth = 0f,
                    Children =
                    [
                        new TextEl(path)
                        {
                            Size = 11f, Color = Tok.TextTertiary, MaxLines = 2, Wrap = TextWrap.Wrap,
                            Trim = TextTrim.CharacterEllipsis,
                        },
                        new BoxEl
                        {
                            Direction = 0, Gap = Spacing.S, Shrink = 0f,
                            Children =
                            [
                                Button.Create(Loc.Get(CzLoc.ShowFile), () => RevealInExplorer(path),
                                    ButtonAppearance.Standard, ControlSize.Small),
                                Button.Create(Loc.Get(CzLoc.CopyPath), () => CustomizerView.CopyPath(path),
                                    ButtonAppearance.Subtle, ControlSize.Small),
                            ],
                        },
                    ],
                }),
            ]);
        }

        static void RevealInExplorer(string path)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"")
                {
                    UseShellExecute = true,
                });
            }
            catch (Exception ex) { Log.Warn("sidebar", "customizer.layoutFile.reveal failed", ex); }
        }
    }

    // ══ 6. THE ROW VOCABULARY ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>An icon button whose <c>MenuFlyout</c> is built at OPEN time (resolving labels in a render would subscribe
    /// the row to the culture epoch).</summary>
    internal sealed class CzMenuButton(string glyph, Func<IReadOnlyList<MenuFlyoutItem>> items, float box = 24f) : Component
    {
        public override Element Render()
        {
            var anchor = UseRef<NodeHandle>(default);
            var handle = UseRef<OverlayHandle?>(null);
            var svc = UseContext(Overlay.Service);

            void Toggle()
            {
                if (Controls.IsNullOverlay(svc)) return;
                if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
                var rows = items();
                if (rows.Count == 0) return;
                handle.Value = svc.Open(
                    () => anchor.Value,
                    () => MenuFlyout.Create(rows, () => handle.Value?.Close()),
                    FlyoutPlacement.BottomEdgeAlignedRight,
                    new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Popup)
                    { ConstrainToRootBounds = false });
                handle.Value.ClosedAction = () => handle.Value = null;
            }

            return new BoxEl
            {
                Width = box, Height = box, Shrink = 0f,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Corners = Radii.ControlAll,
                Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
                OnRealized = h => anchor.Value = h,
                OnClick = Toggle,
                Children = [Icon(glyph, 14f, Tok.TextSecondary)],
            }.Interactive(Interaction.Subtle);
        }
    }

    /// <summary>The property rows — hand-rolled, NOT <c>SettingsCard</c> (whose header lane has no line cap and jammed the
    /// switch in a 320 column). ONE two-column contract: label Grow 1 · MinWidth 0 · ≤2 lines · ellipsis; control Shrink 0
    /// right-aligned; the same 10-DIP vertical padding and 44-DIP floor on every row.</summary>
    internal static class CzRow
    {
        const float RowPadY = 10f;
        const float RowMinHeight = 44f;

        /// <summary>One width for every dropdown / number / text box, so the right edge never stair-steps
        /// (320 host − 2 border − 16 inset − 24 row padding = 278).</summary>
        public const float ComboWidth = 264f;

        /// <summary>The Maximum-items slider's track (a LENGTH, not a stretch).</summary>
        public const float SliderLength = 272f;

        public static TextEl GroupLabel(string text) => global::Wavee.Design.Type.Eyebrow(text) with
        {
            Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
        };

        /// <summary>One always-open group: the eyebrow (plus an optional trailing caption — the item count rides HERE,
        /// never as a body row) above ONE hairline card of rows.</summary>
        public static Element Group(string labelKey, IReadOnlyList<Element> items, string? caption = null) => new BoxEl
        {
            Direction = 1, Shrink = 0f, Gap = Spacing.XS,
            Children =
            [
                new BoxEl
                {
                    Direction = 0, Shrink = 0f, Gap = Spacing.S, AlignItems = FlexAlign.Center,
                    Margin = new Edges4(Spacing.XS, Spacing.S, Spacing.XS, 0f),
                    Children =
                    [
                        GroupLabel(Loc.Get(labelKey)) with { Grow = 1f, Shrink = 1f, MinWidth = 0f },
                        caption is { Length: > 0 }
                            ? (Element)new TextEl(caption) { Size = 11f, Color = Tok.TextTertiary, Shrink = 0f, MaxLines = 1 }
                            : new BoxEl { Width = 0f },
                    ],
                },
                new BoxEl
                {
                    Direction = 1, Shrink = 0f, Corners = Radii.ControlAll, Fill = Tok.FillCardSecondary,
                    BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault, ClipToBounds = true,
                    Children = [.. items],
                },
            ],
        };

        /// <summary>Label (+ sublabel) left, control right.</summary>
        public static Element Prop(string label, string? sub, Element? control, string? icon = null, bool enabled = true)
        {
            var kids = new List<Element>(3);
            if (icon is { Length: > 0 }) kids.Add(Icon(icon, 16f, Tok.TextSecondary));
            kids.Add(LabelColumn(label, sub));
            if (control is not null)
                kids.Add(new BoxEl
                {
                    Direction = 0, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.End,
                    Children = [control],
                });
            return Frame(kids, enabled, vertical: false);
        }

        /// <summary>A full-width control under its label, on the same padding and floor as <see cref="Prop"/>.</summary>
        public static Element Wide(string label, string? sub, Element? control, bool enabled = true)
        {
            Element[] stack = control is null ? [LabelColumn(label, sub)] : [LabelColumn(label, sub), control];
            return Frame(
                [new BoxEl { Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, Gap = 6f, Children = stack }],
                enabled, vertical: true);
        }

        static Element LabelColumn(string label, string? sub)
        {
            var head = new TextEl(label)
            {
                Size = 13f, Color = Tok.TextPrimary, MaxLines = 2, Wrap = TextWrap.Wrap, Trim = TextTrim.CharacterEllipsis,
            };
            Element[] lines = sub is { Length: > 0 }
                ?
                [
                    head,
                    new TextEl(sub)
                    {
                        Size = 11f, Color = Tok.TextTertiary, MaxLines = 2, Wrap = TextWrap.Wrap, Trim = TextTrim.CharacterEllipsis,
                    },
                ]
                : [head];
            return new BoxEl
            {
                Direction = 1, Grow = 1f, Basis = 0f, Shrink = 1f, MinWidth = 0f, Gap = 1f, Justify = FlexJustify.Center,
                Children = lines,
            };
        }

        static Element Frame(IReadOnlyList<Element> kids, bool enabled, bool vertical) => new BoxEl
        {
            Direction = (byte)(vertical ? 1 : 0),
            Shrink = 0f, MinHeight = RowMinHeight, Gap = Spacing.M,
            AlignItems = vertical ? FlexAlign.Stretch : FlexAlign.Center,
            Padding = new Edges4(Spacing.M, RowPadY, Spacing.M, RowPadY),
            Opacity = enabled ? 1f : 0.4f,
            IsEnabled = enabled,
            Children = [.. kids],
        };

        /// <summary>The Maximum-items row: the live VALUE rides right of the header, so 0 reads as the word "All".</summary>
        public static Element Ranged(string label, string valueCaption, Element control) => new BoxEl
        {
            Direction = 1, Shrink = 0f, Gap = Spacing.XS,
            Padding = new Edges4(Spacing.M, Spacing.S, Spacing.M, Spacing.S),
            Children =
            [
                new BoxEl
                {
                    Direction = 0, Shrink = 0f, Gap = Spacing.S, AlignItems = FlexAlign.Center,
                    Children =
                    [
                        new TextEl(label)
                        {
                            Size = 13f, Color = Tok.TextPrimary, Grow = 1f, Shrink = 1f, MinWidth = 0f, MaxLines = 1,
                            Trim = TextTrim.CharacterEllipsis,
                        },
                        new TextEl(valueCaption) { Size = 12f, Weight = 600, Color = Tok.TextSecondary, Shrink = 0f, MaxLines = 1 },
                    ],
                },
                control,
            ],
        };

        /// <summary>The row's subject AND its subscription: the document version (an accepted edit, undo, an external
        /// write) and the host's reject epoch (a rejection moves no version, so without it a row never snaps back).</summary>
        public static SidebarSectionSpec? Subject(ISidebarEditHost host, string sectionId)
        {
            _ = host.RejectEpoch.Value;
            _ = LayoutVersion.Value;
            return Layout.Find(sectionId);
        }

        /// <summary>The epoch a controlled row folds into its mirror dep, so the mirror re-runs on every answer — incl. "no".</summary>
        public static int Epoch(ISidebarEditHost host) => LayoutVersion.Peek() * 397 + host.RejectEpoch.Peek();

        // ── the ONE enum treatment ──

        /// <summary>Segmented minus the accent underline: the filled segment is the indicator, the pill is styled to
        /// nothing through the public part seam (the 3-DIP slot stays, so suppressing costs no relayout).</summary>
        static readonly TemplateParts SegmentedNoPill = new()
        {
            [Segmented.PartSelectionPill] = pill => pill with { Fill = ColorF.Transparent, Width = 0f },
        };

        static Segmented.Style SegmentedCompact => Segmented.DefaultStyle with
        {
            Height = 30f, FontSize = 12f, ItemMinWidth = 40f,
            CornerRadius = Radii.Control, ItemCornerRadius = Radii.Control - 1f,
        };

        /// <summary><see cref="SidebarChoiceTreatment"/> picks from the RESOLVED labels; SelectorBar never appears here.</summary>
        public static Element Choice(string[] labels, Signal<int> index, Action<int> onChange, bool enabled = true)
        {
            if (!SidebarChoiceTreatment.UsesSegmented(labels))
                return ComboBox.Create(labels, index, width: ComboWidth, isEnabled: enabled, onChange: onChange);
            var items = new SegmentedItem[labels.Length];
            for (int i = 0; i < labels.Length; i++) items[i] = new SegmentedItem(labels[i], IsEnabled: enabled);
            return Segmented.Create(items, index, onChange, new Segmented.SegmentedOptions
            {
                IsEnabled = enabled, Style = SegmentedCompact, Parts = SegmentedNoPill,
            });
        }

        /// <summary>The destructive button: the app's one red pairing (critical ink on the critical wash) folded into the
        /// stock Subtle geometry through Button's palette seam. Red is a warning; the undo step is the safety.</summary>
        public static BoxEl Danger(string label, Action onClick, string? glyph = null)
            => Button.Create(label, onClick, ButtonAppearance.Subtle, ControlSize.Small, glyph: glyph, palette: DangerPalette);

        static Button.ButtonPalette DangerPalette
        {
            get
            {
                var wash = Tok.SystemFillCriticalBackground;
                var ink = Tok.SystemFillCritical;
                return new Button.ButtonPalette(
                    Background: new StateBrush(ColorF.Transparent, wash, wash with { A = wash.A * 0.75f }, ColorF.Transparent),
                    Foreground: new StateBrush(ink, ink, ink with { A = 0.85f }, Tok.TextDisabled),
                    Border: Button.BorderRamp.Flat(GradientSpec.Solid(ColorF.Transparent)),
                    Sizing: BackgroundSizing.InnerBorderEdge);
            }
        }
    }

    // ══ 7. THE PROPERTY SURFACE ════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Which rows a kind shows is NOT written here (<see cref="SidebarPropertyRows"/> over the per-kind option
    /// table), and an Extension section's rows are GENERATED from its source's schema. The subject is
    /// <c>host.Selected</c>; groups are added only when non-empty.</summary>
    internal sealed class CzPropertyPanel(ISidebarEditHost host, string scrollKey) : Component
    {
        readonly List<SidebarDisplayField> _appearanceFields = new(8);
        readonly List<SidebarDisplayField> _behaviorFields = new(6);

        public override Element Render()
        {
            // HOOKS FIRST (the no-subject arm returns early): the rejection sentence at the foot, 4 s, re-armed per epoch.
            int epoch = host.RejectEpoch.Value;
            var dismissed = UseSignal(-1);
            UseTimeout(() => dismissed.Value = epoch, 4000f, DepKey.From(epoch));

            _ = LayoutVersion.Value;
            string? id = host.Selected.Value;
            var spec = id is { Length: > 0 } ? Layout.Find(id) : null;
            if (spec is null || id is null) return NoSelection();

            var general = new List<Element>(2);
            if (SidebarPropertyRows.ShowsTitleRow(spec.Kind))
                general.Add(Embed.Comp(() => new CzTitleRow(host, id)) with { Key = "title:" + id });
            general.Add(Embed.Comp(() => new CzHiddenRow(host, id)) with { Key = "hidden:" + id });

            string? contentCaption = null;
            var content = new List<Element>(16);
            if (SidebarSectionKinds.SupportsLibraryQuery(spec.Kind))
                content.Add(Embed.Comp(() => new CzQueryBlock(host, id)) with { Key = "query:" + id });
            if (spec.IsExtension) AppendExtension(content, spec, id);
            if (SidebarSectionKinds.AcceptsItems(spec.Kind))
            {
                contentCaption = Loc.Format(CzLoc.ItemCount, ("count", spec.ItemList.Count));
                AppendItems(content, spec, id);
            }

            SidebarPropertyRows.DisplayFields(spec, _appearanceFields, _behaviorFields);
            var appearance = new List<Element>(_appearanceFields.Count);
            for (int i = 0; i < _appearanceFields.Count; i++) appearance.Add(DisplayRow(id, _appearanceFields[i]));
            var behavior = new List<Element>(_behaviorFields.Count + 1);
            for (int i = 0; i < _behaviorFields.Count; i++) behavior.Add(DisplayRow(id, _behaviorFields[i]));
            // The LIVE collapse state (spec.Collapsed, what the pane reads) stands in for the dead add-time seed.
            if (SidebarPropertyRows.ShowsCollapseRow(spec.Kind))
                behavior.Add(Embed.Comp(() => new CzCollapsedRow(host, id)) with { Key = "collapsed:" + id });

            var groups = new List<Element>(6) { CzRow.Group(CzLoc.GroupGeneral, general) };
            if (content.Count > 0) groups.Add(CzRow.Group(CzLoc.GroupContent, content, contentCaption));
            if (appearance.Count > 0) groups.Add(CzRow.Group(CzLoc.GroupAppearance, appearance));
            if (behavior.Count > 0) groups.Add(CzRow.Group(CzLoc.GroupBehavior, behavior));
            groups.Add(new BoxEl
            {
                Direction = 0, Shrink = 0f, Justify = FlexJustify.Start,
                Padding = new Edges4(Spacing.S, Spacing.M, Spacing.S, Spacing.XXL),
                Children = [CzRow.Danger(Loc.Get(CzLoc.RemoveSection), () => Remove(id), Icons.Delete)],
            });

            var body = ScrollView(new BoxEl
            {
                Direction = 1, Gap = Spacing.M, Padding = new Edges4(0f, Spacing.XS, 0f, Spacing.XXL),
                Children = [.. groups],
            }) with { Grow = 1f, Shrink = 1f, MinHeight = 0f, AutoEdgeFade = true, ScrollKey = scrollKey };

            // 0.3 FIX (ch 26 §0.3 caveat, parity 87): the popover's rejection is no longer mute.
            Element reject = host.LastReject is { Length: > 0 } key && dismissed.Value != epoch
                ? new BoxEl
                {
                    Shrink = 0f, Padding = new Edges4(Spacing.S, 0f, Spacing.S, Spacing.S),
                    Children = [InfoBar.Create(InfoBarSeverity.Informational, Loc.Get(key), "",
                        onClose: () => dismissed.Value = epoch)],
                }
                : new BoxEl { Height = 0f, Shrink = 0f };

            return new BoxEl
            {
                Direction = 1, Grow = 1f, Shrink = 1f, MinHeight = 0f, MinWidth = 0f, ClipToBounds = true,
                Children = [SubjectHeader(spec, id), Divider(), body, reject],
            };
        }

        static Element NoSelection() => new BoxEl
        {
            Grow = 1f, Direction = 1, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Padding = new Edges4(Spacing.L, Spacing.XL, Spacing.L, Spacing.XL), Gap = Spacing.S,
            Children =
            [
                Icon(Icons.Settings, 20f, Tok.TextTertiary),
                new TextEl(Loc.Get(CzLoc.NoSelection)) { Size = 12f, Color = Tok.TextTertiary, Wrap = TextWrap.Wrap, MaxLines = 3 },
            ],
        };

        Element SubjectHeader(SidebarSectionSpec spec, string id) => new BoxEl
        {
            Direction = 0, Height = 52f, Shrink = 0f, Gap = Spacing.S, AlignItems = FlexAlign.Center,
            Padding = new Edges4(Spacing.M, 0f, Spacing.S, 0f),
            Children =
            [
                Icon(RowGlyphs.ForSectionKind(spec.Kind), 16f, Tok.TextSecondary),
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Basis = 0f, Shrink = 1f, MinWidth = 0f,
                    Children =
                    [
                        new TextEl(CzText.TitleOf(spec))
                        {
                            Size = 13f, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                        },
                        new TextEl(Loc.Get(SidebarSectionKinds.PaletteNameLocKey(spec.Kind) ?? CzLoc.ExtensionKind))
                        {
                            Size = 10f, Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                        },
                    ],
                },
                Embed.Comp(() => new CzMenuButton(Icons.More, () => SectionMenu(id))) with { Key = "subject-menu:" + id },
            ],
        };

        /// <summary>Duplicate · ─ · Remove section, built at open time from the LIVE spec (a rename since mount is honoured).</summary>
        IReadOnlyList<MenuFlyoutItem> SectionMenu(string id)
        {
            if (Layout.Find(id) is not { } live) return Array.Empty<MenuFlyoutItem>();
            string copyTitle = Loc.Format(CzLoc.DuplicateSuffix, ("name", CzText.TitleOf(live)));
            return
            [
                new MenuFlyoutItem(Loc.Get(CzLoc.Duplicate), default, true, () => host.Dispatch(new DuplicateSection(id, copyTitle))),
                MenuFlyoutItem.Separator,
                new MenuFlyoutItem(Loc.Get(CzLoc.RemoveSection), Icons.Delete, true, () => Remove(id)),
            ];
        }

        void Remove(string id)
        {
            if (host.Dispatch(new RemoveSection(id)) == SidebarRejectReason.None) host.Select(null);
        }

        Element DisplayRow(string id, SidebarDisplayField field)
        {
            Element row = field switch
            {
                SidebarDisplayField.MaxItems =>
                    Embed.Comp(() => new CzSliderRow(host, id, field, 0, SidebarLayoutReducer.MaxItemsPerSection)),
                SidebarDisplayField.GridColumns =>
                    Embed.Comp(() => new CzNumberRow(host, id, field, SidebarPropertyRows.GridColumnsMin, SidebarPropertyRows.GridColumnsMax)),
                _ when SidebarDisplayValues.IsFlag(field) => Embed.Comp(() => new CzToggleRow(host, id, field)),
                _ => Embed.Comp(() => new CzSelectorRow(host, id, field)),
            };
            return row with { Key = "opt:" + (int)field + ":" + id };
        }

        /// <summary>A contributed section: the read-only source id (an id is the only name a contribution has), then either
        /// one honest note or one generated row per schema field.</summary>
        void AppendExtension(List<Element> into, SidebarSectionSpec spec, string id)
        {
            var xref = spec.Extension;
            ISidebarDataSource? source = null;
            string sourceId = xref is { IsWellFormed: true } ? SidebarContributions.SourceId(xref.ExtensionId, xref.ContributionId) : "";
            bool registered = sourceId.Length > 0 && Binder?.Sources is { } table && table.TryGet(sourceId, out source);
            var note = SidebarConfigFieldRules.NoteFor(xref, registered, source?.ConfigSchema.Version ?? 0);
            if (note == SidebarExtensionNote.PickContribution)
            {
                into.Add(CzPicker.Note(Loc.Get(CzLoc.RejectExtensionRefMissing)));
                return;
            }
            into.Add(CzRow.Prop(Loc.Get(CzLoc.ExtensionKind), sourceId, null));
            if (note == SidebarExtensionNote.ManageExtension || source is null)
            {
                into.Add(CzPicker.Note(Loc.Get(CzLoc.ExtensionManage)));
                return;
            }
            var fields = source.ConfigSchema.Fields;
            for (int i = 0; i < fields.Count; i++)
            {
                var field = fields[i];
                into.Add(Embed.Comp(() => new CzConfigRow(host, id, field)) with { Key = "cfg:" + id + ":" + field.Key });
            }
        }

        /// <summary>The authored items, then the empty hint (Pinned) or the two add buttons (ch 26 parity 80).</summary>
        void AppendItems(List<Element> into, SidebarSectionSpec spec, string id)
        {
            var items = spec.ItemList;
            for (int i = 0; i < items.Count; i++)
            {
                string itemId = items[i].Id;
                into.Add(Embed.Comp(() => new CzItemRow(host, id, itemId)) with { Key = "item:" + id + ":" + itemId });
            }

            var affordances = SidebarPropertyRows.Items(spec.Kind, items.Count);
            if (affordances.EmptyHint) into.Add(EmptyRow(Icons.Pin, Loc.Get(CzLoc.PinEmptyHint)));
            if (!affordances.Buttons) return;

            bool embed = spec.Kind == SidebarSectionKind.EntityEmbed;
            var buttons = new List<Element>(2)
            {
                Embed.Comp(() => new CzAddItemButton(host, id, embed, affordances.AddEnabled))
                    with { Key = "add:" + id + ":" + (affordances.AddEnabled ? "on" : "off") },
            };
            if (affordances.ActionShortcut)
                buttons.Add(Embed.Comp(() => new CzAddActionButton(host, id, affordances.ActionEnabled))
                    with { Key = "addact:" + id + ":" + (affordances.ActionEnabled ? "on" : "off") });
            into.Add(new BoxEl
            {
                Direction = 0, Gap = Spacing.S, Shrink = 0f,
                Padding = new Edges4(Spacing.M, Spacing.S, Spacing.M, Spacing.M),
                Children = [.. buttons],
            });
        }

        /// <summary>An EMPTY-state row on the card's two-column geometry: a dimmed kind glyph beside the hint.</summary>
        static Element EmptyRow(string glyph, string hint) => new BoxEl
        {
            Direction = 0, Shrink = 0f, MinHeight = 44f, Gap = Spacing.M, AlignItems = FlexAlign.Center,
            Padding = Edges4.All(Spacing.M),
            Children =
            [
                new BoxEl
                {
                    Width = 28f, Height = 28f, Shrink = 0f, Corners = Radii.ControlAll, Fill = Tok.FillSubtleSecondary,
                    AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, HitTestVisible = false,
                    Children = [Icon(glyph, 14f, Tok.TextTertiary)],
                },
                new TextEl(hint) { Size = 12f, Color = Tok.TextTertiary, Grow = 1f, Shrink = 1f, MinWidth = 0f, MaxLines = 3, Wrap = TextWrap.Wrap },
            ],
        };
    }

    /// <summary>"Add…" — its own component because the item picker needs the overlay context. The enabled flag is a
    /// MOUNT value, keyed by the caller; the insertion index is re-read at click time.</summary>
    internal sealed class CzAddItemButton(ISidebarEditHost host, string sectionId, bool entitiesOnly, bool enabled) : Component
    {
        public override Element Render()
        {
            var overlay = UseContext(Overlay.Service);
            return Button.Create(Loc.Get(CzLoc.ItemAdd), () => CzPicker.OpenItem(overlay, item =>
                    host.Dispatch(new AddItem(sectionId, item, Layout.Find(sectionId)?.ItemList.Count ?? 0)), entitiesOnly),
                ButtonAppearance.Standard, ControlSize.Small, isEnabled: enabled) with { Grow = 1f };
        }
    }

    internal sealed class CzAddActionButton(ISidebarEditHost host, string sectionId, bool enabled) : Component
    {
        public override Element Render()
        {
            var overlay = UseContext(Overlay.Service);
            return Button.Create(Loc.Get(CzLoc.ItemAction), () => CzPicker.OpenAction(overlay, null, binding =>
                    host.Dispatch(new AddItem(sectionId,
                        new SidebarItemSpec(SidebarIds.NewItem(), SidebarItemTarget.Action, binding.ActionKey, Action: binding),
                        Layout.Find(sectionId)?.ItemList.Count ?? 0))),
                ButtonAppearance.Subtle, ControlSize.Small, isEnabled: enabled) with { Grow = 1f };
        }
    }

    /// <summary>Rename: Enter commits, Esc reverts, and a BLUR commits — load-bearing in the light-dismiss popover, where
    /// the click that closes it is the blur. One rename is one undo step, never per keystroke.</summary>
    internal sealed class CzTitleRow(ISidebarEditHost host, string sectionId) : Component
    {
        readonly Signal<string> _text = new("");

        public override Element Render()
        {
            var spec = CzRow.Subject(host, sectionId);
            string title = spec?.Title ?? "";
            UseLayoutEffect(() => _text.SetIfChanged(title),
                DepKey.From(StringComparer.Ordinal.GetHashCode(title), CzRow.Epoch(host)));

            return CzRow.Wide(Loc.Get(CzLoc.Rename), Loc.Get(CzLoc.RenameHint),
                TextBox.Create(_text, null, new TextBox.TextBoxOptions
                {
                    Width = CzRow.ComboWidth, Height = 32f, MaxLength = SidebarLayoutReducer.MaxTitleLength,
                    Placeholder = spec is null ? "" : CzText.TitleOf(spec),
                    OnCommit = text => host.Dispatch(new RenameSection(sectionId, text)),
                    OnCancel = () => _text.SetIfChanged(title),
                    CommitOnLostFocus = true,
                }));
        }
    }

    internal sealed class CzHiddenRow(ISidebarEditHost host, string sectionId) : Component
    {
        readonly Signal<bool> _on = new(false);

        public override Element Render()
        {
            bool hidden = CzRow.Subject(host, sectionId)?.Hidden ?? false;
            UseLayoutEffect(() => _on.SetIfChanged(hidden), DepKey.From(hidden ? 1 : 0, CzRow.Epoch(host)));
            return CzRow.Prop(Loc.Get(CzLoc.Hidden), Loc.Get(CzLoc.HiddenSub),
                ToggleSwitch.Create(_on, v => host.Dispatch(new SetSectionHidden(sectionId, v))));
        }
    }

    /// <summary>The LIVE collapse row: edits <c>spec.Collapsed</c>, which the pane reads, so the docked section folds in
    /// the same frame. No sublabel — the catalog's "Start this section collapsed" describes the seed it replaced.</summary>
    internal sealed class CzCollapsedRow(ISidebarEditHost host, string sectionId) : Component
    {
        readonly Signal<bool> _on = new(false);

        public override Element Render()
        {
            bool collapsed = CzRow.Subject(host, sectionId)?.Collapsed ?? false;
            UseLayoutEffect(() => _on.SetIfChanged(collapsed), DepKey.From(collapsed ? 1 : 0, CzRow.Epoch(host)));
            return CzRow.Prop(Loc.Get(CzLoc.Collapse), null,
                ToggleSwitch.Create(_on, v => host.Dispatch(new SetSectionCollapsed(sectionId, v))));
        }
    }

    /// <summary>The library query block: kind checkboxes, sort, direction and — only while the projection says the data
    /// supports it — the qualifier. Every edit rebuilds the WHOLE query from the stored one, so the include/exclude uri
    /// sets survive a scalar edit; the reducer repairs illegal combinations.</summary>
    internal sealed class CzQueryBlock(ISidebarEditHost host, string sectionId) : Component
    {
        readonly Signal<bool> _playlists = new(false), _albums = new(false), _artists = new(false), _shows = new(false);
        readonly Signal<int> _sort = new(0);
        readonly Signal<bool> _desc = new(false);
        readonly Signal<int> _qualifier = new(0);

        static readonly string[] SortKeys =
        [
            "sidebar.option.sortRecents", "sidebar.option.sortRecentlyAdded", "sidebar.option.sortAlphabetical",
            "sidebar.option.sortCreator", "sidebar.option.sortCustom",
        ];

        static readonly string[] QualifierKeys =
        [
            "sidebar.option.qualifierAny", "sidebar.option.qualifierByYou", "sidebar.option.qualifierBySpotify",
            "sidebar.option.qualifierMixed",
        ];

        public override Element Render()
        {
            var spec = CzRow.Subject(host, sectionId);
            var kind = spec?.Kind ?? SidebarSectionKind.EntityList;
            var q = SidebarSectionKinds.EffectiveQuery(kind, spec?.Query);
            // The qualifier gate is the SAME capability flag V3's chips read — one authority.
            var entries = Binder?.Entries ?? Entries;
            _ = entries.Version.Value;
            var shape = SidebarQueryPanelShape.For(kind, entries.QualifiersAvailable);

            bool playlists = (q.Kinds & SidebarEntityKinds.Playlists) != 0;
            bool albums = (q.Kinds & SidebarEntityKinds.Albums) != 0;
            bool artists = (q.Kinds & SidebarEntityKinds.Artists) != 0;
            bool shows = (q.Kinds & SidebarEntityKinds.Shows) != 0;
            int sort = (int)q.Sort, qualifier = (int)q.Qualifier;
            bool desc = q.Descending;
            UseLayoutEffect(() =>
            {
                _playlists.SetIfChanged(playlists);
                _albums.SetIfChanged(albums);
                _artists.SetIfChanged(artists);
                _shows.SetIfChanged(shows);
                _sort.SetIfChanged(sort);
                _desc.SetIfChanged(desc);
                _qualifier.SetIfChanged(qualifier);
            }, DepKey.Combine(DepKey.From((int)q.Kinds, sort), DepKey.From(desc ? 1 : 0, qualifier, CzRow.Epoch(host), 0)));

            var sortLabels = new string[SortKeys.Length];
            for (int i = 0; i < SortKeys.Length; i++) sortLabels[i] = Loc.Get(SortKeys[i]);
            // ComboBox freezes Items/ItemEnabled at mount, so the gate rides the KEY and the combo remounts on a flip.
            bool customOk = SidebarPropertyRows.CustomOrderAllowed(kind, q);
            Element sortCombo = ComboBox.Create(sortLabels, _sort, width: CzRow.ComboWidth, onChange: OnSort,
                itemEnabled: [true, true, true, true, customOk]) with { Key = customOk ? "sort:custom" : "sort:nocustom" };

            var kids = new List<Element>(4);
            if (shape.ShowKinds)
                kids.Add(new BoxEl
                {
                    Direction = 1, Gap = 2f, Shrink = 0f, Padding = new Edges4(Spacing.M, Spacing.S, Spacing.M, Spacing.S),
                    Children =
                    [
                        CheckBox.Create(Loc.Get(CzLoc.FilterPlaylists), _playlists, v => OnKind(SidebarEntityKinds.Playlists, v)),
                        CheckBox.Create(Loc.Get(CzLoc.FilterAlbums), _albums, v => OnKind(SidebarEntityKinds.Albums, v)),
                        CheckBox.Create(Loc.Get(CzLoc.FilterArtists), _artists, v => OnKind(SidebarEntityKinds.Artists, v)),
                        CheckBox.Create(Loc.Get(CzLoc.FilterPodcasts), _shows, v => OnKind(SidebarEntityKinds.Shows, v)),
                    ],
                });
            kids.Add(CzRow.Wide(Loc.Get(CzLoc.Sort), null, sortCombo));
            bool descEnabled = SidebarPropertyRows.DescendingEnabled(q);
            kids.Add(CzRow.Prop(Loc.Get(CzLoc.Descending), null, ToggleSwitch.Create(_desc, OnDesc, isEnabled: descEnabled),
                enabled: descEnabled));
            if (shape.ShowQualifier)
            {
                var qualifierLabels = new string[QualifierKeys.Length];
                for (int i = 0; i < QualifierKeys.Length; i++) qualifierLabels[i] = Loc.Get(QualifierKeys[i]);
                // "By Spotify" busts the segmented budget, so this resolves to the dropdown, never a clipping tab strip.
                kids.Add(CzRow.Wide(Loc.Get(CzLoc.Qualifier), null, CzRow.Choice(qualifierLabels, _qualifier, OnQualifier)));
            }
            return new BoxEl { Direction = 1, Shrink = 0f, Children = [.. kids] };
        }

        SidebarEntityQuery Current()
        {
            var spec = Layout.Find(sectionId);
            return SidebarSectionKinds.EffectiveQuery(spec?.Kind ?? SidebarSectionKind.EntityList, spec?.Query);
        }

        void OnKind(SidebarEntityKinds bit, bool on)
        {
            var q = Current();
            Push(q with { Kinds = on ? q.Kinds | bit : q.Kinds & ~bit });
        }

        void OnSort(int index) => Push(Current() with { Sort = (SidebarSortMode)Math.Clamp(index, 0, 4) });

        void OnDesc(bool on) => Push(Current() with { Descending = on });

        void OnQualifier(int index) => Push(Current() with { Qualifier = (SidebarPlaylistQualifier)Math.Clamp(index, 0, 3) });

        void Push(SidebarEntityQuery next) => host.Dispatch(new SetQuery(sectionId, next));
    }

    /// <summary>ONE generated control for ONE schema field. The field freezes at mount, which is right: the row is keyed
    /// by section + field key. Writes go through <see cref="SidebarConfigJson"/>, which copies every untouched member.</summary>
    internal sealed class CzConfigRow(ISidebarEditHost host, string sectionId, SidebarConfigField field) : Component
    {
        readonly Signal<string> _text = new("");
        readonly Signal<double> _number = new(0);
        readonly Signal<bool> _flag = new(false);
        readonly Signal<int> _choice = new(0);

        public override Element Render()
        {
            var spec = CzRow.Subject(host, sectionId);
            var config = new SidebarSourceConfig(spec?.Extension?.Config ?? SidebarJson.EmptyObject);
            var overlay = UseContext(Overlay.Service);
            string label = Loc.Get(field.LabelLocKey);

            string text = config.Str(field.Key) ?? "";
            int number = config.Int(field.Key, SidebarConfigFieldRules.DefaultInt(field));
            bool flag = config.Bool(field.Key, SidebarConfigFieldRules.DefaultBool(field));
            int choice = SidebarConfigFieldRules.ChoiceIndex(field, config.Str(field.Key));
            // ONE mirror for every kind (hooks may not be conditional); only the signal the field renders is read.
            UseLayoutEffect(() =>
            {
                _text.SetIfChanged(text);
                _number.SetIfChanged(number);
                _flag.SetIfChanged(flag);
                _choice.SetIfChanged(choice);
            }, DepKey.Combine(DepKey.From(StringComparer.Ordinal.GetHashCode(text)),
                              DepKey.From(number, flag ? 1 : 0, choice, CzRow.Epoch(host))));

            switch (field.Kind)
            {
                case SidebarConfigFieldKind.Bool:
                    return CzRow.Prop(label, null, ToggleSwitch.Create(_flag,
                        v => Write(SidebarConfigJson.WithBool(Config(), field.Key, v))));

                case SidebarConfigFieldKind.Int:
                {
                    var (min, max) = SidebarConfigFieldRules.IntRange(field);
                    return CzRow.Wide(label, null, NumberBox.CreateWithSpinners(_number,
                        v => Write(SidebarConfigJson.WithInt(Config(), field.Key, SidebarNumberEdit.Normalize(v, min, max))),
                        new NumberBox.NumberBoxOptions { Minimum = min, Maximum = max, SmallChange = 1, Width = CzRow.ComboWidth }));
                }

                case SidebarConfigFieldKind.Enum when SidebarConfigFieldRules.RendersAsEnum(field):
                {
                    var values = field.EnumValues!;
                    var labels = new string[values.Count];
                    for (int i = 0; i < values.Count; i++)
                        labels[i] = SidebarConfigFieldRules.EnumLabelLocKey(values[i]) is { } key ? Loc.Get(key) : values[i];
                    // An extension's vocabulary is unbounded, so the resolved labels pick the treatment.
                    return CzRow.Wide(label, null, CzRow.Choice(labels, _choice, i =>
                    {
                        if ((uint)i < (uint)values.Count) Write(SidebarConfigJson.WithString(Config(), field.Key, values[i]));
                    }));
                }

                case SidebarConfigFieldKind.EntityUri:
                {
                    string shown = text.Length == 0 ? Loc.Get(CzLoc.ArtistUnset) : CzText.EntryNameOf(text) ?? text;
                    return CzRow.Prop(label, shown, Button.Create(Loc.Get(CzLoc.ItemAdd), () => CzPicker.OpenItem(overlay,
                            item => Write(SidebarConfigJson.WithString(Config(), field.Key, item.Key)), entitiesOnly: true,
                            kindFilter: SidebarConfigFieldRules.PicksArtist(field) ? SidebarEntryKind.Artist : null),
                        ButtonAppearance.Standard, ControlSize.Small));
                }

                case SidebarConfigFieldKind.UriList:
                {
                    var uris = new List<string>(4);
                    int count = config.Strings(field.Key, uris);
                    return CzRow.Prop(label, count == 0 ? null : Loc.Format(CzLoc.ItemCount, ("count", count)),
                        Embed.Comp(() => new CzMenuButton(Icons.More, () => UriListMenu(overlay))));
                }

                default:
                    return CzRow.Wide(label, null, TextBox.Create(_text, null, new TextBox.TextBoxOptions
                    {
                        Width = CzRow.ComboWidth, Height = 32f,
                        OnCommit = value => Write(SidebarConfigJson.WithString(Config(), field.Key, value)),
                    }));
            }
        }

        /// <summary>Add… · ─ · up to 20 current uris, where a click REMOVES that one.</summary>
        IReadOnlyList<MenuFlyoutItem> UriListMenu(IOverlayService overlay)
        {
            var uris = new List<string>(8);
            new SidebarSourceConfig(Config()).Strings(field.Key, uris);
            var rows = new List<MenuFlyoutItem>(uris.Count + 2)
            {
                new(Loc.Get(CzLoc.ItemAdd), default, true, () => CzPicker.OpenItem(overlay, item =>
                {
                    var next = new List<string>(uris.Count + 1);
                    next.AddRange(uris);
                    if (!next.Contains(item.Key)) next.Add(item.Key);
                    Write(SidebarConfigJson.WithStrings(Config(), field.Key, next));
                }, entitiesOnly: true)),
            };
            if (uris.Count == 0) return rows;
            rows.Add(MenuFlyoutItem.Separator);
            for (int i = 0; i < uris.Count && i < SidebarConfigFieldRules.UriListMenuCap; i++)
            {
                string uri = uris[i];
                rows.Add(new MenuFlyoutItem(CzText.EntryNameOf(uri) ?? uri, default, true, () =>
                {
                    var next = new List<string>(uris.Count);
                    for (int j = 0; j < uris.Count; j++)
                        if (!string.Equals(uris[j], uri, StringComparison.Ordinal)) next.Add(uris[j]);
                    Write(SidebarConfigJson.WithStrings(Config(), field.Key, next));
                }));
            }
            return rows;
        }

        JsonElement Config() => Layout.Find(sectionId)?.Extension?.Config ?? SidebarJson.EmptyObject;

        void Write(JsonElement config) => host.Dispatch(new SetExtensionConfig(sectionId, config));
    }

    /// <summary>One authored item: its title (+ the "why inert" line), its "…" (rebind an action · the icon whitelist), 🗑
    /// and the label override. An unresolvable item keeps its retained title and dims; it is never auto-removed.
    /// <para>Its glyph is the override, else the route's own mark, else — for an Action — <c>Icons.RefineSparkle</c>: the
    /// editor deliberately does NOT draw the descriptor's mark while the pane does (ch 26 W22, ported knowingly).</para></summary>
    internal sealed class CzItemRow(ISidebarEditHost host, string sectionId, string itemId) : Component
    {
        readonly Signal<string> _label = new("");

        public override Element Render()
        {
            _ = CzRow.Subject(host, sectionId);
            var item = SidebarItemCommands.FindItem(Layout, sectionId, itemId);
            string label = item?.LabelOverride ?? "";
            UseLayoutEffect(() => _label.SetIfChanged(label),
                DepKey.From(StringComparer.Ordinal.GetHashCode(label), CzRow.Epoch(host)));
            var overlay = UseContext(Overlay.Service);
            if (item is null) return new BoxEl { Height = 0f };

            string title = TitleOf(item);
            string? reason = InertReason(item);
            Element[] lines = reason is null
                ? [new TextEl(title) { Size = 13f, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis }]
                :
                [
                    new TextEl(title) { Size = 13f, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                    new TextEl(reason)
                    {
                        Size = 11f, Color = Tok.TextTertiary, MaxLines = 2, Wrap = TextWrap.Wrap, Trim = TextTrim.CharacterEllipsis,
                    },
                ];

            // No plate of its own: the group card is the one surface, and the rows share the card's 12/10 rhythm.
            return new BoxEl
            {
                Direction = 1, Shrink = 0f, Gap = 6f, Padding = new Edges4(Spacing.M, 10f, Spacing.M, 10f),
                Opacity = reason is null ? 1f : 0.7f,
                Children =
                [
                    new BoxEl
                    {
                        Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center,
                        Children =
                        [
                            new BoxEl
                            {
                                Width = 24f, Height = 24f, Shrink = 0f, Corners = Radii.ControlAll, Fill = Tok.FillSubtleSecondary,
                                HitTestVisible = false, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                                Children = [Icon(GlyphOf(item), 13f, Tok.TextSecondary)],
                            },
                            new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, Shrink = 1f, MinWidth = 0f, Gap = 1f, Children = lines },
                            Embed.Comp(() => new CzMenuButton(Icons.More, () => ItemMenu(overlay))),
                            ToolTip.Wrap(IconButton.Create(Icons.Delete, RemoveItemNow, size: ControlSize.Small),
                                Loc.Get(CzLoc.ItemRemove)),
                        ],
                    },
                    TextBox.Create(_label, null, new TextBox.TextBoxOptions
                    {
                        Width = CzRow.ComboWidth, Height = 30f, MaxLength = SidebarLayoutReducer.MaxTitleLength,
                        Placeholder = Loc.Get(CzLoc.ItemLabelPlaceholder),
                        OnCommit = text => Send(new SetItemLabel(sectionId, itemId, text)),
                        OnCancel = () => _label.SetIfChanged(label),
                    }),
                ],
            };
        }

        /// <summary>(action items) Action shortcut · ─ ·, then a disabled "Icon" header over the 30 whitelist names as radio
        /// items; clicking the checked one CLEARS the override. Built at open time from the live item.</summary>
        IReadOnlyList<MenuFlyoutItem> ItemMenu(IOverlayService overlay)
        {
            if (SidebarItemCommands.FindItem(Layout, sectionId, itemId) is not { } item) return Array.Empty<MenuFlyoutItem>();
            var allowed = RowGlyphs.Allowed;
            var rows = new List<MenuFlyoutItem>(allowed.Length + 3);
            if (item.Target == SidebarItemTarget.Action)
            {
                var existing = item.Action;
                rows.Add(new MenuFlyoutItem(Loc.Get(CzLoc.ItemAction), default, true, () => CzPicker.OpenAction(overlay, existing,
                    binding => Send(new SetItemAction(sectionId, itemId, binding)))));
                rows.Add(MenuFlyoutItem.Separator);
            }
            rows.Add(new MenuFlyoutItem(Loc.Get(CzLoc.ItemIcon), default, false, null));
            for (int i = 0; i < allowed.Length; i++)
            {
                string name = allowed[i];
                bool on = string.Equals(item.IconOverride, name, StringComparison.Ordinal);
                rows.Add(MenuFlyoutItem.RadioItem(name, on, () => Send(new SetItemIcon(sectionId, itemId, on ? null : name)),
                    RowGlyphs.Glyph(name, Icons.MusicNote)));
            }
            return rows;
        }

        static string TitleOf(SidebarItemSpec item)
        {
            if (item.LabelOverride is { Length: > 0 } l) return l;
            switch (item.Target)
            {
                case SidebarItemTarget.Route:
                    return CzText.RouteTitle(item.Key);
                case SidebarItemTarget.Action:
                    if (item.Action is { } binding && Actions.Registry.Current is { } registry)
                    {
                        var b = binding.ToActionBinding();
                        if (registry.TryGetAction(in b, out var descriptor)) return descriptor.Label();
                    }
                    // Never "Unavailable" for an action: the raw key is the honest name of a missing descriptor.
                    return item.Action?.ActionKey ?? item.Key;
                default:
                    if (item.FallbackTitle is { Length: > 0 } f) return f;
                    return CzText.EntryNameOf(item.Key) ?? Loc.Get(CzLoc.MissingEntity);
            }
        }

        static string GlyphOf(SidebarItemSpec item)
        {
            if (item.IconOverride is { Length: > 0 } name) return RowGlyphs.Glyph(name, Icons.MusicNote);
            return item.Target switch
            {
                SidebarItemTarget.Route => CzText.RouteGlyph(item.Key),
                SidebarItemTarget.Action => Icons.RefineSparkle,
                _ => RowGlyphs.ForEntityKind(item.EntityKind),
            };
        }

        /// <summary>Why the row would be inert: one of the seven reasons, from the registry's own resolution. No registry
        /// yet ⇒ no line at all, never a fabricated one.</summary>
        static string? InertReason(SidebarItemSpec item)
        {
            if (item.Target != SidebarItemTarget.Action) return null;
            if (item.Action is not { } binding) return Loc.Get(CzLoc.RejectExtensionRefMissing);
            if (Actions.Registry.Current is not { } registry) return null;
            var b = binding.ToActionBinding();
            var resolution = registry.Resolve(Actions.Services, in b);
            return resolution.Available ? null : Loc.Get(resolution.ReasonLocKey ?? CzLoc.MissingEntity);
        }

        /// <summary>The Shortcuts sentinel picks the BAND's rejection words; command routing stays SidebarItemCommands'.</summary>
        SidebarRejectReason Send(SidebarCommand command)
            => SidebarIds.IsTopBar(sectionId) ? host.DispatchTopBar(command) : host.Dispatch(command);

        void RemoveItemNow() => Send(SidebarItemCommands.Remove(sectionId, itemId));
    }

    // ══ 8. THE DISPLAY-OPTION ROWS ═════════════════════════════════════════════════════════════════════════════════════

    /// <summary>A 0/1 flag. The mirror dep carries the EPOCH, not just the value: a rejection leaves the value unchanged.</summary>
    internal sealed class CzToggleRow(ISidebarEditHost host, string sectionId, SidebarDisplayField field) : Component
    {
        readonly Signal<bool> _on = new(false);

        public override Element Render()
        {
            bool value = SidebarDisplayValues.Read(CzRow.Subject(host, sectionId)?.Display, field) != 0;
            UseLayoutEffect(() => _on.SetIfChanged(value), DepKey.From(value ? 1 : 0, CzRow.Epoch(host)));
            // Only the flag whose consequence the label cannot carry gets a sublabel (and only a key the catalog has).
            string? sub = field == SidebarDisplayField.ShowInRail ? Loc.Get(CzLoc.ShowInRailSub) : null;
            return CzRow.Prop(Loc.Get(SidebarDisplayValues.LabelLocKey(field)), sub,
                ToggleSwitch.Create(_on, v => host.Dispatch(new SetDisplayOption(sectionId, field, v ? 1 : 0))));
        }
    }

    internal sealed class CzSelectorRow(ISidebarEditHost host, string sectionId, SidebarDisplayField field) : Component
    {
        readonly Signal<int> _index = new(0);

        public override Element Render()
        {
            int value = SidebarDisplayValues.Read(CzRow.Subject(host, sectionId)?.Display, field);
            UseLayoutEffect(() => _index.SetIfChanged(value), DepKey.From(value, CzRow.Epoch(host)));
            var keys = SidebarDisplayValues.ChoiceLocKeys(field);
            var labels = new string[keys.Length];
            for (int i = 0; i < keys.Length; i++) labels[i] = Loc.Get(keys[i]);
            return CzRow.Wide(Loc.Get(SidebarDisplayValues.LabelLocKey(field)), null,
                CzRow.Choice(labels, _index, i => host.Dispatch(new SetDisplayOption(sectionId, field, i))));
        }
    }

    /// <summary>Maximum items: a slider over 0…500 whose value rides the header, so 0 reads as the WORD "All" — in the
    /// caption and the thumb tooltip — which a spinner structurally cannot show.</summary>
    internal sealed class CzSliderRow(ISidebarEditHost host, string sectionId, SidebarDisplayField field, int min, int max) : Component
    {
        readonly FloatSignal _value = new(0f);

        static readonly Func<float, string> s_thumbCaption = ThumbCaption;

        public override Element Render()
        {
            int value = SidebarDisplayValues.Read(CzRow.Subject(host, sectionId)?.Display, field);
            UseLayoutEffect(() => _value.SetIfChanged(value), DepKey.From(value, CzRow.Epoch(host)));
            return CzRow.Ranged(Loc.Get(SidebarDisplayValues.LabelLocKey(field)), Caption(value),
                Slider.Create(_value, OnCommit, new Slider.SliderOptions
                {
                    Min = min, Max = max, Step = 1f, SmallChange = 1f, LargeChange = 10f,
                    IsThumbToolTipEnabled = true, ThumbToolTipValueConverter = s_thumbCaption,
                }, length: CzRow.SliderLength));
        }

        static string Caption(int value)
            => value == 0 ? Loc.Get(CzLoc.MaxItemsAll) : value.ToString(CultureInfo.CurrentCulture);

        static string ThumbCaption(float v) => Caption(v <= 0f ? 0 : (int)MathF.Round(v));

        void OnCommit(float v) => host.Dispatch(new SetDisplayOption(sectionId, field, (int)MathF.Round(v)));
    }

    /// <summary>Columns (2-4): a discrete pick, not a range. NumberBox affixes freeze at mount, so the inner spinner is keyed
    /// by the AUTHORITATIVE value and remounts after every accepted change.</summary>
    internal sealed class CzNumberRow(ISidebarEditHost host, string sectionId, SidebarDisplayField field, int min, int max) : Component
    {
        public override Element Render()
        {
            int value = SidebarDisplayValues.Read(CzRow.Subject(host, sectionId)?.Display, field);
            Element spinner = Embed.Comp(() => new CzNumberSpinner(host, sectionId, field, min, max, value))
                with { Key = "number:" + sectionId + ":" + (int)field + ":" + value };
            return CzRow.Wide(Loc.Get(SidebarDisplayValues.LabelLocKey(field)), null, spinner);
        }
    }

    /// <summary>A rejected / no-op edit snaps the local value back at once; an accepted one remounts under the new key.</summary>
    internal sealed class CzNumberSpinner : Component
    {
        readonly ISidebarEditHost _host;
        readonly string _sectionId;
        readonly SidebarDisplayField _field;
        readonly int _min, _max, _authoritative;
        readonly Signal<double> _value;

        public CzNumberSpinner(ISidebarEditHost host, string sectionId, SidebarDisplayField field, int min, int max, int authoritative)
        {
            _host = host; _sectionId = sectionId; _field = field; _min = min; _max = max; _authoritative = authoritative;
            _value = new Signal<double>(authoritative);
        }

        public override Element Render() => NumberBox.CreateWithSpinners(_value, OnCommit,
            new NumberBox.NumberBoxOptions { Minimum = _min, Maximum = _max, SmallChange = 1, Width = CzRow.ComboWidth });

        void OnCommit(double value)
        {
            int next = SidebarNumberEdit.Normalize(value, _min, _max);
            if (_host.Dispatch(new SetDisplayOption(_sectionId, _field, next)) != SidebarRejectReason.None)
                _value.SetIfChanged(_authoritative);
        }
    }

    // ══ 9. THE PICKERS ═════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Both pickers are MODAL (a pick is invoked from places with no stable anchor — a palette row, a menu item).
    /// The item picker commits on a row click and carries only Cancel; the action picker is owner I's
    /// (<see cref="Actions.OpenActionPicker"/>) with this file's item picker as its entity chooser.</summary>
    internal static class CzPicker
    {
        /// <summary>The 480 rung: the stock 320 floor clamped the picker bodies instead of the bodies sizing the card.</summary>
        internal const float DialogW = 480f;

        /// <summary>480 − 2 × the 24-DIP ContentDialog padding.</summary>
        internal const float BodyW = DialogW - 48f;

        /// <summary>A route or a library entity → a ready item. <paramref name="entitiesOnly"/> hides the Navigation tab (a
        /// spotlight spotlights an entity); <paramref name="kindFilter"/> narrows the library (an artist field).</summary>
        public static void OpenItem(IOverlayService? overlay, Action<SidebarItemSpec> onPick, bool entitiesOnly = false,
                                    SidebarEntryKind? kindFilter = null)
        {
            if (Controls.IsNullOverlay(overlay)) return;
            OverlayHandle? handle = null;
            handle = ContentDialog.Show(overlay, d =>
            {
                d.Title = Loc.Get(CzLoc.ItemAdd);
                d.PrimaryText = "";                           // "" hides the primary; null would ship a stray "OK"
                d.CloseText = Loc.Get(CzLoc.Cancel);
                d.DefaultButton = ContentDialog.DefaultBtn.Close;   // Enter and Esc both cancel
                d.DialogWidth = DialogW;
                d.Content = Embed.Comp(() => new CzItemPickerBody(spec =>
                {
                    onPick(spec);
                    handle?.Close();
                }, entitiesOnly, kindFilter));
            });
        }

        /// <summary>The action picker over the live registry; the binding it commits becomes the wire record.</summary>
        public static void OpenAction(IOverlayService? overlay, SidebarActionBinding? existing, Action<SidebarActionBinding> onPick)
        {
            if (Controls.IsNullOverlay(overlay)) return;
            Actions.OpenActionPicker(overlay,
                new Actions.ActionPickerSpec(Actions.Registry.Current, Actions.Services,
                    picked => onPick(SidebarActionBindings.FromActionBinding(in picked)))
                {
                    Existing = existing?.ToActionBinding(),
                    PickEntity = done => OpenItem(overlay, spec => done(spec.Key, spec.FallbackTitle ?? spec.Key), entitiesOnly: true),
                });
        }

        internal static Element Note(string text) => new TextEl(text)
        {
            Size = 12f, Color = Tok.TextTertiary, Wrap = TextWrap.Wrap, MaxLines = 3,
            Margin = new Edges4(Spacing.S, Spacing.S, Spacing.S, 0f),
        };
    }

    /// <summary>Routes ∪ library entities under one search box (W12/W13). A route row carries its RAW key as the second
    /// line — half of the search predicate, which is why "liked" finds Liked Songs.</summary>
    internal sealed class CzItemPickerBody(Action<SidebarItemSpec> pick, bool entitiesOnly, SidebarEntryKind? kindFilter) : Component
    {
        readonly Signal<string> _query = new("");
        readonly Signal<int> _tab = new(0);

        public override Element Render()
        {
            string q = SidebarPalette.NormalizeQuery(_query.Value);
            int tab = entitiesOnly ? 1 : _tab.Value;
            var entries = Binder?.Entries ?? Entries;
            _ = entries.Version.Value;   // a library refresh re-lists

            var rows = new List<Element>(24);
            if (tab == 0) AppendRoutes(rows, q);
            else AppendEntities(rows, q, entries.Current);

            var head = new List<Element>(2);
            if (!entitiesOnly)
                head.Add(SelectorBar.Create([Loc.Get(CzLoc.PaletteNavigation), Loc.Get(CzLoc.PaletteLibrary)], _tab));
            head.Add(TextBox.Create(_query, null, new TextBox.TextBoxOptions
            {
                Placeholder = Loc.Get(CzLoc.SearchPlaceholder), Width = CzPicker.BodyW, Height = 32f,
            }));

            return new BoxEl
            {
                Direction = 1, Width = CzPicker.BodyW, Gap = Spacing.S, MinHeight = 0f,
                Children =
                [
                    new BoxEl { Direction = 1, Gap = Spacing.S, Shrink = 0f, Children = [.. head] },
                    ScrollView(new BoxEl { Direction = 1, Gap = 2f, Children = [.. rows] })
                        with { Height = 320f, Shrink = 0f, AutoEdgeFade = true, ScrollKey = "customizer.picker" },
                ],
            };
        }

        /// <summary>The same 11 destinations the palette offers — one table, two readers, so the offers never disagree.</summary>
        void AppendRoutes(List<Element> into, string q)
        {
            var destinations = SidebarPalette.Destinations;
            for (int i = 0; i < destinations.Length; i++)
            {
                if (destinations[i].RouteKey is not { Length: > 0 } routeKey) continue;
                string title = CzText.RouteTitle(routeKey);
                if (!SidebarPalette.Matches(q, title, routeKey)) continue;
                into.Add(Row(routeKey, CzText.RouteGlyph(routeKey), title, routeKey,
                    () => pick(new SidebarItemSpec(SidebarIds.NewItem(), SidebarItemTarget.Route, routeKey))));
            }
        }

        void AppendEntities(List<Element> into, string q, IReadOnlyList<SidebarLibraryEntry> entries)
        {
            if (entries.Count == 0)
            {
                into.Add(CzPicker.Note(Loc.Get(CzLoc.LibraryEmpty)));
                return;
            }
            int shown = 0;
            for (int i = 0; i < entries.Count && shown < SidebarCustomizerPicks.EntityCap; i++)
            {
                var e = entries[i];
                if (!SidebarCustomizerPicks.OffersEntry(e.Kind, kindFilter)) continue;
                if (!SidebarPalette.Matches(q, e.Name, e.Creator)) continue;
                shown++;
                var kind = SidebarCustomizerPicks.EntityKindOf(e.Kind);
                string uri = e.Uri, name = e.Name;
                into.Add(Row(e.Id, RowGlyphs.ForEntityKind(kind), name, e.Creator, () => pick(new SidebarItemSpec(
                    SidebarIds.NewItem(), SidebarItemTarget.Entity, uri, kind, FallbackTitle: name))));
            }
            // The NORMALIZED query (lower-cased), unlike the palette's raw-cased line — ported as 0.2.9 has it.
            if (shown == 0) into.Add(CzPicker.Note(Loc.Format(CzLoc.SearchEmpty, ("query", q))));
        }

        static Element Row(string key, string glyph, string title, string? sub, Action onClick) => new BoxEl
        {
            Key = key,
            Direction = 0, Height = 40f, Shrink = 0f, Gap = Spacing.S, AlignItems = FlexAlign.Center,
            Padding = new Edges4(Spacing.S, 0f, Spacing.S, 0f), Corners = Radii.ControlAll,
            Cursor = CursorId.Hand, Focusable = true, Role = AutomationRole.Button, OnClick = onClick,
            Children =
            [
                Icon(glyph, 14f, Tok.TextSecondary),
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f,
                    Children = sub is { Length: > 0 }
                        ?
                        [
                            new TextEl(title) { Size = 13f, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                            new TextEl(sub) { Size = 11f, Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                        ]
                        : [new TextEl(title) { Size = 13f, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis }],
                },
            ],
        }.Interactive(Interaction.ListRow);
    }

    // ══ 10. THE TEMPLATE MINIATURE ═════════════════════════════════════════════════════════════════════════════════════

    /// <summary>A 220-tall card: a 258-DIP quarter-scale pane on the live row-height ladder beside a synthetic workspace.
    /// A diagram of the real document (<see cref="SidebarMiniaturePlan"/>), never a skeleton and never the live library.</summary>
    internal static class CzMiniature
    {
        public static Element Template(SidebarCustomLayout layout)
        {
            var plan = new List<SidebarMiniatureRow>(16);
            SidebarMiniaturePlan.Build(layout, plan);
            var rows = new Element[plan.Count];
            for (int i = 0; i < plan.Count; i++) rows[i] = RowFor(plan[i], layout);

            var pane = new BoxEl
            {
                Direction = 1, Width = 258f, Shrink = 0f, Gap = Spacing.XXS,
                Padding = new Edges4(Spacing.S, Spacing.M, Spacing.S, Spacing.M), ClipToBounds = true,
                Fill = Tok.FillCardSecondary, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
                Children = rows,
            };
            return new BoxEl
            {
                Direction = 0, Height = 220f, Shrink = 0f, ClipToBounds = true,
                Corners = Radii.CardAll, Fill = Tok.FillCardDefault, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
                Children = [pane, Workspace()],
            };
        }

        static Element RowFor(in SidebarMiniatureRow row, SidebarCustomLayout layout)
        {
            var section = row.Section;
            switch (row.Kind)
            {
                case SidebarMiniatureRowKind.Blank:
                    // The ONLY arm with no rows: a centred "+" over the template's own description.
                    return new BoxEl
                    {
                        Direction = 1, Grow = 1f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Gap = Spacing.S,
                        Children =
                        [
                            Icon(Icons.Add, 20f, Tok.TextTertiary),
                            new TextEl(Loc.Get(SidebarTemplates.DescriptionLocKey(layout.TemplateId)))
                            {
                                Size = 12f, Color = Tok.TextTertiary, Wrap = TextWrap.Wrap, MaxLines = 2,
                            },
                        ],
                    };
                case SidebarMiniatureRowKind.Divider:
                    return new BoxEl
                    {
                        Height = 1f, AlignSelf = FlexAlign.Stretch, Shrink = 0f, Fill = Tok.StrokeDividerDefault,
                        Margin = new Edges4(0f, Spacing.XXS, 0f, Spacing.XXS),
                    };
                case SidebarMiniatureRowKind.Title:
                    return global::Wavee.Design.Type.Eyebrow(CzText.TitleOf(section!)) with
                    {
                        Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                        Margin = new Edges4(Spacing.XS, Spacing.XXS, Spacing.XS, Spacing.XXS),
                    };
                case SidebarMiniatureRowKind.GridPair:
                    return new BoxEl
                    {
                        Direction = 0, Shrink = 0f, Gap = Spacing.XS,
                        Children = [GridCell(SidebarMiniaturePlan.GridSampleA), GridCell(SidebarMiniaturePlan.GridSampleB)],
                    };
                case SidebarMiniatureRowKind.PinnedPlaylist:
                {
                    var p = PlaylistSample(row.Index);
                    return PreviewRow(p.Name, p.ArtUrl, Icons.MusicNote, section!, "pin:playlist", p.Count);
                }
                case SidebarMiniatureRowKind.PinnedArtist:
                {
                    var a = ArtistSample(row.Index);
                    return PreviewRow(a.Name, a.ArtUrl, Icons.Contact, section!, "pin:artist", -1, circular: true);
                }
                case SidebarMiniatureRowKind.Shortcut:
                {
                    var item = section!.ItemList[row.Index];
                    int count = section.Opts.CountBadges ? MiniatureShortcutCount?.Invoke(item.Key) ?? -1 : -1;
                    return PreviewRow(CzText.RouteTitle(item.Key), null, RowGlyphs.For(item, CzText.RouteGlyph(item.Key)),
                        section, "route:" + item.Key, count);
                }
                case SidebarMiniatureRowKind.TreeFolder:
                    return TreeFolder(section!);
                case SidebarMiniatureRowKind.TreePlaylist:
                {
                    var p = PlaylistSample(row.Index);
                    return PreviewRow(p.Name, p.ArtUrl, Icons.MusicNote, section!, "tree:playlist", p.Count, depth: 1);
                }
                default:
                {
                    var p = PlaylistSample(row.Index);
                    return PreviewRow(p.Name, p.ArtUrl, RowGlyphs.ForSectionKind(section!.Kind), section,
                        "section:" + (int)section.Kind, p.Count);
                }
            }
        }

        static MiniatureSample PlaylistSample(int index)
            => MiniaturePlaylist?.Invoke(index) ?? new MiniatureSample(Loc.Get(CzLoc.KindPlaylist), null, -1);

        static MiniatureSample ArtistSample(int index)
            => MiniatureArtist?.Invoke(index) ?? new MiniatureSample(Loc.Get(CzLoc.KindArtist), null, -1);

        static Element PreviewRow(string title, string? artUrl, string glyph, SidebarSectionSpec section, string seed,
                                  int count, bool circular = false, int depth = 0)
        {
            float height = SidebarRowGeometry.HeightFor(section.Opts.Density, section.Opts.Subtitles);
            float art = section.Opts.Density == SidebarDensity.Compact ? Cover.S20 : Cover.S28;
            var kids = new List<Element>(4);
            if (depth > 0) kids.Add(TreeGuide(height));
            kids.Add(section.Opts.Artwork ? Cover.ArtUrl(artUrl, seed, art, circular) : Cover.Glyph(glyph, art, circular));
            kids.Add(new BoxEl
            {
                Direction = 1, Grow = 1f, Basis = 0f, Shrink = 1f, MinWidth = 0f,
                Children =
                [
                    new TextEl(title) { Size = 12f, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                    section.Opts.Subtitles
                        ? (Element)new TextEl(Loc.Get(CzLoc.FilterPlaylists)) { Size = 10f, Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis }
                        : new BoxEl { Height = 0f },
                ],
            });
            if (section.Opts.CountBadges && count >= 0)
                kids.Add(new TextEl(count.ToString(CultureInfo.CurrentCulture)) { Size = 10f, Color = Tok.TextTertiary, Shrink = 0f });
            return new BoxEl
            {
                Direction = 0, Height = height, Shrink = 0f, Gap = Spacing.S, AlignItems = FlexAlign.Center,
                Padding = new Edges4(Spacing.S, 0f, Spacing.S, 0f), Corners = Radii.ControlAll,
                // The sampled playlist rows carry the subtle plate (0.2.9's "…playlist" seed test).
                Fill = seed.EndsWith("playlist", StringComparison.Ordinal) ? Tok.FillSubtleSecondary : ColorF.Transparent,
                Children = [.. kids],
            };
        }

        static Element TreeGuide(float height) => new BoxEl
        {
            Width = Spacing.M, Height = height, Shrink = 0f, ZStack = true, HitTestPassThrough = true,
            Children =
            [
                new BoxEl { Width = 1f, Height = height / 2f, Shrink = 0f, Margin = new Edges4(Spacing.XS, 0f, 0f, 0f), Fill = Tok.StrokeDividerDefault },
                new BoxEl { Width = Spacing.S, Height = 1f, Shrink = 0f, Margin = new Edges4(Spacing.XS, height / 2f, 0f, 0f), Fill = Tok.StrokeDividerDefault },
            ],
        };

        static Element TreeFolder(SidebarSectionSpec section) => new BoxEl
        {
            Direction = 0, Height = SidebarRowGeometry.HeightFor(section.Opts.Density, section.Opts.Subtitles),
            Shrink = 0f, Gap = Spacing.S, AlignItems = FlexAlign.Center, Padding = new Edges4(Spacing.S, 0f, Spacing.S, 0f),
            Children =
            [
                Icon(Icons.ChevronDown, 10f, Tok.TextTertiary),
                Cover.Folder(Cover.S28, expanded: true),
                new TextEl(Loc.Get(CzLoc.Playlists))
                {
                    Size = 12f, Color = Tok.TextPrimary, Grow = 1f, Basis = 0f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                },
                section.Opts.CountBadges
                    ? (Element)new TextEl("2") { Size = 10f, Color = Tok.TextTertiary, Shrink = 0f }
                    : new BoxEl { Width = 0f },
            ],
        };

        static Element GridCell(int sample)
        {
            var p = PlaylistSample(sample);
            return new BoxEl
            {
                Direction = 0, Grow = 1f, Shrink = 1f, MinWidth = 0f, Height = 48f, Gap = Spacing.XS,
                AlignItems = FlexAlign.Center, Padding = Edges4.All(Spacing.XS), Corners = Radii.ControlAll,
                Fill = Tok.FillSubtleSecondary,
                Children =
                [
                    Cover.ArtUrl(p.ArtUrl, "grid:" + sample.ToString(CultureInfo.InvariantCulture), Cover.S40),
                    new TextEl(p.Name) { Size = 11f, Color = Tok.TextPrimary, Grow = 1f, Basis = 0f, MinWidth = 0f, MaxLines = 2, Trim = TextTrim.CharacterEllipsis },
                ],
            };
        }

        static Element Workspace()
        {
            var hero = PlaylistSample(SidebarMiniaturePlan.HeroSample);
            var cardSamples = SidebarMiniaturePlan.WorkspaceCardSamples;
            var cards = new Element[cardSamples.Length];
            for (int i = 0; i < cardSamples.Length; i++) cards[i] = WorkspaceCard(cardSamples[i]);
            return new BoxEl
            {
                Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, Padding = Edges4.All(Spacing.L), Gap = Spacing.L,
                ClipToBounds = true,
                Children =
                [
                    new BoxEl
                    {
                        Direction = 0, Shrink = 0f, Gap = Spacing.M, AlignItems = FlexAlign.Center,
                        Children =
                        [
                            Cover.ArtUrl(hero.ArtUrl, "workspace:hero", Cover.S64),
                            new BoxEl
                            {
                                Direction = 1, Grow = 1f, MinWidth = 0f, Gap = Spacing.S,
                                Children = [Bar(92f, Spacing.S, Tok.FillSubtleTertiary), Bar(156f, Spacing.XS, Tok.StrokeDividerDefault),
                                            Bar(118f, Spacing.XS, Tok.StrokeDividerDefault)],
                            },
                        ],
                    },
                    new BoxEl { Direction = 0, Shrink = 0f, Gap = Spacing.M, Children = cards },
                ],
            };
        }

        static Element WorkspaceCard(int sample)
        {
            var p = PlaylistSample(sample);
            return new BoxEl
            {
                Direction = 1, Width = Cover.S64, Shrink = 0f, Gap = Spacing.XS,
                Children =
                [
                    Cover.ArtUrl(p.ArtUrl, "workspace:" + sample.ToString(CultureInfo.InvariantCulture), Cover.S64),
                    new TextEl(p.Name) { Size = 10f, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                ],
            };
        }

        static Element Bar(float width, float height, ColorF fill) => new BoxEl
        {
            Width = width, Height = height, Shrink = 0f, Corners = CornerRadius4.All(height / 2f), Fill = fill,
        };
    }

    // ══ 11. TEXT, GLYPHS, LOC KEYS ═════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The customizer's display resolution in one place.</summary>
    internal static class CzText
    {
        /// <summary>A section's name on THIS surface: the rename, else the template key, else the kind's PALETTE name — so a
        /// Divider reads "Divider" here (the pane's own title rule leaves it blank), and only a kind this build does not
        /// understand is nameless (the caller supplies "Unrecognized section").</summary>
        public static string TitleOf(SidebarSectionSpec section)
        {
            if (section.Title is { Length: > 0 } t) return t;
            if (section.TitleLocKey is { Length: > 0 } k) return Loc.Get(k);
            return SidebarSectionKinds.PaletteNameLocKey(section.Kind) is { Length: > 0 } p ? Loc.Get(p) : "";
        }

        public static string RouteTitle(string routeKey) => Shell.Dest(Shell.Parse(routeKey)).Title;

        public static string RouteGlyph(string routeKey) => Shell.Dest(Shell.Parse(routeKey)).Glyph;

        /// <summary>A destination's label is the route table's (the tab strip, the breadcrumb and this can never disagree).</summary>
        public static string PaletteLabel(SidebarPaletteEntry entry)
            => entry.RouteKey is { Length: > 0 } route ? RouteTitle(route) : Loc.Get(entry.NameLocKey);

        public static string? PaletteDescription(SidebarPaletteEntry entry)
            => entry.DescriptionLocKey is { Length: > 0 } key ? Loc.Get(key) : null;

        public static string PaletteGlyph(SidebarPaletteEntry entry)
            => entry.RouteKey is { Length: > 0 } route ? RouteGlyph(route) : GlyphForName(entry.IconName);

        /// <summary>The palette table's 18-name glyph vocabulary — deliberately NOT the 30-name item whitelist.</summary>
        public static string GlyphForName(string? name) => name switch
        {
            "Pin" => Icons.Pin,
            "Heart" => Icons.Heart,
            "Link" => Icons.Link,
            "Folder" => Icons.Folder,
            "Filter" => Icons.Filter,
            "FavoriteStar" => Icons.FavoriteStar,
            "Headphones" => Icons.Headphones,
            "Queue" => Icons.Queue,
            "Play" => Icons.Play,
            "Clock" => Icons.Clock,
            "Contact" => Icons.Contact,
            "Album" => Icons.Album,
            "Calendar" => Icons.Calendar,
            "Grid" => Icons.Grid,
            "Font" => Icons.Font,
            "Remove" => Icons.Remove,
            "RefineSparkle" => Icons.RefineSparkle,
            "Code" => Icons.Code,
            _ => Icons.MusicNote,
        };

        /// <summary>An entity uri's name from the published projection, or null (the caller shows the uri or its retained
        /// title — never a fabricated one).</summary>
        public static string? EntryNameOf(string uri)
        {
            var entries = (Binder?.Entries ?? Entries).Current;
            for (int i = 0; i < entries.Count; i++)
                if (string.Equals(entries[i].Uri, uri, StringComparison.Ordinal)) return entries[i].Name;
            return null;
        }
    }

    /// <summary>The customizer's loc keys as literals (a typo renders loudly as <c>[key]</c>).</summary>
    internal static class CzLoc
    {
        public const string Title = "sidebar.customizer.title";
        public const string Templates = "sidebar.customizer.templates";
        public const string AddSection = "sidebar.customizer.addSection";
        public const string GroupGeneral = "sidebar.customizer.group.general";
        public const string GroupContent = "sidebar.customizer.group.content";
        public const string GroupAppearance = "sidebar.customizer.group.appearance";
        public const string GroupBehavior = "sidebar.customizer.group.behavior";
        public const string NoSelection = "sidebar.customizer.noSelection";
        public const string Undo = "sidebar.customizer.undo";
        public const string Redo = "sidebar.customizer.redo";
        public const string UndoOf = "sidebar.customizer.undoOf";
        public const string RedoOf = "sidebar.customizer.redoOf";
        public const string Reset = "sidebar.customizer.reset";
        public const string Done = "sidebar.customizer.done";
        public const string Back = "auth.back";
        public const string Cancel = "auth.cancel";
        public const string RenameHint = "sidebar.customizer.renameHint";
        public const string Hidden = "sidebar.customizer.hidden";
        public const string HiddenSub = "sidebar.option.hiddenSub";
        public const string RejectExtensionRefMissing = "sidebar.customizer.rejectExtensionRefMissing";
        public const string DuplicateSuffix = "sidebar.customizer.duplicateSuffix";
        public const string ItemLabelPlaceholder = "sidebar.customizer.itemLabelPlaceholder";
        public const string ItemIcon = "sidebar.customizer.itemIcon";
        public const string ItemAdd = "sidebar.customizer.itemAdd";
        public const string ItemRemove = "sidebar.customizer.itemRemove";
        public const string ItemAction = "sidebar.customizer.itemAction";
        public const string MissingEntity = "sidebar.customizer.missingEntity";
        public const string Corrupt = "sidebar.customizer.corrupt";
        public const string CorruptSub = "sidebar.customizer.corruptSub";
        public const string TooNew = "sidebar.customizer.tooNew";
        public const string TooNewSub = "sidebar.customizer.tooNewSub";
        public const string CopyPath = "sidebar.customizer.copyPath";
        public const string FaultDiscard = "sidebar.layoutFault.discard";
        public const string SaveFault = "sidebar.customizer.saveFault";
        public const string SaveFaultSub = "sidebar.customizer.saveFaultSub";
        public const string ApplyTemplateTitle = "sidebar.customizer.applyTemplateTitle";
        public const string ApplyTemplateBody = "sidebar.customizer.applyTemplateBody";
        public const string ApplyTemplateConfirm = "sidebar.customizer.applyTemplateConfirm";
        public const string ResetTitle = "sidebar.customizer.resetTitle";
        public const string ResetBody = "sidebar.customizer.resetBody";
        public const string ResetConfirm = "sidebar.customizer.resetConfirm";
        /// <summary>"Collapse section" — the live row's label doubles as the same command's undo label.</summary>
        public const string Collapse = "sidebar.customizer.undo.collapseSection";
        public const string Rename = "sidebar.customizer.undo.renameSection";
        public const string Duplicate = "sidebar.customizer.undo.duplicateSection";
        public const string RemoveSection = "sidebar.customizer.undo.removeSection";
        public const string Show = "sidebar.customizer.undo.showSection";
        public const string ExtensionManage = "sidebar.extension.manage";
        public const string ExtensionKind = "sidebar.section.extension";
        public const string ItemCount = "sidebar.v3.itemCount";
        public const string SearchPlaceholder = "sidebar.v3.searchPlaceholder";
        public const string SearchEmpty = "sidebar.v3.empty.search";
        public const string LibraryEmpty = "sidebar.v3.empty.library";
        public const string SavedLocally = "sidebar.customizer.savedLocally";
        public const string SectionCount = "sidebar.customizer.sectionCount";
        public const string PaletteSearch = "sidebar.customizer.paletteSearch";
        public const string PaletteEmpty = "sidebar.customizer.paletteEmpty";
        public const string PaletteNavigation = "sidebar.palette.navigation";
        public const string PaletteLibrary = "sidebar.palette.library";
        public const string AppendsTo = "sidebar.customizer.appendsTo";
        public const string ContributionUnnamed = "sidebar.customizer.contributionUnnamed";
        public const string Designs = "sidebar.customizer.designs";
        public const string DesignsSub = "sidebar.customizer.designsSub";
        public const string ShowContents = "sidebar.customizer.editShowContents";
        public const string HiddenSections = "sidebar.customizer.hiddenSections";
        public const string HiddenSectionsSub = "sidebar.customizer.hiddenSectionsSub";
        public const string HiddenNone = "sidebar.customizer.hiddenNone";
        public const string UnknownSection = "sidebar.customizer.unknownSection";
        public const string Advanced = "sidebar.customizer.advanced";
        public const string LayoutFile = "sidebar.customizer.layoutFile";
        public const string LayoutFileSub = "sidebar.customizer.layoutFileSub";
        public const string ShowFile = "sidebar.customizer.showFile";
        public const string PinEmptyHint = "sidebar.pin.emptyHint";
        public const string ShowInRailSub = "sidebar.option.showInRailSub";
        public const string MaxItemsAll = "sidebar.option.maxItemsAll";
        public const string Sort = "sidebar.option.sort";
        public const string Descending = "sidebar.option.descending";
        public const string Qualifier = "sidebar.option.qualifier";
        public const string ArtistUnset = "sidebar.source.artistTopTracks.unset";
        public const string FilterPlaylists = "sidebar.v3.filter.playlists";
        public const string FilterAlbums = "sidebar.v3.filter.albums";
        public const string FilterArtists = "sidebar.v3.filter.artists";
        public const string FilterPodcasts = "sidebar.v3.filter.podcasts";
        public const string KindPlaylist = "sidebar.v3.kind.playlist";
        public const string KindArtist = "sidebar.v3.kind.artist";
        public const string Playlists = "sidebar.playlists";
    }
}
