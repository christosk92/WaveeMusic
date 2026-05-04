using System.Text.RegularExpressions;

namespace Wavee.UI.WinUI.Services;

public static partial class PiiRedactor
{
    public static string Redact(string input)
    {
        if (string.IsNullOrEmpty(input))
            return input;

        var result = EmailRegex().Replace(input, "[REDACTED_EMAIL]");
        result = BearerTokenRegex().Replace(result, "[REDACTED_TOKEN]");
        result = GenericTokenRegex().Replace(result, "[REDACTED_TOKEN]");
        result = WindowsPathUserRegex().Replace(result, @"C:\Users\[REDACTED]\");
        result = UnixPathHomeRegex().Replace(result, "/home/[REDACTED]/");

        return result;
    }

    [GeneratedRegex(@"[a-zA-Z0-9._%+\-]+@[a-zA-Z0-9.\-]+\.[a-zA-Z]{2,}")]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"Bearer\s+[A-Za-z0-9\-._~+/]+=*")]
    private static partial Regex BearerTokenRegex();

    [GeneratedRegex(@"(?:access_token|refresh_token|token)[""=:]\s*[""']?[A-Za-z0-9\-._~+/]{20,}[""']?", RegexOptions.IgnoreCase)]
    private static partial Regex GenericTokenRegex();

    [GeneratedRegex(@"C:\\Users\\[^\\]+\\")]
    private static partial Regex WindowsPathUserRegex();

    [GeneratedRegex(@"/home/[^/]+/")]
    private static partial Regex UnixPathHomeRegex();
}
