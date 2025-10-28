using System.Reactive;
using System.Reactive.Linq;
using Google.Protobuf;
using Wavee.Connect.Protocol;

namespace Wavee.Connect;

/// <summary>
/// Extension methods for DealerClient observables providing fluent API.
/// </summary>
public static class DealerExtensions
{
    /// <summary>
    /// Filters dealer messages by URI prefix.
    /// </summary>
    /// <param name="source">Source observable of dealer messages.</param>
    /// <param name="uriPrefix">URI prefix to match (e.g., "hm://pusher", "hm://connect-state/v1/").</param>
    /// <returns>Filtered observable containing only matching messages.</returns>
    public static IObservable<DealerMessage> WhereUri(
        this IObservable<DealerMessage> source,
        string uriPrefix)
    {
        return source.Where(m => m.Uri.StartsWith(uriPrefix, StringComparison.Ordinal));
    }

    /// <summary>
    /// Filters dealer messages by multiple URI prefixes.
    /// </summary>
    /// <param name="source">Source observable of dealer messages.</param>
    /// <param name="uriPrefixes">URI prefixes to match.</param>
    /// <returns>Filtered observable containing messages matching any prefix.</returns>
    public static IObservable<DealerMessage> WhereUriAny(
        this IObservable<DealerMessage> source,
        params string[] uriPrefixes)
    {
        return source.Where(m => uriPrefixes.Any(prefix =>
            m.Uri.StartsWith(prefix, StringComparison.Ordinal)));
    }

    /// <summary>
    /// Filters dealer requests by message_ident prefix.
    /// </summary>
    /// <param name="source">Source observable of dealer requests.</param>
    /// <param name="identPrefix">Message identifier prefix to match.</param>
    /// <returns>Filtered observable containing only matching requests.</returns>
    public static IObservable<DealerRequest> WhereMessageIdent(
        this IObservable<DealerRequest> source,
        string identPrefix)
    {
        return source.Where(r => r.MessageIdent.StartsWith(identPrefix, StringComparison.Ordinal));
    }

    /// <summary>
    /// Parses dealer message payloads as protobuf messages.
    /// Messages that fail to parse are filtered out.
    /// </summary>
    /// <typeparam name="T">Protobuf message type.</typeparam>
    /// <param name="source">Source observable of dealer messages.</param>
    /// <returns>Observable of parsed protobuf messages.</returns>
    public static IObservable<T> ParseProtobuf<T>(this IObservable<DealerMessage> source)
        where T : IMessage<T>, new()
    {
        return source
            .Select(m =>
            {
                try
                {
                    var parser = new MessageParser<T>(() => new T());
                    return (success: true, message: parser.ParseFrom(m.Payload));
                }
                catch
                {
                    return (success: false, message: default(T)!);
                }
            })
            .Where(result => result.success)
            .Select(result => result.message);
    }

    /// <summary>
    /// Handles dealer requests with automatic reply sending.
    /// </summary>
    /// <param name="source">Source observable of dealer requests.</param>
    /// <param name="dealer">DealerClient instance for sending replies.</param>
    /// <param name="handler">Async handler function that processes request and returns result.</param>
    /// <returns>Disposable subscription handle.</returns>
    public static IDisposable HandleRequests(
        this IObservable<DealerRequest> source,
        DealerClient dealer,
        Func<DealerRequest, ValueTask<RequestResult>> handler)
    {
        return source
            .SelectMany(async req =>
            {
                try
                {
                    var result = await handler(req);
                    await dealer.SendReplyAsync(req.Key, result);
                }
                catch
                {
                    // On error, send UpstreamError reply
                    await dealer.SendReplyAsync(req.Key, RequestResult.UpstreamError);
                }
                return Unit.Default;
            })
            .Subscribe();
    }

    /// <summary>
    /// Handles dealer requests with synchronous handler and automatic reply sending.
    /// </summary>
    /// <param name="source">Source observable of dealer requests.</param>
    /// <param name="dealer">DealerClient instance for sending replies.</param>
    /// <param name="handler">Synchronous handler function that processes request and returns result.</param>
    /// <returns>Disposable subscription handle.</returns>
    public static IDisposable HandleRequests(
        this IObservable<DealerRequest> source,
        DealerClient dealer,
        Func<DealerRequest, RequestResult> handler)
    {
        return source
            .SelectMany(async req =>
            {
                try
                {
                    var result = handler(req);
                    await dealer.SendReplyAsync(req.Key, result);
                }
                catch
                {
                    await dealer.SendReplyAsync(req.Key, RequestResult.UpstreamError);
                }
                return Unit.Default;
            })
            .Subscribe();
    }

    /// <summary>
    /// Filters messages by Content-Type header.
    /// </summary>
    /// <param name="source">Source observable of dealer messages.</param>
    /// <param name="contentType">Content-Type to match (e.g., "application/x-protobuf", "application/json").</param>
    /// <returns>Filtered observable containing only matching messages.</returns>
    public static IObservable<DealerMessage> WhereContentType(
        this IObservable<DealerMessage> source,
        string contentType)
    {
        return source.Where(m =>
            m.Headers.TryGetValue("Content-Type", out var ct) &&
            ct.Equals(contentType, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Filters messages to only protobuf payloads.
    /// </summary>
    public static IObservable<DealerMessage> WhereProtobuf(this IObservable<DealerMessage> source)
    {
        return source.WhereContentType("application/x-protobuf");
    }

    /// <summary>
    /// Filters messages to only JSON payloads.
    /// </summary>
    public static IObservable<DealerMessage> WhereJson(this IObservable<DealerMessage> source)
    {
        return source.WhereContentType("application/json");
    }

    /// <summary>
    /// Filters messages to only plain text payloads.
    /// </summary>
    public static IObservable<DealerMessage> WherePlainText(this IObservable<DealerMessage> source)
    {
        return source.WhereContentType("text/plain");
    }
}
