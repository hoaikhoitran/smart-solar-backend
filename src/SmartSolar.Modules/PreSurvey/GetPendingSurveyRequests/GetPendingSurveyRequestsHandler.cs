using SmartSolar.Modules.PreSurvey.Contracts.Persistence;

namespace SmartSolar.Modules.PreSurvey.GetPendingSurveyRequests;

public sealed class GetPendingSurveyRequestsHandler
{
    private readonly IPreSurveyUnitOfWork _unitOfWork;

    public GetPendingSurveyRequestsHandler(
        IPreSurveyUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
    }

    public Task<IReadOnlyList<PendingSurveyRequestItem>>
        HandleAsync(
            CancellationToken cancellationToken)
    {
        return _unitOfWork.GetPendingSurveyRequestsAsync(
            cancellationToken);
    }
}