using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using SmartSolar.Infrastructure.Persistence;
using SmartSolar.Modules.Identity.Entities;
using SmartSolar.Modules.Identity.Enums;
using SmartSolar.Modules.PreSurvey.ClaimSurveyRequest;
using SmartSolar.Modules.PreSurvey.CreateCustomerProfile;
using SmartSolar.Modules.PreSurvey.CreatePreSurvey;
using SmartSolar.Modules.PreSurvey.CreatePropertySite;
using SmartSolar.Modules.PreSurvey.Entities;
using SmartSolar.Modules.PreSurvey.Enums;
using SmartSolar.Modules.PreSurvey.GetMySurveyRequests;
using SmartSolar.Modules.PreSurvey.GetPendingSurveyRequests;
using SmartSolar.Modules.PreSurvey.GetSurveyRequestDetail;
using SmartSolar.Modules.PreSurvey.SubmitPreSurvey;
using SmartSolar.Modules.PreSurvey.UpdatePreSurvey;

namespace SmartSolar.Tests.TestSupport;

public sealed class PreSurveyTestContext : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly string? _databaseFile;
    private readonly List<AppDbContext> _extraContexts = [];

    public PreSurveyTestContext() : this(databaseFile: null)
    {
    }

    private PreSurveyTestContext(string? databaseFile)
    {
        _databaseFile = databaseFile;
        _connection = new SqliteConnection(ConnectionString);
        _connection.Open();

        Db = new AppDbContext(OptionsFor(_connection));
        Db.Database.EnsureCreated();
        UnitOfWork = new PreSurveyUnitOfWork(Db);
    }

    /// <summary>
    /// A temp-file database, so several independent connections can contend for the
    /// same rows; a shared in-memory connection serialises everything in one handle.
    /// </summary>
    public static PreSurveyTestContext CreateFileBacked()
        => new(Path.Combine(Path.GetTempPath(), $"smartsolar-presurvey-{Guid.NewGuid():N}.db"));

    private string ConnectionString => _databaseFile is null
        ? "Filename=:memory:"
        : $"Data Source={_databaseFile};Pooling=False;Default Timeout=30";

    private static DbContextOptions<AppDbContext> OptionsFor(SqliteConnection connection)
        => new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .ReplaceService<IModelCustomizer, SqliteCatalogModelCustomizer>()
            .Options;

    /// <summary>A unit of work on its own connection; only meaningful for file-backed contexts.</summary>
    public PreSurveyUnitOfWork CreateUnitOfWorkOnNewConnection()
    {
        if (_databaseFile is null)
        {
            throw new InvalidOperationException("Use CreateFileBacked() for multi-connection tests.");
        }

        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(ConnectionString)
            .ReplaceService<IModelCustomizer, SqliteCatalogModelCustomizer>()
            .Options);
        _extraContexts.Add(db);
        return new PreSurveyUnitOfWork(db);
    }

    public AppDbContext Db { get;}
    public PreSurveyUnitOfWork UnitOfWork { get;}

    public CreateCustomerProfileHandler CreateCustomerProfileHandler() => new(UnitOfWork);
    public CreatePropertySiteHandler CreatePropertySiteHandler() => new(UnitOfWork);
    public CreatePreSurveyHandler CreatePreSurveyHandler() => new(UnitOfWork);
    public UpdatePreSurveyHandler UpdatePreSurveyHandler() => new(UnitOfWork);
    public SubmitPreSurveyHandler SubmitPreSurveyHandler() => new(UnitOfWork);
    public GetPendingSurveyRequestsHandler GetPendingSurveyRequestsHandler() => new(UnitOfWork);
    public ClaimSurveyRequestHandler ClaimSurveyRequestHandler() => new(UnitOfWork);
    public GetMySurveyRequestsHandler GetMySurveyRequestsHandler() => new(UnitOfWork);
    public GetSurveyRequestDetailHandler GetSurveyRequestDetailHandler() => new(UnitOfWork);

    public static UserAccount NewUser(
        string fullName = "Tran Hoai Khoi test",
        string? email = null,
        string? phone = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new UserAccount
        {
            Id = Guid.NewGuid(),
            Email = email ?? $"user-{Guid.NewGuid():N}@test.com",
            FullName = fullName,
            Phone = phone,
            Status = UserStatus.Active,
            EmailVerifiedAt = now,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    public static Customer NewCustomer(Guid userId) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        CustomerType = CustomerType.Business,
        CompanyName = "Existing Company"
    };

    public static PropertySite NewPropertySite(
        Guid customerId,
        string name = "Main Rooftop",
        string province = "Ho Chi Minh",
        string? district = "District 1",
        InstallationSurfaceType? installationSurfaceType = InstallationSurfaceType.Rooftop) => new()
    {
        Id = Guid.NewGuid(),
        CustomerId = customerId,
        Name = name,
        Province = province,
        District = district,
        Ward = "Ben Nghe",
        StreetLine = "1 Le Loi",
        Latitude = 10.776889m,
        Longitude = 106.700806m,
        InstallationSurfaceType = installationSurfaceType,
        SurfaceMaterial = "Concrete"
    };

    /// <summary>Every technical field is filled unless <paramref name="complete"/> is false.</summary>
    public static Modules.PreSurvey.Entities.PreSurvey NewPreSurvey(
        Guid propertyId,
        PreSurveyStatus status = PreSurveyStatus.Draft,
        bool complete = true)
    {
        var createdAt = DateTimeOffset.UtcNow.AddDays(-1);
        return new Modules.PreSurvey.Entities.PreSurvey
        {
            Id = Guid.NewGuid(),
            PropertyId = propertyId,
            TotalAreaM2 = complete ? 120m : null,
            UsableAreaM2 = complete ? 90m : null,
            TiltDegree = complete ? 15m : null,
            AzimuthDegree = complete ? 180m : null,
            HasObstruction = complete ? false : null,
            Status = status,
            CreatedAt = createdAt,
            UpdatedAt = createdAt
        };
    }

    public static SurveyRequest NewSurveyRequest(
        Guid preSurveyId,
        SurveyRequestStatus status = SurveyRequestStatus.Pending,
        Guid? assignedSaleId = null,
        DateTimeOffset? submittedAt = null,
        DateTimeOffset? assignedAt = null) => new()
    {
        Id = Guid.NewGuid(),
        PreSurveyId = preSurveyId,
        AssignedSaleId = assignedSaleId,
        Status = status,
        SubmittedAt = submittedAt ?? DateTimeOffset.UtcNow,
        AssignedAt = assignedAt
    };

    public Task<UserAccount> SeedUserAsync(
        string fullName = "Tran Hoai Khoi test",
        string? email = null,
        string? phone = null)
        => SeedAsync(NewUser(fullName, email, phone));

    public Task<UserAccount> SeedSalesUserAsync(string fullName = "Sales User")
        => SeedUserAsync(fullName);

    public Task<Customer> SeedCustomerAsync(Guid userId)
        => SeedAsync(NewCustomer(userId));

    public Task<PropertySite> SeedPropertySiteAsync(
        Guid customerId,
        string name = "Main Rooftop",
        string province = "Ho Chi Minh",
        string? district = "District 1",
        InstallationSurfaceType? installationSurfaceType = InstallationSurfaceType.Rooftop)
        => SeedAsync(NewPropertySite(customerId, name, province, district, installationSurfaceType));

    public Task<Modules.PreSurvey.Entities.PreSurvey> SeedPreSurveyAsync(
        Guid propertyId,
        PreSurveyStatus status = PreSurveyStatus.Draft,
        bool complete = true)
        => SeedAsync(NewPreSurvey(propertyId, status, complete));

    public Task<SurveyRequest> SeedSurveyRequestAsync(
        Guid preSurveyId,
        SurveyRequestStatus status = SurveyRequestStatus.Pending,
        Guid? assignedSaleId = null,
        DateTimeOffset? submittedAt = null,
        DateTimeOffset? assignedAt = null)
        => SeedAsync(NewSurveyRequest(preSurveyId, status, assignedSaleId, submittedAt, assignedAt));

    /// <summary>Seeds the full Customer → PropertySite → submitted PreSurvey chain that a survey request hangs off.</summary>
    public async Task<Modules.PreSurvey.Entities.PreSurvey> SeedSubmittedPreSurveyAsync(
        string customerName = "Customer",
        string propertyName = "Main Rooftop",
        string province = "Ho Chi Minh",
        string? district = "District 1")
    {
        var user = await SeedUserAsync(customerName);
        var customer = await SeedCustomerAsync(user.Id);
        var site = await SeedPropertySiteAsync(customer.Id, propertyName, province, district);
        return await SeedPreSurveyAsync(site.Id, PreSurveyStatus.Submitted);
    }

    private async Task<T> SeedAsync<T>(T entity) where T : class
    {
        Db.Add(entity);
        await Db.SaveChangesAsync();
        Db.ChangeTracker.Clear();
        return entity;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var extra in _extraContexts)
        {
            await extra.DisposeAsync();
        }

        await Db.DisposeAsync();
        await _connection.DisposeAsync();

        if (_databaseFile is not null)
        {
            File.Delete(_databaseFile);
        }
    }
}
