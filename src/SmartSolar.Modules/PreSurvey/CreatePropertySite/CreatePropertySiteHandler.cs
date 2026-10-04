using SmartSolar.Modules.PreSurvey.Contracts.Persistence;
using SmartSolar.Modules.PreSurvey.Entities;

namespace SmartSolar.Modules.PreSurvey.CreatePropertySite;

public sealed class CreatePropertySiteHandler
{
    private readonly IPreSurveyUnitOfWork _unitOfWork;

    public CreatePropertySiteHandler(
        IPreSurveyUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
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

        var propertySite = new PropertySite
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,

            Name = command.Name.Trim(),
            Province = command.Province.Trim(),

            District = Normalize(command.District),
            Ward = Normalize(command.Ward),
            StreetLine = Normalize(command.StreetLine),

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