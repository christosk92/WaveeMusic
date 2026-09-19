using Wavee;
using Xunit;

namespace Wavee.Tests;

/// <summary>B2 (plan §3.3 item 7): `SelectionVerbs.For` over the three kind-mix cases — pure, engine-free.</summary>
public class SelectionVerbsTests
{
    [Fact]
    public void All_track_selection_gets_the_track_verb_set()
    {
        var verbs = SelectionVerbs.For([EntityKind.Track, EntityKind.Track, EntityKind.Track]);
        Assert.Contains(ActionId.Play, verbs);
        Assert.Contains(ActionId.PlayNext, verbs);
        Assert.Contains(ActionId.AddToQueue, verbs);
        Assert.Contains(ActionId.ToggleLike, verbs);
        Assert.Contains(ActionId.SelectAll, verbs);
        Assert.DoesNotContain(ActionId.SaveEpisode, verbs);
        Assert.DoesNotContain(ActionId.MarkPlayed, verbs);
    }

    [Fact]
    public void All_episode_selection_gets_the_episode_verb_set()
    {
        var verbs = SelectionVerbs.For([EntityKind.Episode, EntityKind.Episode]);
        Assert.Contains(ActionId.Play, verbs);
        Assert.Contains(ActionId.PlayNext, verbs);
        Assert.Contains(ActionId.AddToQueue, verbs);
        Assert.Contains(ActionId.SaveEpisode, verbs);
        Assert.Contains(ActionId.MarkPlayed, verbs);
        Assert.Contains(ActionId.SelectAll, verbs);
        Assert.DoesNotContain(ActionId.ToggleLike, verbs);
    }

    [Fact]
    public void Mixed_selection_gets_only_the_three_verbs_every_kind_agrees_on()
    {
        var verbs = SelectionVerbs.For([EntityKind.Track, EntityKind.Episode]);
        Assert.Equal(new[] { ActionId.Play, ActionId.PlayNext, ActionId.AddToQueue, ActionId.SelectAll }, verbs);
    }

    [Fact]
    public void Mixed_order_and_duplicates_do_not_change_the_outcome()
    {
        var a = SelectionVerbs.For([EntityKind.Episode, EntityKind.Track, EntityKind.Episode, EntityKind.Track]);
        var b = SelectionVerbs.For([EntityKind.Track, EntityKind.Episode]);
        Assert.Equal(b, a);
    }

    [Fact]
    public void Empty_selection_falls_back_to_the_track_set()
    {
        var verbs = SelectionVerbs.For([]);
        Assert.Contains(ActionId.Play, verbs);
        Assert.Contains(ActionId.ToggleLike, verbs);
    }
}
