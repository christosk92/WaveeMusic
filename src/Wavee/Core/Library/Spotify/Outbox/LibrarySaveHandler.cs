using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Core.Http;
using Wavee.Core.Session;
using Wavee.Core.Storage.Abstractions;
using Wavee.Core.Storage.Outbox;
using Wavee.Protocol.Collection;

namespace Wavee.Core.Library.Spotify.Outbox;

/// <summary>
/// Outbox handler for <c>"library.save"</c> entries. Writes the item to the
/// matching collection set (track/album → "collection", artist → "artist",
/// show → "show", ylpin → "ylpin") via SpClient.
/// </summary>
public sealed class LibrarySaveHandler : IOutboxHandler
{
    public const string Kind = "library.save";
    public string OpKind => Kind;

    private readonly SpClient _spClient;
    private readonly ISession _session;

    public LibrarySaveHandler(SpClient spClient, ISession session)
    {
        _spClient = spClient ?? throw new ArgumentNullException(nameof(spClient));
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    public Task ProcessAsync(OutboxEntry entry, CancellationToken ct)
        => LibraryOpDispatch.WriteAsync(_spClient, _session, entry, isRemoved: false, ct);
}
