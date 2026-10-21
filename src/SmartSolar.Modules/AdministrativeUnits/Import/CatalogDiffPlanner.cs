namespace SmartSolar.Modules.AdministrativeUnits.Import;

public sealed record CommuneChange(string Code, string Field, string? Before, string? After);

/// <summary>
/// What applying a valid dataset would change for its province. Nothing is ever deleted: a commune of the province that is
/// active in the database but absent from the dataset is CLOSED (valid_to = the dataset's effective date); a closed commune that
/// reappears is reopened. <see cref="Refusals"/> non-empty = the dataset must not be applied.
/// </summary>
public sealed record CatalogDiff(
    bool ProvinceIsNew,
    IReadOnlyList<CommuneChange> ProvinceChanges,
    IReadOnlyList<string> NewCommunes,
    IReadOnlyList<CommuneChange> ChangedCommunes,
    IReadOnlyList<string> ClosedCommunes,
    IReadOnlyList<string> ReopenedCommunes,
    int NewLegacyRows,
    int ChangedLegacyRows,
    int UnchangedLegacyRows,
    int RetainedLegacyRows,
    IReadOnlyList<string> Refusals)
{
    public bool HasChanges => ProvinceIsNew || ProvinceChanges.Count > 0 || NewCommunes.Count > 0 || ChangedCommunes.Count > 0
                              || ClosedCommunes.Count > 0 || ReopenedCommunes.Count > 0 || NewLegacyRows > 0 || ChangedLegacyRows > 0;
}

public static class CatalogDiffPlanner
{
    /// <param name="province">The stored province of the dataset, or null.</param>
    /// <param name="communes">Every stored commune of that province (any validity).</param>
    /// <param name="legacy">Every stored legacy row whose current commune belongs to that province.</param>
    public static CatalogDiff Compute(CatalogValidationReport dataset, AdministrativeProvince? province, IReadOnlyList<AdministrativeCommune> communes,
        IReadOnlyList<AdministrativeLegacyCommuneMap> legacy)
    {
        if (!dataset.IsValid || dataset.Manifest is not { EffectiveFrom: { } effective } manifest)
            throw new InvalidOperationException("Only a valid dataset can be compared with the database.");

        var refusals = new List<string>();
        var latest = communes.Count == 0 ? (DateOnly?)null : communes.Max(c => c.ValidTo is { } to && to > c.ValidFrom ? to : c.ValidFrom);
        if (latest is { } newest && effective < newest)
            refusals.Add($"The dataset is effective from {effective:yyyy-MM-dd}, older than catalog data already stored for province {manifest.ProvinceCode} ({newest:yyyy-MM-dd}).");

        var provinceChanges = new List<CommuneChange>();
        if (province is not null)
        {
            if (province.Name != manifest.ProvinceName) provinceChanges.Add(new(province.Code, "name", province.Name, manifest.ProvinceName));
            if (province.UnitType != manifest.ProvinceUnitType) provinceChanges.Add(new(province.Code, "unitType", province.UnitType, manifest.ProvinceUnitType));
            if (province.ValidTo is not null) provinceChanges.Add(new(province.Code, "validTo", province.ValidTo?.ToString("yyyy-MM-dd"), null));
        }

        var stored = communes.ToDictionary(c => c.Code, StringComparer.Ordinal);
        var incoming = dataset.Communes.ToDictionary(c => c.Code, StringComparer.Ordinal);
        var added = new List<string>();
        var changed = new List<CommuneChange>();
        var reopened = new List<string>();
        foreach (var row in dataset.Communes)
        {
            if (!stored.TryGetValue(row.Code, out var existing))
            {
                added.Add(row.Code);
                continue;
            }
            if (existing.Name != row.Name) changed.Add(new(row.Code, "name", existing.Name, row.Name));
            if (existing.UnitType != row.UnitType) changed.Add(new(row.Code, "unitType", existing.UnitType, row.UnitType));
            if (existing.ValidTo is not null) reopened.Add(row.Code);
        }
        var closed = communes.Where(c => c.ValidTo is null && !incoming.ContainsKey(c.Code)).Select(c => c.Code).OrderBy(c => c, StringComparer.Ordinal).ToList();

        var storedLegacy = legacy.ToDictionary(l => (l.CommuneCode, l.MatchKey));
        int newLegacy = 0, changedLegacy = 0, unchangedLegacy = 0;
        foreach (var row in dataset.Legacy)
        {
            if (!storedLegacy.TryGetValue((row.CommuneCode, row.MatchKey), out var existing)) newLegacy++;
            else if (LegacyDiffers(existing, row)) changedLegacy++;
            else unchangedLegacy++;
        }
        var datasetKeys = dataset.Legacy.Select(l => (l.CommuneCode, l.MatchKey)).ToHashSet();
        var retained = legacy.Count(l => !datasetKeys.Contains((l.CommuneCode, l.MatchKey)));

        return new CatalogDiff(province is null, provinceChanges, added, changed, closed, reopened, newLegacy, changedLegacy, unchangedLegacy, retained, refusals);
    }

    public static bool LegacyDiffers(AdministrativeLegacyCommuneMap existing, CatalogLegacyRow row)
        => existing.LegacyCommuneName != row.LegacyCommuneName || existing.LegacyDistrictName != row.LegacyDistrictName
           || existing.LegacyProvinceCode != row.LegacyProvinceCode || existing.LegacyProvinceName != row.LegacyProvinceName
           || existing.MergeType != row.MergeType || existing.SourceNote != row.SourceNote;
}
