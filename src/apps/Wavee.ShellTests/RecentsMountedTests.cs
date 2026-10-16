// ── Wavee.ShellTests/RecentsMountedTests.cs — the Recents page's view, mounted (themes plan 6.1, T10c2) ───────────────────────────────────────
//
// The Recents page's masthead, pivot strip, skeleton region, grouped list, rail, rows and calendar are the structure's since T10c2; `Recents.PageView` keeps the model
// and the verbs (`Wavee.Tests` pins the pure ones: `RecentsContractTests`). The view reads the host only through `IRecentsHost`, so a fixture host stands in for it:
//
//   registered   Classic is the registered template of `Pages.Recents`
//   pending      the pivot strip is keyed on the shape epoch and the core leaves ride along; the semantic surface is NOT mounted (the shimmer is derived)
//   empty        an answered, empty relation shows the vacancy instead of the semantic surface
//   loaded       an answered relation mounts the keyed semantic surface, its list and its rail

using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Scene;
using FluentGpu.Signals;
using Wavee;
using Wavee.Themes.Classic;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Xunit;

namespace Wavee.ShellTests;

public sealed class RecentsMountedTests(ShellHost host)
{
    static ViewHarnessOptions UnderClassic() => new() { Composition = ClassicTheme.Create() };

    static RecentsShape ShapeOf(RecentsSnapshot rows, bool loaded)
    {
        var now = DateTimeOffset.Now;
        var culture = CultureInfo.CurrentCulture;
        var map = RecentsView.Filter(rows, null);
        var sections = RecentsView.BuildSections(rows, map, now, culture);
        var layout = new GroupedListVirtualLayout(sections.HeaderIndices, RecentsLayout.DateHeaderHeight, RecentsLayout.RowHeight);
        layout.ContentExtent(sections.Items.Length, 0f);
        return new RecentsShape(rows, map, RecentsView.FirstOccurrence(rows), sections, RecentsView.DayDensity(rows, map, now, culture),
            layout, now.Offset, loaded, summary: "");
    }

    /// <summary>The page host's seam with nothing behind it: every read answers "nothing known", every verb does nothing.</summary>
    sealed class FixtureHost(RecentsShape seedShape) : IRecentsHost
    {
        readonly Signal<RecentsShape> _shape = new(seedShape);
        readonly Signal<int> _zero = new(0);
        readonly Signal<string?> _chip = new(null);
        readonly Signal<StringId> _expanded = new(StringId.Empty);
        readonly Signal<int> _sticky = new(-1);
        readonly Signal<Design.PageAccent> _accent = new(new Design.PageAccent(ColorF.FromRgba(30, 144, 255, 255), ColorF.FromRgba(30, 144, 255, 255), ""));
        readonly Signal<DateOnly> _day = new(DateOnly.FromDateTime(DateTime.Now));
        readonly Signal<float> _push = new(0f);

        public RecentsShape ShapeNow => seedShape;
        public RecentsShape PendingShape => seedShape;
        public IReadSignal<RecentsShape> ShapeSignal => _shape;
        public IReadSignal<int> Epoch => _zero;
        public IReadSignal<int> ShapeEpoch => _zero;
        public IReadSignal<string?> Chip => _chip;
        public IReadSignal<StringId> ExpandedRow => _expanded;
        public IReadSignal<int> StickyHeader => _sticky;
        public IReadSignal<int> RailMeasuredVersion => _zero;
        public IReadSignal<Design.PageAccent> Accent => _accent;
        public Signal<bool> IsZoomedOut { get; } = new(false);
        public IReadSignal<DateOnly> CalendarDay => _day;
        public CultureInfo Culture => CultureInfo.CurrentCulture;
        public DateOnly Today => DateOnly.FromDateTime(DateTime.Now);
        public string ScrollScope => "test:recents:";
        public ItemsViewController ListController { get; } = new();
        public ItemsViewController CalendarController { get; } = new();
        public AnnotatedScrollBarController ScrollController { get; } = new();
        public SemanticZoomController ZoomController { get; } = new();
        public Prop<ColorF> AccentInkProp => Prop.Of(() => _accent.Value.Ink);
        public Prop<ColorF> AccentFillProp => Prop.Of(() => _accent.Value.Fill);
        public Prop<Affine2D> StickyTransform => Prop.Of(() => Affine2D.Translation(0f, _push.Value));
        public Prop<ColorF> DensityFillProp(int level) => ColorF.Transparent;
        public Action WatchStickyAction { get; } = static () => { };
        public Action<WheelEventArgs> ForwardWheelAction { get; } = static _ => { };
        public Action ResetCalendarDayAction { get; } = static () => { };
        public Func<int, int> MapInToOutFunc { get; } = static i => i;
        public Func<int, int> MapOutToInFunc { get; } = static i => i;
        public Func<int, int> ContentTypeFunc { get; } = static _ => 0;
        public Action<int, RecentsFlatItem> InvokeFlatAction { get; } = static (_, _) => { };
        public Func<int, RecentsFlatItem, string> TextForFunc { get; } = static (_, _) => "";
        public Func<AnnotatedScrollBarLabel[]> RailLabelsFunc { get; } = static () => [];
        public Func<float[]> RailTicksFunc { get; } = static () => [];
        public Func<float, AnnotatedScrollBarLabel?> RailDetailFunc { get; } = static _ => null;

        public void SelectPivot(string? token) { }
        public void ToggleExpanded(int row) { }
        public void Open(int row) { }
        public void Play(int row) { }
        public ContextMenuModel? MenuFor(int row) => null;
        public DragPayload? DragPayloadFor(int row) => null;
        public void PlayContext(EntityId id) { }
        public void InvokeDay(int dayIndex) { }
        public void JumpIn(DateOnly date, int monthIndex) { }
        public void FocusDay(bool focused, DateOnly date) { }
        public void HoverDay(DateOnly date) { }

        public RecentsFacts FactsOf(EntityRef target) => RecentsFacts.Unknown;
        public string? CoverOf(EntityRef target) => null;
        public void SubscribeRow(RecentsShape shape, int row) { }
        public string UriOf(RecentsShape shape, int row, EntityRef target) => "";
        public string WhenOf(RecentsShape shape, int row) => "";
        public string? MorphOf(RecentsShape shape, int row, EntityRef target, string uri) => null;
        public string SubtitleOf(RecentsShape shape, int row, in RecentsEdge edge, string? owner) => owner ?? "";
        public string CountLabel(int count) => count.ToString(CultureInfo.InvariantCulture);
        public string PlayedAt(long playedAtMs) => "";
        public string CalendarReadout(RecentsShape shape, DateOnly date) => "";
        public string CalendarTooltip(RecentsShape shape, DateOnly date) => "";
    }

    static RecentsPageProps Props(IRecentsHost fixture, bool pending, bool failed = false)
        => new()
        {
            Host = fixture,
            Summary = "",
            ShapeEpoch = 0,
            Pending = () => pending,
            Failed = () => failed,
            Retry = static () => { },
            OpenOverview = static () => { },
            OnKeyDown = static _ => { },
            PageAccent = null,
            AccentBinder = new BoxEl { Key = "recents-accent" },
            OwnerDemand = new BoxEl { Key = "recents-owners" },
        };

    [Fact]
    public void Classic_is_the_registered_template_of_the_recents_page()
        => host.Run(() => Assert.Equal("classic.recents.page", ThemeHost.Registry.TemplateIdOf(ViewKeys.RecentsPage.Id)));

    [Fact]
    public void A_pending_page_keeps_the_pivot_strip_and_the_core_leaves_and_mounts_no_surface()
        => ViewHarness.With(host, ViewKeys.RecentsPage, Props(new FixtureHost(ShapeOf(RecentsView.PendingSeedRows(DateTimeOffset.Now), loaded: false)), pending: true),
            harness =>
            {
                harness.RunFrames(30);
                Assert.True(harness.Contains("recents-pivots:0:" + (int)WidthTier.Standard), "the pivot strip is keyed on the shape epoch");
                Assert.True(harness.Contains("recents-accent"), "the accent binder leaf rides in the page");
                Assert.True(harness.Contains("recents-owners"), "the owner demand leaf rides in the page");
                Assert.False(harness.Contains("recents-semantic:all:0"), "the shimmer is derived: the stateful surface never mounts while pending");
            }, UnderClassic());

    [Fact]
    public void An_answered_empty_relation_shows_the_vacancy_and_not_the_surface()
        => ViewHarness.With(host, ViewKeys.RecentsPage, Props(new FixtureHost(ShapeOf(RecentsSnapshot.Empty, loaded: true)), pending: false),
            harness =>
            {
                harness.RunFrames(30);
                Assert.True(harness.Contains("recents-pivots:0:" + (int)WidthTier.Standard));
                Assert.False(harness.Contains("recents-semantic:all:0"), "no rows: the display sentence stands alone");
            }, UnderClassic());

    [Fact]
    public void An_answered_relation_mounts_the_keyed_surface_with_its_list_and_rail()
        => ViewHarness.With(host, ViewKeys.RecentsPage, Props(new FixtureHost(ShapeOf(RecentsView.PendingSeedRows(DateTimeOffset.Now), loaded: true)), pending: false),
            harness =>
            {
                harness.RunFrames(60);
                Assert.True(harness.Contains("recents-semantic:all:0"), "the semantic surface is keyed on the pivot and the shape epoch");
                Assert.True(harness.Contains("recents-list:all:0"), "the zoomed-in list");
                Assert.True(harness.Contains("recents-rail:all:0"), "the annotated rail beside it");
            }, UnderClassic());
}
