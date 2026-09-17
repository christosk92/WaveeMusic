// ── Entities/Home.Customizer.cs ────────────────────────────────────────────────────────────────────────────────────
// the home-customize page (the layout document, reducer and commands are Home.cs; the json store and HomePreferences
// are Home.Host.cs)
//
// Role: UI
// Owner: P
// Wave: 5
// Budget: 300 lines
// Spec: ch 12 §1c/§1d, W13-W20, §3b, §6b, §7b, §9 traps 4-7 · 11 · 12, §10 items 37-64 and 77-90
//
// A PAGE, NOT A DIALOG (ch 12 §0.11): 64-DIP command bar · 1-DIP divider · fault-banner slot · ONE scrolling column,
// 720-capped and LEFT-aligned, of 48-DIP rows over the LIVE document. No OK/Cancel, no staged copy, no undo/toast/reject
// feedback (§0.20 — the `home.customizer.undo.*` keys stay, unrendered). Split so a toggle or a drag re-renders only what
// it touches: CustomizerView (run-once shell) · CustomizerBanner · CustomizerList (the Reorderable) · CustomizerRow.
//
// 0.3 corrections, each deliberate:
//   trap 4  the banner's dismiss is a UseSignal, not a plain field + epoch bump.
//   trap 5  the row root carries Grow/Shrink/MinWidth, so `Reorderable.Item`'s ROW wrapper cannot measure it to 198 DIP
//           with a zero-width label (the sidebar's FillSlot rule; a component anchor mirrors its root's flex).
//   trap 6  0.2.9's Stationary 0.4 dim is kept AND the gesture gets its moving visual: a module chip through the shell's
//           ONE resolver (`Drag.ReorderChip`). Not the ghost lift — these rows have no fill, so a ghost reads its label
//           straight through its neighbours' (the engine's S3 warning on DragVisualStyle.Backplate).
//   trap 7  AnnounceText is wired: the keyboard lift is audible.
//   trap 11 ACCEPTED: ReorderList's slot math needs a fixed ItemExtent, so bar and rows keep hard Heights and clip at
//           ≥150 % text scale exactly as 0.2.9 did.
//   trap 12 onto the ramp: title 16/600 → Ui.BodyStrong 14/20/600 (the nearest rung keeping the weight), hint →
//           Ui.Caption 12/16 tertiary, row label → Ui.Body 14/20.
//   W16     a "Start fresh" whose rename threw keeps the banner up (the store still blocks writes).
// No authored motion: the hidden dim is a hard swap (§5); toggle, FLIP and page motion are the engine's and the shell's.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;

namespace Wavee;

public readonly partial struct Home
{
    /// <summary>The <c>home-customize</c> route's page. The route's arg is only a keep-alive discriminator; nothing here
    /// reads it. The document is <c>HomePreferences.Current</c> — never a context slot.</summary>
    public static Element CustomizerPageFor(in Shell.Route route)
    {
        Drag.ReorderChip ??= ModuleChip;
        return Embed.Comp(static () => new CustomizerView());
    }

    /// <summary>A module mid-drag: the label composed at promotion, the gripper mark and the resting verb. A lookup, never
    /// a build — the resolver runs inside the zero-alloc frame region.</summary>
    static DragChipSpec? ModuleChip(DragState state)
        => string.Equals(state.Kind, CustomizerList.DragKind, StringComparison.Ordinal)
           && state.Payload is ReorderPayload { Item: CustomizerDragItem item }
            ? new DragChipSpec(Title: item.Label, Glyph: Icons.GripperBar, RestingCaption: item.Caption)
            : null;

    /// <summary>What a lifted module carries (<c>ReorderPayload.Item</c>), built ONCE at promotion.</summary>
    sealed record CustomizerDragItem(string Label, string Caption);

    // The banner predicate and the label table are CORE (`Home.Rules.cs` §15, tested in HomeLayoutTests).

    /// <summary>A module's label, resolved LIVE (a language change re-resolves on the next read).</summary>
    static string CustomizerLabel(HomeGroupKind kind)
        => CustomizerLabelOf(kind, HomeModuleCopy.Titles, Loc.Get(Strings.Home.Customizer.Hero),
            Loc.Get(Strings.Home.Customizer.WeeklyPair));

    // ══ the shell ═══════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Run-once: every live read sits in a child, so neither a toggle nor a drag rebuilds the command bar.</summary>
    sealed class CustomizerView : Component
    {
        const float HeaderHeight = 64f, ColumnMaxWidth = 720f;

        public override Element Render() => new BoxEl
        {
            Key = "home-customizer", Grow = 1f, Shrink = 1f, Direction = 1, MinWidth = 0f, MinHeight = 0f,
            ClipToBounds = true,
            Children =
            [
                CommandBar(),
                Ui.Divider(),
                Embed.Comp(static () => new CustomizerBanner()) with { Key = "banners" },
                Ui.ScrollView(new BoxEl
                {
                    Direction = 1, Gap = Spacing.L, MaxWidth = ColumnMaxWidth, MinWidth = 0f,
                    Padding = new Edges4(Spacing.L, Spacing.M, Spacing.L, Spacing.XL),
                    Children =
                    [
                        Ui.Caption(Loc.Get(Strings.Home.Customizer.HiddenHint)).Tertiary() with
                        {
                            Wrap = TextWrap.Wrap, MaxLines = 3,
                        },
                        Embed.Comp(static () => new CustomizerList()),
                    ],
                }) with
                {
                    Key = "home-customizer-column", Grow = 1f, Shrink = 1f, MinHeight = 0f, MinWidth = 0f,
                    AutoEdgeFade = true, ScrollKey = "home.customizer",
                },
            ],
        };

        /// <summary>Back · eyebrow + title · Reset · Done. The title lane is the only flexible child, so under pressure
        /// both lines ellipsize and the buttons hold their size (W17).</summary>
        static Element CommandBar() => new BoxEl
        {
            Key = "cmdbar", Direction = 0, Height = HeaderHeight, Shrink = 0f, Gap = Spacing.S,
            AlignItems = FlexAlign.Center, Padding = new Edges4(Spacing.S, 0f, Spacing.L, 0f),
            Children =
            [
                // The plain wrapper is load-bearing: ToolTip's root sets AlignSelf = Start, which would pin the arrow to
                // the top of the 64-DIP band (the sidebar customizer's note).
                new BoxEl
                {
                    Shrink = 0f,
                    Children =
                    [
                        ToolTip.Wrap(IconButton.Create(Icons.Back, GoBack, size: ControlSize.Small) with { Shrink = 0f },
                            Loc.Get(Strings.Home.Customizer.Back)),
                    ],
                },
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Basis = 0f, Shrink = 1f, MinWidth = 0f, Justify = FlexJustify.Center,
                    Children =
                    [
                        global::Wavee.Design.Type.Eyebrow(Loc.Get(Strings.Home.Customizer.Eyebrow)) with
                        {
                            Color = global::Wavee.Design.Accent.Decor, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                        },
                        Ui.BodyStrong(Loc.Get(Strings.Home.Customizer.Title)) with
                        {
                            MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                        },
                    ],
                },
                new BoxEl
                {
                    Direction = 0, Shrink = 0f, Gap = Spacing.XS, AlignItems = FlexAlign.Center,
                    Children =
                    [
                        Button.Create(Loc.Get(Strings.Home.Customizer.Reset), Reset, ButtonAppearance.Subtle,
                            ControlSize.Small) with { Shrink = 0f },
                        Button.Create(Loc.Get(Strings.Home.Customizer.Done), GoBack, ButtonAppearance.Accent,
                            ControlSize.Small) with { Shrink = 0f },
                    ],
                },
            ],
        };

        /// <summary>Reset → the default document (all 12, designed order, all visible). No confirmation (§6b), and on an
        /// already-default document it still writes (W20 C) — the reducer's verdict, ported as is.</summary>
        static void Reset() => HomePreferences.Current.Dispatch(new ResetHomeLayout());

        /// <summary>Back and Done (ch 12 §6b): flush the one pending write BEST EFFORT — ≤200 ms, the bool is ignored
        /// (§0.15) — then the shell's real Back; with no back stack the newest non-customizer visit; Home last.</summary>
        static void GoBack()
        {
            _ = HomePreferences.Current.WaitForWrites(200);
            if (Shell.CanBack.Peek())
            {
                Shell.GoBack();
                return;
            }
            var log = Shell.History.Store.Entries;
            for (int i = log.Count - 1; i >= 0; i--)
            {
                var route = log[i].Route;
                if (route.Kind == Shell.RouteKind.HomeCustomize) continue;
                Shell.GoTo(route);
                return;
            }
            Shell.GoTo(new Shell.Route(Shell.RouteKind.Home));
        }
    }

    // ══ the fault banner ════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>ONE sentence for all three load faults (§6b — FaultDetail is captured and not shown, as shipped). The quiet
    /// slot is a zero-height box, so dismissing never remounts anything below.</summary>
    sealed class CustomizerBanner : Component
    {
        public override Element Render()
        {
            var prefs = HomePreferences.Current;
            _ = prefs.LayoutVersion.Value;   // DiscardCorrupt bumps it
            var dismissed = UseSignal(false);
            if (!CustomizerShowsFaultBanner(prefs.Fault, prefs.WritesBlocked, dismissed.Value))
                return new BoxEl { Height = 0f, Shrink = 0f };

            return new BoxEl
            {
                Direction = 1, Shrink = 0f, Padding = new Edges4(Spacing.L, Spacing.S, Spacing.L, 0f),
                Children =
                [
                    InfoBar.Create(
                        InfoBarSeverity.Warning,
                        Loc.Get(Strings.Home.Customizer.Corrupt),
                        Loc.Get(Strings.Home.Customizer.CorruptSub),
                        onClose: () => dismissed.Value = true,
                        actionButton: Button.Create(Loc.Get(Strings.Home.Customizer.FaultDiscard), () =>
                        {
                            HomePreferences.Current.DiscardCorrupt();
                            dismissed.Value = false;   // a failed discard must be SEEN, even after an earlier ✕
                        }, ButtonAppearance.Standard, ControlSize.Small)),
                ],
            };
        }
    }

    // ══ the module list ═════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The reorderable column. Rows render in the PROJECTED order (<c>ItemAt</c>) with stable per-kind keys, so
    /// siblings FLIP out of the way after the 200 ms dwell (LiveProject, the engine default) and there is no insertion
    /// line; a release away from the list commits nothing. The delegates are built once; a drag frame allocates nothing
    /// here, and a slot change re-renders this list alone.</summary>
    sealed class CustomizerList : Component
    {
        internal const float RowExtent = 48f;
        internal const string DragKind = "wavee.home-layout";

        readonly Reorderable _reorder;
        readonly Element?[] _rows = new Element?[32];   // one embedded row per kind, built once (HomeGroupKind is a byte)

        public CustomizerList()
        {
            // The row dims in place (0.4) and the CHIP is the moving visual (trap 6).
            _reorder = new Reorderable(DragKind)
            {
                ItemExtent = RowExtent, Spacing = 0f, RequireDropOnList = true,
                DragStyle = new DragVisualStyle { Lift = DragLift.Stationary, Opacity = FluentGpu.Controls.Drag.SourceDimOpacity },
            };
            _reorder.ItemOf = DragItemAt;
            _reorder.OnReorder = Commit;
            _reorder.AnnounceText = Announce;
        }

        public override Element Render()
        {
            var prefs = HomePreferences.Current;
            _ = prefs.LayoutVersion.Value;
            var modules = prefs.Layout.Modules;
            int n = modules.Count;
            _reorder.Scene = Context.Scene;
            _reorder.RequestRender = Context.RequestRerender;
            _reorder.ItemCount = n;

            var rows = new Element[n];
            for (int i = 0; i < n; i++)
            {
                int item = _reorder.ItemAt(i);
                if ((uint)item >= (uint)n) item = i;
                var kind = modules[item].Kind;
                rows[i] = _reorder.Item(item, RowFor(kind), key: HomeLayoutModules.KindName(kind));
            }
            return _reorder.List(new BoxEl { Direction = 1, MinWidth = 0f, Children = rows });
        }

        Element RowFor(HomeGroupKind kind)
        {
            int k = (int)kind;
            return (uint)k < (uint)_rows.Length ? _rows[k] ??= Build(kind) : Build(kind);

            static Element Build(HomeGroupKind kind)
                => Embed.Comp(() => new CustomizerRow(kind)) with { Key = "mod:" + HomeLayoutModules.KindName(kind) };
        }

        /// <summary>The chip's payload, resolved once per lift (never per frame) with the label in the live language.</summary>
        static object? DragItemAt(int index)
        {
            var modules = HomePreferences.Current.Layout.Modules;
            return (uint)index < (uint)modules.Count
                ? new CustomizerDragItem(CustomizerLabel(modules[index].Kind), Loc.Get("home.customizer.dragCaption"))
                : null;
        }

        /// <summary><c>to</c> is interpreted after the removal (the Reorderable contract); a drop on its own slot never
        /// reaches here, and a clamped no-op is the reducer's to reject (W20 D).</summary>
        static void Commit(int from, int to) => HomePreferences.Current.Dispatch(new MoveHomeModule(from, to));

        /// <summary>The four milestones, composed on the EDGE (never per frame). Index is the ORIGINAL index, which is
        /// still the document's until the commit lands.</summary>
        static string? Announce(ReorderAnnounce a)
        {
            var modules = HomePreferences.Current.Layout.Modules;
            if ((uint)a.Index >= (uint)modules.Count) return null;
            string name = CustomizerLabel(modules[a.Index].Kind);
            string where = Loc.Format("home.customizer.position", ("index", a.Slot + 1), ("count", a.Count));
            return a.Kind switch
            {
                ReorderAnnounceKind.Grab => Loc.Format("home.customizer.reorderGrabbed", ("name", name), ("position", where)),
                ReorderAnnounceKind.Move => Loc.Format("home.customizer.reorderMoved", ("name", name), ("position", where)),
                ReorderAnnounceKind.Drop => Loc.Format("home.customizer.reorderDropped", ("name", name), ("position", where)),
                _ => Loc.Format("home.customizer.reorderCancelled", ("name", name)),
            };
        }
    }

    // ══ one row ═════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Gripper · label · toggle. No hover treatment at all (W14 — a shipped fact). Hidden is DIMMED, never
    /// removed: the whole row drops to 0.55 as a hard swap. The toggle is CONTROLLED against the document: its signal is
    /// written from a layout effect keyed on (hidden, LayoutVersion), so a Reset snaps the pill back.</summary>
    sealed class CustomizerRow : Component
    {
        const float HiddenOpacity = 0.55f;

        readonly HomeGroupKind _kind;                      // frozen on purpose: the row's Key IS its kind
        readonly Signal<bool> _visible = new(true);
        bool _hidden;
        Action? _sync;
        Action<bool>? _onToggle;

        public CustomizerRow(HomeGroupKind kind) => _kind = kind;

        public override Element Render()
        {
            var prefs = HomePreferences.Current;
            int version = prefs.LayoutVersion.Value;
            _hidden = prefs.Layout.IsHidden(_kind);
            UseLayoutEffect(_sync ??= SyncToggle, DepKey.From(_hidden ? 1 : 0, version));

            return new BoxEl
            {
                Direction = 0, Height = CustomizerList.RowExtent, AlignItems = FlexAlign.Center, Gap = Spacing.S,
                // trap 5: FILL the Reorderable.Item row wrapper — the component anchor mirrors these three.
                Grow = 1f, Shrink = 1f, MinWidth = 0f,
                Padding = new Edges4(Spacing.S, 0f, Spacing.S, 0f),
                Opacity = _hidden ? HiddenOpacity : 1f,
                Children =
                [
                    new BoxEl
                    {
                        Width = 12f, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                        HitTestVisible = false,
                        Children = [Ui.Icon(Icons.GripperBar, 12f, Tok.TextTertiary)],
                    },
                    Ui.Body(CustomizerLabel(_kind)) with
                    {
                        Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                        Grow = 1f, Shrink = 1f, MinWidth = 0f,
                    },
                    ToggleSwitch.Create(_visible, _onToggle ??= OnToggle),
                ],
            };
        }

        void SyncToggle() => _visible.SetIfChanged(!_hidden);

        void OnToggle(bool on) => HomePreferences.Current.Dispatch(new SetHomeModuleHidden(_kind, !on));
    }
}
