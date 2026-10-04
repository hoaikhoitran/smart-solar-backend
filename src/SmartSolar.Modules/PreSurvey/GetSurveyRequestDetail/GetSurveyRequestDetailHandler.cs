using SmartSolar.Modules.PreSurvey.Contracts.Persistence;

namespace SmartSolar.Modules.PreSurvey.GetSurveyRequestDetail;

public sealed class GetSurveyRequestDetailHandler
{
    private readonly IPreSurveyUnitOfWork _unitOfWork;

    public GetSurveyRequestDetailHandler(
        IPreSurveyUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
    }

    public async Task<GetSurveyRequestDetailResult> HandleAsync(
        Guid surveyRequestId,
        Guid saleUserId,
        CancellationToken cancellationToken)
    {
        var detail =
            await _unitOfWork.GetSurveyRequestDetailAsync(
                surveyRequestId,
                cancellationToken);

        if (detail is null)
        {
            return GetSurveyRequestDetailResult.NotFound();
        }

        if (detail.AssignedSaleId != saleUserId)
        {
            return GetSurveyRequestDetailResult.NotAssignedToSale();
        }

        return GetSurveyRequestDetailResult.Found(detail);
    }
}