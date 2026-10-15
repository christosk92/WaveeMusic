// ── Wavee.Tests/TitleBarContractTests.cs — the title bar's published keys and props (themes plan 6.0, T4b) ────────────────────
//
// The merged chrome row is the structure's (`Shell.TitleBar`), and so are the omnibar field (`Shell.Search`) and its popup body
// (`Shell.SearchSuggestions`). What is pure about the contract: the keys' ids (a published id never changes), Classic registering all
// three under its template ids, and the props records comparing by value so a mount that sets only its form and width (`with`) skips
// the template when nothing else differs.

using FluentGpu.Signals;
using Wavee.Themes.Classic;
using Wavee.Theming.Contracts;
using Xunit;

namespace Wavee.Tests;

public class TitleBarContractTests
{
    [Fact]
    public void The_published_ids_are_the_plans()
    {
        Assert.Equal("Shell.TitleBar", ViewKeys.ShellTitleBar.Id);
        Assert.Equal("Shell.Search", ViewKeys.ShellSearch.Id);
        Assert.Equal("Shell.SearchSuggestions", ViewKeys.ShellSearchSuggestions.Id);
    }

    [Fact]
    public void Classic_registers_the_title_bar_the_search_and_the_suggestions_under_its_template_ids()
    {
        var classic = ClassicTheme.Create();

        Assert.Equal("classic.shell.title-bar", classic.Registry.TemplateIdOf(ViewKeys.ShellTitleBar.Id));
        Assert.Equal("classic.shell.search", classic.Registry.TemplateIdOf(ViewKeys.ShellSearch.Id));
        Assert.Equal("classic.shell.search-suggestions", classic.Registry.TemplateIdOf(ViewKeys.ShellSearchSuggestions.Id));
        Assert.Empty(classic.Validate());
    }

    static SearchSuggestionSource Source() => new()
    {
        Answer = static () => new SearchAnswer(SearchAnswerState.Idle, [], []),
        Text = new Signal<string>(""),
        Highlight = new Signal<int>(-1),
        Retry = static () => { },
        PendingBodyHeight = static (_, _, _) => 0f,
        PendingSkeletonRows = static (_, _) => (0, 0),
        QueryRowHeight = 44f,
        RichRowHeight = 62f,
        PopupBodyMaxHeight = 560f,
    };

    [Fact]
    public void A_mount_that_sets_only_its_form_equals_itself_and_differs_from_the_other_form()
    {
        var model = new OmnibarModel(new OmnibarPorts(
            Source: static () => null,
            FromHistory: static _ => Shell.Omnibar.Suggestions.Empty,
            SetSearchText: static _ => { },
            RequestFocus: static () => { },
            Go: static (_, _) => { },
            SearchRoute: static _ => Shell.Route.None,
            PlayContext: static () => null));
        var text = new Signal<string>("");
        var shared = new ShellSearchProps
        {
            Model = model,
            TextCell = () => text,
            FocusTicket = static () => 0,
            FieldWidth = static () => 330f,
            Suggestions = Source(),
        };

        var field = shared with { Form = SearchForm.Field };
        Assert.Equal(field, shared with { Form = SearchForm.Field });
        Assert.NotEqual(field, shared with { Form = SearchForm.Icon });
    }

    [Fact]
    public void The_popup_props_keep_the_row_caps_and_the_state_the_core_publishes()
    {
        var rows = new[] { new SearchRichRow("spotify:artist:1", static _ => new SearchRichRowFacts { Uri = "spotify:artist:1", Title = "A", Meta = "Artist" }) };
        var answer = new SearchAnswer(SearchAnswerState.Results, ["a", "b"], rows);

        Assert.Equal(SearchAnswerState.Results, answer.State);
        Assert.Equal(2, answer.Queries.Count);
        Assert.Single(answer.Rows);
        Assert.Equal("spotify:artist:1", answer.Rows[0].Uri);
        Assert.Equal("Artist", answer.Rows[0].Facts(null).Meta);
    }
}
