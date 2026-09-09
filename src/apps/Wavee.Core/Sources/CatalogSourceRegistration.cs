namespace Wavee.Core;

/// <summary>Ownership registration for a source whose runtime data comes through normalized catalog queries.</summary>
public sealed class CatalogSourceRegistration : ISource
{
    readonly Func<string, bool> _owns;
    public string Id { get; }
    public SourceCapabilities Capabilities { get; }
    public CatalogSourceRegistration(string id, Func<string, bool> owns, SourceCapabilities capabilities)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        _owns = owns ?? throw new ArgumentNullException(nameof(owns));
        Capabilities = capabilities | SourceCapabilities.Catalog;
    }
    public bool Owns(string uri) => _owns(uri);
}
