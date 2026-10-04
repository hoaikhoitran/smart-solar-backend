using SmartSolar.Modules.PreSurvey.Contracts.Persistence;
using SmartSolar.Modules.PreSurvey.Entities;
using SmartSolar.Modules.PreSurvey.Enums;

namespace SmartSolar.Modules.PreSurvey.CreatePreSurvey;

public sealed class CreatePreSurveyHandler
{
    private readonly IPreSurveyUnitOfWork _unitOfWork;

    public CreatePreSurveyHandler(
        IPreSurveyUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
    }

    public async Task<CreatePreSurveyResult> HandleAsync(
        CreatePreSurveyCommand command,
        CancellationToken cancellationToken)
    {
        var customer =
            await _unitOfWork.FindCustomerByUserIdAsync(
                command.UserId,
                cancellationToken);

        if (customer is null)
        {
            return CreatePreSurveyResult.CustomerNotFound();
        }

        var property =
            await _unitOfWork.FindPropertyByIdAsync(
                command.PropertySiteId,
                cancellationToken);

        if (property is null)
        {
            return CreatePreSurveyResult.PropertySiteNotFound();
        }

        if (property.CustomerId != customer.Id)
        {
            return CreatePreSurveyResult.PropertySiteNotOwned();
        }

        var now = DateTimeOffset.UtcNow;

        var preSurvey = new Entities.PreSurvey
        {
            Id = Guid.NewGuid(),
            PropertyId = property.Id,

            TotalAreaM2 = command.TotalAreaM2,
            UsableAreaM2 = command.UsableAreaM2,
            TiltDegree = command.TiltDegree,
            AzimuthDegree = command.AzimuthDegree,
            HasObstruction = command.HasObstruction,

            Status = PreSurveyStatus.Draft,

            CreatedAt = now,
            UpdatedAt = now
        };

        _unitOfWork.AddPreSurvey(preSurvey);

        await _unitOfWork.SaveChangesAsync(
            cancellationToken);

        return CreatePreSurveyResult.Created(
            preSurvey.Id);
    }
}