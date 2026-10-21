using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.PreSurvey.Enums;
using SmartSolar.Modules.SolarSimulation.Contracts.Persistence;
using SmartSolar.Modules.SolarSimulation.Entities;

namespace SmartSolar.Infrastructure.Persistence;

/// <summary>
/// PostgreSQL side of simulations: only the compact selected_simulation summary is stored (full
/// results live in the distributed cache). Pre-survey consistency is enforced with conditional
/// UPDATEs that lock the pre-survey row; in PostgreSQL READ COMMITTED a waiting UPDATE re-checks its
/// WHERE clause against the committed row, so a concurrent submit or geometry edit makes it match nothing.
/// </summary>
public sealed class SolarSimulationUnitOfWork : ISolarSimulationUnitOfWork
{
    /// <summary>
    /// Appointment statuses whose assigned technician may read the pre-survey's simulations.
    /// Single predicate shared with the scheduling lifecycle (the Flow 1 redesign updates this set).
    /// </summary>
    internal static readonly string[] TechnicianReadableAppointmentStatuses =
    [
        SmartSolar.Modules.Scheduling.Constants.AppointmentStatuses.Accepted,
        SmartSolar.Modules.Scheduling.Constants.AppointmentStatuses.Assigned,
        SmartSolar.Modules.Scheduling.Constants.AppointmentStatuses.Completed,
    ];

    private readonly AppDbContext _db;

    public SolarSimulationUnitOfWork(AppDbContext db)
    {
        _db = db;
    }

    public async Task<SimulationSource?> GetSourceAsync(Guid preSurveyId, CancellationToken cancellationToken)
    {
        var source = await _db.PreSurveys
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
                x.Property.Longitude,
                null))
            .FirstOrDefaultAsync(cancellationToken);
        if (source is null)
        {
            return null;
        }

        // Separate simple query (portable to SQLite): technicians officially assigned to the
        // active appointment of this pre-survey's survey request.
        var technicians = await _db.TechnicianAppointments.AsNoTracking()
            .Where(a => a.AssignedTechnicianUserId != null
                        && TechnicianReadableAppointmentStatuses.Contains(a.Status)
                        && _db.SurveyRequests.Any(r => r.Id == a.SurveyRequestId && r.PreSurveyId == preSurveyId))
            .Select(a => a.AssignedTechnicianUserId!.Value)
            .ToListAsync(cancellationToken);

        return source with { AssignedTechnicianUserIds = technicians };
    }

    public Task<SelectedSimulation?> GetSelectionAsync(Guid preSurveyId, CancellationToken cancellationToken)
        => _db.SelectedSimulations
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.PreSurveyId == preSurveyId, cancellationToken);

    public Task<SelectSimulationOutcome> TrySelectAsync(SelectedSimulation selection, CancellationToken cancellationToken)
    {
        var strategy = _db.Database.CreateExecutionStrategy();

        return strategy.ExecuteAsync(
            cancellationToken,
            async token =>
            {
                _db.ChangeTracker.Clear();
                await using var transaction = await _db.Database.BeginTransactionAsync(token);

                // Row lock + check, and release the FK to the row being replaced: the pre-survey
                // must still be Draft at the selected result's geometry version.
                var locked = await _db.PreSurveys
                    .Where(x =>
                        x.Id == selection.PreSurveyId &&
                        x.Status == PreSurveyStatus.Draft &&
                        x.GeometryVersion == selection.PreSurveyGeometryVersion)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.SelectedSimulationId, (Guid?)null), token);

                if (locked == 0)
                {
                    return await ExplainAsync(selection.PreSurveyId, token);
                }

                await _db.SelectedSimulations
                    .Where(x => x.PreSurveyId == selection.PreSurveyId)
                    .ExecuteDeleteAsync(token);

                _db.SelectedSimulations.Add(selection);
                try
                {
                    await _db.SaveChangesAsync(token);
                }
                catch (DbUpdateException ex) when (DbExceptionClassifier.IsUniqueViolation(ex))
                {
                    _db.ChangeTracker.Clear();
                    return SelectSimulationOutcome.Conflict;
                }

                await _db.PreSurveys
                    .Where(x => x.Id == selection.PreSurveyId)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.SelectedSimulationId, (Guid?)selection.SimulationId), token);

                await transaction.CommitAsync(token);
                _db.ChangeTracker.Clear();
                return SelectSimulationOutcome.Selected;
            });
    }

    public Task<ClearSelectionOutcome> TryClearSelectionAsync(Guid preSurveyId, CancellationToken cancellationToken)
    {
        var strategy = _db.Database.CreateExecutionStrategy();

        return strategy.ExecuteAsync(
            cancellationToken,
            async token =>
            {
                await using var transaction = await _db.Database.BeginTransactionAsync(token);

                var locked = await _db.PreSurveys
                    .Where(x => x.Id == preSurveyId && x.Status == PreSurveyStatus.Draft)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.SelectedSimulationId, (Guid?)null), token);
                if (locked == 0)
                {
                    return ClearSelectionOutcome.NotDraft;
                }

                await _db.SelectedSimulations.Where(x => x.PreSurveyId == preSurveyId).ExecuteDeleteAsync(token);
                await transaction.CommitAsync(token);
                return ClearSelectionOutcome.Cleared;
            });
    }

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
