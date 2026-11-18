using SmartSolar.Modules.PreSurvey.Contracts.Persistence;

namespace SmartSolar.Modules.PreSurvey.GetMySurveyRequests;

public sealed class GetMySurveyRequestsHandler
{
    private readonly IPreSurveyUnitOfWork _unitOfWork;
    private readonly TimeProvider _time;

    public GetMySurveyRequestsHandler(
        IPreSurveyUnitOfWork unitOfWork,
        TimeProvider? time = null)
    {
        _unitOfWork = unitOfWork;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The caller's owned requests with their active appointment (technician included: the caller owns them).</summary>
    public async Task<IReadOnlyList<MySurveyRequestItem>> HandleAsync(
        Guid saleUserId,
        CancellationToken cancellationToken)
    {
        var items = await _unitOfWork.GetSurveyRequestsBySaleAsync(saleUserId, cancellationToken);
        var now = _time.GetUtcNow();
        return items.Select(item => item with { Appointment = item.Appointment?.At(now) }).ToList();
    }

    /// <summary>One page of the caller's own requests (SQL paging; only rows the caller owns are counted).</summary>
    public async Task<SmartSolar.Modules.Common.Paging.PagedResult<MySurveyRequestItem>> HandleAsync(Guid saleUserId, SmartSolar.Modules.Common.Paging.PageRequest page,
        CancellationToken cancellationToken)
    {
        var (rows, total) = await _unitOfWork.PageSurveyRequestsBySaleAsync(saleUserId, page.Skip, page.PageSize, cancellationToken);
        var now = _time.GetUtcNow();
        return page.Result(rows.Select(item => item with { Appointment = item.Appointment?.At(now) }).ToList(), total);
    }
}