using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Wavee.Backend;

public static class ContextJson
{
    public readonly record struct Result(
        string? ContextUri,
        IReadOnlyDictionary<string, string> Metadata,
        int PageCount);

    public static void Parse(ReadOnlySpan<byte> json, List<QueuedRef> refs, ref string? sorting, ref string? nextPage)
        => Parse(json, refs, ref sorting, ref nextPage, out _);

    public static void Parse(ReadOnlySpan<byte> json, List<QueuedRef> refs, ref string? sorting, ref string? nextPage, out Result result)
    {
        string? contextUri = null;
        Dictionary<string, string>? metadata = null;
        int pageCount = 0;

        var r = new Utf8JsonReader(json);
        if (!r.Read() || r.TokenType != JsonTokenType.StartObject)
        {
            result = new Result(null, new Dictionary<string, string>(), 0);
            return;
        }

        while (r.Read() && r.TokenType != JsonTokenType.EndObject)
        {
            if (r.TokenType != JsonTokenType.PropertyName) continue;
            bool isUri = r.ValueTextEquals("uri");
            bool isPages = !isUri && r.ValueTextEquals("pages");
            bool isTracks = !isUri && !isPages && r.ValueTextEquals("tracks");
            bool isMeta = !isUri && !isPages && !isTracks && r.ValueTextEquals("metadata");
            bool isNext = !isUri && !isPages && !isTracks && !isMeta && r.ValueTextEquals("next_page_url");
            r.Read();

            if (isUri && r.TokenType == JsonTokenType.String)
                contextUri = r.GetString();
            else if (isPages && r.TokenType == JsonTokenType.StartArray)
                pageCount += ReadPages(ref r, refs, ref nextPage);
            else if (isTracks && r.TokenType == JsonTokenType.StartArray)
                ReadTracks(ref r, refs);
            else if (isMeta && r.TokenType == JsonTokenType.StartObject)
                ReadMetadata(ref r, ref sorting, ref metadata);
            else if (isNext && r.TokenType == JsonTokenType.String)
                SetFirstString(ref nextPage, r.GetString());
            else
                r.Skip();
        }

        result = new Result(contextUri, metadata ?? new Dictionary<string, string>(), pageCount);
    }

    public static ContextPage ParseRadioApollo(ReadOnlySpan<byte> json)
    {
        var refs = new List<QueuedRef>();
        string? sorting = null, next = null;
        Parse(json, refs, ref sorting, ref next, out _);
        var tracks = new QueuedTrack[refs.Count];
        for (int i = 0; i < refs.Count; i++)
            tracks[i] = new QueuedTrack(ContextResolve.Synthetic(refs[i].Uri), refs[i].Uid, "autoplay", refs[i].Metadata);
        return new ContextPage(tracks, next);
    }

    static int ReadPages(ref Utf8JsonReader r, List<QueuedRef> refs, ref string? nextPage)
    {
        int pages = 0;
        while (r.Read() && r.TokenType != JsonTokenType.EndArray)
        {
            if (r.TokenType == JsonTokenType.StartObject)
            {
                pages++;
                ReadPage(ref r, refs, ref nextPage);
            }
            else r.Skip();
        }
        return pages;
    }

    static void ReadPage(ref Utf8JsonReader r, List<QueuedRef> refs, ref string? nextPage)
    {
        while (r.Read() && r.TokenType != JsonTokenType.EndObject)
        {
            if (r.TokenType != JsonTokenType.PropertyName) continue;
            bool isTracks = r.ValueTextEquals("tracks");
            bool isNext = !isTracks && r.ValueTextEquals("next_page_url");
            r.Read();
            if (isTracks && r.TokenType == JsonTokenType.StartArray)
                ReadTracks(ref r, refs);
            else if (isNext && r.TokenType == JsonTokenType.String)
                SetFirstString(ref nextPage, r.GetString());
            else
                r.Skip();
        }
    }

    static void ReadTracks(ref Utf8JsonReader r, List<QueuedRef> refs)
    {
        while (r.Read() && r.TokenType != JsonTokenType.EndArray)
        {
            if (r.TokenType == JsonTokenType.StartObject) ReadTrack(ref r, refs);
            else r.Skip();
        }
    }

    static void ReadTrack(ref Utf8JsonReader r, List<QueuedRef> refs)
    {
        string uri = "", uid = "", provider = "context";
        Dictionary<string, string>? metadata = null;
        while (r.Read() && r.TokenType != JsonTokenType.EndObject)
        {
            if (r.TokenType != JsonTokenType.PropertyName) continue;
            bool isUri = r.ValueTextEquals("uri");
            bool isUid = !isUri && r.ValueTextEquals("uid");
            bool isProvider = !isUri && !isUid && r.ValueTextEquals("provider");
            bool isMeta = !isUri && !isUid && !isProvider && r.ValueTextEquals("metadata");
            r.Read();
            if (isUri && r.TokenType == JsonTokenType.String) uri = r.GetString() ?? "";
            else if (isUid && r.TokenType == JsonTokenType.String) uid = r.GetString() ?? "";
            else if (isProvider && r.TokenType == JsonTokenType.String) provider = r.GetString() ?? "context";
            else if (isMeta && r.TokenType == JsonTokenType.StartObject) ReadStringMetadata(ref r, ref metadata);
            else r.Skip();
        }
        if (uri.Length > 0) refs.Add(new QueuedRef(uri, uid, provider, metadata));
    }

    static void ReadMetadata(ref Utf8JsonReader r, ref string? sorting, ref Dictionary<string, string>? metadata)
    {
        metadata ??= new Dictionary<string, string>(StringComparer.Ordinal);
        while (r.Read() && r.TokenType != JsonTokenType.EndObject)
        {
            if (r.TokenType != JsonTokenType.PropertyName) continue;
            string key = r.GetString() ?? "";
            bool isSort = key == "sorting.criteria";
            r.Read();
            if (r.TokenType == JsonTokenType.String)
            {
                string value = r.GetString() ?? "";
                if (key.Length > 0) metadata[key] = value;
                if (isSort) sorting = value;
            }
            else r.Skip();
        }
    }

    static void ReadStringMetadata(ref Utf8JsonReader r, ref Dictionary<string, string>? metadata)
    {
        metadata ??= new Dictionary<string, string>(StringComparer.Ordinal);
        while (r.Read() && r.TokenType != JsonTokenType.EndObject)
        {
            if (r.TokenType != JsonTokenType.PropertyName) continue;
            string key = r.GetString() ?? "";
            r.Read();
            if (r.TokenType == JsonTokenType.String)
            {
                if (key.Length > 0) metadata[key] = r.GetString() ?? "";
            }
            else r.Skip();
        }
    }

    static void SetFirstString(ref string? target, string? value)
    {
        if (string.IsNullOrEmpty(target) && !string.IsNullOrEmpty(value)) target = value;
    }
}
