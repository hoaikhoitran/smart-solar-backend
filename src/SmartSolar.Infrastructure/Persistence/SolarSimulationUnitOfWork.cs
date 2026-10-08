using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.PreSurvey.Enums;
using SmartSolar.Modules.SolarSimulation.Contracts.Persistence;
using SimulationEntity = SmartSolar.Modules.SolarSimulation.Entities.SolarSimulation;

namespace SmartSolar.Infrastructure.Persistence;

/// <summary>
/// Snapshots are insert-only: this unit of work has no update or delete operation for them.
/// Pre-survey consistency is enforced in the database with conditional UPDATEs that lock the
/// pre-survey row; in PostgreSQL READ COMMITTED a waiting UPDATE re-checks its WHERE clause
/// against the committed row, so a concurrent submit or geometry edit makes it match nothing.
/// </summary>
public sealed class SolarSimulationUnitOfWork : ISolarSimulationUnitOfWork
{
    private readonly AppDbContext _db;

    public SolarSimulationUnitOfWork(AppDbContext db)
    {
        _db = db;
    }

    public Task<SimulationSource?> GetSourceAsync(Guid preSurveyId, CancellationToken cancellationToken)
        => _db.PreSurveys
            .AsNoTracking()
            .Where(x => x.Id == preSurveyId)
            .Select(x => new SimulationSource(
                x.Id,
                x.Property.Customer.UserId,
                x.SurveyRequest != null ? x.SurveyRequest.AssignedSaleId : null,
                x.Status,
                x.GeometryVersion,
                x.SelectedSimulationId,
                x.SurfaceLengthM,
                x.SurfaceWidthM,
                x.TiltDegree,
                x.AzimuthDegree,
                x.Obstacles,
                x.TotalAreaM2,
                x.Property.Latitude,
                x.Property.Longitude))
            .FirstOrDefaultAsync(cancellationToken);

    public Task<SimulationEntity?> FindReusableAsync(Guid preSurveyId, string fingerprint, CancellationToken cancellationToken)
        => _db.SolarSimulations
            .AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.PreSurveyId == preSurveyId && x.InputFingerprint == fingerprint && x.IsReusable,
                cancellationToken);

    public Task<SaveSimulationOutcome> TrySaveAndSelectAsync(SimulationEntity simulation, CancellationToken cancellationToken)
    {
        var strategy = _db.Database.CreateExecutionStrategy();

        return strategy.ExecuteAsync(
            cancellationToken,
            async token =>
            {
                _db.ChangeTracker.Clear();
                await using var transaction = await _db.Database.BeginTransactionAsync(token);

                // Row lock + check. The no-op SET keeps revision untouched so a concurrent
                // submit that already read the pre-survey stays valid.
                var locked = await _db.PreSurveys
                    .Where(x =>
                        x.Id == simulation.PreSurveyId &&
                        x.Status == PreSurveyStatus.Draft &&
                        x.GeometryVersion == simulation.PreSurveyGeometryVersion)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.GeometryVersion, x => x.GeometryVersion), token);

                if (locked == 0)
                {
                    return await ExplainAsync(simulation.PreSurveyId, token) == SelectSimulationOutcome.NotDraft
                        ? SaveSimulationOutcome.NotDraft
                        : SaveSimulationOutcome.SourceChanged;
                }

                _db.SolarSimulations.Add(simulation);
                try
                {
                    await _db.SaveChangesAsync(token);
                }
                catch (DbUpdateException ex) when (DbExceptionClassifier.IsUniqueViolation(ex))
                {
                    _db.ChangeTracker.Clear();
                    return SaveSimulationOutcome.DuplicateFingerprint;
                }

                await _db.PreSurveys
                    .Where(x => x.Id == simulation.PreSurveyId)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.SelectedSimulationId, simulation.Id), token);

                await transaction.CommitAsync(token);
                _db.ChangeTracker.Clear();
                return SaveSimulationOutcome.Saved;
            });
    }

    public async Task<SelectSimulationOutcome> TrySelectAsync(
        Guid preSurveyId, Guid simulationId, int geometryVersion, CancellationToken cancellationToken)
    {
        var selected = await _db.PreSurveys
            .Where(x =>
                x.Id == preSurveyId &&
                x.Status == PreSurveyStatus.Draft &&
                x.GeometryVersion == geometryVersion &&
                _db.SolarSimulations.Any(s =>
                    s.Id == simulationId &&
                    s.PreSurveyId == preSurveyId &&
                    s.PreSurveyGeometryVersion == geometryVersion))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.SelectedSimulationId, simulationId), cancellationToken);

        return selected == 1 ? SelectSimulationOutcome.Selected : await ExplainAsync(preSurveyId, cancellationToken);
    }

    public async Task<IReadOnlyList<SimulationSummary>> ListAsync(Guid preSurveyId, CancellationToken cancellationToken)
        => await _db.SolarSimulations
            .AsNoTracking()
            .Where(x => x.PreSurveyId == preSurveyId)
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .Select(x => new SimulationSummary(
                x.Id,
                x.PreSurveyGeometryVersion,
                x.Status,
                x.EnergyStatus,
                x.ClimateStatus,
                x.MountingType,
                x.ProductId,
                x.ProductSku,
                x.ProductName,
                x.PanelCount,
                x.InstalledCapacityKwp,
                x.AnnualEnergyKwh,
                x.CreatedAt))
            .ToListAsync(cancellationToken);

    public Task<SimulationEntity?> FindAsync(Guid preSurveyId, Guid simulationId, CancellationToken cancellationToken)
        => _db.SolarSimulations
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.PreSurveyId == preSurveyId && x.Id == simulationId, cancellationToken);

    private async Task<SelectSimulationOutcome> ExplainAsync(Guid preSurveyId, CancellationToken cancellationToken)
    {
        var status = await _db.PreSurveys
            .AsNoTracking()
            .Where(x => x.Id == preSurveyId)
            .Select(x => (PreSurveyStatus?)x.Status)
            .FirstOrDefaultAsync(cancellationToken);

        return status == PreSurveyStatus.Draft ? SelectSimulationOutcome.SourceChanged : SelectSimulationOutcome.NotDraft;
    }
}
