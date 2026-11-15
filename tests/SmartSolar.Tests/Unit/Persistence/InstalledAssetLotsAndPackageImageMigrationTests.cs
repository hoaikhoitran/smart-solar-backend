using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using SmartSolar.Infrastructure.Persistence;
using SmartSolar.Infrastructure.Persistence.Migrations;

namespace SmartSolar.Tests.Unit.Persistence;

/// <summary>
/// Guards the InstalledAssetLotsAndPackageImage migration without touching any database: additive only (a nullable serial number, a quantity
/// defaulting to 1 so every existing row stays a single serialised device, a nullable package cover URL, the matching check constraints and
/// the two new file purposes), and a Down that restores the previous schema.
/// </summary>
public sealed class InstalledAssetLotsAndPackageImageMigrationTests
{
    private static InstalledAssetLotsAndPackageImage Migration()
    {
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql("Host=localhost;Database=model_only").Options);
        return new InstalledAssetLotsAndPackageImage { ActiveProvider = db.Database.ProviderName! };
    }

    [Fact]
    public void Up_only_adds_columns_relaxes_the_serial_number_and_adds_check_constraints()
    {
        var up = Migration().UpOperations;

        Assert.Empty(up.OfType<DropTableOperation>());
        Assert.Empty(up.OfType<DropColumnOperation>());
        Assert.Empty(up.OfType<RenameTableOperation>());
        Assert.Empty(up.OfType<RenameColumnOperation>());
        Assert.Empty(up.OfType<DropForeignKeyOperation>());
        Assert.Empty(up.OfType<DropIndexOperation>());
        Assert.Empty(up.OfType<SqlOperation>());

        var added = up.OfType<AddColumnOperation>().OrderBy(c => c.Table).ThenBy(c => c.Name).ToList();
        Assert.Equal(new[] { ("installed_asset", "quantity", false), ("service_package", "image_url", true) }, added.Select(c => (c.Table, c.Name, c.IsNullable)));
        Assert.Equal(1, added.Single(c => c.Name == "quantity").DefaultValue);   // every existing row is one serialised unit

        var altered = Assert.Single(up.OfType<AlterColumnOperation>());
        Assert.Equal(("installed_asset", "serial_number", true, false), (altered.Table, altered.Name, altered.IsNullable, altered.OldColumn.IsNullable));

        var checks = up.OfType<AddCheckConstraintOperation>().ToDictionary(c => c.Name, c => (c.Table, c.Sql));
        Assert.Equal(("installed_asset", "quantity BETWEEN 1 AND 10000 AND (serial_number IS NULL OR quantity = 1)"), checks["ck_installed_asset_quantity"]);
        Assert.Equal(("installed_asset", "serial_number IS NOT NULL OR product_id IS NOT NULL"), checks["ck_installed_asset_lot_product"]);
        Assert.Equal(("service_package", "image_url IS NULL OR image_url LIKE 'https://%'"), checks["ck_service_package_image_url_https"]);
        Assert.Contains("'PRODUCT_IMAGE', 'PACKAGE_IMAGE'", checks["ck_file_reference_purpose"].Sql);
        // The purpose check is replaced, not duplicated.
        Assert.Equal("ck_file_reference_purpose", Assert.Single(up.OfType<DropCheckConstraintOperation>()).Name);
    }

    [Fact]
    public void Down_restores_the_previous_schema()
    {
        var down = Migration().DownOperations;

        Assert.Equal(new[] { ("installed_asset", "quantity"), ("service_package", "image_url") },
            down.OfType<DropColumnOperation>().Select(c => (c.Table, c.Name)).OrderBy(x => x.Table));
        var serial = Assert.Single(down.OfType<AlterColumnOperation>());
        Assert.Equal(("installed_asset", "serial_number", false), (serial.Table, serial.Name, serial.IsNullable));
        Assert.Equal(new[] { "ck_file_reference_purpose", "ck_installed_asset_lot_product", "ck_installed_asset_quantity", "ck_service_package_image_url_https" },
            down.OfType<DropCheckConstraintOperation>().Select(c => c.Name).Order());
        var restored = Assert.Single(down.OfType<AddCheckConstraintOperation>());
        Assert.Equal("ck_file_reference_purpose", restored.Name);
        Assert.DoesNotContain("PRODUCT_IMAGE", restored.Sql);
        Assert.Empty(down.OfType<DropTableOperation>());
    }
}
