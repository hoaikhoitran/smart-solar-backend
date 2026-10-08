using SmartSolar.Modules.PreSurvey.Contracts.Persistence;
using SmartSolar.Modules.PreSurvey.Entities;
using SmartSolar.Modules.PreSurvey.Enums;

namespace SmartSolar.Modules.PreSurvey.SubmitPreSurvey;

public sealed class SubmitPreSurveyHandler
{
    private readonly IPreSurveyUnitOfWork _unitOfWork;

    public SubmitPreSurveyHandler(
        IPreSurveyUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
    }

    public async Task<SubmitPreSurveyResult> HandleAsync(
        SubmitPreSurveyCommand command,
        CancellationToken cancellationToken)
    {
        var customer =
            await _unitOfWork.FindCustomerByUserIdAsync(
                command.UserId,
                cancellationToken);

        if (customer is null)
        {
            return SubmitPreSurveyResult.CustomerNotFound();
        }

        var preSurvey =
            await _unitOfWork.FindPreSurveyByIdAsync(
                command.PreSurveyId,
                cancellationToken);

        if (preSurvey is null)
        {
            return SubmitPreSurveyResult.PreSurveyNotFound();
        }

        var property =
            await _unitOfWork.FindPropertyByIdAsync(
                preSurvey.PropertyId,
                cancellationToken);

        if (property is null ||
            property.CustomerId != customer.Id)
        {
            return SubmitPreSurveyResult.NotOwned();
        }

        if (preSurvey.Status != PreSurveyStatus.Draft)
        {
            return SubmitPreSurveyResult.AlreadySubmitted();
        }

        if (!IsComplete(preSurvey, property))
        {
            return SubmitPreSurveyResult.Incomplete();
        }

        var now = DateTimeOffset.UtcNow;

        var surveyRequest = new SurveyRequest
        {
            Id = Guid.NewGuid(),
            PreSurveyId = preSurvey.Id,
            AssignedSaleId = null,
            Status = SurveyRequestStatus.Pending,
            SubmittedAt = now,
            AssignedAt = null,
            ScheduledAt = null,
            SalesNote = null
        };

        // Requires the revision that passed the completeness check above, so a concurrent
        // update cannot slip incomplete data into a submitted pre-survey.
        var submitted =
            await _unitOfWork.TrySubmitPreSurveyAsync(
                surveyRequest,
                cancellationToken,
                preSurvey.Revision);

        if (submitted)
        {
            return SubmitPreSurveyResult.Submitted(surveyRequest.Id);
        }

        var status = await _unitOfWork.GetPreSurveyStatusAsync(preSurvey.Id, cancellationToken);

        return status == PreSurveyStatus.Draft
            ? SubmitPreSurveyResult.ConcurrentlyModified()
            : SubmitPreSurveyResult.AlreadySubmitted();
    }

    private static bool IsComplete(
        Entities.PreSurvey preSurvey,
        PropertySite property)
    {
        return
            preSurvey.TotalAreaM2.HasValue &&
            preSurvey.UsableAreaM2.HasValue &&
            preSurvey.TiltDegree.HasValue &&
            preSurvey.AzimuthDegree.HasValue &&
            property.InstallationSurfaceType.HasValue;
    }
}