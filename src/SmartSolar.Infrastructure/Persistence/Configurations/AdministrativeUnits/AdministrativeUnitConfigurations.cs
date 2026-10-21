using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartSolar.Modules.AdministrativeUnits;

namespace SmartSolar.Infrastructure.Persistence.Configurations.AdministrativeUnits;

// Official administrative catalog (reference data, written only by the controlled CLI import `admin-catalog import`).
// Codes are text (leading zeros are significant); their digit formats are checked in the database with portable expressions
// (see AdministrativeCatalogSql) and again by the application.

public sealed class AdministrativeProvinceConfiguration : IEntityTypeConfiguration<AdministrativeProvince>
{
    public void Configure(EntityTypeBuilder<AdministrativeProvince> builder)
    {
        builder.ToTable("administrative_province", t =>
        {
            t.HasCheckConstraint("ck_administrative_province_code", AdministrativeCatalogSql.DigitsCheck("code", 2));
            t.HasCheckConstraint("ck_administrative_province_unit_type",
                $"unit_type IN ({AdministrativeCatalogSql.List(ProvinceUnitTypes.All)})");
            t.HasCheckConstraint("ck_administrative_province_validity", "valid_to IS NULL OR valid_to > valid_from");
        });
        builder.HasKey(x => x.Code);
        builder.Property(x => x.Code).HasColumnName("code").HasMaxLength(2).ValueGeneratedNever();
        builder.Property(x => x.Name).HasColumnName("name").HasMaxLength(150).IsRequired();
        builder.Property(x => x.UnitType).HasColumnName("unit_type").HasMaxLength(20).IsRequired();
        builder.Property(x => x.ValidFrom).HasColumnName("valid_from").IsRequired();
        builder.Property(x => x.ValidTo).HasColumnName("valid_to").IsRequired(false);
        builder.Property(x => x.DatasetKey).HasColumnName("dataset_key").HasMaxLength(100).IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
    }
}

public sealed class AdministrativeCommuneConfiguration : IEntityTypeConfiguration<AdministrativeCommune>
{
    public void Configure(EntityTypeBuilder<AdministrativeCommune> builder)
    {
        builder.ToTable("administrative_commune", t =>
        {
            t.HasCheckConstraint("ck_administrative_commune_code", AdministrativeCatalogSql.DigitsCheck("code", 5));
            t.HasCheckConstraint("ck_administrative_commune_unit_type", $"unit_type IN ({AdministrativeCatalogSql.List(CommuneUnitTypes.All)})");
            t.HasCheckConstraint("ck_administrative_commune_validity", "valid_to IS NULL OR valid_to > valid_from");
        });
        builder.HasKey(x => x.Code);
        builder.Property(x => x.Code).HasColumnName("code").HasMaxLength(5).ValueGeneratedNever();
        builder.Property(x => x.ProvinceCode).HasColumnName("province_code").HasMaxLength(2).IsRequired();
        builder.Property(x => x.Name).HasColumnName("name").HasMaxLength(150).IsRequired();
        builder.Property(x => x.SearchName).HasColumnName("search_name").HasMaxLength(150).IsRequired();
        builder.Property(x => x.UnitType).HasColumnName("unit_type").HasMaxLength(20).IsRequired();
        builder.Property(x => x.ValidFrom).HasColumnName("valid_from").IsRequired();
        builder.Property(x => x.ValidTo).HasColumnName("valid_to").IsRequired(false);
        builder.Property(x => x.DatasetKey).HasColumnName("dataset_key").HasMaxLength(100).IsRequired();
        // FK target of property_site (province_code, commune_code): a site can only reference a commune of its own province.
        builder.HasAlternateKey(x => new { x.ProvinceCode, x.Code }).HasName("ux_administrative_commune_province_code");
        builder.HasIndex(x => new { x.ProvinceCode, x.Name }).HasDatabaseName("ix_administrative_commune_province_name");
        builder.HasIndex(x => new { x.ProvinceCode, x.SearchName }).HasDatabaseName("ix_administrative_commune_province_search_name");
        builder.HasOne<AdministrativeProvince>().WithMany().HasForeignKey(x => x.ProvinceCode).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_administrative_commune_province");
    }
}

public sealed class AdministrativeLegacyCommuneMapConfiguration : IEntityTypeConfiguration<AdministrativeLegacyCommuneMap>
{
    public void Configure(EntityTypeBuilder<AdministrativeLegacyCommuneMap> builder)
    {
        builder.ToTable("administrative_legacy_commune_map", t =>
        {
            t.HasCheckConstraint("ck_administrative_legacy_commune_map_merge_type", $"merge_type IN ({AdministrativeCatalogSql.List(LegacyMergeTypes.All)})");
            t.HasCheckConstraint("ck_administrative_legacy_commune_map_legacy_commune_code",
                $"legacy_commune_code IS NULL OR ({AdministrativeCatalogSql.DigitsCheck("legacy_commune_code", 5)})");
            t.HasCheckConstraint("ck_administrative_legacy_commune_map_legacy_district_code", AdministrativeCatalogSql.DigitsCheck("legacy_district_code", 3));
            t.HasCheckConstraint("ck_administrative_legacy_commune_map_legacy_province_code", AdministrativeCatalogSql.DigitsCheck("legacy_province_code", 2));
            t.HasCheckConstraint("ck_administrative_legacy_commune_map_commune_pair",
                "(legacy_commune_code IS NULL) = (legacy_commune_name IS NULL) AND (legacy_commune_name IS NULL) = (legacy_commune_search_name IS NULL)");
            // match_key = legacy_district_code:legacy_commune_code (or '-'): the portable unique key that treats NULL as a value.
            t.HasCheckConstraint("ck_administrative_legacy_commune_map_match_key",
                "match_key = legacy_district_code || ':' || COALESCE(legacy_commune_code, '-')");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.CommuneCode).HasColumnName("commune_code").HasMaxLength(5).IsRequired();
        builder.Property(x => x.LegacyCommuneCode).HasColumnName("legacy_commune_code").HasMaxLength(5).IsRequired(false);
        builder.Property(x => x.LegacyCommuneName).HasColumnName("legacy_commune_name").HasMaxLength(150).IsRequired(false);
        builder.Property(x => x.LegacyCommuneSearchName).HasColumnName("legacy_commune_search_name").HasMaxLength(150).IsRequired(false);
        builder.Property(x => x.LegacyDistrictCode).HasColumnName("legacy_district_code").HasMaxLength(3).IsRequired();
        builder.Property(x => x.LegacyDistrictName).HasColumnName("legacy_district_name").HasMaxLength(150).IsRequired();
        builder.Property(x => x.LegacyDistrictSearchName).HasColumnName("legacy_district_search_name").HasMaxLength(150).IsRequired();
        builder.Property(x => x.LegacyProvinceCode).HasColumnName("legacy_province_code").HasMaxLength(2).IsRequired();
        builder.Property(x => x.LegacyProvinceName).HasColumnName("legacy_province_name").HasMaxLength(150).IsRequired();
        builder.Property(x => x.MergeType).HasColumnName("merge_type").HasMaxLength(20).IsRequired();
        builder.Property(x => x.SourceNote).HasColumnName("source_note").HasMaxLength(500).IsRequired(false);
        builder.Property(x => x.MatchKey).HasColumnName("match_key").HasMaxLength(12).IsRequired();
        builder.Property(x => x.DatasetKey).HasColumnName("dataset_key").HasMaxLength(100).IsRequired();
        builder.HasIndex(x => new { x.CommuneCode, x.MatchKey }).IsUnique().HasDatabaseName("ux_administrative_legacy_commune_map_commune_match_key");
        builder.HasIndex(x => x.LegacyCommuneSearchName).HasDatabaseName("ix_administrative_legacy_commune_map_commune_search_name");
        builder.HasIndex(x => x.LegacyDistrictSearchName).HasDatabaseName("ix_administrative_legacy_commune_map_district_search_name");
        builder.HasIndex(x => x.LegacyCommuneCode).HasDatabaseName("ix_administrative_legacy_commune_map_legacy_commune_code");
        builder.HasIndex(x => x.LegacyDistrictCode).HasDatabaseName("ix_administrative_legacy_commune_map_legacy_district_code");
        builder.HasOne<AdministrativeCommune>().WithMany().HasForeignKey(x => x.CommuneCode).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_administrative_legacy_commune_map_commune");
    }
}

public sealed class AdministrativeCatalogImportConfiguration : IEntityTypeConfiguration<AdministrativeCatalogImport>
{
    public void Configure(EntityTypeBuilder<AdministrativeCatalogImport> builder)
    {
        builder.ToTable("administrative_catalog_import", t =>
        {
            t.HasCheckConstraint("ck_administrative_catalog_import_status", $"status IN ({AdministrativeCatalogSql.List(CatalogImportStatuses.All)})");
            t.HasCheckConstraint("ck_administrative_catalog_import_applied",
                $"(status = '{CatalogImportStatuses.Applied}') = (applied_at IS NOT NULL)");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.DatasetKey).HasColumnName("dataset_key").HasMaxLength(100).IsRequired();
        builder.Property(x => x.SourceUrl).HasColumnName("source_url").HasMaxLength(500).IsRequired();
        builder.Property(x => x.SourceSha256).HasColumnName("source_sha256").HasMaxLength(64).IsRequired();
        builder.Property(x => x.EffectiveFrom).HasColumnName("effective_from").IsRequired();
        builder.Property(x => x.FilesSha256).HasColumnName("files_sha256").HasColumnType("jsonb").IsRequired();
        builder.Property(x => x.Counts).HasColumnName("counts").HasColumnType("jsonb").IsRequired();
        builder.Property(x => x.Status).HasColumnName("status").HasMaxLength(20).IsRequired();
        builder.Property(x => x.ValidatedAt).HasColumnName("validated_at").IsRequired();
        builder.Property(x => x.AppliedAt).HasColumnName("applied_at").IsRequired(false);
        builder.Property(x => x.AppliedBy).HasColumnName("applied_by").HasMaxLength(200).IsRequired(false);
        builder.Property(x => x.Notes).HasColumnName("notes").HasMaxLength(4000).IsRequired(false);
        builder.HasIndex(x => x.DatasetKey).IsUnique().HasDatabaseName("ux_administrative_catalog_import_dataset_key");
    }
}

/// <summary>Portable SQL fragments for the catalog check constraints (PostgreSQL and the SQLite test database).</summary>
internal static class AdministrativeCatalogSql
{
    /// <summary>
    /// Exactly <paramref name="length"/> ASCII digits: the length, and no character outside 0–9 (each position compared with
    /// BETWEEN '0' AND '9' through substr, which both providers support). Leading zeros are kept because the column is text.
    /// </summary>
    public static string DigitsCheck(string column, int length)
        => $"length({column}) = {length} AND " + string.Join(" AND ", Enumerable.Range(1, length).Select(i => $"substr({column}, {i}, 1) BETWEEN '0' AND '9'"));

    public static string List(IEnumerable<string> values) => string.Join(", ", values.Select(v => $"'{v}'"));
}
