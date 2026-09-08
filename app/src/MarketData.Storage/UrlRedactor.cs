namespace MarketData.Storage;

/// <summary>
/// Removes credentials from URLs before they are written to storage.
/// Raw storage is permanent and versioned, so a leaked key is unrecoverable.
/// </summary>
public static class UrlRedactor
{
    private const string Replacement = "REDACTED";

    private static readonly string[] SensitiveNames =
        ["apikey", "api_key", "token", "key", "secret", "password"];

    public static string Redact(string url)
    {
        var split = url.IndexOf('?');
        if (split < 0)
        {
            return url;
        }

        var prefix = url[..split];
        var pairs = url[(split + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries);

        var redacted = pairs.Select(pair =>
        {
            var eq = pair.IndexOf('=');
            if (eq < 0)
            {
                return pair;
            }

            var name = pair[..eq];
            return IsSensitive(name) ? $"{name}={Replacement}" : pair;
        });

        return $"{prefix}?{string.Join('&', redacted)}";
    }

    private static bool IsSensitive(string name) =>
        SensitiveNames.Contains(name, StringComparer.OrdinalIgnoreCase);
}
