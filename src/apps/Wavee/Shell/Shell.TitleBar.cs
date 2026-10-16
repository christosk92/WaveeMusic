// ── Shell/Shell.TitleBar.cs — the core side of `Shell.TitleBar`, `Shell.Search` and `Shell.SearchSuggestions` ───────────────
//
// Role: CORE (themes plan 6.1, T4b)
//
// The merged chrome row's PICTURE moved to the structure (`Themes\Classic\Structure\Views\TitleBar\ClassicTitleBar.cs`, and
// `Views\Search\ClassicSearch.cs` for the omnibar). What stays here is everything the picture reads: the allocation
// (`Shell.Chrome`, the pressure allocator, in `Shell.Chrome.cs`; its value reaches the view as a `ChromeRowState`), the tab
// workspace's items and verbs (`BuildTabItems`, in `Shell.UI.cs`), the history stacks, the pin, the auth fold, the unread
// count, the omnibar's model and the answer it publishes (`SearchAnswer`: the state, the visible completions and the rich rows
// with their verbs). The view gets one shared, frozen props record per surface; every live value reaches it through a `Func`
// it calls inside the engine bar's render, so it subscribes exactly where the builders did before the move.

using FluentGpu.Controls;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using Wavee.Theming.Contracts;

namespace Wavee;

public static partial class Shell
{
    // ══ THE TITLE BAR'S PROPS ═══════════════════════════════════════════════════════════════════════════════════════

    static ShellTitleBarProps? s_titleBarProps;

    /// <summary>The props of the structure's title bar: one frozen instance for the process (every member is a delegate over the
    /// shell's statics, read lazily, so a root remount mounts the same props on a fresh tree).</summary>
    static ShellTitleBarProps TitleBarProps() => s_titleBarProps ??= new()
    {
        Layout = static () => RowStateOf(ChromeLayout.Value),
        PeekLayout = static () => RowStateOf(ChromeLayout.Peek()),
        TabsVersion = TitleBarTabsVersion,
        ContentVersion = ChromeContentVersion,
        TabItemsVersion = TabStripItemsVersion,
        OnPaneToggle = Sidebar.TogglePane,
        PaneToggleEnabled = s_paneToggleEnabled,
        PaneToggleTip = PaneToggleTip,
        TabItems = BuildTabItems,
        TabSelection = static () => SelectedTab,
        OnActivateTab = static index => WorkspaceVerbs.Shared.ActivateTab(index),
        OnCloseTab = static index => WorkspaceVerbs.Shared.CloseTab(index),
        OnNewTab = static () => WorkspaceVerbs.Shared.OpenTab(new Route(RouteKind.Home)),
        OnTabExtent = static extent => s_tabExtent.SetIfChanged(Chrome.TabExtentFromMetrics(extent)),
        CanBack = static () => Shell.CanBack.Value,
        CanForward = static () => Shell.CanForward.Value,
        GoBack = Shell.GoBack,
        GoForward = Shell.GoForward,
        HistoryMenu = HistoryItems,
        ProfileChip = static () => ProfileChip(),
        SignIn = static () => Spotify.Login(),
        ToggleFriends = static () => Ui.Toggle(RailMode.Friends),
        BellUnread = static () => Diagnostics.StoreShot.Presenting.Value ? 0 : Notify.Unread.Value,
        ToggleNotifications = OpenNotificationPanel,
        Pin = CurrentPin,
        Search = SearchProps(),
    };

    /// <summary>The allocation as the view reads it (<see cref="ChromeRowState"/>): the form of the identity chip and of the search
    /// are the contract's enums.</summary>
    public static ChromeRowState RowStateOf(in Chrome l) => new(
        l.ShowName, l.ShowActions, l.ShowForward, l.ShowBack, l.ShowNewTab, l.ShowTrailing,
        l.Chip switch
        {
            FrameRules.ChipForm.Connecting => ChromeChipForm.Connecting,
            FrameRules.ChipForm.Reconnect => ChromeChipForm.Reconnect,
            FrameRules.ChipForm.SignIn => ChromeChipForm.SignIn,
            _ => ChromeChipForm.Profile,
        },
        l.SearchMode == MergedSearchMode.Icon ? SearchForm.Icon : SearchForm.Field,
        l.SearchWidth, l.TabIslandWidth, l.TabCount, l.TabMaxW);

    /// <summary>The history flyout's rows for the back stack (<paramref name="forward"/> false) or the forward stack, read at OPEN.
    /// An empty stack answers no rows, and then nothing opens at all.</summary>
    static IReadOnlyList<MenuFlyoutItem> HistoryItems(bool forward)
    {
        var stack = forward ? s_nav.Forward : s_nav.Back;   // read at OPEN, never at mount
        var (rows, hasMore) = FrameRules.HistoryMenu(stack.Count);
        if (rows == 0) return [];                           // an empty stack opens nothing at all
        var items = new MenuFlyoutItem[rows + (hasMore ? 2 : 0)];
        for (int i = 0; i < rows; i++)
        {
            var r = stack[FrameRules.HistoryMenuIndex(stack.Count, i)];
            var (title, glyph) = Dest(r);
            items[i] = new MenuFlyoutItem(title, glyph, true, () => GoTo(r));
        }
        if (hasMore)
        {
            items[rows] = MenuFlyoutItem.Separator;
            items[rows + 1] = new MenuFlyoutItem(Loc.Get(Strings.Nav.ViewAllHistory), Icons.Clock, true,
                static () => GoTo(new Route(RouteKind.History)));
        }
        return items;
    }

    /// <summary>The direct Pin / Unpin for the destination on screen, or null when it is not pinnable (<see cref="PinVerbs.TargetFor"/>).</summary>
    static TitleBarPin? CurrentPin()
    {
        if (PinRow(Current.Value) is not { Invoke: { } invoke } row) return null;
        return new TitleBarPin(row.Icon.Glyph ?? Icons.Pin, row.Label, invoke);
    }

    // ══ THE SEARCH'S PROPS ══════════════════════════════════════════════════════════════════════════════════════════

    static ShellSearchProps? s_searchProps;

    /// <summary>The props of the structure's search: the omnibar's model, its text cell and focus ticket, the field width rule, and
    /// what the popup body needs. The row sets the mount's form (and the centre column's live width) with <c>with</c>.</summary>
    static ShellSearchProps SearchProps() => s_searchProps ??= new()
    {
        Model = s_omnibar,
        TextCell = static () => SearchText,
        FocusTicket = static () => SearchFocusRequest.Value,
        FieldWidth = FieldWidthNow,
        Suggestions = new SearchSuggestionSource
        {
            Answer = OmnibarAnswer,
            Text = SearchText,
            Highlight = s_omnibar.Highlight,
            Retry = static () => s_omnibar.Retry(),
            PendingBodyHeight = Omnibar.PendingBodyHeight,
            PendingSkeletonRows = Omnibar.PendingSkeletonRows,
            QueryRowHeight = Omnibar.QueryRowHeight,
            RichRowHeight = Omnibar.RichRowHeight,
            PopupBodyMaxHeight = Omnibar.PopupBodyMaxHeight,
        },
    };

    /// <summary>The field's width now (tuned F6): the rest width (<see cref="Chrome.FieldWidthFor"/>), or, while the field has focus
    /// or its suggestions are open, the allocator's focused width (<see cref="Chrome.FocusedFieldWidth"/>). Reads the allocation and
    /// the two omnibar facts tracked, so the mounted field re-renders when either moves. No measurement is read: the centre column
    /// hugs the field, so it is the feedback of this very width (<see cref="Chrome.FieldWidthFor"/>).</summary>
    static float FieldWidthNow()
    {
        var l = ChromeLayout.Value;
        float rest = Chrome.FieldWidthFor(l.SearchWidth, ShellPolicy.ChromeBudgetNow);
        return s_omnibar.FieldExpanded ? Chrome.FocusedFieldWidth(l.SearchExpandW, rest) : rest;
    }

    /// <summary>The store's state as the popup draws it. Reading it subscribes the reader to the store's version (the popup
    /// re-renders on every change of the request lifecycle). The completions and the rich rows are cut to the visible caps; a rich
    /// row's facts are built by the row's own render, bound to the item the row was mounted with.</summary>
    static SearchAnswer OmnibarAnswer()
    {
        _ = s_omnibar.Version.Value;
        var query = s_omnibar.Query;
        var s = query.Suggestions;
        int queryRows = Omnibar.QueryRowCount(s);
        int richRows = Omnibar.RichRowCount(s);
        var queries = new string[queryRows];
        for (int i = 0; i < queryRows; i++) queries[i] = s.Queries[i];
        var rows = new SearchRichRow[richRows];
        for (int i = 0; i < richRows; i++)
        {
            var item = s.Items[i];
            rows[i] = new SearchRichRow(item.Uri.Text, afterPlay => RichRowFacts(item, afterPlay));
        }
        return new SearchAnswer(AnswerStateOf(query.State), queries, rows);
    }

    static SearchAnswerState AnswerStateOf(Omnibar.State state) => state switch
    {
        Omnibar.State.Pending => SearchAnswerState.Pending,
        Omnibar.State.Results => SearchAnswerState.Results,
        Omnibar.State.Empty => SearchAnswerState.Empty,
        Omnibar.State.Failed => SearchAnswerState.Failed,
        _ => SearchAnswerState.Idle,
    };

    /// <summary>One rich row's facts. Runs in the row's render: <c>item.Ref</c> allocates the entity's table row if unseen (UI thread).
    /// The play decision is the model's (a current track toggles pause; else the host's play); the popup closes after it.</summary>
    static SearchRichRowFacts RichRowFacts(Omnibar.Item item, Action? afterPlay)
    {
        var kind = item.Kind;
        var rf = item.Ref;                                  // allocates the table row if unseen (UI thread, render)
        string uri = item.Uri.Text;
        Action? play = Omnibar.CanPlay(kind) ? () => { s_omnibar.Play(item); afterPlay?.Invoke(); } : null;
        Func<ContextMenuModel?>? menu = s_omnibar.HasRowMenu(item) ? () => s_omnibar.RowMenu(item) : null;

        DragSource? drag = OmnibarRowRules.Drags(kind) && !rf.IsNone
            ? Drag.Source(() => kind == Omnibar.ItemKind.Track
                ? new DragPayload(DragKind.Track, uri, uri, item.Title, rf, Tracks: [new Track(rf.Slot)], ArtUrl: item.ImageUrl)
                : new DragPayload(Drag.KindOf(rf.Kind), uri, uri, item.Title, rf, ArtUrl: item.ImageUrl))
            : null;

        return new SearchRichRowFacts
        {
            Uri = uri,
            Title = item.Title,
            Meta = Search.SubtitleText(OmniKindWord(item), item.Subtitle),
            ImageUrl = item.ImageUrl,
            Circular = Omnibar.IsCircular(kind),
            Play = play,
            Menu = menu,
            Drag = drag,
            Trailing = rf.IsNone ? null : Search.TrailingOf(rf, uri, item.Title),
        };
    }

    /// <summary>The row's kind word: an album's release kind (Single / EP / Compilation) when the wire said it, else the
    /// kind's own label.</summary>
    static string OmniKindWord(Omnibar.Item item)
        => item.Kind == Omnibar.ItemKind.Album && item.ReleaseKind is { } release
            ? Detail.Text.KindLabel(release)
            : OmniTypeLabel(item.Kind);

    static string OmniTypeLabel(Omnibar.ItemKind kind) => Loc.Get(kind switch
    {
        Omnibar.ItemKind.Track => Strings.Search.TypeSong,
        Omnibar.ItemKind.Artist => Strings.Search.TypeArtist,
        Omnibar.ItemKind.Album => Strings.Search.TypeAlbum,
        Omnibar.ItemKind.Playlist => Strings.Search.TypePlaylist,
        Omnibar.ItemKind.Genre => Strings.Search.TypeGenre,
        Omnibar.ItemKind.Episode => Strings.Search.TypeEpisode,
        Omnibar.ItemKind.Podcast => Strings.Search.TypePodcast,
        Omnibar.ItemKind.Audiobook => Strings.Search.TypeAudiobook,
        _ => Strings.Search.TypeUser,
    });
}
