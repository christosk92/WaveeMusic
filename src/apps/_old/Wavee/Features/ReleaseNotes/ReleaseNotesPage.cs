using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Core;
using Wavee.Core.ReleaseNotes;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>One release as the page renders it: the document, the live issue states over it, and the two rail
/// markers.</summary>
sealed record ReleaseEntry(ReleaseNotesDocument Doc, IssueStateCache? IssueStates, bool IsYou, bool IsUnread);

/// <summary>Everything the page needs, assembled OFF the UI thread and published in one write. A partially-built view
/// is never observable: the loader assigns the whole record or nothing, so the page is either the spinner or a
/// complete page — never a hero with no sections.</summary>
sealed record ReleaseNotesView(
    ReleaseEntry[] Releases,
    HighlightItem[] MergedHighlights,
    ReleaseNotesIndex? Index,
    string SelectedVersion,
    string LastSeen);

/// <summary>One highlight ready to RENDER: the highlight, the document it came from (for its version key), and its
/// poster resolved to a real on-disk path — or null when there is no poster for it.
/// <para>The poster is resolved during the LOAD, not during Render: resolving it means <c>File.Exists</c> on the
/// cache folder, and a render pass that hits the disk once per card turns every hover, scroll and theme change into
/// synchronous file I/O on the UI thread.</para></summary>
sealed record HighlightItem(ReleaseHighlight Highlight, ReleaseNotesDocument Doc, string? Poster);

/// <summary>The <c>whatsnew</c> destination (route arg = the semver to show; absent = the running build, STACKED with
/// every release the reader skipped).
///
/// <para>Reading this page is what marks the notes seen: <c>ReleaseNotesLastSeen</c> advances to the running version on
/// mount, which is what clears the About tab's badge and the rail's unread dots. That is deliberate — the alternative
/// (mark on scroll-to-bottom) leaves a badge lit for people who read the highlights and left.</para></summary>
sealed class ReleaseNotesPage(string? versionArg) : Component
{
    readonly Signal<bool> _onlyLatest = new(false);
    readonly Signal<ReleaseNotesView?> _view = new(null);
    readonly Signal<bool> _loaded = new(false);      // distinguishes "still loading" from "nothing to show"

    public override Element Render()
    {
        var svc = UseContext(Services.Slot);
        var go = UseContext(HistoryStore.NavCtx);
        var overlay = UseContext(Overlay.Service);
        var hooks = UseContext(InputHooks.Current);
        var post = UsePost();

        var view = _view.Value;                       // subscribe → the load publishes here
        bool onlyLatest = _onlyLatest.Value;
        bool loaded = _loaded.Value;

        UseEffect(() =>
        {
            // ORDER IS THE WHOLE FEATURE. lastSeen is read HERE, before the load starts, and carried into it (and out
            // again on the view, for the rail). Advancing the setting first and letting the loader re-read it after
            // its first await handed ReleaseNotesRange.Between the pair (me.Core, me.Core) every single time: the
            // "since you last looked" banner and every unread dot were unreachable by construction.
            string lastSeen = svc?.Settings.Get(WaveeSettings.ReleaseNotesLastSeen) ?? "";

            var cts = new CancellationTokenSource();
            _ = LoadAsync(svc, lastSeen, post, cts.Token);

            // Seen on OPEN, and only now. Written through the settings store (not a signal) because the About badge and
            // the rail both re-read the setting, and this is the one place that advances it.
            svc?.Settings.Set(WaveeSettings.ReleaseNotesLastSeen, AppVersion.Info.Core);
            return () => cts.Cancel();
        }, DepKey.Empty);

        if (view is null)
            return Frame(loaded ? Empty() : SettingsShared.Loading(), rail: null);

        void OpenUrl(string url) => LoginView.OpenUrl(url);
        void Copy(string text)
        {
            hooks.Clipboard?.SetText(text);
            Toast.Show(Loc.Get(Strings.WhatsNew.LinkCopied), new ToastOptions { Severity = InfoBarSeverity.Success });
        }

        var releases = view.Releases;
        bool stacked = releases.Length > 1 && !onlyLatest;
        var shown = stacked ? releases : releases[..1];

        var main = new List<Element>(8)
        {
            ReleaseNotesHero.Create(shown[0].Doc, IsLatest(view, shown[0].Doc), OpenUrl, Copy),
            HighlightStrip.Create(view.MergedHighlights,
                i => HighlightViewer.Open(overlay, view.MergedHighlights, i, go, null)),
        };

        for (int i = 0; i < shown.Length; i++)
        {
            var entry = shown[i];
            if (shown.Length > 1 && i > 0) main.Add(ReleaseDivider(entry.Doc));

            // Notices/Sections are never null here: ReleaseNotesStore.Normalize()s every document it hands
            // out, which is the one owner of the "the JSON said null" repair.
            foreach (var notice in entry.Doc.Notices)
                main.Add(InfoBar.Create(
                    string.Equals(notice.Kind, "breaking", StringComparison.OrdinalIgnoreCase) ? InfoBarSeverity.Warning
                        : string.Equals(notice.Kind, "warning", StringComparison.OrdinalIgnoreCase) ? InfoBarSeverity.Warning
                        : InfoBarSeverity.Informational,
                    notice.Text, "", isClosable: false));

            foreach (var section in entry.Doc.Sections)
            {
                if (section.Items is not { Length: > 0 }) continue;
                main.Add(ChangelogSection.Create(section, entry.IssueStates, OpenUrl,
                    "sec:" + entry.Doc.Version + ":" + section.Kind));
            }
        }

        if (shown[0].Doc.GeneratedAt is { Length: > 0 } generated)
            main.Add(new TextEl(Strings.WhatsNew.AsOf(ReleaseNotesText.Date(generated[..Math.Min(10, generated.Length)])))
                { Size = 11.5f, Color = Tok.TextTertiary, Wrap = TextWrap.Wrap, Margin = new Edges4(0f, 6f, 0f, 0f) });
        main.Add(new BoxEl { Height = 24f, HitTestVisible = false });

        var column = new List<Element>(2);
        if (stacked) column.Add(SinceBanner(releases));
        column.Add(ScrollView(new BoxEl
        {
            Direction = 1, Gap = Spacing.L, MinWidth = 0f, AlignSelf = FlexAlign.Stretch,
            Padding = new Edges4(0f, 0f, 6f, 0f),
            Children = main.ToArray(),
        }) with { Grow = 1f, Shrink = 1f, MinHeight = 0f, ScrollKey = "whatsnew:" + view.SelectedVersion });

        return Frame(
            new BoxEl { Direction = 1, Gap = Spacing.M, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Children = column.ToArray() },
            ReleaseRail.Create(view.Index, view.SelectedVersion, AppVersion.Info.Core,
                view.LastSeen,
                v => go?.Invoke("whatsnew", v)));
    }

    // ── chrome ───────────────────────────────────────────────────────────────────────────────────────────────────────

    static Element Frame(Element body, Element? rail) => new BoxEl
    {
        Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Direction = 1,
        Children =
        [
            new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M,
                Padding = new Edges4(Spacing.PageWide, Spacing.L, Spacing.PageWide, Spacing.M),
                Children =
                [
                    Icon(Icons.Tag, 22f, Tok.TextPrimary),
                    WaveeType.PageHero(Loc.Get(Strings.WhatsNew.Title)) with { Grow = 1f },
                ],
            },
            new BoxEl
            {
                Direction = 0, Gap = 14f, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f,
                Padding = new Edges4(Spacing.PageWide, 0f, Spacing.PageWide, 0f),
                Children = rail is null ? new Element[] { body } : new Element[] { body, rail },
            },
        ],
    };

    static bool IsLatest(ReleaseNotesView view, ReleaseNotesDocument doc)
    {
        var releases = view.Index?.Releases;
        if (releases is null || releases.Length == 0) return true;
        return string.Equals(releases[0]?.Version, doc.Version, StringComparison.Ordinal);
    }

    Element SinceBanner(ReleaseEntry[] releases) => new BoxEl
    {
        Direction = 0, Gap = 10f, AlignItems = FlexAlign.Center, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
        Padding = new Edges4(12f, 8f, 12f, 8f), Corners = CornerRadius4.All(6f),
        Fill = Tok.AccentSubtle, BorderWidth = 1f, BorderColor = Tok.AccentDefault,
        Children =
        [
            Icon(Icons.RefineSparkle, 14f, Tok.AccentTextPrimary),
            new TextEl(Strings.WhatsNew.Since(
                    releases.Length,
                    releases[^1].Doc.Version,
                    releases[0].Doc.Version))
                { Size = 12.5f, Color = Tok.TextPrimary, Grow = 1f, Shrink = 1f, MinWidth = 0f, Wrap = TextWrap.Wrap },
            Button.Create(Loc.Get(Strings.WhatsNew.OnlyLatest), () => _onlyLatest.Value = true,
                ButtonAppearance.Subtle, ControlSize.Small) with { Shrink = 0f },
        ],
    };

    static Element ReleaseDivider(ReleaseNotesDocument doc) => new BoxEl
    {
        Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, AlignSelf = FlexAlign.Stretch,
        Margin = new Edges4(0f, Spacing.S, 0f, 0f),
        Children =
        [
            new TextEl(doc.Version + (doc.Name is { Length: > 0 } n ? "  " + n : "")
                    + (ReleaseNotesText.Date(doc.Date) is { Length: > 0 } d ? "  ·  " + d : ""))
                { Size = 12f, Weight = 600, Color = Tok.TextTertiary, Shrink = 0f },
            new BoxEl { Height = 1f, Grow = 1f, Fill = Tok.StrokeDividerDefault },
        ],
    };

    static Element Empty() => new BoxEl
    {
        Grow = 1f, Direction = 1, Gap = Spacing.M, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Children =
        [
            Icon(Icons.Tag, 32f, Tok.TextTertiary),
            new TextEl(Loc.Get(Strings.WhatsNew.Empty))
                { Size = 13f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxWidth = 360f },
            Button.Standard(Loc.Get(Strings.WhatsNew.OpenOnGitHub),
                () => LoginView.OpenUrl(ReleaseNotesText.RepoUrl + "/releases")),
        ],
    };

    // ── load ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>embedded → cache → release asset, for ONE release or for the whole skipped range; then a budgeted issue
    /// refresh over the newest document. Never throws into the UI: a failure publishes the empty state, which the page
    /// renders as "no notes for this version" plus the GitHub link, because that is a real and recoverable situation
    /// (an old release whose asset was deleted, or a machine that has never been online).</summary>
    async Task LoadAsync(Services? svc, string lastSeen, Action<Action> post, CancellationToken ct)
    {
        var me = AppVersion.Info;
        string selected = versionArg is { Length: > 0 } v ? v : me.Core;
        var store = svc?.ReleaseNotes;
        try
        {
            if (store is null) { post(() => _loaded.Value = true); return; }

            try { await store.RefreshIndexAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch { /* the index is a nicety: the rail hides, the document still loads */ }

            var index = store.IndexSnapshot();

            // The stack is only for the DEFAULT destination. Asking for one version explicitly (the rail, a toast's
            // deep link) means that version and nothing else.
            var wanted = new List<string>(4);
            if (versionArg is { Length: > 0 }) wanted.Add(selected);
            else
            {
                if (index is not null)
                    foreach (var e in ReleaseNotesRange.Between(lastSeen, me.Core, index, me.Channel))
                        if (e?.Version is { Length: > 0 } ev) wanted.Add(ev);
                if (wanted.Count == 0) wanted.Add(selected);
            }

            var entries = new List<ReleaseEntry>(wanted.Count);
            foreach (string want in wanted)
            {
                ct.ThrowIfCancellationRequested();
                var doc = await store.GetAsync(want, ct).ConfigureAwait(false);
                if (doc is null) continue;
                entries.Add(new ReleaseEntry(doc, null,
                    IsYou: string.Equals(doc.Version, me.Core, StringComparison.Ordinal),
                    IsUnread: AppUpdateVersion.IsNewer(doc.Version, lastSeen)));
            }

            if (entries.Count == 0) { post(() => { _loaded.Value = true; _view.Value = null; }); return; }

            var view = Build(entries, index, entries[0].Doc.Version is { Length: > 0 } dv ? dv : selected,
                lastSeen, store);
            post(() => { _view.Value = view; _loaded.Value = true; });

            // Live issue states over the NEWEST document only: the budget is per page-open, and the top release is the
            // one whose chips the reader is looking at.
            IssueStateCache? states = null;
            try { states = await store.RefreshIssueStatesAsync(entries[0].Doc, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch { /* rate limit / offline — the baked-in snapshot stands */ }
            if (states is null) return;

            entries[0] = entries[0] with { IssueStates = states };
            var enriched = Build(entries, index, view.SelectedVersion, lastSeen, store);
            post(() => _view.Value = enriched);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            WaveeLog.Instance.Warn("whatsnew", "page load failed", ex);
            post(() => _loaded.Value = true);
        }
    }

    static ReleaseNotesView Build(List<ReleaseEntry> entries, ReleaseNotesIndex? index, string selected,
                                 string lastSeen, ReleaseNotesStore? store)
    {
        // Highlights MERGE across the stack, newest first, capped: three cards is the strip, and a reader who skipped
        // four releases wants the four best things, not four strips. Posters are resolved HERE (off the UI thread,
        // once) rather than in the card's Render. Only VISIBLE highlights count against the cap — a Store install
        // hides the "get it from the Store" announcement (HighlightVisibility), and its slot goes to the next one.
        bool isStoreInstall = AppVersion.Info.IsStore;
        var merged = new List<HighlightItem>(HighlightStrip.Max);
        foreach (var e in entries)
        {
            foreach (var h in e.Doc.Highlights)
            {
                if (merged.Count >= HighlightStrip.Max) break;
                if (!HighlightVisibility.IsVisible(h, isStoreInstall)) continue;
                merged.Add(new HighlightItem(h, e.Doc, HighlightCard.ResolvePoster(h, store, e.Doc)));
            }
            if (merged.Count >= HighlightStrip.Max) break;
        }
        return new ReleaseNotesView(entries.ToArray(), merged.ToArray(), index, selected, lastSeen);
    }
}
