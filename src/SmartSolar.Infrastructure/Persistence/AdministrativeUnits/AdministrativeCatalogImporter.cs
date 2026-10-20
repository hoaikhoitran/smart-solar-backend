using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.AdministrativeUnits;
using SmartSolar.Modules.AdministrativeUnits.Import;

namespace SmartSolar.Infrastructure.Persistence.AdministrativeUnits;

/// <summary>Outcome of a catalog import run (dry run or apply).</summary>
public sealed record CatalogImportResult(
    CatalogValidationReport Report,
    CatalogDiff? Diff,
    IReadOnlyList<string> Refusals,
    bool Applied,
    bool AlreadyApplied)
{
    public bool Succeeded => Report.IsValid && Refusals.Count == 0;
}

/// <summary>
/// Controlled import of an official catalog dataset (driven by the <c>admin-catalog import</c> CLI; there is no HTTP endpoint and no
/// startup import). <see cref="RunAsync"/> validates the files, compares them with the database and, only with apply = true, writes
/// everything in ONE transaction: idempotent upserts of the province, communes and legacy map; communes of the province missing from
/// the dataset are closed (valid_to = the dataset's effective date) — nothing is ever deleted; the run is recorded in
/// administrative_catalog_import. A dataset key already APPLIED with other checksums is refused; re-applying the same files changes
/// nothing. A refused apply is recorded as REJECTED when the key has no record yet.
/// </summary>
public sealed class AdministrativeCatalogImporter(AppDbContext db, TimeProvider time)
{
    public async Task<CatalogImportResult> RunAsync(CatalogDatasetFiles files, bool apply, string? appliedBy, CancellationToken cancellationToken)
    {
        var report = CatalogDatasetValidator.Validate(files);
        if (!report.IsValid)
        {
            if (apply) await RecordRejectedAsync(report, report.Errors, appliedBy, cancellationToken);
            return new CatalogImportResult(report, null, [], false, false);
        }

        var manifest = report.Manifest!;
        var province = await db.AdministrativeProvinces.AsNoTracking().FirstOrDefaultAsync(p => p.Code == manifest.ProvinceCode, cancellationToken);
        var communes = await db.AdministrativeCommunes.AsNoTracking().Where(c => c.ProvinceCode == manifest.ProvinceCode).ToListAsync(cancellationToken);
        var communeCodes = communes.Select(c => c.Code).ToList();
        var legacy = await db.AdministrativeLegacyCommuneMaps.AsNoTracking().Where(l => communeCodes.Contains(l.CommuneCode)).ToListAsync(cancellationToken);
        var diff = CatalogDiffPlanner.Compute(report, province, communes, legacy);

        var refusals = diff.Refusals.ToList();
        var incoming = report.Communes.Select(c => c.Code).ToList();
        var elsewhere = await db.AdministrativeCommunes.AsNoTracking()
            .Where(c => incoming.Contains(c.Code) && c.ProvinceCode != manifest.ProvinceCode).Select(c => c.Code).ToListAsync(cancellationToken);
        if (elsewhere.Count > 0)
            refusals.Add($"Commune codes already stored under another province: {string.Join(", ", elsewhere.Take(20))}.");

        var record = await db.AdministrativeCatalogImports.AsNoTracking().FirstOrDefaultAsync(i => i.DatasetKey == manifest.DatasetKey, cancellationToken);
        var alreadyApplied = false;
        if (record is { Status: CatalogImportStatuses.Applied })
        {
            if (record.SourceSha256 != manifest.SourceSha256 || !SameFiles(record.FilesSha256, report.ComputedSha256))
                refusals.Add($"Dataset '{manifest.DatasetKey}' was already applied with different checksums. Publish the new files under a new dataset key.");
            else alreadyApplied = true;
        }

        if (refusals.Count > 0)
        {
            if (apply) await RecordRejectedAsync(report, refusals, appliedBy, cancellationToken);
            return new CatalogImportResult(report, diff, refusals, false, alreadyApplied);
        }
        if (!apply) return new CatalogImportResult(report, diff, [], false, alreadyApplied);

        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(cancellationToken, async token =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            await ApplyAsync(report, diff, appliedBy, token);
            await transaction.CommitAsync(token);
            db.ChangeTracker.Clear();
        });
        return new CatalogImportResult(report, diff, [], true, alreadyApplied);
    }

    private async Task ApplyAsync(CatalogValidationReport report, CatalogDiff diff, string? appliedBy, CancellationToken token)
    {
        var manifest = report.Manifest!;
        var effective = manifest.EffectiveFrom!.Value;
        var key = manifest.DatasetKey!;
        var now = time.GetUtcNow();

        var province = await db.AdministrativeProvinces.FirstOrDefaultAsync(p => p.Code == manifest.ProvinceCode, token);
        if (province is null)
        {
            db.AdministrativeProvinces.Add(new AdministrativeProvince
            {
                Code = manifest.ProvinceCode!, Name = manifest.ProvinceName!.Trim(), UnitType = manifest.ProvinceUnitType!, ValidFrom = effective,
                DatasetKey = key, CreatedAt = now,
            });
        }
        else if (diff.ProvinceChanges.Count > 0)
        {
            province.Name = manifest.ProvinceName!.Trim();
            province.UnitType = manifest.ProvinceUnitType!;
            province.ValidTo = null;
            province.DatasetKey = key;
        }
        await db.SaveChangesAsync(token);

        var stored = (await db.AdministrativeCommunes.Where(c => c.ProvinceCode == manifest.ProvinceCode).ToListAsync(token))
            .ToDictionary(c => c.Code, StringComparer.Ordinal);
        foreach (var row in report.Communes)
        {
            if (!stored.TryGetValue(row.Code, out var commune))
            {
                db.AdministrativeCommunes.Add(new AdministrativeCommune
                {
                    Code = row.Code, ProvinceCode = row.ProvinceCode, Name = row.Name, SearchName = VietnameseText.Normalize(row.Name), UnitType = row.UnitType,
                    ValidFrom = effective, DatasetKey = key,
                });
                continue;
            }
            if (commune.Name == row.Name && commune.UnitType == row.UnitType && commune.ValidTo is null) continue;
            commune.Name = row.Name;
            commune.SearchName = VietnameseText.Normalize(row.Name);
            commune.UnitType = row.UnitType;
            commune.ValidTo = null;
            commune.DatasetKey = key;
        }
        foreach (var code in diff.ClosedCommunes)
        {
            var commune = stored[code];
            commune.ValidTo = effective > commune.ValidFrom ? effective : commune.ValidFrom.AddDays(1);
            commune.DatasetKey = key;
        }
        await db.SaveChangesAsync(token);

        var codes = report.Communes.Select(c => c.Code).ToList();
        var legacy = (await db.AdministrativeLegacyCommuneMaps.Where(l => codes.Contains(l.CommuneCode)).ToListAsync(token))
            .ToDictionary(l => (l.CommuneCode, l.MatchKey));
        foreach (var row in report.Legacy)
        {
            if (!legacy.TryGetValue((row.CommuneCode, row.MatchKey), out var existing))
            {
                db.AdministrativeLegacyCommuneMaps.Add(new AdministrativeLegacyCommuneMap
                {
                    Id = Guid.NewGuid(), CommuneCode = row.CommuneCode, LegacyCommuneCode = row.LegacyCommuneCode, LegacyCommuneName = row.LegacyCommuneName,
                    LegacyCommuneSearchName = row.LegacyCommuneName is null ? null : VietnameseText.Normalize(row.LegacyCommuneName),
                    LegacyDistrictCode = row.LegacyDistrictCode, LegacyDistrictName = row.LegacyDistrictName,
                    LegacyDistrictSearchName = VietnameseText.Normalize(row.LegacyDistrictName), LegacyProvinceCode = row.LegacyProvinceCode,
                    LegacyProvinceName = row.LegacyProvinceName, MergeType = row.MergeType, SourceNote = row.SourceNote, MatchKey = row.MatchKey, DatasetKey = key,
                });
                continue;
            }
            if (!CatalogDiffPlanner.LegacyDiffers(existing, row)) continue;
            existing.LegacyCommuneName = row.LegacyCommuneName;
            existing.LegacyCommuneSearchName = row.LegacyCommuneName is null ? null : VietnameseText.Normalize(row.LegacyCommuneName);
            existing.LegacyDistrictName = row.LegacyDistrictName;
            existing.LegacyDistrictSearchName = VietnameseText.Normalize(row.LegacyDistrictName);
            existing.LegacyProvinceCode = row.LegacyProvinceCode;
            existing.LegacyProvinceName = row.LegacyProvinceName;
            existing.MergeType = row.MergeType;
            existing.SourceNote = row.SourceNote;
            existing.DatasetKey = key;
        }
        await db.SaveChangesAsync(token);

        var record = await db.AdministrativeCatalogImports.FirstOrDefaultAsync(i => i.DatasetKey == key, token);
        if (record is { Status: CatalogImportStatuses.Applied }) return;   // idempotent re-apply: the first application stays recorded
        if (record is null)
        {
            record = new AdministrativeCatalogImport { Id = Guid.NewGuid(), DatasetKey = key };
            db.AdministrativeCatalogImports.Add(record);
        }
        Fill(record, report, now);
        record.Status = CatalogImportStatuses.Applied;
        record.AppliedAt = now;
        record.AppliedBy = Trim(appliedBy, 200);
        record.Counts = JsonSerializer.Serialize(new
        {
            manifest = manifest.Counts,
            applied = new
            {
                provinceCreated = diff.ProvinceIsNew,
                newCommunes = diff.NewCommunes.Count,
                changedCommunes = diff.ChangedCommunes.Select(c => c.Code).Distinct().Count(),
                closedCommunes = diff.ClosedCommunes.Count,
                reopenedCommunes = diff.ReopenedCommunes.Count,
                newLegacyRows = diff.NewLegacyRows,
                changedLegacyRows = diff.ChangedLegacyRows,
            },
        });
        record.Notes = diff.ClosedCommunes.Count > 0 ? Trim($"Closed communes (valid_to = {effective:yyyy-MM-dd}): {string.Join(", ", diff.ClosedCommunes)}", 4000) : null;
        await db.SaveChangesAsync(token);
    }

    private async Task RecordRejectedAsync(CatalogValidationReport report, IReadOnlyList<string> reasons, string? appliedBy, CancellationToken cancellationToken)
    {
        var manifest = report.Manifest;
        if (manifest?.DatasetKey is not { Length: > 0 and <= 100 } key || manifest.SourceUrl is null || manifest.SourceSha256 is not { Length: 64 }
            || manifest.EffectiveFrom is null)
            return;   // not identifiable enough to record
        db.ChangeTracker.Clear();
        if (await db.AdministrativeCatalogImports.AnyAsync(i => i.DatasetKey == key, cancellationToken)) return;
        var record = new AdministrativeCatalogImport { Id = Guid.NewGuid(), DatasetKey = key, Status = CatalogImportStatuses.Rejected };
        Fill(record, report, time.GetUtcNow());
        record.AppliedBy = Trim(appliedBy, 200);
        record.Counts = JsonSerializer.Serialize(new { manifest = manifest.Counts });
        record.Notes = Trim(string.Join(" | ", reasons), 4000);
        db.AdministrativeCatalogImports.Add(record);
        await db.SaveChangesAsync(cancellationToken);
        db.ChangeTracker.Clear();
    }

    private static void Fill(AdministrativeCatalogImport record, CatalogValidationReport report, DateTimeOffset now)
    {
        var manifest = report.Manifest!;
        record.SourceUrl = Trim(manifest.SourceUrl, 500)!;
        record.SourceSha256 = manifest.SourceSha256!;
        record.EffectiveFrom = manifest.EffectiveFrom!.Value;
        record.FilesSha256 = JsonSerializer.Serialize(report.ComputedSha256.OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p => p.Value));
        record.ValidatedAt = now;
    }

    private static bool SameFiles(string storedJson, IReadOnlyDictionary<string, string> computed)
    {
        try
        {
            var stored = JsonSerializer.Deserialize<Dictionary<string, string>>(storedJson) ?? [];
            return stored.Count == computed.Count && stored.All(p => computed.TryGetValue(p.Key, out var v) && string.Equals(v, p.Value, StringComparison.OrdinalIgnoreCase));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? Trim(string? value, int max) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().Length <= max ? value.Trim() : value.Trim()[..max];
}
