namespace Wavee.Backend;

/// <summary>Mutable spclient base URL shared by mutation strategies and playlist edit HTTP (set on go-live).</summary>
public sealed class SpclientBaseUrlHolder
{
    public string Value { get; set; } = "";
}
