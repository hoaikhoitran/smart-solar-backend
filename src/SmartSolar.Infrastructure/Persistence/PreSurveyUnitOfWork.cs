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
            .Where(x =>
                x.Status == SurveyRequestStatus.Pending &&
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
        var affectedRows =
            await _db.SurveyRequests
                .Where(x =>
                    x.Id == surveyRequestId &&
                    x.Status == SurveyRequestStatus.Pending &&
                    x.AssignedSaleId == null)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(
                            x => x.AssignedSaleId,
                            saleUserId)
                        .SetProperty(
                            x => x.AssignedAt,
                            assignedAt)
                        .SetProperty(
                            x => x.Status,
                            SurveyRequestStatus.Assigned),
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

            null),
           x.PreSurvey.SelectedSimulationId,
           x.PreSurvey.GeometryVersion
       })
        .FirstOrDefaultAsync(cancellationToken);

        if (row is null || row.SelectedSimulationId is not { } selectedId)
        {
            return row?.Detail;
        }

        // Second, simple query (no correlated subquery) so it runs on PostgreSQL and SQLite alike.
        var summary = await _db.SolarSimulations
            .AsNoTracking()
            .Where(s => s.Id == selectedId && s.PreSurveyId == row.Detail.PreSurveyId)
            .Select(s => new SelectedSimulationSummary(
                s.Id,
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

        return row.Detail with { SelectedSimulation = summary };
    }
}