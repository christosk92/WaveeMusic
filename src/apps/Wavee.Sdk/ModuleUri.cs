using System.Text;

namespace Wavee.Sdk;

/// <summary>
/// The playable-uri namespace a module owns: <c>wavee:module:&lt;id&gt;:&lt;base64url(playableId)&gt;</c>.
/// The payload is base64url with the padding stripped, so it is colon-free and the uri splits unambiguously.
/// </summary>
public static class ModuleUri
{
    /// <summary>The fixed uri scheme prefix shared by every module playable.</summary>
    public const string Scheme = "wavee:module:";

    /// <summary>The uri prefix owned by one module — what a provider's <c>Owns(uri)</c> tests against.</summary>
    /// <param name="moduleId">The module id from its manifest.</param>
    public static string Prefix(string moduleId)
    {
        ArgumentException.ThrowIfNullOrEmpty(moduleId);
        return string.Concat(Scheme, moduleId, ":");
    }

    /// <summary>Builds the playable uri for one module-private id.</summary>
    /// <param name="moduleId">The module id from its manifest.</param>
    /// <param name="playableId">The module-private playable id (any text, including colons).</param>
    public static string Encode(string moduleId, string playableId)
    {
        ArgumentException.ThrowIfNullOrEmpty(moduleId);
        ArgumentNullException.ThrowIfNull(playableId);
        return string.Concat(Prefix(moduleId), ToBase64Url(Encoding.UTF8.GetBytes(playableId)));
    }

    /// <summary>Splits a playable uri back into its module id and the module-private playable id.</summary>
    /// <param name="uri">The candidate uri; anything that is not a well-formed module uri returns false.</param>
    /// <param name="moduleId">Receives the module id, or the empty string on failure.</param>
    /// <param name="playableId">Receives the module-private playable id, or the empty string on failure.</param>
    /// <returns>True when <paramref name="uri"/> is a well-formed module playable uri.</returns>
    public static bool TryDecode(string? uri, out string moduleId, out string playableId)
    {
        moduleId = string.Empty;
        playableId = string.Empty;
        if (string.IsNullOrEmpty(uri) || !uri.StartsWith(Scheme, StringComparison.Ordinal)) return false;

        int idStart = Scheme.Length;
        int sep = uri.IndexOf(':', idStart);
        if (sep < 0 || sep == idStart || sep == uri.Length - 1) return false;

        string id = uri[idStart..sep];
        string payload = uri[(sep + 1)..];
        if (!TryFromBase64Url(payload, out byte[]? bytes)) return false;

        moduleId = id;
        playableId = Encoding.UTF8.GetString(bytes);
        return true;
    }

    private static string ToBase64Url(ReadOnlySpan<byte> bytes)
    {
        string s = Convert.ToBase64String(bytes);
        return s.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static bool TryFromBase64Url(string payload, out byte[] bytes)
    {
        bytes = [];
        foreach (char c in payload)
        {
            bool ok = c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_';
            if (!ok) return false;
        }

        var sb = new StringBuilder(payload.Length + 3);
        foreach (char c in payload) sb.Append(c switch { '-' => '+', '_' => '/', _ => c });
        while (sb.Length % 4 != 0) sb.Append('=');

        try
        {
            bytes = Convert.FromBase64String(sb.ToString());
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
