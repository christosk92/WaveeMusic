// ── Entities/Show.Pane.cs ──────────────────────────────────────────────────────────────────────────────────────────
// A1 (plan §3.5, report 2c/2d): the library pane's right column for a show/audiobook — the sibling of Album.Pane. The
// header never skeletons past Knows(ShowFields.Title) (Album's own rule), the body below it is the SAME
// Show.EpisodeReaderBody the route page mounts (Show.Page.cs), so a click into the library and the standalone show
// page render byte-identical rows.
//
// Role: UI
// Wave: A1 (S-reader)
// Spec: plan §3.5 — the CONTRACT A2 (library) builds against: `Show.Pane : Component` + `Show.PaneProps(int Slot)`,
//       mounted from User.Page.Library.cs WITHOUT a Key (re-skins in place, Album's own idiom).
//
// ── THE PANE RE-SKINS IN PLACE (reports 2c / 2d) ─────────────────────────────────────────────────────────────────────
//
// A selection change must NOT remount this pane. The keys on the two arms are therefore the ARM's ("showpane:body" /
// "showpane:skel"), never the slot's: the header, the commands and the reader are re-skinned by their own props while
// the component, its `ReaderModel` memos and the reader's scroll position all stay alive. The `s_paneSwap` transition
// stays for the ONE swap that is a real shape change — the header skeleton giving way to the body.
//
// The READER keeps a key of its own, on the slot: a different show is a different list, and its scroll position, its
// bound source and its frozen word rail are all per-model. That is an identity remount (Album.Pane's own rule), not the
// pane remounting; an unrelated re-render (a title landing, a progress tick, the library resizing) never changes it,
// so the reader does not jump back to the top.
//
// ── …WHICH IS WHY THE SELECTION IS A SIGNAL, AND THE MEMOS ARE THE PANE'S, NOT THE MODEL'S ────────────────────────────
//
// Re-skinning in place has ONE cost, and it was a shipped bug: `UseComputed` builds its `Memo` from the delegate it is
// given on the FIRST call at that source location and DISCARDS the argument on every later render (engine,
// `Hooks/RenderContext.cs` UseComputed). So the old shape — swap `_m` for a fresh `ReaderModel` in `Render`, then call
// `UseComputed(m.ComputeSnap)` — left both memos bound to the FIRST model's closure for the life of the component: the
// header re-skinned (it reads `new Show(_slot)` directly) while the meta line, the filter counts, the continue card,
// the up-next chips and the whole episode list stayed on the first show ever shown.
//
// The cure is two halves, and it needs both:
//   1. THE DELEGATES ARE THE PANE'S and stable — `ComputeSnap`/`ComputeFacts` below are bound once in the constructor
//      and resolve the CURRENT model at call time. `UseComputed` therefore keeps exactly the memo pair it built at
//      mount, and every downstream reader (the slots, the reader host, `_heartShown`) stays subscribed to that ONE pair
//      across a selection change — which is also why each new `ReaderModel` is handed the SAME `Snap`/`Facts` memos.
//   2. THE SELECTION IS A TRACKED SIGNAL — `_model`. A plain field is invisible to the reactive core, so a field swap
//      alone would never invalidate the memos; writing `_model` marks them stale, and `Memo.Recompute` re-tracks its
//      dependencies from scratch (`RunComputation`), so after the swap the memo depends on the NEW model's
//      Status/Order/Find/paging signals and not the old one's.
// The write happens in `ApplyProps` (this pane is an `IPropsHost`), i.e. at the reconciler's props seam, OUTSIDE render
// and inside its `Runtime.Batch` — never during `Render`, which would loop (engine rule 6).

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

public readonly partial struct Show
{
    /// <summary>A2's mount contract (plan §3.5): <c>Embed.Comp(new Show.PaneProps(slot), static () => new Show.Pane())</c>
    /// — NO <c>Key</c>, so a selection change re-skins this component in place instead of remounting it.</summary>
    public sealed record PaneProps(int Slot);

    public sealed class Pane : Component, IPropsHost
    {
        /// <summary>The selection swap — byte-identical to <see cref="Album.Pane"/>'s <c>s_paneSwap</c> (see the file
        /// header): the leaving pane fades out under the entering one, which rises 6 DIP in.</summary>
        static readonly LayoutTransition s_paneSwap = new(
            TransitionChannels.Opacity, TransitionDynamics.Tween(180f, Easing.SmoothOut),
            Enter: new EnterExit(Dy: 6f, Opacity: 0f, Active: true), Exit: new EnterExit(Opacity: 0f, Active: true),
            ExitDynamics: TransitionDynamics.Tween(120f, Easing.SmoothOut));

        /// <summary>The two arms' keys are the ARM's, not the show's — the pane re-skins in place (see the file
        /// header). Only the reader is keyed per show, because a different show is a different list.</summary>
        const string BodyKey = "showpane:body", SkelKey = "showpane:skel";

        int _slot = -1, _pendingSlot = -1;
        /// <summary>THE SELECTION, as a tracked signal (file header §2): the ONE thing a slot change writes, and the
        /// dependency both memos below read first, so a swap really does invalidate them. Never written during render.</summary>
        readonly Signal<ReaderModel?> _model = new(null);
        /// <summary>The pane's ONE memo pair, built at mount and handed to every model the pane ever selects — so a
        /// selection change re-points the memos' SOURCE without re-pointing anybody's subscription to them.</summary>
        Memo<ReaderSnap>? _snap;
        Memo<PageFacts>? _facts;
        string _readerKey = "", _scrollKey = "", _uri = "";
        readonly Func<ReaderSnap> _snapOf;
        readonly Func<PageFacts> _factsOf;
        readonly Func<ContextMenuModel?> _more;
        readonly Action _demandShow, _open, _swap;
        readonly Prop<bool> _heartShown;

        public Pane()
        {
            Controls.Library ??= User.LibrarySeam;
            _snapOf = ComputeSnap;
            _factsOf = ComputeFacts;
            _swap = SwapModel;
            _demandShow = DemandShow;
            _more = MoreMenu;
            _open = () => { var s = new Show(_slot); if (s.IsValid) Shell.GoTo(Shell.For(s.Uri, s.Title)); };
            _heartShown = Prop.Of(() => _facts is { } f && f.Value.Primary != ShowReaderRules.Primary.Follow);
        }

        /// <summary>The props seam (<see cref="IPropsHost"/>): the reconciler calls this at mount and on every re-push,
        /// OUTSIDE render and inside its own <c>Runtime.Batch</c>. A changed slot builds the new <see cref="ReaderModel"/>,
        /// hands it the pane's memo pair, and publishes it through <see cref="_model"/> — the single tracked write that
        /// re-renders this pane in place AND makes both memos recompute against the new model (file header §2).</summary>
        public void ApplyProps(object props)
        {
            var p = (PaneProps)props;
            if (_slot == p.Slot && _model.Peek() is not null) return;   // a data-equal re-push is free
            _pendingSlot = p.Slot;
            // This runs INSIDE the parent's render computation, so an untracked call is what keeps the model's
            // construction (the entity row, the persisted-view seed) from subscribing the LIBRARY PAGE to this pane's
            // reads. The signal WRITE inside still notifies — Untrack gates subscription, not notification.
            Reactive.Untrack(_swap);
        }

        void SwapModel()
        {
            _slot = _pendingSlot;
            var subject = new Show(_slot) is { IsValid: true } s ? s.Uri : default;
            string n = FormatCache.Int(_slot);
            _readerKey = "showpane:reader:" + n; _scrollKey = "lib:episodes:" + n;
            _uri = subject.IsValid ? subject.Text : "";
            // Snap/Facts are null only before the first render builds them; Render assigns them then (and again after,
            // which is a no-op). Everything downstream reads the model THROUGH these, so they must be the pane's pair.
            _model.Value = new ReaderModel(subject) { Snap = _snap, Facts = _facts };
        }

        public override Element Render()
        {
            uint epoch = Entities.ScopeEpoch.Value;
            var scope = Entities.Current;
            _ = scope.Shows.Changed.Value;
            _ = scope.Edges.ShowEpisodes.Changed.Value;

            if (_model.Value is not { } m)                              // mounted propless — ApplyProps never ran
                throw new InvalidOperationException(
                    "Show.Pane was mounted without props; embed it as Embed.Comp(new Show.PaneProps(slot), () => new Show.Pane()).");
            // The delegates are the PANE's and stable, so these two calls keep the memo pair they built at mount — the
            // engine discards the argument after the first call at this site (file header §2).
            m.Snap = _snap = UseComputed(_snapOf);
            m.Facts = _facts = UseComputed(_factsOf);
            var show = new Show(_slot);
            UseEffect(_demandShow, DepKey.From(_slot, (int)epoch));

            // Gated ONLY on the title (report 2d): never on the episode edge — the navigator that mounted this pane
            // already resolved the row it shows, so there is nothing header-shaped left to wait for.
            bool headerReady = show.IsValid && show.Knows(ShowFields.Title);
            BoxEl child = !headerReady ? HeaderSkeleton() : Body(show, m);
            return new BoxEl
            {
                ZStack = true, Grow = 1f, Basis = 0f, MinHeight = 0f, ClipToBounds = true,
                Children = [child with { Key = headerReady ? BodyKey : SkelKey, Animate = s_paneSwap }],
            };
        }

        void DemandShow()
        {
            var show = new Show(_slot);
            if (!show.IsValid) return;
            Entities.Ensure(show, ShowFields.All);
            var edge = Entities.Current.Edges.ShowEpisodes;
            if (edge.State(_slot) == EdgeState.Unknown && !edge.IsFailed(_slot))
                Entities.EnsureEdge(FetchEdge.ShowEpisodes, _slot);
        }

        BoxEl Body(Show show, ReaderModel m)
        {
            var f = m.Facts?.Value ?? default;
            var id = IdentityOf(show, in f);
            Element header = PaneHeader(id.CoverUrl, id.Eyebrow, id.Title, _open,
                new TextEl(Entities.Strings.Resolve(show.PublisherId))
                {
                    Size = 13.5f, LineHeight = 20f, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                },
                id.Meta ?? "");
            Element commands = new BoxEl
            {
                Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, Padding = new Edges4(Spacing.XL, Spacing.S, Spacing.XL, Spacing.M),
                Children =
                [
                    Embed.Comp(new SlotProps(m, () => Tok.AccentDefault), static () => new PrimarySlot()) with { Key = "showpane:primary" },
                    new BoxEl { Visible = _heartShown, Children = [Detail.RailSatelliteSave(_uri, id.Title)] },
                    Detail.RailSatelliteMore(_more),
                ],
            };
            return new BoxEl
            {
                Direction = 1, Grow = 1f, ClipToBounds = true,
                Children = [header, commands, new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, MinHeight = 0f, ClipToBounds = true,
                    // The pane hosts the SAME reader the route page mounts (Show.Page.cs) — one list stack, not a
                    // pane-only twin. The scroll key scopes its position per library slot.
                    Children = [Embed.Comp(new ReaderProps(m, _scrollKey), static () => new ReaderHost()) with { Key = _readerKey }] }],
            };
        }

        /// <summary>The snapshot memo's body: it reads <see cref="_model"/> FIRST (so a selection change invalidates it
        /// and <c>Memo.Recompute</c> re-tracks against the new model's signals) and then delegates to whichever model is
        /// current — never to one captured in a closure.</summary>
        ReaderSnap ComputeSnap() => _model.Value is { } m ? m.ComputeSnap() : ReaderSnap.Empty;

        /// <inheritdoc cref="ComputeSnap"/>
        PageFacts ComputeFacts()
        {
            if (_model.Value is not { } m) return default;
            var show = new Show(_slot);
            bool known = show.IsValid;
            bool followed = Controls.Library is { } lib && m.SubjectText.Length > 0 && lib.IsSaved(m.SubjectText);
            var s = m.Read();
            bool serial = s.Order == ConsumptionOrder.Sequential;
            bool firstKnown = s.FirstSlot > 0;
            var resume = new Episode(s.ResumeSlot);
            bool resumable = s.ResumeSlot > 0 && resume.IsValid;
            return new PageFacts(
                Head: s.Head,
                Primary: ShowReaderRules.PrimaryOf(s.Head, followed, serial, firstKnown, resumable),
                Ghost: ShowReaderRules.GhostOf(serial, firstKnown),
                ResumeLeft: resumable ? Episode.LeftMinutes(resume.ProgressMs, resume.DurationMs) : 0,
                FirstNumber: 1,
                Publisher: known && show.Knows(ShowFields.Publisher) && !show.PublisherId.IsEmpty,
                Flags: known ? show.Flags : ShowFlags.None,
                RatingX100: 0, RatingCount: 0, MyRating: 0,
                HasTrailer: false,
                ToneArgb: known && show.Knows(ShowFields.Appearance) ? show.Tone : 0u,
                Meta: show.IsAudiobook ? Strings.Podcast.Reader.ChaptersCount(s.Total) : MetaOf(s.Total, s.Cadence, s.OldestYear),
                Topics: false);
        }

        ContextMenuModel? MoreMenu()
        {
            var show = new Show(_slot);
            if (!show.IsValid) return null;
            var ctx = new ActionContext(ActionTarget.ForShow(show.Uri, show.Knows(ShowFields.Title) ? show.Title : ""), Actions.Services);
            var rows = new List<MenuFlyoutItem>(4);
            if (Actions.Menu.Row(ActionId.PlayContextNext, in ctx) is { } next) rows.Add(next);
            if (Actions.Menu.Row(ActionId.AddContextToQueue, in ctx) is { } queue) rows.Add(queue);
            Actions.Menu.Group(rows, Episode.ShareMenu(show.Uri, in ctx));
            return rows.Count == 0 ? null : new ContextMenuModel(rows);
        }

        /// <summary>Gated ONLY on <c>Knows(ShowFields.Title)</c> — Album's "header never skeletons" rule (Album.Pane.cs
        /// §"THE HEADER NEVER SKELETONS"): the navigator that mounts this pane already resolved the row it shows, so
        /// there is nothing header-shaped left to wait for.</summary>
        static BoxEl HeaderSkeleton() => new()
        {
            Direction = 0, Gap = 18f, AlignItems = FlexAlign.End, Height = Album.PaneHeaderHeight, ClipToBounds = true,
            Padding = new Edges4(Spacing.XL, Spacing.XL, Spacing.XL, Spacing.M),
            Children =
            [
                new BoxEl { Width = Album.PaneCover, Height = Album.PaneCover, Shrink = 0f, Corners = Radii.CardAll, Fill = Tok.FillSubtleSecondary },
                new BoxEl
                {
                    Direction = 1, Gap = Spacing.S, Grow = 1f, Basis = 0f, MinWidth = 0f,
                    Children =
                    [
                        new BoxEl { Width = 80f, Height = 11f, Corners = CornerRadius4.All(4f), Fill = Tok.FillSubtleSecondary },
                        new BoxEl { Width = 220f, Height = 26f, Corners = CornerRadius4.All(4f), Fill = Tok.FillSubtleSecondary },
                        new BoxEl { Width = 120f, Height = 11f, Corners = CornerRadius4.All(4f), Fill = Tok.FillSubtleSecondary },
                    ],
                },
            ],
        };
    }
}
