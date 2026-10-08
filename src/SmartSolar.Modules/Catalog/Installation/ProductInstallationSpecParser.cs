using System.Text.Json;

namespace SmartSolar.Modules.Catalog.Installation;

/// <summary>
/// Parses the optional "installation" block of Product.Spec. The rest of Spec stays
/// free-form. A spec without the block is reported as absent and is always valid,
/// so existing products are unaffected. A present block must follow the strict
/// format below; unknown keys (including a "verified" flag) are rejected because a
/// self-declared flag cannot prove manufacturer certification.
/// <code>
/// "installation": {
///   "schemaVersion": 1,
///   "requirements": { "panelGapMm": { "minMm": 10, "documentRef": "...", "section": "4.2", "url": "https://...", "notes": "..." } },
///   "moduleThicknessMm": { "valueMm": 35, "documentRef": "..." }
/// }
/// </code>
/// </summary>
public static class ProductInstallationSpecParser
{
    public const string PropertyName = "installation";
    public const int SupportedSchemaVersion = 1;
    public const decimal MaxRequirementMm = 5000m;
    public const decimal MaxModuleThicknessMm = 200m;

    private static readonly HashSet<string> BlockKeys = new(StringComparer.Ordinal)
    {
        "schemaVersion", "requirements", "moduleThicknessMm"
    };

    private static readonly HashSet<string> RequirementKeys = new(StringComparer.Ordinal)
    {
        "minMm", "documentRef", "section", "url", "notes"
    };

    private static readonly HashSet<string> DimensionKeys = new(StringComparer.Ordinal)
    {
        "valueMm", "documentRef", "section", "url", "notes"
    };

    public static InstallationSpecParseResult Parse(string? specJson)
    {
        if (string.IsNullOrWhiteSpace(specJson))
        {
            return Absent();
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(specJson);
        }
        catch (JsonException)
        {
            // Spec-is-an-object is validated elsewhere; without an object there is no block.
            return Absent();
        }

        using (document)
        {
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty(PropertyName, out var block))
            {
                return Absent();
            }

            var errors = new List<string>();

            if (block.ValueKind != JsonValueKind.Object)
            {
                errors.Add($"'{PropertyName}' must be a JSON object.");
                return new InstallationSpecParseResult(true, null, errors);
            }

            int? schemaVersion = null;
            var requirements = new Dictionary<string, DocumentedRequirement>(StringComparer.Ordinal);
            DocumentedDimension? thickness = null;
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var property in block.EnumerateObject())
            {
                var path = $"{PropertyName}.{property.Name}";

                if (!seen.Add(property.Name))
                {
                    errors.Add($"'{path}' appears more than once.");
                    continue;
                }

                switch (property.Name)
                {
                    case "schemaVersion":
                        if (property.Value.ValueKind == JsonValueKind.Number
                            && property.Value.TryGetInt32(out var version)
                            && version == SupportedSchemaVersion)
                        {
                            schemaVersion = version;
                        }
                        else
                        {
                            errors.Add($"'{path}' must be {SupportedSchemaVersion}.");
                        }
                        break;

                    case "requirements":
                        ParseRequirements(property.Value, path, requirements, errors);
                        break;

                    case "moduleThicknessMm":
                        thickness = ParseDimension(property.Value, path, errors);
                        break;

                    default:
                        errors.Add($"'{path}' is not a supported property.");
                        break;
                }
            }

            if (!seen.Contains("schemaVersion"))
            {
                errors.Add($"'{PropertyName}.schemaVersion' is required and must be {SupportedSchemaVersion}.");
            }

            if (errors.Count > 0 || schemaVersion is null)
            {
                return new InstallationSpecParseResult(true, null, errors);
            }

            return new InstallationSpecParseResult(
                true,
                new ProductInstallationSpec(schemaVersion.Value, requirements, thickness),
                errors);
        }
    }

    private static InstallationSpecParseResult Absent() => new(false, null, Array.Empty<string>());

    private static void ParseRequirements(
        JsonElement element,
        string path,
        Dictionary<string, DocumentedRequirement> requirements,
        List<string> errors)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"'{path}' must be a JSON object.");
            return;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var property in element.EnumerateObject())
        {
            var itemPath = $"{path}.{property.Name}";

            if (!seen.Add(property.Name))
            {
                errors.Add($"'{itemPath}' appears more than once.");
                continue;
            }

            if (!InstallationRequirementKeys.All.Contains(property.Name))
            {
                errors.Add($"'{itemPath}' is not a supported requirement.");
                continue;
            }

            if (property.Value.ValueKind != JsonValueKind.Object)
            {
                errors.Add($"'{itemPath}' must be a JSON object.");
                continue;
            }

            var before = errors.Count;
            CheckKeys(property.Value, itemPath, RequirementKeys, errors);
            var min = ReadMillimetres(property.Value, itemPath, "minMm", 0m, MaxRequirementMm, allowZero: true, errors);
            var source = ReadSource(property.Value, itemPath, errors);

            if (errors.Count == before && min is not null && source is not null)
            {
                requirements[property.Name] = new DocumentedRequirement(
                    min.Value, source.Value.DocumentRef, source.Value.Section, source.Value.Url, source.Value.Notes);
            }
        }
    }

    private static DocumentedDimension? ParseDimension(JsonElement element, string path, List<string> errors)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"'{path}' must be a JSON object.");
            return null;
        }

        var before = errors.Count;
        CheckKeys(element, path, DimensionKeys, errors);
        var value = ReadMillimetres(element, path, "valueMm", 0m, MaxModuleThicknessMm, allowZero: false, errors);
        var source = ReadSource(element, path, errors);

        return errors.Count == before && value is not null && source is not null
            ? new DocumentedDimension(value.Value, source.Value.DocumentRef, source.Value.Section, source.Value.Url, source.Value.Notes)
            : null;
    }

    private static void CheckKeys(JsonElement element, string path, HashSet<string> allowed, List<string> errors)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                errors.Add($"'{path}.{property.Name}' appears more than once.");
            }
            else if (!allowed.Contains(property.Name))
            {
                errors.Add($"'{path}.{property.Name}' is not a supported property.");
            }
        }
    }

    private static decimal? ReadMillimetres(
        JsonElement element, string path, string name, decimal min, decimal max, bool allowZero, List<string> errors)
    {
        var fullPath = $"{path}.{name}";

        if (!element.TryGetProperty(name, out var value))
        {
            errors.Add($"'{fullPath}' is required.");
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out var number))
        {
            errors.Add($"'{fullPath}' must be a number in millimetres.");
            return null;
        }

        var tooSmall = allowZero ? number < min : number <= min;
        if (tooSmall || number > max)
        {
            errors.Add(allowZero
                ? $"'{fullPath}' must be between {min} and {max} mm."
                : $"'{fullPath}' must be greater than {min} and at most {max} mm.");
            return null;
        }

        return number;
    }

    private static (string DocumentRef, string? Section, string? Url, string? Notes)? ReadSource(
        JsonElement element, string path, List<string> errors)
    {
        var ok = true;
        var documentRef = ReadString(element, path, "documentRef", 300, required: true, errors, ref ok);
        var section = ReadString(element, path, "section", 50, required: false, errors, ref ok);
        var url = ReadString(element, path, "url", 1000, required: false, errors, ref ok);
        var notes = ReadString(element, path, "notes", 500, required: false, errors, ref ok);

        if (url is not null
            && !(Uri.TryCreate(url, UriKind.Absolute, out var uri)
                 && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)))
        {
            errors.Add($"'{path}.url' must be an absolute http or https URL.");
            ok = false;
        }

        return ok && documentRef is not null ? (documentRef, section, url, notes) : null;
    }

    private static string? ReadString(
        JsonElement element, string path, string name, int maxLength, bool required, List<string> errors, ref bool ok)
    {
        var fullPath = $"{path}.{name}";

        if (!element.TryGetProperty(name, out var value))
        {
            if (required)
            {
                errors.Add($"'{fullPath}' is required.");
                ok = false;
            }
            return null;
        }

        if (value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString())
            || value.GetString()!.Length > maxLength)
        {
            errors.Add($"'{fullPath}' must be a non-empty string of at most {maxLength} characters.");
            ok = false;
            return null;
        }

        return value.GetString()!.Trim();
    }
}
