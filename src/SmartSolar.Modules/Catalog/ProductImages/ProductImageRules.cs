namespace SmartSolar.Modules.Catalog.ProductImages;

/// <summary>Image field rules shared by product create and the image endpoints.</summary>
public static class ProductImageRules
{
    public const int MaxUrlLength = 1000;
    public const int MaxAltTextLength = 255;

    /// <summary>
    /// Trims and lower-cases the scheme, so the stored value always starts with
    /// "https://" (the database check is a case-sensitive prefix match) and
    /// duplicate detection is not fooled by "HTTPS://". The rest is kept verbatim.
    /// </summary>
    public static string NormalizeUrl(string url)
    {
        var trimmed = url.Trim();
        return trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? "https://" + trimmed["https://".Length..]
            : trimmed;
    }

    public static string? NormalizeAltText(string? altText)
        => string.IsNullOrWhiteSpace(altText) ? null : altText.Trim();

    /// <summary>Returns null when <paramref name="url"/> is acceptable, otherwise the reason.</summary>
    public static string? UrlError(string? url, CatalogOptions options)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return "'Url' must not be empty.";
        }

        var trimmed = NormalizeUrl(url);

        if (trimmed.Length > MaxUrlLength)
        {
            return $"'Url' must be at most {MaxUrlLength} characters.";
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || string.IsNullOrEmpty(uri.Host))
        {
            return "'Url' must be an absolute https URL.";
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return "'Url' must not contain credentials.";
        }

        var allowed = options.ImageAllowedHosts?
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .Select(h => h.Trim())
            .ToArray() ?? [];

        if (allowed.Length > 0
            && !allowed.Any(h => string.Equals(h, uri.Host, StringComparison.OrdinalIgnoreCase)))
        {
            return "'Url' host is not an allowed image host.";
        }

        return null;
    }

    public static string? AltTextError(string? altText)
        => altText is not null && altText.Trim().Length > MaxAltTextLength
            ? $"'Alt Text' must be at most {MaxAltTextLength} characters."
            : null;
}
