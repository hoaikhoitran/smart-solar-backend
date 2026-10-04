using SmartSolar.Modules.PreSurvey.Contracts.Persistence;

namespace SmartSolar.Modules.PreSurvey.GetMySurveyRequests;

public sealed class GetMySurveyRequestsHandler
{
    private readonly IPreSurveyUnitOfWork _unitOfWork;

    public GetMySurveyRequestsHandler(
        IPreSurveyUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
    }

    public Task<IReadOnlyList<MySurveyRequestItem>> HandleAsync(
        Guid saleUserId,
        CancellationToken cancellationToken)
    {
        return _unitOfWork.GetSurveyRequestsBySaleAsync(
            saleUserId,
            cancellationToken);
    }
}