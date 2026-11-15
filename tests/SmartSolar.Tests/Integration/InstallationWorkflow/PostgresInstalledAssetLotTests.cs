using Microsoft.EntityFrameworkCore;
using Npgsql;
using SmartSolar.Modules.Installations;
using SmartSolar.Tests.TestSupport;
using SmartSolar.Tests.Unit.InstallationWorkflow;
using static SmartSolar.Tests.Unit.InstallationWorkflow.InstallationTests;

namespace SmartSolar.Tests.Integration.InstallationWorkflow;

/// <summary>
/// The quantity-lot schema on a real PostgreSQL (every migration applied): several lots of one manufacturer coexist (NULL serials are distinct in
/// the unique index), the check constraints refuse an invalid quantity, a serial with a quantity, and a lot without a catalog product, and a
/// duplicate serial is still refused.
/// </summary>
public sealed class PostgresInstalledAssetLotTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [PostgresFact]
    public async Task Lots_coexist_without_serials_and_the_constraints_refuse_invalid_rows()
    {
        var (database, ctx) = await PostgresRace.WorldAsync();
        await using var _ = database;
        await using var __ = ctx;
        var (_, project, tech, _) = await ScheduledAsync(ctx);
        await StartAsync(ctx, project.ProjectId, tech);
        var (panel, inverter) = await InstallationLotRegistrationTests.ContractedAsync(ctx, project.ContractId);

        // Two lots of the same manufacturer plus a serialised inverter: all stored, 13 units.
        var registered = await ctx.Installations().RegisterAssetsAsync(tech, project.ProjectId,
            [InstallationLotRegistrationTests.Lot(panel, "PANEL", 7), InstallationLotRegistrationTests.Lot(panel, "PANEL", 5),
             InstallationLotRegistrationTests.Serialised(inverter, "INVERTER", "PG-LOT-INV")], None);
        Assert.True(registered.Succeeded, registered.Message);
        Assert.Equal(EquipmentComparisonStatuses.Matches, registered.Value!.Equipment!.Status);
        Assert.Equal(13, registered.Value.Assets.Sum(a => a.Quantity));
        Assert.Equal(2, registered.Value.Assets.Count(a => a.SerialNumber is null));

        // The database itself refuses what the handler refuses (defence in depth), each attempt in its own context.
        async Task<string?> TryInsertAsync(Action<InstalledAsset> shape)
        {
            await using var db = database.NewContext();
            var asset = new InstalledAsset
            {
                Id = Guid.NewGuid(), ProjectId = project.ProjectId, AssetType = "PANEL", ProductId = panel.Id, Manufacturer = panel.Brand.ToUpperInvariant(),
                Model = panel.Model ?? "Model", Quantity = 1, RegisteredByUserId = tech, RegisteredAt = ctx.Clock.GetUtcNow(),
            };
            shape(asset);
            db.InstalledAssets.Add(asset);
            try
            {
                await db.SaveChangesAsync();
                return null;
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg)
            {
                return pg.SqlState;
            }
        }

        Assert.Equal("23514", await TryInsertAsync(a => a.Quantity = InstallationLimits.MaxLotQuantity + 1));  // check: quantity ≤ 10000
        // EF never sends a 0 (the CLR default falls back to the column default 1), so the lower bound is exercised with plain SQL.
        var lotId = registered.Value.Assets.First(a => a.SerialNumber is null).AssetId;
        await using (var raw = database.NewContext())
        {
            var zero = await Assert.ThrowsAsync<PostgresException>(() => raw.Database.ExecuteSqlInterpolatedAsync($"UPDATE installed_asset SET quantity = 0 WHERE id = {lotId}"));
            Assert.Equal("23514", zero.SqlState);                                                              // check: quantity ≥ 1
        }
        Assert.Equal("23514", await TryInsertAsync(a => { a.SerialNumber = "PG-SER-1"; a.Quantity = 2; }));   // check: a serialised row is one unit
        Assert.Equal("23514", await TryInsertAsync(a => { a.ProductId = null; a.Quantity = 3; }));            // check: a lot names its product
        Assert.Equal("23505", await TryInsertAsync(a => { a.AssetType = "INVERTER"; a.ProductId = inverter.Id; a.SerialNumber = "PG-LOT-INV"; a.Manufacturer = inverter.Brand.ToUpperInvariant(); }));   // unique serial
        Assert.Null(await TryInsertAsync(a => a.Quantity = 4));                                              // another lot of the same manufacturer
        Assert.Equal(4, await database.NewContext().InstalledAssets.AsNoTracking().CountAsync(a => a.ProjectId == project.ProjectId));
    }
}
