using SmartSolar.Modules.AdministrativeUnits;
using SmartSolar.Modules.PreSurvey.Contracts.Persistence;
using SmartSolar.Modules.PreSurvey.Entities;

namespace SmartSolar.Modules.PreSurvey.CreatePropertySite;

public sealed class CreatePropertySiteHandler
{
    private readonly IPreSurveyUnitOfWork _unitOfWork;
    private readonly IAdministrativeCatalogReader _catalog;
    private readonly TimeProvider _time;

    public CreatePropertySiteHandler(
        IPreSurveyUnitOfWork unitOfWork,
        IAdministrativeCatalogReader catalog,
        TimeProvider time)
    {
        _unitOfWork = unitOfWork;
        _catalog = catalog;
        _time = time;
    }

    public async Task<CreatePropertySiteResult> HandleAsync(
        CreatePropertySiteCommand command,
        CancellationToken cancellationToken)
    {
        var customer =
            await _unitOfWork.FindCustomerByUserIdAsync(
                command.UserId,
                cancellationToken);

        if (customer is null)
        {
            return CreatePropertySiteResult.CustomerNotFound();
        }

        var provinceCode = AdministrativeCodeFormats.Normalize(command.ProvinceCode);
        var communeCode = AdministrativeCodeFormats.Normalize(command.CommuneCode);

        // New values must be ACTIVE official codes that belong together (superseded codes are refused).
        var problem = await AdministrativeLocationRules.CheckCatalogAsync(
            _catalog,
            provinceCode,
            communeCode,
            AdministrativeCatalogClock.Today(_time),
            cancellationToken);

        if (problem is not null)
        {
            return CreatePropertySiteResult.InvalidLocation(problem);
        }

        var propertySite = new PropertySite
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,

            Name = command.Name.Trim(),
            Province = command.Province.Trim(),

            District = Normalize(command.District),
            Ward = Normalize(command.Ward),
            StreetLine = Normalize(command.StreetLine),

            ProvinceCode = provinceCode,
            CommuneCode = communeCode,

            Latitude = command.Latitude,
            Longitude = command.Longitude,

            InstallationSurfaceType =
                command.InstallationSurfaceType,

            SurfaceMaterial =
                Normalize(command.SurfaceMaterial),

            Note = Normalize(command.Note)
        };

        _unitOfWork.AddPropertySite(propertySite);

        await _unitOfWork.SaveChangesAsync(
            cancellationToken);

        return CreatePropertySiteResult.Created(
            propertySite.Id);
    }

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim();
}