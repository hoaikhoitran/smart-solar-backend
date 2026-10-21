using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.AdministrativeUnits;

namespace SmartSolar.Infrastructure.Persistence.AdministrativeUnits;

/// <summary>EF read side of the official administrative catalog (no tracking; the catalog is only written by the CLI import).</summary>
public sealed class AdministrativeCatalogStore(AppDbContext db) : IAdministrativeCatalogReader
{
    private IQueryable<AdministrativeCommune> ActiveCommunes(DateOnly asOf)
        => db.AdministrativeCommunes.AsNoTracking().Where(c => c.ValidFrom <= asOf && (c.ValidTo == null || c.ValidTo > asOf));

    public async Task<IReadOnlyList<AdministrativeProvince>> ListActiveProvincesAsync(DateOnly asOf, CancellationToken cancellationToken)
        => (await db.AdministrativeProvinces.AsNoTracking()
                .Where(p => p.ValidFrom <= asOf && (p.ValidTo == null || p.ValidTo > asOf))
                .ToListAsync(cancellationToken))
            .OrderBy(p => p.Code, StringComparer.Ordinal).ToList();

    public Task<AdministrativeProvince?> FindProvinceAsync(string code, CancellationToken cancellationToken)
        => db.AdministrativeProvinces.AsNoTracking().FirstOrDefaultAsync(p => p.Code == code, cancellationToken);

    public Task<AdministrativeCommune?> FindCommuneAsync(string code, CancellationToken cancellationToken)
        => db.AdministrativeCommunes.AsNoTracking().FirstOrDefaultAsync(c => c.Code == code, cancellationToken);

    public async Task<IReadOnlyList<AdministrativeCommune>> ListActiveCommunesAsync(string provinceCode, string? searchKey, string? unitType, DateOnly asOf,
        CancellationToken cancellationToken)
    {
        var query = ActiveCommunes(asOf).Where(c => c.ProvinceCode == provinceCode);
        if (searchKey is not null) query = query.Where(c => c.SearchName.Contains(searchKey));
        if (unitType is not null) query = query.Where(c => c.UnitType == unitType);
        return (await query.ToListAsync(cancellationToken))
            .OrderBy(c => c.SearchName, StringComparer.Ordinal).ThenBy(c => c.Code, StringComparer.Ordinal).ToList();
    }

    public async Task<IReadOnlyList<AdministrativeCommune>> GetActiveCommunesAsync(IReadOnlyCollection<string> codes, DateOnly asOf, CancellationToken cancellationToken)
    {
        if (codes.Count == 0) return [];
        var list = codes.Distinct().ToList();
        return await ActiveCommunes(asOf).Where(c => list.Contains(c.Code)).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<LegacyMapRow>> FindLegacyRowsAsync(string searchKey, string? code, string? provinceCode, DateOnly asOf, int limit,
        CancellationToken cancellationToken)
    {
        var rows = await db.AdministrativeLegacyCommuneMaps.AsNoTracking()
            .Where(l => (l.LegacyCommuneSearchName != null && l.LegacyCommuneSearchName.Contains(searchKey))
                        || l.LegacyDistrictSearchName.Contains(searchKey)
                        || (code != null && (l.LegacyCommuneCode == code || l.LegacyDistrictCode == code)))
            .Join(ActiveCommunes(asOf).Where(c => provinceCode == null || c.ProvinceCode == provinceCode),
                l => l.CommuneCode, c => c.Code, (l, c) => new { Legacy = l, Commune = c })
            .OrderBy(x => x.Legacy.LegacyDistrictCode).ThenBy(x => x.Legacy.LegacyCommuneCode).ThenBy(x => x.Commune.Code)
            .Take(limit)
            .ToListAsync(cancellationToken);
        return rows.Select(r => new LegacyMapRow(r.Legacy, r.Commune)).ToList();
    }
}
