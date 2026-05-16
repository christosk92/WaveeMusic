using System;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Core.Http;
using Wavee.Core.Session;
using Wavee.Core.Storage.Abstractions;
using Wavee.Core.Storage.Outbox;

namespace Wavee.Core.Library.Spotify.Outbox;

/// <summary>
/// Outbox handler for <c>"library.remove"</c> entries. Writes a removal marker
/// to the matching collection set via SpClient.
/// </summary>
public sealed class LibraryRemoveHandler : IOutboxHandler
{
    public const string Kind = "library.remove";
    public string OpKind => Kind;

    private readonly SpClient _spClient;
    private readonly ISession _session;

    public LibraryRemoveHandler(SpClient spClient, ISession session)
    {
        _spClient = spClient ?? throw new ArgumentNullException(nameof(spClient));
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    public Task ProcessAsync(OutboxEntry entry, CancellationToken ct)
        => LibraryOpDispatch.WriteAsync(_spClient, _session, entry, isRemoved: true, ct);
}
