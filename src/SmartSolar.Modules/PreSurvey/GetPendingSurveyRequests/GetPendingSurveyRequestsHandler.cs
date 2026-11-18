using SmartSolar.Modules.PreSurvey.Contracts.Persistence;

namespace SmartSolar.Modules.PreSurvey.GetPendingSurveyRequests;

public sealed class GetPendingSurveyRequestsHandler
{
    private readonly IPreSurveyUnitOfWork _unitOfWork;
    private readonly TimeProvider _time;

    public GetPendingSurveyRequestsHandler(
        IPreSurveyUnitOfWork unitOfWork,
        TimeProvider? time = null)
    {
        _unitOfWork = unitOfWork;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The unclaimed queue with each request's active appointment (no technician details: no Sales owns these yet).</summary>
    public async Task<IReadOnlyList<PendingSurveyRequestItem>>
        HandleAsync(
            CancellationToken cancellationToken)
    {
        var items = await _unitOfWork.GetPendingSurveyRequestsAsync(cancellationToken);
        var now = _time.GetUtcNow();
        return items.Select(item => item with { Appointment = item.Appointment?.At(now) }).ToList();
    }

    /// <summary>One page of the unclaimed queue (SQL paging; oldest first).</summary>
    public async Task<SmartSolar.Modules.Common.Paging.PagedResult<PendingSurveyRequestItem>> HandleAsync(SmartSolar.Modules.Common.Paging.PageRequest page,
        CancellationToken cancellationToken)
    {
        var (rows, total) = await _unitOfWork.PagePendingSurveyRequestsAsync(page.Skip, page.PageSize, cancellationToken);
        var now = _time.GetUtcNow();
        return page.Result(rows.Select(item => item with { Appointment = item.Appointment?.At(now) }).ToList(), total);
    }
}