using SmartSolar.Modules.PreSurvey.Contracts.Persistence;
using SmartSolar.Modules.PreSurvey.Enums;

namespace SmartSolar.Modules.PreSurvey.UpdatePreSurvey;

public sealed class UpdatePreSurveyHandler
{
    private readonly IPreSurveyUnitOfWork _unitOfWork;

    public UpdatePreSurveyHandler(
        IPreSurveyUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
    }

    public async Task<UpdatePreSurveyResult> HandleAsync(
        UpdatePreSurveyCommand command,
        CancellationToken cancellationToken)
    {
        var customer =
            await _unitOfWork.FindCustomerByUserIdAsync(
                command.UserId,
                cancellationToken);

        if (customer is null)
        {
            return UpdatePreSurveyResult.CustomerNotFound();
        }

        var preSurvey =
            await _unitOfWork.FindPreSurveyByIdAsync(
                command.PreSurveyId,
                cancellationToken);

        if (preSurvey is null)
        {
            return UpdatePreSurveyResult.PreSurveyNotFound();
        }

        var property =
            await _unitOfWork.FindPropertyByIdAsync(
                preSurvey.PropertyId,
                cancellationToken);

        if (property is null ||
            property.CustomerId != customer.Id)
        {
            return UpdatePreSurveyResult.NotOwned();
        }

        if (preSurvey.Status != PreSurveyStatus.Draft)
        {
            return UpdatePreSurveyResult.NotEditable();
        }

        // Tilt and azimuth are the surface orientation: changing them makes existing
        // simulations stale. The revision check below guarantees these values are current.
        var geometryChanged =
            preSurvey.TiltDegree != command.TiltDegree ||
            preSurvey.AzimuthDegree != command.AzimuthDegree;

        preSurvey.TotalAreaM2 = command.TotalAreaM2;
        preSurvey.UsableAreaM2 = command.UsableAreaM2;
        preSurvey.TiltDegree = command.TiltDegree;
        preSurvey.AzimuthDegree = command.AzimuthDegree;
        preSurvey.HasObstruction = command.HasObstruction;

        preSurvey.UpdatedAt = DateTimeOffset.UtcNow;
        preSurvey.Revision++;

        if (geometryChanged)
        {
            preSurvey.GeometryVersion++;
        }

        // Conditional on the revision that was read: a concurrent submit or edit makes this
        // match no row, so a stale read can never overwrite a submitted pre-survey.
        if (!await _unitOfWork.TrySaveDraftChangesAsync(cancellationToken))
        {
            var status = await _unitOfWork.GetPreSurveyStatusAsync(command.PreSurveyId, cancellationToken);

            return status == PreSurveyStatus.Draft
                ? UpdatePreSurveyResult.ConcurrentlyModified()
                : UpdatePreSurveyResult.NotEditable();
        }

        return UpdatePreSurveyResult.Updated();
    }
}