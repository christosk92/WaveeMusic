using System.Linq;
using System.Xml.Linq;

namespace Wavee.Backend.Spotify;

// ── STEP 2 — ProductInfo (the AP pushes cmd 0x50 right after login) ──────────────────────────────────────────────────
// An XML blob describing the account's product (premium / free / open), catalogue, country, and CDN templates. This is
// the authoritative tier source → it drives the premium gate + SessionContext. The parse is pure and unit-tested; the
// live 0x50 read lives in SpotifyConnection. Generic over the field set (reads every <product> child element).
public sealed record ProductInfo(string Type, IReadOnlyDictionary<string, string> Attributes)
{
    /// <summary>Premium (incl. variants like premium_mini/_student/_family). Anything else (free/open/unknown) is not.</summary>
    public bool IsPremium => Type.StartsWith("premium", StringComparison.OrdinalIgnoreCase);

    public string? Catalogue => Attributes.TryGetValue("catalogue", out var v) ? v : null;
    public string? Country => Attributes.TryGetValue("country", out var v) ? v : null;

    public static ProductInfo Parse(string xml)
    {
        var attrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var product = XDocument.Parse(xml).Descendants("product").FirstOrDefault();
            if (product is not null)
                foreach (var el in product.Elements())
                    attrs[el.Name.LocalName] = el.Value;
        }
        catch (System.Xml.XmlException)
        {
            // Malformed payload → empty attributes / "unknown" type. Callers treat unknown conservatively.
        }
        return new ProductInfo(attrs.TryGetValue("type", out var t) ? t : "unknown", attrs);
    }
}
