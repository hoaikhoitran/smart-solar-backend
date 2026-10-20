using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.PreSurvey.Contracts.Persistence;
using SmartSolar.Modules.PreSurvey.Entities;
using SmartSolar.Modules.PreSurvey.Enums;
using SmartSolar.Modules.PreSurvey.GetMySurveyRequests;
using SmartSolar.Modules.PreSurvey.GetPendingSurveyRequests;
using SmartSolar.Modules.PreSurvey.GetSurveyRequestDetail;

namespace SmartSolar.Infrastructure.Persistence;

public sealed class PreSurveyUnitOfWork : IPreSurveyUnitOfWork
{
    private readonly AppDbContext _db;

    public PreSurveyUnitOfWork(AppDbContext db)
    {
        _db = db;
    }
    
    public Task<Customer?> FindCustomerByUserIdAsync(
        Guid userId,
        CancellationToken cancellationToken)
    {
        return _db.Customers
            .FirstOrDefaultAsync(
                x => x.UserId == userId,
                cancellationToken);
    }

    public Task<PropertySite?> FindPropertyByIdAsync(
        Guid propertyId,
        CancellationToken cancellationToken)
    {
        return _db.PropertySites
            .FirstOrDefaultAsync(
                x => x.Id == propertyId,
                cancellationToken);
    }

    public void AddCustomer(Customer customer)
    {
        _db.Customers.Add(customer);
    }

    public void AddPropertySite(PropertySite propertySite)
    {
        _db.PropertySites.Add(propertySite);
    }

    public void AddPreSurvey(
        SmartSolar.Modules.PreSurvey.Entities.PreSurvey preSurvey)
    {
        _db.PreSurveys.Add(preSurvey);
    }

    public Task<int> SaveChangesAsync(
        CancellationToken cancellationToken)
    {
        return _db.SaveChangesAsync(cancellationToken);
    }
    public Task<SmartSolar.Modules.PreSurvey.Entities.PreSurvey?>
    FindPreSurveyByIdAsync(
        Guid preSurveyId,
        CancellationToken cancellationToken)
    {
        return _db.PreSurveys
            .FirstOrDefaultAsync(
                x => x.Id == preSurveyId,
                cancellationToken);
    }
    public async Task<bool> TrySaveDraftChangesAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            // Revision is a concurrency token: EF adds "AND revision = @original" to the UPDATE.
            await _db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            _db.ChangeTracker.Clear();
            return false;
        }
    }

    public async Task<PreSurveyStatus?> GetPreSurveyStatusAsync(
        Guid preSurveyId,
        CancellationToken cancellationToken)
    {
        var status = await _db.PreSurveys
            .AsNoTracking()
            .Where(x => x.Id == preSurveyId)
            .Select(x => (PreSurveyStatus?)x.Status)
            .FirstOrDefaultAsync(cancellationToken);

        return status;
    }

    public Task<bool> TrySubmitPreSurveyAsync(
        SurveyRequest surveyRequest,
        CancellationToken cancellationToken,
        int? expectedRevision = null)
    {
        // EnableRetryOnFailure forbids user transactions outside the execution strategy.
        var strategy = _db.Database.CreateExecutionStrategy();

        return strategy.ExecuteAsync(
            cancellationToken,
            async token =>
            {
                _db.ChangeTracker.Clear();

                await using var transaction =
                    await _db.Database.BeginTransactionAsync(token);

                // The conditional UPDATE row-locks the pre-survey; a concurrent submit
                // waits, then re-evaluates Status == Draft and matches nothing.
                var moved = await _db.PreSurveys
                    .Where(x =>
                        x.Id == surveyRequest.PreSurveyId &&
                        x.Status == PreSurveyStatus.Draft &&
                        (expectedRevision == null || x.Revision == expectedRevision))
                    .ExecuteUpdateAsync(
                        setters => setters
                            .SetProperty(x => x.Status, PreSurveyStatus.Submitted)
                            .SetProperty(x => x.UpdatedAt, surveyRequest.SubmittedAt)
                            .SetProperty(x => x.Revision, x => x.Revision + 1),
                        token);

                if (moved == 0)
                {
                    return false;
                }

                _db.SurveyRequests.Add(surveyRequest);
                await _db.SaveChangesAsync(token);
                await transaction.CommitAsync(token);

                return true;
            });
    }
    public async Task<IReadOnlyList<PendingSurveyRequestItem>>
    GetPendingSurveyRequestsAsync(
        CancellationToken cancellationToken)
    {
        return await _db.SurveyRequests
            .AsNoTracking()
            // "Unclaimed" queue: every non-cancelled request without a commercial owner, whatever its scheduling state.
            .Where(x =>
                x.Status != SurveyRequestStatus.Cancelled &&
                x.AssignedSaleId == null)
            .OrderBy(x => x.SubmittedAt)
            .Select(x => new PendingSurveyRequestItem(
                x.Id,
                x.PreSurveyId,
                x.PreSurvey.Property.Customer.User.FullName,
                x.PreSurvey.Property.Name,
                x.PreSurvey.Property.Province,
                x.PreSurvey.Property.District,
                x.PreSurvey.Property.InstallationSurfaceType,
                x.PreSurvey.TotalAreaM2,
                x.PreSurvey.UsableAreaM2,
                x.SubmittedAt))
            .ToListAsync(cancellationToken);
    }   
    public async Task<bool> TryClaimSurveyRequestAsync(
    Guid surveyRequestId,
    Guid saleUserId,
    DateTimeOffset assignedAt,
    CancellationToken cancellationToken)
    {
        // Commercial ownership only: the first claim wins and the scheduling status is left untouched.
        var affectedRows =
            await _db.SurveyRequests
                .Where(x =>
                    x.Id == surveyRequestId &&
                    (x.Status == SurveyRequestStatus.Pending ||
                     x.Status == SurveyRequestStatus.Scheduled ||
                     x.Status == SurveyRequestStatus.Completed) &&
                    x.AssignedSaleId == null)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(
                            x => x.AssignedSaleId,
                            saleUserId)
                        .SetProperty(
                            x => x.AssignedAt,
                            assignedAt),
                    cancellationToken);

        return affectedRows == 1;
    }
    public async Task<IReadOnlyList<MySurveyRequestItem>>
    GetSurveyRequestsBySaleAsync(
        Guid saleUserId,
        CancellationToken cancellationToken)
    {
        return await _db.SurveyRequests
            .AsNoTracking()
            .Where(x => x.AssignedSaleId == saleUserId)
            .OrderByDescending(x => x.AssignedAt)
            .Select(x => new MySurveyRequestItem(
                x.Id,
                x.PreSurveyId,
                x.PreSurvey.Property.Customer.User.FullName,
                x.PreSurvey.Property.Name,
                x.PreSurvey.Property.Province,
                x.PreSurvey.Property.District,
                x.Status,
                x.SubmittedAt,
                x.AssignedAt,
                x.ScheduledAt))
            .ToListAsync(cancellationToken);
    }
    public async Task<SurveyRequestDetail?>
    GetSurveyRequestDetailAsync(
        Guid surveyRequestId,
        CancellationToken cancellationToken)
    {
        var row = await _db.SurveyRequests
        .AsNoTracking()
        .Where(x => x.Id == surveyRequestId)
       .Select(x => new
       {
           Detail = new SurveyRequestDetail(
            x.Id,
            x.PreSurveyId,
            x.AssignedSaleId,

            x.PreSurvey.Property.Customer.User.FullName,
            x.PreSurvey.Property.Customer.User.Phone,
            x.PreSurvey.Property.Customer.User.Email,

            x.PreSurvey.Property.Name,
            x.PreSurvey.Property.Province,
            x.PreSurvey.Property.District,
            x.PreSurvey.Property.Ward,
            x.PreSurvey.Property.StreetLine,

            x.PreSurvey.Property.Latitude,
            x.PreSurvey.Property.Longitude,

            x.PreSurvey.Property.InstallationSurfaceType,
            x.PreSurvey.Property.SurfaceMaterial,

            x.PreSurvey.TotalAreaM2,
            x.PreSurvey.UsableAreaM2,
            x.PreSurvey.TiltDegree,
            x.PreSurvey.AzimuthDegree,
            x.PreSurvey.HasObstruction,

            x.Status,
            x.SubmittedAt,
            x.AssignedAt,
            x.ScheduledAt,

            x.SalesNote,

            null,
            null),
           x.PreSurvey.SelectedSimulationId,
           x.PreSurvey.GeometryVersion
       })
        .FirstOrDefaultAsync(cancellationToken);

        if (row is null)
        {
            return null;
        }

        var detail = row.Detail with { Appointment = await GetActiveAppointmentSummaryAsync(surveyRequestId, cancellationToken) };

        if (row.SelectedSimulationId is not { } selectedId)
        {
            return detail;
        }

        // Second, simple query (no correlated subquery) so it runs on PostgreSQL and SQLite alike.
        var summary = await _db.SelectedSimulations
            .AsNoTracking()
            .Where(s => s.SimulationId == selectedId && s.PreSurveyId == row.Detail.PreSurveyId)
            .Select(s => new SelectedSimulationSummary(
                s.SimulationId,
                s.Status,
                s.EnergyStatus,
                s.PreSurveyGeometryVersion != row.GeometryVersion,
                s.MountingType,
                s.ProductSku,
                s.ProductName,
                s.PanelCount,
                s.InstalledCapacityKwp,
                s.AnnualEnergyKwh,
                s.CreatedAt))
            .FirstOrDefaultAsync(cancellationToken);

        return detail with { SelectedSimulation = summary };
    }

    private async Task<SurveyAppointmentSummary?> GetActiveAppointmentSummaryAsync(Guid surveyRequestId, CancellationToken cancellationToken)
    {
        var appointment = await _db.TechnicianAppointments
            .AsNoTracking()
            .Where(a => a.SurveyRequestId == surveyRequestId && a.Status != SmartSolar.Modules.Scheduling.Constants.AppointmentStatuses.Cancelled)
            .FirstOrDefaultAsync(cancellationToken);
        if (appointment is null)
        {
            return null;
        }

        var technicianIds = new[] { appointment.ReservedTechnicianUserId ?? Guid.Empty, appointment.AssignedTechnicianUserId ?? Guid.Empty };
        var names = await _db.UserAccounts
            .AsNoTracking()
            .Where(u => technicianIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.FullName, cancellationToken);

        return new SurveyAppointmentSummary(
            appointment.Id,
            appointment.StartsAt,
            appointment.EndsAt,
            appointment.Status,
            appointment.ReservedTechnicianUserId,
            appointment.ReservedTechnicianUserId is { } holder ? names.GetValueOrDefault(holder) : null,
            appointment.AssignedTechnicianUserId,
            appointment.AssignedTechnicianUserId is { } assigned ? names.GetValueOrDefault(assigned) : null,
            appointment.AssignmentSource,
            appointment.AssignmentReason,
            appointment.CustomerNote,
            appointment.BookedAt,
            appointment.AcceptanceDeadlineAt,
            appointment.EscalatedAt,
            appointment.AcceptedAt,
            appointment.AssignedAt,
            appointment.CompletedAt,
            appointment.OperationalZoneId,
            appointment.Revision);
    }
}