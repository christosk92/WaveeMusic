using System.Collections.Generic;
using System.Text;
using Wavee.Backend;
using Xunit;

namespace Wavee.Tests;

// Phase A: the proto-free /context-resolve streaming reader (Utf8JsonReader). Pins ordered (uri,uid) extraction across
// pages, sorting.criteria, the first next_page_url, and robustness against unknown fields / uri-less tracks.
public class ContextJsonTests
{
    [Fact]
    public void Parse_ExtractsOrderedUidsSortingAndNextPage()
    {
        const string json = """
        {
          "uri":"spotify:user:x:collection",
          "url":"context://spotify:user:x:collection",
          "metadata":{"sorting.criteria":"added_at DESC","context_owner":"x"},
          "pages":[
            {"page_url":"hm://context/page/1","next_page_url":"hm://context/page/2",
             "tracks":[
               {"uri":"spotify:track:a","uid":"uidA","provider":"context","metadata":{"k":"v"}},
               {"uri":"spotify:track:b","uid":"uidB","provider":"autoplay","metadata":{"autoplay.is_autoplay":"true"}}
             ]}
          ]
        }
        """;
        var refs = new List<QueuedRef>();
        string? sorting = null, next = null;
        ContextJson.Parse(Encoding.UTF8.GetBytes(json), refs, ref sorting, ref next);

        Assert.Equal(2, refs.Count);
        Assert.Equal("spotify:track:a", refs[0].Uri);
        Assert.Equal("uidA", refs[0].Uid);
        Assert.Equal("context", refs[0].Provider);
        Assert.Equal("v", refs[0].Metadata!["k"]);
        Assert.Equal("spotify:track:b", refs[1].Uri);
        Assert.Equal("uidB", refs[1].Uid);
        Assert.Equal("autoplay", refs[1].Provider);
        Assert.Equal("true", refs[1].Metadata!["autoplay.is_autoplay"]);
        Assert.Equal("added_at DESC", sorting);
        Assert.Equal("hm://context/page/2", next);
    }

    [Fact]
    public void Parse_SkipsUnknownFieldsAndUriLessTracks()
    {
        const string json = """
        {"foo":{"bar":[1,2,3]},"pages":[{"tracks":[{"uid":"noUri"},{"uri":"spotify:track:c","uid":"uidC"}]}],"extra":42}
        """;
        var refs = new List<QueuedRef>();
        string? sorting = null, next = null;
        ContextJson.Parse(Encoding.UTF8.GetBytes(json), refs, ref sorting, ref next);

        Assert.Single(refs);                          // the uri-less track is dropped
        Assert.Equal("spotify:track:c", refs[0].Uri);
        Assert.Equal("uidC", refs[0].Uid);
        Assert.Null(sorting);
        Assert.Null(next);
    }

    [Fact]
    public void Parse_AccumulatesAcrossMultiplePages()
    {
        const string json = """
        {"pages":[
          {"tracks":[{"uri":"spotify:track:a","uid":"a"}]},
          {"tracks":[{"uri":"spotify:track:b","uid":"b"},{"uri":"spotify:track:c","uid":"c"}]}
        ]}
        """;
        var refs = new List<QueuedRef>();
        string? sorting = null, next = null;
        ContextJson.Parse(Encoding.UTF8.GetBytes(json), refs, ref sorting, ref next);

        Assert.Equal(3, refs.Count);
        Assert.Equal(new[] { "spotify:track:a", "spotify:track:b", "spotify:track:c" }, refs.ConvertAll(r => r.Uri));
    }
}
