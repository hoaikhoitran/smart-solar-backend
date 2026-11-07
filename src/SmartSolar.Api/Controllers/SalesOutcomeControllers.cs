using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SmartSolar.Api.Contracts.InstallationWorkflow;
using SmartSolar.Api.Extensions;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Modules.Quotations;

namespace SmartSolar.Api.Controllers;

/// <summary>
/// Unsuccessful-sales outcomes (D-03). The customer withdraws their own request; the Sales owner closes one with a reason. Both are
/// terminal and refused while a contract or an open survey appointment exists.
/// </summary>
[ApiController]
[Route("api")]
public sealed class SalesOutcomeController(QuotationHandlers handlers) : OperationResultControllerBase
{
    [HttpPost("customers/me/survey-requests/{surveyRequestId:guid}/withdraw")]
    [Authorize(Roles = RoleCodes.Customer)]
    [ProducesResponseType(typeof(SmartSolar.Api.Contracts.ApiResponse<SalesOutcomeView>), StatusCodes.Status200OK)]
    [EnableRateLimiting(RateLimitingExtensions.QuotationWritePolicy)]
    public async Task<IActionResult> Withdraw(Guid surveyRequestId, [FromBody] ReasonRequest? request, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await handlers.WithdrawRequestAsync(user, surveyRequestId, request?.Reason, cancellationToken)) : UnauthorizedFailure();

    [HttpPost("survey-requests/{surveyRequestId:guid}/close")]
    [Authorize(Roles = RoleCodes.Sales)]
    [ProducesResponseType(typeof(SmartSolar.Api.Contracts.ApiResponse<SalesOutcomeView>), StatusCodes.Status200OK)]
    [EnableRateLimiting(RateLimitingExtensions.QuotationWritePolicy)]
    public async Task<IActionResult> Close(Guid surveyRequestId, [FromBody] CloseSurveyRequestRequest request, CancellationToken cancellationToken)
        => CurrentUserId() is { } user
            ? Respond(await handlers.CloseRequestAsync(user, surveyRequestId, request.ReasonCode, request.Note, cancellationToken))
            : UnauthorizedFailure();
}
