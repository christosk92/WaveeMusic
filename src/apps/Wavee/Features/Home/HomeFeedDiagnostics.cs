using System.Text;
using Wavee.Backend;
using Wavee.Core;

namespace Wavee;

// One line per composed feed, answering the two questions a screenshot cannot: WHICH modules the composer produced with
// how many cards, and which card won the hero. Both were live unknowns — a build shipped with "Up next" absent and a hero
// titled "daylist" with no tags, and neither could be told apart from correct behaviour for that account without this.
//
// Logged once per distinct shape rather than per render: the page re-renders on facet changes, refresh ticks and hover
// state, and an unconditional event would bury the log.
static class HomeFeedDiagnostics
{
    static string _last = "";

    public static void LogModules(HomeFeed feed)
    {
        var sb = new StringBuilder(160);
        for (int i = 0; i < feed.Groups.Count; i++)
        {
            var g = feed.Groups[i];
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(g.Kind).Append('=').Append(g.Cards.Count);
        }
        string shape = sb.ToString();

        var hero = Hero(feed);
        string heroLine = hero is null
            ? "none"
            : hero.Uri + " | " + hero.Title + " | fmt=" + (hero.Meta?.Format ?? "-")
              + " seeds=" + (hero.Meta?.Seeds?.Count ?? 0);

        string line = shape + " || hero: " + heroLine;
        if (line == _last) return;
        _last = line;

        WaveeLog.Instance.Event(WaveeLogLevel.Info, "ui", "home.feed.modules", "Home feed composed",
            fields:
            [
                WaveeLogField.Of("modules", shape),
                WaveeLogField.Of("groups", feed.Groups.Count),
                WaveeLogField.Of("hero", heroLine),
                WaveeLogField.Of("chips", feed.Chips?.Count ?? 0),
                WaveeLogField.Of("greeting", feed.Greeting),
            ]);
    }

    static HomeCard? Hero(HomeFeed feed)
    {
        var gs = feed.Groups;
        for (int i = 0; i < gs.Count; i++)
            if (gs[i].Kind == HomeGroupKind.Hero && gs[i].Cards.Count > 0) return gs[i].Cards[0];
        return null;
    }
}
