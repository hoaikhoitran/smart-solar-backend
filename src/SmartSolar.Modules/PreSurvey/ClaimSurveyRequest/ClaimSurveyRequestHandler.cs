using SmartSolar.Modules.PreSurvey.Contracts.Persistence;

namespace SmartSolar.Modules.PreSurvey.ClaimSurveyRequest;

public sealed class ClaimSurveyRequestHandler
{
    private readonly IPreSurveyUnitOfWork _unitOfWork;

    public ClaimSurveyRequestHandler(
        IPreSurveyUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
    }

    public async Task<ClaimSurveyRequestResult> HandleAsync(
        ClaimSurveyRequestCommand command,
        CancellationToken cancellationToken)
    {
        var claimed =
            await _unitOfWork.TryClaimSurveyRequestAsync(
                command.SurveyRequestId,
                command.SaleUserId,
                DateTimeOffset.UtcNow,
                cancellationToken);

        return claimed
            ? ClaimSurveyRequestResult.Claimed()
            : ClaimSurveyRequestResult.Unavailable();
    }
}