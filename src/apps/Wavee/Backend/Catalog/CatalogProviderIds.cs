using System;
using Wavee.Backend.MediaSources;
using Wavee.Core;
using Wavee.Sdk;

namespace Wavee.Backend.Catalog;

/// <summary>Playable owners without an ICatalogSource still need one consistent resource namespace.</summary>
public static class CatalogProviderIds
{
    public static string Resolve(string uri, string registeredProvider)
    {
        if (ModuleUri.TryDecode(uri, out var moduleId, out _)) return "module:" + moduleId;
        if (uri.StartsWith(PlayableUri.MediaPrefix, StringComparison.Ordinal)) return "external-media";
        var parsed = EntityUri.Parse(uri).Provider;
        if (registeredProvider.Length > 0 && (registeredProvider != "spotify" || parsed == "spotify")) return registeredProvider;
        return parsed.Length > 0 ? parsed : "unowned";
    }
}
